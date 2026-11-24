using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.Domain;
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
        string? partyCode;
        string? branchName;
        string? salesRepName;
        string? costCenter;
        string? warehouseName;
        DateOnly? deliveryDate;
        DateOnly? validTill;
        decimal extraAmount;
        decimal perDelivered;
        decimal perBilled;
        string? cancelReason;
        string? cancelledBy;
        string? cancelledAt;
        decimal deliveryCost;
        int revisionNo;
        string approvalState;

        await using (var header = connection.CreateCommand())
        {
            header.CommandText = """
                SELECT doc_id, company_id, doc_kind, doc_no, doc_date, party_id, party_name, warehouse_id,
                       currency_code, exchange_rate, subtotal, discount_amount, tax_amount, grand_total,
                       paid_amount, status, notes, party_code, branch_name, sales_rep_name, cost_center,
                       warehouse_name, delivery_date, valid_till, extra_amount, per_delivered, per_billed,
                       cancel_reason, cancelled_by, cancelled_at, delivery_cost, revision_no, approval_state
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
            partyCode = reader.IsDBNull(17) ? null : reader.GetString(17);
            branchName = reader.IsDBNull(18) ? null : reader.GetString(18);
            salesRepName = reader.IsDBNull(19) ? null : reader.GetString(19);
            costCenter = reader.IsDBNull(20) ? null : reader.GetString(20);
            warehouseName = reader.IsDBNull(21) ? null : reader.GetString(21);
            deliveryDate = reader.IsDBNull(22) ? null : DateOnly.Parse(Convert.ToString(reader.GetValue(22), CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture);
            validTill = reader.IsDBNull(23) ? null : DateOnly.Parse(Convert.ToString(reader.GetValue(23), CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture);
            extraAmount = Convert.ToDecimal(reader.GetValue(24), CultureInfo.InvariantCulture);
            perDelivered = Convert.ToDecimal(reader.GetValue(25), CultureInfo.InvariantCulture);
            perBilled = Convert.ToDecimal(reader.GetValue(26), CultureInfo.InvariantCulture);
            cancelReason = reader.IsDBNull(27) ? null : reader.GetString(27);
            cancelledBy = reader.IsDBNull(28) ? null : reader.GetString(28);
            cancelledAt = reader.IsDBNull(29) ? null : Convert.ToString(reader.GetValue(29), CultureInfo.InvariantCulture);
            deliveryCost = Convert.ToDecimal(reader.GetValue(30), CultureInfo.InvariantCulture);
            revisionNo = Convert.ToInt32(reader.GetValue(31), CultureInfo.InvariantCulture);
            approvalState = reader.IsDBNull(32) ? SalesApprovalState.NotRequired : reader.GetString(32);
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
            paidAmount, status, notes, lines.AsReadOnly(),
            partyCode, branchName, salesRepName, costCenter, warehouseName, deliveryDate, validTill,
            extraAmount, perDelivered, perBilled, cancelReason, cancelledBy, cancelledAt, deliveryCost,
            revisionNo, approvalState);
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
            var docId = await CreateSalesDraftInTransactionAsync(connection, transaction, draft, userId, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return docId;
        }
        catch
        {
            // Preserve the original database/business exception. A provider may
            // already have completed or aborted the transaction; rollback is
            // best-effort and disposal remains the final cleanup guarantee.
            try
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // Transaction is already completed; do not mask the root error.
            }
            throw;
        }
    }

    public async Task<long> CreateSalesDraftWithLinkAsync(
        CommercialDocumentDraft draft,
        string sourceDocKind,
        long sourceDocId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.CompanyId == Guid.Empty) throw new ArgumentException("Company is required.", nameof(draft));
        if (string.IsNullOrWhiteSpace(draft.DocKind)) throw new ArgumentException("Document kind is required.", nameof(draft));
        if (string.IsNullOrWhiteSpace(draft.DocNo)) throw new ArgumentException("Document number is required.", nameof(draft));
        if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("User is required.", nameof(userId));
        if (draft.Lines.Count == 0) throw new InvalidOperationException("A commercial document requires at least one line.");
        if (sourceDocId <= 0) throw new ArgumentException("Source document is required.", nameof(sourceDocId));
        if (string.IsNullOrWhiteSpace(sourceDocKind)) throw new ArgumentException("Source document kind is required.", nameof(sourceDocKind));

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var source = CreateCommand(connection, transaction, """
                SELECT status, doc_kind, company_id
                FROM sales_documents
                WHERE doc_id = @sourceId AND is_deleted = 0
                """))
            {
                Add(source, "@sourceId", sourceDocId);
                await using var reader = await source.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("Source sales document was not found.");
                var sourceStatus = reader.GetString(0);
                var persistedSourceKind = reader.GetString(1);
                var persistedCompanyId = reader.GetString(2);
                if (!string.Equals(sourceStatus, CommercialDocumentStatus.Posted, StringComparison.Ordinal))
                    throw new InvalidOperationException("Only posted source documents can be linked to a new sales document.");
                if (!string.Equals(persistedSourceKind, sourceDocKind.Trim(), StringComparison.Ordinal))
                    throw new InvalidOperationException("The source document kind changed before the linked document was saved.");
                if (!string.Equals(persistedCompanyId, draft.CompanyId.ToString("D"), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Source and target documents must belong to the same company.");
            }

            var docId = await CreateSalesDraftInTransactionAsync(connection, transaction, draft, userId, cancellationToken).ConfigureAwait(false);
            await using var link = CreateCommand(connection, transaction, """
                INSERT INTO document_links
                    (company_id, source_doc_kind, source_doc_id, target_doc_kind, target_doc_id, created_by)
                VALUES (@company, @sourceKind, @sourceId, @targetKind, @targetId, @actor)
                """);
            Add(link, "@company", draft.CompanyId.ToString("D"));
            Add(link, "@sourceKind", sourceDocKind.Trim());
            Add(link, "@sourceId", sourceDocId);
            Add(link, "@targetKind", draft.DocKind.Trim());
            Add(link, "@targetId", docId);
            Add(link, "@actor", userId.Trim());
            await link.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            // The sales order whose stored chain progress this creation can
            // affect: the source itself for SO->DLV, or the delivery's
            // parent order for DLV->SAL (resolved by the block below).
            long? progressOrderId = string.Equals(sourceDocKind.Trim(), CommercialDocKinds.SalesOrder, StringComparison.Ordinal)
                ? sourceDocId
                : null;

            // Preserve order-level billed progress when the invoice is
            // created from a delivery note. The direct DLV->SAL link is
            // retained for delivery accounting; this additional SO->SAL
            // link lets the source order report its billed quantities too.
            if (string.Equals(sourceDocKind.Trim(), CommercialDocKinds.DeliveryNote, StringComparison.Ordinal) &&
                string.Equals(draft.DocKind.Trim(), CommercialDocKinds.SalesInvoice, StringComparison.Ordinal))
            {
                long? sourceOrderId = null;
                await using (var parentOrder = CreateCommand(connection, transaction, """
                    SELECT source_doc_id
                    FROM document_links
                    WHERE company_id = @company
                      AND source_doc_kind = @orderKind
                      AND target_doc_kind = @deliveryKind
                      AND target_doc_id = @deliveryId
                    """))
                {
                    Add(parentOrder, "@company", draft.CompanyId.ToString("D"));
                    Add(parentOrder, "@orderKind", CommercialDocKinds.SalesOrder);
                    Add(parentOrder, "@deliveryKind", CommercialDocKinds.DeliveryNote);
                    Add(parentOrder, "@deliveryId", sourceDocId);
                    var value = await parentOrder.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                    if (value is not null and not DBNull)
                        sourceOrderId = Convert.ToInt64(value, CultureInfo.InvariantCulture);
                }

                if (sourceOrderId is not null)
                {
                    await using var orderInvoiceLink = CreateCommand(connection, transaction, """
                        INSERT INTO document_links
                            (company_id, source_doc_kind, source_doc_id, target_doc_kind, target_doc_id, created_by)
                        VALUES (@company, @orderKind, @orderId, @invoiceKind, @invoiceId, @actor)
                        """);
                    Add(orderInvoiceLink, "@company", draft.CompanyId.ToString("D"));
                    Add(orderInvoiceLink, "@orderKind", CommercialDocKinds.SalesOrder);
                    Add(orderInvoiceLink, "@orderId", sourceOrderId.Value);
                    Add(orderInvoiceLink, "@invoiceKind", CommercialDocKinds.SalesInvoice);
                    Add(orderInvoiceLink, "@invoiceId", docId);
                    Add(orderInvoiceLink, "@actor", userId.Trim());
                    await orderInvoiceLink.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    progressOrderId = sourceOrderId;
                }
            }

            if (progressOrderId is not null)
            {
                // Stored per_delivered / per_billed refresh inside this
                // same transaction (single recompute helper owned by
                // OrderDocumentRepository), so the chain document and the
                // progress it produces commit or roll back together.
                var progressRepository = new OrderDocumentRepository(_provider, _connectionString);
                await progressRepository.RecalculateStoredProgressInTransactionAsync(
                    connection, transaction, progressOrderId.Value, cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return docId;
        }
        catch
        {
            // Preserve the original database/business exception. A provider may
            // already have completed or aborted the transaction; rollback is
            // best-effort and disposal remains the final cleanup guarantee.
            try
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // Transaction is already completed; do not mask the root error.
            }
            throw;
        }
    }

    private async Task<long> CreateSalesDraftInTransactionAsync(
        DbConnection connection,
        DbTransaction transaction,
        CommercialDocumentDraft draft,
        string userId,
        CancellationToken cancellationToken)
    {
        // W7a pricing: a SALES INVOICE draft earns its BXGY free lines
        // at save (zero-price companions), and the price/margin/discount
        // guard hard-blocks below-limit lines at save (SAL-7018/7019/
        // 7020) — overrides are a posting-time act, audited there.
        if (draft.DocKind == CommercialDocKinds.SalesInvoice)
        {
            var expanded = SalesPriceResolver.Default.ExpandBonusLines(
                connection, transaction, _provider, draft.CompanyId.ToString("D"),
                draft.PartyCode, draft.Lines);
            if (expanded.Count != draft.Lines.Count)
                draft = draft with { Lines = expanded };
            SalesPriceGuard.Validate(connection, transaction, _provider,
                draft.CompanyId.ToString("D"), draft.DocKind, draft.DocNo, null,
                draft.Lines.Select((l, i) => new PriceGuardLine(
                    i + 1, l.MaterialId, l.MaterialCode, l.Qty, l.UnitPrice, l.DiscountPct)).ToList(),
                null);
        }
        if (draft.DiscountAmount < 0m) throw new InvalidOperationException("Header discount cannot be negative.");
        if (draft.ExtraAmount < 0m) throw new InvalidOperationException("Header extra charges cannot be negative.");
        if (draft.DeliveryCost < 0m) throw new InvalidOperationException("Delivery cost cannot be negative.");
        var docDateText = draft.DocDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        decimal subtotal = 0m, taxAmount = 0m;
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
        }
        // Stored-total rule (native invoice family): grand = subtotal -
        // header discount + tax + header extra charges. The draft carries
        // the header money (chain conversions carry it from the source,
        // pro-rata to the copied lines); it is never silently zeroed.
        var grandTotal = decimal.Round(subtotal - draft.DiscountAmount + taxAmount + draft.ExtraAmount, 2);

        long docId;
        var insertHeader = CreateCommand(connection, transaction,
            "INSERT INTO sales_documents (company_id, doc_kind, doc_no, doc_date, party_id, party_name, warehouse_id, currency_code, exchange_rate, subtotal, discount_amount, tax_amount, grand_total, status, notes, created_by, party_code, delivery_date, valid_till, cost_center, branch_name, sales_rep_name, warehouse_name, extra_amount, delivery_cost) " +
            "VALUES (@company, @kind, @no, @date, @party, @partyName, @warehouse, @currency, @rate, @subtotal, @discount, @tax, @total, @status, @notes, @actor, @partyCode, @deliveryDate, @validTill, @costCenter, @branchName, @salesRepName, @warehouseName, @extra, @deliveryCost)" +
            (_provider == "PostgreSQL" ? " RETURNING doc_id" : "; SELECT last_insert_rowid();"));
        Add(insertHeader, "@company", draft.CompanyId.ToString("D"));
        Add(insertHeader, "@kind", draft.DocKind.Trim());
        Add(insertHeader, "@no", draft.DocNo.Trim());
        Add(insertHeader, "@date", docDateText);
        Add(insertHeader, "@party", (object?)draft.PartyId ?? DBNull.Value);
        Add(insertHeader, "@partyName", (object?)draft.PartyName ?? DBNull.Value);
        Add(insertHeader, "@warehouse", (object?)draft.WarehouseId ?? DBNull.Value);
        Add(insertHeader, "@currency", string.IsNullOrWhiteSpace(draft.CurrencyCode) ? "SAR" : draft.CurrencyCode.Trim());
        // Step 7: the stored rate is the DOCUMENT rate as given —
        // 0 stays 0 ("no document rate"), so posting can fall back
        // to the currency_rates master or fail with ACC-5015. The
        // pre-Step-7 coercion to 1 fabricated a rate that was never
        // entered and made the master table unreachable.
        Add(insertHeader, "@rate", draft.ExchangeRate < 0m ? 0m : draft.ExchangeRate);
        Add(insertHeader, "@subtotal", decimal.Round(subtotal, 2));
        Add(insertHeader, "@discount", draft.DiscountAmount);
        Add(insertHeader, "@extra", draft.ExtraAmount);
        Add(insertHeader, "@deliveryCost", draft.DeliveryCost);
        Add(insertHeader, "@tax", decimal.Round(taxAmount, 2));
        Add(insertHeader, "@total", grandTotal);
        Add(insertHeader, "@status", CommercialDocumentStatus.Draft);
        Add(insertHeader, "@notes", (object?)draft.Notes ?? DBNull.Value);
        Add(insertHeader, "@actor", userId.Trim());
        Add(insertHeader, "@partyCode", (object?)draft.PartyCode ?? DBNull.Value);
        Add(insertHeader, "@deliveryDate", (object?)draft.DeliveryDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? DBNull.Value);
        Add(insertHeader, "@validTill", (object?)draft.ValidTill?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? DBNull.Value);
        Add(insertHeader, "@costCenter", (object?)draft.CostCenter ?? DBNull.Value);
        Add(insertHeader, "@branchName", (object?)draft.BranchName ?? DBNull.Value);
        Add(insertHeader, "@salesRepName", (object?)draft.SalesRepName ?? DBNull.Value);
        Add(insertHeader, "@warehouseName", (object?)draft.WarehouseName ?? DBNull.Value);
        docId = Convert.ToInt64(await insertHeader.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);

        // W1 (migration 095): a SAL invoice's due date is doc_date + the
        // party's credit_days, set at creation when not already set.
        if (draft.DocKind == CommercialDocKinds.SalesInvoice && draft.PartyId is not null
            && PartyLedger.ColumnExists(connection, transaction, _provider, "sales_documents", "due_date"))
        {
            var due = await PartyLedger.ComputeDueDateAsync(connection, transaction, _provider,
                draft.CompanyId.ToString("D"), draft.PartyId.Value, draft.DocDate, cancellationToken).ConfigureAwait(false);
            if (due is not null)
            {
                await using var dueCmd = CreateCommand(connection, transaction,
                    "UPDATE sales_documents SET due_date = @due WHERE doc_id = @doc AND due_date IS NULL");
                Add(dueCmd, "@due", due.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                Add(dueCmd, "@doc", docId);
                await dueCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        var lineNumber = 0;
        var usedLineNumbers = new HashSet<int>();
        for (var index = 0; index < draft.Lines.Count; index++)
        {
            var line = draft.Lines[index];
            var totals = computed[index];
            lineNumber++;
            // Chain identity (purchase-family convention): a copied line
            // persists line_no = its SOURCE line number, so fulfilment and
            // progress aggregate per source line even when one material
            // appears on several source lines. Standalone lines keep
            // sequential numbering.
            var persistedLineNumber = line.SourceLineNo ?? lineNumber;
            if (persistedLineNumber <= 0 || !usedLineNumbers.Add(persistedLineNumber))
                throw new InvalidOperationException("Document line numbers must be positive and unique within a document.");
            await using var insertLine = CreateCommand(connection, transaction,
                "INSERT INTO sales_document_lines (doc_id, line_no, material_id, material_code, description, unit, qty, unit_price, discount_pct, tax_rate, line_subtotal, line_tax, line_total, warehouse_id) " +
                "VALUES (@doc, @line, @material, @materialCode, @description, @unit, @qty, @price, @discount, @taxRate, @subtotal, @tax, @total, @warehouse)");
            Add(insertLine, "@doc", docId);
            Add(insertLine, "@line", persistedLineNumber);
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

        // The caller owns the transaction boundary. Keeping this method
        // transaction-neutral ensures the header, lines and optional source
        // link commit or roll back as one atomic unit.
        return docId;
    }

    /// <summary>
    /// Posts a sales document and writes its accounting side effects atomically:
    /// - DLV (delivery note): one material_stock_movements row per stock line and a
    ///   matching decrement (or initialization) of material_stock_balances.
    /// - SAL (sales invoice): a party_ledger debit for the customer grand total.
    /// - QUO / SO: no side effects.
    /// </summary>
    public async Task PostSalesAsync(long docId, string userId, CancellationToken cancellationToken = default, PriceOverrideContext? priceOverride = null)
    {
        if (docId <= 0) throw new ArgumentException("Document is required.", nameof(docId));
        if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("User is required.", nameof(userId));

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string companyId, docKind, docNo, docDate, currencyCode, status;
            string? partyCode;
            long? partyId;
            long? warehouseId;
            decimal grandTotal;
            decimal subtotal = 0m, taxAmount = 0m, exchangeRate = 1m;
            string? costCenter = null;

            string approvalState = SalesApprovalState.NotRequired;
            await using (var header = CreateCommand(connection, transaction, """
                SELECT company_id, doc_kind, doc_no, doc_date, party_id, warehouse_id, currency_code, grand_total, status, party_code, subtotal, tax_amount, cost_center, exchange_rate, approval_state
                FROM sales_documents
                WHERE doc_id = @doc AND is_deleted = 0
                """))
            {
                Add(header, "@doc", docId);
                await using var reader = await header.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("Sales document was not found.");
                companyId = Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!;
                approvalState = reader.IsDBNull(14) ? SalesApprovalState.NotRequired : reader.GetString(14);
                docKind = reader.GetString(1);
                docNo = reader.GetString(2);
                docDate = Convert.ToString(reader.GetValue(3), CultureInfo.InvariantCulture)!;
                partyId = reader.IsDBNull(4) ? null : Convert.ToInt64(reader.GetValue(4), CultureInfo.InvariantCulture);
                warehouseId = reader.IsDBNull(5) ? null : Convert.ToInt64(reader.GetValue(5), CultureInfo.InvariantCulture);
                currencyCode = reader.GetString(6);
                grandTotal = Convert.ToDecimal(reader.GetValue(7), CultureInfo.InvariantCulture);
                status = reader.GetString(8);
                partyCode = reader.IsDBNull(9) ? null : reader.GetString(9);
                subtotal = reader.IsDBNull(10) ? 0m : Convert.ToDecimal(reader.GetValue(10), CultureInfo.InvariantCulture);
                taxAmount = reader.IsDBNull(11) ? 0m : Convert.ToDecimal(reader.GetValue(11), CultureInfo.InvariantCulture);
                costCenter = reader.IsDBNull(12) ? null : reader.GetString(12);
                // Step 7: a stored rate of 0 means "no document rate" —
                // it is NOT coerced to 1 here. Posting resolves the
                // effective rate through FxRates (document rate, else
                // currency_rates master, else ACC-5015).
                exchangeRate = reader.IsDBNull(13) ? 0m : Convert.ToDecimal(reader.GetValue(13), CultureInfo.InvariantCulture);
            }

            if (status != CommercialDocumentStatus.Draft)
                throw new InvalidOperationException("Sales document was not found or is not in draft status.");

            // Approval gate (W23, migration 098): only when the company
            // policy requires approval (sau_sales_settings, default OFF)
            // must a QUO/SO/SAL document be Approved before it posts.
            if (docKind is CommercialDocKinds.SalesQuotation or CommercialDocKinds.SalesOrder or CommercialDocKinds.SalesInvoice
                && approvalState != SalesApprovalState.Approved
                && await IsApprovalRequiredInTransactionAsync(connection, transaction, companyId, cancellationToken).ConfigureAwait(false))
                throw new AppError(ErrorCodes.ApprovalStateInvalid,
                    $"Document '{docNo}' requires approval before posting; its approval state is '{approvalState}'.",
                    docNo, approvalState);
            // W7a pricing guard at POST (SAL-7018/7019/7020): re-checked
            // here because limits may have tightened since the draft was
            // saved. An authorized override context (the posting user
            // holds SALES.PRICE.OVERRIDE / SALES.DISCOUNT.OVERRIDE) is
            // audited per line in spr_price_overrides instead of blocking.
            if (docKind == CommercialDocKinds.SalesInvoice)
            {
                var guardLines = new List<PriceGuardLine>();
                await using (var lineCmd = CreateCommand(connection, transaction,
                    "SELECT line_no, material_id, material_code, qty, unit_price, discount_pct FROM sales_document_lines WHERE doc_id = @doc ORDER BY line_no"))
                {
                    Add(lineCmd, "@doc", docId);
                    await using var lineReader = await lineCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    while (await lineReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        guardLines.Add(new PriceGuardLine(
                            Convert.ToInt32(lineReader.GetValue(0), CultureInfo.InvariantCulture),
                            lineReader.IsDBNull(1) ? null : Convert.ToInt64(lineReader.GetValue(1), CultureInfo.InvariantCulture),
                            lineReader.IsDBNull(2) ? null : lineReader.GetString(2),
                            Convert.ToDecimal(lineReader.GetValue(3), CultureInfo.InvariantCulture),
                            Convert.ToDecimal(lineReader.GetValue(4), CultureInfo.InvariantCulture),
                            Convert.ToDecimal(lineReader.GetValue(5), CultureInfo.InvariantCulture)));
                    }
                }
                SalesPriceGuard.Validate(connection, transaction, _provider, companyId,
                    docKind, docNo, docId, guardLines,
                    priceOverride is null ? null : priceOverride with { DocKind = docKind, DocNo = docNo, DocId = docId });
            }

            if (docKind == CommercialDocKinds.DeliveryNote)
            {
                await EnsureDeliveryWithinSalesOrderAsync(
                    connection, transaction, docId, companyId, cancellationToken).ConfigureAwait(false);

                var pending = new List<(long MaterialId, decimal Qty, long WarehouseId, string? Unit)>();
                await using (var lines = CreateCommand(connection, transaction,
                    "SELECT material_id, qty, warehouse_id, unit FROM sales_document_lines WHERE doc_id = @doc ORDER BY line_no"))
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
                                     lineWarehouse,
                                     reader.IsDBNull(3) ? null : reader.GetString(3)));
                    }
                }

                foreach (var (materialId, qty, lineWarehouse, lineUnit) in pending)
                {
                    // Service-material rule (owner, 2026-11-17): a service
                    // material is non-stock — a delivery note for it creates
                    // no movement and no balance change. (A material with no
                    // card row keeps the historical behavior and posts.)
                    if (!await IsStockableAsync(connection, transaction, materialId, cancellationToken).ConfigureAwait(false))
                        continue;

                    // Unit equivalence (owner definition, 2026-11-17): stock is
                    // kept in the material's first (base) unit, so a delivery in
                    // another unit deducts qty x equivalent(chosen unit) — the
                    // same rule StkPost applies to invoice documents.
                    var baseQty = qty * await ResolveUnitEquivalentAsync(
                        connection, transaction, materialId, lineUnit, cancellationToken).ConfigureAwait(false);
                    // Outgoing quality gate (Phase B 056, decision Q4):
                    // a delivery of an outgoing-required material needs
                    // an accepted outgoing inspection — Stop refuses
                    // (INV-6013), Warn posts and raises the pending
                    // inspection; a Rejected inspection blocks in both
                    // modes (INV-6011). The gate keys the company by
                    // CODE (the stock world), resolved from this
                    // repository's Guid key.
                    var qcCompany = PartyLedger.ResolveCompanyCode(connection, transaction, _provider, companyId);
                    var qcProfile = QualityGate.LoadProfile(connection, transaction, _provider, qcCompany, materialId);
                    if (qcProfile.OutgoingRequired)
                    {
                        QualityGate.CheckOutgoing(connection, transaction, _provider, qcCompany,
                            "DLV", docId.ToString(CultureInfo.InvariantCulture), docNo,
                            materialId, lineWarehouse, partyId, docDate, userId);
                    }
                    // Valuation (Step 6, Phase B 053): the delivery note
                    // is valued by the ONE StockValuationEngine at the
                    // material's cost method — the pre-engine code wrote
                    // unit_cost = 0 and never touched average_cost, so
                    // chain deliveries carried no cost at all (RC-1).
                    // Batch/serial (Phase B 054): batch issues
                    // auto-allocate FEFO/FIFO inside the engine; the
                    // chain has no serial capture UI, so serial-tracked
                    // lines auto-pick the oldest in-stock serials
                    // (AutoAllocateSerials) — recorded either way in
                    // stock_movement_allocations.
                    StockValuationEngine.Apply(connection, transaction, _provider,
                        new StockMovementRequest(qcCompany, lineWarehouse, materialId,
                            docDate, "DLV", docNo, QtyIn: 0m, QtyOut: baseQty,
                            Note: $"Delivery note {docNo} posting",
                            AutoAllocateSerials: true));
                }

                // Delivery GL (sales cycle B4, gap G12): under perpetual
                // inventory the delivery itself is an accounting event —
                // the stock has left Inventory (1203) but is not yet
                // billed, so its movement cost is parked in Stock
                // Delivered But Not Billed (1215) until a linked invoice
                // releases it to COGS. Posted in THIS transaction from
                // the movements just written, so stock ledger and GL can
                // never diverge. Charts without a postable 1215 fall back
                // to Inventory (the manufacturing optional-leg pattern);
                // when the fallback makes both legs the same account the
                // entry is a documented wash that keeps the delivery's
                // GL audit trail and reversal path uniform. A zero-cost
                // delivery (e.g. service lines only) posts no GL.
                var deliveryCompanyCode = PartyLedger.ResolveCompanyCode(
                    connection, transaction, _provider, companyId);
                decimal deliveryMovementCost;
                await using (var cost = CreateCommand(connection, transaction, """
                    SELECT COALESCE(SUM(cost_amount), 0)
                    FROM material_stock_movements
                    WHERE company_id = @companyCode AND document_no = @deliveryNo
                    """))
                {
                    Add(cost, "@companyCode", deliveryCompanyCode);
                    Add(cost, "@deliveryNo", docNo);
                    deliveryMovementCost = Math.Max(0m, -Convert.ToDecimal(
                        await cost.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                        CultureInfo.InvariantCulture));
                }
                if (deliveryMovementCost > 0m)
                {
                    var companyGuid = Guid.Parse(companyId);
                    var deliveredAccount = GlPosting.TryResolveAccount(
                        connection, transaction, _provider, companyGuid,
                        GlAccounts.StockDeliveredNotBilled) is not null
                            ? GlAccounts.StockDeliveredNotBilled
                            : GlAccounts.Inventory;
                    GlPosting.PostInTransaction(connection, transaction, _provider,
                        new GlDocumentRequest(companyGuid,
                            DateOnly.Parse(docDate, CultureInfo.InvariantCulture),
                            $"DLV {docNo}", CommercialDocKinds.DeliveryNote,
                            docId.ToString(CultureInfo.InvariantCulture), docNo,
                            currencyCode, exchangeRate,
                            new List<GlLineRequest>
                            {
                                new(deliveredAccount, deliveryMovementCost, 0m),
                                new(GlAccounts.Inventory, 0m, deliveryMovementCost)
                            }),
                        userId);
                }
            }
            else if (docKind == CommercialDocKinds.SalesInvoice && partyId is not null)
            {
                // Step 7 (J-5): resolve the effective rate ONCE through
                // FxRates (document rate, else master, else ACC-5015);
                // the guard and the ledger both consume BASE amounts at
                // this rate, and GlPosting below resolves identically
                // from the same stored inputs.
                var fx = FxRates.Resolve(connection, transaction, _provider,
                    Guid.Parse(companyId), currencyCode,
                    DateOnly.Parse(docDate, CultureInfo.InvariantCulture), exchangeRate);

                // Credit-limit gate (ACC-5006): the ledger debit below must
                // not push the customer over the limit on their party card.
                // Step 7: the guard sums base amounts, so it is fed the
                // document's BASE total, not the raw foreign total.
                await PartyCreditGuard.EnsureWithinLimitAsync(
                    connection, transaction, _provider, companyId, "C", partyId.Value, partyCode,
                    FxRates.ToBase(grandTotal, fx.Rate), "@", cancellationToken).ConfigureAwait(false);

                // Step 4 (B-06): party_ledger.company_id is the company
                // CODE; this repository's documents key the company by
                // Guid, so resolve before writing (the guard above accepts
                // both of the company's own keys).
                var ledgerCompany = await PartyLedger.ResolveCompanyCodeAsync(
                    connection, transaction, _provider, companyId, cancellationToken).ConfigureAwait(false);

                // Step 7: written through PartyLedger.WriteEntry so the
                // row carries base_debit/base_credit + the effective
                // rate alongside the transaction amounts.
                PartyLedger.WriteEntry(connection, transaction, _provider,
                    ledgerCompany, "C", partyId.Value,
                    CommercialDocKinds.SalesInvoice, docId, docNo, docDate,
                    grandTotal, 0m, currencyCode,
                    $"Sales invoice {docNo} posting",
                    exchangeRate: fx.Rate);
            }

            // Real GL posting (Step 3, J-2): a posted chain SALES INVOICE
            // creates its balanced journal entry in this same transaction
            // (Dr Receivable / Cr Revenue, tax split only when a postable
            // VAT account is configured). Since sales cycle B4 the DLV
            // carries its own GL entry (Dr 1215 / Cr 1203 at movement
            // cost, above); QUO/SO keep no GL entry.
            if (docKind == CommercialDocKinds.SalesInvoice)
            {
                // A DLV-linked invoice does not issue stock again. Its COGS
                // is released from the cost already captured by the delivery
                // note's stock movements, allocated pro-rata per material
                // across every invoice that bills the delivery, and credited
                // against Stock Delivered But Not Billed (1215) — or against
                // Inventory (1203) when the delivery itself fell back to it.
                var (deliveryCogs, deliveryCogsCreditAccount) = await GetDeliveryCogsForInvoiceAsync(
                    connection, transaction, docId, companyId, cancellationToken).ConfigureAwait(false);

                if (grandTotal > 0m || deliveryCogs > 0m)
                {
                    var companyGuid = Guid.Parse(companyId);
                    var splitTax = taxAmount > 0
                        && GlPosting.TryResolveAccount(connection, transaction, _provider, companyGuid, GlAccounts.OutputVat) is not null;
                    var glLines = new List<GlLineRequest>();
                    if (grandTotal > 0m)
                    {
                        glLines.Add(new GlLineRequest(GlAccounts.Receivable, grandTotal, 0m));
                        glLines.Add(new GlLineRequest(GlAccounts.SalesRevenue, 0m, splitTax ? subtotal : grandTotal, costCenter));
                        if (splitTax)
                            glLines.Add(new GlLineRequest(GlAccounts.OutputVat, 0m, taxAmount));
                    }
                    if (deliveryCogs > 0m)
                    {
                        glLines.Add(new GlLineRequest(GlAccounts.Cogs, deliveryCogs, 0m, costCenter));
                        glLines.Add(new GlLineRequest(deliveryCogsCreditAccount, 0m, deliveryCogs));
                    }

                    GlPosting.PostInTransaction(connection, transaction, _provider,
                        new GlDocumentRequest(companyGuid,
                            DateOnly.Parse(docDate, CultureInfo.InvariantCulture),
                            $"SAL {docNo}", CommercialDocKinds.SalesInvoice,
                            docId.ToString(CultureInfo.InvariantCulture), docNo,
                            currencyCode, exchangeRate, glLines),
                        userId);
                }
            }

            await using var update = CreateCommand(connection, transaction,
                "UPDATE sales_documents SET status = @posted, posted_by = @actor, posted_at = CURRENT_TIMESTAMP WHERE doc_id = @doc AND status = @draft AND is_deleted = 0");
            Add(update, "@posted", CommercialDocumentStatus.Posted);
            Add(update, "@actor", userId.Trim());
            Add(update, "@doc", docId);
            Add(update, "@draft", CommercialDocumentStatus.Draft);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("Sales document was not found or is not in draft status.");

            // Keep the source order's stored per_delivered / per_billed in
            // step with this posting inside the same transaction, so direct
            // repository posters (not only OrderService callers) refresh it.
            await RefreshSourceOrderProgressAsync(
                connection, transaction, companyId, docKind, docId, cancellationToken).ConfigureAwait(false);

            // W8: unified document event (migration 103), same transaction.
            DocEvents.Write(connection, transaction, _provider,
                companyId, "SALES", docKind, docId, docNo,
                DocEventTypes.Posted, userId,
                $"grand_total={grandTotal.ToString(CultureInfo.InvariantCulture)} {currencyCode}");

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Preserve the original database/business exception. A provider may
            // already have completed or aborted the transaction; rollback is
            // best-effort and disposal remains the final cleanup guarantee.
            try
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // Transaction is already completed; do not mask the root error.
            }
            throw;
        }
    }

    /// <summary>
    /// Reverses a posted chain document (sales cycle B4, gap G13) in ONE
    /// transaction — the chain counterpart of InvRepo's invoice reversal:
    /// - DLV: the delivery's stock movements are reversed through
    ///   StockValuationEngine at the ORIGINAL movement cost (a receipt
    ///   per material/warehouse with ReceiptCostAmount = the original
    ///   cost and OriginalDocumentNo set, so batch/serial allocations
    ///   mirror the original issue and net to zero), its GL entry
    ///   (Dr 1215 / Cr 1203) is reversed with a compensating journal,
    ///   and the document is marked Cancelled.
    /// - SAL: its GL entry (receivable / revenue / VAT / COGS legs) is
    ///   reversed with a compensating journal and the party-ledger debit
    ///   is mirrored by an equal credit in the same transaction.
    /// A document_reversals row (reason + actor mandatory) records the
    /// reversal, and the source sales order's stored per_delivered /
    /// per_billed are recomputed when those columns exist.
    /// Guards: a DLV with any posted linked invoice → SAL-7005; a SAL
    /// with paid_amount &gt; 0 or existing payment allocations →
    /// SAL-7006; any other kind/status, a company mismatch, or an
    /// already-recorded reversal → SAL-7007.
    /// </summary>
    public async Task ReverseChainDocumentAsync(
        Guid companyId, long docId, string reason, string actor,
        CancellationToken cancellationToken = default)
    {
        if (companyId == Guid.Empty) throw new ArgumentException("Company is required.", nameof(companyId));
        if (docId <= 0) throw new ArgumentException("Document is required.", nameof(docId));
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A reversal reason is required.", nameof(reason));
        reason = reason.Trim();
        if (reason.Length > 1000) throw new ArgumentException("The reversal reason cannot exceed 1000 characters.", nameof(reason));
        if (string.IsNullOrWhiteSpace(actor)) throw new ArgumentException("Actor is required.", nameof(actor));
        actor = actor.Trim();

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string companyText, docKind, docNo, docDate, currencyCode, status;
            long? partyId;
            decimal grandTotal, paidAmount, exchangeRate;
            await using (var header = CreateCommand(connection, transaction, """
                SELECT company_id, doc_kind, doc_no, doc_date, party_id, currency_code,
                       grand_total, paid_amount, status, exchange_rate
                FROM sales_documents
                WHERE doc_id = @doc AND is_deleted = 0
                """))
            {
                Add(header, "@doc", docId);
                await using var reader = await header.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    throw new AppError(ErrorCodes.ChainDocumentNotReversible,
                        "The document was not found and cannot be reversed.",
                        docId.ToString(CultureInfo.InvariantCulture));
                companyText = Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!;
                docKind = reader.GetString(1);
                docNo = reader.GetString(2);
                docDate = Convert.ToString(reader.GetValue(3), CultureInfo.InvariantCulture)!;
                partyId = reader.IsDBNull(4) ? null : Convert.ToInt64(reader.GetValue(4), CultureInfo.InvariantCulture);
                currencyCode = reader.GetString(5);
                grandTotal = Convert.ToDecimal(reader.GetValue(6), CultureInfo.InvariantCulture);
                paidAmount = Convert.ToDecimal(reader.GetValue(7), CultureInfo.InvariantCulture);
                status = reader.GetString(8);
                exchangeRate = reader.IsDBNull(9) ? 0m : Convert.ToDecimal(reader.GetValue(9), CultureInfo.InvariantCulture);
            }

            if (!string.Equals(companyText, companyId.ToString("D"), StringComparison.OrdinalIgnoreCase) ||
                status != CommercialDocumentStatus.Posted ||
                docKind is not (CommercialDocKinds.DeliveryNote or CommercialDocKinds.SalesInvoice))
                throw new AppError(ErrorCodes.ChainDocumentNotReversible,
                    $"Only a posted delivery note or sales invoice can be reversed; document '{docNo}' is a {docKind} in status '{status}'.",
                    docNo, docKind, status);

            await using (var existing = CreateCommand(connection, transaction, """
                SELECT COUNT(*) FROM document_reversals
                WHERE company_id = @company AND original_doc_kind = @kind AND original_doc_id = @doc
                """))
            {
                Add(existing, "@company", companyId.ToString("D"));
                Add(existing, "@kind", docKind);
                Add(existing, "@doc", docId);
                if (Convert.ToInt64(await existing.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                        CultureInfo.InvariantCulture) > 0)
                    throw new AppError(ErrorCodes.ChainDocumentNotReversible,
                        $"Document '{docNo}' has already been reversed.", docNo);
            }

            if (docKind == CommercialDocKinds.DeliveryNote)
            {
                // Dependency guard: a posted invoice already released
                // this delivery's COGS — it must be reversed first.
                await using var downstream = CreateCommand(connection, transaction, """
                    SELECT COUNT(*)
                    FROM document_links link
                    INNER JOIN sales_documents invoice
                        ON invoice.company_id = link.company_id
                       AND invoice.doc_id = link.target_doc_id
                       AND invoice.doc_kind = link.target_doc_kind
                    WHERE link.company_id = @company
                      AND link.source_doc_kind = @deliveryKind
                      AND link.source_doc_id = @doc
                      AND link.target_doc_kind = @invoiceKind
                      AND invoice.status = @posted
                      AND CAST(invoice.is_deleted AS TEXT) IN ('0', 'false')
                    """);
                Add(downstream, "@company", companyText);
                Add(downstream, "@deliveryKind", CommercialDocKinds.DeliveryNote);
                Add(downstream, "@doc", docId);
                Add(downstream, "@invoiceKind", CommercialDocKinds.SalesInvoice);
                Add(downstream, "@posted", CommercialDocumentStatus.Posted);
                if (Convert.ToInt64(await downstream.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                        CultureInfo.InvariantCulture) > 0)
                    throw new AppError(ErrorCodes.ChainReversalBlockedDownstream,
                        $"Delivery note '{docNo}' cannot be reversed while a posted sales invoice is linked to it. Reverse the invoice first.",
                        docNo);

                // Stock reversal at the ORIGINAL movement cost, grouped
                // per material/warehouse exactly as the delivery issued.
                var stockCompany = PartyLedger.ResolveCompanyCode(
                    connection, transaction, _provider, companyText);
                var originalMovements = new List<(long MaterialId, long WarehouseId, decimal Qty, decimal Cost)>();
                await using (var movements = CreateCommand(connection, transaction, """
                    SELECT material_id, warehouse_id, SUM(qty_out), SUM(-cost_amount)
                    FROM material_stock_movements
                    WHERE company_id = @companyCode AND document_no = @deliveryNo AND qty_out > 0
                    GROUP BY material_id, warehouse_id
                    """))
                {
                    Add(movements, "@companyCode", stockCompany);
                    Add(movements, "@deliveryNo", docNo);
                    await using var reader = await movements.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        originalMovements.Add((
                            Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                            Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                            Convert.ToDecimal(reader.GetValue(2), CultureInfo.InvariantCulture),
                            Math.Max(0m, Convert.ToDecimal(reader.GetValue(3), CultureInfo.InvariantCulture))));
                    }
                }
                foreach (var (materialId, warehouseId, qty, cost) in originalMovements)
                {
                    if (qty <= 0m)
                        continue;
                    StockValuationEngine.Apply(connection, transaction, _provider,
                        new StockMovementRequest(stockCompany, warehouseId, materialId,
                            docDate, CommercialDocKinds.DeliveryNote, docNo + "-REV",
                            QtyIn: qty, QtyOut: 0m,
                            ReceiptCostAmount: cost,
                            Note: $"Reversal of delivery note {docNo}: {reason}",
                            OriginalDocumentNo: docNo,
                            Actor: actor));
                }
            }
            else
            {
                // Payment guard: money already received or allocated
                // against this invoice must be unallocated / refunded
                // through the payment workflow first.
                if (paidAmount > 0m)
                    throw new AppError(ErrorCodes.ChainReversalBlockedPayments,
                        $"Sales invoice '{docNo}' cannot be reversed: it carries a paid amount of {paidAmount}. Reverse its payments first.",
                        docNo);
                if (PartyLedger.TableExists(connection, transaction, _provider, "payment_allocations"))
                {
                    var ledgerCompany = PartyLedger.ResolveCompanyCode(
                        connection, transaction, _provider, companyText);
                    await using var allocations = CreateCommand(connection, transaction, """
                        SELECT COUNT(*) FROM payment_allocations
                        WHERE doc_table = 'SALES' AND doc_id = @doc
                          AND (company_id = @companyCode OR company_id = @companyGuid
                               OR document_company_id = @companyGuid)
                        """);
                    Add(allocations, "@doc", docId);
                    Add(allocations, "@companyCode", ledgerCompany);
                    Add(allocations, "@companyGuid", companyId.ToString("D"));
                    if (Convert.ToInt64(await allocations.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                            CultureInfo.InvariantCulture) > 0)
                        throw new AppError(ErrorCodes.ChainReversalBlockedPayments,
                            $"Sales invoice '{docNo}' cannot be reversed: payments are allocated to it. Unallocate them first.",
                            docNo);
                }
            }

            // GL reversal: the compensating journal mirrors every leg of
            // the document's own entry (delivery 1215/1203, or invoice
            // receivable / revenue / VAT / COGS) in the same transaction.
            var glEntryId = GlPosting.FindEntryBySource(connection, transaction, _provider,
                companyId, docKind, docId.ToString(CultureInfo.InvariantCulture));
            if (glEntryId is not null)
                GlPosting.ReverseInTransaction(connection, transaction, _provider,
                    glEntryId.Value, actor, $"Chain reversal of {docKind} {docNo}: {reason}");

            if (docKind == CommercialDocKinds.SalesInvoice && partyId is not null)
            {
                // Mirror the party-ledger debit written at posting with
                // an equal credit (the chain rows carry no journal link,
                // so GlPosting's journal mirror does not touch them).
                var fx = FxRates.Resolve(connection, transaction, _provider,
                    companyId, currencyCode,
                    DateOnly.Parse(docDate, CultureInfo.InvariantCulture), exchangeRate);
                var ledgerCompany = await PartyLedger.ResolveCompanyCodeAsync(
                    connection, transaction, _provider, companyText, cancellationToken).ConfigureAwait(false);
                PartyLedger.WriteEntry(connection, transaction, _provider,
                    ledgerCompany, "C", partyId.Value,
                    CommercialDocKinds.SalesInvoice, docId, docNo, docDate,
                    0m, grandTotal, currencyCode,
                    $"Reversal of sales invoice {docNo}: {reason}",
                    exchangeRate: fx.Rate);
            }

            await using (var update = CreateCommand(connection, transaction,
                "UPDATE sales_documents SET status = @cancelled WHERE doc_id = @doc AND status = @posted AND is_deleted = 0"))
            {
                Add(update, "@cancelled", CommercialDocumentStatus.Cancelled);
                Add(update, "@doc", docId);
                Add(update, "@posted", CommercialDocumentStatus.Posted);
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new AppError(ErrorCodes.ChainDocumentNotReversible,
                        $"Document '{docNo}' changed status while its reversal was being recorded.", docNo);
            }

            await using (var reversal = CreateCommand(connection, transaction, """
                INSERT INTO document_reversals
                    (reversal_id, company_id, original_doc_kind, original_doc_id,
                     reversal_doc_kind, reversal_doc_id, reason, actor, created_at, status)
                VALUES (@rid, @company, @kind, @doc, @kind, NULL, @reason, @actor, @createdAt, 'Completed')
                """))
            {
                Add(reversal, "@rid", Guid.NewGuid().ToString("N"));
                Add(reversal, "@company", companyId.ToString("D"));
                Add(reversal, "@kind", docKind);
                Add(reversal, "@doc", docId);
                Add(reversal, "@reason", reason);
                Add(reversal, "@actor", actor);
                Add(reversal, "@createdAt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                await reversal.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await RefreshSourceOrderProgressAsync(
                connection, transaction, companyText, docKind, docId, cancellationToken).ConfigureAwait(false);

            // W8: unified document event (migration 103), same transaction.
            DocEvents.Write(connection, transaction, _provider,
                companyText, "SALES", docKind, docId, docNo,
                DocEventTypes.Reversed, actor, reason);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // Transaction is already completed; do not mask the root error.
            }
            throw;
        }
    }

    /// <summary>
    /// Recomputes the stored per_delivered / per_billed of the source
    /// sales order(s) of a just-reversed chain document, using the same
    /// quantity-weighted formula as OrderDocumentRepository.GetProgressAsync.
    /// The columns are added by a parallel workstream (sales cycle B2)
    /// and do not exist on every branch yet, so the update runs only
    /// when both columns are present — on a base without them the
    /// computed-on-read progress already reflects the reversal because
    /// the reversed document is no longer Posted.
    /// </summary>
    private async Task RefreshSourceOrderProgressAsync(
        DbConnection connection,
        DbTransaction transaction,
        string companyId,
        string targetDocKind,
        long targetDocId,
        CancellationToken cancellationToken)
    {
        if (!PartyLedger.ColumnExists(connection, transaction, _provider, "sales_documents", "per_delivered") ||
            !PartyLedger.ColumnExists(connection, transaction, _provider, "sales_documents", "per_billed"))
            return;

        // Source orders of the reversed document: direct SO -> target
        // links, plus SO -> DLV -> SAL when an invoice is reversed.
        var orderIds = new List<long>();
        async Task CollectSourceOrdersAsync(string sourceKind, long fromDocId)
        {
            await using var links = CreateCommand(connection, transaction, """
                SELECT source_doc_id FROM document_links
                WHERE company_id = @company AND source_doc_kind = @sourceKind
                  AND target_doc_id = @targetId
                """);
            Add(links, "@company", companyId);
            Add(links, "@sourceKind", sourceKind);
            Add(links, "@targetId", fromDocId);
            await using var reader = await links.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var id = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
                if (!orderIds.Contains(id))
                    orderIds.Add(id);
            }
        }
        await CollectSourceOrdersAsync(CommercialDocKinds.SalesOrder, targetDocId).ConfigureAwait(false);
        if (targetDocKind == CommercialDocKinds.SalesInvoice)
        {
            var deliveryIds = new List<long>();
            await using (var links = CreateCommand(connection, transaction, """
                SELECT source_doc_id FROM document_links
                WHERE company_id = @company AND source_doc_kind = @deliveryKind
                  AND target_doc_kind = @invoiceKind AND target_doc_id = @targetId
                """))
            {
                Add(links, "@company", companyId);
                Add(links, "@deliveryKind", CommercialDocKinds.DeliveryNote);
                Add(links, "@invoiceKind", CommercialDocKinds.SalesInvoice);
                Add(links, "@targetId", targetDocId);
                await using var reader = await links.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    deliveryIds.Add(Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture));
            }
            foreach (var deliveryId in deliveryIds)
                await CollectSourceOrdersAsync(CommercialDocKinds.SalesOrder, deliveryId).ConfigureAwait(false);
        }

        async Task<decimal> SumLinkedPostedQtyAsync(long orderId, string linkedKind)
        {
            await using var command = CreateCommand(connection, transaction, """
                SELECT COALESCE(SUM(line.qty), 0)
                FROM document_links link
                INNER JOIN sales_documents target
                    ON target.company_id = link.company_id
                   AND target.doc_id = link.target_doc_id
                   AND target.doc_kind = link.target_doc_kind
                INNER JOIN sales_document_lines line ON line.doc_id = target.doc_id
                WHERE link.company_id = @company
                  AND link.source_doc_kind = @orderKind
                  AND link.source_doc_id = @orderId
                  AND link.target_doc_kind = @linkedKind
                  AND target.status = @posted
                  AND CAST(target.is_deleted AS TEXT) IN ('0', 'false')
                """);
            Add(command, "@company", companyId);
            Add(command, "@orderKind", CommercialDocKinds.SalesOrder);
            Add(command, "@orderId", orderId);
            Add(command, "@linkedKind", linkedKind);
            Add(command, "@posted", CommercialDocumentStatus.Posted);
            return Convert.ToDecimal(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
        }

        foreach (var orderId in orderIds)
        {
            decimal orderedQty;
            await using (var ordered = CreateCommand(connection, transaction,
                "SELECT COALESCE(SUM(qty), 0) FROM sales_document_lines WHERE doc_id = @orderId"))
            {
                Add(ordered, "@orderId", orderId);
                orderedQty = Convert.ToDecimal(await ordered.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    CultureInfo.InvariantCulture);
            }
            var deliveredQty = await SumLinkedPostedQtyAsync(orderId, CommercialDocKinds.DeliveryNote).ConfigureAwait(false);
            var billedQty = await SumLinkedPostedQtyAsync(orderId, CommercialDocKinds.SalesInvoice).ConfigureAwait(false);
            var perDelivered = orderedQty <= 0m ? 0m : decimal.Round(100m * deliveredQty / orderedQty, 2);
            var perBilled = orderedQty <= 0m ? 0m : decimal.Round(100m * billedQty / orderedQty, 2);
            await using var update = CreateCommand(connection, transaction,
                "UPDATE sales_documents SET per_delivered = @delivered, per_billed = @billed WHERE doc_id = @orderId");
            Add(update, "@delivered", perDelivered);
            Add(update, "@billed", perBilled);
            Add(update, "@orderId", orderId);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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

    public async Task<IReadOnlyList<CommercialDocumentLink>> GetSalesLinksToAsync(Guid companyId, long targetDocId, CancellationToken cancellationToken = default)
    {
        if (companyId == Guid.Empty) throw new ArgumentException("Company is required.", nameof(companyId));
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT link_id, company_id, source_doc_kind, source_doc_id, target_doc_kind, target_doc_id
            FROM document_links
            WHERE company_id = @company AND target_doc_id = @targetId
            ORDER BY link_id
            """;
        Add(command, "@company", companyId.ToString("D"));
        Add(command, "@targetId", targetDocId);

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

    public async Task<IReadOnlyDictionary<int, decimal>> GetFulfilledQuantitiesBySourceLineAsync(Guid companyId, long sourceDocId, string targetDocKind, CancellationToken cancellationToken = default)
    {
        if (companyId == Guid.Empty) throw new ArgumentException("Company is required.", nameof(companyId));
        if (string.IsNullOrWhiteSpace(targetDocKind)) throw new ArgumentException("Target document kind is required.", nameof(targetDocKind));

        var source = await FindSalesByIdAsync(sourceDocId, cancellationToken).ConfigureAwait(false)
                     ?? throw new InvalidOperationException($"Source sales document '{sourceDocId}' was not found.");
        if (source.CompanyId != companyId)
            throw new InvalidOperationException("Source document belongs to a different company.");

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT l.line_no, l.material_id, SUM(l.qty)
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
            GROUP BY l.line_no, l.material_id
            """;
        Add(command, "@company", companyId.ToString("D"));
        Add(command, "@sourceId", sourceDocId);
        Add(command, "@targetKind", targetDocKind.Trim());
        Add(command, "@posted", CommercialDocumentStatus.Posted);

        var quantities = new Dictionary<int, decimal>();
        var legacyByMaterial = new Dictionary<long, decimal>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var lineNo = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
            var materialId = reader.IsDBNull(1) ? (long?)null : Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
            var qty = Convert.ToDecimal(reader.GetValue(2), CultureInfo.InvariantCulture);
            // Chain-created target lines persist line_no = source line
            // number AND the source line's material: an exact identity
            // match. Anything else is a legacy row, pooled by material
            // below (mirrors the purchase branch's fallback).
            var sourceLineExists = source.Lines.Any(line => line.LineNo == lineNo && line.MaterialId == materialId);
            if (sourceLineExists)
                quantities[lineNo] = quantities.GetValueOrDefault(lineNo) + qty;
            else if (materialId is long legacyMaterialId)
                legacyByMaterial[legacyMaterialId] = legacyByMaterial.GetValueOrDefault(legacyMaterialId) + qty;
        }

        foreach (var (materialId, legacyQty) in legacyByMaterial)
        {
            var remaining = legacyQty;
            foreach (var sourceLine in source.Lines
                .Where(line => line.MaterialId == materialId)
                .OrderBy(line => line.LineNo))
            {
                var available = Math.Max(0m, sourceLine.Qty - quantities.GetValueOrDefault(sourceLine.LineNo));
                var take = Math.Min(available, remaining);
                if (take <= 0m) continue;
                quantities[sourceLine.LineNo] = quantities.GetValueOrDefault(sourceLine.LineNo) + take;
                remaining -= take;
                if (remaining <= 0m) break;
            }
        }

        return quantities;
    }

    private DbConnection CreateConnection() => _provider == "SQLite"
        ? new SqliteConnection(_connectionString)
        : new NpgsqlConnection(_connectionString);

    /// <summary>
    /// Resolves the equivalence of a line's chosen unit relative to the
    /// material's first (base) unit, from the unit rows saved on the material
    /// card (units_json carries the complete list; the unit1..unit5 column
    /// pairs are the fallback for legacy rows). Mirrors the rule StkPost
    /// applies to invoice documents: no unit chosen, the base unit itself, an
    /// unknown or free-typed unit, or a stored equivalent of 0 or less all
    /// resolve to 1. The connection is already scoped to one company's
    /// database, so the lookup keys on material_id alone.
    /// </summary>
    private static Task<decimal> ResolveUnitEquivalentAsync(
        DbConnection connection, DbTransaction transaction, long materialId, string? unitName,
        CancellationToken cancellationToken)
        // Step 11: the rule lives ONCE in UnitEquivalents (shared with
        // MfgOrderRepository and OrderDocumentRepository); this wrapper
        // keeps the chain repository's material_id-only scoping.
        => UnitEquivalents.ResolveAsync(connection, transaction, null, materialId, unitName, cancellationToken);

    /// <summary>
    /// Whether the material is stock-tracked, mirroring the guard StkPost
    /// applies to invoice documents: not stocked, flagged as a service, or
    /// typed as a Service (any one of the three views the material card
    /// keeps in agreement) means non-stock. A material with no card row
    /// keeps the historical chain behavior and is treated as stockable.
    /// </summary>
    private static async Task<bool> IsStockableAsync(
        DbConnection connection, DbTransaction transaction, long materialId,
        CancellationToken cancellationToken)
    {
        await using var cmd = CreateCommand(connection, transaction, """
            SELECT is_stocked, is_service, item_type
            FROM materials
            WHERE material_id = @material
            """);
        Add(cmd, "@material", materialId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return true;

        var isStocked = !reader.IsDBNull(0) && Convert.ToBoolean(reader.GetValue(0), CultureInfo.InvariantCulture);
        var isService = !reader.IsDBNull(1) && Convert.ToBoolean(reader.GetValue(1), CultureInfo.InvariantCulture);
        var itemType = reader.IsDBNull(2) ? null : reader.GetValue(2)?.ToString();
        return isStocked
               && !isService
               && !string.Equals(itemType, "Service", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Enforces the source sales-order quantity ceiling at delivery posting time.
    /// Draft delivery notes do not consume order quantity; the guard runs inside
    /// the same transaction as stock posting so a second concurrent dispatch
    /// cannot post against quantities already delivered by the first.
    /// </summary>
    private async Task EnsureDeliveryWithinSalesOrderAsync(
        DbConnection connection,
        DbTransaction transaction,
        long deliveryDocId,
        string companyId,
        CancellationToken cancellationToken)
    {
        long? sourceOrderId = null;
        await using (var sourceLink = CreateCommand(connection, transaction, """
            SELECT source_doc_id
            FROM document_links
            WHERE company_id = @company
              AND target_doc_kind = @deliveryKind
              AND target_doc_id = @deliveryId
              AND source_doc_kind = @orderKind
            """))
        {
            Add(sourceLink, "@company", companyId);
            Add(sourceLink, "@deliveryKind", CommercialDocKinds.DeliveryNote);
            Add(sourceLink, "@deliveryId", deliveryDocId);
            Add(sourceLink, "@orderKind", CommercialDocKinds.SalesOrder);
            var value = await sourceLink.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (value is not null and not DBNull)
                sourceOrderId = Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }

        // A manually entered, unlinked delivery note follows the legacy path.
        // Linked deliveries must always respect their originating posted order.
        if (sourceOrderId is null)
            return;

        await using (var orderLock = CreateCommand(connection, transaction,
            _provider == "PostgreSQL"
                ? """
                  SELECT doc_id
                  FROM sales_documents
                  WHERE doc_id = @orderId AND company_id = @company
                    AND doc_kind = @orderKind AND status = @posted
                    AND CAST(is_deleted AS TEXT) IN ('0', 'false')
                  FOR UPDATE
                  """
                : """
                  SELECT doc_id
                  FROM sales_documents
                  WHERE doc_id = @orderId AND company_id = @company
                    AND doc_kind = @orderKind AND status = @posted
                    AND CAST(is_deleted AS TEXT) IN ('0', 'false')
                  """))
        {
            Add(orderLock, "@orderId", sourceOrderId.Value);
            Add(orderLock, "@company", companyId);
            Add(orderLock, "@orderKind", CommercialDocKinds.SalesOrder);
            Add(orderLock, "@posted", CommercialDocumentStatus.Posted);
            var locked = await orderLock.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (locked is null or DBNull)
                throw new InvalidOperationException("The source sales order is missing, cancelled, or not posted.");
        }

        async Task<Dictionary<(long MaterialId, string Unit), decimal>> ReadQuantitiesAsync(
            string sql, params (string Name, object Value)[] parameters)
        {
            var result = new Dictionary<(long MaterialId, string Unit), decimal>();
            await using var command = CreateCommand(connection, transaction, sql);
            foreach (var (name, value) in parameters)
                Add(command, name, value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var materialId = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
                var unit = Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) ?? string.Empty;
                result[(materialId, unit)] = Convert.ToDecimal(reader.GetValue(2), CultureInfo.InvariantCulture);
            }
            return result;
        }

        const string groupByMaterialAndUnit = """
            SELECT COALESCE(material_id, -1), COALESCE(unit, ''), SUM(qty)
            FROM sales_document_lines
            WHERE doc_id = @docId
            GROUP BY material_id, unit
            """;

        var ordered = await ReadQuantitiesAsync(groupByMaterialAndUnit, ("@docId", sourceOrderId.Value)).ConfigureAwait(false);
        var current = await ReadQuantitiesAsync(groupByMaterialAndUnit, ("@docId", deliveryDocId)).ConfigureAwait(false);
        var previouslyDelivered = await ReadQuantitiesAsync("""
            SELECT COALESCE(line.material_id, -1), COALESCE(line.unit, ''), SUM(line.qty)
            FROM document_links link
            INNER JOIN sales_documents delivery
                ON delivery.company_id = link.company_id
               AND delivery.doc_id = link.target_doc_id
               AND delivery.doc_kind = link.target_doc_kind
            INNER JOIN sales_document_lines line ON line.doc_id = delivery.doc_id
            WHERE link.company_id = @company
              AND link.source_doc_kind = @orderKind
              AND link.source_doc_id = @orderId
              AND link.target_doc_kind = @deliveryKind
              AND delivery.status = @posted
              AND CAST(delivery.is_deleted AS TEXT) IN ('0', 'false')
              AND delivery.doc_id <> @currentId
            GROUP BY line.material_id, line.unit
            """,
            ("@company", companyId),
            ("@orderKind", CommercialDocKinds.SalesOrder),
            ("@orderId", sourceOrderId.Value),
            ("@deliveryKind", CommercialDocKinds.DeliveryNote),
            ("@posted", CommercialDocumentStatus.Posted),
            ("@currentId", deliveryDocId)).ConfigureAwait(false);

        foreach (var (key, currentQty) in current)
        {
            var orderedQty = ordered.GetValueOrDefault(key);
            var deliveredQty = previouslyDelivered.GetValueOrDefault(key);
            if (orderedQty <= 0m || deliveredQty + currentQty > orderedQty)
            {
                var materialLabel = key.MaterialId < 0
                    ? "unassigned material"
                    : key.MaterialId.ToString(CultureInfo.InvariantCulture);
                throw new InvalidOperationException(
                    $"Delivery quantity for material '{materialLabel}' and unit '{key.Unit}' exceeds the remaining quantity on sales order '{sourceOrderId}'.");
            }
        }
    }

    /// <summary>
    /// Allocates the delivery cost released by this sales invoice
    /// (sales cycle B4, gap G14: partial / multiple invoicing of one
    /// delivery). Per material, the delivery's total stock-movement cost
    /// is spread over the delivered BASE quantity (movement qty_out, so
    /// unit equivalence is already applied): unit cost = movement cost /
    /// delivered qty. This invoice releases unit cost × its invoiced base
    /// qty, rounded to cents; the invoice that completes a material takes
    /// the exact remaining cost (movement cost − what earlier invoices
    /// released, each derived with the same formula), so the COGS of all
    /// invoices against one delivery sums to the delivery's movement
    /// cost to the cent. Already-invoiced quantities are read from the
    /// OTHER posted invoices linked to the same delivery; the delivery
    /// row is locked first so two invoices cannot allocate concurrently.
    /// Failures (invoicing more than was delivered, or a stocked
    /// material the delivery never moved) throw SAL-7009.
    /// Returns the COGS amount and the account to credit: Stock
    /// Delivered But Not Billed (1215) when the delivery's own GL entry
    /// debited it, otherwise Inventory (1203) — the delivery fell back
    /// to 1203 on a chart without 1215, or predates delivery GL.
    /// </summary>
    private async Task<(decimal Cogs, string CreditAccount)> GetDeliveryCogsForInvoiceAsync(
        DbConnection connection,
        DbTransaction transaction,
        long invoiceDocId,
        string companyId,
        CancellationToken cancellationToken)
    {
        long? deliveryDocId = null;
        await using (var link = CreateCommand(connection, transaction, """
            SELECT source_doc_id
            FROM document_links
            WHERE company_id = @company
              AND source_doc_kind = @deliveryKind
              AND target_doc_kind = @invoiceKind
              AND target_doc_id = @invoiceId
            """))
        {
            Add(link, "@company", companyId);
            Add(link, "@deliveryKind", CommercialDocKinds.DeliveryNote);
            Add(link, "@invoiceKind", CommercialDocKinds.SalesInvoice);
            Add(link, "@invoiceId", invoiceDocId);
            var value = await link.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (value is not null and not DBNull)
                deliveryDocId = Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }

        if (deliveryDocId is null)
            return (0m, GlAccounts.Inventory);

        string deliveryNo;
        await using (var deliveryLock = CreateCommand(connection, transaction,
            _provider == "PostgreSQL"
                ? """
                  SELECT doc_no
                  FROM sales_documents
                  WHERE doc_id = @deliveryId AND company_id = @company
                    AND doc_kind = @deliveryKind AND status = @posted
                    AND CAST(is_deleted AS TEXT) IN ('0', 'false')
                  FOR UPDATE
                  """
                : """
                  SELECT doc_no
                  FROM sales_documents
                  WHERE doc_id = @deliveryId AND company_id = @company
                    AND doc_kind = @deliveryKind AND status = @posted
                    AND CAST(is_deleted AS TEXT) IN ('0', 'false')
                  """))
        {
            Add(deliveryLock, "@deliveryId", deliveryDocId.Value);
            Add(deliveryLock, "@company", companyId);
            Add(deliveryLock, "@deliveryKind", CommercialDocKinds.DeliveryNote);
            Add(deliveryLock, "@posted", CommercialDocumentStatus.Posted);
            var value = await deliveryLock.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (value is null or DBNull)
                throw new InvalidOperationException("The source delivery note is missing or is not posted.");
            deliveryNo = Convert.ToString(value, CultureInfo.InvariantCulture)!;
        }

        var companyCode = await PartyLedger.ResolveCompanyCodeAsync(
            connection, transaction, _provider, companyId, cancellationToken).ConfigureAwait(false);

        // The delivery's valued stock movements, per material: base
        // quantity out and its total cost (cost_amount is negative on
        // issues). Service / non-stock lines wrote no movement and
        // release no COGS.
        var deliveredByMaterial = new Dictionary<long, (decimal BaseQty, decimal Cost)>();
        await using (var movements = CreateCommand(connection, transaction, """
            SELECT material_id, SUM(qty_out), SUM(-cost_amount)
            FROM material_stock_movements
            WHERE company_id = @companyCode AND document_no = @deliveryNo AND qty_out > 0
            GROUP BY material_id
            """))
        {
            Add(movements, "@companyCode", companyCode);
            Add(movements, "@deliveryNo", deliveryNo);
            await using var reader = await movements.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                deliveredByMaterial[Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture)] =
                    (Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture),
                     Math.Max(0m, Convert.ToDecimal(reader.GetValue(2), CultureInfo.InvariantCulture)));
            }
        }
        if (deliveredByMaterial.Count == 0)
            return (0m, GlAccounts.Inventory);

        // Line quantities of one document converted to base units, per
        // material (the same equivalence rule delivery posting applies).
        async Task<Dictionary<long, decimal>> ReadBaseQuantitiesAsync(long docId)
        {
            var rows = new List<(long MaterialId, string? Unit, decimal Qty)>();
            await using (var lines = CreateCommand(connection, transaction,
                "SELECT material_id, unit, qty FROM sales_document_lines WHERE doc_id = @doc AND material_id IS NOT NULL"))
            {
                Add(lines, "@doc", docId);
                await using var reader = await lines.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    rows.Add((Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                              reader.IsDBNull(1) ? null : reader.GetString(1),
                              Convert.ToDecimal(reader.GetValue(2), CultureInfo.InvariantCulture)));
                }
            }
            var result = new Dictionary<long, decimal>();
            foreach (var (materialId, unit, qty) in rows)
            {
                var equivalent = await ResolveUnitEquivalentAsync(
                    connection, transaction, materialId, unit, cancellationToken).ConfigureAwait(false);
                result[materialId] = result.GetValueOrDefault(materialId) + qty * equivalent;
            }
            return result;
        }

        // Earlier posted invoices against this delivery, kept per invoice
        // so each one's released cost can be re-derived exactly.
        var previousInvoiceIds = new List<long>();
        await using (var previous = CreateCommand(connection, transaction, """
            SELECT invoice.doc_id
            FROM document_links link
            INNER JOIN sales_documents invoice
                ON invoice.company_id = link.company_id
               AND invoice.doc_id = link.target_doc_id
               AND invoice.doc_kind = link.target_doc_kind
            WHERE link.company_id = @company
              AND link.source_doc_kind = @deliveryKind
              AND link.source_doc_id = @deliveryId
              AND link.target_doc_kind = @invoiceKind
              AND invoice.status = @posted
              AND CAST(invoice.is_deleted AS TEXT) IN ('0', 'false')
              AND invoice.doc_id <> @currentId
            ORDER BY invoice.doc_id
            """))
        {
            Add(previous, "@company", companyId);
            Add(previous, "@deliveryKind", CommercialDocKinds.DeliveryNote);
            Add(previous, "@deliveryId", deliveryDocId.Value);
            Add(previous, "@invoiceKind", CommercialDocKinds.SalesInvoice);
            Add(previous, "@posted", CommercialDocumentStatus.Posted);
            Add(previous, "@currentId", invoiceDocId);
            await using var reader = await previous.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                previousInvoiceIds.Add(Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture));
        }

        var previouslyInvoiced = new Dictionary<long, decimal>();
        var previouslyAllocated = new Dictionary<long, decimal>();
        foreach (var previousId in previousInvoiceIds)
        {
            var quantities = await ReadBaseQuantitiesAsync(previousId).ConfigureAwait(false);
            foreach (var (materialId, baseQty) in quantities)
            {
                if (!deliveredByMaterial.TryGetValue(materialId, out var delivered) || delivered.BaseQty <= 0m)
                    continue;
                previouslyInvoiced[materialId] = previouslyInvoiced.GetValueOrDefault(materialId) + baseQty;
                // No earlier invoice can have taken the completion plug
                // for a material this invoice still bills (that would
                // mean the material was already fully billed), so the
                // pro-rata derivation reproduces its released cost.
                previouslyAllocated[materialId] = previouslyAllocated.GetValueOrDefault(materialId)
                    + decimal.Round(delivered.Cost / delivered.BaseQty * baseQty, 2, MidpointRounding.AwayFromZero);
            }
        }

        var thisInvoice = await ReadBaseQuantitiesAsync(invoiceDocId).ConfigureAwait(false);
        const decimal qtyTolerance = 0.0001m;
        var totalCogs = 0m;
        foreach (var (materialId, thisBaseQty) in thisInvoice)
        {
            if (thisBaseQty <= 0m)
                continue;
            if (!deliveredByMaterial.TryGetValue(materialId, out var delivered) || delivered.BaseQty <= 0m)
            {
                // A stocked material the delivery never moved has no
                // delivery cost to release; a service / non-stock
                // material legitimately releases none.
                if (await IsStockableAsync(connection, transaction, materialId, cancellationToken).ConfigureAwait(false))
                    throw new AppError(ErrorCodes.DeliveryCostAllocationFailed,
                        $"Sales invoice material '{materialId}' was not delivered by delivery note '{deliveryNo}'; its delivery cost cannot be allocated.",
                        materialId.ToString(CultureInfo.InvariantCulture), deliveryNo);
                continue;
            }

            var previousQty = previouslyInvoiced.GetValueOrDefault(materialId);
            if (previousQty + thisBaseQty > delivered.BaseQty + qtyTolerance)
                throw new AppError(ErrorCodes.DeliveryCostAllocationFailed,
                    $"Sales invoice quantity for material '{materialId}' exceeds the quantity delivered by delivery note '{deliveryNo}'.",
                    materialId.ToString(CultureInfo.InvariantCulture), deliveryNo);

            var unitCost = delivered.Cost / delivered.BaseQty;
            var completesMaterial = previousQty + thisBaseQty >= delivered.BaseQty - qtyTolerance;
            var allocated = completesMaterial
                // Completion plug: take the exact remaining cost so the
                // invoices against this delivery sum to its movement
                // cost to the cent. Clamped at zero for the pathological
                // case where repeated round-ups already released a cent
                // more than the material's cost.
                ? Math.Max(0m, delivered.Cost - previouslyAllocated.GetValueOrDefault(materialId))
                : decimal.Round(unitCost * thisBaseQty, 2, MidpointRounding.AwayFromZero);
            totalCogs += allocated;
        }

        // Credit the account the delivery actually debited: 1215 when
        // its GL entry carries a 1215 debit (and the account is still
        // postable), else Inventory 1203.
        var creditAccount = GlAccounts.Inventory;
        if (GlPosting.TryResolveAccount(connection, transaction, _provider,
                Guid.Parse(companyId), GlAccounts.StockDeliveredNotBilled) is not null)
        {
            await using var entry = CreateCommand(connection, transaction, """
                SELECT COUNT(*)
                FROM journal_entries e
                JOIN journal_entry_lines l ON l.journal_entry_id = e.journal_entry_id
                JOIN chart_of_accounts a ON a.account_id = l.account_id AND a.company_id = e.company_id
                WHERE e.company_id = @companyGuid
                  AND e.source_doc_type = @deliveryKind
                  AND e.source_doc_id = @deliveryId
                  AND e.reversal_of_entry_id IS NULL
                  AND e.status = 1
                  AND a.account_code = @deliveredAccount
                  AND l.debit > 0
                """);
            Add(entry, "@companyGuid", GlPosting.FormatGuid(_provider, Guid.Parse(companyId)));
            Add(entry, "@deliveryKind", CommercialDocKinds.DeliveryNote);
            Add(entry, "@deliveryId", deliveryDocId.Value.ToString(CultureInfo.InvariantCulture));
            Add(entry, "@deliveredAccount", GlAccounts.StockDeliveredNotBilled);
            if (Convert.ToInt64(await entry.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    CultureInfo.InvariantCulture) > 0)
                creditAccount = GlAccounts.StockDeliveredNotBilled;
        }
        return (totalCogs, creditAccount);
    }

    // ------------------------------------------------- lifecycle (W23, mig 098)

    /// <summary>Company approval policy inside an open transaction: the
    /// settings row is keyed by company CODE (067 pattern), resolved from
    /// the document's Guid key. A missing row means approval is OFF.</summary>
    private async Task<bool> IsApprovalRequiredInTransactionAsync(
        DbConnection connection, DbTransaction transaction, string companyKey, CancellationToken cancellationToken)
    {
        var companyCode = await PartyLedger.ResolveCompanyCodeAsync(
            connection, transaction, _provider, companyKey, cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, transaction,
            "SELECT require_approval FROM sau_sales_settings WHERE company_code = @code");
        Add(command, "@code", companyCode);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is null or DBNull) return false;
        return value is bool flag ? flag : Convert.ToInt32(value, CultureInfo.InvariantCulture) != 0;
    }

    public async Task<bool> IsApprovalRequiredAsync(Guid companyId, CancellationToken cancellationToken = default)
    {
        if (companyId == Guid.Empty) throw new ArgumentException("Company is required.", nameof(companyId));
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var required = await IsApprovalRequiredInTransactionAsync(
            connection, transaction, companyId.ToString("D"), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return required;
    }

    public async Task SetApprovalRequiredAsync(Guid companyId, bool required, string userId, CancellationToken cancellationToken = default)
    {
        if (companyId == Guid.Empty) throw new ArgumentException("Company is required.", nameof(companyId));
        if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("User is required.", nameof(userId));
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var companyCode = await PartyLedger.ResolveCompanyCodeAsync(
                connection, transaction, _provider, companyId.ToString("D"), cancellationToken).ConfigureAwait(false);
            var now = _provider == "PostgreSQL" ? "now()" : "datetime('now')";
            await using var command = CreateCommand(connection, transaction, $"""
                INSERT INTO sau_sales_settings (company_code, require_approval, updated_by, updated_at)
                VALUES (@code, @required, @actor, {now})
                ON CONFLICT (company_code) DO UPDATE
                SET require_approval = EXCLUDED.require_approval, updated_by = EXCLUDED.updated_by, updated_at = EXCLUDED.updated_at
                """);
            Add(command, "@code", companyCode);
            Add(command, "@required", _provider == "PostgreSQL" ? (object)required : required ? 1 : 0);
            Add(command, "@actor", userId.Trim());
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (InvalidOperationException) { /* already completed; keep root error */ }
            throw;
        }
    }

    private async Task InsertApprovalHistoryAsync(
        DbConnection connection, DbTransaction transaction, string companyId, long docId, string docKind,
        string action, string fromState, string toState, string actor, string? reason,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, """
            INSERT INTO sau_approval_history
                (history_id, company_id, doc_id, doc_kind, action, from_state, to_state, actor, reason)
            VALUES (@id, @company, @doc, @kind, @action, @from, @to, @actor, @reason)
            """);
        Add(command, "@id", Guid.NewGuid().ToString("D"));
        Add(command, "@company", companyId);
        Add(command, "@doc", docId);
        Add(command, "@kind", docKind);
        Add(command, "@action", action);
        Add(command, "@from", fromState);
        Add(command, "@to", toState);
        Add(command, "@actor", actor);
        Add(command, "@reason", (object?)reason ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Shared guarded approval transition: loads the document,
    /// validates kind/status/current state, updates approval_state with a
    /// state-guarded UPDATE and records the history row — atomically.</summary>
    private async Task TransitionApprovalAsync(
        long docId, string userId, IReadOnlySet<string> allowedFrom, string toState,
        string action, string? reason, CancellationToken cancellationToken)
    {
        if (docId <= 0) throw new ArgumentException("Document is required.", nameof(docId));
        if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("User is required.", nameof(userId));
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string companyId, docKind, docNo, status, approvalState;
            await using (var header = CreateCommand(connection, transaction, """
                SELECT company_id, doc_kind, doc_no, status, approval_state
                FROM sales_documents WHERE doc_id = @doc AND is_deleted = 0
                """))
            {
                Add(header, "@doc", docId);
                await using var reader = await header.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    throw new AppError(ErrorCodes.ApprovalStateInvalid,
                        $"Document '{docId}' was not found and cannot change approval state.",
                        docId.ToString(CultureInfo.InvariantCulture), string.Empty);
                companyId = Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!;
                docKind = reader.GetString(1);
                docNo = reader.GetString(2);
                status = reader.GetString(3);
                approvalState = reader.IsDBNull(4) ? SalesApprovalState.NotRequired : reader.GetString(4);
            }

            if (docKind is not (CommercialDocKinds.SalesQuotation or CommercialDocKinds.SalesOrder or CommercialDocKinds.SalesInvoice)
                || status != CommercialDocumentStatus.Draft
                || !allowedFrom.Contains(approvalState))
                throw new AppError(ErrorCodes.ApprovalStateInvalid,
                    $"Document '{docNo}' in status '{status}' with approval state '{approvalState}' cannot perform '{action}'.",
                    docNo, approvalState);

            await using (var update = CreateCommand(connection, transaction,
                "UPDATE sales_documents SET approval_state = @to WHERE doc_id = @doc AND approval_state = @from AND status = @draft AND is_deleted = 0"))
            {
                Add(update, "@to", toState);
                Add(update, "@doc", docId);
                Add(update, "@from", approvalState);
                Add(update, "@draft", CommercialDocumentStatus.Draft);
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new AppError(ErrorCodes.ApprovalStateInvalid,
                        $"Document '{docNo}' changed approval state concurrently; '{action}' was not applied.",
                        docNo, approvalState);
            }

            await InsertApprovalHistoryAsync(connection, transaction, companyId, docId, docKind,
                action, approvalState, toState, userId.Trim(), reason, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (InvalidOperationException) { /* already completed; keep root error */ }
            throw;
        }
    }

    public Task SubmitForApprovalAsync(long docId, string userId, CancellationToken cancellationToken = default) =>
        TransitionApprovalAsync(docId, userId,
            new HashSet<string> { SalesApprovalState.NotRequired, SalesApprovalState.Rejected },
            SalesApprovalState.Pending, SalesApprovalAction.Submitted, null, cancellationToken);

    public Task ApproveDocumentAsync(long docId, string userId, CancellationToken cancellationToken = default) =>
        TransitionApprovalAsync(docId, userId,
            new HashSet<string> { SalesApprovalState.Pending },
            SalesApprovalState.Approved, SalesApprovalAction.Approved, null, cancellationToken);

    public Task RejectDocumentAsync(long docId, string userId, string reason, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A rejection reason is required.", nameof(reason));
        reason = reason.Trim();
        if (reason.Length > 1000) throw new ArgumentException("The rejection reason cannot exceed 1000 characters.", nameof(reason));
        return TransitionApprovalAsync(docId, userId,
            new HashSet<string> { SalesApprovalState.Pending },
            SalesApprovalState.Rejected, SalesApprovalAction.Rejected, reason, cancellationToken);
    }

    public async Task<IReadOnlyList<ApprovalHistoryEntry>> GetApprovalHistoryAsync(long docId, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<ApprovalHistoryEntry>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT history_id, company_id, doc_id, doc_kind, action, from_state, to_state, actor, reason, created_at
            FROM sau_approval_history WHERE doc_id = @doc ORDER BY created_at, history_id
            """;
        Add(command, "@doc", docId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new ApprovalHistoryEntry(
                Guid.Parse(Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!),
                Guid.Parse(Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture)!),
                Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture),
                reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
                reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                Convert.ToString(reader.GetValue(9), CultureInfo.InvariantCulture)!));
        }
        return rows;
    }

    public async Task<IReadOnlyList<DocumentRevisionSnapshot>> GetRevisionsAsync(long docId, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<DocumentRevisionSnapshot>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT revision_id, company_id, doc_id, doc_kind, revision_no, snapshot_json, change_reason, created_by, created_at
            FROM sau_document_revisions WHERE doc_id = @doc ORDER BY revision_no
            """;
        Add(command, "@doc", docId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new DocumentRevisionSnapshot(
                Guid.Parse(Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!),
                Guid.Parse(Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture)!),
                Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture),
                reader.GetString(3),
                Convert.ToInt32(reader.GetValue(4), CultureInfo.InvariantCulture),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetString(7),
                Convert.ToString(reader.GetValue(8), CultureInfo.InvariantCulture)!));
        }
        return rows;
    }

    /// <summary>Generic row-to-dictionary read used for revision snapshots:
    /// the snapshot must preserve EVERY stored column of the superseded
    /// version, including columns added by later migrations.</summary>
    private static async Task<Dictionary<string, object?>> ReadRowAsDictionaryAsync(DbDataReader reader, CancellationToken cancellationToken)
    {
        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var value = await reader.IsDBNullAsync(i, cancellationToken).ConfigureAwait(false) ? null : reader.GetValue(i);
            row[reader.GetName(i)] = value switch
            {
                null => null,
                DateTime moment => moment.ToString("o", CultureInfo.InvariantCulture),
                byte[] bytes => Convert.ToBase64String(bytes),
                _ => value
            };
        }
        return row;
    }

    public async Task<int> AmendSalesDocumentAsync(
        long docId, CommercialDocumentDraft amended, int expectedRevision, string userId,
        string? reason = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(amended);
        if (docId <= 0) throw new ArgumentException("Document is required.", nameof(docId));
        if (expectedRevision <= 0) throw new ArgumentException("The expected revision must be positive.", nameof(expectedRevision));
        if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("User is required.", nameof(userId));
        if (amended.Lines.Count == 0) throw new InvalidOperationException("A commercial document requires at least one line.");
        if (reason is not null && reason.Length > 1000) throw new ArgumentException("The amendment reason cannot exceed 1000 characters.", nameof(reason));

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // ---- Load the current version (header + lines) for the snapshot.
            Dictionary<string, object?> headerRow;
            string companyId, docKind, docNo, status, approvalState;
            int currentRevision;
            await using (var header = CreateCommand(connection, transaction,
                "SELECT * FROM sales_documents WHERE doc_id = @doc AND is_deleted = 0"))
            {
                Add(header, "@doc", docId);
                await using var reader = await header.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    throw new AppError(ErrorCodes.ApprovalStateInvalid,
                        $"Document '{docId}' was not found and cannot be amended.",
                        docId.ToString(CultureInfo.InvariantCulture), string.Empty);
                headerRow = await ReadRowAsDictionaryAsync(reader, cancellationToken).ConfigureAwait(false);
            }
            companyId = Convert.ToString(headerRow["company_id"], CultureInfo.InvariantCulture)!;
            docKind = Convert.ToString(headerRow["doc_kind"], CultureInfo.InvariantCulture)!;
            docNo = Convert.ToString(headerRow["doc_no"], CultureInfo.InvariantCulture)!;
            status = Convert.ToString(headerRow["status"], CultureInfo.InvariantCulture)!;
            approvalState = headerRow["approval_state"] as string ?? SalesApprovalState.NotRequired;
            currentRevision = Convert.ToInt32(headerRow["revision_no"], CultureInfo.InvariantCulture);

            var lineRows = new List<Dictionary<string, object?>>();
            await using (var lines = CreateCommand(connection, transaction,
                "SELECT * FROM sales_document_lines WHERE doc_id = @doc ORDER BY line_no"))
            {
                Add(lines, "@doc", docId);
                await using var reader = await lines.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    lineRows.Add(await ReadRowAsDictionaryAsync(reader, cancellationToken).ConfigureAwait(false));
            }

            // ---- Guards.
            if (docKind is not (CommercialDocKinds.SalesQuotation or CommercialDocKinds.SalesOrder))
                throw new AppError(ErrorCodes.ApprovalStateInvalid,
                    $"Only quotations and sales orders can be amended by revision; document '{docNo}' is a '{docKind}'.",
                    docNo, approvalState);
            if (!string.Equals(amended.DocKind, docKind, StringComparison.Ordinal)
                || amended.CompanyId != Guid.Parse(companyId))
                throw new InvalidOperationException("An amendment must keep the document kind and company of the original.");
            if (currentRevision != expectedRevision)
                throw new AppError(ErrorCodes.DocumentRevisionConflict,
                    $"Document '{docNo}' is at revision {currentRevision}; the amendment was based on revision {expectedRevision}. Reload before amending.",
                    docNo, currentRevision.ToString(CultureInfo.InvariantCulture));
            if (status == CommercialDocumentStatus.Posted)
            {
                // Item 3: a posted order may be amended only when nothing
                // posted hangs below it and no journal entry exists (orders
                // normally post no journal; if one does, the documented
                // reversal machinery — not an amendment — owns it).
                long postedDownstream;
                await using (var downstream = CreateCommand(connection, transaction, """
                    SELECT COUNT(*) FROM document_links l
                    JOIN sales_documents t ON t.doc_id = l.target_doc_id
                    WHERE l.company_id = @company AND l.source_doc_id = @doc
                      AND t.status = @posted AND t.is_deleted = 0
                    """))
                {
                    Add(downstream, "@company", companyId);
                    Add(downstream, "@doc", docId);
                    Add(downstream, "@posted", CommercialDocumentStatus.Posted);
                    postedDownstream = Convert.ToInt64(await downstream.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
                }
                if (postedDownstream > 0)
                    throw new AppError(ErrorCodes.ApprovalStateInvalid,
                        $"Posted document '{docNo}' has posted downstream documents and cannot be amended.",
                        docNo, approvalState);

                long journalCount;
                await using (var journal = CreateCommand(connection, transaction,
                    "SELECT COUNT(*) FROM journal_entries WHERE company_id = @company AND source_doc_type = @type AND source_doc_id = @source AND reversal_of_entry_id IS NULL AND status IN (0, 1)"))
                {
                    Add(journal, "@company", GlPosting.FormatGuid(_provider, Guid.Parse(companyId)));
                    Add(journal, "@type", docKind);
                    Add(journal, "@source", docId.ToString(CultureInfo.InvariantCulture));
                    journalCount = Convert.ToInt64(await journal.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
                }
                if (journalCount > 0)
                    throw new AppError(ErrorCodes.ApprovalStateInvalid,
                        $"Posted document '{docNo}' has a journal entry and cannot be amended; reverse it instead.",
                        docNo, approvalState);
            }
            else if (status != CommercialDocumentStatus.Draft)
            {
                throw new AppError(ErrorCodes.ApprovalStateInvalid,
                    $"Document '{docNo}' in status '{status}' cannot be amended.",
                    docNo, approvalState);
            }

            // ---- Preserve the superseded version in full.
            var snapshotJson = System.Text.Json.JsonSerializer.Serialize(new { header = headerRow, lines = lineRows });
            await using (var revision = CreateCommand(connection, transaction, """
                INSERT INTO sau_document_revisions
                    (revision_id, company_id, doc_id, doc_kind, revision_no, snapshot_json, change_reason, created_by)
                VALUES (@id, @company, @doc, @kind, @revision, @snapshot, @reason, @actor)
                """))
            {
                Add(revision, "@id", Guid.NewGuid().ToString("D"));
                Add(revision, "@company", companyId);
                Add(revision, "@doc", docId);
                Add(revision, "@kind", docKind);
                Add(revision, "@revision", currentRevision);
                Add(revision, "@snapshot", snapshotJson);
                Add(revision, "@reason", (object?)reason?.Trim() ?? DBNull.Value);
                Add(revision, "@actor", userId.Trim());
                await revision.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // ---- Rewrite header + lines from the amendment (totals follow
            // the same stored-total rule as draft creation).
            if (amended.DiscountAmount < 0m) throw new InvalidOperationException("Header discount cannot be negative.");
            if (amended.ExtraAmount < 0m) throw new InvalidOperationException("Header extra charges cannot be negative.");
            decimal subtotal = 0m, taxAmount = 0m;
            var computed = new List<(decimal Subtotal, decimal Tax, decimal Total)>();
            foreach (var line in amended.Lines)
            {
                if (line.Qty <= 0m) throw new InvalidOperationException("Document line quantities must be greater than zero.");
                var lineSubtotal = decimal.Round(line.Qty * line.UnitPrice * (1m - line.DiscountPct / 100m), 2);
                var lineTax = decimal.Round(lineSubtotal * line.TaxRate / 100m, 2);
                computed.Add((lineSubtotal, lineTax, lineSubtotal + lineTax));
                subtotal += lineSubtotal;
                taxAmount += lineTax;
            }
            var grandTotal = decimal.Round(subtotal - amended.DiscountAmount + taxAmount + amended.ExtraAmount, 2);
            var newRevision = currentRevision + 1;

            await using (var update = CreateCommand(connection, transaction, """
                UPDATE sales_documents SET
                    doc_date = @date, party_id = @party, party_name = @partyName, warehouse_id = @warehouse,
                    currency_code = @currency, exchange_rate = @rate, subtotal = @subtotal,
                    discount_amount = @discount, tax_amount = @tax, grand_total = @total,
                    status = @draft, notes = @notes, party_code = @partyCode,
                    delivery_date = @deliveryDate, valid_till = @validTill, cost_center = @costCenter,
                    branch_name = @branchName, sales_rep_name = @salesRepName, warehouse_name = @warehouseName,
                    extra_amount = @extra, delivery_cost = @deliveryCost,
                    posted_by = NULL, posted_at = NULL,
                    revision_no = @newRevision, approval_state = @notRequired
                WHERE doc_id = @doc AND revision_no = @expected AND is_deleted = 0
                """))
            {
                Add(update, "@date", amended.DocDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                Add(update, "@party", (object?)amended.PartyId ?? DBNull.Value);
                Add(update, "@partyName", (object?)amended.PartyName ?? DBNull.Value);
                Add(update, "@warehouse", (object?)amended.WarehouseId ?? DBNull.Value);
                Add(update, "@currency", string.IsNullOrWhiteSpace(amended.CurrencyCode) ? "SAR" : amended.CurrencyCode.Trim());
                Add(update, "@rate", amended.ExchangeRate < 0m ? 0m : amended.ExchangeRate);
                Add(update, "@subtotal", decimal.Round(subtotal, 2));
                Add(update, "@discount", amended.DiscountAmount);
                Add(update, "@tax", decimal.Round(taxAmount, 2));
                Add(update, "@total", grandTotal);
                Add(update, "@draft", CommercialDocumentStatus.Draft);
                Add(update, "@notes", (object?)amended.Notes ?? DBNull.Value);
                Add(update, "@partyCode", (object?)amended.PartyCode ?? DBNull.Value);
                Add(update, "@deliveryDate", (object?)amended.DeliveryDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? DBNull.Value);
                Add(update, "@validTill", (object?)amended.ValidTill?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? DBNull.Value);
                Add(update, "@costCenter", (object?)amended.CostCenter ?? DBNull.Value);
                Add(update, "@branchName", (object?)amended.BranchName ?? DBNull.Value);
                Add(update, "@salesRepName", (object?)amended.SalesRepName ?? DBNull.Value);
                Add(update, "@warehouseName", (object?)amended.WarehouseName ?? DBNull.Value);
                Add(update, "@extra", amended.ExtraAmount);
                Add(update, "@deliveryCost", amended.DeliveryCost);
                Add(update, "@newRevision", newRevision);
                Add(update, "@notRequired", SalesApprovalState.NotRequired);
                Add(update, "@doc", docId);
                Add(update, "@expected", expectedRevision);
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new AppError(ErrorCodes.DocumentRevisionConflict,
                        $"Document '{docNo}' changed while it was being amended; the amendment was not saved.",
                        docNo, currentRevision.ToString(CultureInfo.InvariantCulture));
            }

            await using (var deleteLines = CreateCommand(connection, transaction,
                "DELETE FROM sales_document_lines WHERE doc_id = @doc"))
            {
                Add(deleteLines, "@doc", docId);
                await deleteLines.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            var lineNumber = 0;
            var usedLineNumbers = new HashSet<int>();
            for (var index = 0; index < amended.Lines.Count; index++)
            {
                var line = amended.Lines[index];
                var totals = computed[index];
                lineNumber++;
                var persistedLineNumber = line.SourceLineNo ?? lineNumber;
                if (persistedLineNumber <= 0 || !usedLineNumbers.Add(persistedLineNumber))
                    throw new InvalidOperationException("Document line numbers must be positive and unique within a document.");
                await using var insertLine = CreateCommand(connection, transaction,
                    "INSERT INTO sales_document_lines (doc_id, line_no, material_id, material_code, description, unit, qty, unit_price, discount_pct, tax_rate, line_subtotal, line_tax, line_total, warehouse_id) " +
                    "VALUES (@doc, @line, @material, @materialCode, @description, @unit, @qty, @price, @discount, @taxRate, @subtotal, @tax, @total, @warehouse)");
                Add(insertLine, "@doc", docId);
                Add(insertLine, "@line", persistedLineNumber);
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

            await InsertApprovalHistoryAsync(connection, transaction, companyId, docId, docKind,
                SalesApprovalAction.Amended, approvalState, SalesApprovalState.NotRequired,
                userId.Trim(), reason, cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return newRevision;
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (InvalidOperationException) { /* already completed; keep root error */ }
            throw;
        }
    }

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
