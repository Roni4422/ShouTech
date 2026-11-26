using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.Domain;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;
using ShouTech.Infra;

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
        CommercialDocKinds.SupplierQuotation => "SQ",
        CommercialDocKinds.PurchaseOrder => "PO",
        CommercialDocKinds.PurchaseReceipt => "RCV",
        CommercialDocKinds.PurchaseReceiptReturn => "RCR",
        CommercialDocKinds.PurchaseInvoice => "PUR",
        CommercialDocKinds.PurchaseRequisition => "PRQ",
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
        decimal extraAmount, perDelivered, perBilled;
        string? cancelReason, cancelledBy, cancelledAt;
        DateOnly? deliveryDate, validTill, requiredByDate;
        string? requestType, department;
        decimal deliveryCost;
        int revisionNo;
        string approvalState;
        string? paymentTerms, incoterm, referenceNo, deliveryNoteNo, supplierCreditNoteRef;
        DateOnly? billDate;
        decimal perReceived, storedPerBilled;
        long? amendedFromDocId;

        // per_delivered / per_billed exist only on sales_documents
        // (migration 093), delivery_cost likewise (migration 096), and
        // revision_no / approval_state likewise (migration 098); the
        // purchase family reads constants so one projection serves both.
        var progressColumns = side == CommercialDocSide.Sales ? "per_delivered, per_billed" : "0, 0";
        var deliveryCostColumn = side == CommercialDocSide.Sales ? "delivery_cost" : "0";
        var revisionColumn = side == CommercialDocSide.Sales ? "revision_no" : "1";
        var approvalColumn = side == CommercialDocSide.Sales ? "approval_state" : "'NotRequired'";
        await using (var header = CreateCommand(connection, transaction, $"""
            SELECT doc_id, company_id, doc_kind, doc_no, doc_date, party_id, party_name, warehouse_id,
                   currency_code, exchange_rate, subtotal, discount_amount, tax_amount, grand_total,
                   paid_amount, status, notes, party_code, branch_name, sales_rep_name, cost_center,
                   warehouse_name, delivery_date, valid_till, extra_amount, {progressColumns},
                   cancel_reason, cancelled_by, cancelled_at, {deliveryCostColumn}, {revisionColumn}, {approvalColumn},
                   required_by_date, request_type, department,
                   payment_terms, incoterm, reference_no, bill_date, delivery_note_no,
                   supplier_credit_note_ref, per_received, per_billed, amended_from_doc_id
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
            extraAmount = Convert.ToDecimal(reader.GetValue(24), CultureInfo.InvariantCulture);
            perDelivered = Convert.ToDecimal(reader.GetValue(25), CultureInfo.InvariantCulture);
            perBilled = Convert.ToDecimal(reader.GetValue(26), CultureInfo.InvariantCulture);
            cancelReason = reader.IsDBNull(27) ? null : reader.GetString(27);
            cancelledBy = reader.IsDBNull(28) ? null : reader.GetString(28);
            cancelledAt = reader.IsDBNull(29) ? null : Convert.ToString(reader.GetValue(29), CultureInfo.InvariantCulture);
            deliveryCost = Convert.ToDecimal(reader.GetValue(30), CultureInfo.InvariantCulture);
            revisionNo = Convert.ToInt32(reader.GetValue(31), CultureInfo.InvariantCulture);
            approvalState = reader.IsDBNull(32) ? SalesApprovalState.NotRequired : reader.GetString(32);
            requiredByDate = reader.IsDBNull(33) ? null : DateOnly.Parse(Convert.ToString(reader.GetValue(33), CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture);
            requestType = reader.IsDBNull(34) ? null : reader.GetString(34);
            department = reader.IsDBNull(35) ? null : reader.GetString(35);
            paymentTerms = reader.IsDBNull(36) ? null : reader.GetString(36);
            incoterm = reader.IsDBNull(37) ? null : reader.GetString(37);
            referenceNo = reader.IsDBNull(38) ? null : reader.GetString(38);
            billDate = reader.IsDBNull(39) ? null : DateOnly.Parse(Convert.ToString(reader.GetValue(39), CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture);
            deliveryNoteNo = reader.IsDBNull(40) ? null : reader.GetString(40);
            supplierCreditNoteRef = reader.IsDBNull(41) ? null : reader.GetString(41);
            perReceived = reader.IsDBNull(42) ? 0m : Convert.ToDecimal(reader.GetValue(42), CultureInfo.InvariantCulture);
            storedPerBilled = reader.IsDBNull(43) ? 0m : Convert.ToDecimal(reader.GetValue(43), CultureInfo.InvariantCulture);
            amendedFromDocId = reader.IsDBNull(44) ? null : Convert.ToInt64(reader.GetValue(44), CultureInfo.InvariantCulture);
        }

        var lineList = new List<CommercialLineSnapshot>();
        await using (var lineCommand = CreateCommand(connection, transaction, $"""
            SELECT line_id, line_no, material_id, material_code, description, unit, qty, unit_price,
                   discount_pct, tax_rate, line_subtotal, line_tax, line_total, warehouse_id,
                   uom_factor, line_required_by, rejected_qty, reject_reason, tax_code
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
                    reader.IsDBNull(13) ? null : Convert.ToInt64(reader.GetValue(13), CultureInfo.InvariantCulture),
                    UomFactor: reader.IsDBNull(14) ? null : Convert.ToDecimal(reader.GetValue(14), CultureInfo.InvariantCulture),
                    LineRequiredBy: reader.IsDBNull(15) ? null : DateOnly.Parse(Convert.ToString(reader.GetValue(15), CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture),
                    RejectedQty: reader.IsDBNull(16) ? 0m : Convert.ToDecimal(reader.GetValue(16), CultureInfo.InvariantCulture),
                    RejectReason: reader.IsDBNull(17) ? null : reader.GetString(17),
                    TaxCode: reader.IsDBNull(18) ? null : reader.GetString(18)));
            }
        }

        return new CommercialDocumentSnapshot(
            foundId, foundCompany, foundKind, foundNo, docDate, partyId, partyName, warehouseId,
            currencyCode, exchangeRate, subtotal, discountAmount, taxAmount, grandTotal,
            paidAmount, status, notes, lineList.AsReadOnly(),
            partyCode, branchName, salesRepName, costCenter, warehouseName, deliveryDate, validTill,
            extraAmount, perDelivered, storedPerBilled, cancelReason, cancelledBy, cancelledAt, deliveryCost,
            revisionNo, approvalState, RequiredByDate: requiredByDate, RequestType: requestType, Department: department,
            PaymentTerms: paymentTerms, Incoterm: incoterm, ReferenceNo: referenceNo, BillDate: billDate,
            DeliveryNoteNo: deliveryNoteNo, SupplierCreditNoteRef: supplierCreditNoteRef,
            PerReceived: perReceived, AmendedFromDocId: amendedFromDocId);
    }

    // ----------------------------------------------------------------- write

    public async Task<long> CreateDraftAsync(CommercialDocSide side, CommercialDocumentDraft draft, string userId, CancellationToken cancellationToken = default, PriceOverrideContext? priceOverride = null)
    {
        ValidateDraft(draft, userId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            draft = ApplySalesPricing(connection, transaction, side, draft, priceOverride);
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

    public async Task<long> CreateLinkedDraftAsync(
        CommercialDocSide side, CommercialDocumentDraft draft, string sourceDocKind,
        long sourceDocId, string userId, CancellationToken cancellationToken = default)
    {
        ValidateDraft(draft, userId);
        if (side != CommercialDocSide.Purchase || sourceDocId <= 0)
            throw new ArgumentException("Linked sourcing documents must be purchase documents with a valid source id.");
        var validPair = (sourceDocKind, draft.DocKind) is
            (CommercialDocKinds.RequestForQuotation, CommercialDocKinds.SupplierQuotation) or
            (CommercialDocKinds.SupplierQuotation, CommercialDocKinds.PurchaseOrder) or
            (CommercialDocKinds.PurchaseRequisition, CommercialDocKinds.PurchaseOrder) or
            (CommercialDocKinds.PurchaseRequisition, CommercialDocKinds.RequestForQuotation);
        if (!validPair)
            throw new InvalidOperationException("Unsupported purchasing document conversion.");

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Serialize conversions from the same supplier quotation. PostgreSQL
            // row-locks the source document; SQLite's write transaction provides
            // the corresponding single-writer protection. This closes the
            // check-then-insert race in the application service.
            var sourceLock = _provider == "PostgreSQL" ? " FOR UPDATE" : string.Empty;
            await using (var source = CreateCommand(connection, transaction, """
                SELECT company_id, doc_kind, status
                FROM purchase_documents
                WHERE doc_id = @sourceId AND is_deleted = 0
                """ + sourceLock))
            {
                Add(source, "@sourceId", sourceDocId);
                await using var reader = await source.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("The source purchasing document was not found.");
                var company = reader.GetString(0);
                var kind = reader.GetString(1);
                var status = reader.GetString(2);
                if (!Guid.TryParse(company, out var sourceCompany) || sourceCompany != draft.CompanyId)
                    throw new InvalidOperationException("The source and target documents must belong to the same company.");
                var expectedSourceStatus = sourceDocKind == CommercialDocKinds.PurchaseRequisition
                    ? CommercialDocumentStatus.Draft
                    : CommercialDocumentStatus.Posted;
                if (kind != sourceDocKind || status != expectedSourceStatus)
                    throw new InvalidOperationException("The source document must have the expected type and workflow state.");
            }

            if (sourceDocKind == CommercialDocKinds.SupplierQuotation &&
                draft.DocKind == CommercialDocKinds.PurchaseOrder)
            {
                await using var duplicateOrder = CreateCommand(connection, transaction, """
                    SELECT COUNT(*)
                    FROM document_links l
                    JOIN purchase_documents p
                      ON p.doc_id = l.target_doc_id
                     AND p.is_deleted = 0
                    WHERE l.company_id = @company
                      AND l.source_doc_kind = @sourceKind
                      AND l.source_doc_id = @sourceId
                      AND l.target_doc_kind = @targetKind
                      AND p.status <> @cancelled
                    """);
                Add(duplicateOrder, "@company", draft.CompanyId.ToString("D"));
                Add(duplicateOrder, "@sourceKind", sourceDocKind);
                Add(duplicateOrder, "@sourceId", sourceDocId);
                Add(duplicateOrder, "@targetKind", CommercialDocKinds.PurchaseOrder);
                Add(duplicateOrder, "@cancelled", CommercialDocumentStatus.Cancelled);
                var existingOrders = Convert.ToInt32(
                    await duplicateOrder.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    CultureInfo.InvariantCulture);
                if (existingOrders > 0)
                    throw new InvalidOperationException(
                        "This supplier quotation already has an active purchase order.");
            }

            var targetId = await InsertDocumentAsync(
                connection, transaction, side, draft, CommercialDocumentStatus.Draft, userId, cancellationToken).ConfigureAwait(false);
            await using (var link = CreateCommand(connection, transaction, """
                INSERT INTO document_links
                    (company_id, source_doc_kind, source_doc_id, target_doc_kind, target_doc_id, created_by)
                VALUES (@company, @sourceKind, @sourceId, @targetKind, @targetId, @actor)
                """))
            {
                Add(link, "@company", draft.CompanyId.ToString("D"));
                Add(link, "@sourceKind", sourceDocKind);
                Add(link, "@sourceId", sourceDocId);
                Add(link, "@targetKind", draft.DocKind);
                Add(link, "@targetId", targetId);
                Add(link, "@actor", userId.Trim());
                await link.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return targetId;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task UpdateDraftAsync(CommercialDocSide side, long docId, CommercialDocumentDraft draft, string userId, CancellationToken cancellationToken = default, PriceOverrideContext? priceOverride = null)
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
            draft = ApplySalesPricing(connection, transaction, side, draft, priceOverride, docId);

            var computed = ComputeTotals(draft.Lines, out var subtotal, out var taxAmount, out _);
            var grandTotal = decimal.Round(subtotal - draft.DiscountAmount + taxAmount + draft.ExtraAmount, 2);

            // delivery_cost exists only on sales_documents (migration 096);
            // contract_id likewise (migration 101).
            var deliveryCostSet = side == CommercialDocSide.Sales ? "delivery_cost = @deliveryCost, contract_id = @contractId," : string.Empty;
            await using (var update = CreateCommand(connection, transaction, $"""
                UPDATE {docs}
                SET doc_date = @date, party_id = @party, party_name = @partyName, party_code = @partyCode,
                    warehouse_id = @warehouse, warehouse_name = @warehouseName, currency_code = @currency,
                    exchange_rate = @rate, subtotal = @subtotal, discount_amount = @discount,
                    extra_amount = @extra, tax_amount = @tax, grand_total = @total, {deliveryCostSet}
                    notes = @notes, delivery_date = @deliveryDate, valid_till = @validTill,
                    cost_center = @costCenter, branch_name = @branchName, sales_rep_name = @salesRepName,
                    required_by_date = @requiredBy, request_type = @requestType, department = @department,
                    payment_terms = @paymentTerms, incoterm = @incoterm, reference_no = @referenceNo,
                    bill_date = @billDate, delivery_note_no = @deliveryNoteNo,
                    supplier_credit_note_ref = @creditNoteRef, amended_from_doc_id = @amendedFrom
                WHERE doc_id = @doc AND status = @draft AND is_deleted = 0
                """))
            {
                Add(update, "@discount", draft.DiscountAmount);
                Add(update, "@extra", draft.ExtraAmount);
                if (side == CommercialDocSide.Sales)
                {
                    Add(update, "@deliveryCost", draft.DeliveryCost);
                    Add(update, "@contractId", (object?)draft.ContractId ?? DBNull.Value);
                }
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
                Add(update, "@requiredBy", (object?)draft.RequiredByDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? DBNull.Value);
                Add(update, "@requestType", (object?)draft.RequestType ?? DBNull.Value);
                Add(update, "@department", (object?)draft.Department ?? DBNull.Value);
                Add(update, "@paymentTerms", (object?)draft.PaymentTerms ?? DBNull.Value);
                Add(update, "@incoterm", (object?)draft.Incoterm ?? DBNull.Value);
                Add(update, "@referenceNo", (object?)draft.ReferenceNo ?? DBNull.Value);
                Add(update, "@billDate", (object?)draft.BillDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? DBNull.Value);
                Add(update, "@deliveryNoteNo", (object?)draft.DeliveryNoteNo ?? DBNull.Value);
                Add(update, "@creditNoteRef", (object?)draft.SupplierCreditNoteRef ?? DBNull.Value);
                Add(update, "@amendedFrom", (object?)draft.AmendedFromDocId ?? DBNull.Value);
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

    /// <summary>
    /// W7a pricing at SAVE for sales orders: BXGY free lines are earned
    /// at save (zero-price companions) and the price/margin/discount
    /// guard validates the lines (SAL-7018/7019/7020), auditing breaches
    /// the caller authorized via SALES.PRICE.OVERRIDE /
    /// SALES.DISCOUNT.OVERRIDE. Other sides/kinds pass through.
    /// </summary>
    private CommercialDocumentDraft ApplySalesPricing(
        DbConnection connection, DbTransaction transaction, CommercialDocSide side,
        CommercialDocumentDraft draft, PriceOverrideContext? priceOverride,
        long? excludeDocId = null)
    {
        if (side != CommercialDocSide.Sales || draft.DocKind != CommercialDocKinds.SalesOrder)
            return draft;
        var expanded = SalesPriceResolver.Default.ExpandBonusLines(
            connection, transaction, _provider, draft.CompanyId.ToString("D"),
            draft.PartyCode, draft.Lines);
        if (expanded.Count != draft.Lines.Count)
            draft = draft with { Lines = expanded };
        SalesPriceGuard.Validate(connection, transaction, _provider,
            draft.CompanyId.ToString("D"), draft.DocKind, draft.DocNo, null,
            draft.Lines.Select((l, i) => new PriceGuardLine(
                i + 1, l.MaterialId, l.MaterialCode, l.Qty, l.UnitPrice, l.DiscountPct)).ToList(),
            priceOverride is null ? null : priceOverride with { DocKind = draft.DocKind, DocNo = draft.DocNo });
        // W7c contract draw-down at SAVE (SAL-7023 / SAL-7015): an
        // order linked to a contract validates against it here and
        // again at post; unlinked orders pass through untouched.
        ContractGuard.ValidateOrder(connection, transaction, _provider,
            draft.CompanyId.ToString("D"), draft.ContractId, excludeDocId,
            draft.DocDate, draft.PartyId,
            draft.Lines.Where(l => l.MaterialId is > 0)
                .Select(l => (l.MaterialId!.Value, l.Qty)).ToList());
        return draft;
    }

    public async Task PostOrderAsync(CommercialDocSide side, long docId, string userId, CancellationToken cancellationToken = default, CreditOverrideContext? creditOverride = null, PriceOverrideContext? priceOverride = null)
    {
        if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("User is required.", nameof(userId));
        var (docs, _) = Tables(side);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var budgetBlocked = new List<BudgetViolation>();
        Guid budgetCompany = Guid.Empty;
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

            // P7: posting a purchase order is a commitment — run the
            // same BudgetGuard.CheckCommitment the requisition submit
            // runs (R1), charged to the canonical Purchases account, with
            // identical Stop/Warn semantics (Stop = ACC-5018/5019 and the
            // posting rolls back; Warn records Warned events in-txn).
            if (kind == CommercialDocKinds.PurchaseOrder && status == CommercialDocumentStatus.Draft)
            {
                await using var head = CreateCommand(connection, transaction,
                    $"SELECT company_id, doc_date, grand_total, exchange_rate FROM {docs} WHERE doc_id = @doc AND is_deleted = 0");
                Add(head, "@doc", docId);
                await using var hr = await head.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await hr.ReadAsync(cancellationToken).ConfigureAwait(false)
                    && Guid.TryParse(Convert.ToString(hr.GetValue(0), CultureInfo.InvariantCulture), out budgetCompany))
                {
                    var docDate = DateOnly.Parse(Convert.ToString(hr.GetValue(1), CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture);
                    var total = Convert.ToDecimal(hr.GetValue(2), CultureInfo.InvariantCulture);
                    var rate = Convert.ToDecimal(hr.GetValue(3), CultureInfo.InvariantCulture);
                    BudgetGuard.CheckCommitment(connection, transaction, _provider, budgetCompany,
                        docDate, GlAccounts.Purchases, total * (rate > 0m ? rate : 1m), userId, budgetBlocked);
                }
            }

            var orderKinds = side == CommercialDocSide.Sales
                ? new[] { CommercialDocKinds.SalesOrder, CommercialDocKinds.SalesQuotation }
                : new[] { CommercialDocKinds.PurchaseOrder, CommercialDocKinds.RequestForQuotation, CommercialDocKinds.SupplierQuotation };
            if (Array.IndexOf(orderKinds, kind) < 0)
                throw new InvalidOperationException($"Document kind '{kind}' is not an order kind; use the invoice posting path.");
            if (status != CommercialDocumentStatus.Draft)
                throw new InvalidOperationException($"Only draft orders can be posted; this document is '{status}'.");

            if (side == CommercialDocSide.Sales)
            {
                // Approval gate (W23, migration 098): only when the company
                // policy requires approval (sau_sales_settings, default OFF)
                // must a quotation/order be Approved before it posts.
                string? approvalCompany = null, approvalState = null;
                await using (var read = CreateCommand(connection, transaction,
                    $"SELECT company_id, approval_state FROM {docs} WHERE doc_id = @doc AND is_deleted = 0"))
                {
                    Add(read, "@doc", docId);
                    await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        approvalCompany = reader.IsDBNull(0) ? null : Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture);
                        approvalState = reader.IsDBNull(1) ? null : reader.GetString(1);
                    }
                }
                if (approvalCompany is not null && approvalState != SalesApprovalState.Approved)
                {
                    var companyCode = await PartyLedger.ResolveCompanyCodeAsync(
                        connection, transaction, _provider, approvalCompany, cancellationToken).ConfigureAwait(false);
                    await using var settings = CreateCommand(connection, transaction,
                        "SELECT require_approval FROM sau_sales_settings WHERE company_code = @code");
                    Add(settings, "@code", companyCode);
                    var flag = await settings.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                    var required = flag is bool b ? b : flag is not null and not DBNull && Convert.ToInt32(flag, CultureInfo.InvariantCulture) != 0;
                    if (required)
                        throw new AppError(ErrorCodes.ApprovalStateInvalid,
                            $"Document requires approval before posting; its approval state is '{approvalState ?? SalesApprovalState.NotRequired}'.",
                            approvalState ?? SalesApprovalState.NotRequired);
                }
            }

            if (side == CommercialDocSide.Sales && kind == CommercialDocKinds.SalesOrder)
            {
                // W1 credit control at ORDER stage (spec §1/§3): a posted
                // sales order reserves credit, so the guard runs HERE —
                // against (current party balance + order base total) —
                // not only later at invoice posting. The override context
                // is non-null only when the posting user holds
                // SALES.CREDIT.OVERRIDE (resolved by the caller, where
                // permissions are visible); otherwise ACC-5006 hard-blocks.
                long? partyId = null;
                string? partyCode = null, docNo = null, companyId = null, currencyCode = null;
                decimal grandTotal = 0m, exchangeRate = 0m;
                DateOnly docDate = DateOnly.FromDateTime(DateTime.Today);
                await using (var read = CreateCommand(connection, transaction,
                    $"SELECT party_id, party_code, doc_no, company_id, grand_total, exchange_rate, currency_code, doc_date FROM {docs} WHERE doc_id = @doc AND is_deleted = 0"))
                {
                    Add(read, "@doc", docId);
                    await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        partyId = reader.IsDBNull(0) ? null : reader.GetInt64(0);
                        partyCode = reader.IsDBNull(1) ? null : reader.GetString(1);
                        docNo = reader.IsDBNull(2) ? null : reader.GetString(2);
                        companyId = reader.IsDBNull(3) ? null : reader.GetString(3);
                        grandTotal = reader.IsDBNull(4) ? 0m : Convert.ToDecimal(reader.GetValue(4), CultureInfo.InvariantCulture);
                        exchangeRate = reader.IsDBNull(5) ? 0m : Convert.ToDecimal(reader.GetValue(5), CultureInfo.InvariantCulture);
                        currencyCode = reader.IsDBNull(6) ? null : reader.GetString(6);
                        if (!reader.IsDBNull(7))
                            docDate = DateOnly.Parse(Convert.ToString(reader.GetValue(7), CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture);
                    }
                }
                if (partyId is not null && companyId is not null && grandTotal > 0m)
                {
                    var fx = FxRates.Resolve(connection, transaction, _provider,
                        Guid.Parse(companyId), string.IsNullOrWhiteSpace(currencyCode) ? "SAR" : currencyCode, docDate, exchangeRate);
                    var effectiveOverride = creditOverride is null
                        ? null
                        : creditOverride with { DocKind = kind, DocNo = docNo };
                    await PartyCreditGuard.EnsureWithinLimitAsync(
                        connection, transaction, _provider, companyId, "C", partyId.Value, partyCode,
                        FxRates.ToBase(grandTotal, fx.Rate), "@", effectiveOverride, cancellationToken).ConfigureAwait(false);
                }
                // W7a pricing guard at POST (SAL-7018/7019/7020): the
                // order's stored lines are re-validated because limits
                // may have tightened since save; an authorized price
                // override is audited in spr_price_overrides.
                var priceLines = new List<PriceGuardLine>();
                await using (var lineCmd = CreateCommand(connection, transaction,
                    "SELECT line_no, material_id, material_code, qty, unit_price, discount_pct FROM sales_document_lines WHERE doc_id = @doc ORDER BY line_no"))
                {
                    Add(lineCmd, "@doc", docId);
                    await using var lineReader = await lineCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    while (await lineReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        priceLines.Add(new PriceGuardLine(
                            Convert.ToInt32(lineReader.GetValue(0), CultureInfo.InvariantCulture),
                            lineReader.IsDBNull(1) ? null : Convert.ToInt64(lineReader.GetValue(1), CultureInfo.InvariantCulture),
                            lineReader.IsDBNull(2) ? null : lineReader.GetString(2),
                            Convert.ToDecimal(lineReader.GetValue(3), CultureInfo.InvariantCulture),
                            Convert.ToDecimal(lineReader.GetValue(4), CultureInfo.InvariantCulture),
                            Convert.ToDecimal(lineReader.GetValue(5), CultureInfo.InvariantCulture)));
                    }
                }
                if (companyId is not null)
                    SalesPriceGuard.Validate(connection, transaction, _provider, companyId,
                        kind, docNo, docId, priceLines,
                        priceOverride is null ? null : priceOverride with { DocKind = kind, DocNo = docNo, DocId = docId });

                // W7c contract draw-down at POST (SAL-7023 / SAL-7015):
                // the stored order is re-validated against its contract
                // because the contract may have been suspended or fully
                // drawn by other orders since save.
                if (kind == CommercialDocKinds.SalesOrder && companyId is not null
                    && PartyLedger.ColumnExists(connection, transaction, _provider, docs, "contract_id"))
                {
                    string? contractId = null;
                    DateOnly contractDocDate = default;
                    long? orderParty = null;
                    await using (var head = CreateCommand(connection, transaction,
                        $"SELECT contract_id, doc_date, party_id FROM {docs} WHERE doc_id = @doc AND is_deleted = 0"))
                    {
                        Add(head, "@doc", docId);
                        await using var headReader = await head.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                        if (await headReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        {
                            contractId = headReader.IsDBNull(0) ? null : Convert.ToString(headReader.GetValue(0), CultureInfo.InvariantCulture);
                            contractDocDate = DateOnly.Parse(Convert.ToString(headReader.GetValue(1), CultureInfo.InvariantCulture)![..10], CultureInfo.InvariantCulture);
                            orderParty = headReader.IsDBNull(2) ? null : Convert.ToInt64(headReader.GetValue(2), CultureInfo.InvariantCulture);
                        }
                    }
                    ContractGuard.ValidateOrder(connection, transaction, _provider, companyId,
                        contractId, docId, contractDocDate, orderParty,
                        priceLines.Where(l => l.MaterialId is > 0)
                            .Select(l => (l.MaterialId!.Value, l.Qty)).ToList());
                }
            }

            await using var update = CreateCommand(connection, transaction,
                $"UPDATE {docs} SET status = @posted, posted_by = @actor, posted_at = CURRENT_TIMESTAMP WHERE doc_id = @doc AND status = @draft AND is_deleted = 0");
            Add(update, "@posted", CommercialDocumentStatus.Posted);
            Add(update, "@actor", userId.Trim());
            Add(update, "@doc", docId);
            Add(update, "@draft", CommercialDocumentStatus.Draft);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("Document was not found or is not in draft status.");
            if (side == CommercialDocSide.Sales)
            {
                // A freshly posted sales order starts its stored chain
                // progress from the same computation every reader uses.
                await RecalculateStoredProgressInTransactionAsync(connection, transaction, docId, cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            // P7: a budget Stop rolled the posting back; record the
            // Blocked budget-control events after the rollback (the
            // JournalRepository / requisition boundary pattern).
            if (budgetBlocked.Count > 0 && budgetCompany != Guid.Empty)
            {
                await using var bt = await connection.BeginTransactionAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    BudgetGuard.RecordBlockedEvents(connection, bt, _provider, budgetCompany, userId, budgetBlocked);
                    await bt.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    await bt.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
            }
            throw;
        }
    }

    public async Task SetStatusAsync(CommercialDocSide side, long docId, string newStatus, string userId, CancellationToken cancellationToken = default, string? cancelReason = null)
    {
        if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("User is required.", nameof(userId));
        var allowed = newStatus switch
        {
            CommercialDocumentStatus.Closed => new[] { CommercialDocumentStatus.Posted },
            CommercialDocumentStatus.Cancelled => new[] { CommercialDocumentStatus.Draft, CommercialDocumentStatus.Posted },
            CommercialDocumentStatus.Lost => new[] { CommercialDocumentStatus.Draft, CommercialDocumentStatus.Posted },
            CommercialDocumentStatus.Expired => new[] { CommercialDocumentStatus.Posted },
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
            if (newStatus == CommercialDocumentStatus.Lost &&
                kind is not (CommercialDocKinds.SalesQuotation or CommercialDocKinds.SupplierQuotation))
                throw new InvalidOperationException("Only sales or supplier quotations can be marked as lost.");
            if (newStatus == CommercialDocumentStatus.Expired &&
                kind is not CommercialDocKinds.SalesQuotation)
                throw new InvalidOperationException("Only sales quotations can expire.");
            if (status == CommercialDocumentStatus.Posted
                && newStatus is CommercialDocumentStatus.Cancelled or CommercialDocumentStatus.Lost or CommercialDocumentStatus.Expired)
            {
                var isCommitment = kind is CommercialDocKinds.SalesOrder
                    or CommercialDocKinds.PurchaseOrder
                    or CommercialDocKinds.SalesQuotation
                    or CommercialDocKinds.RequestForQuotation
                    or CommercialDocKinds.SupplierQuotation;

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
                        "A posted commitment cannot be cancelled, marked lost, or expired while posted downstream documents are linked. Reverse or cancel the downstream documents first.");
            }

            if (Array.IndexOf(allowed, status) < 0)
                throw new InvalidOperationException($"A document in status '{status}' cannot move to '{newStatus}'.");

            // Documented cancellation (sales-cycle spec sections 3/14):
            // the transition to Cancelled stamps the reason, the actor,
            // and the timestamp in the same guarded update.
            var cancelColumns = newStatus == CommercialDocumentStatus.Cancelled
                ? ", cancel_reason = @cancelReason, cancelled_by = @actor, cancelled_at = CURRENT_TIMESTAMP"
                : string.Empty;

            // P6: cancelling a commitment cancels its DRAFT downstream
            // documents in the same transaction (posted downstream
            // already blocked above). Drafts carry no GL/stock/ledger
            // effects, so a status flip is the whole cascade. The
            // downstream table is the same family as the source for
            // every commitment kind this guard admits.
            if (newStatus == CommercialDocumentStatus.Cancelled)
            {
                await using var cascade = CreateCommand(connection, transaction,
                    $"UPDATE {docs} SET status = @cancelled WHERE doc_id IN " +
                    "(SELECT dl.target_doc_id FROM document_links dl WHERE dl.company_id = @company AND dl.source_doc_kind = @kind AND dl.source_doc_id = @doc) " +
                    "AND status = @draft AND is_deleted = 0");
                Add(cascade, "@cancelled", CommercialDocumentStatus.Cancelled);
                Add(cascade, "@company", companyId);
                Add(cascade, "@kind", kind);
                Add(cascade, "@doc", docId);
                Add(cascade, "@draft", CommercialDocumentStatus.Draft);
                await cascade.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using var update = CreateCommand(connection, transaction,
                $"UPDATE {docs} SET status = @new{cancelColumns} WHERE doc_id = @doc AND status = @old AND is_deleted = 0");
            Add(update, "@new", newStatus);
            Add(update, "@cancelReason", (object?)cancelReason?.Trim() ?? DBNull.Value);
            Add(update, "@actor", userId.Trim());
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
        var kind = docKind.Trim();
        var isPg = _provider == "PostgreSQL";
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        // The sequence is keyed by the company CODE so chain SAL/PUR
        // documents share ONE counter with InvRepo's (NUMBERING_STUDY).
        var companyCode = await ResolveCompanyCodeAsync(connection, null, companyId, cancellationToken).ConfigureAwait(false)
            ?? companyId.ToString("D");
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var config = await SequenceAllocator.ReadConfigAsync(connection, transaction, isPg, companyCode, kind,
                SequenceAllocator.DefaultConfig(PrefixFor(kind)), cancellationToken).ConfigureAwait(false);
            var year = DateTime.Today.Year;
            var segment = config.YearlySegment ? year : 0;
            var documentPrefix = config.YearlySegment ? $"{config.Prefix}-{year}-" : $"{config.Prefix}-";
            var suffixStart = documentPrefix.Length + 1;
            var substr = isPg ? "SUBSTRING(doc_no FROM @start)" : "SUBSTR(doc_no, @start)";
            var predicate = isPg
                ? $"{substr} ~ '^[0-9]+$'"
                : $"{substr} GLOB '[0-9]*' AND {substr} NOT GLOB '*[^0-9]*'";
            long seed;
            await using (var maxCommand = CreateCommand(connection, transaction, $"""
                SELECT COALESCE(MAX(CAST({substr} AS {(isPg ? "BIGINT" : "INTEGER")})), 0)
                FROM {docs}
                WHERE company_id IN (@guid, @code) AND doc_kind = @kind
                  AND doc_no LIKE @pattern AND {predicate}
                """))
            {
                Add(maxCommand, "@guid", companyId.ToString("D"));
                Add(maxCommand, "@code", companyCode);
                Add(maxCommand, "@kind", kind);
                Add(maxCommand, "@pattern", $"{documentPrefix}%");
                Add(maxCommand, "@start", suffixStart);
                seed = Convert.ToInt64(await maxCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            }
            var allocated = await SequenceAllocator.AllocateAsync(connection, transaction, isPg, companyCode, kind,
                segment, seed, config.StartValue - 1, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return SequenceAllocator.Format(config, segment, allocated);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
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
            SELECT doc_id, doc_kind, doc_no, doc_date, party_code, party_name, grand_total, status, delivery_date, valid_till, per_received, per_billed
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
                reader.IsDBNull(9) ? null : DateOnly.Parse(Convert.ToString(reader.GetValue(9), CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture),
                reader.IsDBNull(10) ? 0m : Convert.ToDecimal(reader.GetValue(10), CultureInfo.InvariantCulture),
                reader.IsDBNull(11) ? 0m : Convert.ToDecimal(reader.GetValue(11), CultureInfo.InvariantCulture)));
        }
        return results.AsReadOnly();
    }

    // -------------------------------------------------------------- progress

    public async Task<OrderProgress> GetProgressAsync(CommercialDocSide side, long docId, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ComputeProgressAsync(connection, null, side, docId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The single progress computation behind both <see cref="GetProgressAsync"/>
    /// and the stored per_delivered / per_billed columns: fulfilment and billing
    /// are aggregated BY SOURCE LINE for both sides (chain-created target lines
    /// persist line_no = source line number), with the material-keyed legacy
    /// fallback inside LinkedQuantitiesBySourceLineAsync for target lines that
    /// predate line identity.
    /// </summary>
    private async Task<OrderProgress> ComputeProgressAsync(
        DbConnection connection, DbTransaction? transaction, CommercialDocSide side,
        long docId, CancellationToken cancellationToken)
    {
        var order = await ReadSnapshotAsync(connection, transaction, side, docId, null, null, null, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Order was not found.");

        var fulfillKind = side == CommercialDocSide.Sales ? CommercialDocKinds.DeliveryNote : CommercialDocKinds.PurchaseReceipt;
        var billedKind = side == CommercialDocSide.Sales ? CommercialDocKinds.SalesInvoice : CommercialDocKinds.PurchaseInvoice;
        var fulfilledByLine = await LinkedQuantitiesBySourceLineAsync(
            connection, transaction, side, order.CompanyId, docId, fulfillKind, order.Lines, cancellationToken).ConfigureAwait(false);
        var billedByLine = await LinkedQuantitiesBySourceLineAsync(
            connection, transaction, side, order.CompanyId, docId, billedKind, order.Lines, cancellationToken).ConfigureAwait(false);

        var lines = new List<OrderProgressLine>();
        decimal totalOrdered = 0m, totalFulfilled = 0m, totalBilled = 0m;
        foreach (var line in order.Lines)
        {
            var lineFulfilled = Math.Min(line.Qty, fulfilledByLine.GetValueOrDefault(line.LineNo));
            var lineBilled = Math.Min(line.Qty, billedByLine.GetValueOrDefault(line.LineNo));
            lines.Add(new OrderProgressLine(line.MaterialId, line.MaterialCode, line.Description, line.Qty, lineFulfilled, lineBilled, line.LineNo));
            totalOrdered += line.Qty;
            totalFulfilled += lineFulfilled;
            totalBilled += lineBilled;
        }
        var perFulfilled = totalOrdered <= 0m ? 0m : decimal.Round(100m * totalFulfilled / totalOrdered, 2);
        var perBilled = totalOrdered <= 0m ? 0m : decimal.Round(100m * totalBilled / totalOrdered, 2);
        return new OrderProgress(order.DocId, order.DocKind, order.DocNo, lines.AsReadOnly(), perFulfilled, perBilled, order.Status);
    }

    public async Task RecalculateStoredProgressAsync(long salesDocId, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RecalculateStoredProgressInTransactionAsync(connection, transaction, salesDocId, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Recomputes and persists per_delivered / per_billed for one sales
    /// document inside the caller's transaction, so a chain creation,
    /// posting, or conversion and the progress it produces commit or roll
    /// back together. Internal: CommercialDocumentRepository (same assembly,
    /// same database) calls it from its chain-creation transaction.
    /// </summary>
    internal async Task RecalculateStoredProgressInTransactionAsync(
        DbConnection connection, DbTransaction transaction, long salesDocId, CancellationToken cancellationToken)
    {
        var progress = await ComputeProgressAsync(
            connection, transaction, CommercialDocSide.Sales, salesDocId, cancellationToken).ConfigureAwait(false);
        await using var update = CreateCommand(connection, transaction, """
            UPDATE sales_documents
            SET per_delivered = @perDelivered, per_billed = @perBilled
            WHERE doc_id = @doc AND is_deleted = 0
            """);
        Add(update, "@perDelivered", progress.PerFulfilled);
        Add(update, "@perBilled", progress.PerBilled);
        Add(update, "@doc", salesDocId);
        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("Sales document was not found while storing chain progress.");
    }

    /// <summary>
    /// P5 (migration 111): recomputes a purchase order's STORED
    /// per_received / per_billed from the same linked-quantity semantics
    /// as <see cref="GetProgressAsync"/> (per-line capped, qty-weighted),
    /// net of PRT match unwinds (I4), and writes them inside the caller's
    /// posting transaction. Called by the receipt, receipt-return,
    /// invoice and return paths so the stored values can never drift
    /// from the chain documents.
    /// </summary>
    private async Task RefreshStoredProgressAsync(
        DbConnection connection, DbTransaction transaction, long purchaseOrderId,
        CancellationToken cancellationToken)
    {
        var order = await ReadSnapshotAsync(connection, transaction, CommercialDocSide.Purchase,
            purchaseOrderId, null, CommercialDocKinds.PurchaseOrder, null, cancellationToken).ConfigureAwait(false);
        if (order is null) return;
        var receivedByLine = await LinkedQuantitiesBySourceLineAsync(
            connection, transaction, CommercialDocSide.Purchase, order.CompanyId,
            purchaseOrderId, CommercialDocKinds.PurchaseReceipt, order.Lines, cancellationToken).ConfigureAwait(false);
        var billedByLine = new Dictionary<int, decimal>(await LinkedQuantitiesBySourceLineAsync(
            connection, transaction, CommercialDocSide.Purchase, order.CompanyId,
            purchaseOrderId, CommercialDocKinds.PurchaseInvoice, order.Lines, cancellationToken).ConfigureAwait(false));

        // I4 note: billed quantities already net posted PRT returns —
        // LinkedQuantitiesBySourceLineAsync subtracts the mig-080
        // purchase-return allocations for PUR targets, and the 078
        // match unwind in InvRepo keeps the match table consistent.

        decimal totalOrdered = 0m, totalReceived = 0m, totalBilled = 0m;
        foreach (var line in order.Lines)
        {
            totalOrdered += line.Qty;
            totalReceived += Math.Min(line.Qty, receivedByLine.GetValueOrDefault(line.LineNo));
            totalBilled += Math.Min(line.Qty, billedByLine.GetValueOrDefault(line.LineNo));
        }
        var perReceived = totalOrdered <= 0m ? 0m : decimal.Round(100m * totalReceived / totalOrdered, 2);
        var perBilled = totalOrdered <= 0m ? 0m : decimal.Round(100m * totalBilled / totalOrdered, 2);
        await using var update = CreateCommand(connection, transaction,
            "UPDATE purchase_documents SET per_received = @received, per_billed = @billed WHERE doc_id = @doc");
        Add(update, "@received", perReceived);
        Add(update, "@billed", perBilled);
        Add(update, "@doc", purchaseOrderId);
        await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyDictionary<long, decimal>> LinkedQuantitiesAsync(
        DbConnection connection, DbTransaction? transaction, CommercialDocSide side,
        Guid companyId, long sourceDocId, string targetDocKind, CancellationToken cancellationToken)
    {
        var (docs, lines) = Tables(side);
        await using var command = CreateCommand(connection, transaction, $"""
            SELECT l.material_id, l.line_no,
                   SUM(l.qty) - CASE WHEN @targetKind = @receiptKind THEN COALESCE((
                       SELECT SUM(a.returned_qty)
                       FROM purchase_receipt_return_line_allocations a
                       JOIN {docs} rd
                         ON rd.doc_id = a.return_doc_id
                        AND rd.company_id = a.company_id
                        AND rd.doc_kind = @returnKind
                        AND rd.status = @posted
                        AND rd.is_deleted = 0
                       WHERE a.company_id = dl.company_id
                         AND a.original_doc_id = d.doc_id
                         AND a.original_line_id = l.line_id
                   ), 0) ELSE 0 END
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
            GROUP BY l.material_id, l.line_no, l.line_id, d.doc_id, dl.company_id
            """);
        Add(command, "@company", companyId.ToString("D"));
        Add(command, "@sourceId", sourceDocId);
        Add(command, "@targetKind", targetDocKind);
        Add(command, "@posted", CommercialDocumentStatus.Posted);
        Add(command, "@receiptKind", CommercialDocKinds.PurchaseReceipt);
        Add(command, "@returnKind", CommercialDocKinds.PurchaseReceiptReturn);

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

    private async Task<IReadOnlyDictionary<int, decimal>> LinkedQuantitiesBySourceLineAsync(
        DbConnection connection, DbTransaction? transaction, CommercialDocSide side,
        Guid companyId, long sourceDocId, string targetDocKind,
        IReadOnlyList<CommercialLineSnapshot> sourceLines, CancellationToken cancellationToken)
    {
        var (docs, lines) = Tables(side);
        // W5 (migration 102, sales side only): consolidated invoice
        // lines carry explicit source attribution (source_doc_id =
        // delivery, source_line_no = that delivery's line). Such a line
        // counts toward THIS order only when its delivery is linked
        // from this order, and it is keyed by the delivery's line
        // number (which by chain identity equals the order's). The
        // purchase tables have no attribution columns; constants keep
        // the row shape identical.
        var attributionColumns = side == CommercialDocSide.Sales
            ? """
              , l.source_doc_id, l.source_line_no,
                CASE WHEN l.source_doc_id IS NULL THEN 1
                     WHEN EXISTS (
                         SELECT 1 FROM document_links dlv_link
                         WHERE dlv_link.company_id = dl.company_id
                           AND dlv_link.source_doc_id = @sourceId
                           AND dlv_link.target_doc_kind = @deliveryKind
                           AND dlv_link.target_doc_id = l.source_doc_id) THEN 1
                     ELSE 0 END
              """
            : ", NULL, NULL, 1";
        await using var command = CreateCommand(connection, transaction, $"""
            SELECT l.line_no, l.material_id,
                   SUM(l.qty) - CASE WHEN @targetKind = @receiptKind THEN COALESCE((
                       SELECT SUM(a.returned_qty)
                       FROM purchase_receipt_return_line_allocations a
                       JOIN {docs} rd
                         ON rd.doc_id = a.return_doc_id
                        AND rd.company_id = a.company_id
                        AND rd.doc_kind = @returnKind
                        AND rd.status = @posted
                        AND rd.is_deleted = 0
                       WHERE a.company_id = dl.company_id
                         AND a.original_doc_id = d.doc_id
                         AND a.original_line_id = l.line_id
                   ), 0)
                   WHEN @targetKind = @invoiceKind THEN COALESCE((
                       -- I4: a posted purchase return (PRT debit note)
                       -- un-bills the returned quantity, so the goods
                       -- can be re-invoiced. Allocations key the original
                       -- invoice line; the PRT document itself is the
                       -- company/status guard (its company key is the
                       -- invoice family's CODE, not this Guid).
                       SELECT SUM(a.returned_qty)
                       FROM purchase_return_line_allocations a
                       JOIN {docs} rd
                         ON rd.doc_id = a.return_doc_id
                        AND rd.doc_kind = @prtKind
                        AND rd.status = @posted
                        AND CAST(rd.is_deleted AS TEXT) IN ('0', 'false')
                       WHERE a.original_doc_id = d.doc_id
                         AND a.original_line_id = l.line_id
                   ), 0) ELSE 0 END
                   {attributionColumns}
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
            GROUP BY l.line_no, l.material_id, l.line_id, d.doc_id, dl.company_id
            """);
        Add(command, "@company", companyId.ToString("D"));
        Add(command, "@sourceId", sourceDocId);
        Add(command, "@targetKind", targetDocKind);
        Add(command, "@posted", CommercialDocumentStatus.Posted);
        Add(command, "@receiptKind", CommercialDocKinds.PurchaseReceipt);
        Add(command, "@returnKind", CommercialDocKinds.PurchaseReceiptReturn);
        if (side == CommercialDocSide.Sales)
            Add(command, "@deliveryKind", CommercialDocKinds.DeliveryNote);
        Add(command, "@invoiceKind", CommercialDocKinds.PurchaseInvoice);
        Add(command, "@prtKind", CommercialDocKinds.PurchaseReturn);

        var quantities = new Dictionary<int, decimal>();
        var legacyByMaterial = new Dictionary<long, decimal>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var lineNo = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
            var materialId = reader.IsDBNull(1)
                ? (long?)null
                : Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
            var qty = Convert.ToDecimal(reader.GetValue(2), CultureInfo.InvariantCulture);
            var lineSourceDocId = reader.IsDBNull(3) ? (long?)null : Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture);
            var lineSourceLineNo = reader.IsDBNull(4) ? (int?)null : Convert.ToInt32(reader.GetValue(4), CultureInfo.InvariantCulture);
            var attributedToSource = Convert.ToInt32(reader.GetValue(5), CultureInfo.InvariantCulture) != 0;
            if (lineSourceDocId is not null)
            {
                if (!attributedToSource)
                    continue;
                lineNo = lineSourceLineNo ?? lineNo;
            }
            var sourceLineExists = sourceLines.Any(line => line.LineNo == lineNo && line.MaterialId == materialId);
            if (sourceLineExists)
            {
                quantities[lineNo] = quantities.GetValueOrDefault(lineNo) + qty;
            }
            else if (materialId is long legacyMaterialId)
            {
                legacyByMaterial[legacyMaterialId] = legacyByMaterial.GetValueOrDefault(legacyMaterialId) + qty;
            }
        }

        foreach (var (materialId, legacyQty) in legacyByMaterial)
        {
            var remaining = legacyQty;
            foreach (var sourceLine in sourceLines
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

    public async Task<long> CreatePurchaseReceiptAsync(
        Guid companyId, long purchaseOrderId, string receiptDocNo, DateOnly receiptDate,
        IReadOnlyList<CommercialLineDraft> receivedLines, string userId,
        string? deliveryNoteNo = null,
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
                    ? "SELECT doc_id FROM purchase_documents WHERE company_id = @company AND doc_id = @doc AND doc_kind = @kind AND status = @posted AND CAST(is_deleted AS TEXT) IN ('0', 'false') FOR UPDATE"
                    : "SELECT doc_id FROM purchase_documents WHERE company_id = @company AND doc_id = @doc AND doc_kind = @kind AND status = @posted AND CAST(is_deleted AS TEXT) IN ('0', 'false')"))
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

            // Q4: the receipt over-delivery tolerance % from the
            // purchase match policy (mig 082 table, mig 111 column).
            // Default 0 = the historical hard block at 100%.
            var tolerancePct = 0m;
            var companyCodeForPolicy = await ResolveCompanyCodeAsync(connection, transaction, companyId, cancellationToken).ConfigureAwait(false);
            if (companyCodeForPolicy is not null
                && PartyLedger.TableExists(connection, transaction, _provider, "purchase_match_policies")
                && PartyLedger.ColumnExists(connection, transaction, _provider, "purchase_match_policies", "receipt_over_tolerance_pct"))
            {
                await using var policyCmd = CreateCommand(connection, transaction,
                    "SELECT receipt_over_tolerance_pct FROM purchase_match_policies WHERE company_code = @company");
                Add(policyCmd, "@company", companyCodeForPolicy);
                var policyValue = await policyCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (policyValue is not null and not DBNull)
                    tolerancePct = Math.Max(0m, Convert.ToDecimal(policyValue, CultureInfo.InvariantCulture));
            }
            var toleranceFactor = 1m + tolerancePct / 100m;

            var requested = new Dictionary<long, decimal>();
            var requestedWarehouse = new Dictionary<long, long?>();
            var requestedBySourceLine = new Dictionary<int, CommercialLineDraft>();
            foreach (var line in receivedLines)
            {
                if (line.MaterialId is not long materialId || materialId <= 0)
                    throw new InvalidOperationException("Every receipt line must identify a material from the purchase order.");
                if (line.Qty <= 0m)
                    throw new InvalidOperationException("Received quantities must be greater than zero.");
                // Q2: rejected qty is recorded on the receipt line; it
                // never counts as received (see the receipt-line build
                // below) and carries no stock or GL effect — the goods
                // never enter the books, exactly like goods turned away
                // at the gate before the QC flow starts.
                if (line.RejectedQty < 0m)
                    throw new InvalidOperationException("Rejected quantities cannot be negative.");
                if (line.RejectedQty > 0m && string.IsNullOrWhiteSpace(line.RejectReason))
                    throw new InvalidOperationException("A rejected quantity at receipt requires a reason.");
                requested[materialId] = requested.GetValueOrDefault(materialId) + line.Qty;
                if (line.WarehouseId is not null)
                    requestedWarehouse[materialId] = line.WarehouseId;
                if (line.SourceLineNo is int sourceLineNo)
                {
                    var sourceLine = order.Lines.SingleOrDefault(candidate => candidate.LineNo == sourceLineNo);
                    if (sourceLine is null || sourceLine.MaterialId != materialId)
                        throw new InvalidOperationException("A receipt source line must identify a matching purchase-order line.");
                    if (!requestedBySourceLine.TryAdd(sourceLineNo, line))
                        throw new InvalidOperationException("A purchase-order line can appear only once in a goods receipt.");
                }
            }

            var received = await LinkedQuantitiesAsync(connection, transaction, CommercialDocSide.Purchase,
                order.CompanyId, purchaseOrderId, CommercialDocKinds.PurchaseReceipt, cancellationToken).ConfigureAwait(false);
            var receivedBySourceLine = await LinkedQuantitiesBySourceLineAsync(
                connection, transaction, CommercialDocSide.Purchase, order.CompanyId,
                purchaseOrderId, CommercialDocKinds.PurchaseReceipt, order.Lines, cancellationToken).ConfigureAwait(false);
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
                // Q4: the hard block applies beyond ordered x (1 + tolerance).
                var allowed = decimal.Round(orderedQty * toleranceFactor, 6, MidpointRounding.AwayFromZero) - alreadyReceived;
                if (qty > allowed)
                    throw new InvalidOperationException(
                        $"Receipt quantity for material '{materialId}' exceeds the remaining purchase-order quantity ({remaining}) beyond the {tolerancePct}% over-receipt tolerance.");
            }

            var requestedToAllocate = new Dictionary<long, decimal>(requested);
            var explicitlyReceivedBySourceLine = new Dictionary<int, decimal>();
            var receiptDraftLines = new List<CommercialLineDraft>();
            foreach (var line in order.Lines)
            {
                if (!requestedBySourceLine.TryGetValue(line.LineNo, out var explicitRequest))
                    continue;
                var materialId = line.MaterialId!.Value;
                var prior = Math.Min(line.Qty, receivedBySourceLine.GetValueOrDefault(line.LineNo));
                var remaining = line.Qty - prior;
                var allowedLine = decimal.Round(line.Qty * toleranceFactor, 6, MidpointRounding.AwayFromZero) - prior;
                if (explicitRequest.Qty > allowedLine)
                    throw new InvalidOperationException(
                        $"Receipt quantity for purchase-order line {line.LineNo} exceeds its remaining quantity ({Math.Max(0m, remaining)}) beyond the {tolerancePct}% over-receipt tolerance.");
                explicitlyReceivedBySourceLine[line.LineNo] = explicitRequest.Qty;
                requestedToAllocate[materialId] -= explicitRequest.Qty;
                receiptDraftLines.Add(new CommercialLineDraft(
                    line.MaterialId, line.MaterialCode, line.Description, line.Unit,
                    explicitRequest.Qty, line.UnitPrice, line.DiscountPct, 0m,
                    explicitRequest.WarehouseId ?? line.WarehouseId,
                    line.LineNo,
                    explicitRequest.BatchAllocations,
                    explicitRequest.Serials,
                    explicitRequest.AllowExpiredBatchIssue,
                    RejectedQty: explicitRequest.RejectedQty, RejectReason: explicitRequest.RejectReason,
                    UomFactor: line.UomFactor, LineRequiredBy: line.LineRequiredBy ?? order.RequiredByDate, TaxCode: line.TaxCode));
            }

            foreach (var line in order.Lines)
            {
                var materialId = line.MaterialId!.Value;
                var prior = Math.Min(line.Qty, receivedBySourceLine.GetValueOrDefault(line.LineNo));
                var explicitQty = explicitlyReceivedBySourceLine.GetValueOrDefault(line.LineNo);
                var lineRemaining = decimal.Round(line.Qty * toleranceFactor, 6, MidpointRounding.AwayFromZero) - prior - explicitQty;
                var take = Math.Min(lineRemaining, requestedToAllocate.GetValueOrDefault(materialId));
                if (take <= 0m) continue;
                requestedToAllocate[materialId] -= take;
                receiptDraftLines.Add(new CommercialLineDraft(
                    line.MaterialId, line.MaterialCode, line.Description, line.Unit,
                    take, line.UnitPrice, line.DiscountPct, 0m,
                    requestedWarehouse.TryGetValue(materialId, out var chosenWarehouse)
                        ? chosenWarehouse : line.WarehouseId,
                    line.LineNo,
                    UomFactor: line.UomFactor,
                    LineRequiredBy: line.LineRequiredBy ?? order.RequiredByDate,
                    TaxCode: line.TaxCode));
            }

            if (receiptDraftLines.Count == 0 || requestedToAllocate.Values.Any(qty => qty > 0m))
                throw new InvalidOperationException("The requested receipt quantities could not be matched to the remaining purchase-order lines.");

            var receiptDraft = new CommercialDocumentDraft(
                order.CompanyId, CommercialDocKinds.PurchaseReceipt, receiptDocNo.Trim(), receiptDate,
                order.PartyId, order.PartyName, order.WarehouseId, order.CurrencyCode, order.ExchangeRate,
                $"Goods receipt against purchase order {order.DocNo}",
                receiptDraftLines, order.PartyCode, null, null, order.CostCenter,
                order.BranchName, order.SalesRepName, order.WarehouseName,
                DeliveryNoteNo: string.IsNullOrWhiteSpace(deliveryNoteNo) ? null : deliveryNoteNo.Trim());
            var receiptId = await InsertDocumentAsync(connection, transaction, CommercialDocSide.Purchase,
                receiptDraft, CommercialDocumentStatus.Posted, userId, cancellationToken).ConfigureAwait(false);
            ComputeTotals(receiptDraftLines, out var receiptSubtotal, out _, out _);

            _ = await PostConversionStockAsync(
                connection, transaction, CommercialDocSide.Purchase, order, receiptDraftLines,
                receiptDocNo.Trim(), receiptDate, cancellationToken, CommercialDocKinds.PurchaseReceipt, userId, sourceDocumentId: receiptId).ConfigureAwait(false);

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

            // P5: maintain the PO's stored percentages in this transaction.
            await RefreshStoredProgressAsync(connection, transaction, purchaseOrderId, cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return receiptId;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IReadOnlyList<PurchaseReceiptReturnableLine>> GetPurchaseReceiptReturnableLinesAsync(
        Guid companyId, long receiptId, CancellationToken cancellationToken = default)
    {
        if (companyId == Guid.Empty) throw new ArgumentException("Company is required.", nameof(companyId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(receiptId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var receipt = await ReadSnapshotAsync(connection, null, CommercialDocSide.Purchase,
            receiptId, companyId, CommercialDocKinds.PurchaseReceipt, null, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The goods receipt was not found in the selected company.");
        if (receipt.Status != CommercialDocumentStatus.Posted)
            throw new InvalidOperationException("Only a posted goods receipt can be returned.");

        var invoiced = new Dictionary<long, decimal>();
        await using (var matches = CreateCommand(connection, null, """
            SELECT m.receipt_line_id, COALESCE(SUM(m.matched_qty), 0)
            FROM purchase_receipt_invoice_matches m
            INNER JOIN purchase_documents inv
              ON inv.company_id = m.company_id
             AND inv.doc_id = m.invoice_doc_id
             AND inv.doc_kind = @invoiceKind
             AND inv.status = @posted
             AND CAST(inv.is_deleted AS TEXT) IN ('0', 'false')
            WHERE m.company_id = @company AND m.receipt_doc_id = @receipt
            GROUP BY m.receipt_line_id
            """))
        {
            Add(matches, "@invoiceKind", CommercialDocKinds.PurchaseInvoice);
            Add(matches, "@posted", CommercialDocumentStatus.Posted);
            Add(matches, "@company", companyId.ToString("D"));
            Add(matches, "@receipt", receiptId);
            await using var reader = await matches.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                invoiced[Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture)] =
                    Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture);
        }

        var returned = new Dictionary<long, decimal>();
        await using (var returns = CreateCommand(connection, null, """
            SELECT a.original_line_id, COALESCE(SUM(a.returned_qty), 0)
            FROM purchase_receipt_return_line_allocations a
            JOIN purchase_documents d
              ON d.doc_id = a.return_doc_id
             AND d.company_id = a.company_id
             AND d.doc_kind = @returnKind
             AND d.status = @posted
             AND d.is_deleted = 0
            WHERE a.company_id = @company AND a.original_doc_id = @receipt
            GROUP BY a.original_line_id
            """))
        {
            Add(returns, "@returnKind", CommercialDocKinds.PurchaseReceiptReturn);
            Add(returns, "@posted", CommercialDocumentStatus.Posted);
            Add(returns, "@company", companyId.ToString("D"));
            Add(returns, "@receipt", receiptId);
            await using var reader = await returns.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                returned[Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture)] =
                    Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture);
        }

        return receipt.Lines.OrderBy(line => line.LineNo).Select(line =>
        {
            var invoicedQty = invoiced.GetValueOrDefault(line.LineId);
            var returnedQty = returned.GetValueOrDefault(line.LineId);
            return new PurchaseReceiptReturnableLine(
                line.LineId, line.LineNo, line.MaterialId, line.MaterialCode, line.Description,
                line.Unit, line.Qty, invoicedQty, returnedQty,
                Math.Max(0m, line.Qty - invoicedQty - returnedQty),
                line.UnitPrice, line.DiscountPct, line.WarehouseId);
        }).ToArray();
    }

    public async Task<long> CreatePurchaseReceiptReturnAsync(
        Guid companyId, long receiptId, string returnDocNo, DateOnly returnDate,
        IReadOnlyList<CommercialLineDraft> returnedLines, string userId, string? reason = null,
        CancellationToken cancellationToken = default)
    {
        if (companyId == Guid.Empty) throw new ArgumentException("Company is required.", nameof(companyId));
        if (receiptId <= 0) throw new ArgumentOutOfRangeException(nameof(receiptId));
        if (string.IsNullOrWhiteSpace(returnDocNo)) throw new ArgumentException("Return number is required.", nameof(returnDocNo));
        if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("User is required.", nameof(userId));
        ArgumentNullException.ThrowIfNull(returnedLines);
        if (returnedLines.Count == 0) throw new InvalidOperationException("A goods receipt return requires at least one line.");

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Resolve the source PO first, then lock it. Invoice conversion and
            // receipt returns serialize on this same row to prevent a race where
            // the same receipt quantity is both invoiced and returned.
            long purchaseOrderId;
            await using (var sourceOrder = CreateCommand(connection, transaction, """
                SELECT source_doc_id
                FROM document_links
                WHERE company_id = @company
                  AND source_doc_kind = @orderKind
                  AND target_doc_kind = @receiptKind
                  AND target_doc_id = @receiptId
                ORDER BY link_id
                LIMIT 1
                """))
            {
                Add(sourceOrder, "@company", companyId.ToString("D"));
                Add(sourceOrder, "@orderKind", CommercialDocKinds.PurchaseOrder);
                Add(sourceOrder, "@receiptKind", CommercialDocKinds.PurchaseReceipt);
                Add(sourceOrder, "@receiptId", receiptId);
                var value = await sourceOrder.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (value is null or DBNull)
                    throw new InvalidOperationException("The goods receipt is not linked to a purchase order.");
                purchaseOrderId = Convert.ToInt64(value, CultureInfo.InvariantCulture);
            }

            await using (var orderLock = CreateCommand(connection, transaction,
                _provider == "PostgreSQL"
                    ? "SELECT doc_id FROM purchase_documents WHERE company_id = @company AND doc_id = @order AND doc_kind = @kind AND status = @posted AND is_deleted = 0 FOR UPDATE"
                    : "SELECT doc_id FROM purchase_documents WHERE company_id = @company AND doc_id = @order AND doc_kind = @kind AND status = @posted AND is_deleted = 0"))
            {
                Add(orderLock, "@company", companyId.ToString("D"));
                Add(orderLock, "@order", purchaseOrderId);
                Add(orderLock, "@kind", CommercialDocKinds.PurchaseOrder);
                Add(orderLock, "@posted", CommercialDocumentStatus.Posted);
                if (await orderLock.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
                    throw new InvalidOperationException("Only a posted purchase order can have a receipt return.");
            }

            await using (var receiptLock = CreateCommand(connection, transaction,
                _provider == "PostgreSQL"
                    ? "SELECT doc_id FROM purchase_documents WHERE company_id = @company AND doc_id = @receipt AND doc_kind = @kind AND status = @posted AND is_deleted = 0 FOR UPDATE"
                    : "SELECT doc_id FROM purchase_documents WHERE company_id = @company AND doc_id = @receipt AND doc_kind = @kind AND status = @posted AND is_deleted = 0"))
            {
                Add(receiptLock, "@company", companyId.ToString("D"));
                Add(receiptLock, "@receipt", receiptId);
                Add(receiptLock, "@kind", CommercialDocKinds.PurchaseReceipt);
                Add(receiptLock, "@posted", CommercialDocumentStatus.Posted);
                if (await receiptLock.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
                    throw new InvalidOperationException("Only a posted goods receipt can be returned.");
            }

            var order = await ReadSnapshotAsync(connection, transaction, CommercialDocSide.Purchase,
                purchaseOrderId, companyId, CommercialDocKinds.PurchaseOrder, null, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The source purchase order could not be loaded.");
            var receipt = await ReadSnapshotAsync(connection, transaction, CommercialDocSide.Purchase,
                receiptId, companyId, CommercialDocKinds.PurchaseReceipt, null, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The source goods receipt could not be loaded.");
            if (receipt.PartyId is null || receipt.PartyId != order.PartyId)
                throw new InvalidOperationException("The receipt supplier does not match its purchase order.");
            if (returnDate < receipt.DocDate)
                throw new InvalidOperationException("A goods receipt return date cannot precede its source receipt date.");

            var matchedByLine = new Dictionary<long, decimal>();
            await using (var matched = CreateCommand(connection, transaction, """
                SELECT m.receipt_line_id, COALESCE(SUM(m.matched_qty), 0)
                FROM purchase_receipt_invoice_matches m
                INNER JOIN purchase_documents inv
                  ON inv.company_id = m.company_id
                 AND inv.doc_id = m.invoice_doc_id
                 AND inv.doc_kind = @invoiceKind
                 AND inv.status = @posted
                 AND CAST(inv.is_deleted AS TEXT) IN ('0', 'false')
                WHERE m.company_id = @company AND m.receipt_doc_id = @receipt
                GROUP BY m.receipt_line_id
                """))
            {
                Add(matched, "@invoiceKind", CommercialDocKinds.PurchaseInvoice);
                Add(matched, "@posted", CommercialDocumentStatus.Posted);
                Add(matched, "@company", companyId.ToString("D"));
                Add(matched, "@receipt", receiptId);
                await using var reader = await matched.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    matchedByLine[Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture)] =
                        Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture);
            }

            var returnedByLine = new Dictionary<long, decimal>();
            await using (var returned = CreateCommand(connection, transaction, """
                SELECT a.original_line_id, COALESCE(SUM(a.returned_qty), 0)
                FROM purchase_receipt_return_line_allocations a
                JOIN purchase_documents d
                  ON d.doc_id = a.return_doc_id
                 AND d.company_id = a.company_id
                 AND d.doc_kind = @returnKind
                 AND d.status = @posted
                 AND d.is_deleted = 0
                WHERE a.company_id = @company AND a.original_doc_id = @receipt
                GROUP BY a.original_line_id
                """))
            {
                Add(returned, "@returnKind", CommercialDocKinds.PurchaseReceiptReturn);
                Add(returned, "@posted", CommercialDocumentStatus.Posted);
                Add(returned, "@company", companyId.ToString("D"));
                Add(returned, "@receipt", receiptId);
                await using var reader = await returned.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    returnedByLine[Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture)] =
                        Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture);
            }

            var receiptLines = receipt.Lines.ToDictionary(line => line.LineNo);
            var requestedLines = new Dictionary<int, CommercialLineDraft>();
            foreach (var requested in returnedLines)
            {
                if (requested.SourceLineNo is not int sourceLineNo ||
                    !receiptLines.TryGetValue(sourceLineNo, out var sourceLine))
                    throw new InvalidOperationException("Every return line must identify an original goods receipt line.");
                if (!requestedLines.TryAdd(sourceLineNo, requested))
                    throw new InvalidOperationException("A goods receipt line can appear only once in a return document.");
                if (requested.Qty <= 0m || requested.MaterialId != sourceLine.MaterialId)
                    throw new InvalidOperationException("Return quantities must be positive and material identities must match the source receipt.");
                var invoiced = matchedByLine.GetValueOrDefault(sourceLine.LineId);
                var alreadyReturned = returnedByLine.GetValueOrDefault(sourceLine.LineId);
                var remaining = sourceLine.Qty - invoiced - alreadyReturned;
                if (requested.Qty > remaining)
                    throw new InvalidOperationException(
                        $"Return quantity for receipt line {sourceLineNo} exceeds the unbilled, unreturned quantity ({Math.Max(0m, remaining)}).");
            }

            // A return document owns its own sequential line numbers. Keep the
            // source receipt line number separately for the allocation audit;
            // SourceLineNo must not become the new document's line number.
            var returnDraftLines = new List<CommercialLineDraft>();
            var returnSourceLineNumbers = new List<int>();
            foreach (var (sourceLineNo, requested) in requestedLines.OrderBy(x => x.Key))
            {
                var sourceLine = receiptLines[sourceLineNo];
                returnSourceLineNumbers.Add(sourceLineNo);
                returnDraftLines.Add(new CommercialLineDraft(
                    sourceLine.MaterialId, sourceLine.MaterialCode, sourceLine.Description,
                    sourceLine.Unit, requested.Qty, sourceLine.UnitPrice, sourceLine.DiscountPct,
                    0m, sourceLine.WarehouseId, null,
                    requested.BatchAllocations, requested.Serials, requested.AllowExpiredBatchIssue));
            }

            var receiptFx = FxRates.Resolve(connection, transaction, _provider,
                companyId, receipt.CurrencyCode, receipt.DocDate, receipt.ExchangeRate);
            var returnDraft = new CommercialDocumentDraft(
                companyId, CommercialDocKinds.PurchaseReceiptReturn, returnDocNo.Trim(), returnDate,
                receipt.PartyId, receipt.PartyName, receipt.WarehouseId, receipt.CurrencyCode,
                receiptFx.Rate, $"Return against goods receipt {receipt.DocNo}. Reason: {(string.IsNullOrWhiteSpace(reason) ? "Supplier return" : reason.Trim())}",
                returnDraftLines, receipt.PartyCode, null, null, order.CostCenter,
                order.BranchName, order.SalesRepName, receipt.WarehouseName);
            var returnId = await InsertDocumentAsync(connection, transaction, CommercialDocSide.Purchase,
                returnDraft, CommercialDocumentStatus.Posted, userId, cancellationToken).ConfigureAwait(false);

            var returnLineIds = new Dictionary<int, long>();
            await using (var lineIds = CreateCommand(connection, transaction,
                "SELECT line_id, line_no FROM purchase_document_lines WHERE doc_id = @doc"))
            {
                Add(lineIds, "@doc", returnId);
                await using var reader = await lineIds.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    returnLineIds[Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture)] =
                        Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
            }

            for (var returnLineIndex = 0; returnLineIndex < returnDraftLines.Count; returnLineIndex++)
            {
                var line = returnDraftLines[returnLineIndex];
                var sourceLineNo = returnSourceLineNumbers[returnLineIndex];
                var sourceLine = receiptLines[sourceLineNo];
                // Return-line identity belongs to this new document; source-line
                // identity is stored independently in original_line_id.
                var returnLineNo = returnLineIndex + 1;
                var allocationId = Guid.NewGuid();
                await using var allocation = CreateCommand(connection, transaction, """
                    INSERT INTO purchase_receipt_return_line_allocations
                        (allocation_id, company_id, return_doc_id, return_line_id, original_doc_id,
                         original_line_id, material_id, returned_qty, created_by)
                    VALUES (@id, @company, @returnDoc, @returnLine, @sourceDoc,
                            @sourceLine, @material, @qty, @actor)
                    """);
                Add(allocation, "@id", _provider == "PostgreSQL" ? (object)allocationId : allocationId.ToString("D"));
                Add(allocation, "@company", companyId.ToString("D"));
                Add(allocation, "@returnDoc", returnId);
                Add(allocation, "@returnLine", returnLineIds[returnLineNo]);
                Add(allocation, "@sourceDoc", receiptId);
                Add(allocation, "@sourceLine", sourceLine.LineId);
                Add(allocation, "@material", sourceLine.MaterialId!.Value);
                Add(allocation, "@qty", line.Qty);
                Add(allocation, "@actor", userId.Trim());
                await allocation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var companyCode = await PartyLedger.ResolveCompanyCodeAsync(
                connection, transaction, _provider, companyId.ToString("D"), cancellationToken).ConfigureAwait(false);
            decimal inventoryCostBase = 0m;
            decimal nonStockBase = 0m;
            decimal grniBase = 0m;
            for (var returnLineIndex = 0; returnLineIndex < returnDraftLines.Count; returnLineIndex++)
            {
                var line = returnDraftLines[returnLineIndex];
                var sourceLineNo = returnSourceLineNumbers[returnLineIndex];
                var sourceLine = receiptLines[sourceLineNo];
                var discountedUnitCost = sourceLine.UnitPrice * (1m - sourceLine.DiscountPct / 100m);
                var baseAmount = decimal.Round(line.Qty * discountedUnitCost * receiptFx.Rate, 6, MidpointRounding.AwayFromZero);
                grniBase += baseAmount;
                if (line.MaterialId is not long materialId ||
                    !await IsStockableAsync(connection, transaction, materialId, cancellationToken).ConfigureAwait(false))
                {
                    nonStockBase += baseAmount;
                    continue;
                }

                var warehouseId = line.WarehouseId ?? receipt.WarehouseId
                    ?? await ResolveDefaultWarehouseAsync(connection, transaction, companyCode, cancellationToken).ConfigureAwait(false);
                await EnsureWarehouseIsActiveAsync(connection, transaction, companyCode, warehouseId, cancellationToken).ConfigureAwait(false);
                var equivalent = await ResolveUnitEquivalentAsync(connection, transaction, materialId, line.Unit, cancellationToken).ConfigureAwait(false);
                var movement = StockValuationEngine.Apply(connection, transaction, _provider,
                    new StockMovementRequest(companyCode, warehouseId, materialId,
                        returnDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        CommercialDocKinds.PurchaseReceiptReturn, returnDocNo.Trim(),
                        QtyIn: 0m, QtyOut: line.Qty * equivalent,
                        Note: $"Supplier return against receipt {receipt.DocNo}",
                        BatchAllocations: line.BatchAllocations?.Select(allocation =>
                            new BatchAllocationRequest(
                                allocation.BatchNo,
                                decimal.Round(allocation.Qty * equivalent, 6, MidpointRounding.AwayFromZero),
                                allocation.ExpiryDate, allocation.MfgDate, allocation.SupplierBatchNo)).ToArray(),
                        Serials: line.Serials?.Select(serial =>
                            new SerialSelection(serial.SerialNo, serial.BatchNo, serial.WarrantyExpiry)).ToArray(),
                        AllowExpiredBatchIssue: line.AllowExpiredBatchIssue,
                        OriginalDocumentNo: receipt.DocNo,
                        OriginalSourceDocumentLineId: sourceLine.LineId,
                        SourceDocumentLineId: returnLineIds[returnLineIndex + 1],
                        PartyId: receipt.PartyId));
                inventoryCostBase += -movement.CostAmount;
            }

            var varianceBase = grniBase - inventoryCostBase - nonStockBase;
            if (grniBase > 0m || inventoryCostBase > 0m || nonStockBase > 0m)
            {
                var rate = receiptFx.Rate;
                var glLines = new List<GlLineRequest>
                {
                    new(GlAccounts.GoodsReceivedNotInvoiced, grniBase / rate, 0m, order.CostCenter)
                };
                if (inventoryCostBase > 0m)
                    glLines.Add(new GlLineRequest(GlAccounts.Inventory, 0m, inventoryCostBase / rate, order.CostCenter));
                if (nonStockBase > 0m)
                    glLines.Add(new GlLineRequest(GlAccounts.Purchases, 0m, nonStockBase / rate, order.CostCenter));
                if (varianceBase > 0m)
                    glLines.Add(new GlLineRequest(GlAccounts.PurchaseReturns, 0m, varianceBase / rate, order.CostCenter));
                else if (varianceBase < 0m)
                    glLines.Add(new GlLineRequest(GlAccounts.PurchaseReturns, -varianceBase / rate, 0m, order.CostCenter));
                GlPosting.PostInTransaction(connection, transaction, _provider,
                    new GlDocumentRequest(companyId, returnDate,
                        $"Pre-invoice supplier return {returnDocNo.Trim()} against receipt {receipt.DocNo}",
                        CommercialDocKinds.PurchaseReceiptReturn, returnId.ToString(CultureInfo.InvariantCulture),
                        returnDocNo.Trim(), receipt.CurrencyCode, rate, glLines),
                    userId);
            }

            await using (var link = CreateCommand(connection, transaction, """
                INSERT INTO document_links (company_id, source_doc_kind, source_doc_id, target_doc_kind, target_doc_id, created_by)
                VALUES (@company, @sourceKind, @sourceId, @targetKind, @targetId, @actor)
                """))
            {
                Add(link, "@company", companyId.ToString("D"));
                Add(link, "@sourceKind", CommercialDocKinds.PurchaseReceipt);
                Add(link, "@sourceId", receiptId);
                Add(link, "@targetKind", CommercialDocKinds.PurchaseReceiptReturn);
                Add(link, "@targetId", returnId);
                Add(link, "@actor", userId.Trim());
                await link.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // P5: a receipt return reduces the PO's stored per_received.
            {
                var poIds = new List<long>();
                await using (var poLookup = CreateCommand(connection, transaction, """
                    SELECT source_doc_id FROM document_links
                    WHERE company_id = @company AND source_doc_kind = @poKind
                      AND target_doc_kind = @receiptKind AND target_doc_id = @receipt
                    """))
                {
                    Add(poLookup, "@company", companyId.ToString("D"));
                    Add(poLookup, "@poKind", CommercialDocKinds.PurchaseOrder);
                    Add(poLookup, "@receiptKind", CommercialDocKinds.PurchaseReceipt);
                    Add(poLookup, "@receipt", receiptId);
                    await using var poReader = await poLookup.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    while (await poReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        poIds.Add(Convert.ToInt64(poReader.GetValue(0), CultureInfo.InvariantCulture));
                }
                foreach (var poId in poIds)
                    await RefreshStoredProgressAsync(connection, transaction, poId, cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return returnId;
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
        string? supplierBillNo = null, DateOnly? billDate = null,
        CancellationToken cancellationToken = default)
    {
        if (companyId == Guid.Empty) throw new ArgumentException("Company is required.", nameof(companyId));
        ArgumentNullException.ThrowIfNull(invoiceLines);
        if (invoiceLines.Count == 0) throw new InvalidOperationException("A supplier invoice requires at least one line.");
        return ConvertToInvoiceCoreAsync(CommercialDocSide.Purchase, purchaseOrderId,
            invoiceDocNo, invoiceDate, invoiceLines, companyId, userId, cancellationToken,
            supplierBillNo, billDate);
    }

    private async Task<long> ConvertToInvoiceCoreAsync(
        CommercialDocSide side, long orderDocId, string invoiceDocNo, DateOnly invoiceDate,
        IReadOnlyList<CommercialLineDraft>? invoiceLineOverrides, Guid? expectedCompanyId,
        string userId, CancellationToken cancellationToken,
        string? supplierBillNo = null, DateOnly? billDate = null)
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
            // Both sales and purchase invoice conversions consume order quantities.
            // Serialize on the source order before reading linked billing progress;
            // otherwise two PostgreSQL transactions can both observe the same
            // remaining quantity and create duplicate invoices. SQLite's write
            // transaction serializes writers; PostgreSQL requires SELECT FOR UPDATE.
            // Purchase invoices additionally require an explicit company scope.
            if (side == CommercialDocSide.Purchase &&
                (expectedCompanyId is not Guid purchaseCompanyId || purchaseCompanyId == Guid.Empty))
                throw new InvalidOperationException("Supplier invoice posting requires an explicit company scope.");

            var (orderTable, _) = Tables(side);
            var expectedCompanyValue = expectedCompanyId?.ToString("D");
            await using (var orderLock = CreateCommand(connection, transaction,
                _provider == "PostgreSQL"
                    ? $"""
                      SELECT doc_id
                      FROM {orderTable}
                      WHERE doc_id = @id
                        AND (@company IS NULL OR company_id = @company)
                        AND doc_kind = @kind AND status = @posted
                        AND CAST(is_deleted AS TEXT) IN ('0', 'false')
                      FOR UPDATE
                      """
                    : $"""
                      SELECT doc_id
                      FROM {orderTable}
                      WHERE doc_id = @id
                        AND (@company IS NULL OR company_id = @company)
                        AND doc_kind = @kind AND status = @posted
                        AND CAST(is_deleted AS TEXT) IN ('0', 'false')
                      """))
            {
                Add(orderLock, "@id", orderDocId);
                Add(orderLock, "@company", (object?)expectedCompanyValue ?? DBNull.Value);
                Add(orderLock, "@kind", orderKind);
                Add(orderLock, "@posted", CommercialDocumentStatus.Posted);
                var lockedOrderId = await orderLock.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (lockedOrderId is null or DBNull)
                    throw new InvalidOperationException(side == CommercialDocSide.Purchase
                        ? "Only a posted purchase order in the selected company can be invoiced."
                        : "Only a posted sales order can be invoiced.");
            }

            var order = await ReadSnapshotAsync(connection, transaction, side, orderDocId, null, null, null, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Order was not found.");
            if (order.DocKind != orderKind)
                throw new InvalidOperationException($"Document '{order.DocNo}' is a '{order.DocKind}', not an order ('{orderKind}').");
            if (side == CommercialDocSide.Purchase && expectedCompanyId is null)
                throw new InvalidOperationException("Supplier invoice posting requires an explicit company scope.");
            if (expectedCompanyId is Guid expectedCompany && order.CompanyId != expectedCompany)
                throw new InvalidOperationException("The purchase order belongs to a different company.");
            if (order.Status != CommercialDocumentStatus.Posted)
                throw new InvalidOperationException($"Only posted orders can be converted; order '{order.DocNo}' is '{order.Status}'.");
            if (order.PartyId is null)
                throw new InvalidOperationException($"Order '{order.DocNo}' has no party account id; set the party from its card before converting.");

            // I1: the supplier's own bill number is unique per
            // (company, supplier, normalized reference) on the chain
            // path too — the SAME catalogued error (PUR-1011) the
            // manual path raises. Both company keys are checked because
            // the invoice family writes the company CODE into the same
            // column this repository keys by Guid (mig 084 pattern).
            if (side == CommercialDocSide.Purchase && !string.IsNullOrWhiteSpace(supplierBillNo))
            {
                var companyCodeKey = await ResolveCompanyCodeAsync(connection, transaction, order.CompanyId, cancellationToken).ConfigureAwait(false);
                await using var dup = CreateCommand(connection, transaction, """
                    SELECT doc_no FROM purchase_documents
                    WHERE (company_id = @guid OR company_id = @code)
                      AND doc_kind = @kind AND party_id = @party
                      AND lower(trim(reference_no)) = lower(trim(@ref))
                      AND CAST(is_deleted AS TEXT) IN ('0', 'false')
                    LIMIT 1
                    """);
                Add(dup, "@guid", order.CompanyId.ToString("D"));
                Add(dup, "@code", (object?)companyCodeKey ?? DBNull.Value);
                Add(dup, "@kind", CommercialDocKinds.PurchaseInvoice);
                Add(dup, "@party", order.PartyId.Value);
                Add(dup, "@ref", supplierBillNo.Trim());
                var existingNo = await dup.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (existingNo is not null and not DBNull)
                    throw new AppError(ErrorCodes.SupplierBillReferenceDuplicate,
                        $"Supplier bill '{supplierBillNo.Trim()}' already exists for this supplier.",
                        supplierBillNo.Trim(), Convert.ToString(existingNo, CultureInfo.InvariantCulture) ?? string.Empty);
            }

            var remainingBySourceLine = new Dictionary<int, decimal>();
            if (side == CommercialDocSide.Purchase)
            {
                var billedByLine = await LinkedQuantitiesBySourceLineAsync(
                    connection, transaction, side, order.CompanyId, orderDocId,
                    CommercialDocKinds.PurchaseInvoice, order.Lines, cancellationToken).ConfigureAwait(false);
                var receivedByLine = await LinkedQuantitiesBySourceLineAsync(
                    connection, transaction, side, order.CompanyId, orderDocId,
                    CommercialDocKinds.PurchaseReceipt, order.Lines, cancellationToken).ConfigureAwait(false);
                foreach (var line in order.Lines)
                {
                    var alreadyBilled = billedByLine.GetValueOrDefault(line.LineNo);
                    var alreadyReceived = receivedByLine.GetValueOrDefault(line.LineNo);
                    var remaining = Math.Min(line.Qty - alreadyBilled, alreadyReceived - alreadyBilled);
                    remainingBySourceLine[line.LineNo] = Math.Max(0m, remaining);
                }
            }
            else
            {
                var delivered = await LinkedQuantitiesAsync(
                    connection, transaction, side, order.CompanyId, orderDocId,
                    CommercialDocKinds.DeliveryNote, cancellationToken).ConfigureAwait(false);
                if (delivered.Values.Any(quantity => quantity > 0m))
                    throw new InvalidOperationException(
                        $"Sales order '{order.DocNo}' already has posted delivery notes. Create the invoice from the delivery note so stock is not issued twice.");

                var billedByLine = await LinkedQuantitiesBySourceLineAsync(
                    connection, transaction, side, order.CompanyId, orderDocId, invoiceKind, order.Lines, cancellationToken).ConfigureAwait(false);
                foreach (var line in order.Lines)
                    remainingBySourceLine[line.LineNo] = Math.Max(0m, line.Qty - billedByLine.GetValueOrDefault(line.LineNo));
            }

            var invoiceLines = new List<CommercialLineDraft>();
            if (invoiceLineOverrides is null)
            {
                foreach (var line in order.Lines)
                {
                    var remaining = remainingBySourceLine.GetValueOrDefault(line.LineNo);
                    if (remaining <= 0m)
                        continue;
                    invoiceLines.Add(new CommercialLineDraft(
                        line.MaterialId, line.MaterialCode, line.Description, line.Unit,
                        remaining, line.UnitPrice, line.DiscountPct, line.TaxRate, line.WarehouseId,
                        line.LineNo,
                        UomFactor: line.UomFactor, TaxCode: line.TaxCode));
                }
            }
            else
            {
                var sourceByLineNumber = order.Lines.ToDictionary(line => line.LineNo);
                var sourceByMaterial = order.Lines
                    .Where(line => line.MaterialId is not null)
                    .GroupBy(line => line.MaterialId!.Value)
                    .ToDictionary(group => group.Key, group => group.ToArray());
                foreach (var supplied in invoiceLineOverrides)
                {
                    if (supplied.MaterialId is not long materialId || materialId <= 0)
                        throw new InvalidOperationException("Every supplier invoice line must identify a material from the purchase order.");
                    if (supplied.Qty <= 0m)
                        throw new InvalidOperationException("Supplier invoice quantities must be greater than zero.");
                    if (supplied.UnitPrice < 0m || supplied.DiscountPct < 0m || supplied.DiscountPct > 100m
                        || supplied.TaxRate < 0m || supplied.TaxRate > 100m)
                        throw new InvalidOperationException("Supplier invoice price, discount, or tax rate is outside the allowed range.");

                    CommercialLineSnapshot[] candidateSources;
                    if (supplied.SourceLineNo is int sourceLineNo)
                    {
                        if (!sourceByLineNumber.TryGetValue(sourceLineNo, out var matchedSource)
                            || matchedSource.MaterialId != materialId)
                            throw new InvalidOperationException($"Purchase-order line '{sourceLineNo}' does not belong to material '{materialId}'.");
                        candidateSources = new[] { matchedSource };
                    }
                    else
                    {
                        if (!sourceByMaterial.TryGetValue(materialId, out var matchingSources))
                            throw new InvalidOperationException($"Material '{materialId}' is not part of purchase order '{order.DocNo}'.");
                        // When a purchase order contains the same material on
                        // multiple lines, an invoice line without an explicit
                        // source line is distributed deterministically across
                        // those source lines in line-number order. This preserves
                        // per-line receipt/GRNI matching while keeping ordinary
                        // supplier invoice entry concise.
                        candidateSources = matchingSources.OrderBy(source => source.LineNo).ToArray();
                    }

                    var quantityToAllocate = supplied.Qty;
                    foreach (var source in candidateSources)
                    {
                        if (quantityToAllocate <= 0m)
                            break;
                        var remaining = remainingBySourceLine.GetValueOrDefault(source.LineNo);
                        if (remaining <= 0m)
                            continue;
                        var allocatedQty = Math.Min(quantityToAllocate, remaining);
                        remainingBySourceLine[source.LineNo] = remaining - allocatedQty;
                        invoiceLines.Add(new CommercialLineDraft(
                            source.MaterialId, source.MaterialCode, source.Description, source.Unit,
                            allocatedQty, supplied.UnitPrice, supplied.DiscountPct, supplied.TaxRate,
                            supplied.WarehouseId ?? source.WarehouseId, source.LineNo,
                            UomFactor: source.UomFactor,
                            TaxCode: supplied.TaxCode ?? source.TaxCode));
                        quantityToAllocate -= allocatedQty;
                    }

                    if (quantityToAllocate > 0m)
                    {
                        var sourceLabel = supplied.SourceLineNo?.ToString(CultureInfo.InvariantCulture)
                            ?? materialId.ToString(CultureInfo.InvariantCulture);
                        throw new InvalidOperationException(
                            $"Supplier invoice quantity for source '{sourceLabel}' exceeds its received, unbilled quantity by {quantityToAllocate}.");
                    }
                }
            }
            if (invoiceLines.Count == 0)
                throw new InvalidOperationException(side == CommercialDocSide.Purchase
                    ? $"Purchase order '{order.DocNo}' has no received quantity available to invoice. Post a goods receipt first."
                    : $"Order '{order.DocNo}' is already fully billed.");

            if (side == CommercialDocSide.Purchase && invoiceLines.Any(line => line.TaxRate > 0m)
                && !await IsCompanyTaxEnabledAsync(connection, transaction, order.CompanyId, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("Tax is disabled for this company. Enable the company's tax system before posting a taxable supplier invoice.");

            ComputeTotals(invoiceLines, out var invoiceSubtotal, out var invoiceTax, out _);
            // Header money follows the invoiced lines: the order's header
            // discount / extra charges are carried pro-rata to the invoiced
            // line subtotal, and the stored grand total is recomputed from
            // them inside InsertDocumentAsync (subtotal - discount + tax +
            // extra), so the ledger/GL legs below — which use the
            // recomputed total — stay coherent with the stored document.
            var carryRatio = order.Subtotal > 0m ? invoiceSubtotal / order.Subtotal : 0m;
            var carriedDiscount = decimal.Round(order.DiscountAmount * carryRatio, 2);
            var carriedExtra = decimal.Round(order.ExtraAmount * carryRatio, 2);
            var grandTotal = decimal.Round(invoiceSubtotal - carriedDiscount + invoiceTax + carriedExtra, 2);

            var invoiceDraft = new CommercialDocumentDraft(
                order.CompanyId, invoiceKind, invoiceDocNo.Trim(), invoiceDate,
                order.PartyId, order.PartyName, order.WarehouseId,
                order.CurrencyCode, order.ExchangeRate,
                $"From {orderKind} {order.DocNo}",
                invoiceLines,
                order.PartyCode, null, null, order.CostCenter, order.BranchName, order.SalesRepName, order.WarehouseName,
                carriedDiscount, carriedExtra,
                ReferenceNo: side == CommercialDocSide.Purchase && !string.IsNullOrWhiteSpace(supplierBillNo)
                    ? supplierBillNo.Trim() : null,
                BillDate: side == CommercialDocSide.Purchase ? billDate : null);

            var invoiceId = await InsertDocumentAsync(connection, transaction, side, invoiceDraft, CommercialDocumentStatus.Posted, userId, cancellationToken).ConfigureAwait(false);

            // Withholding tax (W1, migration 114): same shared evaluator
            // as the manual path, on the VAT-excluding base (the lines
            // subtotal). Determination here is supplier default ->
            // company default withholding rule (chain invoices carry no
            // invoice-level selection; the PO's supplier governs).
            var withholding = WithholdingTax.None;
            if (side == CommercialDocSide.Purchase)
            {
                var companyCodeForWithholding = await PartyLedger.ResolveCompanyCodeAsync(
                    connection, transaction, _provider, order.CompanyId.ToString("D"), cancellationToken).ConfigureAwait(false);
                withholding = WithholdingTax.Evaluate(
                    connection, transaction, _provider, companyCodeForWithholding, order.CompanyId,
                    order.PartyId, invoiceDate, invoiceSubtotal, explicitCode: null, excludeDocId: invoiceId);
                if (withholding.Amount > grandTotal)
                    throw new AppError(ErrorCodes.WithholdingExceedsPayable,
                        $"Withholding {withholding.Amount} on invoice '{invoiceDocNo.Trim()}' exceeds the amount payable to the supplier.", invoiceDocNo.Trim());
            }

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
            // I2: the chain tax register is written BEFORE the GL block
            // because the manual path's recoverability split feeds the
            // GL: recoverable input tax debits InputVat; non-recoverable
            // tax is capitalized into the purchase cost below.
            decimal chainRecoverableTax = invoiceTax, chainNonRecoverableTax = 0m;
            if (side == CommercialDocSide.Purchase)
            {
                var chainTax = await WriteChainTaxTransactionsAsync(connection, transaction, order, invoiceId,
                    invoiceDocNo.Trim(), invoiceDate, invoiceLines, fx.Rate, userId, cancellationToken).ConfigureAwait(false);
                chainRecoverableTax = chainTax.Recoverable;
                chainNonRecoverableTax = chainTax.NonRecoverable;
            }
            var stockCompanyCode = await PartyLedger.ResolveCompanyCodeAsync(
                connection, transaction, _provider, order.CompanyId.ToString("D"), cancellationToken).ConfigureAwait(false);
            if (side == CommercialDocSide.Purchase)
            {
                await PersistPurchaseMatchEvaluationAsync(
                    connection, transaction, order, invoiceId, invoiceLines, stockCompanyCode,
                    invoiceSubtotal, invoiceTax, grniBaseAmount, fx.Rate, userId, cancellationToken).ConfigureAwait(false);
            }
            var cogsAmount = 0m;
            if (side == CommercialDocSide.Sales)
            {
                var stockResult = await PostConversionStockAsync(
                    connection, transaction, side, order, invoiceLines,
                    invoiceDocNo.Trim(), invoiceDate, cancellationToken, actor: userId).ConfigureAwait(false);
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
                // Withholding (W1): the supplier's ledger balance is NET
                // (gross − withheld), exactly like the manual path.
                side == CommercialDocSide.Purchase ? grandTotal - withholding.Amount : 0m,
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
                    // I2: non-recoverable input tax is capitalized into
                    // the purchase cost (manual-path semantics), so the
                    // cost side carries subtotal + non-recoverable tax
                    // and only the recoverable part debits InputVat.
                    var invoiceCostAmount = splitTax ? invoiceSubtotal + chainNonRecoverableTax : grandTotal;
                    var purchaseVariance = invoiceCostAmount - grniDocumentAmount;
                    glLines.Add(new GlLineRequest(GlAccounts.GoodsReceivedNotInvoiced, grniDocumentAmount, 0m, order.CostCenter));
                    if (purchaseVariance > 0m)
                        glLines.Add(new GlLineRequest(GlAccounts.PurchasePriceVariance, purchaseVariance, 0m, order.CostCenter));
                    else if (purchaseVariance < 0m)
                        glLines.Add(new GlLineRequest(GlAccounts.PurchasePriceVariance, 0m, -purchaseVariance, order.CostCenter));
                    if (splitTax && chainRecoverableTax > 0m) glLines.Add(new GlLineRequest(GlAccounts.InputVat, chainRecoverableTax, 0m));
                    glLines.Add(new GlLineRequest(GlAccounts.Payable, 0m, grandTotal));
                    // Withholding (W1): AP credited GROSS above; the
                    // deduction leg moves the withheld part to the
                    // withholding payable account (net AP balance).
                    if (withholding is { Amount: > 0m } wht && wht.AccountCode is not null)
                    {
                        glLines.Add(new GlLineRequest(GlAccounts.Payable, wht.Amount, 0m));
                        glLines.Add(new GlLineRequest(wht.AccountCode, 0m, wht.Amount));
                    }
                }
                GlPosting.PostInTransaction(connection, transaction, _provider,
                    new GlDocumentRequest(order.CompanyId, invoiceDate,
                        $"{invoiceKind} {invoiceDocNo.Trim()} from order {order.DocNo}",
                        invoiceKind, invoiceId.ToString(CultureInfo.InvariantCulture), invoiceDocNo.Trim(),
                        order.CurrencyCode, fx.Rate, glLines),
                    userId);
            }

            if (side == CommercialDocSide.Sales)
            {
                // The converted invoice is posted and linked above, so the
                // order's stored per_billed refreshes in this transaction.
                await RecalculateStoredProgressInTransactionAsync(
                    connection, transaction, orderDocId, cancellationToken).ConfigureAwait(false);
            }
            if (side == CommercialDocSide.Purchase && withholding.Evaluated)
            {
                // Withholding (W1): persist the outcome on the invoice
                // header and register it in tax_transactions, in this
                // same transaction (identical to the manual path).
                WithholdingTax.PersistOutcome(connection, transaction, _provider, invoiceId, withholding);
                WithholdingTax.WriteRegisterRow(
                    connection, transaction, _provider, stockCompanyCode, withholding.CountryCode,
                    CommercialDocKinds.PurchaseInvoice, invoiceId, invoiceDocNo.Trim(), withholding,
                    order.CurrencyCode, fx.Rate, invoiceDocNo.Trim(), userId, invoiceDate);
            }

            if (side == CommercialDocSide.Purchase)
            {
                // P5: maintain the PO's stored per_billed in this txn.
                await RefreshStoredProgressAsync(connection, transaction, orderDocId, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// I2: writes the immutable tax_transactions INPUT rows for a chain
    /// purchase invoice, mirroring InvRepo.WriteTaxTransactions: the
    /// line's tax code is validated against the company's active tax
    /// rules at the document date (starter rules must be reviewed, the
    /// approved rate must match), recoverability is snapshotted, and
    /// the registered tax must reconcile with the document tax total.
    /// A taxed line without a code inherits the code of the active rule
    /// whose rate matches (the sane default from the company's tax
    /// setup); a taxed line with no resolvable rule fails exactly like
    /// the manual path. Returns the recoverable / non-recoverable split
    /// the GL block capitalizes on.
    /// </summary>
    private async Task<(decimal Recoverable, decimal NonRecoverable)> WriteChainTaxTransactionsAsync(
        DbConnection connection, DbTransaction transaction, CommercialDocumentSnapshot order,
        long invoiceId, string invoiceDocNo, DateOnly invoiceDate,
        IReadOnlyList<CommercialLineDraft> invoiceLines, decimal exchangeRate,
        string userId, CancellationToken cancellationToken)
    {
        var totals = ComputeTotals(invoiceLines, out _, out _, out _);
        var companyCode = await PartyLedger.ResolveCompanyCodeAsync(
            connection, transaction, _provider, order.CompanyId.ToString("D"), cancellationToken).ConfigureAwait(false);
        var isPg = _provider == "PostgreSQL";
        var enabledValue = isPg ? "TRUE" : "1";
        string countryCode = string.Empty;
        if (PartyLedger.TableExists(connection, transaction, _provider, "company_tax_settings"))
        {
            await using var settings = CreateCommand(connection, transaction,
                $"SELECT country_code FROM company_tax_settings WHERE company_code = @company AND tax_enabled = {enabledValue} LIMIT 1");
            Add(settings, "@company", companyCode);
            var value = await settings.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            countryCode = value is null or DBNull ? string.Empty : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }
        if (string.IsNullOrWhiteSpace(countryCode))
        {
            if (invoiceLines.Any(line => line.TaxRate != 0m))
                throw new InvalidOperationException(
                    "Tax is present on this invoice, but the company's tax system is not enabled. Enable the correct tax system or remove tax before posting.");
            return (0m, 0m);
        }
        if (!PartyLedger.TableExists(connection, transaction, _provider, "tax_transactions"))
            throw new InvalidOperationException("Tax register schema is missing. Apply database migration 069 before posting a taxable chain invoice.");

        decimal recoverableTotal = 0m, nonRecoverableTotal = 0m, registeredTax = 0m;
        for (var index = 0; index < invoiceLines.Count; index++)
        {
            var line = invoiceLines[index];
            var lineTax = totals[index].Tax;
            var lineSubtotal = totals[index].Subtotal;
            var taxCode = line.TaxCode;
            if (string.IsNullOrWhiteSpace(taxCode) && line.TaxRate != 0m)
            {
                await using var byRate = CreateCommand(connection, transaction, """
                    SELECT tax_code FROM company_tax_rules
                    WHERE company_code = @company AND country_code = @country AND is_active = @enabled
                      AND rate = @rate AND effective_from <= @date
                      AND (effective_to IS NULL OR effective_to >= @date)
                    ORDER BY effective_from DESC LIMIT 1
                    """);
                Add(byRate, "@company", companyCode);
                Add(byRate, "@country", countryCode);
                Add(byRate, "@enabled", isPg ? (object)true : 1);
                Add(byRate, "@rate", line.TaxRate);
                Add(byRate, "@date", invoiceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                var found = await byRate.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                taxCode = found is null or DBNull ? null : Convert.ToString(found, CultureInfo.InvariantCulture);
            }
            if (string.IsNullOrWhiteSpace(taxCode))
            {
                if (line.TaxRate != 0m || lineTax != 0m)
                    throw new InvalidOperationException(
                        $"Invoice line {index + 1} has a tax rate but no tax code. Select a valid tax system code before posting.");
                continue;
            }

            string taxType, treatment, accountCode, dataStatus;
            bool recoverable;
            decimal approvedRate;
            await using (var rule = CreateCommand(connection, transaction, """
                SELECT tax_type, tax_treatment, rate, is_recoverable,
                       COALESCE(input_account_code, ''), COALESCE(data_status, 'USER_CONFIGURED')
                FROM company_tax_rules
                WHERE company_code = @company AND country_code = @country AND tax_code = @code
                  AND is_active = @enabled AND effective_from <= @date
                  AND (effective_to IS NULL OR effective_to >= @date)
                ORDER BY effective_from DESC LIMIT 1
                """))
            {
                Add(rule, "@company", companyCode);
                Add(rule, "@country", countryCode);
                Add(rule, "@code", taxCode.Trim());
                Add(rule, "@enabled", isPg ? (object)true : 1);
                Add(rule, "@date", invoiceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                await using var reader = await rule.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException(
                        $"Tax code '{taxCode}' on invoice line {index + 1} has no active rule for {countryCode} on {invoiceDate:yyyy-MM-dd}.");
                taxType = Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture) ?? string.Empty;
                treatment = Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) ?? "Standard";
                approvedRate = Convert.ToDecimal(reader.GetValue(2), CultureInfo.InvariantCulture);
                recoverable = Convert.ToBoolean(reader.GetValue(3), CultureInfo.InvariantCulture);
                accountCode = Convert.ToString(reader.GetValue(4), CultureInfo.InvariantCulture) ?? string.Empty;
                dataStatus = Convert.ToString(reader.GetValue(5), CultureInfo.InvariantCulture) ?? "USER_CONFIGURED";
            }
            if (string.Equals(dataStatus, "BASELINE_REVIEW_REQUIRED", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Tax rule '{taxCode}' is a starter rule and has not been reviewed. Open Tax Configuration, review the effective rate and account mapping, then save the rule before posting.");
            if (string.IsNullOrWhiteSpace(accountCode))
                accountCode = GlAccounts.InputVat;
            if (Math.Abs(line.TaxRate - approvedRate) > 0.000001m)
                throw new InvalidOperationException(
                    $"Tax rate on invoice line {index + 1} does not match the approved effective rate for '{taxCode}'. Reload the tax code and recalculate the invoice.");

            registeredTax += lineTax;
            if (recoverable) recoverableTotal += lineTax; else nonRecoverableTotal += lineTax;

            await using var insert = CreateCommand(connection, transaction, """
                INSERT INTO tax_transactions
                    (company_code, country_code, source_doc_kind, source_doc_id, source_doc_no,
                     line_no, tax_code, tax_type, tax_treatment, tax_rate, taxable_amount,
                     tax_amount, is_recoverable, tax_direction, tax_account_code, currency_code,
                     exchange_rate, base_tax_amount, journal_doc_no, created_by, document_date)
                VALUES
                    (@company, @country, @kind, @docId, @docNo,
                     @lineNo, @taxCode, @taxType, @treatment, @rate, @taxable,
                     @tax, @recoverable, 'INPUT', @account, @currency,
                     @exchangeRate, @baseTax, @journal, @user, @docDate)
                ON CONFLICT (company_code, source_doc_kind, source_doc_id, line_no, tax_code) DO NOTHING
                """);
            Add(insert, "@company", companyCode);
            Add(insert, "@country", countryCode);
            Add(insert, "@kind", CommercialDocKinds.PurchaseInvoice);
            Add(insert, "@docId", invoiceId);
            Add(insert, "@docNo", invoiceDocNo);
            Add(insert, "@lineNo", line.SourceLineNo ?? index + 1);
            Add(insert, "@taxCode", taxCode.Trim());
            Add(insert, "@taxType", taxType);
            Add(insert, "@treatment", treatment);
            Add(insert, "@rate", approvedRate);
            Add(insert, "@taxable", lineSubtotal);
            Add(insert, "@tax", lineTax);
            Add(insert, "@recoverable", isPg ? (object)recoverable : (recoverable ? 1 : 0));
            Add(insert, "@account", accountCode);
            Add(insert, "@currency", order.CurrencyCode);
            Add(insert, "@exchangeRate", exchangeRate);
            Add(insert, "@baseTax", decimal.Round(lineTax * exchangeRate, 6, MidpointRounding.AwayFromZero));
            Add(insert, "@journal", invoiceDocNo);
            Add(insert, "@user", userId.Trim());
            Add(insert, "@docDate", invoiceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var documentTax = totals.Sum(t => t.Tax);
        if (Math.Abs(registeredTax - documentTax) > 0.0001m)
            throw new InvalidOperationException(
                "The invoice tax lines do not reconcile with the stored document tax total. Recalculate the invoice before posting.");
        return (recoverableTotal, nonRecoverableTotal);
    }

    /// <summary>
    /// P6 (ERPNext amend): a cancelled purchase order is amended by
    /// creating a NEW draft PO that copies header + lines, linked to
    /// the cancelled original via amended_from_doc_id and a
    /// document_links row. The original stays cancelled forever.
    /// </summary>
    public async Task<long> AmendPurchaseOrderAsync(
        Guid companyId, long cancelledPoId, string newDocNo, DateOnly docDate, string userId,
        CancellationToken cancellationToken = default)
    {
        if (companyId == Guid.Empty) throw new ArgumentException("Company is required.", nameof(companyId));
        if (string.IsNullOrWhiteSpace(newDocNo)) throw new ArgumentException("Document number is required.", nameof(newDocNo));
        if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("User is required.", nameof(userId));
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var original = await ReadSnapshotAsync(connection, transaction, CommercialDocSide.Purchase,
                cancelledPoId, companyId, CommercialDocKinds.PurchaseOrder, null, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Purchase order was not found.");
            if (original.Status != CommercialDocumentStatus.Cancelled)
                throw new InvalidOperationException("Only a cancelled purchase order can be amended.");
            var lines = original.Lines.Select(line => new CommercialLineDraft(
                line.MaterialId, line.MaterialCode, line.Description, line.Unit, line.Qty,
                line.UnitPrice, line.DiscountPct, line.TaxRate, line.WarehouseId, line.LineNo,
                UomFactor: line.UomFactor,
                LineRequiredBy: line.LineRequiredBy ?? original.RequiredByDate,
                TaxCode: line.TaxCode)).ToArray();
            var draft = new CommercialDocumentDraft(
                original.CompanyId, CommercialDocKinds.PurchaseOrder, newDocNo.Trim(), docDate,
                original.PartyId, original.PartyName, original.WarehouseId,
                original.CurrencyCode, original.ExchangeRate,
                $"Amended from {original.DocNo}",
                lines, original.PartyCode, original.DeliveryDate, original.ValidTill,
                original.CostCenter, original.BranchName, original.SalesRepName, original.WarehouseName,
                RequiredByDate: original.RequiredByDate,
                PaymentTerms: original.PaymentTerms, Incoterm: original.Incoterm,
                AmendedFromDocId: original.DocId);
            var newId = await InsertDocumentAsync(connection, transaction, CommercialDocSide.Purchase,
                draft, CommercialDocumentStatus.Draft, userId, cancellationToken).ConfigureAwait(false);
            await using (var link = CreateCommand(connection, transaction, """
                INSERT INTO document_links (company_id, source_doc_kind, source_doc_id, target_doc_kind, target_doc_id, created_by)
                VALUES (@company, @kind, @sourceId, @kind, @targetId, @actor)
                """))
            {
                Add(link, "@company", original.CompanyId.ToString("D"));
                Add(link, "@kind", CommercialDocKinds.PurchaseOrder);
                Add(link, "@sourceId", original.DocId);
                Add(link, "@targetId", newId);
                Add(link, "@actor", userId.Trim());
                await link.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return newId;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    // ------------------------------------------------------- P1 invitations

    public async Task<IReadOnlyList<RfqInvitation>> GetRfqInvitationsAsync(
        Guid companyId, long rfqDocId, CancellationToken cancellationToken = default)
    {
        if (companyId == Guid.Empty) throw new ArgumentException("Company is required.", nameof(companyId));
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<RfqInvitation>();
        if (!PartyLedger.TableExists(connection, null, _provider, "rfq_invitations"))
            return results.AsReadOnly();
        await using var command = CreateCommand(connection, null, """
            SELECT invitation_id, rfq_doc_id, party_id, party_code, party_name, status, invited_at, responded_at
            FROM rfq_invitations WHERE company_id = @company AND rfq_doc_id = @rfq
            ORDER BY party_name, party_id
            """);
        Add(command, "@company", companyId.ToString("D"));
        Add(command, "@rfq", rfqDocId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new RfqInvitation(
                Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5),
                DateTime.Parse(Convert.ToString(reader.GetValue(6), CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture),
                reader.IsDBNull(7) ? null : DateTime.Parse(Convert.ToString(reader.GetValue(7), CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture)));
        }
        return results.AsReadOnly();
    }

    public async Task AddRfqInvitationsAsync(
        Guid companyId, long rfqDocId,
        IReadOnlyList<(long PartyId, string? PartyCode, string? PartyName)> suppliers,
        string userId, CancellationToken cancellationToken = default)
    {
        if (companyId == Guid.Empty) throw new ArgumentException("Company is required.", nameof(companyId));
        ArgumentNullException.ThrowIfNull(suppliers);
        if (suppliers.Count == 0) throw new InvalidOperationException("At least one supplier must be invited.");
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var rfq = await ReadSnapshotAsync(connection, transaction, CommercialDocSide.Purchase,
                rfqDocId, companyId, CommercialDocKinds.RequestForQuotation, null, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Request for quotation was not found.");
            if (rfq.Status == CommercialDocumentStatus.Cancelled)
                throw new InvalidOperationException("Suppliers cannot be invited to a cancelled RFQ.");
            foreach (var (partyId, partyCode, partyName) in suppliers)
            {
                if (partyId <= 0) throw new InvalidOperationException("Every invitation must identify a supplier.");
                await using var insert = CreateCommand(connection, transaction, """
                    INSERT INTO rfq_invitations (company_id, rfq_doc_id, party_id, party_code, party_name, status, created_by)
                    VALUES (@company, @rfq, @party, @code, @name, 'Invited', @actor)
                    ON CONFLICT (company_id, rfq_doc_id, party_id) DO NOTHING
                    """);
                Add(insert, "@company", companyId.ToString("D"));
                Add(insert, "@rfq", rfqDocId);
                Add(insert, "@party", partyId);
                Add(insert, "@code", (object?)partyCode ?? DBNull.Value);
                Add(insert, "@name", (object?)partyName ?? DBNull.Value);
                Add(insert, "@actor", userId.Trim());
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task SetRfqInvitationStatusAsync(
        Guid companyId, long rfqDocId, long partyId, string status, string userId,
        CancellationToken cancellationToken = default)
    {
        if (status is not ("Invited" or "Quoted" or "Declined"))
            throw new ArgumentException($"Unknown invitation status '{status}'.", nameof(status));
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var update = CreateCommand(connection, transaction, """
                UPDATE rfq_invitations
                SET status = @status,
                    responded_at = CASE WHEN @status = 'Invited' THEN responded_at ELSE CURRENT_TIMESTAMP END
                WHERE company_id = @company AND rfq_doc_id = @rfq AND party_id = @party
                """);
            Add(update, "@status", status);
            Add(update, "@company", companyId.ToString("D"));
            Add(update, "@rfq", rfqDocId);
            Add(update, "@party", partyId);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
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
            SELECT l.target_doc_id
            FROM document_links l
            INNER JOIN purchase_documents d
                ON d.doc_id = l.target_doc_id AND CAST(d.company_id AS TEXT) = l.company_id
            WHERE l.company_id = @company AND l.source_doc_kind = @sourceKind
              AND l.source_doc_id = @sourceId AND l.target_doc_kind = @receiptKind
            ORDER BY d.doc_date, d.doc_id
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
            SELECT m.receipt_line_id, COALESCE(SUM(m.matched_qty), 0)
            FROM purchase_receipt_invoice_matches m
            INNER JOIN purchase_documents inv
              ON inv.company_id = m.company_id
             AND inv.doc_id = m.invoice_doc_id
             AND inv.doc_kind = @invoiceKind
             AND inv.status = @posted
             AND CAST(inv.is_deleted AS TEXT) IN ('0', 'false')
            WHERE m.company_id = @company AND m.purchase_order_id = @order
            GROUP BY m.receipt_line_id
            """))
        {
            Add(prior, "@invoiceKind", CommercialDocKinds.PurchaseInvoice);
            Add(prior, "@posted", CommercialDocumentStatus.Posted);
            Add(prior, "@company", order.CompanyId.ToString("D"));
            Add(prior, "@order", purchaseOrderId);
            await using var reader = await prior.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                matchedByReceiptLine[Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture)] =
                    Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture);
        }

        var returnedByReceiptLine = new Dictionary<long, decimal>();
        foreach (var receiptId in receiptIds)
        {
            await using var returned = CreateCommand(connection, transaction, """
                SELECT a.original_line_id, COALESCE(SUM(a.returned_qty), 0)
                FROM purchase_receipt_return_line_allocations a
                JOIN purchase_documents d
                  ON d.doc_id = a.return_doc_id
                 AND d.company_id = a.company_id
                 AND d.doc_kind = @returnKind
                 AND d.status = @posted
                 AND d.is_deleted = 0
                WHERE a.company_id = @company AND a.original_doc_id = @receipt
                GROUP BY a.original_line_id
                """);
            Add(returned, "@returnKind", CommercialDocKinds.PurchaseReceiptReturn);
            Add(returned, "@posted", CommercialDocumentStatus.Posted);
            Add(returned, "@company", order.CompanyId.ToString("D"));
            Add(returned, "@receipt", receiptId);
            await using var reader = await returned.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                returnedByReceiptLine[Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture)] =
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
                foreach (var receiptLine in receipt.Lines.Where(line => line.MaterialId == invoiceLine.MaterialId && line.LineNo == invoiceLine.LineNo))
                {
                    var alreadyMatched = matchedByReceiptLine.GetValueOrDefault(receiptLine.LineId);
                    var receiptRemaining = receiptLine.Qty - alreadyMatched - returnedByReceiptLine.GetValueOrDefault(receiptLine.LineId);
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
        string? purchaseMovementKind = null, string? actor = null, long? sourceDocumentId = null)
    {
        var companyCode = await PartyLedger.ResolveCompanyCodeAsync(
            connection, transaction, _provider, order.CompanyId.ToString("D"), cancellationToken).ConfigureAwait(false);
        var cogsAmount = 0m;
        var movementDate = invoiceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        IReadOnlyList<long>? sourceLineIds = null;
        if (sourceDocumentId is long sourceId)
        {
            var lineTable = side == CommercialDocSide.Sales ? "sales_document_lines" : "purchase_document_lines";
            var ids = new List<long>();
            await using var lineCommand = CreateCommand(connection, transaction,
                $"SELECT line_id FROM {lineTable} WHERE doc_id = @doc ORDER BY line_no");
            Add(lineCommand, "@doc", sourceId);
            await using var lineReader = await lineCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await lineReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                ids.Add(Convert.ToInt64(lineReader.GetValue(0), CultureInfo.InvariantCulture));
            sourceLineIds = ids;
            if (sourceLineIds.Count != invoiceLines.Count)
                throw new InvalidOperationException("Stock movement source lines do not match the posted commercial document lines.");
        }
        for (var lineIndex = 0; lineIndex < invoiceLines.Count; lineIndex++)
        {
            var line = invoiceLines[lineIndex];
            var sourceLineId = sourceLineIds is null ? (long?)null : sourceLineIds[lineIndex];
            if (line.MaterialId is not long materialId)
                continue; // no stock identity — same rule as chain delivery notes
            if (!await IsStockableAsync(connection, transaction, materialId, cancellationToken).ConfigureAwait(false))
                continue;
            var warehouseId = line.WarehouseId ?? order.WarehouseId
                ?? await ResolveDefaultWarehouseAsync(connection, transaction, companyCode, cancellationToken).ConfigureAwait(false);
            await EnsureWarehouseIsActiveAsync(connection, transaction, companyCode, warehouseId, cancellationToken).ConfigureAwait(false);
            // P4: the STORED line factor (snapshot from the material
            // card at entry) wins; only legacy lines without one
            // re-derive from the card.
            var equivalent = line.UomFactor is decimal storedFactor && storedFactor > 0m
                ? storedFactor
                : await ResolveUnitEquivalentAsync(connection, transaction, materialId, line.Unit, cancellationToken).ConfigureAwait(false);
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
                var batchAllocations = line.BatchAllocations?.Select(allocation =>
                    new BatchAllocationRequest(
                        allocation.BatchNo,
                        decimal.Round(allocation.Qty * equivalent, 6, MidpointRounding.AwayFromZero),
                        allocation.ExpiryDate,
                        allocation.MfgDate,
                        allocation.SupplierBatchNo)).ToArray();
                var serials = line.Serials?.Select(serial =>
                    new SerialSelection(serial.SerialNo, serial.BatchNo, serial.WarrantyExpiry)).ToArray();

                // Q1 (blocker): the migration-056 quality gate, exactly
                // as StkPost applies it — a QC-required RECEIPT line
                // lands in the quarantine warehouse (never directly in
                // sellable stock) and a Draft inspection is created in
                // this same transaction. QualityGate.CompleteInTransaction
                // (the existing QC release path) later moves the accepted
                // qty quarantine -> target at the same value; rejected
                // qty stays quarantined, returnable via RCR. The receipt
                // GL was already split on stockability only, so the
                // quarantine movement at receipt value keeps GL/stock
                // consistent. Service / non-stock lines never reach
                // here (IsStockableAsync guard above).
                var effectiveWarehouseId = warehouseId;
                var diverted = false;
                if (purchaseMovementKind == CommercialDocKinds.PurchaseReceipt)
                {
                    var qcProfile = QualityGate.LoadProfile(connection, transaction, _provider, companyCode, materialId);
                    if (qcProfile.QualityCheckRequired)
                    {
                        var quarantineId = QualityGate.EnsureQuarantineWarehouse(connection, transaction, _provider, companyCode);
                        if (quarantineId > 0)
                        {
                            effectiveWarehouseId = quarantineId;
                            diverted = true;
                        }
                    }
                }

                var movement = StockValuationEngine.Apply(connection, transaction, _provider,
                    new StockMovementRequest(companyCode, effectiveWarehouseId, materialId,
                        movementDate, purchaseMovementKind ?? CommercialDocKinds.PurchaseInvoice, invoiceDocNo,
                        QtyIn: baseQty, QtyOut: 0m,
                        ReceiptUnitCost: unitCost * FxRates.Resolve(connection, transaction, _provider,
                            order.CompanyId, order.CurrencyCode, invoiceDate, order.ExchangeRate).Rate,
                        Note: line.Description,
                        BatchAllocations: batchAllocations,
                        Serials: serials,
                        AllowExpiredBatchIssue: line.AllowExpiredBatchIssue,
                        PartyId: order.PartyId,
                        Actor: actor,
                        SourceDocumentLineId: sourceLineId));

                if (diverted)
                {
                    var batchId = movement.AllocationRecords
                        .FirstOrDefault(a => a.BatchId is not null)?.BatchId;
                    QualityGate.EnsureInspection(connection, transaction, _provider, companyCode,
                        QualityInspectionTypes.Incoming, purchaseMovementKind!,
                        sourceDocumentId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, invoiceDocNo,
                        materialId, batchId, warehouseId, effectiveWarehouseId,
                        order.PartyId, baseQty, movementDate, actor ?? string.Empty);
                }
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
            "SELECT company_code FROM companies WHERE CAST(company_id AS TEXT) = @id");
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
        if (draft.DiscountAmount < 0m) throw new InvalidOperationException("Header discount cannot be negative.");
        if (draft.ExtraAmount < 0m) throw new InvalidOperationException("Header extra charges cannot be negative.");
        if (draft.DeliveryCost < 0m) throw new InvalidOperationException("Delivery cost cannot be negative.");
    }

    private async Task PersistPurchaseMatchEvaluationAsync(
        DbConnection connection,
        DbTransaction transaction,
        CommercialDocumentSnapshot order,
        long invoiceDocId,
        IReadOnlyList<CommercialLineDraft> invoiceLines,
        string companyCode,
        decimal invoiceSubtotal,
        decimal invoiceTax,
        decimal grniBaseAmount,
        decimal exchangeRate,
        string userId,
        CancellationToken cancellationToken)
    {
        var policyVersion = 1;
        var policyEnabled = false;
        var requireReceiptMatch = true;
        var priceWarning = 0m;
        var priceBlock = 0m;
        var taxWarning = 0m;
        var taxBlock = 0m;
        var baseWarning = 0m;
        var baseBlock = 0m;

        var policySql = """
            SELECT is_enabled, require_receipt_match, price_warning_percent, price_block_percent,
                   tax_warning_percent, tax_block_percent, base_variance_warning_amount,
                   base_variance_block_amount, policy_version
            FROM purchase_match_policies
            WHERE company_code = @company
            """ + (_provider == "PostgreSQL" ? " FOR SHARE" : string.Empty);
        await using (var policyCommand = CreateCommand(connection, transaction, policySql))
        {
            Add(policyCommand, "@company", companyCode);
            await using var reader = await policyCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                policyEnabled = Convert.ToBoolean(reader.GetValue(0), CultureInfo.InvariantCulture);
                requireReceiptMatch = Convert.ToBoolean(reader.GetValue(1), CultureInfo.InvariantCulture);
                priceWarning = Convert.ToDecimal(reader.GetValue(2), CultureInfo.InvariantCulture);
                priceBlock = Convert.ToDecimal(reader.GetValue(3), CultureInfo.InvariantCulture);
                taxWarning = Convert.ToDecimal(reader.GetValue(4), CultureInfo.InvariantCulture);
                taxBlock = Convert.ToDecimal(reader.GetValue(5), CultureInfo.InvariantCulture);
                baseWarning = Convert.ToDecimal(reader.GetValue(6), CultureInfo.InvariantCulture);
                baseBlock = Convert.ToDecimal(reader.GetValue(7), CultureInfo.InvariantCulture);
                policyVersion = Convert.ToInt32(reader.GetValue(8), CultureInfo.InvariantCulture);
            }
        }

        var policy = new PurchaseMatchPolicy(
            policyEnabled, requireReceiptMatch, priceWarning, priceBlock,
            taxWarning, taxBlock, baseWarning, baseBlock, policyVersion);

        decimal expectedTax = 0m;
        decimal actualTax = 0m;
        var sourceLines = order.Lines.ToDictionary(line => line.LineNo);
        foreach (var invoiceLine in invoiceLines)
        {
            if (invoiceLine.SourceLineNo is not int sourceLineNo ||
                !sourceLines.TryGetValue(sourceLineNo, out var sourceLine))
                throw new InvalidOperationException("A supplier invoice line is missing its purchase-order source line for match evaluation.");

            var net = decimal.Round(
                invoiceLine.Qty * invoiceLine.UnitPrice * (1m - invoiceLine.DiscountPct / 100m),
                2, MidpointRounding.AwayFromZero);
            expectedTax += decimal.Round(net * sourceLine.TaxRate / 100m, 2, MidpointRounding.AwayFromZero);
            actualTax += decimal.Round(net * invoiceLine.TaxRate / 100m, 2, MidpointRounding.AwayFromZero);
        }

        var taxVariancePercent = expectedTax == 0m
            ? (actualTax == 0m ? 0m : decimal.MaxValue)
            : decimal.Round(Math.Abs(actualTax - expectedTax) / Math.Abs(expectedTax) * 100m, 6, MidpointRounding.AwayFromZero);
        var invoiceBaseAmount = decimal.Round(invoiceSubtotal * exchangeRate, 6, MidpointRounding.AwayFromZero);
        var priceVariancePercent = grniBaseAmount == 0m
            ? (invoiceBaseAmount == 0m ? 0m : decimal.MaxValue)
            : decimal.Round(Math.Abs(invoiceBaseAmount - grniBaseAmount) / Math.Abs(grniBaseAmount) * 100m, 6, MidpointRounding.AwayFromZero);
        var absoluteBaseVariance = Math.Abs(invoiceBaseAmount - grniBaseAmount);

        var evaluation = PurchaseMatchPolicyEvaluator.Evaluate(
            policy,
            new PurchaseMatchVariance(
                priceVariancePercent,
                taxVariancePercent,
                absoluteBaseVariance,
                HasReceiptMatch: true));

        var nextVersion = 1;
        await using (var versionCommand = CreateCommand(connection, transaction, """
            SELECT COALESCE(MAX(evaluation_version), 0) + 1
            FROM purchase_match_evaluations
            WHERE company_code = @company AND invoice_doc_id = @invoice
            """))
        {
            Add(versionCommand, "@company", companyCode);
            Add(versionCommand, "@invoice", invoiceDocId);
            nextVersion = Convert.ToInt32(
                await versionCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
        }

        var evaluationId = Guid.NewGuid();
        var policySnapshot = JsonSerializer.Serialize(new
        {
            policyEnabled, requireReceiptMatch, priceWarning, priceBlock,
            taxWarning, taxBlock, baseWarning, baseBlock, policyVersion
        });
        var matchDetails = new List<object>();
        await using (var matchCommand = CreateCommand(connection, transaction, """
            SELECT match_id, purchase_order_id, receipt_doc_id, receipt_line_id,
                   invoice_line_id, material_id, matched_qty, receipt_unit_cost,
                   receipt_exchange_rate, invoice_unit_cost, invoice_exchange_rate,
                   receipt_base_amount, invoice_base_amount, variance_base_amount
            FROM purchase_receipt_invoice_matches
            WHERE company_id = @company AND invoice_doc_id = @invoice
            ORDER BY receipt_doc_id, receipt_line_id, invoice_line_id, match_id
            """))
        {
            Add(matchCommand, "@company", order.CompanyId.ToString("D"));
            Add(matchCommand, "@invoice", invoiceDocId);
            await using var matchReader = await matchCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await matchReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                matchDetails.Add(new
                {
                    matchId = Convert.ToString(matchReader.GetValue(0), CultureInfo.InvariantCulture),
                    purchaseOrderId = Convert.ToInt64(matchReader.GetValue(1), CultureInfo.InvariantCulture),
                    receiptDocId = Convert.ToInt64(matchReader.GetValue(2), CultureInfo.InvariantCulture),
                    receiptLineId = Convert.ToInt64(matchReader.GetValue(3), CultureInfo.InvariantCulture),
                    invoiceLineId = Convert.ToInt64(matchReader.GetValue(4), CultureInfo.InvariantCulture),
                    materialId = Convert.ToInt64(matchReader.GetValue(5), CultureInfo.InvariantCulture),
                    matchedQty = Convert.ToDecimal(matchReader.GetValue(6), CultureInfo.InvariantCulture),
                    receiptUnitCost = Convert.ToDecimal(matchReader.GetValue(7), CultureInfo.InvariantCulture),
                    receiptExchangeRate = Convert.ToDecimal(matchReader.GetValue(8), CultureInfo.InvariantCulture),
                    invoiceUnitCost = Convert.ToDecimal(matchReader.GetValue(9), CultureInfo.InvariantCulture),
                    invoiceExchangeRate = Convert.ToDecimal(matchReader.GetValue(10), CultureInfo.InvariantCulture),
                    receiptBaseAmount = Convert.ToDecimal(matchReader.GetValue(11), CultureInfo.InvariantCulture),
                    invoiceBaseAmount = Convert.ToDecimal(matchReader.GetValue(12), CultureInfo.InvariantCulture),
                    varianceBaseAmount = Convert.ToDecimal(matchReader.GetValue(13), CultureInfo.InvariantCulture)
                });
            }
        }

        // I3b: per-line variance (qty / rate / amount) is part of the
        // persisted evaluation snapshot, viewable in the match-policy
        // window's history detail — not just the aggregate percents.
        var lineVariance = invoiceLines.Select(invoiceLine =>
        {
            var sourceLine = invoiceLine.SourceLineNo is int n && sourceLines.TryGetValue(n, out var src) ? src : null;
            var qtyVariance = sourceLine is null ? 0m : invoiceLine.Qty - sourceLine.Qty;
            var rateVariance = sourceLine is null ? 0m : invoiceLine.UnitPrice - sourceLine.UnitPrice;
            var amountVariance = sourceLine is null ? 0m
                : decimal.Round((invoiceLine.UnitPrice - sourceLine.UnitPrice) * invoiceLine.Qty
                    * (1m - invoiceLine.DiscountPct / 100m), 2, MidpointRounding.AwayFromZero);
            return new
            {
                sourceLineNo = invoiceLine.SourceLineNo,
                materialId = invoiceLine.MaterialId,
                orderedQty = sourceLine?.Qty ?? 0m,
                invoicedQty = invoiceLine.Qty,
                qtyVariance,
                orderRate = sourceLine?.UnitPrice ?? 0m,
                invoiceRate = invoiceLine.UnitPrice,
                rateVariance,
                amountVariance
            };
        }).ToArray();

        var resultSnapshot = JsonSerializer.Serialize(new
        {
            priceVariancePercent, taxVariancePercent, absoluteBaseVariance,
            grniBaseAmount, invoiceBaseAmount, invoiceTax, expectedTax,
            matchDetails,
            lineVariance,
            evaluation.Decision, evaluation.ReasonCodes
        });

        var reasonsValue = _provider == "PostgreSQL" ? "CAST(@reasons AS jsonb)" : "@reasons";
        var policySnapshotValue = _provider == "PostgreSQL" ? "CAST(@policySnapshot AS jsonb)" : "@policySnapshot";
        var resultSnapshotValue = _provider == "PostgreSQL" ? "CAST(@resultSnapshot AS jsonb)" : "@resultSnapshot";
        await using var insert = CreateCommand(connection, transaction, $"""
            INSERT INTO purchase_match_evaluations
                (evaluation_id, company_code, invoice_doc_id, evaluation_version, policy_version,
                 decision, reason_codes_json, policy_snapshot_json, result_snapshot_json, evaluated_by)
            VALUES
                (@id, @company, @invoice, @version, @policyVersion,
                 @decision, {reasonsValue}, {policySnapshotValue}, {resultSnapshotValue}, @actor)
            """);
        Add(insert, "@id", _provider == "PostgreSQL" ? (object)evaluationId : evaluationId.ToString("D"));
        Add(insert, "@company", companyCode);
        Add(insert, "@invoice", invoiceDocId);
        Add(insert, "@version", nextVersion);
        Add(insert, "@policyVersion", policyVersion);
        Add(insert, "@decision", evaluation.Decision);
        Add(insert, "@reasons", JsonSerializer.Serialize(evaluation.ReasonCodes));
        Add(insert, "@policySnapshot", policySnapshot);
        Add(insert, "@resultSnapshot", resultSnapshot);
        Add(insert, "@actor", userId.Trim());
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
        var computed = ComputeTotals(draft.Lines, out var subtotal, out var taxAmount, out _);
        // Stored-total rule (native invoice family): grand = subtotal -
        // header discount + tax + header extra charges. The draft carries
        // the header money; it is never silently zeroed.
        var grandTotal = decimal.Round(subtotal - draft.DiscountAmount + taxAmount + draft.ExtraAmount, 2);
        long docId;
        // delivery_cost exists only on sales_documents (migration 096);
        // contract_id likewise (migration 101, W7c draw-down link).
        var deliveryCostColumn = side == CommercialDocSide.Sales ? ", delivery_cost, contract_id" : string.Empty;
        var deliveryCostValue = side == CommercialDocSide.Sales ? ", @deliveryCost, @contractId" : string.Empty;
        await using (var insertHeader = CreateCommand(connection, transaction,
            $"INSERT INTO {docs} (company_id, doc_kind, doc_no, doc_date, party_id, party_name, warehouse_id, currency_code, exchange_rate, subtotal, discount_amount, tax_amount, grand_total, status, notes, created_by, party_code, delivery_date, valid_till, cost_center, branch_name, sales_rep_name, warehouse_name, posted_by, posted_at, extra_amount{deliveryCostColumn}, required_by_date, request_type, department, payment_terms, incoterm, reference_no, bill_date, delivery_note_no, supplier_credit_note_ref, amended_from_doc_id) " +
            $"VALUES (@company, @kind, @no, @date, @party, @partyName, @warehouse, @currency, @rate, @subtotal, @discount, @tax, @total, @status, @notes, @actor, @partyCode, @deliveryDate, @validTill, @costCenter, @branchName, @salesRepName, @warehouseName, @postedBy, @postedAt, @extra{deliveryCostValue}, @requiredBy, @requestType, @department, @paymentTerms, @incoterm, @referenceNo, @billDate, @deliveryNoteNo, @creditNoteRef, @amendedFrom)" +
            (_provider == "PostgreSQL" ? " RETURNING doc_id" : "; SELECT last_insert_rowid();")))
        {
            if (side == CommercialDocSide.Sales)
            {
                Add(insertHeader, "@deliveryCost", draft.DeliveryCost);
                Add(insertHeader, "@contractId", (object?)draft.ContractId ?? DBNull.Value);
            }
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
            Add(insertHeader, "@discount", draft.DiscountAmount);
            Add(insertHeader, "@extra", draft.ExtraAmount);
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
            Add(insertHeader, "@requiredBy", (object?)draft.RequiredByDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? DBNull.Value);
            Add(insertHeader, "@requestType", (object?)draft.RequestType ?? DBNull.Value);
            Add(insertHeader, "@department", (object?)draft.Department ?? DBNull.Value);
            Add(insertHeader, "@paymentTerms", (object?)draft.PaymentTerms ?? DBNull.Value);
            Add(insertHeader, "@incoterm", (object?)draft.Incoterm ?? DBNull.Value);
            Add(insertHeader, "@referenceNo", (object?)draft.ReferenceNo ?? DBNull.Value);
            Add(insertHeader, "@billDate", (object?)draft.BillDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? DBNull.Value);
            Add(insertHeader, "@deliveryNoteNo", (object?)draft.DeliveryNoteNo ?? DBNull.Value);
            Add(insertHeader, "@creditNoteRef", (object?)draft.SupplierCreditNoteRef ?? DBNull.Value);
            Add(insertHeader, "@amendedFrom", (object?)draft.AmendedFromDocId ?? DBNull.Value);
            var posted = status == CommercialDocumentStatus.Posted;
            Add(insertHeader, "@postedBy", posted ? userId.Trim() : DBNull.Value);
            Add(insertHeader, "@postedAt", posted ? DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : DBNull.Value);
            docId = Convert.ToInt64(await insertHeader.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }
        // W1 (migration 095): a SAL invoice's due date is doc_date + the
        // party's credit_days (covers ConvertToInvoice, which is born
        // posted through this insert).
        if (side == CommercialDocSide.Sales && draft.DocKind == CommercialDocKinds.SalesInvoice && draft.PartyId is not null
            && PartyLedger.ColumnExists(connection, transaction, _provider, docs, "due_date"))
        {
            var due = await PartyLedger.ComputeDueDateAsync(connection, transaction, _provider,
                draft.CompanyId.ToString("D"), draft.PartyId.Value, draft.DocDate, cancellationToken).ConfigureAwait(false);
            if (due is not null)
            {
                await using var dueCmd = CreateCommand(connection, transaction,
                    $"UPDATE {docs} SET due_date = @due WHERE doc_id = @doc AND due_date IS NULL");
                Add(dueCmd, "@due", due.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                Add(dueCmd, "@doc", docId);
                await dueCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        if (side == CommercialDocSide.Purchase &&
            string.Equals(draft.DocKind.Trim(), CommercialDocKinds.PurchaseInvoice, StringComparison.Ordinal))
            await StampPurchaseInvoiceDueDateAsync(connection, transaction, docId, draft, cancellationToken).ConfigureAwait(false);
        await InsertLinesAsync(connection, transaction, side, docId, draft.Lines, computed, cancellationToken).ConfigureAwait(false);
        return docId;
    }

    /// <summary>
    /// Y3 (migration 109): every purchase invoice carries a due date so the
    /// payable aging report can age by due date instead of entry date. An
    /// explicit draft due date wins; otherwise the due date is the document
    /// date plus the supplier's payment terms (parties.credit_days, resolved
    /// by the canonical ledger_id the document header carries); with no
    /// resolvable supplier terms it falls back to the document date itself.
    /// Runs inside the caller's insert transaction.
    /// </summary>
    private async Task StampPurchaseInvoiceDueDateAsync(
        DbConnection connection, DbTransaction transaction, long docId,
        CommercialDocumentDraft draft, CancellationToken cancellationToken)
    {
        var dueDate = draft.DueDate ?? draft.DocDate;
        if (draft.DueDate is null && draft.PartyId is long supplierLedgerId)
        {
            await using var lookup = CreateCommand(connection, transaction,
                "SELECT credit_days FROM parties WHERE ledger_id = @party AND kind = 'S' AND is_deleted = " + (_provider == "PostgreSQL" ? "FALSE" : "0"));
            Add(lookup, "@party", supplierLedgerId);
            var terms = await lookup.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (terms is not null and not DBNull)
            {
                var creditDays = Convert.ToInt32(terms, CultureInfo.InvariantCulture);
                if (creditDays > 0)
                    dueDate = draft.DocDate.AddDays(creditDays);
            }
        }

        await using var update = CreateCommand(connection, transaction,
            "UPDATE purchase_documents SET due_date = @due WHERE doc_id = @doc");
        Add(update, "@due", dueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add(update, "@doc", docId);
        await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task InsertLinesAsync(
        DbConnection connection, DbTransaction transaction, CommercialDocSide side, long docId,
        IReadOnlyList<CommercialLineDraft> lines, List<(decimal Subtotal, decimal Tax, decimal Total)> computed,
        CancellationToken cancellationToken)
    {
        var (_, lineTable) = Tables(side);
        var lineNumber = 0;
        var usedLineNumbers = new HashSet<int>();
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            var totals = computed[index];
            // P4: snapshot the UOM factor at entry from the material
            // card when the caller did not supply one, so later card
            // changes can never restate a saved document.
            if (line.UomFactor is null && line.MaterialId is long factorMaterialId)
            {
                var factor = await UnitEquivalents.ResolveAsync(
                    connection, transaction, null, factorMaterialId, line.Unit, cancellationToken).ConfigureAwait(false);
                line = line with { UomFactor = factor };
            }
            lineNumber++;
            var persistedLineNumber = line.SourceLineNo ?? lineNumber;
            if (persistedLineNumber <= 0 || !usedLineNumbers.Add(persistedLineNumber))
                throw new InvalidOperationException("Document line numbers must be positive and unique within a document.");
            await using var insertLine = CreateCommand(connection, transaction,
                $"INSERT INTO {lineTable} (doc_id, line_no, material_id, material_code, description, unit, qty, unit_price, discount_pct, tax_rate, line_subtotal, line_tax, line_total, warehouse_id, uom_factor, line_required_by, rejected_qty, reject_reason, tax_code) " +
                "VALUES (@doc, @line, @material, @materialCode, @description, @unit, @qty, @price, @discount, @taxRate, @subtotal, @tax, @total, @warehouse, @uomFactor, @lineRequiredBy, @rejectedQty, @rejectReason, @taxCode)");
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
            Add(insertLine, "@uomFactor", (object?)line.UomFactor ?? DBNull.Value);
            Add(insertLine, "@lineRequiredBy", (object?)line.LineRequiredBy?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? DBNull.Value);
            Add(insertLine, "@rejectedQty", line.RejectedQty);
            Add(insertLine, "@rejectReason", (object?)line.RejectReason ?? DBNull.Value);
            Add(insertLine, "@taxCode", (object?)line.TaxCode ?? string.Empty);
            await insertLine.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------- picking (W4)
    // Warehouse picking staged from a posted sales order (spec §4). A
    // picking posts nothing: it records what the warehouse picked per
    // source line, and a Confirmed picking feeds delivery-note creation
    // in SalesDeliveryWin. Remaining quantities always come from the one
    // progress computation, inside the caller's transaction.

    public async Task<long> CreatePickingFromOrderAsync(long salesOrderId, string userId, CancellationToken cancellationToken = default)
    {
        if (salesOrderId <= 0) throw new ArgumentOutOfRangeException(nameof(salesOrderId));
        if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("User is required.", nameof(userId));
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var order = await ReadSnapshotAsync(connection, transaction, CommercialDocSide.Sales, salesOrderId, null, null, null, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Sales order was not found.");
            if (order.DocKind != CommercialDocKinds.SalesOrder)
                throw new AppError(ErrorCodes.ChainTransitionNotAllowed,
                    $"Documents cannot be converted from '{order.DocKind}' to a picking.",
                    order.DocKind, "Picking");
            if (order.Status != CommercialDocumentStatus.Posted)
                throw new AppError(ErrorCodes.ChainSourceNotPosted,
                    $"The source document '{order.DocNo}' must be posted before it can be converted.",
                    order.DocNo);

            var progress = await ComputeProgressAsync(connection, transaction, CommercialDocSide.Sales, salesOrderId, cancellationToken).ConfigureAwait(false);
            var remainingByLine = progress.Lines.ToDictionary(line => line.LineNo, line => line.RemainingToFulfill);

            long pickingId;
            await using (var insert = CreateCommand(connection, transaction,
                "INSERT INTO sales_picking (company_id, sales_order_id, status, created_by, created_at) " +
                "VALUES (@company, @order, @status, @actor, @now)" +
                (_provider == "PostgreSQL" ? " RETURNING picking_id" : "; SELECT last_insert_rowid();")))
            {
                Add(insert, "@company", order.CompanyId.ToString("D"));
                Add(insert, "@order", salesOrderId);
                Add(insert, "@status", SalesPickingStatus.Draft);
                Add(insert, "@actor", userId.Trim());
                Add(insert, "@now", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                pickingId = Convert.ToInt64(await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            }

            var inserted = 0;
            foreach (var line in order.Lines)
            {
                var remaining = remainingByLine.GetValueOrDefault(line.LineNo);
                if (remaining <= 0m)
                    continue;
                await using var insertLine = CreateCommand(connection, transaction, """
                    INSERT INTO sales_picking_lines
                        (picking_id, source_line_no, material_id, material_code, description, unit, ordered_qty, picked_qty, warehouse_id)
                    VALUES (@picking, @lineNo, @material, @materialCode, @description, @unit, @ordered, @picked, @warehouse)
                    """);
                Add(insertLine, "@picking", pickingId);
                Add(insertLine, "@lineNo", line.LineNo);
                Add(insertLine, "@material", (object?)line.MaterialId ?? DBNull.Value);
                Add(insertLine, "@materialCode", (object?)line.MaterialCode ?? DBNull.Value);
                Add(insertLine, "@description", line.Description);
                Add(insertLine, "@unit", (object?)line.Unit ?? DBNull.Value);
                Add(insertLine, "@ordered", line.Qty);
                Add(insertLine, "@picked", remaining);
                Add(insertLine, "@warehouse", (object?)(line.WarehouseId ?? order.WarehouseId) ?? DBNull.Value);
                await insertLine.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                inserted++;
            }
            if (inserted == 0)
                throw new AppError(ErrorCodes.ChainSourceFullyFulfilled,
                    $"The source document '{order.DocNo}' is already fully fulfilled.",
                    order.DocNo);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return pickingId;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<SalesPickingSnapshot?> GetPickingAsync(long pickingId, CancellationToken cancellationToken = default)
    {
        if (pickingId <= 0) throw new ArgumentOutOfRangeException(nameof(pickingId));
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadPickingAsync(connection, null, pickingId, null, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SalesPickingSnapshot?> FindConfirmedPickingAsync(Guid companyId, long salesOrderId, CancellationToken cancellationToken = default)
    {
        if (companyId == Guid.Empty) throw new ArgumentException("Company is required.", nameof(companyId));
        if (salesOrderId <= 0) throw new ArgumentOutOfRangeException(nameof(salesOrderId));
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        long? pickingId = null;
        await using (var command = CreateCommand(connection, null, """
            SELECT picking_id FROM sales_picking
            WHERE company_id = @company AND sales_order_id = @order AND status = @confirmed
            ORDER BY picking_id DESC LIMIT 1
            """))
        {
            Add(command, "@company", companyId.ToString("D"));
            Add(command, "@order", salesOrderId);
            Add(command, "@confirmed", SalesPickingStatus.Confirmed);
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (value is not null and not DBNull)
                pickingId = Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        if (pickingId is null)
            return null;
        return await ReadPickingAsync(connection, null, pickingId.Value, null, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task ConfirmPickingAsync(long pickingId, IReadOnlyDictionary<int, decimal> pickedBySourceLine, string userId, CancellationToken cancellationToken = default)
    {
        if (pickingId <= 0) throw new ArgumentOutOfRangeException(nameof(pickingId));
        ArgumentNullException.ThrowIfNull(pickedBySourceLine);
        if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("User is required.", nameof(userId));
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var picking = await ReadPickingAsync(connection, transaction, pickingId, null, null, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Picking was not found.");
            if (picking.Status != SalesPickingStatus.Draft)
                throw new InvalidOperationException($"Only a draft picking can be confirmed; picking '{pickingId}' is '{picking.Status}'.");

            // Over-pick guard (SAL-7012): the picked quantity of a line
            // may never exceed what the order still has to deliver,
            // recomputed inside this transaction from posted deliveries.
            var progress = await ComputeProgressAsync(connection, transaction, CommercialDocSide.Sales, picking.SalesOrderId, cancellationToken).ConfigureAwait(false);
            var remainingByLine = progress.Lines.ToDictionary(line => line.LineNo, line => line.RemainingToFulfill);

            foreach (var line in picking.Lines)
            {
                var picked = pickedBySourceLine.TryGetValue(line.SourceLineNo, out var requested)
                    ? requested
                    : line.PickedQty;
                if (picked < 0m)
                    throw new InvalidOperationException("Picked quantities cannot be negative.");
                var remaining = remainingByLine.GetValueOrDefault(line.SourceLineNo);
                if (picked > remaining)
                    throw new AppError(ErrorCodes.PickingExceedsRemaining,
                        $"The picked quantity for '{line.Description}' exceeds the remaining quantity to deliver.",
                        line.Description);
                await using var updateLine = CreateCommand(connection, transaction,
                    "UPDATE sales_picking_lines SET picked_qty = @picked WHERE picking_line_id = @id");
                Add(updateLine, "@picked", picked);
                Add(updateLine, "@id", line.PickingLineId);
                await updateLine.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var update = CreateCommand(connection, transaction, """
                UPDATE sales_picking
                SET status = @confirmed, confirmed_by = @actor, confirmed_at = @now
                WHERE picking_id = @id AND status = @draft
                """))
            {
                Add(update, "@confirmed", SalesPickingStatus.Confirmed);
                Add(update, "@draft", SalesPickingStatus.Draft);
                Add(update, "@actor", userId.Trim());
                Add(update, "@now", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                Add(update, "@id", pickingId);
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new InvalidOperationException("Picking could not be confirmed.");
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task CancelPickingAsync(long pickingId, string userId, CancellationToken cancellationToken = default)
    {
        if (pickingId <= 0) throw new ArgumentOutOfRangeException(nameof(pickingId));
        if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("User is required.", nameof(userId));
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var update = CreateCommand(connection, null, """
            UPDATE sales_picking SET status = @cancelled
            WHERE picking_id = @id AND status IN (@draft, @confirmed)
            """);
        Add(update, "@cancelled", SalesPickingStatus.Cancelled);
        Add(update, "@draft", SalesPickingStatus.Draft);
        Add(update, "@confirmed", SalesPickingStatus.Confirmed);
        Add(update, "@id", pickingId);
        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("Picking was not found or is already cancelled.");
    }

    private async Task<SalesPickingSnapshot?> ReadPickingAsync(
        DbConnection connection, DbTransaction? transaction, long pickingId,
        Guid? companyId, long? salesOrderId, CancellationToken cancellationToken)
    {
        long foundId, foundOrderId;
        Guid foundCompany;
        string status;
        string? createdBy, createdAt, confirmedBy, confirmedAt;
        await using (var header = CreateCommand(connection, transaction, """
            SELECT picking_id, company_id, sales_order_id, status, created_by, created_at, confirmed_by, confirmed_at
            FROM sales_picking
            WHERE picking_id = @id
              AND (@company IS NULL OR company_id = @company)
              AND (@order IS NULL OR sales_order_id = @order)
            """))
        {
            Add(header, "@id", pickingId);
            Add(header, "@company", (object?)companyId?.ToString("D") ?? DBNull.Value);
            Add(header, "@order", (object?)salesOrderId ?? DBNull.Value);
            await using var reader = await header.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return null;
            foundId = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
            if (!Guid.TryParse(Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture), out foundCompany))
                throw new InvalidOperationException("The saved picking has an invalid company identifier.");
            foundOrderId = Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture);
            status = reader.GetString(3);
            createdBy = reader.IsDBNull(4) ? null : reader.GetString(4);
            createdAt = reader.IsDBNull(5) ? null : Convert.ToString(reader.GetValue(5), CultureInfo.InvariantCulture);
            confirmedBy = reader.IsDBNull(6) ? null : reader.GetString(6);
            confirmedAt = reader.IsDBNull(7) ? null : Convert.ToString(reader.GetValue(7), CultureInfo.InvariantCulture);
        }

        var lines = new List<SalesPickingLineSnapshot>();
        await using (var lineCommand = CreateCommand(connection, transaction, """
            SELECT picking_line_id, source_line_no, material_id, material_code, description, unit,
                   ordered_qty, picked_qty, warehouse_id
            FROM sales_picking_lines
            WHERE picking_id = @id
            ORDER BY source_line_no
            """))
        {
            Add(lineCommand, "@id", foundId);
            await using var reader = await lineCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                lines.Add(new SalesPickingLineSnapshot(
                    Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                    Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture),
                    reader.IsDBNull(2) ? null : Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    Convert.ToDecimal(reader.GetValue(6), CultureInfo.InvariantCulture),
                    Convert.ToDecimal(reader.GetValue(7), CultureInfo.InvariantCulture),
                    reader.IsDBNull(8) ? null : Convert.ToInt64(reader.GetValue(8), CultureInfo.InvariantCulture)));
            }
        }
        return new SalesPickingSnapshot(
            foundId, foundCompany, foundOrderId, status, createdBy, createdAt,
            confirmedBy, confirmedAt, lines.AsReadOnly());
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
