using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.Domain;
using ShouTech.Domain.Entities;

namespace ShouTech.Infrastructure;

/// <summary>
/// Provider-neutral sales-commission persistence (migration 097),
/// following the CrmRepository patterns: parameterized SQL only,
/// company scoping on every statement, soft deletes, single-transaction
/// writes. Company keying is the party world's company CODE, exactly
/// like sales_reps (031); documents carry Guid-or-code company keys, so
/// generation resolves the code through the companies registry
/// (PartyLedger.ResolveCompanyCodeAsync) before touching rep/policy
/// rows.
///
/// Generation is deliberately IDEMPOTENT (the ux_sco_entries_source
/// unique index plus an existence check before every insert): a posting
/// hook may fire twice for one document without doubling commission.
/// </summary>
public sealed class CommRepository : ICommRepository
{
    private readonly string _provider;
    private readonly string _connectionString;

    public CommRepository(string provider, string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("A database connection string is required.", nameof(connectionString));
        _provider = GlPosting.NormalizeProvider(provider);
        _connectionString = connectionString;
    }

    private bool IsPg => _provider == "PostgreSQL";
    private string NotDeleted => IsPg ? "is_deleted = false" : "is_deleted = 0";
    private string NowExpr => IsPg ? "now()" : "datetime('now')";
    private object Truth(bool value) => IsPg ? value : (value ? 1 : 0);

    // ------------------------------------------------------------------
    // Policies
    // ------------------------------------------------------------------
    public async Task<IReadOnlyList<CommPolicy>> GetPoliciesAsync(string companyId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var policies = await ReadPoliciesAsync(connection, null, companyId.Trim(), null, cancellationToken).ConfigureAwait(false);
        await LoadTiersAsync(connection, null, companyId.Trim(), policies, cancellationToken).ConfigureAwait(false);
        return policies;
    }

