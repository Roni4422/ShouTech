using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using ShouTech.Domain;
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

    /// <summary>
    /// Sales-commission generation hook (Phase 09, W7b), invoked after a
    /// chain SALES INVOICE posts: (companyKey, docId, userId) -&gt; task.
    /// Set by the composition root (AppHost) to CommService generation.
    /// The hook is FAIL-SOFT by contract: the delegate catches and
    /// reports its own failures, because a commission problem must never
    /// break invoice posting. Null (tests, headless hosts) disables
    /// generation on this path; the commission window's period actions
    /// and the native InvoiceWin hook cover the other paths.
    /// </summary>
    public Func<string, long, string, CancellationToken, Task>? CommissionHook { get; set; }

    public static CommercialDocSide SideFor(string docKind) => docKind switch
    {
        CommercialDocKinds.SalesOrder or CommercialDocKinds.SalesQuotation or CommercialDocKinds.DeliveryNote or CommercialDocKinds.SalesInvoice => CommercialDocSide.Sales,
        CommercialDocKinds.PurchaseOrder or CommercialDocKinds.RequestForQuotation or CommercialDocKinds.SupplierQuotation or CommercialDocKinds.PurchaseReceipt or CommercialDocKinds.PurchaseReceiptReturn or CommercialDocKinds.PurchaseInvoice or CommercialDocKinds.PurchaseRequisition => CommercialDocSide.Purchase,
        _ => throw new ArgumentException($"Unknown document kind '{docKind}'.", nameof(docKind))
    };

    public static bool IsOrderKind(string docKind) =>
        docKind is CommercialDocKinds.SalesOrder or CommercialDocKinds.PurchaseOrder
            or CommercialDocKinds.SalesQuotation or CommercialDocKinds.RequestForQuotation or CommercialDocKinds.SupplierQuotation;

    public async Task<long> CreateOrderAsync(CommercialDocumentDraft draft, string userId, CancellationToken cancellationToken = default)
        => await CreateOrderAsync(draft, userId, null, cancellationToken).ConfigureAwait(false);

    /// <summary>Creates an order draft with an optional authorized W7a
    /// price override (supplied only when the user holds
    /// SALES.PRICE.OVERRIDE / SALES.DISCOUNT.OVERRIDE; audited by the
    /// guard in spr_price_overrides instead of blocking SAL-7018/7019/7020).</summary>
    public async Task<long> CreateOrderAsync(CommercialDocumentDraft draft, string userId, PriceOverrideContext? priceOverride, CancellationToken cancellationToken = default)
    {
        ValidateOrderDraft(draft);
        return await _orders.CreateDraftAsync(SideFor(draft.DocKind), draft, userId, cancellationToken, priceOverride).ConfigureAwait(false);
    }

    public async Task UpdateOrderAsync(long docId, CommercialDocumentDraft draft, string userId, CancellationToken cancellationToken = default)
        => await UpdateOrderAsync(docId, draft, userId, null, cancellationToken).ConfigureAwait(false);

    /// <summary>Updates an order draft with an optional authorized W7a price override (see CreateOrderAsync).</summary>
    public async Task UpdateOrderAsync(long docId, CommercialDocumentDraft draft, string userId, PriceOverrideContext? priceOverride, CancellationToken cancellationToken = default)
    {
        ValidateOrderDraft(draft);
        await _orders.UpdateDraftAsync(SideFor(draft.DocKind), docId, draft, userId, cancellationToken, priceOverride).ConfigureAwait(false);
    }

    public Task<CommercialDocumentSnapshot?> FindByIdAsync(CommercialDocSide side, long docId, CancellationToken cancellationToken = default)
        => _orders.FindByIdAsync(side, docId, cancellationToken);

    public Task<CommercialDocumentSnapshot?> FindByNumberAsync(CommercialDocSide side, Guid companyId, string docKind, string docNo, CancellationToken cancellationToken = default)
        => _orders.FindByNumberAsync(side, companyId, docKind, docNo, cancellationToken);

    public Task<string> NextDocNoAsync(CommercialDocSide side, Guid companyId, string docKind, CancellationToken cancellationToken = default)
        => _orders.NextDocNoAsync(side, companyId, docKind, cancellationToken);

    public async Task UpdatePurchaseRequisitionDraftAsync(long requisitionId,CommercialDocumentDraft draft,string userId,CancellationToken cancellationToken=default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requisitionId);
        if(draft.DocKind!=CommercialDocKinds.PurchaseRequisition||draft.PartyId is not null)
            throw new InvalidOperationException("Only a supplier-neutral purchase requisition draft can be updated.");
        await _orders.UpdateDraftAsync(CommercialDocSide.Purchase,requisitionId,draft,userId,cancellationToken).ConfigureAwait(false);
    }

    public Task PostOrderAsync(CommercialDocSide side, long docId, string userId, CancellationToken cancellationToken = default)
        => _orders.PostOrderAsync(side, docId, userId, cancellationToken);

    /// <summary>
    /// Posts an order with an optional authorized credit override (W1):
    /// the caller (the window, where the session's permissions are
    /// visible) supplies the override context only when the posting user
    /// holds SALES.CREDIT.OVERRIDE; the guard then audits the breach in
    /// credit_overrides instead of hard-blocking with ACC-5006.
    /// </summary>
    public Task PostOrderAsync(CommercialDocSide side, long docId, string userId, CreditOverrideContext? creditOverride, CancellationToken cancellationToken = default)
        => _orders.PostOrderAsync(side, docId, userId, cancellationToken, creditOverride);

    /// <summary>Posts an order with authorized credit AND W7a price
    /// overrides (each supplied only when the posting user holds the
    /// matching permission; both guards audit their breaches).</summary>
    public Task PostOrderAsync(CommercialDocSide side, long docId, string userId, CreditOverrideContext? creditOverride, PriceOverrideContext? priceOverride, CancellationToken cancellationToken = default)
        => _orders.PostOrderAsync(side, docId, userId, cancellationToken, creditOverride, priceOverride);

    /// <summary>
    /// Creates a draft delivery note from the remaining quantities on a posted
    /// sales order. The chain repository records the source link atomically;
    /// posting is a separate operation so the warehouse can review the draft.
    /// </summary>
    public async Task<(long DeliveryId, string DeliveryDocNo)> CreateSalesDeliveryAsync(
        long salesOrderId,
        DateOnly deliveryDate,
        string userId,
        IReadOnlyDictionary<int, decimal>? quantityBySourceLine = null,
        long? warehouseId = null,
        CancellationToken cancellationToken = default,
        decimal deliveryCost = 0m)
    {
        if (deliveryCost < 0m) throw new InvalidOperationException("Delivery cost cannot be negative.");
        if (salesOrderId <= 0) throw new ArgumentOutOfRangeException(nameof(salesOrderId));
        var order = await _chain.FindSalesByIdAsync(salesOrderId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Sales order was not found.");
        if (order.DocKind != CommercialDocKinds.SalesOrder)
            throw new AppError(ErrorCodes.ChainTransitionNotAllowed,
                $"Documents cannot be converted from '{order.DocKind}' to '{CommercialDocKinds.DeliveryNote}'.",
                order.DocKind, CommercialDocKinds.DeliveryNote);
        if (order.Status != CommercialDocumentStatus.Posted)
            throw new AppError(ErrorCodes.ChainSourceNotPosted,
                $"The source document '{order.DocNo}' must be posted before it can be converted.",
                order.DocNo);

        var deliveryDocNo = await _orders.NextDocNoAsync(
            CommercialDocSide.Sales, order.CompanyId, CommercialDocKinds.DeliveryNote, cancellationToken).ConfigureAwait(false);
        var chain = new SalesChainService(_chain);
        var deliveryId = await chain.CreateFromSourceAsync(
            salesOrderId, deliveryDocNo, deliveryDate, userId,
            quantityBySourceLine, warehouseId, cancellationToken, deliveryCost).ConfigureAwait(false);
        return (deliveryId, deliveryDocNo);
    }

    // ------------------------------------------------------------- picking (W4)

    /// <summary>
    /// Stages a warehouse picking for a posted sales order (spec §4):
    /// lines pre-filled with the remaining-to-deliver quantity per
    /// source line. The picking posts nothing; confirming it records
    /// what was actually picked, and a confirmed picking feeds
    /// delivery-note creation in the delivery window.
    /// </summary>
    public async Task<long> CreatePickingFromOrderAsync(long salesOrderId, string userId, CancellationToken cancellationToken = default)
    {
        if (salesOrderId <= 0) throw new ArgumentOutOfRangeException(nameof(salesOrderId));
        var order = await _orders.FindByIdAsync(CommercialDocSide.Sales, salesOrderId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Sales order was not found.");
        if (order.DocKind != CommercialDocKinds.SalesOrder)
            throw new AppError(ErrorCodes.ChainTransitionNotAllowed,
                $"Documents cannot be converted from '{order.DocKind}' to a picking.",
                order.DocKind, "Picking");
        if (order.Status != CommercialDocumentStatus.Posted)
            throw new AppError(ErrorCodes.ChainSourceNotPosted,
                $"The source document '{order.DocNo}' must be posted before it can be converted.",
                order.DocNo);
        return await _orders.CreatePickingFromOrderAsync(salesOrderId, userId, cancellationToken).ConfigureAwait(false);
    }

    public Task<SalesPickingSnapshot?> GetPickingAsync(long pickingId, CancellationToken cancellationToken = default)
        => _orders.GetPickingAsync(pickingId, cancellationToken);

    /// <summary>The order's latest confirmed picking, whose picked
    /// quantities delivery creation defaults from; null when none.</summary>
    public Task<SalesPickingSnapshot?> FindConfirmedPickingAsync(Guid companyId, long salesOrderId, CancellationToken cancellationToken = default)
        => _orders.FindConfirmedPickingAsync(companyId, salesOrderId, cancellationToken);

    /// <summary>Confirms a draft picking. Picking more than the order's
    /// remaining-to-deliver quantity on a line fails with SAL-7012.</summary>
    public Task ConfirmPickingAsync(long pickingId, IReadOnlyDictionary<int, decimal> pickedBySourceLine, string userId, CancellationToken cancellationToken = default)
        => _orders.ConfirmPickingAsync(pickingId, pickedBySourceLine, userId, cancellationToken);

    public Task CancelPickingAsync(long pickingId, string userId, CancellationToken cancellationToken = default)
        => _orders.CancelPickingAsync(pickingId, userId, cancellationToken);

    /// <summary>True when a posted sales invoice is linked to the
    /// delivery note — the gate the delivery desk's Returned flow uses
    /// to decide between reversing the DLV and the invoice-return
    /// route.</summary>
    public async Task<bool> HasPostedSalesInvoiceAsync(long deliveryDocId, CancellationToken cancellationToken = default)
    {
        if (deliveryDocId <= 0) throw new ArgumentOutOfRangeException(nameof(deliveryDocId));
        var delivery = await _chain.FindSalesByIdAsync(deliveryDocId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Delivery note was not found.");
        var links = await _chain.GetSalesLinksFromAsync(delivery.CompanyId, deliveryDocId, cancellationToken).ConfigureAwait(false);
        foreach (var link in links.Where(link => link.TargetDocKind == CommercialDocKinds.SalesInvoice))
        {
            var invoice = await _chain.FindSalesByIdAsync(link.TargetDocId, cancellationToken).ConfigureAwait(false);
            if (invoice is not null && invoice.Status == CommercialDocumentStatus.Posted)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Creates a draft sales invoice from a posted delivery note. The invoice
    /// inherits the delivered lines and carries a DLV-to-SAL document link.
    /// </summary>
    public async Task<(long InvoiceId, string InvoiceDocNo)> CreateSalesInvoiceFromDeliveryAsync(
        long deliveryDocId,
        DateOnly invoiceDate,
        string userId,
        IReadOnlyDictionary<int, decimal>? quantityBySourceLine = null,
        CancellationToken cancellationToken = default)
    {
        if (deliveryDocId <= 0) throw new ArgumentOutOfRangeException(nameof(deliveryDocId));
        var delivery = await _chain.FindSalesByIdAsync(deliveryDocId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Delivery note was not found.");
        if (delivery.DocKind != CommercialDocKinds.DeliveryNote)
            throw new AppError(ErrorCodes.ChainTransitionNotAllowed,
                $"Documents cannot be converted from '{delivery.DocKind}' to '{CommercialDocKinds.SalesInvoice}'.",
                delivery.DocKind, CommercialDocKinds.SalesInvoice);
        if (delivery.Status != CommercialDocumentStatus.Posted)
            throw new AppError(ErrorCodes.ChainSourceNotPosted,
                $"The source document '{delivery.DocNo}' must be posted before it can be converted.",
                delivery.DocNo);

        // Partial invoicing (sales cycle B4, gap G14): a delivery may be
        // billed by several invoices over time, so a posted invoice no
        // longer blocks creating the next one — the posting path
        // allocates the delivery cost pro-rata and refuses quantities
        // beyond what was delivered (SAL-7009). Only a duplicate DRAFT
        // for the same delivery is blocked: two open drafts would race
        // for the same uninvoiced quantity.
        var existingLinks = await _chain.GetSalesLinksFromAsync(
            delivery.CompanyId, deliveryDocId, cancellationToken).ConfigureAwait(false);
        foreach (var existingLink in existingLinks.Where(link =>
                     link.TargetDocKind == CommercialDocKinds.SalesInvoice))
        {
            var existingInvoice = await _chain.FindSalesByIdAsync(
                existingLink.TargetDocId, cancellationToken).ConfigureAwait(false);
            if (existingInvoice is not null &&
                existingInvoice.Status == CommercialDocumentStatus.Draft)
                throw new InvalidOperationException(
                    $"Delivery note '{delivery.DocNo}' already has a draft sales invoice '{existingInvoice.DocNo}'. Post or cancel it before creating another.");
        }

        var invoiceDocNo = await _orders.NextDocNoAsync(
            CommercialDocSide.Sales, delivery.CompanyId, CommercialDocKinds.SalesInvoice, cancellationToken).ConfigureAwait(false);
        var chain = new SalesChainService(_chain);
        var invoiceId = await chain.CreateFromSourceAsync(
            deliveryDocId, invoiceDocNo, invoiceDate, userId, quantityBySourceLine, null, cancellationToken).ConfigureAwait(false);
        return (invoiceId, invoiceDocNo);
    }

    /// <summary>Posts a sales-chain draft, including the DLV-linked invoice accounting path.</summary>
    public async Task PostSalesChainDocumentAsync(long documentId, string userId, CancellationToken cancellationToken = default)
        => await PostSalesChainDocumentAsync(documentId, userId, null, cancellationToken).ConfigureAwait(false);

    /// <summary>Posts a sales-chain draft with an optional authorized
    /// W7a price override (SAL-7018/7019/7020 audited instead of blocked).</summary>
    public async Task PostSalesChainDocumentAsync(long documentId, string userId, PriceOverrideContext? priceOverride, CancellationToken cancellationToken = default)
    {
        await _chain.PostSalesAsync(documentId, userId, cancellationToken, priceOverride).ConfigureAwait(false);
        await RefreshStoredChainProgressAsync(documentId, cancellationToken).ConfigureAwait(false);
        if (CommissionHook is not null)
        {
            var posted = await _chain.FindSalesByIdAsync(documentId, cancellationToken).ConfigureAwait(false);
            if (posted is not null && posted.DocKind == CommercialDocKinds.SalesInvoice)
                await CommissionHook(posted.CompanyId.ToString("D", CultureInfo.InvariantCulture), documentId, userId, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reverses a posted chain delivery note or sales invoice (stock +
    /// GL + party ledger compensated, document cancelled, reversal
    /// audited). The repository enforces the dependency guards
    /// (SAL-7005 downstream invoice, SAL-7006 payments, SAL-7007 state).
    /// </summary>
    public async Task ReverseSalesChainDocumentAsync(long documentId, string reason, string userId, CancellationToken cancellationToken = default)
    {
        if (documentId <= 0) throw new ArgumentOutOfRangeException(nameof(documentId));
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A reversal reason is required.", nameof(reason));
        var document = await _chain.FindSalesByIdAsync(documentId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Sales document was not found.");
        await _chain.ReverseChainDocumentAsync(
            document.CompanyId, documentId, reason, userId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Posts a delivery note through the stock-valuation chain in one transaction.</summary>
    public async Task PostSalesDeliveryAsync(long deliveryDocId, string userId, CancellationToken cancellationToken = default)
    {
        await _chain.PostSalesAsync(deliveryDocId, userId, cancellationToken).ConfigureAwait(false);
        await RefreshStoredChainProgressAsync(deliveryDocId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Refreshes the stored per_delivered / per_billed of the sales order
    /// behind a freshly posted chain document. The posting itself commits
    /// in CommercialDocumentRepository (a different workstream owns that
    /// region); this refresh runs immediately after it through the single
    /// recompute helper, following the document_links chain back to the
    /// order (SAL → its SO link, or SAL → DLV → SO; DLV → SO).
    /// </summary>
    private async Task RefreshStoredChainProgressAsync(long postedDocId, CancellationToken cancellationToken)
    {
        var posted = await _chain.FindSalesByIdAsync(postedDocId, cancellationToken).ConfigureAwait(false);
        if (posted is null)
            return;
        long? orderId = posted.DocKind == CommercialDocKinds.SalesOrder ? posted.DocId : null;
        if (orderId is null)
        {
            var linksTo = await _chain.GetSalesLinksToAsync(posted.CompanyId, postedDocId, cancellationToken).ConfigureAwait(false);
            foreach (var link in linksTo)
            {
                if (link.SourceDocKind == CommercialDocKinds.SalesOrder)
                {
                    orderId = link.SourceDocId;
                    break;
                }
            }
            if (orderId is null)
            {
                foreach (var link in linksTo)
                {
                    if (link.SourceDocKind != CommercialDocKinds.DeliveryNote)
                        continue;
                    var deliveryLinks = await _chain.GetSalesLinksToAsync(posted.CompanyId, link.SourceDocId, cancellationToken).ConfigureAwait(false);
                    foreach (var deliveryLink in deliveryLinks)
                    {
                        if (deliveryLink.SourceDocKind == CommercialDocKinds.SalesOrder)
                        {
                            orderId = deliveryLink.SourceDocId;
                            break;
                        }
                    }
                    if (orderId is not null)
                        break;
                }
            }
        }
        if (orderId is not null)
            await _orders.RecalculateStoredProgressAsync(orderId.Value, cancellationToken).ConfigureAwait(false);
    }

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
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(receiptId);
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

    /// <summary>
    /// Cancels an order/quotation with a documented reason (sales-cycle
    /// specification sections 3/14): the reason is mandatory and is
    /// persisted with the cancelling actor and timestamp.
    /// </summary>
    public async Task CancelOrderAsync(CommercialDocSide side, long docId, string userId, CancellationToken cancellationToken = default, string? reason = null)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("A cancellation reason is required.", nameof(reason));
        await _orders.SetStatusAsync(
            side, docId, CommercialDocumentStatus.Cancelled, userId, cancellationToken, reason.Trim()).ConfigureAwait(false);
    }

    public async Task MarkQuotationLostAsync(long docId, string userId, CancellationToken cancellationToken = default)
    {
        var quotation = await _orders.FindByIdAsync(CommercialDocSide.Sales, docId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Sales quotation was not found.");
        if (quotation.DocKind != CommercialDocKinds.SalesQuotation)
            throw new AppError(ErrorCodes.ChainTransitionNotAllowed,
                $"Documents cannot be converted from '{quotation.DocKind}' to '{CommercialDocumentStatus.Lost}'.",
                quotation.DocKind, CommercialDocumentStatus.Lost);
        if (quotation.Status != CommercialDocumentStatus.Posted)
            throw new AppError(ErrorCodes.ChainSourceNotPosted,
                $"The source document '{quotation.DocNo}' must be posted before it can be converted.",
                quotation.DocNo);
        await _orders.SetStatusAsync(
            CommercialDocSide.Sales, docId, CommercialDocumentStatus.Lost, userId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Expires every Posted sales quotation of the company whose validity
    /// ended before <paramref name="asOf"/> (valid_till &lt; asOf). Each
    /// expiry goes through the repository's guarded Posted → Expired
    /// transition for quotations, so a quotation with posted downstream
    /// documents is left untouched. Returns how many quotations expired.
    /// </summary>
    public async Task<int> ExpirePostedQuotationsAsync(Guid companyId, DateOnly asOf, CancellationToken cancellationToken = default)
    {
        if (companyId == Guid.Empty) throw new ArgumentException("Company is required.", nameof(companyId));
        var posted = await _orders.ListAsync(
            CommercialDocSide.Sales, companyId, CommercialDocKinds.SalesQuotation,
            CommercialDocumentStatus.Posted, null, null, null, int.MaxValue, cancellationToken).ConfigureAwait(false);
        var expired = 0;
        foreach (var quotation in posted)
        {
            if (quotation.ValidTill is not { } validTill || validTill >= asOf)
                continue;
            await _orders.SetStatusAsync(
                CommercialDocSide.Sales, quotation.DocId, CommercialDocumentStatus.Expired,
                "system", cancellationToken).ConfigureAwait(false);
            expired++;
        }
        return expired;
    }

    public async Task MarkSupplierQuotationLostAsync(long docId, string userId, CancellationToken cancellationToken = default)
    {
        var quotation = await _orders.FindByIdAsync(CommercialDocSide.Purchase, docId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Supplier quotation was not found.");
        if (quotation.DocKind != CommercialDocKinds.SupplierQuotation ||
            quotation.Status != CommercialDocumentStatus.Posted)
            throw new InvalidOperationException("Only a posted supplier quotation can be marked as lost.");
        await _orders.SetStatusAsync(
            CommercialDocSide.Purchase, docId, CommercialDocumentStatus.Lost, userId, cancellationToken).ConfigureAwait(false);
    }

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
            throw new AppError(ErrorCodes.ChainTransitionNotAllowed,
                $"Documents cannot be converted from '{quotation.DocKind}' to '{CommercialDocKinds.SalesOrder}'.",
                quotation.DocKind, CommercialDocKinds.SalesOrder);
        if (quotation.ValidTill is { } validTill && validTill < DateOnly.FromDateTime(DateTime.Today))
            throw new AppError(ErrorCodes.QuotationExpired,
                $"Quotation '{quotation.DocNo}' expired on {validTill.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} and can no longer be converted.",
                quotation.DocNo, validTill.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
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
