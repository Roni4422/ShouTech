using ShouTech.Domain.Entities;

namespace ShouTech.Domain.Interfaces;

/// <summary>CRM persistence (migration 057). CRM lives in the party
/// world, so every method keys the company by its CODE — the key
/// parties / sales_reps are written with — unlike the journal-world
/// repositories, which key by Guid. The one deliberate exception is the
/// quotation document row (see CreateQuotationAsync): the step-9 chain
/// requires the Guid company key on sales_documents, and InvRepo.Load
/// accepts both keys, so the Guid-keyed row stays openable in
/// InvoiceWin (decision C3, docs/BUILD_PHASEB_057.md).
///
/// NO entitlement gate exists here by design: licensing/entitlement
/// work is postponed by the owner, and CRM is governed by the CRM.*
/// RBAC permissions only. Entitlement gating arrives with the
/// postponed licensing work.</summary>
public interface ICrmRepository
{
    // ---- Stages / lost reasons (per-company configuration) --------------
    /// <summary>The company's stages in sequence order. Seeds the
    /// design's default pipeline on first use for the company.</summary>
    Task<IReadOnlyList<CrmStage>> GetStagesAsync(string companyId, CancellationToken cancellationToken = default);
    Task SaveStageAsync(string companyId, CrmStage stage, string userId, CancellationToken cancellationToken = default);
    Task SoftDeleteStageAsync(string companyId, Guid stageId, string userId, CancellationToken cancellationToken = default);

    /// <summary>The company's lost reasons. Seeds the design's six
    /// defaults on first use for the company.</summary>
    Task<IReadOnlyList<CrmLostReason>> GetLostReasonsAsync(string companyId, CancellationToken cancellationToken = default);
    Task SaveLostReasonAsync(string companyId, CrmLostReason reason, string userId, CancellationToken cancellationToken = default);

    // ---- Leads -------------------------------------------------------------
    Task<IReadOnlyList<CrmLead>> GetLeadsAsync(string companyId, string? status = null, CancellationToken cancellationToken = default);
    Task<CrmLead?> GetLeadAsync(string companyId, Guid leadId, CancellationToken cancellationToken = default);
    /// <summary>Saves a lead. Throws CRM-9007 when the lead's email or
    /// phone matches another lead or an existing party (duplicate
    /// detection, gap G1) — checked inside the save transaction.</summary>
    Task SaveLeadAsync(string companyId, CrmLead lead, string userId, CancellationToken cancellationToken = default);
    Task SoftDeleteLeadAsync(string companyId, Guid leadId, string userId, CancellationToken cancellationToken = default);

    /// <summary>Finds a duplicate for a lead: the same email OR phone
    /// (phone/mobile cross-checked) on another lead or on an existing
    /// party of the company. The lead's own row (lead.Id) is excluded.
    /// Null when there is no match.</summary>
    Task<CrmDuplicateMatch?> FindLeadDuplicateAsync(string companyId, CrmLead lead, CancellationToken cancellationToken = default);

    /// <summary>Lead conversion (ONE transaction): creates the party in
    /// the unified master (parties, kind C, with its ledger_id assigned
    /// exactly like MasterCardRepository assigns it — no second party
    /// store), creates the opportunity at the first Open stage, stamps
    /// both conversion links on the lead and sets its status Converted.
    /// Throws CRM-9001 when the lead is already converted.</summary>
    Task<CrmConversionResult> ConvertLeadAsync(string companyId, Guid leadId, string userId, string currencyCode, CancellationToken cancellationToken = default);

    /// <summary>Lead conversion linked to an EXISTING party (gap G2):
    /// no new party is inserted; the opportunity is created against
    /// <paramref name="existingPartyId"/> and the lead is stamped with
    /// that party. Throws CRM-9004 when the party does not exist for
    /// the company.</summary>
    Task<CrmConversionResult> ConvertLeadToExistingPartyAsync(string companyId, Guid leadId, Guid existingPartyId, string userId, string currencyCode, CancellationToken cancellationToken = default);

    // ---- Opportunities -------------------------------------------------------
    Task<IReadOnlyList<CrmOpportunity>> GetOpportunitiesAsync(string companyId, CancellationToken cancellationToken = default);
    Task<CrmOpportunity?> GetOpportunityAsync(string companyId, Guid oppId, CancellationToken cancellationToken = default);

    /// <summary>Creates or updates an opportunity. Creation places it at
    /// its stage (defaulting probability from the stage when the caller
    /// left it at 0 on a fresh record) and writes the creation history
    /// row; updates never move the stage (stage moves go through
    /// MoveStageAsync so history is complete). The opportunity's Items
    /// (migration 092) are replaced in the SAME transaction, and when
    /// items exist ExpectedAmount is stored as the items total.</summary>
    Task SaveOpportunityAsync(string companyId, CrmOpportunity opportunity, string userId, CancellationToken cancellationToken = default);

    /// <summary>The opportunity's live items in line order.</summary>
    Task<IReadOnlyList<CrmOpportunityItem>> GetOpportunityItemsAsync(string companyId, Guid oppId, CancellationToken cancellationToken = default);
    Task SoftDeleteOpportunityAsync(string companyId, Guid oppId, string userId, CancellationToken cancellationToken = default);