    public async Task SavePolicyAsync(string companyId, CommPolicy policy, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(policy);
        var company = companyId.Trim();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var exists = await ExistsAsync(connection, transaction,
                $"SELECT COUNT(*) FROM sco_commission_policies WHERE company_id = @c AND policy_id = @id AND {NotDeleted}",
                company, policy.Id, cancellationToken).ConfigureAwait(false);
            if (!exists)
            {
                await using var insert = CreateCommand(connection, transaction, """
                    INSERT INTO sco_commission_policies
                        (policy_id, company_id, policy_name, basis, rate_pct, rep_scope, rep_code,
                         product_scope, material_id, material_group_code, customer_scope, party_id,
                         area_code, valid_from, valid_to, is_active, created_by)
                    VALUES (@id, @c, @name, @basis, @rate, @repScope, @rep,
                            @prodScope, @material, @group, @custScope, @party,
                            @area, @from, @to, @active, @user)
                    """);
                BindPolicy(insert, company, policy, userId);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await using var update = CreateCommand(connection, transaction, $"""
                    UPDATE sco_commission_policies SET policy_name = @name, basis = @basis, rate_pct = @rate,
                        rep_scope = @repScope, rep_code = @rep, product_scope = @prodScope, material_id = @material,
                        material_group_code = @group, customer_scope = @custScope, party_id = @party,
                        area_code = @area, valid_from = @from, valid_to = @to, is_active = @active,
                        updated_by = @user, updated_at = {NowExpr}
                    WHERE company_id = @c AND policy_id = @id AND {NotDeleted}
                    """);
                BindPolicy(update, company, policy, userId);
                await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // Tiers are replaced wholesale: the editor owns the full slab
            // list, so a removed slab must not survive as a soft-deleted
            // ghost that still resolves.
            await using (var wipe = CreateCommand(connection, transaction,
                "DELETE FROM sco_commission_policy_tiers WHERE company_id = @c AND policy_id = @id"))
            {
                Add(wipe, "@c", company);
                Add(wipe, "@id", FormatGuid(policy.Id));
                await wipe.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            foreach (var tier in policy.Tiers)
            {
                await using var insertTier = CreateCommand(connection, transaction, """
                    INSERT INTO sco_commission_policy_tiers (tier_id, company_id, policy_id, from_amount, rate_pct, created_by)
                    VALUES (@id, @c, @policy, @from, @rate, @user)
                    """);
                Add(insertTier, "@id", FormatGuid(tier.Id));
                Add(insertTier, "@c", company);
                Add(insertTier, "@policy", FormatGuid(policy.Id));
                Add(insertTier, "@from", tier.FromAmount);
                Add(insertTier, "@rate", tier.RatePct);
                Add(insertTier, "@user", userId);
                await insertTier.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task SoftDeletePolicyAsync(string companyId, Guid policyId, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null, $"""
            UPDATE sco_commission_policies SET is_deleted = @t, updated_by = @user, updated_at = {NowExpr}
            WHERE company_id = @c AND policy_id = @id AND {NotDeleted}
            """);
        Add(command, "@t", Truth(true));
        Add(command, "@user", userId);
        Add(command, "@c", companyId.Trim());
        Add(command, "@id", FormatGuid(policyId));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    // Entries: read + status transitions
    // ------------------------------------------------------------------
    public async Task<IReadOnlyList<CommEntry>> GetEntriesAsync(
        string companyId, string? repCode = null, string? status = null,
        string? periodFrom = null, string? periodTo = null, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null, $"""
            SELECT entry_id, policy_id, rep_code, source_doc_kind, source_doc_id, source_doc_no, source_ref,
                   party_id, party_name, basis, basis_amount, rate_pct, commission_amount, status,
                   period_date, settlement_id, notes
            FROM sco_commission_entries
            WHERE company_id = @c AND {NotDeleted}
              AND (@rep IS NULL OR rep_code = @rep)
              AND (@status IS NULL OR status = @status)
              AND (@from IS NULL OR period_date >= @from)
              AND (@to IS NULL OR period_date <= @to)
            ORDER BY period_date, created_at
            """);
        Add(command, "@c", companyId.Trim());
        Add(command, "@rep", string.IsNullOrWhiteSpace(repCode) ? DBNull.Value : repCode.Trim());
        Add(command, "@status", string.IsNullOrWhiteSpace(status) ? DBNull.Value : status.Trim());
        Add(command, "@from", string.IsNullOrWhiteSpace(periodFrom) ? DBNull.Value : periodFrom.Trim());
        Add(command, "@to", string.IsNullOrWhiteSpace(periodTo) ? DBNull.Value : periodTo.Trim());
        var result = new List<CommEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(ReadEntry(reader));
        return result;
    }

    public async Task<CommEntry?> GetEntryAsync(string companyId, Guid entryId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null, $"""
            SELECT entry_id, policy_id, rep_code, source_doc_kind, source_doc_id, source_doc_no, source_ref,
                   party_id, party_name, basis, basis_amount, rate_pct, commission_amount, status,
                   period_date, settlement_id, notes
            FROM sco_commission_entries
            WHERE company_id = @c AND entry_id = @id AND {NotDeleted}
            """);
        Add(command, "@c", companyId.Trim());
        Add(command, "@id", FormatGuid(entryId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadEntry(reader) : null;
    }

    /// <summary>Moves one entry between two statuses, refusing (0 rows)
    /// when the entry is not currently in <paramref name="fromStatus"/> —
    /// the service turns a 0 into SAL-7014.</summary>
    public async Task<int> SetEntryStatusAsync(string companyId, Guid entryId, string fromStatus, string toStatus, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null, $"""
            UPDATE sco_commission_entries SET status = @to, updated_by = @user, updated_at = {NowExpr}
            WHERE company_id = @c AND entry_id = @id AND status = @from AND {NotDeleted}
            """);
        Add(command, "@to", toStatus);
        Add(command, "@user", userId);
        Add(command, "@c", companyId.Trim());
        Add(command, "@id", FormatGuid(entryId));
        Add(command, "@from", fromStatus);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    // Settlements
    // ------------------------------------------------------------------
    public async Task<IReadOnlyList<CommSettlement>> GetSettlementsAsync(string companyId, string? repCode = null, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null, $"""
            SELECT s.settlement_id, s.rep_code, s.period_from, s.period_to, s.total_amount, s.status, s.journal_ref,
                   (SELECT COUNT(*) FROM sco_commission_entries e WHERE e.settlement_id = s.settlement_id AND e.{NotDeleted})
            FROM sco_commission_settlements s
            WHERE s.company_id = @c AND s.{NotDeleted}
              AND (@rep IS NULL OR s.rep_code = @rep)
            ORDER BY s.period_from, s.rep_code
            """);
        Add(command, "@c", companyId.Trim());
        Add(command, "@rep", string.IsNullOrWhiteSpace(repCode) ? DBNull.Value : repCode.Trim());
        var result = new List<CommSettlement>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new CommSettlement
            {
                Id = ReadGuid(reader, 0),
                RepCode = ReadString(reader, 1) ?? string.Empty,
                PeriodFrom = ReadString(reader, 2) ?? string.Empty,
                PeriodTo = ReadString(reader, 3) ?? string.Empty,
                TotalAmount = ReadDecimalOrNull(reader, 4) ?? 0m,
                Status = ReadString(reader, 5) ?? CommSettlementStatuses.Draft,
                JournalRef = ReadString(reader, 6),
                EntryCount = Convert.ToInt32(reader.GetValue(7), CultureInfo.InvariantCulture)
            });
        }
        return result;
    }

    public async Task<CommSettlement?> GetSettlementAsync(string companyId, Guid settlementId, CancellationToken cancellationToken = default)
    {
        var all = await GetSettlementsAsync(companyId, null, cancellationToken).ConfigureAwait(false);
        foreach (var settlement in all)
            if (settlement.Id == settlementId) return settlement;
        return null;
    }

    /// <summary>Creates a Draft settlement capturing every Approved,
    /// not-yet-settled entry of the rep inside the period, in ONE
    /// transaction: the total and the entries' settlement link can never
    /// disagree. No capturable entries is SAL-7014.</summary>
    public async Task<CommSettlement> CreateSettlementAsync(string companyId, string repCode, string periodFrom, string periodTo, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        var company = companyId.Trim();
        var settlement = new CommSettlement
        {
            RepCode = repCode.Trim(),
            PeriodFrom = periodFrom.Trim(),
            PeriodTo = periodTo.Trim(),
            Status = CommSettlementStatuses.Draft
        };
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            decimal total;
            await using (var sum = CreateCommand(connection, transaction, $"""
                SELECT COALESCE(SUM(commission_amount), 0), COUNT(*) FROM sco_commission_entries
                WHERE company_id = @c AND rep_code = @rep AND status = 'Approved' AND settlement_id IS NULL
                  AND period_date >= @from AND period_date <= @to AND {NotDeleted}
                """))
            {
                Add(sum, "@c", company);
                Add(sum, "@rep", settlement.RepCode);
                Add(sum, "@from", settlement.PeriodFrom);
                Add(sum, "@to", settlement.PeriodTo);
                await using var reader = await sum.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                total = ReadDecimalOrNull(reader, 0) ?? 0m;
                settlement.EntryCount = Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);
            }
            if (settlement.EntryCount == 0)
                throw new AppError(ErrorCodes.CommissionSettlementInvalid,
                    $"No approved, unsettled commission entries exist for rep '{settlement.RepCode}' in {settlement.PeriodFrom}..{settlement.PeriodTo}.",
                    settlement.RepCode);
            settlement.TotalAmount = total;

            await using (var insert = CreateCommand(connection, transaction, """
                INSERT INTO sco_commission_settlements
                    (settlement_id, company_id, rep_code, period_from, period_to, total_amount, status, created_by)
                VALUES (@id, @c, @rep, @from, @to, @total, 'Draft', @user)
                """))
            {
                Add(insert, "@id", FormatGuid(settlement.Id));
                Add(insert, "@c", company);
                Add(insert, "@rep", settlement.RepCode);
                Add(insert, "@from", settlement.PeriodFrom);
                Add(insert, "@to", settlement.PeriodTo);
                Add(insert, "@total", settlement.TotalAmount);
                Add(insert, "@user", userId);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await using (var link = CreateCommand(connection, transaction, $"""
                UPDATE sco_commission_entries SET settlement_id = @sid, updated_by = @user, updated_at = {NowExpr}
                WHERE company_id = @c AND rep_code = @rep AND status = 'Approved' AND settlement_id IS NULL
                  AND period_date >= @from AND period_date <= @to AND {NotDeleted}
                """))
            {
                Add(link, "@sid", FormatGuid(settlement.Id));
                Add(link, "@user", userId);
                Add(link, "@c", company);
                Add(link, "@rep", settlement.RepCode);
                Add(link, "@from", settlement.PeriodFrom);
                Add(link, "@to", settlement.PeriodTo);
                await link.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return settlement;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Settlement state transition guarded on the current
    /// status; 0 rows when the transition is not available.</summary>
    public async Task<int> SetSettlementStatusAsync(string companyId, Guid settlementId, string fromStatus, string toStatus, string? journalRef, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int rows;
            await using (var update = CreateCommand(connection, transaction, $"""
                UPDATE sco_commission_settlements SET status = @to, journal_ref = COALESCE(@ref, journal_ref),
                    updated_by = @user, updated_at = {NowExpr}
                WHERE company_id = @c AND settlement_id = @id AND status = @from AND {NotDeleted}
                """))
            {
                Add(update, "@to", toStatus);
                Add(update, "@ref", string.IsNullOrWhiteSpace(journalRef) ? DBNull.Value : journalRef.Trim());
                Add(update, "@user", userId);
                Add(update, "@c", companyId.Trim());
                Add(update, "@id", FormatGuid(settlementId));
                Add(update, "@from", fromStatus);
                rows = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            if (rows > 0 && toStatus == CommSettlementStatuses.Paid)
            {
                await using var settle = CreateCommand(connection, transaction, $"""
                    UPDATE sco_commission_entries SET status = 'Settled', updated_by = @user, updated_at = {NowExpr}
                    WHERE company_id = @c AND settlement_id = @id AND status = 'Approved' AND {NotDeleted}
                    """);
                Add(settle, "@user", userId);
                Add(settle, "@c", companyId.Trim());
                Add(settle, "@id", FormatGuid(settlementId));
                await settle.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return rows;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Resolves the payment accounts for a settlement: company
    /// Guid + currency from the companies registry; the expense account
    /// is the first postable chart account named for commissions; the
    /// credit account is the canonical Cash code. Null when either leg
    /// cannot be resolved (the service raises SAL-7014).</summary>
    public async Task<CommPaymentAccounts?> ResolvePaymentAccountsAsync(string companyId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        Guid companyGuid;
        string currency = "SAR";
        await using (var company = CreateCommand(connection, null,
            "SELECT CAST(company_id AS TEXT), currency_code FROM companies WHERE company_code = @c"))
        {
            Add(company, "@c", companyId.Trim());
            await using var reader = await company.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return null;
            if (!Guid.TryParse(ReadString(reader, 0), out companyGuid))
                return null;
            currency = ReadString(reader, 1) ?? "SAR";
        }

        var expense = await ResolveAccountByNameAsync(connection, companyGuid, "commission", cancellationToken).ConfigureAwait(false);
        var cash = await ResolveAccountByCodeAsync(connection, companyGuid, GlAccounts.Cash, cancellationToken).ConfigureAwait(false);
        if (expense is null || cash is null)
            return null;
        return new CommPaymentAccounts(companyGuid, expense.Value.Id, expense.Value.Code, cash.Value.Id, cash.Value.Code, currency);
    }

    private async Task<(Guid Id, string Code)?> ResolveAccountByCodeAsync(DbConnection connection, Guid companyGuid, string code, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, null, $"""
            SELECT CAST(account_id AS TEXT), account_code FROM chart_of_accounts
            WHERE company_id = @company AND account_code = @code
              AND is_active = @t AND is_postable = @t AND {NotDeleted}
            ORDER BY account_code LIMIT 1
            """);
        Add(command, "@company", FormatGuid(companyGuid));
        Add(command, "@code", code);
        Add(command, "@t", Truth(true));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return Guid.TryParse(ReadString(reader, 0), out var id) ? (id, ReadString(reader, 1) ?? code) : null;
    }

    private async Task<(Guid Id, string Code)?> ResolveAccountByNameAsync(DbConnection connection, Guid companyGuid, string nameFragment, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, null, $"""
            SELECT CAST(account_id AS TEXT), account_code FROM chart_of_accounts
            WHERE company_id = @company AND LOWER(account_name) LIKE @fragment
              AND is_active = @t AND is_postable = @t AND {NotDeleted}
            ORDER BY account_code LIMIT 1
            """);
        Add(command, "@company", FormatGuid(companyGuid));
        Add(command, "@fragment", "%" + nameFragment + "%");
        Add(command, "@t", Truth(true));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return Guid.TryParse(ReadString(reader, 0), out var id) ? (id, ReadString(reader, 1) ?? string.Empty) : null;
    }

    /// <summary>Active reps of the company (sales_reps, 031) for the
    /// window's rep pickers and filters.</summary>
    public async Task<IReadOnlyList<(string Code, string Name)>> GetRepOptionsAsync(string companyId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null, $"""
            SELECT code, name_en FROM sales_reps
            WHERE company_id = @c AND is_active = @t AND {NotDeleted} ORDER BY code
            """);
        Add(command, "@c", companyId.Trim());
        Add(command, "@t", Truth(true));
        var result = new List<(string, string)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add((ReadString(reader, 0) ?? string.Empty, ReadString(reader, 1) ?? string.Empty));
        return result;
    }

    // ------------------------------------------------------------------
    // Generation: invoice (Sales / Profit), return adjustments, period
    // collection run. Every generator runs in ONE transaction and is
    // idempotent; "no rep / no policy" is a normal outcome (0 created),
    // never an error — the posting hooks stay fail-soft by design.
    // ------------------------------------------------------------------

    /// <summary>Generates Sales- and Profit-basis entries for one POSTED
    /// sales invoice (either posting path — the document row is the
    /// seam). Per line, the most specific matching policy is resolved;
    /// lines sharing a policy aggregate into one entry. Sales basis =
    /// line totals against the stored grand total as slab base; Profit
    /// basis = (line subtotal) − the line material's share of the
    /// posting's movement COGS, against the document gross profit as
    /// slab base. Returns the number of entries created.</summary>
    public async Task<int> GenerateForInvoiceAsync(string companyKey, long docId, string userId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(companyKey)) throw new ArgumentException("Company is required.", nameof(companyKey));
        if (docId <= 0) throw new ArgumentOutOfRangeException(nameof(docId));
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var doc = await ReadSalesDocAsync(connection, transaction, docId, cancellationToken).ConfigureAwait(false);
            if (doc is null || doc.DocKind != "SAL" || doc.Status != "Posted")
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return 0;
            }
            var company = await PartyLedger.ResolveCompanyCodeAsync(connection, transaction, _provider, doc.CompanyKey, cancellationToken).ConfigureAwait(false);
            var repCode = await ResolveRepCodeAsync(connection, transaction, company, doc.RepName, cancellationToken).ConfigureAwait(false);
            if (repCode is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return 0;
            }
            var lines = await ReadDocLinesAsync(connection, transaction, docId, cancellationToken).ConfigureAwait(false);
            var policies = await ReadPoliciesAsync(connection, transaction, company, null, cancellationToken).ConfigureAwait(false);
            await LoadTiersAsync(connection, transaction, company, policies, cancellationToken).ConfigureAwait(false);
            if (policies.Count == 0)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return 0;
            }

            var created = 0;
            var periodDate = DatePart(doc.DocDate);

            // ---- Sales basis ------------------------------------------
            var salesByPolicy = new Dictionary<Guid, (CommPolicy Policy, decimal Basis)>();
            foreach (var line in lines)
            {
                var policy = CommPolicyResolver.Resolve(policies, CommBases.Sales, repCode,
                    line.MaterialId, line.GroupCode, doc.PartyId, null, periodDate);
                if (policy is null) continue;
                salesByPolicy[policy.Id] = salesByPolicy.TryGetValue(policy.Id, out var acc)
                    ? (policy, acc.Basis + line.LineTotal)
                    : (policy, line.LineTotal);
            }
            foreach (var (policy, basis) in salesByPolicy.Values)
            {
                var rate = CommPolicyResolver.ResolveRate(policy, doc.GrandTotal);
                created += await InsertEntryIfAbsentAsync(connection, transaction, company, new CommEntry
                {
                    PolicyId = policy.Id,
                    RepCode = repCode,
                    SourceDocKind = CommSourceKinds.SalesInvoice,
                    SourceDocId = docId,
                    SourceDocNo = doc.DocNo,
                    PartyId = doc.PartyId,
                    PartyName = doc.PartyName,
                    Basis = CommBases.Sales,
                    BasisAmount = basis,
                    RatePct = rate,
                    CommissionAmount = Math.Round(basis * rate / 100m, 2),
                    Status = CommEntryStatuses.Pending,
                    PeriodDate = periodDate
                }, userId, cancellationToken).ConfigureAwait(false);
            }

            // ---- Profit basis ------------------------------------------
            if (policies.Any(p => string.Equals(p.Basis, CommBases.Profit, StringComparison.OrdinalIgnoreCase)))
            {
                var cogsByMaterial = await ReadCogsByMaterialAsync(connection, transaction, company, doc, docId, cancellationToken).ConfigureAwait(false);
                var totalRevenue = lines.Sum(l => l.LineSubtotal);
                // Gross profit per material = revenue − COGS; a material
                // appearing on several lines shares its movement COGS
                // across those lines in proportion to line revenue.
                var profitByPolicy = new Dictionary<Guid, (CommPolicy Policy, decimal Basis)>();
                foreach (var line in lines)
                {
                    var policy = CommPolicyResolver.Resolve(policies, CommBases.Profit, repCode,
                        line.MaterialId, line.GroupCode, doc.PartyId, null, periodDate);
                    if (policy is null) continue;
                    var lineCogs = 0m;
                    if (line.MaterialId is long materialId && cogsByMaterial.TryGetValue(materialId, out var materialCogs) && materialCogs > 0m)
                    {
                        var materialRevenue = lines.Where(l => l.MaterialId == materialId).Sum(l => l.LineSubtotal);
                        lineCogs = materialRevenue > 0m
                            ? Math.Round(materialCogs * line.LineSubtotal / materialRevenue, 2)
                            : materialCogs;
                    }
                    var profit = line.LineSubtotal - lineCogs;
                    profitByPolicy[policy.Id] = profitByPolicy.TryGetValue(policy.Id, out var acc)
                        ? (policy, acc.Basis + profit)
                        : (policy, profit);
                }
                var documentProfit = totalRevenue - cogsByMaterial.Values.Sum();
                foreach (var (policy, basis) in profitByPolicy.Values)
                {
                    var rate = CommPolicyResolver.ResolveRate(policy, documentProfit);
                    created += await InsertEntryIfAbsentAsync(connection, transaction, company, new CommEntry
                    {
                        PolicyId = policy.Id,
                        RepCode = repCode,
                        SourceDocKind = CommSourceKinds.SalesInvoice,
                        SourceDocId = docId,
                        SourceDocNo = doc.DocNo,
                        PartyId = doc.PartyId,
                        PartyName = doc.PartyName,
                        Basis = CommBases.Profit,
                        BasisAmount = basis,
                        RatePct = rate,
                        CommissionAmount = Math.Round(basis * rate / 100m, 2),
                        Status = CommEntryStatuses.Pending,
                        PeriodDate = periodDate
                    }, userId, cancellationToken).ConfigureAwait(false);
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return created;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Generates NEGATIVE adjustment entries for a POSTED sales
    /// return (SRT): for every non-Rejected commission entry on the
    /// original invoice, an adjustment of −(entry amount × returned
    /// share), where the returned share is the return's grand total over
    /// the original invoice's grand total. Adjustments keep the original
    /// policy and rate (no re-resolution: the return claws back what the
    /// invoice earned, at the rate it earned it).</summary>
    public async Task<int> GenerateForReturnAsync(string companyKey, long returnDocId, string userId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(companyKey)) throw new ArgumentException("Company is required.", nameof(companyKey));
        if (returnDocId <= 0) throw new ArgumentOutOfRangeException(nameof(returnDocId));
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var ret = await ReadSalesDocAsync(connection, transaction, returnDocId, cancellationToken).ConfigureAwait(false);
            if (ret is null || ret.DocKind != "SRT" || ret.Status != "Posted" || ret.OriginalDocId is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return 0;
            }
            var original = await ReadSalesDocAsync(connection, transaction, ret.OriginalDocId.Value, cancellationToken).ConfigureAwait(false);
            if (original is null || original.GrandTotal <= 0m)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return 0;
            }
            var company = await PartyLedger.ResolveCompanyCodeAsync(connection, transaction, _provider, ret.CompanyKey, cancellationToken).ConfigureAwait(false);
            var share = Math.Abs(ret.GrandTotal) / original.GrandTotal;
            if (share > 1m) share = 1m;

            var originals = new List<CommEntry>();
            await using (var command = CreateCommand(connection, transaction, $"""
                SELECT entry_id, policy_id, rep_code, source_doc_kind, source_doc_id, source_doc_no, source_ref,
                       party_id, party_name, basis, basis_amount, rate_pct, commission_amount, status,
                       period_date, settlement_id, notes
                FROM sco_commission_entries
                WHERE company_id = @c AND source_doc_kind = 'SAL' AND source_doc_id = @doc
                  AND status <> 'Rejected' AND {NotDeleted}
                """))
            {
                Add(command, "@c", company);
                Add(command, "@doc", ret.OriginalDocId.Value);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    originals.Add(ReadEntry(reader));
            }

            var created = 0;
            foreach (var entry in originals)
            {
                created += await InsertEntryIfAbsentAsync(connection, transaction, company, new CommEntry
                {
                    PolicyId = entry.PolicyId,
                    RepCode = entry.RepCode,
                    SourceDocKind = CommSourceKinds.SalesReturn,
                    SourceDocId = returnDocId,
                    SourceDocNo = ret.DocNo,
                    SourceRef = entry.Id.ToString("D"),
                    PartyId = entry.PartyId,
                    PartyName = entry.PartyName,
                    Basis = entry.Basis,
                    BasisAmount = -Math.Round(entry.BasisAmount * share, 2),
                    RatePct = entry.RatePct,
                    CommissionAmount = -Math.Round(entry.CommissionAmount * share, 2),
                    Status = CommEntryStatuses.Pending,
                    PeriodDate = DatePart(ret.DocDate),
                    Notes = $"Return adjustment for {original.DocNo}"
                }, userId, cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return created;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Collection-basis PERIOD RUN (the deterministic seam this
    /// phase chose over a PayRecWin allocation hook): for every customer
    /// payment allocation dated inside the period whose invoice carries
    /// a resolvable rep, generate a Collection entry of the allocated
    /// amount under the most specific Collection policy (rep / party /
    /// company dimensions — an allocation has no material dimension).
    /// Re-running the period is safe: allocations already commissioned
    /// are skipped by the source_ref idempotency check.</summary>
    public async Task<int> GenerateCollectionForPeriodAsync(string companyId, string periodFrom, string periodTo, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        var company = companyId.Trim();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var policies = await ReadPoliciesAsync(connection, transaction, company, CommBases.Collection, cancellationToken).ConfigureAwait(false);
            await LoadTiersAsync(connection, transaction, company, policies, cancellationToken).ConfigureAwait(false);
            if (policies.Count == 0)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return 0;
            }

            var allocations = new List<(string AllocationId, long DocId, string? DocNo, decimal Amount, string AllocatedAt, string DocCompanyKey, string? RepName, long? PartyId, string? PartyName)>();
            await using (var command = CreateCommand(connection, transaction, """
                SELECT CAST(a.allocation_id AS TEXT), a.doc_id, a.doc_no, a.amount, CAST(a.allocated_at AS TEXT),
                       d.company_id, d.sales_rep_name, d.party_id, d.party_name
                FROM payment_allocations a
                JOIN sales_documents d ON d.doc_id = a.doc_id
                WHERE a.doc_table = 'SALES' AND a.party_kind = 'C'
                  AND substr(CAST(a.allocated_at AS TEXT), 1, 10) >= @from
                  AND substr(CAST(a.allocated_at AS TEXT), 1, 10) <= @to
                """))
            {
                Add(command, "@from", periodFrom.Trim());
                Add(command, "@to", periodTo.Trim());
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    allocations.Add((
                        ReadString(reader, 0) ?? string.Empty,
                        Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                        ReadString(reader, 2),
                        ReadDecimalOrNull(reader, 3) ?? 0m,
                        ReadString(reader, 4) ?? string.Empty,
                        ReadString(reader, 5) ?? string.Empty,
                        ReadString(reader, 6),
                        reader.IsDBNull(7) ? null : Convert.ToInt64(reader.GetValue(7), CultureInfo.InvariantCulture),
                        ReadString(reader, 8)));
                }
            }

            var created = 0;
            var repCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var allocation in allocations)
            {
                var docCompany = await PartyLedger.ResolveCompanyCodeAsync(connection, transaction, _provider, allocation.DocCompanyKey, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(docCompany, company, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrWhiteSpace(allocation.RepName)) continue;
                if (!repCache.TryGetValue(allocation.RepName, out var repCode))
                {
                    repCode = await ResolveRepCodeAsync(connection, transaction, company, allocation.RepName, cancellationToken).ConfigureAwait(false);
                    repCache[allocation.RepName] = repCode;
                }
                if (repCode is null) continue;
                var periodDate = DatePart(allocation.AllocatedAt);
                var policy = CommPolicyResolver.Resolve(policies, CommBases.Collection, repCode,
                    null, null, allocation.PartyId, null, periodDate);
                if (policy is null) continue;
                var rate = CommPolicyResolver.ResolveRate(policy, allocation.Amount);
                created += await InsertEntryIfAbsentAsync(connection, transaction, company, new CommEntry
                {
                    PolicyId = policy.Id,
                    RepCode = repCode,
                    SourceDocKind = CommSourceKinds.Allocation,
                    SourceDocId = allocation.DocId,
                    SourceDocNo = allocation.DocNo,
                    SourceRef = allocation.AllocationId,
                    PartyId = allocation.PartyId,
                    PartyName = allocation.PartyName,
                    Basis = CommBases.Collection,
                    BasisAmount = allocation.Amount,
                    RatePct = rate,
                    CommissionAmount = Math.Round(allocation.Amount * rate / 100m, 2),
                    Status = CommEntryStatuses.Pending,
                    PeriodDate = periodDate
                }, userId, cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return created;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    // ------------------------------------------------------------------
    // Generation internals
    // ------------------------------------------------------------------
    private sealed record DocHead(string CompanyKey, string DocKind, string DocNo, string DocDate,
        long? PartyId, string? PartyName, string? RepName, decimal GrandTotal, string Status, long? OriginalDocId);

    private sealed record DocLine(long? MaterialId, string? GroupCode, decimal LineSubtotal, decimal LineTotal);

    private async Task<DocHead?> ReadSalesDocAsync(DbConnection connection, DbTransaction transaction, long docId, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, $"""
            SELECT company_id, doc_kind, doc_no, CAST(doc_date AS TEXT), party_id, party_name,
                   sales_rep_name, grand_total, status, original_doc_id
            FROM sales_documents
            WHERE doc_id = @id AND {NotDeleted}
            """);
        Add(command, "@id", docId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return new DocHead(
            ReadString(reader, 0) ?? string.Empty,
            ReadString(reader, 1) ?? string.Empty,
            ReadString(reader, 2) ?? string.Empty,
            ReadString(reader, 3) ?? string.Empty,
            reader.IsDBNull(4) ? null : Convert.ToInt64(reader.GetValue(4), CultureInfo.InvariantCulture),
            ReadString(reader, 5),
            ReadString(reader, 6),
            ReadDecimalOrNull(reader, 7) ?? 0m,
            ReadString(reader, 8) ?? string.Empty,
            reader.IsDBNull(9) ? null : Convert.ToInt64(reader.GetValue(9), CultureInfo.InvariantCulture));
    }

    private async Task<List<DocLine>> ReadDocLinesAsync(DbConnection connection, DbTransaction transaction, long docId, CancellationToken cancellationToken)
    {
        var rows = new List<(long? MaterialId, decimal Subtotal, decimal Total)>();
        await using (var command = CreateCommand(connection, transaction, """
            SELECT material_id, line_subtotal, line_total FROM sales_document_lines WHERE doc_id = @id ORDER BY line_no
            """))
        {
            Add(command, "@id", docId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add((
                    reader.IsDBNull(0) ? null : Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                    ReadDecimalOrNull(reader, 1) ?? 0m,
                    ReadDecimalOrNull(reader, 2) ?? 0m));
            }
        }
        var result = new List<DocLine>();
        foreach (var row in rows)
        {
            string? group = null;
            if (row.MaterialId is long materialId)
            {
                await using var command = CreateCommand(connection, transaction, """
                    SELECT group_code FROM materials WHERE material_id = @m LIMIT 1
                    """);
                Add(command, "@m", materialId);
                var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                group = value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
            }
            result.Add(new DocLine(row.MaterialId, group, row.Subtotal, row.Total));
        }
        return result;
    }

    /// <summary>COGS the posting carried, per material: the valued stock
    /// movements written under the invoice's own document number, plus —
    /// for a delivery-linked chain invoice, whose COGS is released from
    /// the DELIVERY's movements — the movements of every delivery linked
    /// to this invoice in document_links. cost_amount is negative on
    /// issues, so COGS is the negated sum.</summary>
    private async Task<Dictionary<long, decimal>> ReadCogsByMaterialAsync(DbConnection connection, DbTransaction transaction, string companyCode, DocHead doc, long docId, CancellationToken cancellationToken)
    {
        var docNos = new List<string> { doc.DocNo };
        await using (var links = CreateCommand(connection, transaction, """
            SELECT d.doc_no FROM document_links l
            JOIN sales_documents d ON d.doc_id = l.source_doc_id
            WHERE l.target_doc_id = @id AND l.source_doc_kind = 'DLV'
            """))
        {
            Add(links, "@id", docId);
            await using var reader = await links.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var no = ReadString(reader, 0);
                if (!string.IsNullOrWhiteSpace(no)) docNos.Add(no);
            }
        }

        var result = new Dictionary<long, decimal>();
        foreach (var docNo in docNos.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await using var command = CreateCommand(connection, transaction, """
                SELECT material_id, SUM(-cost_amount) FROM material_stock_movements
                WHERE company_id = @c AND document_no = @no AND qty_out > 0 AND cost_amount IS NOT NULL
                GROUP BY material_id
                """);
            Add(command, "@c", companyCode);
            Add(command, "@no", docNo);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var materialId = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
                var cost = Math.Max(0m, ReadDecimalOrNull(reader, 1) ?? 0m);
                result[materialId] = result.TryGetValue(materialId, out var acc) ? acc + cost : cost;
            }
        }
        return result;
    }

    private async Task<string?> ResolveRepCodeAsync(DbConnection connection, DbTransaction transaction, string companyCode, string? repName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(repName)) return null;
        await using var command = CreateCommand(connection, transaction, $"""
            SELECT code FROM sales_reps
            WHERE company_id = @c AND code = @name AND is_active = @t AND {NotDeleted} LIMIT 1
            """);
        Add(command, "@c", companyCode);
        Add(command, "@name", repName.Trim());
        Add(command, "@t", Truth(true));
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private async Task<int> InsertEntryIfAbsentAsync(DbConnection connection, DbTransaction transaction, string company, CommEntry entry, string userId, CancellationToken cancellationToken)
    {
        await using (var check = CreateCommand(connection, transaction, $"""
            SELECT COUNT(*) FROM sco_commission_entries
            WHERE company_id = @c AND policy_id = @p AND source_doc_kind = @kind AND source_doc_id = @doc
              AND COALESCE(source_ref, '') = @ref AND {NotDeleted}
            """))
        {
            Add(check, "@c", company);
            Add(check, "@p", FormatGuid(entry.PolicyId));
            Add(check, "@kind", entry.SourceDocKind);
            Add(check, "@doc", entry.SourceDocId);
            Add(check, "@ref", entry.SourceRef ?? string.Empty);
            var count = Convert.ToInt64(await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            if (count > 0) return 0;
        }
        await using var insert = CreateCommand(connection, transaction, """
            INSERT INTO sco_commission_entries
                (entry_id, company_id, policy_id, rep_code, source_doc_kind, source_doc_id, source_doc_no, source_ref,
                 party_id, party_name, basis, basis_amount, rate_pct, commission_amount, status, period_date, notes, created_by)
            VALUES (@id, @c, @p, @rep, @kind, @doc, @docNo, @ref,
                    @party, @partyName, @basis, @basisAmount, @rate, @amount, @status, @period, @notes, @user)
            """);
        Add(insert, "@id", FormatGuid(entry.Id));
        Add(insert, "@c", company);
        Add(insert, "@p", FormatGuid(entry.PolicyId));
        Add(insert, "@rep", entry.RepCode);
        Add(insert, "@kind", entry.SourceDocKind);
        Add(insert, "@doc", entry.SourceDocId);
        Add(insert, "@docNo", entry.SourceDocNo is null ? DBNull.Value : entry.SourceDocNo);
        Add(insert, "@ref", entry.SourceRef is null ? DBNull.Value : entry.SourceRef);
        Add(insert, "@party", entry.PartyId is null ? DBNull.Value : entry.PartyId.Value);
        Add(insert, "@partyName", entry.PartyName is null ? DBNull.Value : entry.PartyName);
        Add(insert, "@basis", entry.Basis);
        Add(insert, "@basisAmount", entry.BasisAmount);
        Add(insert, "@rate", entry.RatePct);
        Add(insert, "@amount", entry.CommissionAmount);
        Add(insert, "@status", entry.Status);
        Add(insert, "@period", entry.PeriodDate);
        Add(insert, "@notes", entry.Notes is null ? DBNull.Value : entry.Notes);
        Add(insert, "@user", userId);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return 1;
    }

    // ------------------------------------------------------------------
    // Policy reading (shared by CRUD + generation)
    // ------------------------------------------------------------------
    private async Task<List<CommPolicy>> ReadPoliciesAsync(DbConnection connection, DbTransaction? transaction, string company, string? basis, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, $"""
            SELECT policy_id, policy_name, basis, rate_pct, rep_scope, rep_code, product_scope, material_id,
                   material_group_code, customer_scope, party_id, area_code, valid_from, valid_to, is_active
            FROM sco_commission_policies
            WHERE company_id = @c AND {NotDeleted} AND (@basis IS NULL OR basis = @basis)
            ORDER BY created_at, policy_name
            """);
        Add(command, "@c", company);
        Add(command, "@basis", basis is null ? DBNull.Value : basis);
        var result = new List<CommPolicy>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new CommPolicy
            {
                Id = ReadGuid(reader, 0),
                Name = ReadString(reader, 1) ?? string.Empty,
                Basis = ReadString(reader, 2) ?? CommBases.Sales,
                RatePct = ReadDecimalOrNull(reader, 3) ?? 0m,
                RepScope = ReadString(reader, 4) ?? CommRepScopes.All,
                RepCode = ReadString(reader, 5),
                ProductScope = ReadString(reader, 6) ?? CommProductScopes.All,
                MaterialId = reader.IsDBNull(7) ? null : Convert.ToInt64(reader.GetValue(7), CultureInfo.InvariantCulture),
                MaterialGroupCode = ReadString(reader, 8),
                CustomerScope = ReadString(reader, 9) ?? CommCustomerScopes.All,
                PartyId = reader.IsDBNull(10) ? null : Convert.ToInt64(reader.GetValue(10), CultureInfo.InvariantCulture),
                AreaCode = ReadString(reader, 11),
                ValidFrom = ReadString(reader, 12),
                ValidTo = ReadString(reader, 13),
                IsActive = Truthy(reader.GetValue(14))
            });
        }
        return result;
    }

    private async Task LoadTiersAsync(DbConnection connection, DbTransaction? transaction, string company, List<CommPolicy> policies, CancellationToken cancellationToken)
    {
        if (policies.Count == 0) return;
        var byId = new Dictionary<Guid, CommPolicy>();
        foreach (var policy in policies) byId[policy.Id] = policy;
        await using var command = CreateCommand(connection, transaction, $"""
            SELECT tier_id, policy_id, from_amount, rate_pct FROM sco_commission_policy_tiers
            WHERE company_id = @c AND {NotDeleted} ORDER BY from_amount
            """);
        Add(command, "@c", company);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var policyId = ReadGuid(reader, 1);
            if (!byId.TryGetValue(policyId, out var policy)) continue;
            policy.Tiers.Add(new CommPolicyTier
            {
                Id = ReadGuid(reader, 0),
                PolicyId = policyId,
                FromAmount = ReadDecimalOrNull(reader, 2) ?? 0m,
                RatePct = ReadDecimalOrNull(reader, 3) ?? 0m
            });
        }
    }

    private void BindPolicy(DbCommand command, string company, CommPolicy policy, string userId)
    {
        Add(command, "@id", FormatGuid(policy.Id));
        Add(command, "@c", company);
        Add(command, "@name", policy.Name.Trim());
        Add(command, "@basis", policy.Basis);
        Add(command, "@rate", policy.RatePct);
        Add(command, "@repScope", policy.RepScope);
        Add(command, "@rep", policy.RepScope == CommRepScopes.SpecificRep && !string.IsNullOrWhiteSpace(policy.RepCode) ? policy.RepCode.Trim() : DBNull.Value);
        Add(command, "@prodScope", policy.ProductScope);
        Add(command, "@material", policy.ProductScope == CommProductScopes.Material && policy.MaterialId is not null ? policy.MaterialId.Value : DBNull.Value);
        Add(command, "@group", policy.ProductScope == CommProductScopes.Group && !string.IsNullOrWhiteSpace(policy.MaterialGroupCode) ? policy.MaterialGroupCode.Trim() : DBNull.Value);
        Add(command, "@custScope", policy.CustomerScope);
        Add(command, "@party", policy.CustomerScope == CommCustomerScopes.SpecificParty && policy.PartyId is not null ? policy.PartyId.Value : DBNull.Value);
        Add(command, "@area", string.IsNullOrWhiteSpace(policy.AreaCode) ? DBNull.Value : policy.AreaCode.Trim());
        Add(command, "@from", string.IsNullOrWhiteSpace(policy.ValidFrom) ? DBNull.Value : policy.ValidFrom.Trim());
        Add(command, "@to", string.IsNullOrWhiteSpace(policy.ValidTo) ? DBNull.Value : policy.ValidTo.Trim());
        Add(command, "@active", Truth(policy.IsActive));
        Add(command, "@user", userId);
    }

    private static CommEntry ReadEntry(DbDataReader reader) => new()
    {
        Id = ReadGuid(reader, 0),
        PolicyId = ReadGuid(reader, 1),
        RepCode = ReadString(reader, 2) ?? string.Empty,
        SourceDocKind = ReadString(reader, 3) ?? string.Empty,
        SourceDocId = Convert.ToInt64(reader.GetValue(4), CultureInfo.InvariantCulture),
        SourceDocNo = ReadString(reader, 5),
        SourceRef = ReadString(reader, 6),
        PartyId = reader.IsDBNull(7) ? null : Convert.ToInt64(reader.GetValue(7), CultureInfo.InvariantCulture),
        PartyName = ReadString(reader, 8),
        Basis = ReadString(reader, 9) ?? CommBases.Sales,
        BasisAmount = ReadDecimalOrNull(reader, 10) ?? 0m,
        RatePct = ReadDecimalOrNull(reader, 11) ?? 0m,
        CommissionAmount = ReadDecimalOrNull(reader, 12) ?? 0m,
        Status = ReadString(reader, 13) ?? CommEntryStatuses.Pending,
        PeriodDate = ReadString(reader, 14) ?? string.Empty,
        SettlementId = ReadGuidOrNull(reader, 15),
        Notes = ReadString(reader, 16)
    };

    private static string DatePart(string value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : (value.Length > 10 ? value[..10] : value);

    // ------------------------------------------------------------------
    // Helpers (CrmRepository pattern)
    // ------------------------------------------------------------------
    private async Task<bool> ExistsAsync(DbConnection connection, DbTransaction transaction, string sql, string company, Guid id, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, sql);
        Add(command, "@c", company);
        Add(command, "@id", FormatGuid(id));
        var count = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        return count > 0;
    }

    private static void RequireCompany(string companyId)
    {
        if (string.IsNullOrWhiteSpace(companyId))
            throw new ArgumentException("Company is required.", nameof(companyId));
    }

    private static bool Truthy(object value) => value switch
    {
        bool b => b,
        long l => l != 0,
        int i => i != 0,
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

    private static Guid ReadGuid(DbDataReader reader, int ordinal)
        => Guid.Parse(Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture)!);

    private static Guid? ReadGuidOrNull(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal)
            ? null
            : Guid.Parse(Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture)!);

    private static string? ReadString(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static decimal? ReadDecimalOrNull(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : Convert.ToDecimal(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
}
