using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
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
                Add(update, "@rate", draft.ExchangeRate < 0m ? 0m : draft.ExchangeRate); // Step 7: 0 = no document rate (master fallback / ACC-5015 at posting), never fabricated to 1
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
            string status, kind, companyId;
            await using (var read = CreateCommand(connection, transaction, $"SELECT status, doc_kind, company_id FROM {docs} WHERE doc_id = @doc AND is_deleted = 0"))
            {
                Add(read, "@doc", docId);
                await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("Document was not found.");
                status = reader.GetString(0);
                kind = reader.GetString(1);
                companyId = reader.GetString(2);
            }
            if (newStatus == CommercialDocumentStatus.Lost && kind != CommercialDocKinds.SalesQuotation)
                throw new InvalidOperationException("Only quotations can be marked as lost.");
            if (status == CommercialDocumentStatus.Posted
                && newStatus is CommercialDocumentStatus.Cancelled or CommercialDocumentStatus.Lost)
            {
                var isCommitment = kind is CommercialDocKinds.SalesOrder
                    or CommercialDocKinds.PurchaseOrder
                    or CommercialDocKinds.SalesQuotation
                    or CommercialDocKinds.RequestForQuotation;

                // Operational documents have already affected stock, GL or
                // party balances. They must be corrected with a return or a
                // linked reversal, never by changing their status directly.
                if (newStatus == CommercialDocumentStatus.Cancelled && !isCommitment)
                    throw new InvalidOperationException(
                        "A posted operational document cannot be cancelled directly. Create a return or reverse the posted document.");

                // A posted commitment cannot be cancelled or marked lost
                // while a posted downstream document still depends on it.
                await using var downstream = CreateCommand(connection, transaction, $"""
                    SELECT COUNT(*)
                    FROM document_links dl
                    JOIN {docs} target
                      ON target.company_id = dl.company_id
                     AND target.doc_id = dl.target_doc_id
                     AND target.doc_kind = dl.target_doc_kind
                    WHERE dl.company_id = @company
                      AND dl.source_doc_kind = @kind
                      AND dl.source_doc_id = @doc
                      AND target.status = @posted
                      AND target.is_deleted = 0
                    """);
                Add(downstream, "@company", companyId);
                Add(downstream, "@kind", kind);
                Add(downstream, "@doc", docId);
                Add(downstream, "@posted", CommercialDocumentStatus.Posted);
                var linkedCount = Convert.ToInt64(
                    await downstream.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    CultureInfo.InvariantCulture);
                if (linkedCount > 0)
                    throw new InvalidOperationException(
                        "A posted commitment cannot be cancelled or marked lost while posted downstream documents are linked. Reverse or cancel the downstream documents first.");
            }

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
        // Step 9: the document tables carry TWO company keys — this
        // repository's Guid and the invoice family's company CODE
        // (InvRepo writes the code into the same column). DocSearch is
        // the cross-family surface: a search for one company must match
        // either of ITS OWN keys, or native InvoiceWin invoices and
        // quotations (code-keyed) never appear in the results at all.
        var companyCodeKey = await ResolveCompanyCodeAsync(connection, null, companyId, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT doc_id, doc_kind, doc_no, doc_date, party_code, party_name, grand_total, status, delivery_date, valid_till
            FROM {docs}
            WHERE (company_id = @company OR company_id = @companyCode) AND is_deleted = 0
              AND (@kind IS NULL OR doc_kind = @kind)
              AND (@status IS NULL OR status = @status)
              AND (@from IS NULL OR doc_date >= @from)
              AND (@to IS NULL OR doc_date <= @to)
              AND (@search IS NULL OR doc_no LIKE @searchLike OR party_name LIKE @searchLike OR party_code LIKE @searchLike)
            ORDER BY doc_date DESC, doc_id DESC
            LIMIT @limit
            """;
        Add(command, "@company", companyId.ToString("D"));
        Add(command, "@companyCode", (object?)companyCodeKey ?? DBNull.Value);
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

        var remainingFulfilled = fulfilled.ToDictionary(pair => pair.Key, pair => pair.Value);
        var remainingBilled = billed.ToDictionary(pair => pair.Key, pair => pair.Value);
        var lines = new List<OrderProgressLine>();
        decimal totalOrdered = 0m, totalFulfilled = 0m, totalBilled = 0m;
        foreach (var line in order.Lines)
        {
            var key = line.MaterialId ?? -line.LineNo;
            var fulfilledAvailable = remainingFulfilled.GetValueOrDefault(key);
            var billedAvailable = remainingBilled.GetValueOrDefault(key);
            var lineFulfilled = Math.Min(line.Qty, fulfilledAvailable);
            var lineBilled = Math.Min(line.Qty, billedAvailable);
            remainingFulfilled[key] = Math.Max(0m, fulfilledAvailable - lineFulfilled);
            remainingBilled[key] = Math.Max(0m, billedAvailable - lineBilled);
            lines.Add(new OrderProgressLine(line.MaterialId, line.MaterialCode, line.Description, line.Qty, lineFulfilled, lineBilled));
            totalOrdered += line.Qty;
            totalFulfilled += lineFulfilled;
            totalBilled += lineBilled;
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

    public async Task<long> CreatePurchaseReceiptAsync(
        Guid companyId, long purchaseOrderId, string receiptDocNo, DateOnly receiptDate,
        IReadOnlyList<CommercialLineDraft> receivedLines, string userId,
        CancellationToken cancellationToken = default)
    {
        if (companyId == Guid.Empty) throw new ArgumentException("Company is required.", nameof(companyId));
        if (purchaseOrderId <= 0) throw new ArgumentOutOfRangeException(nameof(purchaseOrderId));
        if (string.IsNullOrWhiteSpace(receiptDocNo)) throw new ArgumentException("Receipt number is required.", nameof(receiptDocNo));
        if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("User is required.", nameof(userId));
        ArgumentNullException.ThrowIfNull(receivedLines);
        if (receivedLines.Count == 0) throw new InvalidOperationException("A goods receipt requires at least one received line.");

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Serialize receipts for one PO so concurrent users cannot receive the same remaining quantity.
            await using (var orderLock = CreateCommand(connection, transaction,
                _provider == "PostgreSQL"
                    ? "SELECT doc_id FROM purchase_documents WHERE company_id = @company AND doc_id = @doc AND doc_kind = @kind AND status = @posted AND is_deleted = FALSE FOR UPDATE"
                    : "SELECT doc_id FROM purchase_documents WHERE company_id = @company AND doc_id = @doc AND doc_kind = @kind AND status = @posted AND is_deleted = 0"))
            {
                Add(orderLock, "@company", companyId.ToString("D"));
                Add(orderLock, "@doc", purchaseOrderId);
                Add(orderLock, "@kind", CommercialDocKinds.PurchaseOrder);
                Add(orderLock, "@posted", CommercialDocumentStatus.Posted);
                if (await orderLock.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
                    throw new InvalidOperationException("Only a posted purchase order can be received.");
            }

            var order = await ReadSnapshotAsync(connection, transaction, CommercialDocSide.Purchase,
                purchaseOrderId, companyId, null, null, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Purchase order was not found.");
            if (order.DocKind != CommercialDocKinds.PurchaseOrder || order.Status != CommercialDocumentStatus.Posted)
                throw new InvalidOperationException("Only a posted purchase order can be received.");
            if (order.PartyId is null)
                throw new InvalidOperationException("The purchase order must identify a supplier before receiving goods.");

            var requested = new Dictionary<long, decimal>();
            var requestedWarehouse = new Dictionary<long, long?>();
            foreach (var line in receivedLines)
            {
                if (line.MaterialId is not long materialId || materialId <= 0)
                    throw new InvalidOperationException("Every receipt line must identify a material from the purchase order.");
                if (line.Qty <= 0m)
                    throw new InvalidOperationException("Received quantities must be greater than zero.");
                requested[materialId] = requested.GetValueOrDefault(materialId) + line.Qty;
                if (line.WarehouseId is not null)
                    requestedWarehouse[materialId] = line.WarehouseId;
            }

            var received = await LinkedQuantitiesAsync(connection, transaction, CommercialDocSide.Purchase,
                order.CompanyId, purchaseOrderId, CommercialDocKinds.PurchaseReceipt, cancellationToken).ConfigureAwait(false);
            var orderedByMaterial = new Dictionary<long, decimal>();
            foreach (var line in order.Lines)
            {
                if (line.MaterialId is not long materialId)
                    throw new InvalidOperationException("Every purchase-order line must identify a material before a goods receipt can be posted.");
                orderedByMaterial[materialId] = orderedByMaterial.GetValueOrDefault(materialId) + line.Qty;
            }

            foreach (var (materialId, qty) in requested)
            {
                if (!orderedByMaterial.TryGetValue(materialId, out var orderedQty))
                    throw new InvalidOperationException($"Material '{materialId}' is not part of purchase order '{order.DocNo}'.");
                var alreadyReceived = received.GetValueOrDefault(materialId);
                var remaining = orderedQty - alreadyReceived;
                if (qty > remaining)
                    throw new InvalidOperationException(
                        $"Receipt quantity for material '{materialId}' exceeds the remaining purchase-order quantity ({remaining}).");
            }

            var priorToAllocate = received.ToDictionary(pair => pair.Key, pair => pair.Value);
            var requestedToAllocate = new Dictionary<long, decimal>(requested);
            var receiptDraftLines = new List<CommercialLineDraft>();
            foreach (var line in order.Lines)
            {
                var materialId = line.MaterialId!.Value;
                var prior = Math.Min(line.Qty, priorToAllocate.GetValueOrDefault(materialId));
                priorToAllocate[materialId] = Math.Max(0m, priorToAllocate.GetValueOrDefault(materialId) - prior);
                var lineRemaining = line.Qty - prior;
                var take = Math.Min(lineRemaining, requestedToAllocate.GetValueOrDefault(materialId));
                if (take <= 0m) continue;
                requestedToAllocate[materialId] -= take;
                receiptDraftLines.Add(new CommercialLineDraft(
                    line.MaterialId, line.MaterialCode, line.Description, line.Unit,
                    take, line.UnitPrice, line.DiscountPct, 0m,
                    requestedWarehouse.TryGetValue(materialId, out var chosenWarehouse)
                        ? chosenWarehouse : line.WarehouseId));
            }

            if (receiptDraftLines.Count == 0 || requestedToAllocate.Values.Any(qty => qty > 0m))
                throw new InvalidOperationException("The requested receipt quantities could not be matched to the remaining purchase-order lines.");

            var receiptDraft = new CommercialDocumentDraft(
                order.CompanyId, CommercialDocKinds.PurchaseReceipt, receiptDocNo.Trim(), receiptDate,
                order.PartyId, order.PartyName, order.WarehouseId, order.CurrencyCode, order.ExchangeRate,
                $"Goods receipt against purchase order {order.DocNo}",
                receiptDraftLines, order.PartyCode, null, null, order.CostCenter,
                order.BranchName, order.SalesRepName, order.WarehouseName);
            var receiptId = await InsertDocumentAsync(connection, transaction, CommercialDocSide.Purchase,
                receiptDraft, CommercialDocumentStatus.Posted, userId, cancellationToken).ConfigureAwait(false);
            ComputeTotals(receiptDraftLines, out var receiptSubtotal, out _, out _);

            _ = await PostConversionStockAsync(
                connection, transaction, CommercialDocSide.Purchase, order, receiptDraftLines,
                receiptDocNo.Trim(), receiptDate, cancellationToken, CommercialDocKinds.PurchaseReceipt).ConfigureAwait(false);

            var fx = FxRates.Resolve(connection, transaction, _provider,
                order.CompanyId, order.CurrencyCode, receiptDate, order.ExchangeRate);
            decimal inventorySubtotal = 0m;
            decimal nonStockSubtotal = 0m;
            foreach (var line in receiptDraftLines)
            {
                ComputeTotals(new[] { line }, out var lineSubtotal, out _, out _);
                if (line.MaterialId is long materialId
                    && await IsStockableAsync(connection, transaction, materialId, cancellationToken).ConfigureAwait(false))
                    inventorySubtotal += lineSubtotal;
                else
                    nonStockSubtotal += lineSubtotal;
            }
            if (receiptSubtotal > 0m)
            {
                var receiptGlLines = new List<GlLineRequest>();
                if (inventorySubtotal > 0m)
                    receiptGlLines.Add(new GlLineRequest(GlAccounts.Inventory, inventorySubtotal, 0m, order.CostCenter));
                if (nonStockSubtotal > 0m)
                    receiptGlLines.Add(new GlLineRequest(GlAccounts.Purchases, nonStockSubtotal, 0m, order.CostCenter));
                receiptGlLines.Add(new GlLineRequest(GlAccounts.GoodsReceivedNotInvoiced, 0m, receiptSubtotal));
                GlPosting.PostInTransaction(connection, transaction, _provider,
                    new GlDocumentRequest(order.CompanyId, receiptDate,
                        $"Goods receipt {receiptDocNo.Trim()} against {order.DocNo}",
                        CommercialDocKinds.PurchaseReceipt, receiptId.ToString(CultureInfo.InvariantCulture),
                        receiptDocNo.Trim(), order.CurrencyCode, fx.Rate, receiptGlLines),
                    userId);
            }

            await using (var link = CreateCommand(connection, transaction, """
                INSERT INTO document_links (company_id, source_doc_kind, source_doc_id, target_doc_kind, target_doc_id, created_by)
                VALUES (@company, @sourceKind, @sourceId, @targetKind, @targetId, @actor)
                """))
            {
                Add(link, "@company", order.CompanyId.ToString("D"));
                Add(link, "@sourceKind", CommercialDocKinds.PurchaseOrder);
                Add(link, "@sourceId", purchaseOrderId);
                Add(link, "@targetKind", CommercialDocKinds.PurchaseReceipt);
                Add(link, "@targetId", receiptId);
                Add(link, "@actor", userId.Trim());
                await link.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return receiptId;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    // -------------------------------------------------------------- convert

    public Task<long> ConvertToInvoiceAsync(CommercialDocSide side, long orderDocId, string invoiceDocNo, DateOnly invoiceDate, string userId, CancellationToken cancellationToken = default)
    {
        if (side == CommercialDocSide.Purchase)
            throw new InvalidOperationException("Supplier invoices must use CreatePurchaseInvoiceAsync with company-scoped receipt matching.");
        return ConvertToInvoiceCoreAsync(side, orderDocId, invoiceDocNo, invoiceDate, null, null, userId, cancellationToken);
    }

    public Task<long> CreatePurchaseInvoiceAsync(
        Guid companyId, long purchaseOrderId, string invoiceDocNo, DateOnly invoiceDate,
        IReadOnlyList<CommercialLineDraft> invoiceLines, string userId,
        CancellationToken cancellationToken = default)
    {
        if (companyId == Guid.Empty) throw new ArgumentException("Company is required.", nameof(companyId));
        ArgumentNullException.ThrowIfNull(invoiceLines);
        if (invoiceLines.Count == 0) throw new InvalidOperationException("A supplier invoice requires at least one line.");
        return ConvertToInvoiceCoreAsync(CommercialDocSide.Purchase, purchaseOrderId,
            invoiceDocNo, invoiceDate, invoiceLines, companyId, userId, cancellationToken);
    }

    private async Task<long> ConvertToInvoiceCoreAsync(
        CommercialDocSide side, long orderDocId, string invoiceDocNo, DateOnly invoiceDate,
        IReadOnlyList<CommercialLineDraft>? invoiceLineOverrides, Guid? expectedCompanyId,
        string userId, CancellationToken cancellationToken)
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
            if (expectedCompanyId is Guid expectedCompany && order.CompanyId != expectedCompany)
                throw new InvalidOperationException("The purchase order belongs to a different company.");
            if (order.Status != CommercialDocumentStatus.Posted)
                throw new InvalidOperationException($"Only posted orders can be converted; order '{order.DocNo}' is '{order.Status}'.");
            if (order.PartyId is null)
                throw new InvalidOperationException($"Order '{order.DocNo}' has no party account id; set the party from its card before converting.");

            var billed = await LinkedQuantitiesAsync(connection, transaction, side, order.CompanyId, orderDocId, invoiceKind, cancellationToken).ConfigureAwait(false);
            var received = side == CommercialDocSide.Purchase
                ? await LinkedQuantitiesAsync(connection, transaction, side, order.CompanyId, orderDocId, CommercialDocKinds.PurchaseReceipt, cancellationToken).ConfigureAwait(false)
                : new Dictionary<long, decimal>();
            var remainingByMaterial = new Dictionary<long, decimal>();
            foreach (var group in order.Lines.GroupBy(line => line.MaterialId ?? -line.LineNo))
            {
                var orderedQty = group.Sum(line => line.Qty);
                var alreadyBilled = billed.GetValueOrDefault(group.Key);
                var remaining = orderedQty - alreadyBilled;
                if (side == CommercialDocSide.Purchase)
                    remaining = Math.Min(remaining, received.GetValueOrDefault(group.Key) - alreadyBilled);
                remainingByMaterial[group.Key] = Math.Max(0m, remaining);
            }

            var invoiceLines = new List<CommercialLineDraft>();
            if (invoiceLineOverrides is null)
            {
                foreach (var line in order.Lines)
                {
                    var key = line.MaterialId ?? -line.LineNo;
                    var remaining = Math.Min(line.Qty, remainingByMaterial.GetValueOrDefault(key));
                    if (remaining <= 0m)
                        continue;
                    remainingByMaterial[key] -= remaining;
                    invoiceLines.Add(new CommercialLineDraft(
                        line.MaterialId, line.MaterialCode, line.Description, line.Unit,
                        remaining, line.UnitPrice, line.DiscountPct, line.TaxRate, line.WarehouseId));
                }
            }
            else
            {
                var sourceByMaterial = order.Lines
                    .Where(line => line.MaterialId is not null)
                    .GroupBy(line => line.MaterialId!.Value)
                    .ToDictionary(group => group.Key, group => group.First());
                foreach (var supplied in invoiceLineOverrides)
                {
                    if (supplied.MaterialId is not long materialId || materialId <= 0)
                        throw new InvalidOperationException("Every supplier invoice line must identify a material from the purchase order.");
                    if (supplied.Qty <= 0m)
                        throw new InvalidOperationException("Supplier invoice quantities must be greater than zero.");
                    if (supplied.UnitPrice < 0m || supplied.DiscountPct < 0m || supplied.DiscountPct > 100m
                        || supplied.TaxRate < 0m || supplied.TaxRate > 100m)
                        throw new InvalidOperationException("Supplier invoice price, discount, or tax rate is outside the allowed range.");
                    if (!sourceByMaterial.TryGetValue(materialId, out var source))
                        throw new InvalidOperationException($"Material '{materialId}' is not part of purchase order '{order.DocNo}'.");
                    var remaining = remainingByMaterial.GetValueOrDefault(materialId);
                    if (supplied.Qty > remaining)
                        throw new InvalidOperationException(
                            $"Supplier invoice quantity for material '{materialId}' exceeds the received, unbilled quantity ({remaining}).");
                    remainingByMaterial[materialId] = remaining - supplied.Qty;
                    invoiceLines.Add(new CommercialLineDraft(
                        source.MaterialId, source.MaterialCode, source.Description, source.Unit,
                        supplied.Qty, supplied.UnitPrice, supplied.DiscountPct, supplied.TaxRate,
                        supplied.WarehouseId ?? source.WarehouseId));
                }
            }
            if (invoiceLines.Count == 0)
                throw new InvalidOperationException(side == CommercialDocSide.Purchase
                    ? $"Purchase order '{order.DocNo}' has no received quantity available to invoice. Post a goods receipt first."
                    : $"Order '{order.DocNo}' is already fully billed.");

            if (side == CommercialDocSide.Purchase && invoiceLines.Any(line => line.TaxRate > 0m)
                && !await IsCompanyTaxEnabledAsync(connection, transaction, order.CompanyId, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("Tax is disabled for this company. Enable the company's tax system before posting a taxable supplier invoice.");

            var invoiceDraft = new CommercialDocumentDraft(
                order.CompanyId, invoiceKind, invoiceDocNo.Trim(), invoiceDate,
                order.PartyId, order.PartyName, order.WarehouseId,
                order.CurrencyCode, order.ExchangeRate,
                $"From {orderKind} {order.DocNo}",
                invoiceLines,
                order.PartyCode, null, null, order.CostCenter, order.BranchName, order.SalesRepName, order.WarehouseName);

            var invoiceId = await InsertDocumentAsync(connection, transaction, side, invoiceDraft, CommercialDocumentStatus.Posted, userId, cancellationToken).ConfigureAwait(false);

            ComputeTotals(invoiceLines, out var invoiceSubtotal, out var invoiceTax, out var grandTotal);

            // Step 7 (J-5): resolve the order's effective rate ONCE
            // through FxRates (document rate, else master, else
            // ACC-5015); guard + ledger consume BASE amounts at it, and
            // GlPosting below resolves identically from the same inputs.
            var fx = FxRates.Resolve(connection, transaction, _provider,
                order.CompanyId, order.CurrencyCode, invoiceDate, order.ExchangeRate);

            decimal grniBaseAmount = 0m;
            IReadOnlyList<long> matchedReceiptIds = Array.Empty<long>();
            if (side == CommercialDocSide.Purchase)
            {
                var matchResult = await MatchPurchaseInvoiceToReceiptsAsync(
                    connection, transaction, order, orderDocId, invoiceId, invoiceLines,
                    fx.Rate, userId, cancellationToken).ConfigureAwait(false);
                grniBaseAmount = matchResult.GrniBaseAmount;
                matchedReceiptIds = matchResult.ReceiptIds;
            }

            // Credit-limit gate (ACC-5006): the converted invoice is created
            // already posted and its ledger row is written below, so the
            // party's limit is enforced before that row lands. Step 7:
            // the guard sums base amounts — it is fed the BASE total.
            await PartyCreditGuard.EnsureWithinLimitAsync(
                connection, transaction, _provider, order.CompanyId.ToString("D"),
                side == CommercialDocSide.Sales ? "C" : "S", order.PartyId.Value,
                order.PartyCode, FxRates.ToBase(grandTotal, fx.Rate), "@", cancellationToken).ConfigureAwait(false);

            // Step 9 (sweep ORD-01 / I-4): the converted invoice is born
            // Posted, so its STOCK effect must happen HERE, in this same
            // transaction — before this fix the conversion wrote the
            // invoice, the party ledger and the GL but never moved
            // stock, leaving converted goods double-sellable (sales) or
            // never received (purchase) with no later path able to
            // repair it. It runs AFTER the credit guard (a validation)
            // and BEFORE the GL block, whose COGS legs are built from
            // the engine's cost. All-or-nothing with everything else:
            // an engine failure (e.g. insufficient stock) rolls the
            // whole conversion back, invoice included.
            var stockCompanyCode = await PartyLedger.ResolveCompanyCodeAsync(
                connection, transaction, _provider, order.CompanyId.ToString("D"), cancellationToken).ConfigureAwait(false);
            var cogsAmount = 0m;
            if (side == CommercialDocSide.Sales)
            {
                var stockResult = await PostConversionStockAsync(
                    connection, transaction, side, order, invoiceLines,
                    invoiceDocNo.Trim(), invoiceDate, cancellationToken).ConfigureAwait(false);
                stockCompanyCode = stockResult.CompanyCode;
                cogsAmount = stockResult.CogsAmount;
            }

            // Step 4 (B-06): party_ledger.company_id is the company
            // CODE, not the Guid this repository keys documents by.
            // Step 7: written through PartyLedger.WriteEntry so the row
            // carries base amounts + the effective rate.
            PartyLedger.WriteEntry(connection, transaction, _provider,
                stockCompanyCode,
                side == CommercialDocSide.Sales ? "C" : "S", order.PartyId.Value,
                invoiceKind, invoiceId, invoiceDocNo.Trim(),
                invoiceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                side == CommercialDocSide.Sales ? grandTotal : 0m,
                side == CommercialDocSide.Purchase ? grandTotal : 0m,
                order.CurrencyCode,
                $"Invoice {invoiceDocNo.Trim()} from order {order.DocNo}",
                exchangeRate: fx.Rate);

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

            if (side == CommercialDocSide.Purchase)
            {
                foreach (var receiptId in matchedReceiptIds)
                {
                    await using var receiptLink = CreateCommand(connection, transaction, """
                        INSERT INTO document_links (company_id, source_doc_kind, source_doc_id, target_doc_kind, target_doc_id, created_by)
                        VALUES (@company, @sourceKind, @sourceId, @targetKind, @targetId, @actor)
                        """);
                    Add(receiptLink, "@company", order.CompanyId.ToString("D"));
                    Add(receiptLink, "@sourceKind", CommercialDocKinds.PurchaseReceipt);
                    Add(receiptLink, "@sourceId", receiptId);
                    Add(receiptLink, "@targetKind", CommercialDocKinds.PurchaseInvoice);
                    Add(receiptLink, "@targetId", invoiceId);
                    Add(receiptLink, "@actor", userId.Trim());
                    await receiptLink.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            // Real GL posting (Step 3, J-2): the converted invoice is
            // created already-Posted, so its balanced journal entry is
            // written in this same transaction (sales: Dr Receivable /
            // Cr Revenue + Step 9 Dr COGS / Cr Inventory; purchase:
            // Dr Purchases / Cr Payable; tax split only when a postable
            // VAT account is configured). A zero-value sales conversion
            // still posts its COGS pair (GlPosting skips 0/0 lines, so
            // the free-goods entry carries exactly the cost legs).
            if (grandTotal > 0 || cogsAmount > 0 || grniBaseAmount > 0)
            {
                var splitTax = invoiceTax > 0 && GlPosting.TryResolveAccount(connection, transaction, _provider,
                    order.CompanyId, side == CommercialDocSide.Sales ? GlAccounts.OutputVat : GlAccounts.InputVat) is not null;
                var glLines = new List<GlLineRequest>();
                if (side == CommercialDocSide.Sales)
                {
                    glLines.Add(new GlLineRequest(GlAccounts.Receivable, grandTotal, 0m));
                    glLines.Add(new GlLineRequest(GlAccounts.SalesRevenue, 0m, splitTax ? invoiceSubtotal : grandTotal, order.CostCenter));
                    if (splitTax) glLines.Add(new GlLineRequest(GlAccounts.OutputVat, 0m, invoiceTax));
                    // Step 9: COGS at the valuation engine's cost for the
                    // stock issued above — the same legs InvRepo builds
                    // for an InvoiceWin sales invoice (Dr COGS / Cr
                    // Inventory), so a converted sale carries its cost of
                    // goods exactly like a direct one. The revenue entry
                    // above stays balanced on its own; this pair balances
                    // itself, so the whole entry remains balanced.
                    if (cogsAmount > 0)
                    {
                        glLines.Add(new GlLineRequest(GlAccounts.Cogs, cogsAmount, 0m, order.CostCenter));
                        glLines.Add(new GlLineRequest(GlAccounts.Inventory, 0m, cogsAmount));
                    }
                }
                else
                {
                    // Clear GRNI at the receipt's historical base value; price
                    // and FX differences go to the purchase variance account.
                    var grniDocumentAmount = decimal.Round(grniBaseAmount / fx.Rate, 6, MidpointRounding.AwayFromZero);
                    var invoiceCostAmount = splitTax ? invoiceSubtotal : grandTotal;
                    var purchaseVariance = invoiceCostAmount - grniDocumentAmount;
                    glLines.Add(new GlLineRequest(GlAccounts.GoodsReceivedNotInvoiced, grniDocumentAmount, 0m, order.CostCenter));
                    if (purchaseVariance > 0m)
                        glLines.Add(new GlLineRequest(GlAccounts.Purchases, purchaseVariance, 0m, order.CostCenter));
                    else if (purchaseVariance < 0m)
                        glLines.Add(new GlLineRequest(GlAccounts.Purchases, 0m, -purchaseVariance, order.CostCenter));
                    if (splitTax) glLines.Add(new GlLineRequest(GlAccounts.InputVat, invoiceTax, 0m));
                    glLines.Add(new GlLineRequest(GlAccounts.Payable, 0m, grandTotal));
                }
                GlPosting.PostInTransaction(connection, transaction, _provider,
                    new GlDocumentRequest(order.CompanyId, invoiceDate,
                        $"{invoiceKind} {invoiceDocNo.Trim()} from order {order.DocNo}",
                        invoiceKind, invoiceId.ToString(CultureInfo.InvariantCulture), invoiceDocNo.Trim(),
                        order.CurrencyCode, fx.Rate, glLines),
                    userId);
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

    private async Task<bool> IsCompanyTaxEnabledAsync(
        DbConnection connection, DbTransaction transaction, Guid companyId,
        CancellationToken cancellationToken)
    {
        var companyCode = await PartyLedger.ResolveCompanyCodeAsync(
            connection, transaction, _provider, companyId.ToString("D"), cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, transaction,
            "SELECT tax_enabled FROM company_tax_settings WHERE company_code = @company");
        Add(command, "@company", companyCode);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is not null and not DBNull
            && Convert.ToBoolean(value, CultureInfo.InvariantCulture);
    }

    private async Task<(decimal GrniBaseAmount, IReadOnlyList<long> ReceiptIds)> MatchPurchaseInvoiceToReceiptsAsync(
        DbConnection connection, DbTransaction transaction, CommercialDocumentSnapshot order,
        long purchaseOrderId, long invoiceId, IReadOnlyList<CommercialLineDraft> invoiceLines,
        decimal invoiceExchangeRate, string userId, CancellationToken cancellationToken)
    {
        var receiptIds = new List<long>();
        await using (var receipts = CreateCommand(connection, transaction, """
            SELECT target_doc_id
            FROM document_links
            WHERE company_id = @company AND source_doc_kind = @sourceKind
              AND source_doc_id = @sourceId AND target_doc_kind = @receiptKind
            ORDER BY target_doc_id
            """))
        {
            Add(receipts, "@company", order.CompanyId.ToString("D"));
            Add(receipts, "@sourceKind", CommercialDocKinds.PurchaseOrder);
            Add(receipts, "@sourceId", purchaseOrderId);
            Add(receipts, "@receiptKind", CommercialDocKinds.PurchaseReceipt);
            await using var reader = await receipts.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                receiptIds.Add(Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture));
        }
        if (receiptIds.Count == 0)
            throw new InvalidOperationException("A supplier invoice cannot be posted without a posted goods receipt.");

        var matchedByReceiptLine = new Dictionary<long, decimal>();
        await using (var prior = CreateCommand(connection, transaction, """
            SELECT receipt_line_id, COALESCE(SUM(matched_qty), 0)
            FROM purchase_receipt_invoice_matches
            WHERE company_id = @company AND purchase_order_id = @order
            GROUP BY receipt_line_id
            """))
        {
            Add(prior, "@company", order.CompanyId.ToString("D"));
            Add(prior, "@order", purchaseOrderId);
            await using var reader = await prior.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                matchedByReceiptLine[Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture)] =
                    Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture);
        }

        var receiptSnapshots = new List<(CommercialDocumentSnapshot Document, decimal ExchangeRate)>();
        foreach (var receiptId in receiptIds)
        {
            var receipt = await ReadSnapshotAsync(connection, transaction, CommercialDocSide.Purchase,
                receiptId, order.CompanyId, CommercialDocKinds.PurchaseReceipt, null, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Goods receipt '{receiptId}' could not be loaded for matching.");
            if (receipt.Status != CommercialDocumentStatus.Posted)
                throw new InvalidOperationException($"Goods receipt '{receipt.DocNo}' is not posted.");
            if (!string.Equals(receipt.CurrencyCode, order.CurrencyCode, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Goods receipt '{receipt.DocNo}' uses a different currency from its purchase order.");
            var receiptFx = FxRates.Resolve(connection, transaction, _provider,
                order.CompanyId, receipt.CurrencyCode, receipt.DocDate, receipt.ExchangeRate);
            receiptSnapshots.Add((receipt, receiptFx.Rate));
        }

        var persistedInvoiceLines = new List<(long LineId, int LineNo, long MaterialId, decimal Qty, decimal UnitPrice, decimal DiscountPct)>();
        await using (var invoiceReaderCommand = CreateCommand(connection, transaction, """
            SELECT line_id, line_no, material_id, qty, unit_price, discount_pct
            FROM purchase_document_lines
            WHERE doc_id = @invoice
            ORDER BY line_no
            """))
        {
            Add(invoiceReaderCommand, "@invoice", invoiceId);
            await using var reader = await invoiceReaderCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.IsDBNull(2))
                    throw new InvalidOperationException("Supplier invoice lines must reference a material card to be matched to receipts.");
                persistedInvoiceLines.Add((
                    Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                    Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture),
                    Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture),
                    Convert.ToDecimal(reader.GetValue(3), CultureInfo.InvariantCulture),
                    Convert.ToDecimal(reader.GetValue(4), CultureInfo.InvariantCulture),
                    Convert.ToDecimal(reader.GetValue(5), CultureInfo.InvariantCulture)));
            }
        }
        if (persistedInvoiceLines.Count != invoiceLines.Count)
            throw new InvalidOperationException("The persisted supplier invoice lines do not match the requested invoice lines.");

        var usedReceiptIds = new HashSet<long>();
        decimal totalGrniBase = 0m;
        for (var invoiceIndex = 0; invoiceIndex < persistedInvoiceLines.Count; invoiceIndex++)
        {
            var invoiceLine = persistedInvoiceLines[invoiceIndex];
            var invoiceRemaining = invoiceLine.Qty;
            var invoiceUnitCost = invoiceLine.UnitPrice * (1m - invoiceLine.DiscountPct / 100m);

            foreach (var (receipt, receiptExchangeRate) in receiptSnapshots)
            {
                foreach (var receiptLine in receipt.Lines.Where(line => line.MaterialId == invoiceLine.MaterialId))
                {
                    var alreadyMatched = matchedByReceiptLine.GetValueOrDefault(receiptLine.LineId);
                    var receiptRemaining = receiptLine.Qty - alreadyMatched;
                    var take = Math.Min(invoiceRemaining, receiptRemaining);
                    if (take <= 0m) continue;

                    var receiptUnitCost = receiptLine.UnitPrice * (1m - receiptLine.DiscountPct / 100m);
                    var receiptBaseAmount = decimal.Round(take * receiptUnitCost * receiptExchangeRate, 6, MidpointRounding.AwayFromZero);
                    var invoiceBaseAmount = decimal.Round(take * invoiceUnitCost * invoiceExchangeRate, 6, MidpointRounding.AwayFromZero);
                    var varianceBaseAmount = invoiceBaseAmount - receiptBaseAmount;
                    var matchId = Guid.NewGuid();

                    await using var insert = CreateCommand(connection, transaction, """
                        INSERT INTO purchase_receipt_invoice_matches
                            (match_id, company_id, purchase_order_id, receipt_doc_id, receipt_line_id,
                             invoice_doc_id, invoice_line_id, material_id, matched_qty,
                             receipt_unit_cost, receipt_exchange_rate, invoice_unit_cost, invoice_exchange_rate,
                             receipt_base_amount, invoice_base_amount, variance_base_amount, created_by)
                        VALUES
                            (@id, @company, @order, @receipt, @receiptLine,
                             @invoice, @invoiceLine, @material, @qty,
                             @receiptCost, @receiptRate, @invoiceCost, @invoiceRate,
                             @receiptBase, @invoiceBase, @varianceBase, @actor)
                        """);
                    Add(insert, "@id", _provider == "PostgreSQL" ? (object)matchId : matchId.ToString("D"));
                    Add(insert, "@company", order.CompanyId.ToString("D"));
                    Add(insert, "@order", purchaseOrderId);
                    Add(insert, "@receipt", receipt.DocId);
                    Add(insert, "@receiptLine", receiptLine.LineId);
                    Add(insert, "@invoice", invoiceId);
                    Add(insert, "@invoiceLine", invoiceLine.LineId);
                    Add(insert, "@material", invoiceLine.MaterialId);
                    Add(insert, "@qty", take);
                    Add(insert, "@receiptCost", receiptUnitCost);
                    Add(insert, "@receiptRate", receiptExchangeRate);
                    Add(insert, "@invoiceCost", invoiceUnitCost);
                    Add(insert, "@invoiceRate", invoiceExchangeRate);
                    Add(insert, "@receiptBase", receiptBaseAmount);
                    Add(insert, "@invoiceBase", invoiceBaseAmount);
                    Add(insert, "@varianceBase", varianceBaseAmount);
                    Add(insert, "@actor", userId.Trim());
                    await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                    matchedByReceiptLine[receiptLine.LineId] = alreadyMatched + take;
                    invoiceRemaining -= take;
                    totalGrniBase += receiptBaseAmount;
                    usedReceiptIds.Add(receipt.DocId);
                    if (invoiceRemaining <= 0m) break;
                }
                if (invoiceRemaining <= 0m) break;
            }

            if (invoiceRemaining > 0m)
                throw new InvalidOperationException(
                    $"Supplier invoice line {invoiceLine.LineNo} exceeds the remaining unbilled quantity on posted goods receipts.");
        }

        return (totalGrniBase, usedReceiptIds.OrderBy(id => id).ToArray());
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

    /// <summary>
    /// Step 9: posts the converted invoice's stock through the ONE
    /// StockValuationEngine (Step 6), inside the conversion
    /// transaction. Keyed by the company CODE — the stock world's key,
    /// the same one StkPost uses for invoice documents — so a converted
    /// invoice's stock is indistinguishable from an InvoiceWin-posted
    /// invoice's: sales issue at the material's cost method (never the
    /// selling price), purchases receive at the line's actual cost
    /// restated per base unit, unit equivalents applied, service
    /// materials skipped, lines without a material id carry no stock
    /// identity (the chain delivery-note rule), and insufficient stock
    /// aborts the whole conversion (engine guard). Returns the company
    /// code (reused for the party-ledger write) and the total COGS the
    /// GL block must carry for a sales conversion.
    /// </summary>
    private async Task<(string CompanyCode, decimal CogsAmount)> PostConversionStockAsync(
        DbConnection connection, DbTransaction transaction, CommercialDocSide side,
        CommercialDocumentSnapshot order, IReadOnlyList<CommercialLineDraft> invoiceLines,
        string invoiceDocNo, DateOnly invoiceDate, CancellationToken cancellationToken,
        string? purchaseMovementKind = null)
    {
        var companyCode = await PartyLedger.ResolveCompanyCodeAsync(
            connection, transaction, _provider, order.CompanyId.ToString("D"), cancellationToken).ConfigureAwait(false);
        var cogsAmount = 0m;
        var movementDate = invoiceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        foreach (var line in invoiceLines)
        {
            if (line.MaterialId is not long materialId)
                continue; // no stock identity — same rule as chain delivery notes
            if (!await IsStockableAsync(connection, transaction, materialId, cancellationToken).ConfigureAwait(false))
                continue;
            var warehouseId = line.WarehouseId ?? order.WarehouseId
                ?? await ResolveDefaultWarehouseAsync(connection, transaction, companyCode, cancellationToken).ConfigureAwait(false);
            await EnsureWarehouseIsActiveAsync(connection, transaction, companyCode, warehouseId, cancellationToken).ConfigureAwait(false);
            var equivalent = await ResolveUnitEquivalentAsync(connection, transaction, materialId, line.Unit, cancellationToken).ConfigureAwait(false);
            var baseQty = line.Qty * equivalent;
            if (side == CommercialDocSide.Sales)
            {
                var issued = StockValuationEngine.Apply(connection, transaction, _provider,
                    new StockMovementRequest(companyCode, warehouseId, materialId,
                        movementDate, CommercialDocKinds.SalesInvoice, invoiceDocNo,
                        QtyIn: 0m, QtyOut: baseQty,
                        Note: line.Description));
                cogsAmount += -issued.CostAmount;
            }
            else
            {
                var discountedUnitPrice = line.UnitPrice * (1m - line.DiscountPct / 100m);
                var unitCost = equivalent == 1m
                    ? discountedUnitPrice
                    : decimal.Round(discountedUnitPrice / equivalent, 6, MidpointRounding.AwayFromZero);
                StockValuationEngine.Apply(connection, transaction, _provider,
                    new StockMovementRequest(companyCode, warehouseId, materialId,
                        movementDate, purchaseMovementKind ?? CommercialDocKinds.PurchaseInvoice, invoiceDocNo,
                        QtyIn: baseQty, QtyOut: 0m,
                        ReceiptUnitCost: unitCost * FxRates.Resolve(connection, transaction, _provider,
                            order.CompanyId, order.CurrencyCode, invoiceDate, order.ExchangeRate).Rate,
                        Note: line.Description));
            }
        }
        return (companyCode, cogsAmount);
    }

    /// <summary>The company CODE for a company Guid (the key the stock
    /// world and party_ledger use), or null when the company row cannot
    /// be resolved. Mirrors PartyLedger.ResolveCompanyCodeAsync's lookup
    /// without its throw, for read paths that must tolerate legacy rows.</summary>
    private async Task<string?> ResolveCompanyCodeAsync(
        DbConnection connection, DbTransaction? transaction, Guid companyId, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction,
            "SELECT company_code FROM companies WHERE company_id = @id");
        Add(command, "@id", companyId.ToString("D"));
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    /// <summary>Whether the material is stock-tracked, mirroring the
    /// guard StkPost and the chain repository apply: not stocked, flagged
    /// as a service, or typed as a Service means non-stock. A material
    /// with no card row keeps the historical behavior and is treated as
    /// stockable (the valuation engine then fails honestly if the card
    /// truly does not exist).</summary>
    private async Task<bool> IsStockableAsync(
        DbConnection connection, DbTransaction transaction, long materialId, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, """
            SELECT is_stocked, is_service, item_type
            FROM materials
            WHERE material_id = @material
            """);
        Add(command, "@material", materialId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return true;

        var isStocked = !reader.IsDBNull(0) && Convert.ToBoolean(reader.GetValue(0), CultureInfo.InvariantCulture);
        var isService = !reader.IsDBNull(1) && Convert.ToBoolean(reader.GetValue(1), CultureInfo.InvariantCulture);
        var itemType = reader.IsDBNull(2) ? null : reader.GetValue(2)?.ToString();
        return isStocked
               && !isService
               && !string.Equals(itemType, "Service", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The equivalence of a line's chosen unit relative to the
    /// material's first (base) unit — the same rule StkPost and the
    /// chain repository apply (units_json first, unit column pairs as
    /// fallback; base unit, unknown unit, or a non-positive stored
    /// equivalent all resolve to 1).</summary>
    private Task<decimal> ResolveUnitEquivalentAsync(
        DbConnection connection, DbTransaction transaction, long materialId, string? unitName,
        CancellationToken cancellationToken)
        // Step 11: the rule lives ONCE in UnitEquivalents (shared with
        // MfgOrderRepository and CommercialDocumentRepository); this
        // wrapper keeps the order repository's material_id-only scoping.
        => UnitEquivalents.ResolveAsync(connection, transaction, null, materialId, unitName, cancellationToken);

    /// <summary>The company's default stock warehouse for a conversion
    /// line that carries none: the lowest warehouse id under the company
    /// CODE (the stock world's key), creating the MAIN warehouse when
    /// the company has none — the same fallback StkPost applies to
    /// invoice documents.</summary>
    private async Task<long> ResolveDefaultWarehouseAsync(
        DbConnection connection, DbTransaction transaction, string companyCode, CancellationToken cancellationToken)
    {
        var isPg = _provider == "PostgreSQL";
        await using (var command = CreateCommand(connection, transaction, $"""
            SELECT warehouse_id FROM warehouses
            WHERE company_id = @company AND is_deleted = {(isPg ? "FALSE" : "0")}
            ORDER BY warehouse_id LIMIT 1
            """))
        {
            Add(command, "@company", companyCode);
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (value is not null and not DBNull)
                return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }

        await using var insert = CreateCommand(connection, transaction, isPg
            ? """
              INSERT INTO warehouses (company_id, warehouse_code, warehouse_name_ar, is_active)
              VALUES (@company, 'MAIN', 'Main Warehouse', TRUE) RETURNING warehouse_id
              """
            : """
              INSERT INTO warehouses (company_id, warehouse_code, warehouse_name_ar, is_active)
              VALUES (@company, 'MAIN', 'Main Warehouse', 1);
              SELECT last_insert_rowid();
              """);
        Add(insert, "@company", companyCode);
        return Convert.ToInt64(await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    /// <summary>Mirrors StkPost's warehouse guard: stock may only move
    /// through an existing, active warehouse of the company.</summary>
    private async Task EnsureWarehouseIsActiveAsync(
        DbConnection connection, DbTransaction transaction, string companyCode, long warehouseId,
        CancellationToken cancellationToken)
    {
        var isPg = _provider == "PostgreSQL";
        await using var command = CreateCommand(connection, transaction, $"""
            SELECT COUNT(1) FROM warehouses
            WHERE company_id = @company AND warehouse_id = @warehouse
              AND is_deleted = {(isPg ? "FALSE" : "0")} AND is_active = {(isPg ? "TRUE" : "1")}
            """);
        Add(command, "@company", companyCode);
        Add(command, "@warehouse", warehouseId);
        var count = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        if (count != 1)
            throw new InvalidOperationException("The selected warehouse is unavailable or inactive.");
    }

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
            Add(insertHeader, "@rate", draft.ExchangeRate < 0m ? 0m : draft.ExchangeRate); // Step 7: as above
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
