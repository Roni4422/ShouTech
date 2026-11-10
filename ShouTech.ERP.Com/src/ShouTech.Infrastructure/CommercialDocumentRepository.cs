using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.Infrastructure;

/// <summary>Provider-neutral persistence for sales documents and document chain links (SQLite and PostgreSQL).</summary>
public sealed class CommercialDocumentRepository : ICommercialDocumentRepository
{
    private readonly string _provider;
    private readonly string _connectionString;

    public CommercialDocumentRepository(string provider, string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("A database connection string is required.", nameof(connectionString));
        _provider = NormalizeProvider(provider);
        _connectionString = connectionString;
    }

    public async Task<CommercialDocumentSnapshot?> FindSalesByIdAsync(long docId, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        long foundId;
        Guid companyId;
        string docKind;
        string docNo;
        DateOnly docDate;
        long? partyId;
        string? partyName;
        long? warehouseId;
        string currencyCode;
        decimal exchangeRate;
        decimal subtotal;
        decimal discountAmount;
        decimal taxAmount;
        decimal grandTotal;
        decimal paidAmount;
        string status;
        string? notes;

        await using (var header = connection.CreateCommand())
        {
            header.CommandText = """
                SELECT doc_id, company_id, doc_kind, doc_no, doc_date, party_id, party_name, warehouse_id,
                       currency_code, exchange_rate, subtotal, discount_amount, tax_amount, grand_total,
                       paid_amount, status, notes
                FROM sales_documents
                WHERE doc_id = @docId AND is_deleted = 0
                """;
            Add(header, "@docId", docId);
            await using var reader = await header.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return null;

            foundId = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
            if (!Guid.TryParse(Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture), out companyId) || companyId == Guid.Empty)
                throw new InvalidOperationException("The saved sales document has an invalid company identifier.");
            docKind = reader.GetString(2);
            docNo = reader.GetString(3);
            docDate = DateOnly.Parse(Convert.ToString(reader.GetValue(4), CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture);
            partyId = reader.IsDBNull(5) ? null : Convert.ToInt64(reader.GetValue(5), CultureInfo.InvariantCulture);
            partyName = reader.IsDBNull(6) ? null : reader.GetString(6);
            warehouseId = reader.IsDBNull(7) ? null : Convert.ToInt64(reader.GetValue(7), CultureInfo.InvariantCulture);
            currencyCode = reader.GetString(8);
            exchangeRate = Convert.ToDecimal(reader.GetValue(9), CultureInfo.InvariantCulture);
            subtotal = Convert.ToDecimal(reader.GetValue(10), CultureInfo.InvariantCulture);
            discountAmount = Convert.ToDecimal(reader.GetValue(11), CultureInfo.InvariantCulture);
            taxAmount = Convert.ToDecimal(reader.GetValue(12), CultureInfo.InvariantCulture);
            grandTotal = Convert.ToDecimal(reader.GetValue(13), CultureInfo.InvariantCulture);
            paidAmount = Convert.ToDecimal(reader.GetValue(14), CultureInfo.InvariantCulture);
            status = reader.GetString(15);
            notes = reader.IsDBNull(16) ? null : reader.GetString(16);
        }

        var lines = new List<CommercialLineSnapshot>();
        await using (var lineCommand = connection.CreateCommand())
        {
            lineCommand.CommandText = """
                SELECT line_id, line_no, material_id, material_code, description, unit, qty, unit_price,
                       discount_pct, tax_rate, line_subtotal, line_tax, line_total, warehouse_id
                FROM sales_document_lines
                WHERE doc_id = @docId
                ORDER BY line_no
                """;
            Add(lineCommand, "@docId", docId);
            await using var reader = await lineCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                lines.Add(new CommercialLineSnapshot(
                    Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                    Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture),
                    reader.IsDBNull(2) ? null : Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    Convert.ToDecimal(reader.GetValue(6), CultureInfo.InvariantCulture),
                    Convert.ToDecimal(reader.GetValue(7), CultureInfo.InvariantCulture),
                    Convert.ToDecimal(reader.GetValue(8), CultureInfo.InvariantCulture),
                    Convert.ToDecimal(reader.GetValue(9), CultureInfo.InvariantCulture),
                    Convert.ToDecimal(reader.GetValue(10), CultureInfo.InvariantCulture),
                    Convert.ToDecimal(reader.GetValue(11), CultureInfo.InvariantCulture),
                    Convert.ToDecimal(reader.GetValue(12), CultureInfo.InvariantCulture),
                    reader.IsDBNull(13) ? null : Convert.ToInt64(reader.GetValue(13), CultureInfo.InvariantCulture)));
            }
        }

        return new CommercialDocumentSnapshot(
            foundId, companyId, docKind, docNo, docDate, partyId, partyName, warehouseId,
            currencyCode, exchangeRate, subtotal, discountAmount, taxAmount, grandTotal,
            paidAmount, status, notes, lines.AsReadOnly());
    }

    public async Task<long> CreateSalesDraftAsync(CommercialDocumentDraft draft, string userId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.CompanyId == Guid.Empty) throw new ArgumentException("Company is required.", nameof(draft));
        if (string.IsNullOrWhiteSpace(draft.DocKind)) throw new ArgumentException("Document kind is required.", nameof(draft));
        if (string.IsNullOrWhiteSpace(draft.DocNo)) throw new ArgumentException("Document number is required.", nameof(draft));
        if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("User is required.", nameof(userId));
        if (draft.Lines.Count == 0) throw new InvalidOperationException("A commercial document requires at least one line.");

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var docDateText = draft.DocDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            decimal subtotal = 0m, taxAmount = 0m, grandTotal = 0m;
            var computed = new List<(decimal Subtotal, decimal Tax, decimal Total)>();
            foreach (var line in draft.Lines)
            {
                if (line.Qty <= 0m) throw new InvalidOperationException("Document line quantities must be greater than zero.");
                var lineSubtotal = decimal.Round(line.Qty * line.UnitPrice * (1m - line.DiscountPct / 100m), 2);
                var lineTax = decimal.Round(lineSubtotal * line.TaxRate / 100m, 2);
                var lineTotal = lineSubtotal + lineTax;
                computed.Add((lineSubtotal, lineTax, lineTotal));
                subtotal += lineSubtotal;
                taxAmount += lineTax;
                grandTotal += lineTotal;
            }

            long docId;
            var insertHeader = CreateCommand(connection, transaction,
                "INSERT INTO sales_documents (company_id, doc_kind, doc_no, doc_date, party_id, party_name, warehouse_id, currency_code, exchange_rate, subtotal, discount_amount, tax_amount, grand_total, status, notes, created_by) " +
                "VALUES (@company, @kind, @no, @date, @party, @partyName, @warehouse, @currency, @rate, @subtotal, @discount, @tax, @total, @status, @notes, @actor)" +
                (_provider == "PostgreSQL" ? " RETURNING doc_id" : "; SELECT last_insert_rowid();"));
            Add(insertHeader, "@company", draft.CompanyId.ToString("D"));
            Add(insertHeader, "@kind", draft.DocKind.Trim());
            Add(insertHeader, "@no", draft.DocNo.Trim());
            Add(insertHeader, "@date", docDateText);
            Add(insertHeader, "@party", (object?)draft.PartyId ?? DBNull.Value);
            Add(insertHeader, "@partyName", (object?)draft.PartyName ?? DBNull.Value);
            Add(insertHeader, "@warehouse", (object?)draft.WarehouseId ?? DBNull.Value);
            Add(insertHeader, "@currency", string.IsNullOrWhiteSpace(draft.CurrencyCode) ? "SAR" : draft.CurrencyCode.Trim());
            Add(insertHeader, "@rate", draft.ExchangeRate <= 0m ? 1m : draft.ExchangeRate);
            Add(insertHeader, "@subtotal", decimal.Round(subtotal, 2));
            Add(insertHeader, "@discount", 0m);
            Add(insertHeader, "@tax", decimal.Round(taxAmount, 2));
            Add(insertHeader, "@total", decimal.Round(grandTotal, 2));
            Add(insertHeader, "@status", CommercialDocumentStatus.Draft);
            Add(insertHeader, "@notes", (object?)draft.Notes ?? DBNull.Value);
            Add(insertHeader, "@actor", userId.Trim());
            docId = Convert.ToInt64(await insertHeader.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);

            var lineNumber = 0;
            for (var index = 0; index < draft.Lines.Count; index++)
            {
                var line = draft.Lines[index];
                var totals = computed[index];
                await using var insertLine = CreateCommand(connection, transaction,
                    "INSERT INTO sales_document_lines (doc_id, line_no, material_id, material_code, description, unit, qty, unit_price, discount_pct, tax_rate, line_subtotal, line_tax, line_total, warehouse_id) " +
                    "VALUES (@doc, @line, @material, @materialCode, @description, @unit, @qty, @price, @discount, @taxRate, @subtotal, @tax, @total, @warehouse)");
                Add(insertLine, "@doc", docId);
                Add(insertLine, "@line", ++lineNumber);
                Add(insertLine, "@material", (object?)line.MaterialId ?? DBNull.Value);
                Add(insertLine, "@materialCode", (object?)line.MaterialCode ?? DBNull.Value);
                Add(insertLine, "@description", string.IsNullOrWhiteSpace(line.Description) ? line.MaterialCode ?? "Item" : line.Description.Trim());
                Add(insertLine, "@unit", (object?)line.Unit ?? DBNull.Value);
                Add(insertLine, "@qty", line.Qty);
                Add(insertLine, "@price", line.UnitPrice);
                Add(insertLine, "@discount", line.DiscountPct);
                Add(insertLine, "@taxRate", line.TaxRate);
                Add(insertLine, "@subtotal", totals.Subtotal);
                Add(insertLine, "@tax", totals.Tax);
                Add(insertLine, "@total", totals.Total);
                Add(insertLine, "@warehouse", (object?)line.WarehouseId ?? DBNull.Value);
                await insertLine.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return docId;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Posts a sales document and writes its accounting side effects atomically:
    /// - DLV (delivery note): one material_stock_movements row per stock line and a
    ///   matching decrement (or initialization) of material_stock_balances.
    /// - SAL (sales invoice): a party_ledger debit for the customer grand total.
    /// - QUO / SO: no side effects.
    /// </summary>
    public async Task PostSalesAsync(long docId, string userId, CancellationToken cancellationToken = default)
    {
        if (docId <= 0) throw new ArgumentException("Document is required.", nameof(docId));
        if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("User is required.", nameof(userId));

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string companyId, docKind, docNo, docDate, currencyCode, status;
            long? partyId;
            long? warehouseId;
            decimal grandTotal;

            await using (var header = CreateCommand(connection, transaction, """
                SELECT company_id, doc_kind, doc_no, doc_date, party_id, warehouse_id, currency_code, grand_total, status
                FROM sales_documents
                WHERE doc_id = @doc AND is_deleted = 0
                """))
            {
                Add(header, "@doc", docId);
                await using var reader = await header.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("Sales document was not found.");
                companyId = Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!;
                docKind = reader.GetString(1);
                docNo = reader.GetString(2);
                docDate = Convert.ToString(reader.GetValue(3), CultureInfo.InvariantCulture)!;
                partyId = reader.IsDBNull(4) ? null : Convert.ToInt64(reader.GetValue(4), CultureInfo.InvariantCulture);
                warehouseId = reader.IsDBNull(5) ? null : Convert.ToInt64(reader.GetValue(5), CultureInfo.InvariantCulture);
                currencyCode = reader.GetString(6);
                grandTotal = Convert.ToDecimal(reader.GetValue(7), CultureInfo.InvariantCulture);
                status = reader.GetString(8);
            }

            if (status != CommercialDocumentStatus.Draft)
                throw new InvalidOperationException("Sales document was not found or is not in draft status.");

            if (docKind == CommercialDocKinds.DeliveryNote)
            {
                var pending = new List<(long MaterialId, decimal Qty, long WarehouseId)>();
                await using (var lines = CreateCommand(connection, transaction,
                    "SELECT material_id, qty, warehouse_id FROM sales_document_lines WHERE doc_id = @doc ORDER BY line_no"))
                {
                    Add(lines, "@doc", docId);
                    await using var reader = await lines.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        if (reader.IsDBNull(0))
                            continue;
                        var lineWarehouse = reader.IsDBNull(2)
                            ? warehouseId ?? 0L
                            : Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture);
                        pending.Add((Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                                     Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture),
                                     lineWarehouse));
                    }
                }

                foreach (var (materialId, qty, lineWarehouse) in pending)
                {
                    await using var movement = CreateCommand(connection, transaction, """
                        INSERT INTO material_stock_movements
                            (company_id, warehouse_id, material_id, movement_date, movement_type, document_no, qty_in, qty_out, unit_cost, note)
                        VALUES (@company, @warehouse, @material, @date, @type, @docNo, 0, @qty, 0, @note)
                        """);
                    Add(movement, "@company", companyId);
                    Add(movement, "@warehouse", lineWarehouse);
                    Add(movement, "@material", materialId);
                    Add(movement, "@date", docDate);
                    Add(movement, "@type", "DLV");
                    Add(movement, "@docNo", docNo);
                    Add(movement, "@qty", qty);
                    Add(movement, "@note", $"Delivery note {docNo} posting");
                    await movement.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                    await using var balance = CreateCommand(connection, transaction, """
                        INSERT INTO material_stock_balances (company_id, warehouse_id, material_id, available_qty)
                        VALUES (@company, @warehouse, @material, -@qty)
                        ON CONFLICT (company_id, warehouse_id, material_id)
                        DO UPDATE SET available_qty = material_stock_balances.available_qty - @qty,
                                      last_updated = CURRENT_TIMESTAMP
                        """);
                    Add(balance, "@company", companyId);
                    Add(balance, "@warehouse", lineWarehouse);
                    Add(balance, "@material", materialId);
                    Add(balance, "@qty", qty);
                    await balance.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            else if (docKind == CommercialDocKinds.SalesInvoice && partyId is not null)
            {
                await using var ledger = CreateCommand(connection, transaction, """
                    INSERT INTO party_ledger
                        (company_id, party_kind, party_id, doc_kind, doc_id, doc_no, entry_date, debit, credit, currency_code, note)
                    VALUES (@company, 'C', @party, @kind, @doc, @docNo, @date, @debit, 0, @currency, @note)
                    """);
                Add(ledger, "@company", companyId);
                Add(ledger, "@party", partyId.Value);
                Add(ledger, "@kind", CommercialDocKinds.SalesInvoice);
                Add(ledger, "@doc", docId);
                Add(ledger, "@docNo", docNo);
                Add(ledger, "@date", docDate);
                Add(ledger, "@debit", grandTotal);
                Add(ledger, "@currency", currencyCode);
                Add(ledger, "@note", $"Sales invoice {docNo} posting");
                await ledger.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using var update = CreateCommand(connection, transaction,
                "UPDATE sales_documents SET status = @posted, posted_by = @actor, posted_at = CURRENT_TIMESTAMP WHERE doc_id = @doc AND status = @draft AND is_deleted = 0");
            Add(update, "@posted", CommercialDocumentStatus.Posted);
            Add(update, "@actor", userId.Trim());
            Add(update, "@doc", docId);
            Add(update, "@draft", CommercialDocumentStatus.Draft);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("Sales document was not found or is not in draft status.");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task AddLinkAsync(Guid companyId, string sourceDocKind, long sourceDocId, string targetDocKind, long targetDocId, string userId, CancellationToken cancellationToken = default)
    {
        if (companyId == Guid.Empty) throw new ArgumentException("Company is required.", nameof(companyId));
        if (string.IsNullOrWhiteSpace(sourceDocKind) || string.IsNullOrWhiteSpace(targetDocKind))
            throw new ArgumentException("Source and target document kinds are required.");
        if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("User is required.", nameof(userId));

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO document_links (company_id, source_doc_kind, source_doc_id, target_doc_kind, target_doc_id, created_by)
            VALUES (@company, @sourceKind, @sourceId, @targetKind, @targetId, @actor)
            """;
        Add(command, "@company", companyId.ToString("D"));
        Add(command, "@sourceKind", sourceDocKind.Trim());
        Add(command, "@sourceId", sourceDocId);
        Add(command, "@targetKind", targetDocKind.Trim());
        Add(command, "@targetId", targetDocId);
        Add(command, "@actor", userId.Trim());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CommercialDocumentLink>> GetSalesLinksFromAsync(Guid companyId, long sourceDocId, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT link_id, company_id, source_doc_kind, source_doc_id, target_doc_kind, target_doc_id
            FROM document_links
            WHERE company_id = @company AND source_doc_id = @sourceId
            ORDER BY link_id
            """;
        Add(command, "@company", companyId.ToString("D"));
        Add(command, "@sourceId", sourceDocId);

        var links = new List<CommercialDocumentLink>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            links.Add(new CommercialDocumentLink(
                Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                Guid.Parse(Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture)!),
                reader.GetString(2),
                Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture),
                reader.GetString(4),
                Convert.ToInt64(reader.GetValue(5), CultureInfo.InvariantCulture)));
        }
        return links.AsReadOnly();
    }

    public async Task<IReadOnlyDictionary<long, decimal>> GetFulfilledQuantitiesAsync(Guid companyId, long sourceDocId, string targetDocKind, CancellationToken cancellationToken = default)
    {
        if (companyId == Guid.Empty) throw new ArgumentException("Company is required.", nameof(companyId));
        if (string.IsNullOrWhiteSpace(targetDocKind)) throw new ArgumentException("Target document kind is required.", nameof(targetDocKind));

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT l.material_id, SUM(l.qty)
            FROM document_links dl
            JOIN sales_documents d
              ON d.company_id = dl.company_id
             AND d.doc_id = dl.target_doc_id
             AND d.doc_kind = dl.target_doc_kind
            JOIN sales_document_lines l
              ON l.doc_id = d.doc_id
            WHERE dl.company_id = @company
              AND dl.source_doc_id = @sourceId
              AND dl.target_doc_kind = @targetKind
              AND d.status = @posted
              AND d.is_deleted = 0
            GROUP BY l.material_id
            """;
        Add(command, "@company", companyId.ToString("D"));
        Add(command, "@sourceId", sourceDocId);
        Add(command, "@targetKind", targetDocKind.Trim());
        Add(command, "@posted", CommercialDocumentStatus.Posted);

        var quantities = new Dictionary<long, decimal>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.IsDBNull(0))
                continue;
            var materialId = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
            quantities[materialId] = Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture);
        }
        return quantities;
    }

    private DbConnection CreateConnection() => _provider == "SQLite"
        ? new SqliteConnection(_connectionString)
        : new NpgsqlConnection(_connectionString);

    private static DbCommand CreateCommand(DbConnection connection, DbTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
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

    private static string NormalizeProvider(string provider) => provider.Trim().ToLowerInvariant() switch
    {
        "sqlite" or "sqlite3" => "SQLite",
        "postgres" or "postgresql" or "npgsql" => "PostgreSQL",
        _ => throw new NotSupportedException($"Unsupported database provider '{provider}'. Use SQLite or PostgreSQL.")
    };
}
