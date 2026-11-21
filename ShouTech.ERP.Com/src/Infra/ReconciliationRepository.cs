using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.Infrastructure;

/// <summary>
/// Provider-neutral Payment Reconciliation persistence (SQLite + PostgreSQL).
///
/// Model: posting a sales document writes a DEBIT to party_ledger for the
/// customer; posting a purchase document writes a CREDIT for the supplier.
/// Money received from a customer is a CREDIT ledger entry; money paid to a
/// supplier is a DEBIT entry. An allocation links such a payment entry to an
/// open document and increases the document's paid_amount. Allocation runs in
/// ONE transaction and re-reads/re-validates the payment balance and the
/// document open amount inside it, so concurrent or double submissions cannot
/// over-allocate. All SQL is parameterized; the only composed identifiers are
/// the whitelisted document table names.
///
/// Step 4 discipline (sweep L-10, cash-payments MAJORs):
/// - Only genuine payment entries are allocatable: doc_kind RCV (cash
///   receipt / collected at invoice posting), PAY (cash payment) and INS
///   (installment collection). Return credits/debits (SRT/PRT) are NOT
///   payments and can no longer be allocated as if they were cash.
/// - Only real trade documents are settleable: SAL/POS on the sales side,
///   PUR on the purchase side — returns and stock kinds are excluded.
/// - No row caps: the lists are the full open sets (unlimited-rows rule);
///   settled rows are filtered in SQL, not after a LIMIT.
/// </summary>
public sealed class ReconciliationRepository : IReconciliationRepository
{
    private const decimal Tolerance = 0.005m;

    private readonly string _provider;
    private readonly string _connectionString;

    public ReconciliationRepository(string provider, string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("A database connection string is required.", nameof(connectionString));
        _provider = NormalizeProvider(provider);
        _connectionString = connectionString;
    }

    private string NotDeleted => _provider == "PostgreSQL" ? "is_deleted = false" : "is_deleted = 0";

