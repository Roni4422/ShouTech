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

/// <summary>
/// Provider-neutral order persistence over the commercial document engine
/// (SQLite + PostgreSQL). Mirrors CommercialDocumentRepository conventions:
/// parameterized SQL only, company scoping, computed totals, and
/// single-transaction writes. Table names come from a fixed allow-list
/// keyed by <see cref="CommercialDocSide"/> — never from user input.
/// </summary>
public sealed class OrderDocumentRepository : IOrderDocumentRepository
{
    private readonly string _provider;
    private readonly string _connectionString;

    public OrderDocumentRepository(string provider, string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("A database connection string is required.", nameof(connectionString));
        _provider = NormalizeProvider(provider);
        _connectionString = connectionString;
    }

    private static (string Docs, string Lines) Tables(CommercialDocSide side) => side == CommercialDocSide.Sales
        ? ("sales_documents", "sales_document_lines")
        : ("purchase_documents", "purchase_document_lines");

    private static string PrefixFor(string docKind) => docKind switch
    {
        CommercialDocKinds.SalesQuotation => "QUO",
        CommercialDocKinds.SalesOrder => "SO",
        CommercialDocKinds.DeliveryNote => "DLV",
        CommercialDocKinds.SalesInvoice => "SAL",
        CommercialDocKinds.RequestForQuotation => "RFQ",
        CommercialDocKinds.PurchaseOrder => "PO",
        CommercialDocKinds.PurchaseReceipt => "RCV",
        CommercialDocKinds.PurchaseInvoice => "PUR",
        _ => "DOC"
    };

    // ------------------------------------------------------------------ read

