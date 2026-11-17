using System.Data;
using System.Data.Common;
using System.Globalization;
using ShouTech.Domain;
using ShouTech.Domain.Entities;

namespace ShouTech.Infrastructure;

/// <summary>Canonical GL account codes of the seeded chart templates
/// (cod/app/src/Resources/COA/*.shc — every template carries these codes).
/// Per-material acc_* columns override the inventory / sales / COGS legs
/// when they hold an account CODE; these constants are the fallback.</summary>
public static class GlAccounts
{
    public const string Cash = "131";
    public const string Bank = "132";
    public const string Receivable = "1201";
    public const string Inventory = "1203";
    public const string InputVat = "1205";
    public const string Payable = "2201";
    public const string OutputVat = "2205";
    public const string FixedAssets = "1102";
    public const string AccumulatedDepreciation = "1190";
    public const string SalesRevenue = "301";
    public const string SalesReturns = "302";
    public const string Cogs = "401";
    public const string Purchases = "402";
    public const string PurchaseReturns = "403";
    public const string DepreciationExpense = "505";
}

/// <summary>
/// The ONE document-to-GL posting path (Fix Phase Step 3, sweep J-2).
/// Every operational document — invoice, POS sale, order conversion,
/// manufacturing completion, asset depreciation — posts through here,
/// INSIDE the caller's transaction, so the document and its journal
/// entry commit or roll back together. The entry is created as a draft,
/// its lines are inserted, cost centers are validated and split
/// (ACC-5007/5008/5009/5010), and the entry is then flipped to Posted so
/// the database posting triggers validate the open fiscal period and the
/// postable accounts exactly as they do for hand-typed journals.
/// JournalRepository.PostAsync shares the same cost-center validation
/// (<see cref="ApplyCostCenters"/>), so JrnWin/CshWin inherit J-3 too.
/// </summary>
public static class GlPosting
{
    // ------------------------------------------------------------------
    // Public entry points
    // ------------------------------------------------------------------