    public async Task<IReadOnlyList<OpenDocumentRow>> GetOpenDocumentsAsync(string companyId, string partyKind, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        var table = DocumentTable(PartyKindToDocTable(partyKind));
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var documentCompanyKey = await ResolveDocumentCompanyKeyAsync(
            connection, null, companyId, cancellationToken).ConfigureAwait(false);
        var kindFilter = PartyKindToDocTable(partyKind) == "SALES"
            ? "d.doc_kind IN ('SAL', 'POS')"
            : "d.doc_kind IN ('PUR')";
        await using var command = CreateCommand(connection, null,
            $"""
            SELECT d.doc_id, d.doc_no, d.doc_date, d.party_id, d.party_name, d.currency_code, d.grand_total, d.paid_amount,
                   COALESCE((SELECT SUM(a.amount) FROM payment_allocations a
                             WHERE a.company_id = @company AND a.doc_table = @docTable AND a.doc_id = d.doc_id
                               AND (a.document_company_id = CAST(d.company_id AS TEXT)
                                    OR (a.document_company_id IS NULL AND a.company_id = CAST(d.company_id AS TEXT)))), 0) AS allocated
            FROM {table} d
            WHERE CAST(d.company_id AS TEXT) IN (@company, @documentCompany)
              AND d.{NotDeleted} AND d.status = 'Posted' AND d.party_id IS NOT NULL
              AND {kindFilter} AND d.grand_total - d.paid_amount > 0.005
            ORDER BY d.doc_date, d.doc_id
            """);
        Add(command, "@company", companyId.Trim());
        Add(command, "@documentCompany", documentCompanyKey);
        Add(command, "@docTable", PartyKindToDocTable(partyKind));
        var result = new List<OpenDocumentRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = new OpenDocumentRow(
                DocId: Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                DocTable: PartyKindToDocTable(partyKind),
                DocNo: ReadString(reader, 1) ?? string.Empty,
                DocDate: ReadDate(reader, 2) ?? DateOnly.FromDateTime(DateTime.Today),
                PartyId: Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture),
                PartyName: ReadString(reader, 4),
                CurrencyCode: ReadString(reader, 5) ?? "SAR",
                Total: ReadDecimal(reader, 6),
                Paid: ReadDecimal(reader, 7),
                Allocated: ReadDecimal(reader, 8));
            if (row.OpenAmount > Tolerance)
                result.Add(row);
        }
        return result;
    }

    public async Task<IReadOnlyList<UnallocatedPaymentRow>> GetPaymentsAsync(string companyId, string partyKind, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        RequirePartyKind(partyKind);
        // Customers settle with credit entries; suppliers with debit entries.
        var sidePredicate = partyKind == "C" ? "l.credit > 0" : "l.debit > 0";
        var amountExpr = partyKind == "C" ? "l.credit" : "l.debit";
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            $"""
            SELECT l.ledger_id, l.entry_date, l.doc_kind, l.doc_no, l.party_id, l.note, {amountExpr} AS amount,
                   COALESCE((SELECT SUM(a.amount) FROM payment_allocations a
                             WHERE a.company_id = l.company_id AND a.payment_ledger_id = l.ledger_id), 0) AS allocated,
                   COALESCE(NULLIF(p.name_en, ''), p.name_ar, '') AS party_name,
                   COALESCE(l.currency_code, 'SAR') AS currency_code
            FROM party_ledger l
            LEFT JOIN parties p
              ON p.company_id = l.company_id AND p.kind = l.party_kind AND p.ledger_id = l.party_id
            WHERE l.company_id = @company AND l.party_kind = @kind AND {sidePredicate}
              AND l.doc_kind IN ('RCV', 'PAY', 'INS')
            ORDER BY l.entry_date, l.ledger_id
            """);
        Add(command, "@company", companyId.Trim());
        Add(command, "@kind", partyKind);
        var result = new List<UnallocatedPaymentRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = new UnallocatedPaymentRow(
                LedgerId: Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                EntryDate: ReadDate(reader, 1) ?? DateOnly.FromDateTime(DateTime.Today),
                DocKind: ReadString(reader, 2),
                DocNo: ReadString(reader, 3),
                PartyId: Convert.ToInt64(reader.GetValue(4), CultureInfo.InvariantCulture),
                Note: ReadString(reader, 5),
                Amount: ReadDecimal(reader, 6),
                Allocated: ReadDecimal(reader, 7),
                PartyName: ReadString(reader, 8),
                CurrencyCode: ReadString(reader, 9) ?? "SAR");
            if (row.Available > Tolerance)
                result.Add(row);
        }
        return result;
    }

    public async Task<Guid> AllocateAsync(string companyId, string partyKind, long paymentLedgerId, string docTable, long docId, decimal amount, string userId, string? note = null, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        RequirePartyKind(partyKind);
        var expectedDocTable = PartyKindToDocTable(partyKind);
        if (!string.Equals(docTable, expectedDocTable, StringComparison.Ordinal))
            throw new InvalidOperationException($"Documents of table '{docTable}' cannot be reconciled against party kind '{partyKind}'.");
        if (amount <= 0)
            throw new InvalidOperationException("Allocation amount must be greater than zero.");
        var table = DocumentTable(docTable);
        var invoiceKindFilter = docTable == "SALES" ? "doc_kind IN ('SAL', 'POS')" : "doc_kind = 'PUR'";

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var documentCompanyKey = await ResolveDocumentCompanyKeyAsync(
                connection, transaction, companyId, cancellationToken).ConfigureAwait(false);

            // 1) Re-read the payment inside the transaction.
            string paymentKind;
            long paymentParty;
            decimal paymentAmount;
            string? paymentDocKind;
            string paymentCurrency;
            await using (var pay = CreateCommand(connection, transaction,
                "SELECT party_kind, party_id, debit, credit, doc_kind, COALESCE(currency_code, 'SAR') FROM party_ledger WHERE company_id = @company AND ledger_id = @id"
                + (_provider == "PostgreSQL" ? " FOR UPDATE" : "")))
            {
                Add(pay, "@company", companyId.Trim());
                Add(pay, "@id", paymentLedgerId);
                await using var reader = await pay.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("The payment entry was not found for this company.");
                paymentKind = ReadString(reader, 0) ?? string.Empty;
                paymentParty = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
                var debit = ReadDecimal(reader, 2);
                var credit = ReadDecimal(reader, 3);
                paymentAmount = partyKind == "C" ? credit : debit;
                paymentDocKind = ReadString(reader, 4);
                paymentCurrency = ReadString(reader, 5) ?? "SAR";
            }
            // Step 4 (L-10): returns and other non-payment entries are not
            // allocatable payments, even if they sit on the payment side.
            if (paymentDocKind is not ("RCV" or "PAY" or "INS"))
                throw new InvalidOperationException("The selected ledger entry is not a payment (only receipt, payment and installment-collection entries can be allocated).");
            if (!string.Equals(paymentKind, partyKind, StringComparison.Ordinal))
                throw new InvalidOperationException("The payment entry belongs to a different party kind.");
            if (paymentAmount <= 0)
                throw new InvalidOperationException("The selected ledger entry is not a payment on the expected side.");

            decimal paymentAllocated;
            await using (var sum = CreateCommand(connection, transaction,
                "SELECT COALESCE(SUM(amount), 0) FROM payment_allocations WHERE company_id = @company AND payment_ledger_id = @id"))
            {
                Add(sum, "@company", companyId.Trim());
                Add(sum, "@id", paymentLedgerId);
                paymentAllocated = Convert.ToDecimal(await sum.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            }
            if (amount > paymentAmount - paymentAllocated + Tolerance)
                throw new InvalidOperationException("Allocation exceeds the unallocated remainder of this payment.");

            // 2) Re-read the document inside the transaction.
            string docNo;
            string actualDocumentCompanyKey;
            long docParty;
            decimal openAmount;
            decimal docTotal, docPaid;
            string docStatus;
            string documentCurrency;
            await using (var doc = CreateCommand(connection, transaction,
                $"SELECT CAST(company_id AS TEXT), doc_no, party_id, grand_total, paid_amount, status, COALESCE(currency_code, 'SAR') FROM {table} WHERE CAST(company_id AS TEXT) IN (@company, @documentCompany) AND doc_id = @doc AND {invoiceKindFilter} AND {NotDeleted}"
                + (_provider == "PostgreSQL" ? " FOR UPDATE" : "")))
            {
                Add(doc, "@company", companyId.Trim());
                Add(doc, "@documentCompany", documentCompanyKey);
                Add(doc, "@doc", docId);
                await using var reader = await doc.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("The document was not found for this company.");
                actualDocumentCompanyKey = ReadString(reader, 0) ?? string.Empty;
                docNo = ReadString(reader, 1) ?? string.Empty;
                docParty = reader.IsDBNull(2) ? -1 : Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture);
                docTotal = ReadDecimal(reader, 3);
                docPaid = ReadDecimal(reader, 4);
                docStatus = ReadString(reader, 5) ?? string.Empty;
                documentCurrency = ReadString(reader, 6) ?? "SAR";
            }
            if (!string.Equals(docStatus, "Posted", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Only posted documents can be reconciled.");
            // paid_amount already includes every prior allocation (this same
            // transaction maintains it), so it is the document's settled total.
            openAmount = docTotal - docPaid;
            if (docParty != paymentParty)
                throw new InvalidOperationException("The payment and the document belong to different parties.");
            if (!string.Equals(paymentCurrency.Trim(), documentCurrency.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"The payment currency '{paymentCurrency}' does not match the document currency '{documentCurrency}'. Convert the funds or use a payment in the document currency before allocating.");
            if (amount > openAmount + Tolerance)
                throw new InvalidOperationException("Allocation exceeds the document's open amount.");

            // 3) Insert the allocation and settle it against the document.
            var allocationId = Guid.NewGuid();
            await using (var insert = CreateCommand(connection, transaction,
                "INSERT INTO payment_allocations (allocation_id, company_id, document_company_id, party_kind, party_id, payment_ledger_id, doc_table, doc_id, doc_no, amount, allocated_by, note) VALUES (@id, @company, @documentCompany, @kind, @party, @payment, @docTable, @doc, @docNo, @amount, @user, @note)"))
            {
                Add(insert, "@id", FormatGuid(allocationId));
                Add(insert, "@company", companyId.Trim());
                Add(insert, "@documentCompany", actualDocumentCompanyKey);
                Add(insert, "@kind", partyKind);
                Add(insert, "@party", paymentParty);
                Add(insert, "@payment", paymentLedgerId);
                Add(insert, "@docTable", docTable);
                Add(insert, "@doc", docId);
                Add(insert, "@docNo", docNo);
                Add(insert, "@amount", amount);
                Add(insert, "@user", userId);
                Add(insert, "@note", (object?)NullIfBlank(note) ?? DBNull.Value);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var settle = CreateCommand(connection, transaction,
                $"UPDATE {table} SET paid_amount = paid_amount + @amount WHERE CAST(company_id AS TEXT) = @documentCompany AND doc_id = @doc AND {NotDeleted}"))
            {
                Add(settle, "@amount", amount);
                Add(settle, "@documentCompany", actualDocumentCompanyKey);
                Add(settle, "@doc", docId);
                if (await settle.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new InvalidOperationException("The document could not be settled (missing or deleted).");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return allocationId;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<string> ResolveDocumentCompanyKeyAsync(
        DbConnection connection, DbTransaction? transaction, string companyId,
        CancellationToken cancellationToken)
    {
        if (Guid.TryParse(companyId, out var suppliedGuid))
            return suppliedGuid.ToString("D");

        await using var command = CreateCommand(connection, transaction,
            "SELECT company_id FROM companies WHERE company_code = @code");
        Add(command, "@code", companyId.Trim());
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is null or DBNull)
            throw new InvalidOperationException($"Company '{companyId.Trim()}' could not be resolved to its operational document key.");
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? throw new InvalidOperationException(
            $"Company '{companyId.Trim()}' has an invalid operational document key.");
    }

    private static string PartyKindToDocTable(string partyKind) => partyKind switch
    {
        "C" => "SALES",
        "S" => "PURCHASE",
        _ => throw new InvalidOperationException($"Unsupported party kind '{partyKind}'. Use 'C' or 'S'.")
    };

    private static string DocumentTable(string docTable) => docTable switch
    {
        "SALES" => "sales_documents",
        "PURCHASE" => "purchase_documents",
        _ => throw new InvalidOperationException($"Unsupported document table '{docTable}'.")
    };

    private static void RequireCompany(string companyId)
    {
        if (string.IsNullOrWhiteSpace(companyId))
            throw new ArgumentException("Company is required.", nameof(companyId));
    }

    private static void RequirePartyKind(string partyKind)
    {
        if (partyKind is not ("C" or "S"))
            throw new ArgumentException("Party kind must be 'C' (customer) or 'S' (supplier).", nameof(partyKind));
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private DbConnection CreateConnection() => _provider == "SQLite"
        ? new SqliteConnection(_connectionString)
        : new NpgsqlConnection(_connectionString);

    private static DbCommand CreateCommand(DbConnection connection, DbTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        if (transaction != null)
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

    private object FormatGuid(Guid value) => _provider == "PostgreSQL" ? value : value.ToString();

    private static string? ReadString(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static decimal ReadDecimal(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? 0m : Convert.ToDecimal(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static DateOnly? ReadDate(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return null;
        var value = reader.GetValue(ordinal);
        return value switch
        {
            DateOnly d => d,
            DateTime dt => DateOnly.FromDateTime(dt),
            string s when s.Length >= 10 && DateOnly.TryParse(s[..10], CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    private static string NormalizeProvider(string provider) => provider.Trim().ToLowerInvariant() switch
    {
        "sqlite" or "sqlite3" => "SQLite",
        "postgres" or "postgresql" or "npgsql" => "PostgreSQL",
        _ => throw new NotSupportedException($"Unsupported database provider '{provider}'. Use SQLite or PostgreSQL.")
    };
}