    public async Task<CommercialDocumentSnapshot?> FindByIdAsync(CommercialDocSide side, long docId, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadSnapshotAsync(connection, null, side, docId, null, null, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CommercialDocumentSnapshot?> FindByNumberAsync(CommercialDocSide side, Guid companyId, string docKind, string docNo, CancellationToken cancellationToken = default)
    {
        if (companyId == Guid.Empty) throw new ArgumentException("Company is required.", nameof(companyId));
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadSnapshotAsync(connection, null, side, null, companyId, docKind, docNo, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CommercialDocumentSnapshot?> ReadSnapshotAsync(
        DbConnection connection, DbTransaction? transaction, CommercialDocSide side,
        long? docId, Guid? companyId, string? docKind, string? docNo, CancellationToken cancellationToken)
    {
        var (docs, lines) = Tables(side);
        long foundId;
        Guid foundCompany;
        string foundKind, foundNo, currencyCode, status;
        DateOnly docDate;
        long? partyId, warehouseId;
        string? partyName, notes, partyCode, branchName, salesRepName, costCenter, warehouseName;
        decimal exchangeRate, subtotal, discountAmount, taxAmount, grandTotal, paidAmount;
        DateOnly? deliveryDate, validTill;

        await using (var header = CreateCommand(connection, transaction, $"""
            SELECT doc_id, company_id, doc_kind, doc_no, doc_date, party_id, party_name, warehouse_id,
                   currency_code, exchange_rate, subtotal, discount_amount, tax_amount, grand_total,
                   paid_amount, status, notes, party_code, branch_name, sales_rep_name, cost_center,
                   warehouse_name, delivery_date, valid_till
            FROM {docs}
            WHERE is_deleted = 0
              AND (@docId IS NULL OR doc_id = @docId)
              AND (@company IS NULL OR company_id = @company)
              AND (@kind IS NULL OR doc_kind = @kind)
              AND (@no IS NULL OR doc_no = @no)
            """))
        {
            Add(header, "@docId", (object?)docId ?? DBNull.Value);
            Add(header, "@company", (object?)companyId?.ToString("D") ?? DBNull.Value);
            Add(header, "@kind", (object?)docKind ?? DBNull.Value);
            Add(header, "@no", (object?)docNo ?? DBNull.Value);
            await using var reader = await header.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return null;

            foundId = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
            if (!Guid.TryParse(Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture), out foundCompany) || foundCompany == Guid.Empty)
                throw new InvalidOperationException("The saved document has an invalid company identifier.");
            foundKind = reader.GetString(2);
            foundNo = reader.GetString(3);
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
            partyCode = reader.IsDBNull(17) ? null : reader.GetString(17);
            branchName = reader.IsDBNull(18) ? null : reader.GetString(18);
            salesRepName = reader.IsDBNull(19) ? null : reader.GetString(19);
            costCenter = reader.IsDBNull(20) ? null : reader.GetString(20);
            warehouseName = reader.IsDBNull(21) ? null : reader.GetString(21);
            deliveryDate = reader.IsDBNull(22) ? null : DateOnly.Parse(Convert.ToString(reader.GetValue(22), CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture);
            validTill = reader.IsDBNull(23) ? null : DateOnly.Parse(Convert.ToString(reader.GetValue(23), CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture);
        }

        var lineList = new List<CommercialLineSnapshot>();
        await using (var lineCommand = CreateCommand(connection, transaction, $"""
            SELECT line_id, line_no, material_id, material_code, description, unit, qty, unit_price,
                   discount_pct, tax_rate, line_subtotal, line_tax, line_total, warehouse_id
            FROM {lines}
            WHERE doc_id = @docId
            ORDER BY line_no
            """))
        {
            Add(lineCommand, "@docId", foundId);
            await using var reader = await lineCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                lineList.Add(new CommercialLineSnapshot(
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
            foundId, foundCompany, foundKind, foundNo, docDate, partyId, partyName, warehouseId,
            currencyCode, exchangeRate, subtotal, discountAmount, taxAmount, grandTotal,
            paidAmount, status, notes, lineList.AsReadOnly(),
            partyCode, branchName, salesRepName, costCenter, warehouseName, deliveryDate, validTill);
    }

    // ----------------------------------------------------------------- write

    public async Task<long> CreateDraftAsync(CommercialDocSide side, CommercialDocumentDraft draft, string userId, CancellationToken cancellationToken = default)
    {
        ValidateDraft(draft, userId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var docId = await InsertDocumentAsync(connection, transaction, side, draft, CommercialDocumentStatus.Draft, userId, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return docId;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task UpdateDraftAsync(CommercialDocSide side, long docId, CommercialDocumentDraft draft, string userId, CancellationToken cancellationToken = default)
    {
        ValidateDraft(draft, userId);
        var (docs, lines) = Tables(side);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await ReadSnapshotAsync(connection, transaction, side, docId, null, null, null, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Document was not found.");
            if (existing.Status != CommercialDocumentStatus.Draft)
                throw new InvalidOperationException($"Only draft documents can be edited; document '{existing.DocNo}' is '{existing.Status}'.");
            if (existing.CompanyId != draft.CompanyId)
                throw new InvalidOperationException("Document belongs to a different company.");

            var computed = ComputeTotals(draft.Lines, out var subtotal, out var taxAmount, out var grandTotal);

            await using (var update = CreateCommand(connection, transaction, $"""
                UPDATE {docs}
                SET doc_date = @date, party_id = @party, party_name = @partyName, party_code = @partyCode,
                    warehouse_id = @warehouse, warehouse_name = @warehouseName, currency_code = @currency,
                    exchange_rate = @rate, subtotal = @subtotal, tax_amount = @tax, grand_total = @total,
                    notes = @notes, delivery_date = @deliveryDate, valid_till = @validTill,
                    cost_center = @costCenter, branch_name = @branchName, sales_rep_name = @salesRepName
                WHERE doc_id = @doc AND status = @draft AND is_deleted = 0
                """))
            {
                Add(update, "@date", draft.DocDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                Add(update, "@party", (object?)draft.PartyId ?? DBNull.Value);
                Add(update, "@partyName", (object?)draft.PartyName ?? DBNull.Value);
                Add(update, "@partyCode", (object?)draft.PartyCode ?? DBNull.Value);
                Add(update, "@warehouse", (object?)draft.WarehouseId ?? DBNull.Value);
                Add(update, "@warehouseName", (object?)draft.WarehouseName ?? DBNull.Value);
                Add(update, "@currency", string.IsNullOrWhiteSpace(draft.CurrencyCode) ? "SAR" : draft.CurrencyCode.Trim());
                Add(update, "@rate", draft.ExchangeRate <= 0m ? 1m : draft.ExchangeRate);
                Add(update, "@subtotal", subtotal);
                Add(update, "@tax", taxAmount);
                Add(update, "@total", grandTotal);
                Add(update, "@notes", (object?)draft.Notes ?? DBNull.Value);
                Add(update, "@deliveryDate", (object?)draft.DeliveryDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? DBNull.Value);
                Add(update, "@validTill", (object?)draft.ValidTill?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? DBNull.Value);
                Add(update, "@costCenter", (object?)draft.CostCenter ?? DBNull.Value);
                Add(update, "@branchName", (object?)draft.BranchName ?? DBNull.Value);
                Add(update, "@salesRepName", (object?)draft.SalesRepName ?? DBNull.Value);
                Add(update, "@doc", docId);
                Add(update, "@draft", CommercialDocumentStatus.Draft);
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new InvalidOperationException("Document was not found or is not in draft status.");
            }

            await using (var deleteLines = CreateCommand(connection, transaction, $"DELETE FROM {lines} WHERE doc_id = @doc"))
            {
                Add(deleteLines, "@doc", docId);
                await deleteLines.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await InsertLinesAsync(connection, transaction, side, docId, draft.Lines, computed, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task PostOrderAsync(CommercialDocSide side, long docId, string userId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("User is required.", nameof(userId));
        var (docs, _) = Tables(side);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string kind, status;
            await using (var read = CreateCommand(connection, transaction, $"SELECT doc_kind, status FROM {docs} WHERE doc_id = @doc AND is_deleted = 0"))
            {
                Add(read, "@doc", docId);
                await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("Document was not found.");
                kind = reader.GetString(0);
                status = reader.GetString(1);
            }

            var orderKinds = side == CommercialDocSide.Sales
                ? new[] { CommercialDocKinds.SalesOrder, CommercialDocKinds.SalesQuotation }
                : new[] { CommercialDocKinds.PurchaseOrder, CommercialDocKinds.RequestForQuotation };
            if (Array.IndexOf(orderKinds, kind) < 0)
                throw new InvalidOperationException($"Document kind '{kind}' is not an order kind; use the invoice posting path.");
            if (status != CommercialDocumentStatus.Draft)
                throw new InvalidOperationException($"Only draft orders can be posted; this document is '{status}'.");

            await using var update = CreateCommand(connection, transaction,
                $"UPDATE {docs} SET status = @posted, posted_by = @actor, posted_at = CURRENT_TIMESTAMP WHERE doc_id = @doc AND status = @draft AND is_deleted = 0");
            Add(update, "@posted", CommercialDocumentStatus.Posted);
            Add(update, "@actor", userId.Trim());
            Add(update, "@doc", docId);
            Add(update, "@draft", CommercialDocumentStatus.Draft);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("Document was not found or is not in draft status.");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task SetStatusAsync(CommercialDocSide side, long docId, string newStatus, string userId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("User is required.", nameof(userId));
        var allowed = newStatus switch
        {
            CommercialDocumentStatus.Closed => new[] { CommercialDocumentStatus.Posted },
            CommercialDocumentStatus.Cancelled => new[] { CommercialDocumentStatus.Draft, CommercialDocumentStatus.Posted },
            CommercialDocumentStatus.Lost => new[] { CommercialDocumentStatus.Draft, CommercialDocumentStatus.Posted },
            _ => throw new ArgumentException($"Status '{newStatus}' cannot be set directly.", nameof(newStatus))
        };
        var (docs, _) = Tables(side);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string status, kind;
            await using (var read = CreateCommand(connection, transaction, $"SELECT status, doc_kind FROM {docs} WHERE doc_id = @doc AND is_deleted = 0"))
            {
                Add(read, "@doc", docId);
                await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("Document was not found.");
                status = reader.GetString(0);
                kind = reader.GetString(1);
            }
            if (newStatus == CommercialDocumentStatus.Lost && kind != CommercialDocKinds.SalesQuotation)
                throw new InvalidOperationException("Only quotations can be marked as lost.");
            if (Array.IndexOf(allowed, status) < 0)
                throw new InvalidOperationException($"A document in status '{status}' cannot move to '{newStatus}'.");

            await using var update = CreateCommand(connection, transaction,
                $"UPDATE {docs} SET status = @new WHERE doc_id = @doc AND status = @old AND is_deleted = 0");
            Add(update, "@new", newStatus);
            Add(update, "@doc", docId);
            Add(update, "@old", status);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("Document status changed concurrently; reload and retry.");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<string> NextDocNoAsync(CommercialDocSide side, Guid companyId, string docKind, CancellationToken cancellationToken = default)
    {
        if (companyId == Guid.Empty) throw new ArgumentException("Company is required.", nameof(companyId));
        var (docs, _) = Tables(side);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT doc_no FROM {docs} WHERE company_id = @company AND doc_kind = @kind";
        Add(command, "@company", companyId.ToString("D"));
        Add(command, "@kind", docKind.Trim());
        var max = 1000;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var text = reader.GetString(0);
            var dash = text.LastIndexOf('-');
            var suffix = dash >= 0 ? text[(dash + 1)..] : text;
            if (int.TryParse(suffix, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > max)
                max = value;
        }
        return $"{PrefixFor(docKind)}-{max + 1}";
    }

    public async Task<IReadOnlyList<CommercialDocumentSummary>> ListAsync(CommercialDocSide side, Guid companyId, string? docKind, string? status, string? search, DateOnly? from, DateOnly? to, int limit = 500, CancellationToken cancellationToken = default)
    {
        if (companyId == Guid.Empty) throw new ArgumentException("Company is required.", nameof(companyId));
        var (docs, _) = Tables(side);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT doc_id, doc_kind, doc_no, doc_date, party_code, party_name, grand_total, status, delivery_date, valid_till
            FROM {docs}
            WHERE company_id = @company AND is_deleted = 0
              AND (@kind IS NULL OR doc_kind = @kind)
              AND (@status IS NULL OR status = @status)
              AND (@from IS NULL OR doc_date >= @from)
              AND (@to IS NULL OR doc_date <= @to)
              AND (@search IS NULL OR doc_no LIKE @searchLike OR party_name LIKE @searchLike OR party_code LIKE @searchLike)
            ORDER BY doc_date DESC, doc_id DESC
            LIMIT @limit
            """;
        Add(command, "@company", companyId.ToString("D"));
        Add(command, "@kind", (object?)docKind ?? DBNull.Value);
        Add(command, "@status", (object?)status ?? DBNull.Value);
        Add(command, "@from", (object?)from?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? DBNull.Value);
        Add(command, "@to", (object?)to?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? DBNull.Value);
        Add(command, "@search", (object?)search ?? DBNull.Value);
        Add(command, "@searchLike", string.IsNullOrWhiteSpace(search) ? DBNull.Value : $"%{search.Trim()}%");
        Add(command, "@limit", Math.Clamp(limit, 1, 5000));

        var results = new List<CommercialDocumentSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new CommercialDocumentSummary(
                Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                reader.GetString(1),
                reader.GetString(2),
                DateOnly.Parse(Convert.ToString(reader.GetValue(3), CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                Convert.ToDecimal(reader.GetValue(6), CultureInfo.InvariantCulture),
                reader.GetString(7),
                reader.IsDBNull(8) ? null : DateOnly.Parse(Convert.ToString(reader.GetValue(8), CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture),
                reader.IsDBNull(9) ? null : DateOnly.Parse(Convert.ToString(reader.GetValue(9), CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture)));
        }
        return results.AsReadOnly();
    }

    // -------------------------------------------------------------- progress

    public async Task<OrderProgress> GetProgressAsync(CommercialDocSide side, long docId, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var order = await ReadSnapshotAsync(connection, null, side, docId, null, null, null, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Order was not found.");

        var fulfillKind = side == CommercialDocSide.Sales ? CommercialDocKinds.DeliveryNote : CommercialDocKinds.PurchaseReceipt;
        var billedKind = side == CommercialDocSide.Sales ? CommercialDocKinds.SalesInvoice : CommercialDocKinds.PurchaseInvoice;
        var fulfilled = await LinkedQuantitiesAsync(connection, null, side, order.CompanyId, docId, fulfillKind, cancellationToken).ConfigureAwait(false);
        var billed = await LinkedQuantitiesAsync(connection, null, side, order.CompanyId, docId, billedKind, cancellationToken).ConfigureAwait(false);

        var lines = new List<OrderProgressLine>();
        decimal totalOrdered = 0m, totalFulfilled = 0m, totalBilled = 0m;
        foreach (var line in order.Lines)
        {
            var key = line.MaterialId ?? -line.LineNo;
            var f = fulfilled.TryGetValue(key, out var fv) ? fv : 0m;
            var b = billed.TryGetValue(key, out var bv) ? bv : 0m;
            lines.Add(new OrderProgressLine(line.MaterialId, line.MaterialCode, line.Description, line.Qty, f, b));
            totalOrdered += line.Qty;
            totalFulfilled += Math.Min(f, line.Qty);
            totalBilled += Math.Min(b, line.Qty);
        }
        var perFulfilled = totalOrdered <= 0m ? 0m : decimal.Round(100m * totalFulfilled / totalOrdered, 2);
        var perBilled = totalOrdered <= 0m ? 0m : decimal.Round(100m * totalBilled / totalOrdered, 2);
        return new OrderProgress(order.DocId, order.DocKind, order.DocNo, lines.AsReadOnly(), perFulfilled, perBilled);
    }

    private async Task<IReadOnlyDictionary<long, decimal>> LinkedQuantitiesAsync(
        DbConnection connection, DbTransaction? transaction, CommercialDocSide side,
        Guid companyId, long sourceDocId, string targetDocKind, CancellationToken cancellationToken)
    {
        var (docs, lines) = Tables(side);
        await using var command = CreateCommand(connection, transaction, $"""
            SELECT l.material_id, l.line_no, SUM(l.qty)
            FROM document_links dl
            JOIN {docs} d
              ON d.company_id = dl.company_id
             AND d.doc_id = dl.target_doc_id
             AND d.doc_kind = dl.target_doc_kind
            JOIN {lines} l
              ON l.doc_id = d.doc_id
            WHERE dl.company_id = @company
              AND dl.source_doc_id = @sourceId
              AND dl.target_doc_kind = @targetKind
              AND d.status = @posted
              AND d.is_deleted = 0
            GROUP BY l.material_id, l.line_no
            """);
        Add(command, "@company", companyId.ToString("D"));
        Add(command, "@sourceId", sourceDocId);
        Add(command, "@targetKind", targetDocKind);
        Add(command, "@posted", CommercialDocumentStatus.Posted);

        var quantities = new Dictionary<long, decimal>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // Lines without a material id are matched by line number, mirroring SalesChainService.
            var key = reader.IsDBNull(0)
                ? -Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture)
                : Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
            quantities[key] = (quantities.TryGetValue(key, out var existing) ? existing : 0m)
                + Convert.ToDecimal(reader.GetValue(2), CultureInfo.InvariantCulture);
        }
        return quantities;
    }

    // -------------------------------------------------------------- convert

    public async Task<long> ConvertToInvoiceAsync(CommercialDocSide side, long orderDocId, string invoiceDocNo, DateOnly invoiceDate, string userId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(invoiceDocNo)) throw new ArgumentException("Invoice number is required.", nameof(invoiceDocNo));
        if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("User is required.", nameof(userId));
        var orderKind = side == CommercialDocSide.Sales ? CommercialDocKinds.SalesOrder : CommercialDocKinds.PurchaseOrder;
        var invoiceKind = side == CommercialDocSide.Sales ? CommercialDocKinds.SalesInvoice : CommercialDocKinds.PurchaseInvoice;

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var order = await ReadSnapshotAsync(connection, transaction, side, orderDocId, null, null, null, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Order was not found.");
            if (order.DocKind != orderKind)
                throw new InvalidOperationException($"Document '{order.DocNo}' is a '{order.DocKind}', not an order ('{orderKind}').");
            if (order.Status != CommercialDocumentStatus.Posted)
                throw new InvalidOperationException($"Only posted orders can be converted; order '{order.DocNo}' is '{order.Status}'.");
            if (order.PartyId is null)
                throw new InvalidOperationException($"Order '{order.DocNo}' has no party account id; set the party from its card before converting.");

            var billed = await LinkedQuantitiesAsync(connection, transaction, side, order.CompanyId, orderDocId, invoiceKind, cancellationToken).ConfigureAwait(false);
            var invoiceLines = new List<CommercialLineDraft>();
            foreach (var line in order.Lines)
            {
                var key = line.MaterialId ?? -line.LineNo;
                var alreadyBilled = billed.TryGetValue(key, out var value) ? value : 0m;
                var remaining = line.Qty - alreadyBilled;
                if (remaining <= 0m)
                    continue;
                invoiceLines.Add(new CommercialLineDraft(
                    line.MaterialId, line.MaterialCode, line.Description, line.Unit,
                    remaining, line.UnitPrice, line.DiscountPct, line.TaxRate, line.WarehouseId));
            }
            if (invoiceLines.Count == 0)
                throw new InvalidOperationException($"Order '{order.DocNo}' is already fully billed.");

            var invoiceDraft = new CommercialDocumentDraft(
                order.CompanyId, invoiceKind, invoiceDocNo.Trim(), invoiceDate,
                order.PartyId, order.PartyName, order.WarehouseId,
                order.CurrencyCode, order.ExchangeRate,
                $"From {orderKind} {order.DocNo}",
                invoiceLines,
                order.PartyCode, null, null, order.CostCenter, order.BranchName, order.SalesRepName, order.WarehouseName);

            var invoiceId = await InsertDocumentAsync(connection, transaction, side, invoiceDraft, CommercialDocumentStatus.Posted, userId, cancellationToken).ConfigureAwait(false);

            ComputeTotals(invoiceLines, out _, out _, out var grandTotal);
            await using (var ledger = CreateCommand(connection, transaction, """
                INSERT INTO party_ledger
                    (company_id, party_kind, party_id, doc_kind, doc_id, doc_no, entry_date, debit, credit, currency_code, note)
                VALUES (@company, @partyKind, @party, @kind, @doc, @docNo, @date, @debit, @credit, @currency, @note)
                """))
            {
                Add(ledger, "@company", order.CompanyId.ToString("D"));
                Add(ledger, "@partyKind", side == CommercialDocSide.Sales ? "C" : "S");
                Add(ledger, "@party", order.PartyId.Value);
                Add(ledger, "@kind", invoiceKind);
                Add(ledger, "@doc", invoiceId);
                Add(ledger, "@docNo", invoiceDocNo.Trim());
                Add(ledger, "@date", invoiceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                Add(ledger, "@debit", side == CommercialDocSide.Sales ? grandTotal : 0m);
                Add(ledger, "@credit", side == CommercialDocSide.Purchase ? grandTotal : 0m);
                Add(ledger, "@currency", order.CurrencyCode);
                Add(ledger, "@note", $"Invoice {invoiceDocNo.Trim()} from order {order.DocNo}");
                await ledger.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var link = CreateCommand(connection, transaction, """
                INSERT INTO document_links (company_id, source_doc_kind, source_doc_id, target_doc_kind, target_doc_id, created_by)
                VALUES (@company, @sourceKind, @sourceId, @targetKind, @targetId, @actor)
                """))
            {
                Add(link, "@company", order.CompanyId.ToString("D"));
                Add(link, "@sourceKind", orderKind);
                Add(link, "@sourceId", orderDocId);
                Add(link, "@targetKind", invoiceKind);
                Add(link, "@targetId", invoiceId);
                Add(link, "@actor", userId.Trim());
                await link.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return invoiceId;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IReadOnlyList<CommercialDocumentLink>> GetLinksFromAsync(Guid companyId, long sourceDocId, CancellationToken cancellationToken = default)
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

    // --------------------------------------------------------------- helpers

    private static void ValidateDraft(CommercialDocumentDraft draft, string userId)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.CompanyId == Guid.Empty) throw new ArgumentException("Company is required.", nameof(draft));
        if (string.IsNullOrWhiteSpace(draft.DocKind)) throw new ArgumentException("Document kind is required.", nameof(draft));
        if (string.IsNullOrWhiteSpace(draft.DocNo)) throw new ArgumentException("Document number is required.", nameof(draft));
        if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("User is required.", nameof(userId));
        if (draft.Lines.Count == 0) throw new InvalidOperationException("A commercial document requires at least one line.");
    }

    private static List<(decimal Subtotal, decimal Tax, decimal Total)> ComputeTotals(
        IReadOnlyList<CommercialLineDraft> lines, out decimal subtotal, out decimal taxAmount, out decimal grandTotal)
    {
        subtotal = 0m;
        taxAmount = 0m;
        grandTotal = 0m;
        var computed = new List<(decimal Subtotal, decimal Tax, decimal Total)>();
        foreach (var line in lines)
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
        subtotal = decimal.Round(subtotal, 2);
        taxAmount = decimal.Round(taxAmount, 2);
        grandTotal = decimal.Round(grandTotal, 2);
        return computed;
    }

    private async Task<long> InsertDocumentAsync(
        DbConnection connection, DbTransaction transaction, CommercialDocSide side,
        CommercialDocumentDraft draft, string status, string userId, CancellationToken cancellationToken)
    {
        var (docs, _) = Tables(side);
        var computed = ComputeTotals(draft.Lines, out var subtotal, out var taxAmount, out var grandTotal);
        long docId;
        await using (var insertHeader = CreateCommand(connection, transaction,
            $"INSERT INTO {docs} (company_id, doc_kind, doc_no, doc_date, party_id, party_name, warehouse_id, currency_code, exchange_rate, subtotal, discount_amount, tax_amount, grand_total, status, notes, created_by, party_code, delivery_date, valid_till, cost_center, branch_name, sales_rep_name, warehouse_name, posted_by, posted_at) " +
            "VALUES (@company, @kind, @no, @date, @party, @partyName, @warehouse, @currency, @rate, @subtotal, @discount, @tax, @total, @status, @notes, @actor, @partyCode, @deliveryDate, @validTill, @costCenter, @branchName, @salesRepName, @warehouseName, @postedBy, @postedAt)" +
            (_provider == "PostgreSQL" ? " RETURNING doc_id" : "; SELECT last_insert_rowid();")))
        {
            Add(insertHeader, "@company", draft.CompanyId.ToString("D"));
            Add(insertHeader, "@kind", draft.DocKind.Trim());
            Add(insertHeader, "@no", draft.DocNo.Trim());
            Add(insertHeader, "@date", draft.DocDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            Add(insertHeader, "@party", (object?)draft.PartyId ?? DBNull.Value);
            Add(insertHeader, "@partyName", (object?)draft.PartyName ?? DBNull.Value);
            Add(insertHeader, "@warehouse", (object?)draft.WarehouseId ?? DBNull.Value);
            Add(insertHeader, "@currency", string.IsNullOrWhiteSpace(draft.CurrencyCode) ? "SAR" : draft.CurrencyCode.Trim());
            Add(insertHeader, "@rate", draft.ExchangeRate <= 0m ? 1m : draft.ExchangeRate);
            Add(insertHeader, "@subtotal", subtotal);
            Add(insertHeader, "@discount", 0m);
            Add(insertHeader, "@tax", taxAmount);
            Add(insertHeader, "@total", grandTotal);
            Add(insertHeader, "@status", status);
            Add(insertHeader, "@notes", (object?)draft.Notes ?? DBNull.Value);
            Add(insertHeader, "@actor", userId.Trim());
            Add(insertHeader, "@partyCode", (object?)draft.PartyCode ?? DBNull.Value);
            Add(insertHeader, "@deliveryDate", (object?)draft.DeliveryDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? DBNull.Value);
            Add(insertHeader, "@validTill", (object?)draft.ValidTill?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? DBNull.Value);
            Add(insertHeader, "@costCenter", (object?)draft.CostCenter ?? DBNull.Value);
            Add(insertHeader, "@branchName", (object?)draft.BranchName ?? DBNull.Value);
            Add(insertHeader, "@salesRepName", (object?)draft.SalesRepName ?? DBNull.Value);
            Add(insertHeader, "@warehouseName", (object?)draft.WarehouseName ?? DBNull.Value);
            var posted = status == CommercialDocumentStatus.Posted;
            Add(insertHeader, "@postedBy", posted ? userId.Trim() : DBNull.Value);
            Add(insertHeader, "@postedAt", posted ? DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : DBNull.Value);
            docId = Convert.ToInt64(await insertHeader.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }
        await InsertLinesAsync(connection, transaction, side, docId, draft.Lines, computed, cancellationToken).ConfigureAwait(false);
        return docId;
    }

    private async Task InsertLinesAsync(
        DbConnection connection, DbTransaction transaction, CommercialDocSide side, long docId,
        IReadOnlyList<CommercialLineDraft> lines, List<(decimal Subtotal, decimal Tax, decimal Total)> computed,
        CancellationToken cancellationToken)
    {
        var (_, lineTable) = Tables(side);
        var lineNumber = 0;
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            var totals = computed[index];
            await using var insertLine = CreateCommand(connection, transaction,
                $"INSERT INTO {lineTable} (doc_id, line_no, material_id, material_code, description, unit, qty, unit_price, discount_pct, tax_rate, line_subtotal, line_tax, line_total, warehouse_id) " +
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
    }

    private DbConnection CreateConnection() => _provider == "SQLite"
        ? new SqliteConnection(_connectionString)
        : new NpgsqlConnection(_connectionString);

    private static DbCommand CreateCommand(DbConnection connection, DbTransaction? transaction, string sql)
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

    private static string NormalizeProvider(string provider) => provider.Trim().ToLowerInvariant() switch
    {
        "sqlite" or "sqlite3" => "SQLite",
        "postgres" or "postgresql" or "npgsql" => "PostgreSQL",
        _ => throw new NotSupportedException($"Unsupported database provider '{provider}'. Use SQLite or PostgreSQL.")
    };
}