    /// <summary>Posts a balanced document entry inside an EXISTING
    /// connection/transaction owned by the caller (the document's own
    /// posting transaction). Synchronous ADO is used deliberately: the
    /// app-layer document repositories (InvRepo) are synchronous, and the
    /// async infrastructure repositories call this inside their async
    /// transaction scope without changing their commit boundary.</summary>
    public static GlPostingResult PostInTransaction(
        DbConnection connection, DbTransaction transaction, string provider,
        GlDocumentRequest request, string userId)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(request);
        if (request.CompanyId == Guid.Empty)
            throw new ArgumentException("Company is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("User is required.", nameof(userId));
        var prov = NormalizeProvider(provider);

        var lines = Aggregate(request.Lines);
        var totalDebit = lines.Sum(l => l.Debit);
        var totalCredit = lines.Sum(l => l.Credit);
        if (lines.Count < 2)
            throw new AppError(ErrorCodes.JournalTooFewLines, "A journal entry requires at least two lines.");
        if (totalDebit != totalCredit)
            throw new AppError(ErrorCodes.JournalNotBalanced, "Journal entry debit and credit totals must balance.");
        if (totalDebit == 0)
            throw new AppError(ErrorCodes.JournalZeroAmount, "A journal entry must have a non-zero amount.");

        // Duplicate guard: one live GL entry per source document.
        using (var dup = CreateCommand(connection, transaction,
            "SELECT COUNT(*) FROM journal_entries WHERE company_id = @company AND source_doc_type = @type AND source_doc_id = @source AND reversal_of_entry_id IS NULL AND status IN (0, 1)"))
        {
            Add(dup, "@company", FormatGuid(prov, request.CompanyId));
            Add(dup, "@type", request.SourceDocType.Trim());
            Add(dup, "@source", request.SourceDocId.Trim());
            if (Convert.ToInt64(dup.ExecuteScalar(), CultureInfo.InvariantCulture) > 0)
                throw new AppError(ErrorCodes.DocumentAlreadyPostedToGl,
                    $"Document '{request.SourceDocNo}' already has a posted general-ledger entry and cannot be posted twice.", request.SourceDocNo);
        }

        var periodId = ResolveOpenPeriod(connection, transaction, prov, request.CompanyId, request.EntryDate);

        // Resolve every account code against the canonical chart.
        var resolved = new List<ResolvedLine>();
        foreach (var line in lines)
        {
            var (accountId, costRequired) = ResolveAccount(connection, transaction, prov, request.CompanyId, line.AccountCode);
            resolved.Add(new ResolvedLine(accountId, line.AccountCode.Trim(), line.Debit, line.Credit,
                string.IsNullOrWhiteSpace(line.CostCenter) ? null : line.CostCenter.Trim(),
                string.IsNullOrWhiteSpace(line.Note) ? null : line.Note.Trim(), costRequired));
        }

        var entryId = Guid.NewGuid();
        var documentNumber = "JV-" + request.SourceDocNo.Trim();
        using (var exists = CreateCommand(connection, transaction,
            "SELECT COUNT(*) FROM journal_entries WHERE company_id = @company AND document_number = @doc"))
        {
            Add(exists, "@company", FormatGuid(prov, request.CompanyId));
            Add(exists, "@doc", documentNumber);
            if (Convert.ToInt64(exists.ExecuteScalar(), CultureInfo.InvariantCulture) > 0)
                documentNumber = documentNumber + "-" + entryId.ToString("N")[..8];
        }

        object entryDateValue = prov == "PostgreSQL"
            ? request.EntryDate
            : request.EntryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        using (var insert = CreateCommand(connection, transaction,
            "INSERT INTO journal_entries (journal_entry_id, company_id, entry_date, description, status, created_by, fiscal_period_id, reference_number, currency_code, exchange_rate, document_number, source_doc_type, source_doc_id, source_doc_no) VALUES (@id, @company, @date, @description, 0, @actor, @period, @reference, @currency, @rate, @documentNumber, @sourceType, @sourceId, @sourceNo)"))
        {
            Add(insert, "@id", FormatGuid(prov, entryId));
            Add(insert, "@company", FormatGuid(prov, request.CompanyId));
            Add(insert, "@date", entryDateValue);
            Add(insert, "@description", request.Description.Trim());
            Add(insert, "@actor", userId.Trim());
            Add(insert, "@period", prov == "PostgreSQL" ? (object)Guid.Parse(periodId) : periodId);
            Add(insert, "@reference", request.SourceDocNo.Trim());
            Add(insert, "@currency", string.IsNullOrWhiteSpace(request.CurrencyCode) ? "SAR" : request.CurrencyCode.Trim().ToUpperInvariant());
            Add(insert, "@rate", request.ExchangeRate <= 0 ? 1m : request.ExchangeRate);
            Add(insert, "@documentNumber", documentNumber);
            Add(insert, "@sourceType", request.SourceDocType.Trim());
            Add(insert, "@sourceId", request.SourceDocId.Trim());
            Add(insert, "@sourceNo", request.SourceDocNo.Trim());
            insert.ExecuteNonQuery();
        }

        var snapshots = new List<CostLine>();
        var lineNumber = 0;
        foreach (var line in resolved)
        {
            lineNumber++;
            using var command = CreateCommand(connection, transaction,
                "INSERT INTO journal_entry_lines (journal_entry_id, line_number, account_id, debit, credit, cost_center, note) VALUES (@id, @line, @account, @debit, @credit, @costCenter, @note)");
            Add(command, "@id", FormatGuid(prov, entryId));
            Add(command, "@line", lineNumber);
            Add(command, "@account", FormatGuid(prov, line.AccountId));
            Add(command, "@debit", line.Debit);
            Add(command, "@credit", line.Credit);
            Add(command, "@costCenter", (object?)line.CostCenter ?? DBNull.Value);
            Add(command, "@note", (object?)line.Note ?? DBNull.Value);
            command.ExecuteNonQuery();
            snapshots.Add(new CostLine(lineNumber, line.AccountId, line.AccountCode, line.Debit, line.Credit, line.CostCenter, line.CostRequired));
        }

        ApplyCostCenters(connection, transaction, prov, request.CompanyId, entryId, request.EntryDate,
            snapshots, enforceValidation: true);

        using (var post = CreateCommand(connection, transaction,
            "UPDATE journal_entries SET status = 1, posted_by = @actor, posted_at = CURRENT_TIMESTAMP WHERE journal_entry_id = @id AND status = 0"))
        {
            Add(post, "@actor", userId.Trim());
            Add(post, "@id", FormatGuid(prov, entryId));
            if (post.ExecuteNonQuery() != 1)
                throw new AppError(ErrorCodes.JournalPostingFailed, "The journal entry could not be posted.");
        }
        AddAudit(connection, transaction, prov, entryId, 1, userId, "Document posting created");
        AddAudit(connection, transaction, prov, entryId, 2, userId, $"Posted from {request.SourceDocType} {request.SourceDocNo}");
        return new GlPostingResult(entryId, documentNumber);
    }

