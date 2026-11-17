using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.Domain;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.Infrastructure;

/// <summary>
/// Provider-neutral CRM persistence (migration 057), following the
/// QualityRepository / MasterCardRepository patterns: parameterized SQL
/// only, company scoping on every statement, soft deletes,
/// single-transaction writes. Company keying is the party world's
/// company CODE (see ICrmRepository); the quotation document row is the
/// one Guid-keyed exception (decision C3).
///
/// Conversion is the heart of the module: it inserts the party into the
/// SAME parties table MasterCardRepository writes (same columns, same
/// ledger_id assignment rule) inside the conversion transaction — there
/// is deliberately no CRM-side party store to diverge (Step 4's whole
/// point).
/// </summary>
public sealed class CrmRepository : ICrmRepository
{
    private readonly string _provider;
    private readonly string _connectionString;

    public CrmRepository(string provider, string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("A database connection string is required.", nameof(connectionString));
        _provider = GlPosting.NormalizeProvider(provider);
        _connectionString = connectionString;
    }

    private bool IsPg => _provider == "PostgreSQL";
    private string NotDeleted => IsPg ? "is_deleted = false" : "is_deleted = 0";
    private string NowExpr => IsPg ? "now()" : "datetime('now')";

    /// <summary>The design's default pipeline (section 4.2), seeded per
    /// company on first use: stages are configuration, not code.</summary>
    private static readonly (string En, string Ar, int Seq, decimal Prob, string Type)[] DefaultStages =
    {
        ("New", "", 10, 10m, CrmStageTypes.Open),
        ("Qualified", "", 20, 25m, CrmStageTypes.Open),
        ("Proposition", "", 30, 50m, CrmStageTypes.Open),
        ("Negotiation", "", 40, 75m, CrmStageTypes.Open),
        ("Won", "", 50, 100m, CrmStageTypes.Won),
        ("Lost", "", 60, 0m, CrmStageTypes.Lost)
    };

    /// <summary>The design's default lost reasons (section 4.2).</summary>
    private static readonly string[] DefaultLostReasons =
        { "Price", "Competitor", "No Budget", "No Response", "Timing", "Project Cancelled" };

