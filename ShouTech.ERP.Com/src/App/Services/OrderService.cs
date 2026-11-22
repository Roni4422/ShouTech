using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.Application.Services;

/// <summary>
/// Sales/purchase order service over the commercial-document engine.
/// Orders and quotations are commitment documents: they post no GL and no
/// stock (ERPNext pattern). All invariants are validated here before the
/// repository is touched; the repository re-validates state inside its
/// transactions as a second line of defence.
/// </summary>
public sealed class OrderService
{
    private readonly IOrderDocumentRepository _orders;
    private readonly ICommercialDocumentRepository _chain;

    public OrderService(IOrderDocumentRepository orders, ICommercialDocumentRepository chain)
    {
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _chain = chain ?? throw new ArgumentNullException(nameof(chain));
    }

    internal IOrderDocumentRepository Orders => _orders;

    public static CommercialDocSide SideFor(string docKind) => docKind switch
    {
        CommercialDocKinds.SalesOrder or CommercialDocKinds.SalesQuotation or CommercialDocKinds.DeliveryNote or CommercialDocKinds.SalesInvoice => CommercialDocSide.Sales,
        CommercialDocKinds.PurchaseOrder or CommercialDocKinds.RequestForQuotation or CommercialDocKinds.SupplierQuotation or CommercialDocKinds.PurchaseReceipt or CommercialDocKinds.PurchaseReceiptReturn or CommercialDocKinds.PurchaseInvoice => CommercialDocSide.Purchase,
        _ => throw new ArgumentException($"Unknown document kind '{docKind}'.", nameof(docKind))
    };

    public static bool IsOrderKind(string docKind) =>
        docKind is CommercialDocKinds.SalesOrder or CommercialDocKinds.PurchaseOrder
            or CommercialDocKinds.SalesQuotation or CommercialDocKinds.RequestForQuotation or CommercialDocKinds.SupplierQuotation;