    /// <summary>Finds the live (posted, non-reversal) GL entry created for
    /// a source document, or null when the document never posted to GL.</summary>
    public static Guid? FindEntryBySource(
        DbConnection connection, DbTransaction? transaction, string provider,
        Guid companyId, string sourceDocType, string sourceDocId)
    {
        var prov = NormalizeProvider(provider);
        using var command = transaction is null
            ? CreateCommand(connection, null, "SELECT journal_entry_id FROM journal_entries WHERE company_id = @company AND source_doc_type = @type AND source_doc_id = @source AND reversal_of_entry_id IS NULL AND status = 1")
            : CreateCommand(connection, transaction, "SELECT journal_entry_id FROM journal_entries WHERE company_id = @company AND source_doc_type = @type AND source_doc_id = @source AND reversal_of_entry_id IS NULL AND status = 1");
        Add(command, "@company", FormatGuid(prov, companyId));
        Add(command, "@type", sourceDocType.Trim());
        Add(command, "@source", sourceDocId.Trim());
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!);
    }

    /// <summary>
    /// Reverses a posted entry INSIDE the caller's transaction — the same
    /// semantics as JournalRepository.ReverseAsync (which delegates here):
    /// the reversal mirrors the source lines with debit/credit swapped,
    /// is posted in the source entry's (still open) fiscal period, the
    /// source is marked Reversed, source-document columns are copied to
    /// the reversal so the GL-to-document trace survives, and cost splits
    /// are recorded without re-validating master data that may have
    /// changed since the original posting.
    /// </summary>
    public static Guid ReverseInTransaction(
        DbConnection connection, DbTransaction transaction, string provider,
        Guid journalEntryId, string userId, string? details = null)
    {
        var prov = NormalizeProvider(provider);
        string companyIdText;
        DateOnly entryDate;
        string description;
        string periodId;
        string? referenceNumber;
        string currencyCode;
        decimal exchangeRate;
        int status;
        string? sourceType, sourceId, sourceNo;
        using (var header = CreateCommand(connection, transaction,
            "SELECT company_id, entry_date, description, status, fiscal_period_id, reference_number, currency_code, exchange_rate, source_doc_type, source_doc_id, source_doc_no FROM journal_entries WHERE journal_entry_id = @id"))
        {
            Add(header, "@id", FormatGuid(prov, journalEntryId));
            using var reader = header.ExecuteReader();
            if (!reader.Read())
                throw new InvalidOperationException("Journal entry was not found.");
            companyIdText = Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!;
            entryDate = prov == "PostgreSQL"
                ? reader.GetFieldValue<DateOnly>(1)
                : DateOnly.Parse(Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture);
            description = reader.GetString(2);
            status = Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture);
            periodId = reader.IsDBNull(4) ? string.Empty : Convert.ToString(reader.GetValue(4), CultureInfo.InvariantCulture)!;
            referenceNumber = reader.IsDBNull(5) ? null : reader.GetString(5);
            currencyCode = reader.GetString(6);
            exchangeRate = Convert.ToDecimal(reader.GetValue(7), CultureInfo.InvariantCulture);
            sourceType = reader.IsDBNull(8) ? null : reader.GetString(8);
            sourceId = reader.IsDBNull(9) ? null : reader.GetString(9);
            sourceNo = reader.IsDBNull(10) ? null : reader.GetString(10);
        }
        if (status != 1)
            throw new InvalidOperationException("Only an existing posted journal entry can be reversed.");
        if (string.IsNullOrWhiteSpace(periodId))
            throw new InvalidOperationException("The source journal has no fiscal period. Assign it to a valid period before reversal.");
        var companyId = Guid.Parse(companyIdText);
        var openPredicate = prov == "PostgreSQL" ? "fy.is_closed = false" : "fy.is_closed = 0";
        using (var periodCheck = CreateCommand(connection, transaction,
            "SELECT COUNT(*) FROM fiscal_periods fp JOIN fiscal_years fy ON fy.fiscal_year_id=fp.fiscal_year_id WHERE fp.fiscal_period_id=@period AND fp.company_id=@company AND fp.status='OPEN' AND " + openPredicate))
        {
            Add(periodCheck, "@period", prov == "PostgreSQL" ? (object)Guid.Parse(periodId) : periodId);
            Add(periodCheck, "@company", prov == "PostgreSQL" ? (object)companyId : companyIdText);
            if (Convert.ToInt64(periodCheck.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
                throw new InvalidOperationException("The original fiscal period is closed. Reversal in a closed period is blocked; use an approved open-period adjustment workflow.");
        }

        var sourceLines = new List<CostLine>();
        using (var lines = CreateCommand(connection, transaction,
            "SELECT l.line_number, l.account_id, a.account_code, l.debit, l.credit, l.cost_center, l.note, COALESCE(a.cost_required, 0) FROM journal_entry_lines l LEFT JOIN chart_of_accounts a ON a.account_id = l.account_id AND a.company_id = @company WHERE l.journal_entry_id = @id ORDER BY l.line_number"))
        {
            Add(lines, "@company", FormatGuid(prov, companyId));
            Add(lines, "@id", FormatGuid(prov, journalEntryId));
            using var reader = lines.ExecuteReader();
            while (reader.Read())
            {
                sourceLines.Add(new CostLine(
                    Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture),
                    Guid.Parse(Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture)!),
                    reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    Convert.ToDecimal(reader.GetValue(3), CultureInfo.InvariantCulture),
                    Convert.ToDecimal(reader.GetValue(4), CultureInfo.InvariantCulture),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    Convert.ToBoolean(reader.GetValue(7), CultureInfo.InvariantCulture)));
                // note (index 6) is not needed for the reversal lines beyond mirroring; read below via second pass is avoided — note is mirrored from this reader:
                sourceLines[^1] = sourceLines[^1] with { Note = reader.IsDBNull(6) ? null : reader.GetString(6) };
            }
        }
        if (sourceLines.Count < 2 || sourceLines.Sum(x => x.Debit) <= 0 || sourceLines.Sum(x => x.Debit) != sourceLines.Sum(x => x.Credit))
            throw new InvalidOperationException("The source journal is not balanced and cannot be reversed safely.");

        var reversalId = Guid.NewGuid();
        var reversalDescription = $"Reversal: {description}";
        if (reversalDescription.Length > 500) reversalDescription = reversalDescription[..500];
        using (var insert = CreateCommand(connection, transaction,
            "INSERT INTO journal_entries (journal_entry_id, company_id, entry_date, description, status, created_by, reversal_of_entry_id, fiscal_period_id, reference_number, currency_code, exchange_rate, document_number, source_doc_type, source_doc_id, source_doc_no) VALUES (@id, @company, @date, @description, 0, @actor, @source, @period, @reference, @currency, @rate, @documentNumber, @sourceType, @sourceId, @sourceNo)"))
        {
            Add(insert, "@id", FormatGuid(prov, reversalId));
            Add(insert, "@company", prov == "PostgreSQL" ? (object)companyId : companyIdText);
            Add(insert, "@date", prov == "PostgreSQL" ? (object)entryDate : entryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            Add(insert, "@description", reversalDescription);
            Add(insert, "@actor", userId.Trim());
            Add(insert, "@source", FormatGuid(prov, journalEntryId));
            Add(insert, "@period", prov == "PostgreSQL" ? (object)Guid.Parse(periodId) : periodId);
            Add(insert, "@reference", (object?)referenceNumber ?? DBNull.Value);
            Add(insert, "@currency", currencyCode);
            Add(insert, "@rate", exchangeRate);
            Add(insert, "@documentNumber", $"REV-{reversalId.ToString("N")[..12]}");
            Add(insert, "@sourceType", (object?)sourceType ?? DBNull.Value);
            Add(insert, "@sourceId", (object?)sourceId ?? DBNull.Value);
            Add(insert, "@sourceNo", (object?)sourceNo ?? DBNull.Value);
            insert.ExecuteNonQuery();
        }

        var lineNumber = 0;
        var reversalSnapshots = new List<CostLine>();
        foreach (var line in sourceLines)
        {
            lineNumber++;
            using var insertLine = CreateCommand(connection, transaction,
                "INSERT INTO journal_entry_lines (journal_entry_id, line_number, account_id, debit, credit, cost_center, note) VALUES (@id, @line, @account, @debit, @credit, @costCenter, @note)");
            Add(insertLine, "@id", FormatGuid(prov, reversalId));
            Add(insertLine, "@line", lineNumber);
            Add(insertLine, "@account", FormatGuid(prov, line.AccountId));
            Add(insertLine, "@debit", line.Credit);
            Add(insertLine, "@credit", line.Debit);
            Add(insertLine, "@costCenter", (object?)line.CostCenter ?? DBNull.Value);
            Add(insertLine, "@note", (object?)line.Note ?? DBNull.Value);
            insertLine.ExecuteNonQuery();
            reversalSnapshots.Add(new CostLine(lineNumber, line.AccountId, line.AccountCode, line.Credit, line.Debit, line.CostCenter, line.CostRequired));
        }

        using (var postReversal = CreateCommand(connection, transaction,
            "UPDATE journal_entries SET status = 1, posted_by = @actor, posted_at = CURRENT_TIMESTAMP WHERE journal_entry_id = @id AND status = 0"))
        {
            Add(postReversal, "@actor", userId.Trim());
            Add(postReversal, "@id", FormatGuid(prov, reversalId));
            if (postReversal.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("Reversal journal could not be posted under fiscal-period controls.");
        }
        using (var updateOriginal = CreateCommand(connection, transaction,
            "UPDATE journal_entries SET status = 2, reversed_by = @actor, reversed_at = CURRENT_TIMESTAMP WHERE journal_entry_id = @id AND status = 1"))
        {
            Add(updateOriginal, "@actor", userId.Trim());
            Add(updateOriginal, "@id", FormatGuid(prov, journalEntryId));
            if (updateOriginal.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("Journal entry changed while reversal was being created.");
        }

        ApplyCostCenters(connection, transaction, prov, companyId, reversalId, entryDate,
            reversalSnapshots, enforceValidation: false);

        AddAudit(connection, transaction, prov, journalEntryId, 3, userId, details ?? $"Reversed by journal {reversalId:D}");
        AddAudit(connection, transaction, prov, reversalId, 2, userId, $"Reversal of journal {journalEntryId:D}");
        return reversalId;
    }

    // ------------------------------------------------------------------
    // Cost-center validation + splits (J-3) — shared with JournalRepository
    // ------------------------------------------------------------------

    /// <summary>One journal line as the cost-center engine sees it.</summary>
    public sealed record CostLine(
        int LineNumber, Guid AccountId, string AccountCode,
        decimal Debit, decimal Credit, string? CostCenter, bool CostRequired)
    {
        public string? Note { get; init; }
    }

    /// <summary>
    /// Validates cost centers and records journal_line_cost_splits inside
    /// the caller's transaction. J-3 rules (enforceValidation = true):
    /// - a line whose account is flagged cost_required MUST carry a cost
    ///   center (ACC-5010);
    /// - a cost-center code matching NO master row fails the posting
    ///   (ACC-5009) — the pre-Step-3 free-text escape is closed;
    /// - a group or inactive center fails (ACC-5008), and an allocation
    ///   that does not total 100 or targets a non-leaf fails (ACC-5007) —
    ///   unchanged from Phase A2;
    /// - a valid allocation splits the line amounts exactly across its
    ///   targets; otherwise the line maps 100% to its own leaf center.
    /// With enforceValidation = false (reversals mirroring an already
    /// posted entry) nothing is blocked and unknown centers simply record
    /// no split, preserving the pre-Step-3 reversal behavior.
    /// Cost centers are keyed by company CODE in the master tables; the
    /// journal world keys by company Guid, resolved through companies.
    /// </summary>
    public static void ApplyCostCenters(
        DbConnection connection, DbTransaction transaction, string provider,
        Guid companyId, Guid journalEntryId, DateOnly entryDate,
        IReadOnlyList<CostLine> lines, bool enforceValidation)
    {
        var prov = NormalizeProvider(provider);
        var boolTrue = prov == "PostgreSQL" ? "true" : "1";
        var boolFalse = prov == "PostgreSQL" ? "false" : "0";

        string? companyCode;
        using (var company = CreateCommand(connection, transaction,
            "SELECT company_code FROM companies WHERE company_id = @company"))
        {
            Add(company, "@company", FormatGuid(prov, companyId));
            var value = company.ExecuteScalar();
            companyCode = value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
        }
        if (string.IsNullOrWhiteSpace(companyCode))
            return;

        using (var clear = CreateCommand(connection, transaction,
            "DELETE FROM journal_line_cost_splits WHERE journal_entry_id = @id"))
        {
            Add(clear, "@id", FormatGuid(prov, journalEntryId));
            clear.ExecuteNonQuery();
        }

        void InsertSplit(int lineNumber, string centerId, string centerCode, decimal percentage, decimal debitShare, decimal creditShare)
        {
            using var insert = CreateCommand(connection, transaction,
                "INSERT INTO journal_line_cost_splits (id, company_id, journal_entry_id, line_number, cost_center_id, cost_center_code, percentage, debit_amount, credit_amount) VALUES (@id, @company, @entry, @line, @center, @code, @pct, @debit, @credit)");
            Add(insert, "@id", FormatGuid(prov, Guid.NewGuid()));
            Add(insert, "@company", companyCode);
            Add(insert, "@entry", FormatGuid(prov, journalEntryId));
            Add(insert, "@line", lineNumber);
            Add(insert, "@center", prov == "PostgreSQL" ? (object)Guid.Parse(centerId) : centerId);
            Add(insert, "@code", centerCode);
            Add(insert, "@pct", percentage);
            Add(insert, "@debit", debitShare);
            Add(insert, "@credit", creditShare);
            insert.ExecuteNonQuery();
        }

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line.CostCenter))
            {
                if (enforceValidation && line.CostRequired)
                    throw new AppError(ErrorCodes.CostCenterRequired,
                        $"Account '{line.AccountCode}' requires a cost center; the journal line has none.", line.AccountCode);
                continue;
            }
            var code = line.CostCenter.Trim();

            string centerId;
            bool isGroup, isActive;
            bool found;
            using (var center = CreateCommand(connection, transaction,
                $"SELECT id, is_group, is_active FROM cost_centers WHERE company_id = @company AND code = @code AND is_deleted = {boolFalse}"))
            {
                Add(center, "@company", companyCode);
                Add(center, "@code", code);
                using var reader = center.ExecuteReader();
                found = reader.Read();
                if (!found)
                {
                    centerId = string.Empty; isGroup = false; isActive = false;
                }
                else
                {
                    centerId = Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!;
                    isGroup = Convert.ToBoolean(reader.GetValue(1), CultureInfo.InvariantCulture);
                    isActive = Convert.ToBoolean(reader.GetValue(2), CultureInfo.InvariantCulture);
                }
            }
            if (!found)
            {
                if (enforceValidation)
                    throw new AppError(ErrorCodes.CostCenterUnknown,
                        $"Cost center '{code}' does not match any active cost center for this company and cannot be posted.", code);
                continue; // reversal mirror: legacy free-text, no split
            }

            string? allocationId = null;
            using (var allocation = CreateCommand(connection, transaction,
                $"""
                SELECT a.id FROM cost_center_allocations a
                JOIN cost_centers main ON main.id = a.main_cost_center_id
                WHERE a.company_id = @company AND main.code = @code
                  AND a.is_active = {boolTrue} AND a.is_deleted = {boolFalse} AND main.is_deleted = {boolFalse}
                  AND a.valid_from <= @date
                ORDER BY a.valid_from DESC LIMIT 1
                """))
            {
                Add(allocation, "@company", companyCode);
                Add(allocation, "@code", code);
                Add(allocation, "@date", entryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                var value = allocation.ExecuteScalar();
                allocationId = value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
            }

            var allocationApplied = false;
            if (allocationId is not null)
            {
                var targets = new List<(string Id, string Code, decimal Percentage, bool Valid)>();
                using (var loadTargets = CreateCommand(connection, transaction,
                    """
                    SELECT c.id, c.code, l.percentage, c.is_group, c.is_active, c.is_deleted
                    FROM cost_center_allocation_lines l
                    JOIN cost_centers c ON c.id = l.cost_center_id
                    WHERE l.allocation_id = @allocation
                    """))
                {
                    Add(loadTargets, "@allocation", prov == "PostgreSQL" ? (object)Guid.Parse(allocationId) : allocationId);
                    using var reader = loadTargets.ExecuteReader();
                    while (reader.Read())
                    {
                        var targetIsGroup = Convert.ToBoolean(reader.GetValue(3), CultureInfo.InvariantCulture);
                        var targetIsActive = Convert.ToBoolean(reader.GetValue(4), CultureInfo.InvariantCulture);
                        var targetIsDeleted = Convert.ToBoolean(reader.GetValue(5), CultureInfo.InvariantCulture);
                        targets.Add((Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!,
                            reader.GetString(1),
                            Convert.ToDecimal(reader.GetValue(2), CultureInfo.InvariantCulture),
                            !targetIsGroup && targetIsActive && !targetIsDeleted));
                    }
                }

                var totalPercentage = targets.Sum(t => t.Percentage);
                var allocationValid = targets.Count > 0
                    && targets.All(t => t.Valid)
                    && Math.Abs(totalPercentage - 100m) <= 0.01m;
                if (!allocationValid)
                {
                    if (enforceValidation)
                        throw new AppError(ErrorCodes.CostCenterAllocationInvalid,
                            $"The allocation for cost center '{code}' is invalid: its percentages must total 100 and every target must be an active leaf cost center.", code);
                }
                else
                {
                    var ordered = targets.OrderByDescending(t => t.Percentage).ToList();
                    var debitAssigned = 0m;
                    var creditAssigned = 0m;
                    for (var i = 1; i < ordered.Count; i++)
                    {
                        var target = ordered[i];
                        var debitShare = Math.Round(line.Debit * target.Percentage / 100m, 4, MidpointRounding.AwayFromZero);
                        var creditShare = Math.Round(line.Credit * target.Percentage / 100m, 4, MidpointRounding.AwayFromZero);
                        debitAssigned += debitShare;
                        creditAssigned += creditShare;
                        InsertSplit(line.LineNumber, target.Id, target.Code, target.Percentage, debitShare, creditShare);
                    }
                    var largest = ordered[0];
                    InsertSplit(line.LineNumber, largest.Id, largest.Code, largest.Percentage,
                        line.Debit - debitAssigned, line.Credit - creditAssigned);
                    allocationApplied = true;
                }
            }

            if (allocationApplied)
                continue;

            if (enforceValidation && (isGroup || !isActive))
                throw new AppError(ErrorCodes.CostCenterNotLeaf,
                    $"Cost center '{code}' is a group or inactive cost center and cannot receive postings.", code);

            InsertSplit(line.LineNumber, centerId, code, 100m, line.Debit, line.Credit);
        }
    }

    /// <summary>Resolves an account code to its Guid + cost_required flag
    /// against the canonical chart; ACC-5011 when missing / inactive /
    /// deleted / not postable.</summary>
    public static (Guid AccountId, bool CostRequired) ResolveAccount(
        DbConnection connection, DbTransaction transaction, string provider,
        Guid companyId, string accountCode)
    {
        var prov = NormalizeProvider(provider);
        var activePredicate = prov == "PostgreSQL"
            ? "is_active=true AND is_deleted=false AND is_postable=true"
            : "is_active=1 AND is_deleted=0 AND is_postable=1";
        using var command = CreateCommand(connection, transaction,
            $"SELECT account_id, COALESCE(cost_required, 0) FROM chart_of_accounts WHERE company_id=@company AND account_code=@code AND {activePredicate}");
        Add(command, "@company", FormatGuid(prov, companyId));
        Add(command, "@code", accountCode.Trim());
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            throw new AppError(ErrorCodes.DocumentPostingAccountMissing,
                $"Document posting account '{accountCode.Trim()}' is missing, inactive, deleted, or not postable for this company.", accountCode.Trim());
        return (Guid.Parse(Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!),
            Convert.ToBoolean(reader.GetValue(1), CultureInfo.InvariantCulture));
    }

    /// <summary>Resolves a code only when a postable account exists;
    /// otherwise null (used for the OPTIONAL tax leg: the seeded charts
    /// carry the VAT codes as non-postable group accounts, so tax is
    /// split out only when the company configured a postable tax
    /// account — otherwise the tax stays inside the revenue/purchase
    /// leg, exactly as the pre-Step-3 totals did).</summary>
    public static Guid? TryResolveAccount(
        DbConnection connection, DbTransaction transaction, string provider,
        Guid companyId, string accountCode)
    {
        try { return ResolveAccount(connection, transaction, provider, companyId, accountCode).AccountId; }
        catch (AppError ex) when (ex.Code == ErrorCodes.DocumentPostingAccountMissing) { return null; }
    }

    // ------------------------------------------------------------------
    // Internals
    // ------------------------------------------------------------------

    private sealed record ResolvedLine(
        Guid AccountId, string AccountCode, decimal Debit, decimal Credit,
        string? CostCenter, string? Note, bool CostRequired);

    private static List<GlLineRequest> Aggregate(IReadOnlyList<GlLineRequest> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var map = new Dictionary<(string Code, string? Cc, bool IsDebit), GlLineRequest>();
        var order = new List<(string, string?, bool)>();
        foreach (var line in lines)
        {
            if (line.Debit < 0 || line.Credit < 0 || (line.Debit > 0 && line.Credit > 0))
                throw new ArgumentException("A GL line cannot be negative or carry both debit and credit.");
            if (line.Debit == 0 && line.Credit == 0) continue;
            var isDebit = line.Debit > 0;
            var key = (line.AccountCode.Trim(), string.IsNullOrWhiteSpace(line.CostCenter) ? null : line.CostCenter.Trim(), isDebit);
            if (!map.TryGetValue(key, out var existing))
            {
                map[key] = line;
                order.Add(key);
            }
            else
            {
                map[key] = isDebit
                    ? existing with { Debit = existing.Debit + line.Debit }
                    : existing with { Credit = existing.Credit + line.Credit };
            }
        }
        return order.Select(k => map[k]).ToList();
    }

    private static string ResolveOpenPeriod(
        DbConnection connection, DbTransaction transaction, string prov, Guid companyId, DateOnly entryDate)
    {
        object entryDateValue = prov == "PostgreSQL"
            ? entryDate
            : entryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        using var period = CreateCommand(connection, transaction,
            "SELECT fp.fiscal_period_id FROM fiscal_periods fp JOIN fiscal_years fy ON fy.fiscal_year_id=fp.fiscal_year_id WHERE fp.company_id=@company AND @date BETWEEN fp.start_date AND fp.end_date AND fp.status='OPEN' AND fy.is_closed=" + (prov == "PostgreSQL" ? "false" : "0"));
        Add(period, "@company", FormatGuid(prov, companyId));
        Add(period, "@date", entryDateValue);
        var value = period.ExecuteScalar();
        if (value is null || value is DBNull)
            throw new InvalidOperationException("No open fiscal period matches the document date and company.");
        return Convert.ToString(value, CultureInfo.InvariantCulture)!;
    }

    private static void AddAudit(DbConnection connection, DbTransaction transaction, string prov,
        Guid entryId, int eventType, string actor, string? details)
    {
        using var command = CreateCommand(connection, transaction,
            "INSERT INTO journal_audit_events (journal_entry_id, event_type, actor, details) VALUES (@id, @type, @actor, @details)");
        Add(command, "@id", FormatGuid(prov, entryId));
        Add(command, "@type", eventType);
        Add(command, "@actor", actor.Trim());
        Add(command, "@details", (object?)details ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    internal static string NormalizeProvider(string provider) => provider.Trim().ToLowerInvariant() switch
    {
        "sqlite" => "SQLite",
        "postgres" or "postgresql" or "npgsql" => "PostgreSQL",
        _ => throw new NotSupportedException($"Unsupported database provider '{provider}'. Use SQLite or PostgreSQL.")
    };

    internal static object FormatGuid(string prov, Guid value) => prov == "PostgreSQL" ? value : value.ToString("D");

    internal static DbCommand CreateCommand(DbConnection connection, DbTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        if (transaction is not null) command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    internal static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