    // ------------------------------------------------------------------
    // Stages / lost reasons
    // ------------------------------------------------------------------
    public async Task<IReadOnlyList<CrmStage>> GetStagesAsync(string companyId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await EnsureStagesAsync(connection, transaction, companyId, "system", cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await ReadStagesAsync(connection, null, companyId, cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveStageAsync(string companyId, CrmStage stage, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(stage);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var exists = await ExistsAsync(connection, transaction,
                $"SELECT COUNT(*) FROM crm_stages WHERE company_id = @c AND stage_id = @id AND {NotDeleted}",
                companyId, stage.Id, cancellationToken).ConfigureAwait(false);
            if (!exists)
            {
                await using var insert = CreateCommand(connection, transaction, """
                    INSERT INTO crm_stages (stage_id, company_id, name_en, name_ar, sequence, probability, stage_type, is_active, created_by)
                    VALUES (@id, @c, @en, @ar, @seq, @prob, @type, @active, @user)
                    """);
                BindStage(insert, companyId, stage, userId);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await using var update = CreateCommand(connection, transaction, $"""
                    UPDATE crm_stages SET name_en = @en, name_ar = @ar, sequence = @seq, probability = @prob,
                        stage_type = @type, is_active = @active, updated_by = @user, updated_at = {NowExpr}
                    WHERE company_id = @c AND stage_id = @id AND {NotDeleted}
                    """);
                BindStage(update, companyId, stage, userId);
                await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public Task SoftDeleteStageAsync(string companyId, Guid stageId, string userId, CancellationToken cancellationToken = default)
        => SoftDeleteAsync("crm_stages", "stage_id", companyId, stageId, userId, cancellationToken);

    public async Task<IReadOnlyList<CrmLostReason>> GetLostReasonsAsync(string companyId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await EnsureLostReasonsAsync(connection, transaction, companyId, "system", cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        await using var command = CreateCommand(connection, null, $"""
            SELECT reason_id, name_en, name_ar, is_active FROM crm_lost_reasons
            WHERE company_id = @c AND {NotDeleted} ORDER BY name_en
            """);
        Add(command, "@c", companyId.Trim());
        var result = new List<CrmLostReason>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new CrmLostReason
            {
                Id = ReadGuid(reader, 0),
                NameEn = ReadString(reader, 1) ?? string.Empty,
                NameAr = ReadString(reader, 2) ?? string.Empty,
                IsActive = Truthy(reader.GetValue(3))
            });
        }
        return result;
    }

    public async Task SaveLostReasonAsync(string companyId, CrmLostReason reason, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(reason);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var exists = await ExistsAsync(connection, transaction,
                $"SELECT COUNT(*) FROM crm_lost_reasons WHERE company_id = @c AND reason_id = @id AND {NotDeleted}",
                companyId, reason.Id, cancellationToken).ConfigureAwait(false);
            if (!exists)
            {
                await using var insert = CreateCommand(connection, transaction, """
                    INSERT INTO crm_lost_reasons (reason_id, company_id, name_en, name_ar, is_active, created_by)
                    VALUES (@id, @c, @en, @ar, @active, @user)
                    """);
                BindReason(insert, companyId, reason, userId);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await using var update = CreateCommand(connection, transaction, $"""
                    UPDATE crm_lost_reasons SET name_en = @en, name_ar = @ar, is_active = @active,
                        updated_by = @user, updated_at = {NowExpr}
                    WHERE company_id = @c AND reason_id = @id AND {NotDeleted}
                    """);
                BindReason(update, companyId, reason, userId);
                await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    // ------------------------------------------------------------------
    // Leads
    // ------------------------------------------------------------------
    private const string LeadColumns = "lead_id, lead_name, organization, email, phone, mobile, address, source, campaign, status, assigned_rep_code, converted_party_id, converted_opportunity_id, notes";

    public async Task<IReadOnlyList<CrmLead>> GetLeadsAsync(string companyId, string? status = null, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var sql = $"SELECT {LeadColumns} FROM crm_leads WHERE company_id = @c AND {NotDeleted}";
        if (!string.IsNullOrWhiteSpace(status)) sql += " AND status = @status";
        sql += " ORDER BY created_at DESC, lead_name";
        await using var command = CreateCommand(connection, null, sql);
        Add(command, "@c", companyId.Trim());
        if (!string.IsNullOrWhiteSpace(status)) Add(command, "@status", status.Trim());
        var result = new List<CrmLead>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(ReadLead(reader));
        return result;
    }

    public async Task<CrmLead?> GetLeadAsync(string companyId, Guid leadId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            $"SELECT {LeadColumns} FROM crm_leads WHERE company_id = @c AND lead_id = @id AND {NotDeleted}");
        Add(command, "@c", companyId.Trim());
        Add(command, "@id", FormatGuid(leadId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadLead(reader) : null;
    }

    public async Task SaveLeadAsync(string companyId, CrmLead lead, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(lead);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var exists = await ExistsAsync(connection, transaction,
                $"SELECT COUNT(*) FROM crm_leads WHERE company_id = @c AND lead_id = @id AND {NotDeleted}",
                companyId, lead.Id, cancellationToken).ConfigureAwait(false);
            if (!exists)
            {
                await using var insert = CreateCommand(connection, transaction, """
                    INSERT INTO crm_leads (lead_id, company_id, lead_name, organization, email, phone, mobile, address,
                        source, campaign, status, assigned_rep_code, notes, created_by)
                    VALUES (@id, @c, @name, @org, @email, @phone, @mobile, @address,
                        @source, @campaign, @status, @rep, @notes, @user)
                    """);
                BindLead(insert, companyId, lead, userId);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // Conversion links are NOT editable through the card: the
                // update deliberately omits converted_* so a converted
                // lead's links can never be re-pointed by an edit.
                await using var update = CreateCommand(connection, transaction, $"""
                    UPDATE crm_leads SET lead_name = @name, organization = @org, email = @email, phone = @phone,
                        mobile = @mobile, address = @address, source = @source, campaign = @campaign,
                        status = @status, assigned_rep_code = @rep, notes = @notes,
                        updated_by = @user, updated_at = {NowExpr}
                    WHERE company_id = @c AND lead_id = @id AND {NotDeleted}
                    """);
                BindLead(update, companyId, lead, userId);
                await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public Task SoftDeleteLeadAsync(string companyId, Guid leadId, string userId, CancellationToken cancellationToken = default)
        => SoftDeleteAsync("crm_leads", "lead_id", companyId, leadId, userId, cancellationToken);

    // ------------------------------------------------------------------
    // Conversion: lead -> party (unified master) + opportunity
    // ------------------------------------------------------------------
    public async Task<CrmConversionResult> ConvertLeadAsync(string companyId, Guid leadId, string userId, string currencyCode, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        RequireUser(userId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureStagesAsync(connection, transaction, companyId, userId, cancellationToken).ConfigureAwait(false);

            CrmLead lead;
            await using (var command = CreateCommand(connection, transaction,
                $"SELECT {LeadColumns} FROM crm_leads WHERE company_id = @c AND lead_id = @id AND {NotDeleted}"))
            {
                Add(command, "@c", companyId.Trim());
                Add(command, "@id", FormatGuid(leadId));
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    throw new AppError(ErrorCodes.CrmLeadStateInvalid,
                        $"Lead '{leadId}' was not found for this company.", leadId.ToString());
                lead = ReadLead(reader);
            }

            if (lead.Status == CrmLeadStatuses.Converted || lead.ConvertedPartyId is not null)
                throw new AppError(ErrorCodes.CrmLeadAlreadyConverted,
                    $"Lead '{lead.LeadName}' has already been converted.", lead.LeadName);
            if (lead.Status == CrmLeadStatuses.Unqualified)
                throw new AppError(ErrorCodes.CrmLeadStateInvalid,
                    $"Lead '{lead.LeadName}' is unqualified and cannot be converted.", lead.LeadName);

            // 1) The party, in the unified master, through the same
            //    columns + ledger_id rule MasterCardRepository uses.
            var partyId = Guid.NewGuid();
            var partyCode = await NextPartyCodeAsync(connection, transaction, companyId, cancellationToken).ConfigureAwait(false);
            var ledgerId = await NextLedgerIdAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            var partyName = !string.IsNullOrWhiteSpace(lead.Organization) ? lead.Organization!.Trim() : lead.LeadName.Trim();
            await using (var insert = CreateCommand(connection, transaction, """
                INSERT INTO parties (id, company_id, kind, code, name_en, name_ar, party_group_id, tax_no,
                    commercial_reg_no, credit_limit, credit_days, price_tier, gl_account_code, phone, mobile,
                    email, address, is_active, notes, created_by, ledger_id)
                VALUES (@id, @c, 'C', @code, @nameEn, NULL, NULL, NULL, NULL, 0, 30, NULL, NULL,
                    @phone, @mobile, @email, @address, @active, @notes, @user, @ledgerId)
                """))
            {
                Add(insert, "@id", FormatGuid(partyId));
                Add(insert, "@c", companyId.Trim());
                Add(insert, "@code", partyCode);
                Add(insert, "@nameEn", partyName);
                Add(insert, "@phone", (object?)NullIfBlank(lead.Phone) ?? DBNull.Value);
                Add(insert, "@mobile", (object?)NullIfBlank(lead.Mobile) ?? DBNull.Value);
                Add(insert, "@email", (object?)NullIfBlank(lead.Email) ?? DBNull.Value);
                Add(insert, "@address", (object?)NullIfBlank(lead.Address) ?? DBNull.Value);
                Add(insert, "@active", BoolValue(true));
                Add(insert, "@notes", (object?)NullIfBlank(lead.Notes) ?? DBNull.Value);
                Add(insert, "@user", userId.Trim());
                Add(insert, "@ledgerId", ledgerId);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // 2) The opportunity at the first Open stage.
            var stages = await ReadStagesAsync(connection, transaction, companyId, cancellationToken).ConfigureAwait(false);
            var firstOpen = stages.FirstOrDefault(s => s.StageType == CrmStageTypes.Open)
                ?? throw new AppError(ErrorCodes.CrmOpportunityStageInvalid,
                    "The pipeline has no Open stage to receive the converted opportunity.", companyId);
            var opportunity = new CrmOpportunity
            {
                Id = Guid.NewGuid(),
                Title = partyName,
                PartyId = partyId,
                LeadId = lead.Id,
                StageId = firstOpen.Id,
                Probability = firstOpen.Probability,
                ExpectedAmount = 0m,
                CurrencyCode = string.IsNullOrWhiteSpace(currencyCode) ? "SAR" : currencyCode.Trim(),
                OppType = CrmOpportunityTypes.NewBusiness,
                Source = lead.Source,
                Campaign = lead.Campaign,
                AssignedRepCode = lead.AssignedRepCode
            };
            await InsertOpportunityAsync(connection, transaction, companyId, opportunity, userId, cancellationToken).ConfigureAwait(false);
            await InsertHistoryAsync(connection, transaction, companyId, opportunity.Id,
                null, null, firstOpen.Id, firstOpen.Probability, userId, "Lead conversion", cancellationToken).ConfigureAwait(false);

            // 3) Stamp the lead: status + both links, atomically with 1+2.
            await using (var stamp = CreateCommand(connection, transaction, $"""
                UPDATE crm_leads SET status = 'Converted', converted_party_id = @party, converted_opportunity_id = @opp,
                    updated_by = @user, updated_at = {NowExpr}
                WHERE company_id = @c AND lead_id = @id AND {NotDeleted}
                """))
            {
                Add(stamp, "@party", FormatGuid(partyId));
                Add(stamp, "@opp", FormatGuid(opportunity.Id));
                Add(stamp, "@user", userId.Trim());
                Add(stamp, "@c", companyId.Trim());
                Add(stamp, "@id", FormatGuid(leadId));
                if (await stamp.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new AppError(ErrorCodes.CrmLeadStateInvalid,
                        $"Lead '{lead.LeadName}' could not be stamped as converted.", lead.LeadName);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new CrmConversionResult(partyId, ledgerId, partyCode, opportunity.Id);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    // ------------------------------------------------------------------
    // Opportunities
    // ------------------------------------------------------------------
    private const string OppColumns = """
        o.opp_id, o.title, o.party_id, p.name_en, p.ledger_id, o.lead_id, o.stage_id, s.name_en, s.stage_type,
        o.probability, o.expected_amount, o.currency_code, o.expected_close_date, o.opp_type, o.source,
        o.campaign, o.assigned_rep_code, o.lost_reason_id, o.lost_notes
        """;

    private string OppFrom => $"""
        FROM crm_opportunities o
        LEFT JOIN parties p ON p.id = o.party_id AND p.company_id = o.company_id AND {(IsPg ? "p.is_deleted = false" : "p.is_deleted = 0")}
        LEFT JOIN crm_stages s ON s.stage_id = o.stage_id AND s.company_id = o.company_id AND {(IsPg ? "s.is_deleted = false" : "s.is_deleted = 0")}
        """;

    public async Task<IReadOnlyList<CrmOpportunity>> GetOpportunitiesAsync(string companyId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            $"SELECT {OppColumns} {OppFrom} WHERE o.company_id = @c AND {(IsPg ? "o.is_deleted = false" : "o.is_deleted = 0")} ORDER BY o.created_at DESC, o.title");
        Add(command, "@c", companyId.Trim());
        var result = new List<CrmOpportunity>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(ReadOpportunity(reader));
        return result;
    }

    public async Task<CrmOpportunity?> GetOpportunityAsync(string companyId, Guid oppId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            $"SELECT {OppColumns} {OppFrom} WHERE o.company_id = @c AND o.opp_id = @id AND {(IsPg ? "o.is_deleted = false" : "o.is_deleted = 0")}");
        Add(command, "@c", companyId.Trim());
        Add(command, "@id", FormatGuid(oppId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadOpportunity(reader) : null;
    }

    public async Task SaveOpportunityAsync(string companyId, CrmOpportunity opportunity, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(opportunity);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureStagesAsync(connection, transaction, companyId, userId, cancellationToken).ConfigureAwait(false);
            var stages = await ReadStagesAsync(connection, transaction, companyId, cancellationToken).ConfigureAwait(false);
            var stage = ResolveStage(stages, opportunity, companyId);

            var exists = await ExistsAsync(connection, transaction,
                $"SELECT COUNT(*) FROM crm_opportunities WHERE company_id = @c AND opp_id = @id AND {NotDeleted}",
                companyId, opportunity.Id, cancellationToken).ConfigureAwait(false);
            if (!exists)
            {
                opportunity.StageId = stage.Id;
                if (opportunity.Probability == 0m && stage.Probability != 0m)
                    opportunity.Probability = stage.Probability;
                await InsertOpportunityAsync(connection, transaction, companyId, opportunity, userId, cancellationToken).ConfigureAwait(false);
                await InsertHistoryAsync(connection, transaction, companyId, opportunity.Id,
                    null, null, stage.Id, opportunity.Probability, userId, "Opportunity created", cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // Stage moves are NOT part of the card save: they go
                // through MoveStageAsync so the history stays complete.
                // The stored stage/probability/lost fields are preserved.
                await using var update = CreateCommand(connection, transaction, $"""
                    UPDATE crm_opportunities SET title = @title, party_id = @party, lead_id = @lead,
                        expected_amount = @amount, currency_code = @currency, expected_close_date = @close,
                        opp_type = @type, source = @source, campaign = @campaign, assigned_rep_code = @rep,
                        updated_by = @user, updated_at = {NowExpr}
                    WHERE company_id = @c AND opp_id = @id AND {NotDeleted}
                    """);
                BindOpportunityCard(update, companyId, opportunity, userId);
                await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public Task SoftDeleteOpportunityAsync(string companyId, Guid oppId, string userId, CancellationToken cancellationToken = default)
        => SoftDeleteAsync("crm_opportunities", "opp_id", companyId, oppId, userId, cancellationToken);

    public async Task MoveStageAsync(string companyId, Guid oppId, Guid toStageId, decimal? probability,
        Guid? lostReasonId, string? lostNotes, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        RequireUser(userId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureStagesAsync(connection, transaction, companyId, userId, cancellationToken).ConfigureAwait(false);
            await EnsureLostReasonsAsync(connection, transaction, companyId, userId, cancellationToken).ConfigureAwait(false);
            var stages = await ReadStagesAsync(connection, transaction, companyId, cancellationToken).ConfigureAwait(false);
            var target = stages.FirstOrDefault(s => s.Id == toStageId)
                ?? throw new AppError(ErrorCodes.CrmOpportunityStageInvalid,
                    $"Stage '{toStageId}' was not found for this company.", toStageId.ToString());

            Guid? fromStageId;
            decimal fromProbability;
            await using (var command = CreateCommand(connection, transaction,
                $"SELECT stage_id, probability FROM crm_opportunities WHERE company_id = @c AND opp_id = @id AND {NotDeleted}"))
            {
                Add(command, "@c", companyId.Trim());
                Add(command, "@id", FormatGuid(oppId));
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    throw new AppError(ErrorCodes.CrmOpportunityStageInvalid,
                        $"Opportunity '{oppId}' was not found for this company.", oppId.ToString());
                fromStageId = ReadGuidOrNull(reader, 0);
                fromProbability = Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture);
            }

            if (target.StageType == CrmStageTypes.Lost && lostReasonId is null)
                throw new AppError(ErrorCodes.CrmLostReasonRequired,
                    "A lost reason is required when an opportunity is marked lost.", oppId.ToString());

            var newProbability = probability ?? target.Probability;
            await using (var update = CreateCommand(connection, transaction, $"""
                UPDATE crm_opportunities SET stage_id = @stage, probability = @prob,
                    lost_reason_id = @reason, lost_notes = @lostNotes,
                    updated_by = @user, updated_at = {NowExpr}
                WHERE company_id = @c AND opp_id = @id AND {NotDeleted}
                """))
            {
                Add(update, "@stage", FormatGuid(target.Id));
                Add(update, "@prob", newProbability);
                Add(update, "@reason", target.StageType == CrmStageTypes.Lost ? FormatGuidOrNull(lostReasonId) : DBNull.Value);
                Add(update, "@lostNotes", target.StageType == CrmStageTypes.Lost ? (object?)NullIfBlank(lostNotes) ?? DBNull.Value : DBNull.Value);
                Add(update, "@user", userId.Trim());
                Add(update, "@c", companyId.Trim());
                Add(update, "@id", FormatGuid(oppId));
                await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await InsertHistoryAsync(connection, transaction, companyId, oppId,
                fromStageId, fromProbability, target.Id, newProbability, userId,
                target.StageType == CrmStageTypes.Lost ? NullIfBlank(lostNotes) : null, cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IReadOnlyList<CrmStageHistoryEntry>> GetStageHistoryAsync(string companyId, Guid oppId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null, """
            SELECT h.history_id, h.opp_id, h.from_stage_id, fs.name_en, h.to_stage_id, ts.name_en,
                   h.from_probability, h.to_probability, h.changed_by, h.changed_at, h.notes
            FROM crm_opportunity_stage_history h
            LEFT JOIN crm_stages fs ON fs.stage_id = h.from_stage_id
            LEFT JOIN crm_stages ts ON ts.stage_id = h.to_stage_id
            WHERE h.company_id = @c AND h.opp_id = @id ORDER BY h.changed_at, h.history_id
            """);
        Add(command, "@c", companyId.Trim());
        Add(command, "@id", FormatGuid(oppId));
        var result = new List<CrmStageHistoryEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new CrmStageHistoryEntry
            {
                Id = ReadGuid(reader, 0),
                OppId = ReadGuid(reader, 1),
                FromStageId = ReadGuidOrNull(reader, 2),
                FromStageName = ReadString(reader, 3),
                ToStageId = ReadGuidOrNull(reader, 4),
                ToStageName = ReadString(reader, 5),
                FromProbability = ReadDecimalOrNull(reader, 6),
                ToProbability = ReadDecimalOrNull(reader, 7),
                ChangedBy = ReadString(reader, 8),
                ChangedAt = ReadString(reader, 9),
                Notes = ReadString(reader, 10)
            });
        }
        return result;
    }

    public async Task<CrmPipelineBoard> GetPipelineAsync(string companyId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        var stages = await GetStagesAsync(companyId, cancellationToken).ConfigureAwait(false);
        var opportunities = await GetOpportunitiesAsync(companyId, cancellationToken).ConfigureAwait(false);
        var columns = stages
            .Select(stage => new CrmPipelineColumn(stage,
                opportunities.Where(o => o.StageId == stage.Id).ToList()))
            .ToList();
        return new CrmPipelineBoard(columns);
    }

    // ------------------------------------------------------------------
    // Documents: quotation creation + links (decision C3)
    // ------------------------------------------------------------------
    public async Task<CrmQuotationResult> CreateQuotationAsync(string companyId, Guid oppId, string userId,
        IReadOnlyList<CrmQuotationLine>? lines = null, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        RequireUser(userId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CrmOpportunity opp;
            await using (var command = CreateCommand(connection, transaction,
                $"SELECT {OppColumns} {OppFrom} WHERE o.company_id = @c AND o.opp_id = @id AND {(IsPg ? "o.is_deleted = false" : "o.is_deleted = 0")}"))
            {
                Add(command, "@c", companyId.Trim());
                Add(command, "@id", FormatGuid(oppId));
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    throw new AppError(ErrorCodes.CrmOpportunityStageInvalid,
                        $"Opportunity '{oppId}' was not found for this company.", oppId.ToString());
                opp = ReadOpportunity(reader);
            }
            if (opp.PartyId is null || opp.PartyLedgerId is null)
                throw new AppError(ErrorCodes.CrmQuotationPartyMissing,
                    $"Opportunity '{opp.Title}' has no customer party; link or convert a party before creating a quotation.", opp.Title);

            Guid companyGuid;
            await using (var command = CreateCommand(connection, transaction,
                "SELECT company_id FROM companies WHERE company_code = @c"))
            {
                Add(command, "@c", companyId.Trim());
                var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (value is null or DBNull || !Guid.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out companyGuid) || companyGuid == Guid.Empty)
                    throw new AppError(ErrorCodes.CrmQuotationPartyMissing,
                        $"The company Guid for '{companyId}' could not be resolved, so a chain-compatible quotation cannot be created.", companyId);
            }

            var effectiveLines = lines is { Count: > 0 }
                ? lines
                : new[] { new CrmQuotationLine(null, null, opp.Title, null, 1m, opp.ExpectedAmount) };
            decimal subtotal = 0m;
            var computed = new List<(CrmQuotationLine Line, decimal Sub, decimal Tax, decimal Total)>();
            foreach (var line in effectiveLines)
            {
                var sub = decimal.Round(line.Qty * line.UnitPrice, 4, MidpointRounding.AwayFromZero);
                computed.Add((line, sub, 0m, sub));
                subtotal += sub;
            }

            var year = DateTime.Today.Year.ToString(CultureInfo.InvariantCulture);
            string docNo;
            await using (var command = CreateCommand(connection, transaction,
                "SELECT COUNT(*) FROM sales_documents WHERE company_id = @guid AND doc_kind = 'QUO' AND doc_no LIKE @pattern"))
            {
                Add(command, "@guid", companyGuid.ToString("D"));
                Add(command, "@pattern", $"QUO-{year}-%");
                var count = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
                docNo = $"QUO-{year}-{(count + 1).ToString("D4", CultureInfo.InvariantCulture)}";
            }

            long docId;
            var insertSql = IsPg
                ? """
                  INSERT INTO sales_documents (company_id, doc_kind, doc_no, doc_date, party_id, party_name,
                      currency_code, exchange_rate, subtotal, discount_amount, tax_amount, grand_total,
                      paid_amount, status, notes, created_by, is_deleted, party_code)
                  VALUES (@guid, 'QUO', @no, @date, @party, @pname, @cur, 1, @sub, 0, 0, @grand, 0, 'Draft', @notes, @user, 0, @pcode)
                  RETURNING doc_id
                  """
                : """
                  INSERT INTO sales_documents (company_id, doc_kind, doc_no, doc_date, party_id, party_name,
                      currency_code, exchange_rate, subtotal, discount_amount, tax_amount, grand_total,
                      paid_amount, status, notes, created_by, is_deleted, party_code)
                  VALUES (@guid, 'QUO', @no, @date, @party, @pname, @cur, 1, @sub, 0, 0, @grand, 0, 'Draft', @notes, @user, 0, @pcode);
                  SELECT last_insert_rowid();
                  """;
            await using (var insert = CreateCommand(connection, transaction, insertSql))
            {
                Add(insert, "@guid", companyGuid.ToString("D"));
                Add(insert, "@no", docNo);
                Add(insert, "@date", DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                Add(insert, "@party", opp.PartyLedgerId.Value);
                Add(insert, "@pname", (object?)opp.PartyName ?? DBNull.Value);
                Add(insert, "@cur", string.IsNullOrWhiteSpace(opp.CurrencyCode) ? "SAR" : opp.CurrencyCode.Trim());
                Add(insert, "@sub", subtotal);
                Add(insert, "@grand", subtotal);
                Add(insert, "@notes", $"CRM opportunity: {opp.Title}");
                Add(insert, "@user", userId.Trim());
                Add(insert, "@pcode", (object?)await PartyCodeAsync(connection, transaction, companyId, opp.PartyId.Value, cancellationToken).ConfigureAwait(false) ?? DBNull.Value);
                docId = Convert.ToInt64(await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            }

            var lineNo = 0;
            foreach (var (line, sub, tax, total) in computed)
            {
                lineNo++;
                await using var lineInsert = CreateCommand(connection, transaction, """
                    INSERT INTO sales_document_lines (doc_id, line_no, material_id, material_code, description, unit,
                        qty, unit_price, discount_pct, tax_rate, line_subtotal, line_tax, line_total)
                    VALUES (@doc, @no, @mat, @mcode, @desc, @unit, @qty, @price, 0, 0, @sub, @tax, @total)
                    """);
                Add(lineInsert, "@doc", docId);
                Add(lineInsert, "@no", lineNo);
                Add(lineInsert, "@mat", (object?)line.MaterialId ?? DBNull.Value);
                Add(lineInsert, "@mcode", (object?)NullIfBlank(line.MaterialCode) ?? DBNull.Value);
                Add(lineInsert, "@desc", line.Description);
                Add(lineInsert, "@unit", (object?)NullIfBlank(line.Unit) ?? DBNull.Value);
                Add(lineInsert, "@qty", line.Qty);
                Add(lineInsert, "@price", line.UnitPrice);
                Add(lineInsert, "@sub", sub);
                Add(lineInsert, "@tax", tax);
                Add(lineInsert, "@total", total);
                await lineInsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await InsertDocumentLinkAsync(connection, transaction, companyId, oppId,
                CrmDocumentLinkTypes.Quotation, "QUO", docId, userId, cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new CrmQuotationResult(docId, docNo);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task LinkDocumentAsync(string companyId, Guid oppId, string linkType, long docId, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        RequireUser(userId);
        if (!CrmDocumentLinkTypes.All.Contains(linkType))
            throw new ArgumentException($"Unknown CRM document link type '{linkType}'.", nameof(linkType));
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string docKind;
            await using (var command = CreateCommand(connection, transaction,
                "SELECT doc_kind FROM sales_documents WHERE doc_id = @id"))
            {
                Add(command, "@id", docId);
                var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                docKind = value is null or DBNull
                    ? CrmDocumentLinkTypes.DocKindFor(linkType)
                    : Convert.ToString(value, CultureInfo.InvariantCulture)!;
            }
            await InsertDocumentLinkAsync(connection, transaction, companyId, oppId, linkType, docKind, docId, userId, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IReadOnlyList<CrmOpportunityDocument>> GetOpportunityDocumentsAsync(string companyId, Guid oppId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null, """
            SELECT l.link_id, l.opp_id, l.doc_kind, l.doc_id, l.link_type, d.doc_no, d.status, d.grand_total, l.created_at
            FROM crm_opportunity_documents l
            LEFT JOIN sales_documents d ON d.doc_id = l.doc_id
            WHERE l.company_id = @c AND l.opp_id = @id ORDER BY l.created_at, l.link_id
            """);
        Add(command, "@c", companyId.Trim());
        Add(command, "@id", FormatGuid(oppId));
        var result = new List<CrmOpportunityDocument>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new CrmOpportunityDocument
            {
                Id = ReadGuid(reader, 0),
                OppId = ReadGuid(reader, 1),
                DocKind = ReadString(reader, 2) ?? string.Empty,
                DocId = Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture),
                LinkType = ReadString(reader, 4) ?? CrmDocumentLinkTypes.Quotation,
                DocNo = ReadString(reader, 5),
                DocStatus = ReadString(reader, 6),
                DocTotal = ReadDecimalOrNull(reader, 7),
                CreatedAt = ReadString(reader, 8)
            });
        }
        return result;
    }

    // ------------------------------------------------------------------
    // Activities
    // ------------------------------------------------------------------
    private const string ActivityColumns = "activity_id, subject, activity_type, due_at, completed_at, status, lead_id, opp_id, party_id, assigned_to, result_notes";

    public async Task<IReadOnlyList<CrmActivity>> GetActivitiesAsync(string companyId, bool openOnly = false, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var sql = $"SELECT {ActivityColumns} FROM crm_activities WHERE company_id = @c AND {NotDeleted}";
        if (openOnly) sql += " AND status = 'Planned'";
        sql += " ORDER BY due_at, subject";
        await using var command = CreateCommand(connection, null, sql);
        Add(command, "@c", companyId.Trim());
        var result = new List<CrmActivity>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(ReadActivity(reader));
        return result;
    }

    public async Task<IReadOnlyList<CrmActivity>> GetOverdueActivitiesAsync(string companyId, string asOf, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        if (string.IsNullOrWhiteSpace(asOf)) throw new ArgumentException("An as-of moment is required.", nameof(asOf));
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null, $"""
            SELECT {ActivityColumns} FROM crm_activities
            WHERE company_id = @c AND {NotDeleted} AND status = 'Planned' AND due_at < @asof
            ORDER BY due_at, subject
            """);
        Add(command, "@c", companyId.Trim());
        Add(command, "@asof", asOf.Trim());
        var result = new List<CrmActivity>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(ReadActivity(reader));
        return result;
    }

    public async Task<IReadOnlyList<CrmActivity>> GetActivitiesForAsync(string companyId, Guid? leadId, Guid? oppId, Guid? partyId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        var targets = (leadId is null ? 0 : 1) + (oppId is null ? 0 : 1) + (partyId is null ? 0 : 1);
        if (targets != 1)
            throw new AppError(ErrorCodes.CrmActivityTargetInvalid,
                "Activities are listed for exactly one of a lead, an opportunity or a party.");
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var (column, target) = leadId is not null ? ("lead_id", leadId.Value)
            : oppId is not null ? ("opp_id", oppId.Value) : ("party_id", partyId!.Value);
        await using var command = CreateCommand(connection, null, $"""
            SELECT {ActivityColumns} FROM crm_activities
            WHERE company_id = @c AND {NotDeleted} AND {column} = @target
            ORDER BY due_at, subject
            """);
        Add(command, "@c", companyId.Trim());
        Add(command, "@target", FormatGuid(target));
        var result = new List<CrmActivity>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(ReadActivity(reader));
        return result;
    }

    public async Task SaveActivityAsync(string companyId, CrmActivity activity, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(activity);
        var targets = (activity.LeadId is null ? 0 : 1) + (activity.OppId is null ? 0 : 1) + (activity.PartyId is null ? 0 : 1);
        if (targets != 1)
            throw new AppError(ErrorCodes.CrmActivityTargetInvalid,
                $"Activity '{activity.Subject}' must target exactly one of a lead, an opportunity or a party.", activity.Subject);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var exists = await ExistsAsync(connection, transaction,
                $"SELECT COUNT(*) FROM crm_activities WHERE company_id = @c AND activity_id = @id AND {NotDeleted}",
                companyId, activity.Id, cancellationToken).ConfigureAwait(false);
            if (!exists)
            {
                await using var insert = CreateCommand(connection, transaction, """
                    INSERT INTO crm_activities (activity_id, company_id, subject, activity_type, due_at, completed_at,
                        status, lead_id, opp_id, party_id, assigned_to, result_notes, created_by)
                    VALUES (@id, @c, @subject, @type, @due, @completed, @status, @lead, @opp, @party, @assigned, @result, @user)
                    """);
                BindActivity(insert, companyId, activity, userId);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await using var update = CreateCommand(connection, transaction, $"""
                    UPDATE crm_activities SET subject = @subject, activity_type = @type, due_at = @due,
                        completed_at = @completed, status = @status, lead_id = @lead, opp_id = @opp,
                        party_id = @party, assigned_to = @assigned, result_notes = @result,
                        updated_by = @user, updated_at = {NowExpr}
                    WHERE company_id = @c AND activity_id = @id AND {NotDeleted}
                    """);
                BindActivity(update, companyId, activity, userId);
                await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task CompleteActivityAsync(string companyId, Guid activityId, string? resultNotes, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        RequireUser(userId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var update = CreateCommand(connection, transaction, $"""
                UPDATE crm_activities SET status = 'Done', completed_at = {NowExpr}, result_notes = @result,
                    updated_by = @user, updated_at = {NowExpr}
                WHERE company_id = @c AND activity_id = @id AND {NotDeleted} AND status = 'Planned'
                """);
            Add(update, "@result", (object?)NullIfBlank(resultNotes) ?? DBNull.Value);
            Add(update, "@user", userId.Trim());
            Add(update, "@c", companyId.Trim());
            Add(update, "@id", FormatGuid(activityId));
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new AppError(ErrorCodes.CrmActivityTargetInvalid,
                    $"Activity '{activityId}' was not found or is no longer planned.", activityId.ToString());
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public Task SoftDeleteActivityAsync(string companyId, Guid activityId, string userId, CancellationToken cancellationToken = default)
        => SoftDeleteAsync("crm_activities", "activity_id", companyId, activityId, userId, cancellationToken);

    // ------------------------------------------------------------------
    // Lookups
    // ------------------------------------------------------------------
    public async Task<IReadOnlyList<CrmPartyOption>> GetPartyOptionsAsync(string companyId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null, $"""
            SELECT id, code, name_en, ledger_id FROM parties
            WHERE company_id = @c AND kind = 'C' AND {NotDeleted} ORDER BY code
            """);
        Add(command, "@c", companyId.Trim());
        var result = new List<CrmPartyOption>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new CrmPartyOption(
                ReadGuid(reader, 0),
                ReadString(reader, 1) ?? string.Empty,
                ReadString(reader, 2) ?? string.Empty,
                ReadLongOrNull(reader, 3) ?? 0L));
        }
        return result;
    }

    public async Task<IReadOnlyList<CrmRepOption>> GetRepOptionsAsync(string companyId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null, $"""
            SELECT code, name_en FROM sales_reps
            WHERE company_id = @c AND {NotDeleted} AND {(IsPg ? "is_active = true" : "is_active = 1")} ORDER BY code
            """);
        Add(command, "@c", companyId.Trim());
        var result = new List<CrmRepOption>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new CrmRepOption(ReadString(reader, 0) ?? string.Empty, ReadString(reader, 1) ?? string.Empty));
        return result;
    }

    // ------------------------------------------------------------------
    // Internals
    // ------------------------------------------------------------------
    private async Task EnsureStagesAsync(DbConnection connection, DbTransaction transaction, string companyId, string userId, CancellationToken cancellationToken)
    {
        await using (var count = CreateCommand(connection, transaction,
            $"SELECT COUNT(*) FROM crm_stages WHERE company_id = @c AND {NotDeleted}"))
        {
            Add(count, "@c", companyId.Trim());
            if (Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) > 0)
                return;
        }
        foreach (var (en, ar, seq, prob, type) in DefaultStages)
        {
            await using var insert = CreateCommand(connection, transaction, """
                INSERT INTO crm_stages (stage_id, company_id, name_en, name_ar, sequence, probability, stage_type, is_active, created_by)
                VALUES (@id, @c, @en, @ar, @seq, @prob, @type, @active, @user)
                """);
            Add(insert, "@id", FormatGuid(Guid.NewGuid()));
            Add(insert, "@c", companyId.Trim());
            Add(insert, "@en", en);
            Add(insert, "@ar", ar);
            Add(insert, "@seq", seq);
            Add(insert, "@prob", prob);
            Add(insert, "@type", type);
            Add(insert, "@active", BoolValue(true));
            Add(insert, "@user", string.IsNullOrWhiteSpace(userId) ? "system" : userId.Trim());
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task EnsureLostReasonsAsync(DbConnection connection, DbTransaction transaction, string companyId, string userId, CancellationToken cancellationToken)
    {
        await using (var count = CreateCommand(connection, transaction,
            $"SELECT COUNT(*) FROM crm_lost_reasons WHERE company_id = @c AND {NotDeleted}"))
        {
            Add(count, "@c", companyId.Trim());
            if (Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) > 0)
                return;
        }
        foreach (var name in DefaultLostReasons)
        {
            await using var insert = CreateCommand(connection, transaction, """
                INSERT INTO crm_lost_reasons (reason_id, company_id, name_en, name_ar, is_active, created_by)
                VALUES (@id, @c, @en, '', @active, @user)
                """);
            Add(insert, "@id", FormatGuid(Guid.NewGuid()));
            Add(insert, "@c", companyId.Trim());
            Add(insert, "@en", name);
            Add(insert, "@active", BoolValue(true));
            Add(insert, "@user", string.IsNullOrWhiteSpace(userId) ? "system" : userId.Trim());
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<IReadOnlyList<CrmStage>> ReadStagesAsync(DbConnection connection, DbTransaction? transaction, string companyId, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, $"""
            SELECT stage_id, name_en, name_ar, sequence, probability, stage_type, is_active
            FROM crm_stages WHERE company_id = @c AND {NotDeleted} ORDER BY sequence, name_en
            """);
        Add(command, "@c", companyId.Trim());
        var result = new List<CrmStage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new CrmStage
            {
                Id = ReadGuid(reader, 0),
                NameEn = ReadString(reader, 1) ?? string.Empty,
                NameAr = ReadString(reader, 2) ?? string.Empty,
                Sequence = Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture),
                Probability = Convert.ToDecimal(reader.GetValue(4), CultureInfo.InvariantCulture),
                StageType = ReadString(reader, 5) ?? CrmStageTypes.Open,
                IsActive = Truthy(reader.GetValue(6))
            });
        }
        return result;
    }

    private static CrmStage ResolveStage(IReadOnlyList<CrmStage> stages, CrmOpportunity opportunity, string companyId)
    {
        if (opportunity.StageId is { } stageId)
        {
            return stages.FirstOrDefault(s => s.Id == stageId)
                ?? throw new AppError(ErrorCodes.CrmOpportunityStageInvalid,
                    $"Stage '{stageId}' was not found for this company.", stageId.ToString());
        }
        return stages.FirstOrDefault(s => s.StageType == CrmStageTypes.Open)
            ?? throw new AppError(ErrorCodes.CrmOpportunityStageInvalid,
                "The pipeline has no Open stage for a new opportunity.", companyId);
    }

    private async Task InsertOpportunityAsync(DbConnection connection, DbTransaction transaction, string companyId, CrmOpportunity o, string userId, CancellationToken cancellationToken)
    {
        await using var insert = CreateCommand(connection, transaction, """
            INSERT INTO crm_opportunities (opp_id, company_id, title, party_id, lead_id, stage_id, probability,
                expected_amount, currency_code, expected_close_date, opp_type, source, campaign,
                assigned_rep_code, lost_reason_id, lost_notes, created_by)
            VALUES (@id, @c, @title, @party, @lead, @stage, @prob, @amount, @currency, @close, @type,
                @source, @campaign, @rep, @reason, @lostNotes, @user)
            """);
        Add(insert, "@id", FormatGuid(o.Id));
        Add(insert, "@c", companyId.Trim());
        Add(insert, "@title", o.Title.Trim());
        Add(insert, "@party", FormatGuidOrNull(o.PartyId));
        Add(insert, "@lead", FormatGuidOrNull(o.LeadId));
        Add(insert, "@stage", FormatGuidOrNull(o.StageId));
        Add(insert, "@prob", o.Probability);
        Add(insert, "@amount", o.ExpectedAmount);
        Add(insert, "@currency", string.IsNullOrWhiteSpace(o.CurrencyCode) ? "SAR" : o.CurrencyCode.Trim());
        Add(insert, "@close", (object?)NullIfBlank(o.ExpectedCloseDate) ?? DBNull.Value);
        Add(insert, "@type", (object?)NullIfBlank(o.OppType) ?? DBNull.Value);
        Add(insert, "@source", (object?)NullIfBlank(o.Source) ?? DBNull.Value);
        Add(insert, "@campaign", (object?)NullIfBlank(o.Campaign) ?? DBNull.Value);
        Add(insert, "@rep", (object?)NullIfBlank(o.AssignedRepCode) ?? DBNull.Value);
        Add(insert, "@reason", FormatGuidOrNull(o.LostReasonId));
        Add(insert, "@lostNotes", (object?)NullIfBlank(o.LostNotes) ?? DBNull.Value);
        Add(insert, "@user", userId.Trim());
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private void BindOpportunityCard(DbCommand command, string companyId, CrmOpportunity o, string userId)
    {
        Add(command, "@title", o.Title.Trim());
        Add(command, "@party", FormatGuidOrNull(o.PartyId));
        Add(command, "@lead", FormatGuidOrNull(o.LeadId));
        Add(command, "@amount", o.ExpectedAmount);
        Add(command, "@currency", string.IsNullOrWhiteSpace(o.CurrencyCode) ? "SAR" : o.CurrencyCode.Trim());
        Add(command, "@close", (object?)NullIfBlank(o.ExpectedCloseDate) ?? DBNull.Value);
        Add(command, "@type", (object?)NullIfBlank(o.OppType) ?? DBNull.Value);
        Add(command, "@source", (object?)NullIfBlank(o.Source) ?? DBNull.Value);
        Add(command, "@campaign", (object?)NullIfBlank(o.Campaign) ?? DBNull.Value);
        Add(command, "@rep", (object?)NullIfBlank(o.AssignedRepCode) ?? DBNull.Value);
        Add(command, "@user", userId.Trim());
        Add(command, "@c", companyId.Trim());
        Add(command, "@id", FormatGuid(o.Id));
    }

    private async Task InsertHistoryAsync(DbConnection connection, DbTransaction transaction, string companyId, Guid oppId,
        Guid? fromStageId, decimal? fromProbability, Guid toStageId, decimal toProbability,
        string userId, string? notes, CancellationToken cancellationToken)
    {
        await using var insert = CreateCommand(connection, transaction, """
            INSERT INTO crm_opportunity_stage_history (history_id, company_id, opp_id, from_stage_id, to_stage_id,
                from_probability, to_probability, changed_by, notes)
            VALUES (@id, @c, @opp, @from, @to, @fromProb, @toProb, @user, @notes)
            """);
        Add(insert, "@id", FormatGuid(Guid.NewGuid()));
        Add(insert, "@c", companyId.Trim());
        Add(insert, "@opp", FormatGuid(oppId));
        Add(insert, "@from", FormatGuidOrNull(fromStageId));
        Add(insert, "@to", FormatGuid(toStageId));
        Add(insert, "@fromProb", (object?)fromProbability ?? DBNull.Value);
        Add(insert, "@toProb", toProbability);
        Add(insert, "@user", userId.Trim());
        Add(insert, "@notes", (object?)NullIfBlank(notes) ?? DBNull.Value);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task InsertDocumentLinkAsync(DbConnection connection, DbTransaction transaction, string companyId, Guid oppId,
        string linkType, string docKind, long docId, string userId, CancellationToken cancellationToken)
    {
        await using (var exists = CreateCommand(connection, transaction,
            "SELECT COUNT(*) FROM crm_opportunity_documents WHERE opp_id = @opp AND doc_kind = @kind AND doc_id = @doc"))
        {
            Add(exists, "@opp", FormatGuid(oppId));
            Add(exists, "@kind", docKind);
            Add(exists, "@doc", docId);
            if (Convert.ToInt64(await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) > 0)
                return;
        }
        await using var insert = CreateCommand(connection, transaction, """
            INSERT INTO crm_opportunity_documents (link_id, company_id, opp_id, doc_kind, doc_id, link_type, created_by)
            VALUES (@id, @c, @opp, @kind, @doc, @type, @user)
            """);
        Add(insert, "@id", FormatGuid(Guid.NewGuid()));
        Add(insert, "@c", companyId.Trim());
        Add(insert, "@opp", FormatGuid(oppId));
        Add(insert, "@kind", docKind);
        Add(insert, "@doc", docId);
        Add(insert, "@type", linkType);
        Add(insert, "@user", userId.Trim());
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> PartyCodeAsync(DbConnection connection, DbTransaction transaction, string companyId, Guid partyId, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction,
            $"SELECT code FROM parties WHERE company_id = @c AND id = @id AND {NotDeleted}");
        Add(command, "@c", companyId.Trim());
        Add(command, "@id", FormatGuid(partyId));
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    /// <summary>The next free customer code (CUST-0001 pattern), probed
    /// inside the conversion transaction so concurrent conversions in
    /// the same company cannot collide on the unique index.</summary>
    private async Task<string> NextPartyCodeAsync(DbConnection connection, DbTransaction transaction, string companyId, CancellationToken cancellationToken)
    {
        var n = 0L;
        await using (var command = CreateCommand(connection, transaction,
            $"SELECT COUNT(*) FROM parties WHERE company_id = @c AND kind = 'C' AND {NotDeleted}"))
        {
            Add(command, "@c", companyId.Trim());
            n = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }
        while (true)
        {
            n++;
            var candidate = $"CUST-{n.ToString("D4", CultureInfo.InvariantCulture)}";
            await using var probe = CreateCommand(connection, transaction,
                $"SELECT COUNT(*) FROM parties WHERE company_id = @c AND kind = 'C' AND code = @code AND {NotDeleted}");
            Add(probe, "@c", companyId.Trim());
            Add(probe, "@code", candidate);
            if (Convert.ToInt64(await probe.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) == 0)
                return candidate;
        }
    }

    /// <summary>MasterCardRepository's rule, verbatim: the next
    /// ledger_id sits above every identity already in use (existing
    /// parties AND orphan party_ledger rows), so a new party can never
    /// inherit a stranger's balance.</summary>
    private async Task<long> NextLedgerIdAsync(DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
    {
        long maxParty = 0, maxLedger = 0;
        await using (var parties = CreateCommand(connection, transaction, "SELECT COALESCE(MAX(ledger_id), 0) FROM parties"))
        {
            maxParty = Convert.ToInt64(await parties.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }
        await using (var ledger = CreateCommand(connection, transaction, "SELECT COALESCE(MAX(party_id), 0) FROM party_ledger"))
        {
            maxLedger = Convert.ToInt64(await ledger.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }
        return Math.Max(maxParty, maxLedger) + 1;
    }

    private async Task SoftDeleteAsync(string table, string idColumn, string companyId, Guid id, string userId, CancellationToken cancellationToken)
    {
        RequireCompany(companyId);
        RequireUser(userId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null, $"""
            UPDATE {table} SET is_deleted = {(IsPg ? "true" : "1")}, updated_by = @user, updated_at = {NowExpr}
            WHERE company_id = @c AND {idColumn} = @id AND {NotDeleted}
            """);
        Add(command, "@user", userId.Trim());
        Add(command, "@c", companyId.Trim());
        Add(command, "@id", FormatGuid(id));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> ExistsAsync(DbConnection connection, DbTransaction transaction, string sql, string companyId, Guid id, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, sql);
        Add(command, "@c", companyId.Trim());
        Add(command, "@id", FormatGuid(id));
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) > 0;
    }

    private void BindStage(DbCommand command, string companyId, CrmStage s, string userId)
    {
        Add(command, "@id", FormatGuid(s.Id));
        Add(command, "@c", companyId.Trim());
        Add(command, "@en", s.NameEn.Trim());
        Add(command, "@ar", s.NameAr.Trim());
        Add(command, "@seq", s.Sequence);
        Add(command, "@prob", s.Probability);
        Add(command, "@type", s.StageType);
        Add(command, "@active", BoolValue(s.IsActive));
        Add(command, "@user", RequireUser(userId));
    }

    private void BindReason(DbCommand command, string companyId, CrmLostReason r, string userId)
    {
        Add(command, "@id", FormatGuid(r.Id));
        Add(command, "@c", companyId.Trim());
        Add(command, "@en", r.NameEn.Trim());
        Add(command, "@ar", r.NameAr.Trim());
        Add(command, "@active", BoolValue(r.IsActive));
        Add(command, "@user", RequireUser(userId));
    }

    private void BindLead(DbCommand command, string companyId, CrmLead lead, string userId)
    {
        Add(command, "@id", FormatGuid(lead.Id));
        Add(command, "@c", companyId.Trim());
        Add(command, "@name", lead.LeadName.Trim());
        Add(command, "@org", (object?)NullIfBlank(lead.Organization) ?? DBNull.Value);
        Add(command, "@email", (object?)NullIfBlank(lead.Email) ?? DBNull.Value);
        Add(command, "@phone", (object?)NullIfBlank(lead.Phone) ?? DBNull.Value);
        Add(command, "@mobile", (object?)NullIfBlank(lead.Mobile) ?? DBNull.Value);
        Add(command, "@address", (object?)NullIfBlank(lead.Address) ?? DBNull.Value);
        Add(command, "@source", (object?)NullIfBlank(lead.Source) ?? DBNull.Value);
        Add(command, "@campaign", (object?)NullIfBlank(lead.Campaign) ?? DBNull.Value);
        Add(command, "@status", lead.Status);
        Add(command, "@rep", (object?)NullIfBlank(lead.AssignedRepCode) ?? DBNull.Value);
        Add(command, "@notes", (object?)NullIfBlank(lead.Notes) ?? DBNull.Value);
        Add(command, "@user", RequireUser(userId));
    }

    private void BindActivity(DbCommand command, string companyId, CrmActivity a, string userId)
    {
        Add(command, "@id", FormatGuid(a.Id));
        Add(command, "@c", companyId.Trim());
        Add(command, "@subject", a.Subject.Trim());
        Add(command, "@type", a.ActivityType);
        Add(command, "@due", a.DueAt.Trim());
        Add(command, "@completed", (object?)NullIfBlank(a.CompletedAt) ?? DBNull.Value);
        Add(command, "@status", a.Status);
        Add(command, "@lead", FormatGuidOrNull(a.LeadId));
        Add(command, "@opp", FormatGuidOrNull(a.OppId));
        Add(command, "@party", FormatGuidOrNull(a.PartyId));
        Add(command, "@assigned", (object?)NullIfBlank(a.AssignedTo) ?? DBNull.Value);
        Add(command, "@result", (object?)NullIfBlank(a.ResultNotes) ?? DBNull.Value);
        Add(command, "@user", RequireUser(userId));
    }

    private static CrmLead ReadLead(DbDataReader reader) => new()
    {
        Id = ReadGuid(reader, 0),
        LeadName = ReadString(reader, 1) ?? string.Empty,
        Organization = ReadString(reader, 2),
        Email = ReadString(reader, 3),
        Phone = ReadString(reader, 4),
        Mobile = ReadString(reader, 5),
        Address = ReadString(reader, 6),
        Source = ReadString(reader, 7),
        Campaign = ReadString(reader, 8),
        Status = ReadString(reader, 9) ?? CrmLeadStatuses.New,
        AssignedRepCode = ReadString(reader, 10),
        ConvertedPartyId = ReadGuidOrNull(reader, 11),
        ConvertedOpportunityId = ReadGuidOrNull(reader, 12),
        Notes = ReadString(reader, 13)
    };

    private static CrmOpportunity ReadOpportunity(DbDataReader reader) => new()
    {
        Id = ReadGuid(reader, 0),
        Title = ReadString(reader, 1) ?? string.Empty,
        PartyId = ReadGuidOrNull(reader, 2),
        PartyName = ReadString(reader, 3),
        PartyLedgerId = ReadLongOrNull(reader, 4),
        LeadId = ReadGuidOrNull(reader, 5),
        StageId = ReadGuidOrNull(reader, 6),
        StageName = ReadString(reader, 7),
        StageType = ReadString(reader, 8),
        Probability = Convert.ToDecimal(reader.GetValue(9), CultureInfo.InvariantCulture),
        ExpectedAmount = Convert.ToDecimal(reader.GetValue(10), CultureInfo.InvariantCulture),
        CurrencyCode = ReadString(reader, 11) ?? "SAR",
        ExpectedCloseDate = ReadString(reader, 12),
        OppType = ReadString(reader, 13),
        Source = ReadString(reader, 14),
        Campaign = ReadString(reader, 15),
        AssignedRepCode = ReadString(reader, 16),
        LostReasonId = ReadGuidOrNull(reader, 17),
        LostNotes = ReadString(reader, 18)
    };

    private static CrmActivity ReadActivity(DbDataReader reader) => new()
    {
        Id = ReadGuid(reader, 0),
        Subject = ReadString(reader, 1) ?? string.Empty,
        ActivityType = ReadString(reader, 2) ?? CrmActivityTypes.Task,
        DueAt = ReadString(reader, 3) ?? string.Empty,
        CompletedAt = ReadString(reader, 4),
        Status = ReadString(reader, 5) ?? CrmActivityStatuses.Planned,
        LeadId = ReadGuidOrNull(reader, 6),
        OppId = ReadGuidOrNull(reader, 7),
        PartyId = ReadGuidOrNull(reader, 8),
        AssignedTo = ReadString(reader, 9),
        ResultNotes = ReadString(reader, 10)
    };

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void RequireCompany(string companyId)
    {
        if (string.IsNullOrWhiteSpace(companyId))
            throw new ArgumentException("Company is required.", nameof(companyId));
    }

    private static string RequireUser(string userId)
        => string.IsNullOrWhiteSpace(userId)
            ? throw new ArgumentException("User is required.", nameof(userId))
            : userId.Trim();

    private object BoolValue(bool value) => IsPg ? value : (value ? 1 : 0);

    private static bool Truthy(object value) => value switch
    {
        bool b => b,
        long l => l != 0,
        int i => i != 0,
        string s => s is "1" or "true" or "True",
        _ => Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0
    };

    private DbConnection CreateConnection() => IsPg
        ? new NpgsqlConnection(_connectionString)
        : new SqliteConnection(_connectionString);

    private static DbCommand CreateCommand(DbConnection connection, DbTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        if (transaction is not null) command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private object FormatGuid(Guid value) => IsPg ? value : value.ToString("D");

    private object FormatGuidOrNull(Guid? value)
        => value is null ? DBNull.Value : FormatGuid(value.Value);

    private static Guid ReadGuid(DbDataReader reader, int ordinal)
        => Guid.Parse(Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture)!);

    private static Guid? ReadGuidOrNull(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal)
            ? null
            : Guid.Parse(Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture)!);

    private static long? ReadLongOrNull(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static string? ReadString(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static decimal? ReadDecimalOrNull(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : Convert.ToDecimal(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
}
