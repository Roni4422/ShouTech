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
        CommercialDocKinds.PurchaseOrder or CommercialDocKinds.RequestForQuotation or CommercialDocKinds.PurchaseReceipt or CommercialDocKinds.PurchaseInvoice => CommercialDocSide.Purchase,
        _ => throw new ArgumentException($"Unknown document kind '{docKind}'.", nameof(docKind))
    };

    public static bool IsOrderKind(string docKind) =>
        docKind is CommercialDocKinds.SalesOrder or CommercialDocKinds.PurchaseOrder
            or CommercialDocKinds.SalesQuotation or CommercialDocKinds.RequestForQuotation;

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
