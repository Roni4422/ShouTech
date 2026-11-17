using System.Data.Common;
using System.Globalization;

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
        string? journalEntryId = null)
    {
        // journal_entry_id exists from migration 061; hand-built fixtures
        // predating it keep working with the original column list.
        var hasJournal = ColumnExists(connection, transaction, provider, "party_ledger", "journal_entry_id");
        var columns = hasJournal
            ? "company_id, party_kind, party_id, doc_kind, doc_id, doc_no, entry_date, debit, credit, currency_code, note, journal_entry_id"
            : "company_id, party_kind, party_id, doc_kind, doc_id, doc_no, entry_date, debit, credit, currency_code, note";
        var values = hasJournal
            ? "@company, @kind, @party, @docKind, @doc, @docNo, @date, @debit, @credit, @currency, @note, @journal"
            : "@company, @kind, @party, @docKind, @doc, @docNo, @date, @debit, @credit, @currency, @note";
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
        Add(command, "@journal", (object?)journalEntryId ?? DBNull.Value);
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
        Guid companyGuid, Guid originalEntryId, Guid reversalEntryId, string reversalDate)
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
        var originals = new List<(string Company, string Kind, long Party, string? DocKind, long? DocId, string? DocNo, string Date, decimal Debit, decimal Credit, string Currency, string? Note)>();
        using (var load = Create(connection, transaction,
            "SELECT company_id, party_kind, party_id, doc_kind, doc_id, doc_no, entry_date, debit, credit, currency_code, note FROM party_ledger WHERE journal_entry_id = @journal"))
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
                    reader.IsDBNull(10) ? null : Convert.ToString(reader.GetValue(10), CultureInfo.InvariantCulture)));
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
            WriteEntry(connection, transaction, provider, company, row.Kind, row.Party,
                row.DocKind, row.DocId, row.DocNo,
                reversalDate,
                row.Credit, row.Debit, row.Currency,
                $"Reversal of journal settlement {originalEntryId:D}",
                reversalEntryId.ToString("D"));
        }
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
