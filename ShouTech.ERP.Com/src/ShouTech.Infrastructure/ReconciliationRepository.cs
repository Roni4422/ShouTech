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
/// </summary>
public sealed class ReconciliationRepository : IReconciliationRepository
{
    private const int ListLimit = 500;
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
        await using var command = CreateCommand(connection, null,
            $"""
            SELECT d.doc_id, d.doc_no, d.doc_date, d.party_id, d.party_name, d.currency_code, d.grand_total, d.paid_amount,
                   COALESCE((SELECT SUM(a.amount) FROM payment_allocations a
                             WHERE a.company_id = d.company_id AND a.doc_table = @docTable AND a.doc_id = d.doc_id), 0) AS allocated
            FROM {table} d
            WHERE d.company_id = @company AND d.{NotDeleted} AND d.status = 'Posted' AND d.party_id IS NOT NULL
            ORDER BY d.doc_date, d.doc_id
            LIMIT {ListLimit}
            """);
        Add(command, "@company", companyId.Trim());
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
                             WHERE a.company_id = l.company_id AND a.payment_ledger_id = l.ledger_id), 0) AS allocated
            FROM party_ledger l
            WHERE l.company_id = @company AND l.party_kind = @kind AND {sidePredicate}
            ORDER BY l.entry_date, l.ledger_id
            LIMIT {ListLimit}
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
                Allocated: ReadDecimal(reader, 7));
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

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 1) Re-read the payment inside the transaction.
            string paymentKind;
            long paymentParty;
            decimal paymentAmount;
            await using (var pay = CreateCommand(connection, transaction,
                "SELECT party_kind, party_id, debit, credit FROM party_ledger WHERE company_id = @company AND ledger_id = @id"))
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
            }
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
            long docParty;
            decimal openAmount;
            decimal docTotal, docPaid;
            string docStatus;
            await using (var doc = CreateCommand(connection, transaction,
                $"SELECT doc_no, party_id, grand_total, paid_amount, status FROM {table} WHERE company_id = @company AND doc_id = @doc AND {NotDeleted}"))
            {
                Add(doc, "@company", companyId.Trim());
                Add(doc, "@doc", docId);
                await using var reader = await doc.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("The document was not found for this company.");
                docNo = ReadString(reader, 0) ?? string.Empty;
                docParty = reader.IsDBNull(1) ? -1 : Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
                docTotal = ReadDecimal(reader, 2);
                docPaid = ReadDecimal(reader, 3);
                docStatus = ReadString(reader, 4) ?? string.Empty;
            }
            if (!string.Equals(docStatus, "Posted", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Only posted documents can be reconciled.");
            decimal docAllocated;
            await using (var sum = CreateCommand(connection, transaction,
                "SELECT COALESCE(SUM(amount), 0) FROM payment_allocations WHERE company_id = @company AND doc_table = @docTable AND doc_id = @doc"))
            {
                Add(sum, "@company", companyId.Trim());
                Add(sum, "@docTable", docTable);
                Add(sum, "@doc", docId);
                docAllocated = Convert.ToDecimal(await sum.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            }
            openAmount = docTotal - docPaid - docAllocated;
            if (docParty != paymentParty)
                throw new InvalidOperationException("The payment and the document belong to different parties.");
            if (amount > openAmount + Tolerance)
                throw new InvalidOperationException("Allocation exceeds the document's open amount.");

            // 3) Insert the allocation and settle it against the document.
            var allocationId = Guid.NewGuid();
            await using (var insert = CreateCommand(connection, transaction,
                "INSERT INTO payment_allocations (allocation_id, company_id, party_kind, party_id, payment_ledger_id, doc_table, doc_id, doc_no, amount, allocated_by, note) VALUES (@id, @company, @kind, @party, @payment, @docTable, @doc, @docNo, @amount, @user, @note)"))
            {
                Add(insert, "@id", FormatGuid(allocationId));
                Add(insert, "@company", companyId.Trim());
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
                $"UPDATE {table} SET paid_amount = paid_amount + @amount WHERE company_id = @company AND doc_id = @doc AND {NotDeleted}"))
            {
                Add(settle, "@amount", amount);
                Add(settle, "@company", companyId.Trim());
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
