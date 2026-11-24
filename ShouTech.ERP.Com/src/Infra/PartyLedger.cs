using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using ShouTech.Domain;

namespace ShouTech.Infrastructure;

/// <summary>
/// The party-ledger primitives shared by every posting path (Fix Phase
/// Step 4, migration 061). After unification there is ONE party master —
/// <c>parties</c> (031) — and every operational reference
/// (<c>party_ledger.party_id</c>, document <c>party_id</c>,
/// <c>payment_allocations.party_id</c>) carries the party's stable integer
/// identity, <c>parties.ledger_id</c>.
///
/// Rules enforced here and by every caller:
/// - Resolution is ALWAYS (company, kind, code) -> ledger_id against
///   <c>parties</c>. A party code is never parsed as a number (sweep C-2:
///   code "1" inherited a stranger's 777 balance), and no query in this
///   area runs without a company predicate (sweep G-1).
/// - <c>party_ledger.company_id</c> is the company CODE. Callers that hold
///   the company Guid (chain / order repositories) resolve it through
///   <see cref="ResolveCompanyCode"/>; migration 061 normalized the rows
///   historical writers stored under the Guid (sweep B-06).
/// - Settlement entries (money that settles a party balance) are always
///   CREDIT for a customer and DEBIT for a supplier — the mirror of the
///   invoice entries (customer debit / supplier credit).
/// </summary>
public static class PartyLedger
{
    public static bool IsPostgres(string provider)
        => provider.Trim().ToLowerInvariant() is "postgres" or "postgresql" or "npgsql";

    public static string NotDeleted(string provider)
        => IsPostgres(provider) ? "is_deleted = false" : "is_deleted = 0";

    /// <summary>
    /// Resolves a company key that may be the company CODE (app layer) or
    /// the company Guid (journal-side repositories) to the company CODE
    /// that party_ledger rows carry. Unknown keys pass through unchanged
    /// (repositories whose fixtures never seeded a companies row keep
    /// working with their own key, consistently for writer and reader).
    /// </summary>
    public static string ResolveCompanyCode(DbConnection connection, DbTransaction? transaction, string provider, string companyKey)
    {
        if (string.IsNullOrWhiteSpace(companyKey))
            return companyKey;
        using var command = Create(connection, transaction,
            "SELECT company_code FROM companies WHERE company_code = @key OR CAST(company_id AS TEXT) = @key");
        Add(command, "@key", companyKey.Trim());
        var value = command.ExecuteScalar();
        return value is null or DBNull ? companyKey.Trim() : Convert.ToString(value, CultureInfo.InvariantCulture)!;
    }