    /// <summary>Moves an opportunity to a stage in ONE transaction:
    /// updates stage + probability (the stage default unless an
    /// explicit probability is given), records lost reason/notes when
    /// the target is a Lost stage (required there, CRM-9006), and
    /// appends the stage-history row.</summary>
    Task MoveStageAsync(string companyId, Guid oppId, Guid toStageId, decimal? probability,
        Guid? lostReasonId, string? lostNotes, string userId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CrmStageHistoryEntry>> GetStageHistoryAsync(string companyId, Guid oppId, CancellationToken cancellationToken = default);

    /// <summary>The pipeline board: every stage (sequence order) with
    /// its live opportunities and per-column raw/weighted totals.</summary>
    Task<CrmPipelineBoard> GetPipelineAsync(string companyId, CancellationToken cancellationToken = default);

    // ---- Documents (quotation link, decision C3) ------------------------------
    /// <summary>Creates a Quotation-kind draft document for the
    /// opportunity's party and links it (ONE transaction): a
    /// sales_documents row with doc_kind 'QUO', status Draft, the
    /// company's GUID key (chain-compatible; InvRepo.Load accepts both
    /// keys), party = the opportunity party's ledger_id, plus the
    /// crm_opportunity_documents link. Per Step 5 a quotation has NO
    /// effects: this writes no party_ledger, journal or stock rows.
    /// Throws CRM-9004 when the opportunity has no party or the
    /// company's Guid cannot be resolved. Lines: explicit
    /// <paramref name="lines"/> win; otherwise the quotation is built
    /// from the opportunity's items (migration 092, discount/tax
    /// carried); only an item-less opportunity falls back to the
    /// title x expected-amount line. The document number comes from
    /// the document_number_sequences mechanism (migration 075, the
    /// InvRepo allocation pattern — never COUNT(*)+1), the exchange
    /// rate is resolved through FxRates for the opportunity currency
    /// (0 = unresolved when no rate exists anywhere, never a
    /// fabricated 1), and <paramref name="validTill"/> is stored on
    /// the quotation header when given.</summary>
    Task<CrmQuotationResult> CreateQuotationAsync(string companyId, Guid oppId, string userId,
        IReadOnlyList<CrmQuotationLine>? lines = null, DateOnly? validTill = null, CancellationToken cancellationToken = default);

    /// <summary>Records an opportunity &lt;-&gt; document link for a document
    /// that already exists (e.g. the order/invoice the step-9 chain
    /// produced from a linked quotation). Idempotent per document.</summary>
    Task LinkDocumentAsync(string companyId, Guid oppId, string linkType, long docId, string userId, CancellationToken cancellationToken = default);

    /// <summary>The opportunity's linked documents with live header
    /// facts (number, status, total) from sales_documents.</summary>
    Task<IReadOnlyList<CrmOpportunityDocument>> GetOpportunityDocumentsAsync(string companyId, Guid oppId, CancellationToken cancellationToken = default);

    // ---- Activities -------------------------------------------------------------
    Task<IReadOnlyList<CrmActivity>> GetActivitiesAsync(string companyId, bool openOnly = false, CancellationToken cancellationToken = default);

    /// <summary>Planned activities whose due moment is before
    /// <paramref name="asOf"/> ('yyyy-MM-dd' or 'yyyy-MM-dd HH:mm') —
    /// the overdue worklist data.</summary>
    Task<IReadOnlyList<CrmActivity>> GetOverdueActivitiesAsync(string companyId, string asOf, CancellationToken cancellationToken = default);

    /// <summary>Activities attached to one lead / opportunity / party.</summary>
    Task<IReadOnlyList<CrmActivity>> GetActivitiesForAsync(string companyId, Guid? leadId, Guid? oppId, Guid? partyId, CancellationToken cancellationToken = default);
    Task SaveActivityAsync(string companyId, CrmActivity activity, string userId, CancellationToken cancellationToken = default);

    /// <summary>Completes a Planned activity (status Done, completed_at
    /// stamped, result notes kept) in one transaction.</summary>
    Task CompleteActivityAsync(string companyId, Guid activityId, string? resultNotes, string userId, CancellationToken cancellationToken = default);
    Task SoftDeleteActivityAsync(string companyId, Guid activityId, string userId, CancellationToken cancellationToken = default);

    // ---- Campaigns (migration 105, sales Level 3) -------------------------------
    /// <summary>The company's campaigns in name order.</summary>
    Task<IReadOnlyList<CrmCampaign>> GetCampaignsAsync(string companyId, CancellationToken cancellationToken = default);
    Task SaveCampaignAsync(string companyId, CrmCampaign campaign, string userId, CancellationToken cancellationToken = default);
    Task SoftDeleteCampaignAsync(string companyId, Guid campaignId, string userId, CancellationToken cancellationToken = default);

    /// <summary>Outcome roll-up per campaign: linked leads, linked
    /// opportunities, and the expected amount of linked opportunities
    /// whose stage type is Won.</summary>
    Task<IReadOnlyList<CrmCampaignOutcome>> GetCampaignOutcomesAsync(string companyId, CancellationToken cancellationToken = default);

    // ---- Lookups the windows offer ----------------------------------------------
    /// <summary>Live parties of the company (the unified master) for
    /// opportunity / activity pickers: Guid id, code, name, ledger id.</summary>
    Task<IReadOnlyList<CrmPartyOption>> GetPartyOptionsAsync(string companyId, CancellationToken cancellationToken = default);

    /// <summary>Active CORE sales reps (code + name) for assignment
    /// pickers. Reps stay core data; CRM never writes them.</summary>
    Task<IReadOnlyList<CrmRepOption>> GetRepOptionsAsync(string companyId, CancellationToken cancellationToken = default);
}
