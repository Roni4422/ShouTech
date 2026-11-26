using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ShouTech.App.Services;
using ShouTech.Application.Services;
using ShouTech.Domain;
using ShouTech.Domain.Entities;
using ShouTech.Infrastructure;
using Xunit;

namespace Kern.Tests;

/// <summary>
/// Phase B 057 — CRM pipeline (migration 057, CrmRepository/CrmService).
/// Pins, against real migrated SQLite databases:
///   * lead -> qualify -> convert creates the party in the UNIFIED
///     master (parties, kind C, lead data carried, stable ledger_id),
///     stamps the lead Converted with both links, creates the
///     opportunity at the first Open stage, and leaves NO second party
///     store behind (the legacy customers/suppliers tables stay dropped
///     since 061); a second conversion is CRM-9001;
///   * the stage walk records one history row per change and the
///     weighted pipeline value (amount x probability) is exact at every
///     stage; losing without a reason is CRM-9006;
///   * an activity due yesterday is in the overdue worklist and
///     completing it removes it; an activity must target exactly one of
///     lead/opportunity/party (CRM-9005);
///   * an opportunity quotation is a Quotation-kind Draft linked both
///     ways with NO party-ledger / GL / stock effects (Step 5 policy);
///   * every CRM field round-trips (fully populated lead + opportunity);
///   * B1 (G1/G2/G3): opportunity items round-trip and define the
///     expected amount (invalid items are CRM-9008); a quotation built
///     from items carries price/discount/tax with correct totals, a
///     sequence number and valid-till; duplicate leads are CRM-9007;
///     link-existing conversion creates no new party.
/// Execution requires Windows (net8.0-windows testhost); the same
/// scenarios are verified headlessly by
/// ~/workspace/research/phaseb-057/crm57_harness.py
/// (see docs/BUILD_PHASEB_054.md).
/// </summary>
public class CrmPipelineTests
{
    private const string Actor = "crm-test";
    private const string CO = "CRM1";
    private const string Prov = "SQLite";
    private static readonly Guid CompanyGuid = Guid.Parse("57c057c0-0000-4000-8000-000000000057");