    public async Task<long> CreateOrderAsync(CommercialDocumentDraft draft, string userId, CancellationToken cancellationToken = default)
    {
        ValidateOrderDraft(draft);
        return await _orders.CreateDraftAsync(SideFor(draft.DocKind), draft, userId, cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateOrderAsync(long docId, CommercialDocumentDraft draft, string userId, CancellationToken cancellationToken = default)
    {
        ValidateOrderDraft(draft);
        await _orders.UpdateDraftAsync(SideFor(draft.DocKind), docId, draft, userId, cancellationToken).ConfigureAwait(false);
    }

    public Task<CommercialDocumentSnapshot?> FindByIdAsync(CommercialDocSide side, long docId, CancellationToken cancellationToken = default)
        => _orders.FindByIdAsync(side, docId, cancellationToken);

    public Task<CommercialDocumentSnapshot?> FindByNumberAsync(CommercialDocSide side, Guid companyId, string docKind, string docNo, CancellationToken cancellationToken = default)
        => _orders.FindByNumberAsync(side, companyId, docKind, docNo, cancellationToken);

    public Task<string> NextDocNoAsync(CommercialDocSide side, Guid companyId, string docKind, CancellationToken cancellationToken = default)
        => _orders.NextDocNoAsync(side, companyId, docKind, cancellationToken);

    public Task PostOrderAsync(CommercialDocSide side, long docId, string userId, CancellationToken cancellationToken = default)
        => _orders.PostOrderAsync(side, docId, userId, cancellationToken);

    /// <summary>
    /// Creates a draft delivery note from the remaining quantities on a posted
    /// sales order. The chain repository records the source link atomically;
    /// posting is a separate operation so the warehouse can review the draft.
    /// </summary>
    public async Task<(long DeliveryId, string DeliveryDocNo)> CreateSalesDeliveryAsync(
        long salesOrderId,
        DateOnly deliveryDate,
        string userId,
        IReadOnlyDictionary<long, decimal>? quantityByMaterialId = null,
        long? warehouseId = null,
        CancellationToken cancellationToken = default)
    {
        if (salesOrderId <= 0) throw new ArgumentOutOfRangeException(nameof(salesOrderId));
        var order = await _chain.FindSalesByIdAsync(salesOrderId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Sales order was not found.");
        if (order.DocKind != CommercialDocKinds.SalesOrder)
            throw new InvalidOperationException("A delivery note must be created from a sales order.");
        if (order.Status != CommercialDocumentStatus.Posted)
            throw new InvalidOperationException("Only a posted sales order can be delivered.");

        var deliveryDocNo = await _orders.NextDocNoAsync(
            CommercialDocSide.Sales, order.CompanyId, CommercialDocKinds.DeliveryNote, cancellationToken).ConfigureAwait(false);
        var chain = new SalesChainService(_chain);
        var deliveryId = await chain.CreateFromSourceAsync(
            salesOrderId, deliveryDocNo, deliveryDate, userId,
            quantityByMaterialId, warehouseId, cancellationToken).ConfigureAwait(false);
        return (deliveryId, deliveryDocNo);
    }

    /// <summary>
    /// Creates a draft sales invoice from a posted delivery note. The invoice
    /// inherits the delivered lines and carries a DLV-to-SAL document link.
    /// </summary>
    public async Task<(long InvoiceId, string InvoiceDocNo)> CreateSalesInvoiceFromDeliveryAsync(
        long deliveryDocId,
        DateOnly invoiceDate,
        string userId,
        CancellationToken cancellationToken = default)
    {
        if (deliveryDocId <= 0) throw new ArgumentOutOfRangeException(nameof(deliveryDocId));
        var delivery = await _chain.FindSalesByIdAsync(deliveryDocId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Delivery note was not found.");
        if (delivery.DocKind != CommercialDocKinds.DeliveryNote)
            throw new InvalidOperationException("A sales invoice must be created from a delivery note.");
        if (delivery.Status != CommercialDocumentStatus.Posted)
            throw new InvalidOperationException("Only a posted delivery note can be invoiced.");

        var existingLinks = await _chain.GetSalesLinksFromAsync(
            delivery.CompanyId, deliveryDocId, cancellationToken).ConfigureAwait(false);
        foreach (var existingLink in existingLinks.Where(link =>
                     link.TargetDocKind == CommercialDocKinds.SalesInvoice))
        {
            var existingInvoice = await _chain.FindSalesByIdAsync(
                existingLink.TargetDocId, cancellationToken).ConfigureAwait(false);
            if (existingInvoice is not null &&
                existingInvoice.Status != CommercialDocumentStatus.Cancelled)
                throw new InvalidOperationException(
                    $"Delivery note '{delivery.DocNo}' already has an active sales invoice '{existingInvoice.DocNo}'.");
        }

        var invoiceDocNo = await _orders.NextDocNoAsync(
            CommercialDocSide.Sales, delivery.CompanyId, CommercialDocKinds.SalesInvoice, cancellationToken).ConfigureAwait(false);
        var chain = new SalesChainService(_chain);
        var invoiceId = await chain.CreateFromSourceAsync(
            deliveryDocId, invoiceDocNo, invoiceDate, userId, null, null, cancellationToken).ConfigureAwait(false);
        return (invoiceId, invoiceDocNo);
    }

    /// <summary>Posts a sales-chain draft, including the DLV-linked invoice accounting path.</summary>
    public Task PostSalesChainDocumentAsync(long documentId, string userId, CancellationToken cancellationToken = default)
        => _chain.PostSalesAsync(documentId, userId, cancellationToken);

    /// <summary>Posts a delivery note through the stock-valuation chain in one transaction.</summary>
    public Task PostSalesDeliveryAsync(long deliveryDocId, string userId, CancellationToken cancellationToken = default)
        => _chain.PostSalesAsync(deliveryDocId, userId, cancellationToken);

    public async Task<(long ReceiptId, string ReceiptDocNo)> CreatePurchaseReceiptAsync(Guid companyId, long purchaseOrderId, DateOnly receiptDate, IReadOnlyList<CommercialLineDraft> receivedLines, string userId, CancellationToken cancellationToken = default)
    {
        var order = await _orders.FindByIdAsync(CommercialDocSide.Purchase, purchaseOrderId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Purchase order was not found.");
        if (order.CompanyId != companyId)
            throw new InvalidOperationException("The purchase order belongs to a different company.");
        if (order.DocKind != CommercialDocKinds.PurchaseOrder)
            throw new InvalidOperationException("A goods receipt must be created from a purchase order.");
        var receiptDocNo = await _orders.NextDocNoAsync(CommercialDocSide.Purchase, order.CompanyId, CommercialDocKinds.PurchaseReceipt, cancellationToken).ConfigureAwait(false);
        var receiptId = await _orders.CreatePurchaseReceiptAsync(companyId, purchaseOrderId, receiptDocNo, receiptDate, receivedLines, userId, cancellationToken).ConfigureAwait(false);
        return (receiptId, receiptDocNo);
    }

    public Task<IReadOnlyList<PurchaseReceiptReturnableLine>> GetPurchaseReceiptReturnableLinesAsync(
        Guid companyId, long receiptId, CancellationToken cancellationToken = default)
    {
        if (companyId == Guid.Empty) throw new ArgumentException("Company is required.", nameof(companyId));
        if (receiptId <= 0) throw new ArgumentOutOfRangeException(nameof(receiptId));
        return _orders.GetPurchaseReceiptReturnableLinesAsync(companyId, receiptId, cancellationToken);
    }

    /// <summary>Returns unbilled goods to a supplier from a posted receipt, reversing GRNI and stock in one transaction.</summary>
    public async Task<(long ReturnId, string ReturnDocNo)> CreatePurchaseReceiptReturnAsync(
        Guid companyId, long receiptId, DateOnly returnDate,
        IReadOnlyList<CommercialLineDraft> returnedLines, string userId, string? reason = null,
        CancellationToken cancellationToken = default)
    {
        if (companyId == Guid.Empty) throw new ArgumentException("Company is required.", nameof(companyId));
        if (receiptId <= 0) throw new ArgumentOutOfRangeException(nameof(receiptId));
        ArgumentNullException.ThrowIfNull(returnedLines);
        if (returnedLines.Count == 0) throw new InvalidOperationException("At least one return line is required.");
        var docNo = await _orders.NextDocNoAsync(
            CommercialDocSide.Purchase, companyId, CommercialDocKinds.PurchaseReceiptReturn, cancellationToken).ConfigureAwait(false);
        var returnId = await _orders.CreatePurchaseReceiptReturnAsync(
            companyId, receiptId, docNo, returnDate, returnedLines, userId, reason, cancellationToken).ConfigureAwait(false);
        return (returnId, docNo);
    }

    public async Task<(long InvoiceId, string InvoiceDocNo)> CreatePurchaseInvoiceAsync(
        Guid companyId, long purchaseOrderId, DateOnly invoiceDate,
        IReadOnlyList<CommercialLineDraft> invoiceLines, string userId,
        CancellationToken cancellationToken = default)
    {
        if (companyId == Guid.Empty) throw new ArgumentException("Company is required.", nameof(companyId));
        ArgumentNullException.ThrowIfNull(invoiceLines);
        var order = await _orders.FindByIdAsync(CommercialDocSide.Purchase, purchaseOrderId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Purchase order was not found.");
        if (order.CompanyId != companyId)
            throw new InvalidOperationException("The purchase order belongs to a different company.");
        if (order.DocKind != CommercialDocKinds.PurchaseOrder)
            throw new InvalidOperationException("A supplier invoice must be created from a purchase order.");
        var invoiceDocNo = await _orders.NextDocNoAsync(
            CommercialDocSide.Purchase, companyId, CommercialDocKinds.PurchaseInvoice, cancellationToken).ConfigureAwait(false);
        var invoiceId = await _orders.CreatePurchaseInvoiceAsync(
            companyId, purchaseOrderId, invoiceDocNo, invoiceDate, invoiceLines, userId, cancellationToken).ConfigureAwait(false);
        return (invoiceId, invoiceDocNo);
    }

    public Task CloseOrderAsync(CommercialDocSide side, long docId, string userId, CancellationToken cancellationToken = default)
        => _orders.SetStatusAsync(side, docId, CommercialDocumentStatus.Closed, userId, cancellationToken);

    public Task CancelOrderAsync(CommercialDocSide side, long docId, string userId, CancellationToken cancellationToken = default)
        => _orders.SetStatusAsync(side, docId, CommercialDocumentStatus.Cancelled, userId, cancellationToken);

    public Task MarkQuotationLostAsync(long docId, string userId, CancellationToken cancellationToken = default)
        => _orders.SetStatusAsync(CommercialDocSide.Sales, docId, CommercialDocumentStatus.Lost, userId, cancellationToken);

    public Task<IReadOnlyList<CommercialDocumentSummary>> SearchAsync(CommercialDocSide side, Guid companyId, string? docKind, string? status, string? search, DateOnly? from, DateOnly? to, int limit = 500, CancellationToken cancellationToken = default)
        => _orders.ListAsync(side, companyId, docKind, status, search, from, to, limit, cancellationToken);

    public Task<OrderProgress> GetProgressAsync(CommercialDocSide side, long docId, CancellationToken cancellationToken = default)
        => _orders.GetProgressAsync(side, docId, cancellationToken);

    /// <summary>
    /// Converts a posted order into a posted invoice with only the remaining
    /// unbilled quantities (single transaction in the repository).
    /// </summary>
    public async Task<(long InvoiceId, string InvoiceDocNo)> ConvertToInvoiceAsync(CommercialDocSide side, long orderDocId, DateOnly invoiceDate, string userId, CancellationToken cancellationToken = default)
    {
        if (side == CommercialDocSide.Purchase)
            throw new InvalidOperationException("Supplier invoices must use CreatePurchaseInvoiceAsync with the active company and received line quantities.");
        var order = await _orders.FindByIdAsync(side, orderDocId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Order was not found.");
        var invoiceKind = side == CommercialDocSide.Sales ? CommercialDocKinds.SalesInvoice : CommercialDocKinds.PurchaseInvoice;
        var invoiceDocNo = await _orders.NextDocNoAsync(side, order.CompanyId, invoiceKind, cancellationToken).ConfigureAwait(false);
        var invoiceId = await _orders.ConvertToInvoiceAsync(side, orderDocId, invoiceDocNo, invoiceDate, userId, cancellationToken).ConfigureAwait(false);
        return (invoiceId, invoiceDocNo);
    }

    /// <summary>
    /// Converts a posted, unexpired quotation into a sales order via the
    /// selling chain (link recorded in document_links). Expired quotations
    /// are blocked (RS-6): extend the validity on the draft instead.
    /// </summary>
    public async Task<long> ConvertQuotationToOrderAsync(long quotationDocId, DateOnly orderDate, string userId, CancellationToken cancellationToken = default)
    {
        var quotation = await _chain.FindSalesByIdAsync(quotationDocId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Quotation was not found.");
        if (quotation.DocKind != CommercialDocKinds.SalesQuotation)
            throw new InvalidOperationException($"Document '{quotation.DocNo}' is not a quotation.");
        if (quotation.ValidTill is { } validTill && validTill < DateOnly.FromDateTime(DateTime.Today))
            throw new InvalidOperationException($"Quotation '{quotation.DocNo}' expired on {validTill:yyyy-MM-dd}; extend its validity before converting.");
        var chain = new SalesChainService(_chain);
        var docNo = await _orders.NextDocNoAsync(CommercialDocSide.Sales, quotation.CompanyId, CommercialDocKinds.SalesOrder, cancellationToken).ConfigureAwait(false);
        return await chain.CreateFromSourceAsync(quotationDocId, docNo, orderDate, userId, null, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Records a supplier's offer against a posted RFQ. The quote lines must
    /// map one-to-one to the requested lines; the original quantities and
    /// source line numbers are retained for an auditable comparison.
    /// </summary>
    public async Task<(long QuotationId, string QuotationNo)> CreateSupplierQuotationFromRfqAsync(
        long rfqId, long supplierId, string supplierCode, string supplierName,
        string supplierQuotationNumber, DateOnly quotationDate, DateOnly validTill,
        string currencyCode, decimal exchangeRate, IReadOnlyList<CommercialLineDraft> quotedLines,
        string userId, DateOnly? deliveryDate = null, string? terms = null,
        CancellationToken cancellationToken = default)
    {
        if (rfqId <= 0 || supplierId <= 0) throw new ArgumentOutOfRangeException(nameof(rfqId));
        if (string.IsNullOrWhiteSpace(supplierCode) || string.IsNullOrWhiteSpace(supplierName))
            throw new ArgumentException("Supplier identity is required.");
        if (string.IsNullOrWhiteSpace(supplierQuotationNumber))
            throw new ArgumentException("The supplier's quotation reference is required.", nameof(supplierQuotationNumber));
        if (string.IsNullOrWhiteSpace(currencyCode) || exchangeRate <= 0m)
            throw new ArgumentException("A valid quotation currency and exchange rate are required.");
        if (validTill < quotationDate) throw new InvalidOperationException("Quotation validity cannot precede its date.");
        if (deliveryDate is { } promised && promised < quotationDate)
            throw new InvalidOperationException("Promised delivery cannot precede the quotation date.");
        ArgumentNullException.ThrowIfNull(quotedLines);

        var rfq = await _orders.FindByIdAsync(CommercialDocSide.Purchase, rfqId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Request for quotation was not found.");
        if (rfq.DocKind != CommercialDocKinds.RequestForQuotation || rfq.Status != CommercialDocumentStatus.Posted)
            throw new InvalidOperationException("A supplier quotation must respond to a posted request for quotation.");
        if (rfq.ValidTill is { } deadline && quotationDate > deadline)
            throw new InvalidOperationException("The request for quotation response deadline has passed.");
        if (quotedLines.Count != rfq.Lines.Count)
            throw new InvalidOperationException("A supplier quotation must respond to every requested line.");

        var requested = rfq.Lines.ToDictionary(line => line.LineNo);
        var seen = new HashSet<int>();
        foreach (var line in quotedLines)
        {
            if (line.SourceLineNo is not { } sourceLineNo || !requested.TryGetValue(sourceLineNo, out var source))
                throw new InvalidOperationException("Every supplier quotation line must reference an original RFQ line.");
            if (!seen.Add(sourceLineNo) || line.Qty != source.Qty ||
                line.MaterialId != source.MaterialId ||
                !string.Equals(line.MaterialCode, source.MaterialCode, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Supplier quotation lines must match the RFQ items and requested quantities exactly.");
        }

        var docNo = await _orders.NextDocNoAsync(
            CommercialDocSide.Purchase, rfq.CompanyId, CommercialDocKinds.SupplierQuotation, cancellationToken).ConfigureAwait(false);
        var draft = new CommercialDocumentDraft(
            rfq.CompanyId, CommercialDocKinds.SupplierQuotation, docNo, quotationDate,
            supplierId, supplierName.Trim(), rfq.WarehouseId, currencyCode.Trim().ToUpperInvariant(),
            exchangeRate, $"Supplier quotation: {supplierQuotationNumber.Trim()}\n{terms}".Trim(),
            quotedLines, supplierCode.Trim(), deliveryDate, validTill,
            WarehouseName: rfq.WarehouseName);
        var quotationId = await _orders.CreateLinkedDraftAsync(
            CommercialDocSide.Purchase, draft, CommercialDocKinds.RequestForQuotation, rfqId,
            userId, cancellationToken).ConfigureAwait(false);
        return (quotationId, docNo);
    }

    /// <summary>Returns the posted supplier responses linked to an RFQ for side-by-side evaluation.</summary>
    public async Task<IReadOnlyList<SupplierQuotationOffer>> CompareSupplierQuotationsAsync(
        long rfqId, CancellationToken cancellationToken = default)
    {
        var rfq = await _orders.FindByIdAsync(CommercialDocSide.Purchase, rfqId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Request for quotation was not found.");
        if (rfq.DocKind != CommercialDocKinds.RequestForQuotation)
            throw new InvalidOperationException("Supplier quotations can only be compared against a request for quotation.");
        var links = await _orders.GetLinksFromAsync(rfq.CompanyId, rfqId, cancellationToken).ConfigureAwait(false);
        var offers = new List<SupplierQuotationOffer>();
        foreach (var link in links.Where(x => x.SourceDocKind == CommercialDocKinds.RequestForQuotation &&
                     x.TargetDocKind == CommercialDocKinds.SupplierQuotation))
        {
            var quote = await _orders.FindByIdAsync(CommercialDocSide.Purchase, link.TargetDocId, cancellationToken).ConfigureAwait(false);
            if (quote is null || quote.Status != CommercialDocumentStatus.Posted ||
                quote.DocKind != CommercialDocKinds.SupplierQuotation)
                continue;
            var quotedLines = quote.Lines.Select(line => new SupplierQuotationLineOffer(
                line.LineNo,
                line.MaterialId,
                line.MaterialCode,
                line.Description,
                line.Unit,
                line.Qty,
                line.UnitPrice,
                line.DiscountPct,
                line.TaxRate,
                line.LineTotal,
                decimal.Round(line.LineTotal * quote.ExchangeRate, 4, MidpointRounding.AwayFromZero)))
                .ToArray();
            offers.Add(new SupplierQuotationOffer(
                quote.DocId, quote.DocNo, quote.PartyName ?? quote.PartyCode ?? "Unknown supplier",
                quote.CurrencyCode, quote.ExchangeRate, quote.Subtotal, quote.TaxAmount,
                quote.GrandTotal, quote.DocDate, quote.ValidTill, quote.DeliveryDate, quote.Status,
                quotedLines));
        }
        return offers.OrderBy(x => x.GrandTotal * x.ExchangeRate)
            .ThenBy(x => x.DeliveryDate ?? DateOnly.MaxValue)
            .ToArray();
    }

    /// <summary>Creates a draft PO from an accepted, unexpired supplier quotation.</summary>
    public async Task<(long PurchaseOrderId, string PurchaseOrderNo)> CreatePurchaseOrderFromSupplierQuotationAsync(
        long supplierQuotationId, DateOnly orderDate, string userId,
        CancellationToken cancellationToken = default)
    {
        var quote = await _orders.FindByIdAsync(CommercialDocSide.Purchase, supplierQuotationId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Supplier quotation was not found.");
        if (quote.DocKind != CommercialDocKinds.SupplierQuotation || quote.Status != CommercialDocumentStatus.Posted)
            throw new InvalidOperationException("Only a posted supplier quotation can be converted to a purchase order.");
        if (quote.ValidTill is { } expiry && orderDate > expiry)
            throw new InvalidOperationException("The supplier quotation has expired; obtain a renewed offer before ordering.");
        var links = await _orders.GetLinksFromAsync(quote.CompanyId, quote.DocId, cancellationToken).ConfigureAwait(false);
        foreach (var link in links.Where(x => x.TargetDocKind == CommercialDocKinds.PurchaseOrder))
        {
            var existing = await _orders.FindByIdAsync(CommercialDocSide.Purchase, link.TargetDocId, cancellationToken).ConfigureAwait(false);
            if (existing is not null && existing.Status != CommercialDocumentStatus.Cancelled)
                throw new InvalidOperationException("This supplier quotation already has an active purchase order.");
        }

        var docNo = await _orders.NextDocNoAsync(
            CommercialDocSide.Purchase, quote.CompanyId, CommercialDocKinds.PurchaseOrder, cancellationToken).ConfigureAwait(false);
        var lines = quote.Lines.Select(line => new CommercialLineDraft(
            line.MaterialId, line.MaterialCode, line.Description, line.Unit, line.Qty,
            line.UnitPrice, line.DiscountPct, line.TaxRate, line.WarehouseId, line.LineNo)).ToArray();
        var draft = new CommercialDocumentDraft(
            quote.CompanyId, CommercialDocKinds.PurchaseOrder, docNo, orderDate,
            quote.PartyId, quote.PartyName, quote.WarehouseId, quote.CurrencyCode,
            quote.ExchangeRate, $"Created from supplier quotation {quote.DocNo}",
            lines, quote.PartyCode, quote.DeliveryDate, null,
            quote.CostCenter, quote.BranchName, WarehouseName: quote.WarehouseName);
        var orderId = await _orders.CreateLinkedDraftAsync(
            CommercialDocSide.Purchase, draft, CommercialDocKinds.SupplierQuotation,
            supplierQuotationId, userId, cancellationToken).ConfigureAwait(false);
        return (orderId, docNo);
    }

    private static void ValidateOrderDraft(CommercialDocumentDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (!IsOrderKind(draft.DocKind))
            throw new InvalidOperationException($"Document kind '{draft.DocKind}' is not an order kind.");
        if (draft.DeliveryDate is { } delivery && delivery < draft.DocDate)
            throw new InvalidOperationException("Delivery date cannot precede the order date.");
        if (draft.ValidTill is { } validTill && validTill < draft.DocDate)
            throw new InvalidOperationException("Validity date cannot precede the document date.");
        foreach (var line in draft.Lines)
        {
            if (line.Qty <= 0m)
                throw new InvalidOperationException("Order line quantities must be greater than zero.");
            if (line.UnitPrice < 0m)
                throw new InvalidOperationException("Order line prices cannot be negative.");
        }
    }
}