    public static async Task<string> ResolveCompanyCodeAsync(DbConnection connection, DbTransaction? transaction, string provider, string companyKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(companyKey))
            return companyKey;
        await using var command = Create(connection, transaction,
            "SELECT company_code FROM companies WHERE company_code = @key OR CAST(company_id AS TEXT) = @key");
        Add(command, "@key", companyKey.Trim());
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? companyKey.Trim() : Convert.ToString(value, CultureInfo.InvariantCulture)!;
    }

    /// <summary>
    /// Exact (company, kind, code) resolution to the party's ledger id.
    /// Returns null when no live party matches — never a numeric guess.
    /// </summary>
    public static long? ResolveLedgerId(DbConnection connection, DbTransaction? transaction, string provider, string companyCode, string partyKind, string partyCode)
    {
        using var command = Create(connection, transaction,
            $"SELECT ledger_id FROM parties WHERE company_id = @company AND kind = @kind AND code = @code AND {NotDeleted(provider)}");
        Add(command, "@company", companyCode.Trim());
        Add(command, "@kind", partyKind.Trim().ToUpperInvariant());
        Add(command, "@code", partyCode.Trim());
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    public static async Task<long?> ResolveLedgerIdAsync(DbConnection connection, DbTransaction? transaction, string provider, string companyCode, string partyKind, string partyCode, CancellationToken cancellationToken = default)
    {
        await using var command = Create(connection, transaction,
            $"SELECT ledger_id FROM parties WHERE company_id = @company AND kind = @kind AND code = @code AND {NotDeleted(provider)}");
        Add(command, "@company", companyCode.Trim());
        Add(command, "@kind", partyKind.Trim().ToUpperInvariant());
        Add(command, "@code", partyCode.Trim());
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Inserts one party_ledger row inside the caller's transaction and
    /// returns its ledger_id. All values are parameters; the caller owns
    /// the debit/credit decision and the transaction boundary.
    /// </summary>
    public static long WriteEntry(
        DbConnection connection, DbTransaction transaction, string provider,
        string companyCode, string partyKind, long partyId,
        string? docKind, long? docId, string? docNo, string entryDate,
        decimal debit, decimal credit, string currencyCode, string? note,
        string? journalEntryId = null,
        decimal exchangeRate = 0m,
        decimal? baseDebit = null,
        decimal? baseCredit = null)
    {
        // journal_entry_id exists from migration 061; hand-built fixtures
        // predating it keep working with the original column list.
        var hasJournal = ColumnExists(connection, transaction, provider, "party_ledger", "journal_entry_id");
        // Step 7 (migration 062): base amounts exist from 062; fixtures
        // predating it keep working with the original column list.
        var hasBase = ColumnExists(connection, transaction, provider, "party_ledger", "base_debit");
        var columns = "company_id, party_kind, party_id, doc_kind, doc_id, doc_no, entry_date, debit, credit, currency_code, note";
        var values = "@company, @kind, @party, @docKind, @doc, @docNo, @date, @debit, @credit, @currency, @note";
        if (hasJournal) { columns += ", journal_entry_id"; values += ", @journal"; }
        if (hasBase) { columns += ", base_debit, base_credit, exchange_rate"; values += ", @baseDebit, @baseCredit, @rate"; }
        var sql = IsPostgres(provider)
            ? $"INSERT INTO party_ledger ({columns}) VALUES ({values}) RETURNING ledger_id"
            : $"INSERT INTO party_ledger ({columns}) VALUES ({values}); SELECT last_insert_rowid();";
        using var command = Create(connection, transaction, sql);
        Add(command, "@company", companyCode.Trim());
        Add(command, "@kind", partyKind.Trim().ToUpperInvariant());
        Add(command, "@party", partyId);
        Add(command, "@docKind", (object?)docKind ?? DBNull.Value);
        Add(command, "@doc", (object?)docId ?? DBNull.Value);
        Add(command, "@docNo", (object?)docNo ?? DBNull.Value);
        Add(command, "@date", entryDate);
        Add(command, "@debit", debit);
        Add(command, "@credit", credit);
        Add(command, "@currency", string.IsNullOrWhiteSpace(currencyCode) ? "SAR" : currencyCode.Trim());
        Add(command, "@note", (object?)note ?? DBNull.Value);
        if (hasJournal)
            Add(command, "@journal", (object?)journalEntryId ?? DBNull.Value);
        if (hasBase)
        {
            // Base amounts: explicit overrides win (reversal mirrors pass
            // the original row's stored base, swapped). Otherwise the
            // effective rate is resolved through the ONE path (FxRates):
            // caller-supplied document rate first, currency_rates master
            // where absent, 1 for the base currency — and a foreign
            // amount with no rate anywhere fails with ACC-5015 instead
            // of silently landing at face value.
            decimal rate;
            decimal bd, bc;
            if (baseDebit.HasValue && baseCredit.HasValue)
            {
                rate = exchangeRate;
                bd = baseDebit.Value;
                bc = baseCredit.Value;
            }
            else
            {
                var date = DateOnly.TryParse(entryDate, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : DateOnly.FromDateTime(DateTime.Today);
                var fx = FxRates.ResolveByCompanyCode(connection, transaction, provider,
                    companyCode, string.IsNullOrWhiteSpace(currencyCode) ? "SAR" : currencyCode,
                    date, exchangeRate);
                rate = fx.Rate;
                bd = FxRates.ToBase(debit, rate);
                bc = FxRates.ToBase(credit, rate);
            }
            Add(command, "@baseDebit", bd);
            Add(command, "@baseCredit", bc);
            Add(command, "@rate", rate);
        }
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Settlement side for a party kind: money that settles a balance is a
    /// CREDIT for customers and a DEBIT for suppliers. Returns the
    /// (debit, credit) pair for a positive settlement amount.
    /// </summary>
    public static (decimal Debit, decimal Credit) SettlementSides(string partyKind, decimal amount)
        => partyKind.Trim().ToUpperInvariant() == "C" ? (0m, amount) : (amount, 0m);

    /// <summary>
    /// Mirrors every party_ledger row written for a journal entry (the
    /// cash-voucher settlement link, migration 061) into the entry's
    /// reversal, inside the reversal transaction: debit/credit swap, the
    /// mirror row points at the reversal entry. Rows of documents that
    /// reverse through return documents carry no journal link and are
    /// never touched here. A second reversal of the same entry is blocked
    /// by the journal engine itself, so mirrors cannot duplicate.
    /// </summary>
    public static void MirrorForJournalReversal(
        DbConnection connection, DbTransaction transaction, string provider,
        Guid companyGuid, Guid originalEntryId, Guid reversalEntryId, string reversalDate, string userId)
    {
        if (!TableExists(connection, transaction, provider, "party_ledger"))
            return;
        // journal_entry_id was added by migration 061; older test fixtures
        // that hand-build party_ledger without it simply have no links.
        if (!ColumnExists(connection, transaction, provider, "party_ledger", "journal_entry_id"))
            return;

        var companyCode = TableExists(connection, transaction, provider, "companies")
            ? ResolveCompanyCode(connection, transaction, provider, companyGuid.ToString("D"))
            : companyGuid.ToString("D");
        var hasBase = ColumnExists(connection, transaction, provider, "party_ledger", "base_debit");
        var originals = new List<(string Company, string Kind, long Party, string? DocKind, long? DocId, string? DocNo, string Date, decimal Debit, decimal Credit, string Currency, string? Note, decimal? BaseDebit, decimal? BaseCredit, decimal Rate)>();
        using (var load = Create(connection, transaction,
            "SELECT company_id, party_kind, party_id, doc_kind, doc_id, doc_no, entry_date, debit, credit, currency_code, note"
            + (hasBase ? ", base_debit, base_credit, exchange_rate" : "")
            + " FROM party_ledger WHERE journal_entry_id = @journal"))
        {
            Add(load, "@journal", originalEntryId.ToString("D"));
            using var reader = load.ExecuteReader();
            while (reader.Read())
            {
                originals.Add((
                    Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!,
                    Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture)!,
                    Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture),
                    reader.IsDBNull(3) ? null : Convert.ToString(reader.GetValue(3), CultureInfo.InvariantCulture),
                    reader.IsDBNull(4) ? null : Convert.ToInt64(reader.GetValue(4), CultureInfo.InvariantCulture),
                    reader.IsDBNull(5) ? null : Convert.ToString(reader.GetValue(5), CultureInfo.InvariantCulture),
                    Convert.ToString(reader.GetValue(6), CultureInfo.InvariantCulture)!,
                    Convert.ToDecimal(reader.GetValue(7), CultureInfo.InvariantCulture),
                    Convert.ToDecimal(reader.GetValue(8), CultureInfo.InvariantCulture),
                    reader.IsDBNull(9) ? "SAR" : Convert.ToString(reader.GetValue(9), CultureInfo.InvariantCulture)!,
                    reader.IsDBNull(10) ? null : Convert.ToString(reader.GetValue(10), CultureInfo.InvariantCulture),
                    hasBase && !reader.IsDBNull(11) ? Convert.ToDecimal(reader.GetValue(11), CultureInfo.InvariantCulture) : null,
                    hasBase && !reader.IsDBNull(12) ? Convert.ToDecimal(reader.GetValue(12), CultureInfo.InvariantCulture) : null,
                    hasBase && !reader.IsDBNull(13) ? Convert.ToDecimal(reader.GetValue(13), CultureInfo.InvariantCulture) : 0m));
            }
        }

        foreach (var row in originals)
        {
            // The mirror belongs to the same company the original row was
            // written under; when that is the journal's company (the normal
            // case) it is exactly the resolved code.
            var company = string.Equals(row.Company, companyGuid.ToString("D"), StringComparison.OrdinalIgnoreCase)
                ? companyCode
                : row.Company;
            // Step 7: the mirror swaps the STORED base amounts (never a
            // recomputation at today's rate), so the pair nets to zero
            // in base exactly. Rows without stored base fall through to
            // rate resolution at the original row's rate.
            if (row.DocKind is "RCV" or "PAY" or "ADV")
                UnallocateVoucherPayments(connection, transaction, provider, row.Company, company, originalEntryId, reversalEntryId, userId);

            WriteEntry(connection, transaction, provider, company, row.Kind, row.Party,
                row.DocKind, row.DocId, row.DocNo,
                reversalDate,
                row.Credit, row.Debit, row.Currency,
                $"Reversal of journal settlement {originalEntryId:D}",
                reversalEntryId.ToString("D"),
                exchangeRate: row.Rate,
                baseDebit: row.BaseCredit,
                baseCredit: row.BaseDebit);
        }
    }

    /// <summary>
    /// Releases every allocation made from a cash receipt/payment voucher
    /// before its party-ledger mirror is written. This keeps document
    /// paid_amount and payment availability consistent when a voucher is
    /// reversed; the caller owns the transaction, so a partial release can
    /// never commit.
    /// </summary>
    private static void UnallocateVoucherPayments(
        DbConnection connection, DbTransaction transaction, string provider,
        string ledgerCompanyId, string companyCode, Guid journalEntryId, Guid reversalJournalId, string userId)
    {
        if (!TableExists(connection, transaction, provider, "payment_allocations")
            || !ColumnExists(connection, transaction, provider, "party_ledger", "journal_entry_id"))
            return;

        long paymentLedgerId;
        using (var payment = Create(connection, transaction,
            "SELECT ledger_id FROM party_ledger WHERE company_id = @company AND journal_entry_id = @journal AND doc_kind IN ('RCV','PAY','ADV')"
            + (IsPostgres(provider) ? " FOR UPDATE" : "")))
        {
            Add(payment, "@company", ledgerCompanyId);
            Add(payment, "@journal", journalEntryId.ToString("D"));
            var value = payment.ExecuteScalar();
            if (value is null or DBNull) return;
            paymentLedgerId = Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }

        var hasDocumentCompany = ColumnExists(connection, transaction, provider,
            "payment_allocations", "document_company_id");
        var companyExpression = hasDocumentCompany
            ? "COALESCE(document_company_id, company_id)"
            : "company_id";
        // Supplier advances (migration 110): an advance allocation is
        // NOT released by reversing the advance voucher — the clearing
        // journal (Dr AP / Cr Advances) belongs to the allocation, so a
        // consumed advance blocks the voucher reversal until each
        // advance allocation is itself reversed in PayRecWin
        // (ReconciliationRepository.UnallocateAsync).
        var hasSourceKind = ColumnExists(connection, transaction, provider,
            "payment_allocations", "source_kind");
        var allocations = new List<(string AllocationId, string Table, long DocumentId,
            string DocumentCompany, decimal Amount, string? AllocatedBy, string? Note)>();
        using (var load = Create(connection, transaction,
            $"""
            SELECT allocation_id, doc_table, doc_id, {companyExpression}, amount, allocated_by, note
            {(hasSourceKind ? ", source_kind" : "")}
            FROM payment_allocations
            WHERE company_id = @company AND payment_ledger_id = @payment
            ORDER BY allocation_id
            """))
        {
            Add(load, "@company", companyCode);
            Add(load, "@payment", paymentLedgerId);
            using var reader = load.ExecuteReader();
            while (reader.Read())
            {
                var table = Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) ?? string.Empty;
                if (table is not ("SALES" or "PURCHASE"))
                    throw new InvalidOperationException($"Unsupported allocated document table '{table}' during voucher reversal.");
                if (hasSourceKind && !reader.IsDBNull(7)
                    && string.Equals(Convert.ToString(reader.GetValue(7), CultureInfo.InvariantCulture), "ADVANCE", StringComparison.OrdinalIgnoreCase))
                    throw new AppError(ErrorCodes.AdvanceReversalBlockedWhileConsumed,
                        "This supplier advance is partly consumed by invoice allocations and cannot be reversed until those allocations are reversed.",
                        paymentLedgerId.ToString(CultureInfo.InvariantCulture));
                allocations.Add((
                    Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture) ?? string.Empty,
                    table,
                    Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture),
                    Convert.ToString(reader.GetValue(3), CultureInfo.InvariantCulture) ?? companyCode,
                    Convert.ToDecimal(reader.GetValue(4), CultureInfo.InvariantCulture),
                    reader.IsDBNull(5) ? null : Convert.ToString(reader.GetValue(5), CultureInfo.InvariantCulture),
                    reader.IsDBNull(6) ? null : Convert.ToString(reader.GetValue(6), CultureInfo.InvariantCulture)));
            }
        }

        if (allocations.Count == 0) return;
        if (!TableExists(connection, transaction, provider, "payment_allocation_reversal_audit"))
            throw new InvalidOperationException(
                "Payment-allocation reversal audit schema is missing. Apply database migrations before reversing this allocated voucher.");

        var isPg = IsPostgres(provider);
        foreach (var allocation in allocations)
        {
            var auditId = Guid.NewGuid();
            using (var audit = Create(connection, transaction, """
                INSERT INTO payment_allocation_reversal_audit
                    (audit_id, company_id, allocation_id, payment_ledger_id, doc_table, doc_id,
                     document_company_id, amount, allocated_by, note, released_by, reversal_journal_id)
                VALUES
                    (@audit, @company, @allocation, @payment, @table, @doc,
                     @documentCompany, @amount, @allocatedBy, @note, @releasedBy, @reversal)
                """))
            {
                Add(audit, "@audit", isPg ? (object)auditId : auditId.ToString("D"));
                Add(audit, "@company", companyCode);
                Add(audit, "@allocation", isPg
                    ? (object)Guid.Parse(allocation.AllocationId)
                    : allocation.AllocationId);
                Add(audit, "@payment", paymentLedgerId);
                Add(audit, "@table", allocation.Table);
                Add(audit, "@doc", allocation.DocumentId);
                Add(audit, "@documentCompany", allocation.DocumentCompany);
                Add(audit, "@amount", allocation.Amount);
                Add(audit, "@allocatedBy", (object?)allocation.AllocatedBy ?? DBNull.Value);
                Add(audit, "@note", (object?)allocation.Note ?? DBNull.Value);
                Add(audit, "@releasedBy", userId.Trim());
                Add(audit, "@reversal", isPg ? (object)reversalJournalId : reversalJournalId.ToString("D"));
                audit.ExecuteNonQuery();
            }

            var documentTable = allocation.Table == "SALES" ? "sales_documents" : "purchase_documents";
            string actualDocumentCompany;
            decimal currentPaidAmount;
            bool documentDeleted;
            using (var inspect = Create(connection, transaction,
                $"""
                SELECT CAST(company_id AS TEXT), paid_amount, is_deleted
                FROM {documentTable}
                WHERE CAST(company_id AS TEXT) IN (@documentCompany, @company) AND doc_id = @doc
                """))
            {
                Add(inspect, "@documentCompany", allocation.DocumentCompany);
                Add(inspect, "@company", companyCode);
                Add(inspect, "@doc", allocation.DocumentId);
                using var reader = inspect.ExecuteReader();
                if (!reader.Read())
                    throw new InvalidOperationException(
                        $"Could not find {allocation.Table} document {allocation.DocumentId} for company keys '{allocation.DocumentCompany}' or '{companyCode}' while reversing the payment voucher.");
                actualDocumentCompany = Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture) ?? string.Empty;
                currentPaidAmount = Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture);
                documentDeleted = !reader.IsDBNull(2)
                    && Convert.ToBoolean(reader.GetValue(2), CultureInfo.InvariantCulture);
            }

            if (documentDeleted || currentPaidAmount + 0.005m < allocation.Amount)
                throw new InvalidOperationException(
                    $"Cannot release {allocation.Amount} from {allocation.Table} document {allocation.DocumentId}: paid amount is {currentPaidAmount}, deleted={documentDeleted}, company='{actualDocumentCompany}'.");

            using var release = Create(connection, transaction,
                $"""
                UPDATE {documentTable}
                SET paid_amount = CASE
                    WHEN CAST(paid_amount AS NUMERIC) <= CAST(@amount AS NUMERIC) + 0.005 THEN 0
                    ELSE CAST(paid_amount AS NUMERIC) - CAST(@amount AS NUMERIC)
                END
                WHERE CAST(company_id AS TEXT) = @documentCompany
                  AND doc_id = @doc
                  AND CAST(paid_amount AS NUMERIC) + 0.005 >= CAST(@amount AS NUMERIC)
                  AND COALESCE(is_deleted, {(isPg ? "FALSE" : "0")}) = {(isPg ? "FALSE" : "0")}
                """);
            Add(release, "@amount", allocation.Amount);
            Add(release, "@documentCompany", actualDocumentCompany);
            Add(release, "@doc", allocation.DocumentId);
            if (release.ExecuteNonQuery() != 1)
                throw new InvalidOperationException(
                    $"Could not release the {allocation.Amount} allocation from {allocation.Table} document {allocation.DocumentId} (company '{actualDocumentCompany}', paid amount {currentPaidAmount}) while reversing the payment voucher.");
        }

        using var delete = Create(connection, transaction,
            "DELETE FROM payment_allocations WHERE company_id = @company AND payment_ledger_id = @payment");
        Add(delete, "@company", companyCode);
        Add(delete, "@payment", paymentLedgerId);
        delete.ExecuteNonQuery();
    }

    /// <summary>
    /// Due date for a party document (W1, migration 095): doc_date + the
    /// party's credit_days from the canonical card (resolved by ledger id
    /// in the resolved company). Null when the party or its credit_days
    /// cannot be resolved — due dates are then left unset and aging
    /// falls back to the entry date.
    /// </summary>
    public static DateOnly? ComputeDueDate(DbConnection connection, DbTransaction? transaction, string provider, string companyKey, long partyId, DateOnly docDate)
    {
        if (partyId <= 0)
            return null;
        var companyCode = ResolveCompanyCode(connection, transaction, provider, companyKey);
        using var command = Create(connection, transaction,
            $"SELECT credit_days FROM parties WHERE company_id = @company AND ledger_id = @party AND {NotDeleted(provider)}");
        Add(command, "@company", companyCode);
        Add(command, "@party", partyId);
        var value = command.ExecuteScalar();
        if (value is null or DBNull)
            return null;
        var days = Convert.ToInt32(value, CultureInfo.InvariantCulture);
        return docDate.AddDays(Math.Max(days, 0));
    }

    public static async Task<DateOnly?> ComputeDueDateAsync(DbConnection connection, DbTransaction? transaction, string provider, string companyKey, long partyId, DateOnly docDate, CancellationToken cancellationToken = default)
    {
        if (partyId <= 0)
            return null;
        var companyCode = await ResolveCompanyCodeAsync(connection, transaction, provider, companyKey, cancellationToken).ConfigureAwait(false);
        await using var command = Create(connection, transaction,
            $"SELECT credit_days FROM parties WHERE company_id = @company AND ledger_id = @party AND {NotDeleted(provider)}");
        Add(command, "@company", companyCode);
        Add(command, "@party", partyId);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is null or DBNull)
            return null;
        var days = Convert.ToInt32(value, CultureInfo.InvariantCulture);
        return docDate.AddDays(Math.Max(days, 0));
    }

    public static bool TableExists(DbConnection connection, DbTransaction? transaction, string provider, string table)
    {
        var sql = IsPostgres(provider)
            ? "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = current_schema() AND table_name = @name"
            : "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name";
        using var command = Create(connection, transaction, sql);
        Add(command, "@name", table);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    public static bool ColumnExists(DbConnection connection, DbTransaction? transaction, string provider, string table, string column)
    {
        if (IsPostgres(provider))
        {
            using var command = Create(connection, transaction,
                "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = @table AND column_name = @column");
            Add(command, "@table", table);
            Add(command, "@column", column);
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
        }

        // SQLite: read the table's DDL from sqlite_master (table/column are
        // internal constants at every call site, never user input).
        using var ddl = Create(connection, transaction,
            "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = @table");
        Add(ddl, "@table", table);
        var sql = Convert.ToString(ddl.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
        return sql.Contains(column, StringComparison.OrdinalIgnoreCase);
    }

    private static DbCommand Create(DbConnection connection, DbTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        if (transaction is not null)
            command.Transaction = transaction;
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
}