    [Fact]
    public async Task Lead_Qualify_Convert_CreatesPartyInUnifiedMaster_AndOpportunity()
    {
        using var db = await SeededAsync();
        var lead = FullLead();
        await db.Crm.SaveLeadAsync(CO, lead, Actor);
        lead.Status = CrmLeadStatuses.Qualified;
        await db.Crm.SaveLeadAsync(CO, lead, Actor);

        var result = await db.Crm.ConvertLeadAsync(CO, lead.Id, Actor, "SAR");
        Assert.NotEqual(Guid.Empty, result.PartyId);
        Assert.True(result.PartyLedgerId > 0);
        Assert.StartsWith("CUST-", result.PartyCode);

        // The party lives in the unified master with the lead's data.
        var parties = await db.Crm.GetPartyOptionsAsync(CO);
        var party = Assert.Single(parties, p => p.Id == result.PartyId);
        Assert.Equal(result.PartyCode, party.Code);
        Assert.Equal("Acme Trading LLC", party.Name);
        Assert.Equal(result.PartyLedgerId, party.LedgerId);
        Assert.Equal(1, db.ScalarLong(
            $"SELECT COUNT(*) FROM parties WHERE company_id = '{CO}' AND kind = 'C' AND email = 'sara@acme.example' AND phone = '+962-6-500-1000' AND mobile = '+962-79-000-1000' AND address = 'Rainbow Street 12, Amman'"));
        // No second party store exists anywhere else.
        Assert.Equal(0, db.ScalarLong(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('customers', 'suppliers')"));

        // The lead is stamped Converted with both links.
        var converted = await db.Crm.GetLeadAsync(CO, lead.Id);
        Assert.NotNull(converted);
        Assert.Equal(CrmLeadStatuses.Converted, converted!.Status);
        Assert.Equal(result.PartyId, converted.ConvertedPartyId);
        Assert.Equal(result.OpportunityId, converted.ConvertedOpportunityId);

        // The opportunity sits at the first Open stage, linked both ways.
        var opp = await db.Crm.GetOpportunityAsync(CO, result.OpportunityId);
        Assert.NotNull(opp);
        Assert.Equal(result.PartyId, opp!.PartyId);
        Assert.Equal(lead.Id, opp.LeadId);
        var stages = await db.Crm.GetStagesAsync(CO);
        Assert.Equal(stages.First(s => s.StageType == CrmStageTypes.Open).Id, opp.StageId);
        Assert.Equal(10m, opp.Probability);

        // A second conversion is a cataloged refusal, not a duplicate party.
        var ex = await Assert.ThrowsAsync<AppError>(() => db.Crm.ConvertLeadAsync(CO, lead.Id, Actor, "SAR"));
        Assert.Equal(ErrorCodes.CrmLeadAlreadyConverted, ex.Code);
        Assert.Equal(1, db.ScalarLong($"SELECT COUNT(*) FROM parties WHERE company_id = '{CO}' AND kind = 'C'"));
    }

    [Fact]
    public async Task Opportunity_StageWalk_HistoryRows_AndWeightedMath_AtEachStage()
    {
        using var db = await SeededAsync();
        var stages = await db.Crm.GetStagesAsync(CO);
        Assert.Equal(6, stages.Count);
        var byName = stages.ToDictionary(s => s.NameEn);

        var opp = new CrmOpportunity
        {
            Title = "ERP rollout for Acme",
            ExpectedAmount = 10000m,
            CurrencyCode = "SAR",
            StageId = byName["New"].Id
        };
        await db.Crm.SaveOpportunityAsync(CO, opp, Actor);

        async Task AssertWeighted(decimal expected, string stageName)
        {
            var board = await db.Crm.GetPipelineAsync(CO);
            var column = Assert.Single(board.Columns, c => c.Stage.NameEn == stageName);
            var row = Assert.Single(column.Opportunities, o => o.Id == opp.Id);
            Assert.Equal(expected, row.WeightedValue);
            Assert.Equal(expected, column.WeightedAmount);
        }

        await AssertWeighted(1000m, "New");
        await db.Crm.MoveStageAsync(CO, opp.Id, byName["Qualified"].Id, null, null, null, Actor);
        await AssertWeighted(2500m, "Qualified");
        await db.Crm.MoveStageAsync(CO, opp.Id, byName["Proposition"].Id, null, null, null, Actor);
        await AssertWeighted(5000m, "Proposition");
        await db.Crm.MoveStageAsync(CO, opp.Id, byName["Negotiation"].Id, null, null, null, Actor);
        await AssertWeighted(7500m, "Negotiation");

        // History: creation + three moves, probabilities chained.
        var history = await db.Crm.GetStageHistoryAsync(CO, opp.Id);
        Assert.Equal(4, history.Count);
        Assert.Null(history[0].FromStageId);
        Assert.Equal(byName["New"].Id, history[0].ToStageId);
        Assert.Equal(10m, history[0].ToProbability);
        Assert.Equal(25m, history[1].ToProbability);
        Assert.Equal(50m, history[2].ToProbability);
        Assert.Equal(75m, history[3].ToProbability);
        Assert.Equal(history[2].ToProbability, history[3].FromProbability);

        // Losing requires a reason (CRM-9006); with one, probability -> 0.
        var lost = await Assert.ThrowsAsync<AppError>(() =>
            db.Crm.MoveStageAsync(CO, opp.Id, byName["Lost"].Id, null, null, null, Actor));
        Assert.Equal(ErrorCodes.CrmLostReasonRequired, lost.Code);
        var reasons = await db.Crm.GetLostReasonsAsync(CO);
        Assert.Equal(6, reasons.Count);
        await db.Crm.MoveStageAsync(CO, opp.Id, byName["Lost"].Id, null,
            reasons.First(r => r.NameEn == "Price").Id, "Budget cut", Actor);
        var after = await db.Crm.GetOpportunityAsync(CO, opp.Id);
        Assert.Equal(0m, after!.Probability);
        Assert.Equal(0m, after.WeightedValue);
        Assert.NotNull(after.LostReasonId);
        Assert.Equal(5, (await db.Crm.GetStageHistoryAsync(CO, opp.Id)).Count);
    }

    [Fact]
    public async Task Activity_DueYesterday_IsOverdue_AndCompleting_RemovesIt()
    {
        using var db = await SeededAsync();
        var opp = new CrmOpportunity { Title = "Follow-up deal", ExpectedAmount = 500m, CurrencyCode = "SAR" };
        await db.Crm.SaveOpportunityAsync(CO, opp, Actor);

        var yesterday = DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd") + " 09:00";
        var activity = new CrmActivity
        {
            Subject = "Call the buyer",
            ActivityType = CrmActivityTypes.Call,
            DueAt = yesterday,
            OppId = opp.Id,
            AssignedTo = Actor
        };
        await db.Crm.SaveActivityAsync(CO, activity, Actor);

        var today = DateTime.Today.ToString("yyyy-MM-dd") + " 00:00";
        var overdue = await db.Crm.GetOverdueActivitiesAsync(CO, today);
        Assert.Contains(overdue, a => a.Id == activity.Id);

        await db.Crm.CompleteActivityAsync(CO, activity.Id, "Buyer asked for the quotation", Actor);
        overdue = await db.Crm.GetOverdueActivitiesAsync(CO, today);
        Assert.DoesNotContain(overdue, a => a.Id == activity.Id);
        var forOpp = await db.Crm.GetActivitiesForAsync(CO, null, opp.Id, null);
        var done = Assert.Single(forOpp);
        Assert.Equal(CrmActivityStatuses.Done, done.Status);
        Assert.Equal("Buyer asked for the quotation", done.ResultNotes);
        Assert.NotNull(done.CompletedAt);
    }

    [Fact]
    public async Task Activity_MustTargetExactlyOne()
    {
        using var db = await SeededAsync();
        var none = new CrmActivity { Subject = "Orphan", DueAt = "2026-11-20 09:00" };
        var ex = await Assert.ThrowsAsync<AppError>(() => db.Crm.SaveActivityAsync(CO, none, Actor));
        Assert.Equal(ErrorCodes.CrmActivityTargetInvalid, ex.Code);

        var lead = FullLead();
        await db.Crm.SaveLeadAsync(CO, lead, Actor);
        var opp = new CrmOpportunity { Title = "Deal", CurrencyCode = "SAR" };
        await db.Crm.SaveOpportunityAsync(CO, opp, Actor);
        var both = new CrmActivity { Subject = "Ambiguous", DueAt = "2026-11-20 09:00", LeadId = lead.Id, OppId = opp.Id };
        ex = await Assert.ThrowsAsync<AppError>(() => db.Crm.SaveActivityAsync(CO, both, Actor));
        Assert.Equal(ErrorCodes.CrmActivityTargetInvalid, ex.Code);
    }

    [Fact]
    public async Task Quotation_IsLinkedBothWays_AndCreatesNoEffects()
    {
        using var db = await SeededAsync();
        var lead = FullLead();
        await db.Crm.SaveLeadAsync(CO, lead, Actor);
        var conversion = await db.Crm.ConvertLeadAsync(CO, lead.Id, Actor, "SAR");

        var quote = await db.Crm.CreateQuotationAsync(CO, conversion.OpportunityId, Actor,
            new[] { new CrmQuotationLine(null, null, "ERP rollout", "PCS", 2m, 1500m) });
        Assert.StartsWith("QUO-", quote.DocNo);
        Assert.Equal(1, db.ScalarLong(
            $"SELECT COUNT(*) FROM sales_documents WHERE doc_id = {quote.DocId} AND doc_kind = 'QUO' AND status = 'Draft' AND grand_total = 3000"));
        Assert.Equal(1, db.ScalarLong(
            $"SELECT COUNT(*) FROM sales_documents WHERE doc_id = {quote.DocId} AND party_id = {conversion.PartyLedgerId}"));

        // Step 5: a quotation has NO receivable / GL / stock effects.
        Assert.Equal(0, db.ScalarLong($"SELECT COUNT(*) FROM party_ledger WHERE company_id = '{CO}'"));
        Assert.Equal(0, db.ScalarLong("SELECT COUNT(*) FROM journal_entries"));
        Assert.Equal(0, db.ScalarLong(
            $"SELECT COUNT(*) FROM material_stock_movements WHERE company_id = '{CO}'"));

        // The link reads back with the live document facts.
        var documents = await db.Crm.GetOpportunityDocumentsAsync(CO, conversion.OpportunityId);
        var link = Assert.Single(documents);
        Assert.Equal(CrmDocumentLinkTypes.Quotation, link.LinkType);
        Assert.Equal(quote.DocId, link.DocId);
        Assert.Equal(quote.DocNo, link.DocNo);
        Assert.Equal("Draft", link.DocStatus);
        Assert.Equal(3000m, link.DocTotal);
    }

    [Fact]
    public async Task FullyPopulated_Lead_And_Opportunity_RoundTrip()
    {
        using var db = await SeededAsync();
        var lead = FullLead();
        await db.Crm.SaveLeadAsync(CO, lead, Actor);
        var loadedLead = await db.Crm.GetLeadAsync(CO, lead.Id);
        Assert.NotNull(loadedLead);
        Assert.Equal(lead.LeadName, loadedLead!.LeadName);
        Assert.Equal(lead.Organization, loadedLead.Organization);
        Assert.Equal(lead.Email, loadedLead.Email);
        Assert.Equal(lead.Phone, loadedLead.Phone);
        Assert.Equal(lead.Mobile, loadedLead.Mobile);
        Assert.Equal(lead.Address, loadedLead.Address);
        Assert.Equal(lead.Source, loadedLead.Source);
        Assert.Equal(lead.Campaign, loadedLead.Campaign);
        Assert.Equal(lead.Status, loadedLead.Status);
        Assert.Equal(lead.AssignedRepCode, loadedLead.AssignedRepCode);
        Assert.Equal(lead.Notes, loadedLead.Notes);

        var conversion = await db.Crm.ConvertLeadAsync(CO, lead.Id, Actor, "SAR");
        var stages = await db.Crm.GetStagesAsync(CO);
        var opp = new CrmOpportunity
        {
            Title = "Full deal",
            PartyId = conversion.PartyId,
            LeadId = lead.Id,
            StageId = stages.First(s => s.NameEn == "Proposition").Id,
            Probability = 55m,
            ExpectedAmount = 12345.67m,
            CurrencyCode = "USD",
            ExpectedCloseDate = "2026-12-24",
            OppType = CrmOpportunityTypes.Upsell,
            Source = CrmSources.Exhibition,
            Campaign = "GITEX 2026",
            AssignedRepCode = "REP-1"
        };
        await db.Crm.SaveOpportunityAsync(CO, opp, Actor);
        var loaded = await db.Crm.GetOpportunityAsync(CO, opp.Id);
        Assert.NotNull(loaded);
        Assert.Equal(opp.Title, loaded!.Title);
        Assert.Equal(opp.PartyId, loaded.PartyId);
        Assert.Equal(conversion.PartyLedgerId, loaded.PartyLedgerId);
        Assert.Equal(opp.LeadId, loaded.LeadId);
        Assert.Equal(opp.StageId, loaded.StageId);
        Assert.Equal(55m, loaded.Probability);
        Assert.Equal(12345.67m, loaded.ExpectedAmount);
        Assert.Equal(6790.1185m, loaded.WeightedValue); // 12345.67 x 55%
        Assert.Equal("USD", loaded.CurrencyCode);
        Assert.Equal("2026-12-24", loaded.ExpectedCloseDate);
        Assert.Equal(opp.OppType, loaded.OppType);
        Assert.Equal(opp.Source, loaded.Source);
        Assert.Equal(opp.Campaign, loaded.Campaign);
        Assert.Equal(opp.AssignedRepCode, loaded.AssignedRepCode);
    }

    [Fact]
    public async Task OpportunityItems_RoundTrip_AndDefineExpectedAmount()
    {
        using var db = await SeededAsync();
        var opp = new CrmOpportunity
        {
            Title = "Priced deal",
            ExpectedAmount = 999m, // must be replaced by the items total
            CurrencyCode = "SAR",
            Items =
            {
                new CrmOpportunityItem { Description = "Implementation", Qty = 2m, Unit = "DAY", UnitPrice = 100m, DiscountPct = 10m, TaxRate = 15m },
                new CrmOpportunityItem { MaterialCode = "LIC-1", Description = "Licence", Qty = 1m, Unit = "PCS", UnitPrice = 50m }
            }
        };
        await db.Crm.SaveOpportunityAsync(CO, opp, Actor);

        // Line math: 2 x 100 x 0.9 = 180, tax 27, total 207; 50 + 0 = 50.
        var loaded = await db.Crm.GetOpportunityAsync(CO, opp.Id);
        Assert.NotNull(loaded);
        Assert.Equal(257m, loaded!.ExpectedAmount);
        Assert.Equal(2, loaded.Items.Count);
        var first = loaded.Items[0];
        Assert.Equal(1, first.LineNo);
        Assert.Equal("Implementation", first.Description);
        Assert.Equal(2m, first.Qty);
        Assert.Equal("DAY", first.Unit);
        Assert.Equal(100m, first.UnitPrice);
        Assert.Equal(10m, first.DiscountPct);
        Assert.Equal(15m, first.TaxRate);
        Assert.Equal(180m, first.LineSubtotal);
        Assert.Equal(27m, first.LineTax);
        Assert.Equal(207m, first.LineTotal);
        Assert.Equal("LIC-1", loaded.Items[1].MaterialCode);
        Assert.Equal(2, loaded.Items[1].LineNo);

        // Update: change a qty, drop the second line — both persist.
        loaded.Items[0].Qty = 3m;
        loaded.Items.RemoveAt(1);
        await db.Crm.SaveOpportunityAsync(CO, loaded, Actor);
        var reloaded = await db.Crm.GetOpportunityAsync(CO, opp.Id);
        Assert.NotNull(reloaded);
        var only = Assert.Single(reloaded!.Items);
        Assert.Equal(3m, only.Qty);
        Assert.Equal(310.5m, reloaded.ExpectedAmount); // 3 x 100 x 0.9 = 270, +15% tax = 310.5
        Assert.Equal(1, db.ScalarLong(
            $"SELECT COUNT(*) FROM crm_opportunity_items WHERE company_id = '{CO}' AND opportunity_id = '{opp.Id:D}' AND is_deleted = 0"));
    }

    [Fact]
    public async Task OpportunityItem_Invalid_IsCrm9008()
    {
        using var db = await SeededAsync();
        var opp = new CrmOpportunity
        {
            Title = "Bad items",
            CurrencyCode = "SAR",
            Items = { new CrmOpportunityItem { Description = "Zero qty", Qty = 0m, UnitPrice = 10m } }
        };
        var ex = await Assert.ThrowsAsync<AppError>(() => db.Crm.SaveOpportunityAsync(CO, opp, Actor));
        Assert.Equal(ErrorCodes.CrmOpportunityItemInvalid, ex.Code);

        opp.Items[0].Qty = 1m;
        opp.Items[0].UnitPrice = -1m;
        ex = await Assert.ThrowsAsync<AppError>(() => db.Crm.SaveOpportunityAsync(CO, opp, Actor));
        Assert.Equal(ErrorCodes.CrmOpportunityItemInvalid, ex.Code);
    }

    [Fact]
    public async Task Quotation_FromItems_CarriesPriceDiscountTax_AndTotals()
    {
        using var db = await SeededAsync();
        var lead = new CrmLead
        {
            LeadName = "Buyer One",
            Organization = "Buyer Co",
            Email = "buyer@co.example",
            Phone = "+100-1",
            Status = CrmLeadStatuses.Qualified
        };
        await db.Crm.SaveLeadAsync(CO, lead, Actor);
        var conversion = await db.Crm.ConvertLeadAsync(CO, lead.Id, Actor, "SAR");

        var opp = await db.Crm.GetOpportunityAsync(CO, conversion.OpportunityId);
        Assert.NotNull(opp);
        opp!.Items.Add(new CrmOpportunityItem { Description = "Implementation", Qty = 2m, Unit = "DAY", UnitPrice = 100m, DiscountPct = 10m, TaxRate = 15m });
        opp.Items.Add(new CrmOpportunityItem { MaterialCode = "LIC-1", Description = "Licence", Qty = 1m, Unit = "PCS", UnitPrice = 50m, TaxRate = 15m });
        await db.Crm.SaveOpportunityAsync(CO, opp, Actor);

        // No explicit lines: the repository builds them from the items.
        var validTill = new DateOnly(2027, 1, 31);
        var quote = await db.Crm.CreateQuotationAsync(CO, opp.Id, Actor, null, validTill);
        Assert.StartsWith("QUO-", quote.DocNo);

        // Header: subtotal 180 + 50 = 230, tax 27 + 7.5 = 34.5, grand 264.5.
        Assert.Equal(1, db.ScalarLong(
            $"SELECT COUNT(*) FROM sales_documents WHERE doc_id = {quote.DocId} AND doc_kind = 'QUO' AND subtotal = 230 AND tax_amount = 34.5 AND grand_total = 264.5 AND valid_till = '2027-01-31'"));
        // Lines carry discount and tax (never forced to 0).
        Assert.Equal(1, db.ScalarLong(
            $"SELECT COUNT(*) FROM sales_document_lines WHERE doc_id = {quote.DocId} AND line_no = 1 AND description = 'Implementation' AND qty = 2 AND unit_price = 100 AND discount_pct = 10 AND tax_rate = 15 AND line_subtotal = 180 AND line_tax = 27 AND line_total = 207"));
        Assert.Equal(1, db.ScalarLong(
            $"SELECT COUNT(*) FROM sales_document_lines WHERE doc_id = {quote.DocId} AND line_no = 2 AND material_code = 'LIC-1' AND qty = 1 AND unit_price = 50 AND discount_pct = 0 AND tax_rate = 15 AND line_total = 57.5"));
        // No fabricated title line.
        Assert.Equal(2, db.ScalarLong($"SELECT COUNT(*) FROM sales_document_lines WHERE doc_id = {quote.DocId}"));
        Assert.Equal(0, db.ScalarLong(
            $"SELECT COUNT(*) FROM sales_document_lines WHERE doc_id = {quote.DocId} AND description = 'Buyer Co'"));

        // A second quotation gets the NEXT sequence number, not a recount.
        var quote2 = await db.Crm.CreateQuotationAsync(CO, opp.Id, Actor);
        Assert.NotEqual(quote.DocNo, quote2.DocNo);
    }

    [Fact]
    public async Task DuplicateLead_SameEmailOrPhone_IsCrm9007()
    {
        using var db = await SeededAsync();
        var first = new CrmLead { LeadName = "First", Email = "dup@example.com", Phone = "+200-1", Status = CrmLeadStatuses.New };
        await db.Crm.SaveLeadAsync(CO, first, Actor);

        var sameEmail = new CrmLead { LeadName = "Second", Email = "DUP@example.com", Status = CrmLeadStatuses.New };
        var ex = await Assert.ThrowsAsync<AppError>(() => db.Crm.SaveLeadAsync(CO, sameEmail, Actor));
        Assert.Equal(ErrorCodes.CrmDuplicateLeadOrParty, ex.Code);
        Assert.Contains("First", ex.Message);

        var samePhone = new CrmLead { LeadName = "Third", Mobile = "+200-1", Status = CrmLeadStatuses.New };
        ex = await Assert.ThrowsAsync<AppError>(() => db.Crm.SaveLeadAsync(CO, samePhone, Actor));
        Assert.Equal(ErrorCodes.CrmDuplicateLeadOrParty, ex.Code);

        // Editing the original lead itself is not a duplicate.
        first.LeadName = "First Renamed";
        await db.Crm.SaveLeadAsync(CO, first, Actor);

        // Converting is refused too when a duplicate appears AFTER the
        // lead was saved (here: a party created through master data).
        var solo = new CrmLead { LeadName = "Solo", Email = "solo@example.com", Status = CrmLeadStatuses.Qualified };
        await db.Crm.SaveLeadAsync(CO, solo, Actor);
        db.Exec($"INSERT INTO parties (id, company_id, kind, code, name_en, email, ledger_id) VALUES ('{Guid.NewGuid():D}', '{CO}', 'C', 'CUST-9002', 'Solo Party Co', 'solo@example.com', 9002);");
        ex = await Assert.ThrowsAsync<AppError>(() => db.Crm.ConvertLeadAsync(CO, solo.Id, Actor, "SAR"));
        Assert.Equal(ErrorCodes.CrmDuplicateLeadOrParty, ex.Code);
    }

    [Fact]
    public async Task DuplicateLead_AgainstExistingParty_IsCrm9007_NamingTheParty()
    {
        using var db = await SeededAsync();
        var partyId = Guid.NewGuid();
        db.Exec($"INSERT INTO parties (id, company_id, kind, code, name_en, email, phone, ledger_id) VALUES ('{partyId:D}', '{CO}', 'C', 'CUST-9001', 'Existing Customer Co', 'party@example.com', '+300-9', 9001);");

        var lead = new CrmLead { LeadName = "New Lead", Email = "party@example.com", Status = CrmLeadStatuses.New };
        var ex = await Assert.ThrowsAsync<AppError>(() => db.Crm.SaveLeadAsync(CO, lead, Actor));
        Assert.Equal(ErrorCodes.CrmDuplicateLeadOrParty, ex.Code);
        Assert.Contains("Existing Customer Co", ex.Message);
    }

    [Fact]
    public async Task ConvertLead_LinkExistingParty_CreatesNoNewParty()
    {
        using var db = await SeededAsync();
        var partyId = Guid.NewGuid();
        db.Exec($"INSERT INTO parties (id, company_id, kind, code, name_en, email, phone, ledger_id) VALUES ('{partyId:D}', '{CO}', 'C', 'CUST-9001', 'Existing Customer Co', 'link@example.com', '+300-9', 9001);");

        // The lead predates the party (it was created before the
        // duplicate existed), so its row is inserted directly —
        // SaveLeadAsync would now refuse it as a duplicate.
        var leadId = Guid.NewGuid();
        db.Exec($"INSERT INTO crm_leads (lead_id, company_id, lead_name, email, phone, status) VALUES ('{leadId:D}', '{CO}', 'Link Me', 'link@example.com', '+300-9', 'Qualified');");

        var result = await db.Crm.ConvertLeadToExistingPartyAsync(CO, leadId, partyId, Actor, "SAR");
        Assert.Equal(partyId, result.PartyId);
        Assert.Equal(9001, result.PartyLedgerId);
        Assert.Equal("CUST-9001", result.PartyCode);

        // No new party was created.
        Assert.Equal(1, db.ScalarLong($"SELECT COUNT(*) FROM parties WHERE company_id = '{CO}'"));

        // The opportunity is against the existing party; the lead is stamped.
        var opp = await db.Crm.GetOpportunityAsync(CO, result.OpportunityId);
        Assert.NotNull(opp);
        Assert.Equal(partyId, opp!.PartyId);
        Assert.Equal(leadId, opp.LeadId);
        var lead = await db.Crm.GetLeadAsync(CO, leadId);
        Assert.Equal(CrmLeadStatuses.Converted, lead!.Status);
        Assert.Equal(partyId, lead.ConvertedPartyId);
        Assert.Equal(result.OpportunityId, lead.ConvertedOpportunityId);
    }

    // ------------------------------------------------------------------
    // W10 (migration 105): campaigns master, campaign_id linkage and
    // the outcome roll-up.
    // ------------------------------------------------------------------
    [Fact]
    public async Task Campaign_RoundTrips_AndLinksLeadOpportunityAndConversion()
    {
        using var db = await SeededAsync();
        var campaign = new CrmCampaign
        {
            Name = "Q4 Outreach",
            Kind = CrmCampaignKinds.Exhibition,
            StartDate = "2026-10-01",
            EndDate = "2026-12-31",
            Budget = 5000m,
            TargetNotes = "Trade fair push",
            Status = CrmCampaignStatuses.Active
        };
        await db.Crm.SaveCampaignAsync(CO, campaign, Actor);
        var saved = Assert.Single(await db.Crm.GetCampaignsAsync(CO));
        Assert.Equal(campaign.Id, saved.Id);
        Assert.Equal("Q4 Outreach", saved.Name);
        Assert.Equal(CrmCampaignKinds.Exhibition, saved.Kind);
        Assert.Equal("2026-10-01", saved.StartDate);
        Assert.Equal("2026-12-31", saved.EndDate);
        Assert.Equal(5000m, saved.Budget);
        Assert.Equal("Trade fair push", saved.TargetNotes);
        Assert.Equal(CrmCampaignStatuses.Active, saved.Status);

        // The lead carries the structured link next to the legacy text.
        var lead = FullLead();
        lead.CampaignId = campaign.Id;
        await db.Crm.SaveLeadAsync(CO, lead, Actor);
        var loadedLead = await db.Crm.GetLeadAsync(CO, lead.Id);
        Assert.Equal(campaign.Id, loadedLead!.CampaignId);
        Assert.Equal("Q4 Outreach", loadedLead.Campaign);

        // Conversion propagates the link to the created opportunity.
        lead.Status = CrmLeadStatuses.Qualified;
        await db.Crm.SaveLeadAsync(CO, lead, Actor);
        var conversion = await db.Crm.ConvertLeadAsync(CO, lead.Id, Actor, "SAR");
        var convertedOpp = await db.Crm.GetOpportunityAsync(CO, conversion.OpportunityId);
        Assert.Equal(campaign.Id, convertedOpp!.CampaignId);

        // A directly saved opportunity round-trips the link too.
        var opp = new CrmOpportunity
        {
            Title = "Campaign deal", ExpectedAmount = 8000m, CurrencyCode = "SAR",
            CampaignId = campaign.Id, Campaign = campaign.Name
        };
        await db.Crm.SaveOpportunityAsync(CO, opp, Actor);
        Assert.Equal(campaign.Id, (await db.Crm.GetOpportunityAsync(CO, opp.Id))!.CampaignId);
    }

    [Fact]
    public async Task CampaignOutcome_RollUp_CountsLeadsOpportunitiesAndWonValue()
    {
        using var db = await SeededAsync();
        var campaign = new CrmCampaign { Name = "Rollup", Status = CrmCampaignStatuses.Active };
        await db.Crm.SaveCampaignAsync(CO, campaign, Actor);
        var empty = new CrmCampaign { Name = "Empty", Status = CrmCampaignStatuses.Planned };
        await db.Crm.SaveCampaignAsync(CO, empty, Actor);

        var lead = FullLead();
        lead.CampaignId = campaign.Id;
        await db.Crm.SaveLeadAsync(CO, lead, Actor);

        var stages = await db.Crm.GetStagesAsync(CO);
        var won = stages.Single(s => s.StageType == CrmStageTypes.Won);
        var wonOpp = new CrmOpportunity
        {
            Title = "Won deal", ExpectedAmount = 8000m, CurrencyCode = "SAR", CampaignId = campaign.Id
        };
        await db.Crm.SaveOpportunityAsync(CO, wonOpp, Actor);
        await db.Crm.MoveStageAsync(CO, wonOpp.Id, won.Id, null, null, null, Actor);
        var openOpp = new CrmOpportunity
        {
            Title = "Open deal", ExpectedAmount = 2000m, CurrencyCode = "SAR", CampaignId = campaign.Id
        };
        await db.Crm.SaveOpportunityAsync(CO, openOpp, Actor);
        var unlinked = new CrmOpportunity { Title = "Unlinked", ExpectedAmount = 9999m, CurrencyCode = "SAR" };
        await db.Crm.SaveOpportunityAsync(CO, unlinked, Actor);

        var outcomes = await db.Crm.GetCampaignOutcomesAsync(CO);
        var rollup = Assert.Single(outcomes, o => o.CampaignId == campaign.Id);
        Assert.Equal(1, rollup.LeadCount);
        Assert.Equal(2, rollup.OpportunityCount);
        Assert.Equal(8000m, rollup.WonValue);
        var emptyRollup = Assert.Single(outcomes, o => o.CampaignId == empty.Id);
        Assert.Equal(0, emptyRollup.LeadCount);
        Assert.Equal(0, emptyRollup.OpportunityCount);
        Assert.Equal(0m, emptyRollup.WonValue);
    }

    [Fact]
    public async Task Campaign_InvalidKindStatusOrNegativeBudget_IsRefused()
    {
        using var db = await SeededAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            db.Crm.SaveCampaignAsync(CO, new CrmCampaign { Name = "Bad", Kind = "Bogus" }, Actor));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            db.Crm.SaveCampaignAsync(CO, new CrmCampaign { Name = "Bad", Status = "Bogus" }, Actor));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            db.Crm.SaveCampaignAsync(CO, new CrmCampaign { Name = "Bad", Budget = -1m }, Actor));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            db.Crm.SaveCampaignAsync(CO, new CrmCampaign { Name = "Bad", StartDate = "2026-12-31", EndDate = "2026-01-01" }, Actor));
        Assert.Empty(await db.Crm.GetCampaignsAsync(CO));
    }

    private static CrmLead FullLead() => new()
    {
        LeadName = "Sara Haddad",
        Organization = "Acme Trading LLC",
        Email = "sara@acme.example",
        Phone = "+962-6-500-1000",
        Mobile = "+962-79-000-1000",
        Address = "Rainbow Street 12, Amman",
        Source = CrmSources.Referral,
        Campaign = "Q4 Outreach",
        Status = CrmLeadStatuses.Working,
        AssignedRepCode = "REP-1",
        Notes = "Met at the trade fair; wants the full rollout."
    };

    private static async Task<TestDb> SeededAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "crm-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var dbPath = Path.Combine(root, "crm.db");
        var cs = $"Data Source={dbPath};";
        Assert.True((await SchemaMigrator.ApplyAllAsync("SQLite", dbPath)).Success);

        using (var c = new SqliteConnection(cs))
        {
            c.Open();
            void Exec(string sql)
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
            Exec($"INSERT INTO companies (company_id, company_code, company_name_ar, company_name_en) VALUES ('{CompanyGuid:D}', '{CO}', 'CRM Test Co', 'CRM Test Co');");
            Exec($"INSERT INTO sales_reps (id, company_id, code, name_en) VALUES ('{Guid.NewGuid():D}', '{CO}', 'REP-1', 'Rania Saleh');");
        }

        return new TestDb
        {
            Root = root,
            Cs = cs,
            Crm = new CrmService(new CrmRepository(Prov, cs))
        };
    }

    private sealed class TestDb : IDisposable
    {
        public required string Root { get; init; }
        public required string Cs { get; init; }
        public required CrmService Crm { get; init; }

        public long ScalarLong(string sql)
        {
            using var c = new SqliteConnection(Cs);
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToInt64(cmd.ExecuteScalar());
        }

        public void Exec(string sql)
        {
            using var c = new SqliteConnection(Cs);
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
        }
    }
}
