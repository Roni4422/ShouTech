using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ShouTech.Domain.Entities;

namespace ShouTech.Domain.Interfaces;

/// <summary>Which commercial document table family an operation targets.</summary>
public enum CommercialDocSide
{
    Sales,
    Purchase
}

/// <summary>
/// Order-side persistence over the commercial document engine
/// (sales_documents / purchase_documents + document_links).
/// Orders and quotations post no GL and no stock; conversion to an invoice
/// happens in a single transaction (create invoice + party ledger + link).
/// </summary>
public interface IOrderDocumentRepository
{
    Task<CommercialDocumentSnapshot?> FindByIdAsync(CommercialDocSide side, long docId, CancellationToken cancellationToken = default);

    Task<CommercialDocumentSnapshot?> FindByNumberAsync(CommercialDocSide side, Guid companyId, string docKind, string docNo, CancellationToken cancellationToken = default);

    Task<long> CreateDraftAsync(CommercialDocSide side, CommercialDocumentDraft draft, string userId, CancellationToken cancellationToken = default, PriceOverrideContext? priceOverride = null);

    /// <summary>Creates a draft and its source-document link atomically.</summary>
    Task<long> CreateLinkedDraftAsync(CommercialDocSide side, CommercialDocumentDraft draft, string sourceDocKind, long sourceDocId, string userId, CancellationToken cancellationToken = default);

    /// <summary>Replaces header fields and lines of a Draft document; throws when not a draft.</summary>
    Task UpdateDraftAsync(CommercialDocSide side, long docId, CommercialDocumentDraft draft, string userId, CancellationToken cancellationToken = default, PriceOverrideContext? priceOverride = null);

    /// <summary>Posts an order/quotation (status flip only — orders have no GL/stock side effects).</summary>
    Task PostOrderAsync(CommercialDocSide side, long docId, string userId, CancellationToken cancellationToken = default, CreditOverrideContext? creditOverride = null, PriceOverrideContext? priceOverride = null);

    /// <summary>Creates and posts a partial goods receipt against a posted purchase order. Stock, GRNI journal, receipt document, and source link commit atomically.</summary>
    /// <summary>Creates a posted supplier invoice from received quantities, records line-level receipt matching and price variance, and clears GRNI atomically.</summary>
    Task<long> CreatePurchaseInvoiceAsync(Guid companyId, long purchaseOrderId, string invoiceDocNo, DateOnly invoiceDate, IReadOnlyList<CommercialLineDraft> invoiceLines, string userId, string? supplierBillNo = null, DateOnly? billDate = null, CancellationToken cancellationToken = default);
    Task<long> CreatePurchaseReceiptAsync(Guid companyId, long purchaseOrderId, string receiptDocNo, DateOnly receiptDate, IReadOnlyList<CommercialLineDraft> receivedLines, string userId, string? deliveryNoteNo = null, CancellationToken cancellationToken = default);

    /// <summary>P6: creates a new DRAFT purchase order copying a cancelled PO, linked via amended_from_doc_id + document_links.</summary>
    Task<long> AmendPurchaseOrderAsync(Guid companyId, long cancelledPoId, string newDocNo, DateOnly docDate, string userId, CancellationToken cancellationToken = default);

    /// <summary>P1: RFQ supplier invitations (migration 097).</summary>
    Task<IReadOnlyList<RfqInvitation>> GetRfqInvitationsAsync(Guid companyId, long rfqDocId, CancellationToken cancellationToken = default);
    Task AddRfqInvitationsAsync(Guid companyId, long rfqDocId, IReadOnlyList<(long PartyId, string? PartyCode, string? PartyName)> suppliers, string userId, CancellationToken cancellationToken = default);
    Task SetRfqInvitationStatusAsync(Guid companyId, long rfqDocId, long partyId, string status, string userId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PurchaseReceiptReturnableLine>> GetPurchaseReceiptReturnableLinesAsync(Guid companyId, long receiptId, CancellationToken cancellationToken = default);

    /// <summary>Creates a posted pre-invoice goods return against an original receipt, consuming only quantities not yet invoiced or returned.</summary>
    Task<long> CreatePurchaseReceiptReturnAsync(Guid companyId, long receiptId, string returnDocNo, DateOnly returnDate, IReadOnlyList<CommercialLineDraft> returnedLines, string userId, string? reason = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a document to Closed / Cancelled / Lost / Expired with transition
    /// guards. A move to Cancelled persists <paramref name="cancelReason"/>
    /// together with the actor and timestamp (cancelled_by / cancelled_at).
    /// </summary>
    Task SetStatusAsync(CommercialDocSide side, long docId, string newStatus, string userId, CancellationToken cancellationToken = default, string? cancelReason = null);

    /// <summary>Next document number for the kind, formatted PREFIX-NNNN (e.g. SO-1001).</summary>
    Task<string> NextDocNoAsync(CommercialDocSide side, Guid companyId, string docKind, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CommercialDocumentSummary>> ListAsync(CommercialDocSide side, Guid companyId, string? docKind, string? status, string? search, DateOnly? from, DateOnly? to, int limit = 500, CancellationToken cancellationToken = default);

    /// <summary>Per-source-line ordered/fulfilled/billed quantities plus qty-weighted percentages.</summary>
    Task<OrderProgress> GetProgressAsync(CommercialDocSide side, long docId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Recomputes the stored per_delivered / per_billed columns of a sales
    /// document from the same math as <see cref="GetProgressAsync"/> and
    /// persists them in its own transaction. Called wherever chain documents
    /// are created, posted, or converted so the stored progress never drifts
    /// from the computed progress.
    /// </summary>
    Task RecalculateStoredProgressAsync(long salesDocId, CancellationToken cancellationToken = default);


    /// <summary>
    /// Converts a posted order into a posted invoice containing only the
    /// remaining unbilled quantities, writes the party-ledger side effect and
    /// the document link — all in one transaction. Returns the invoice id.
    /// </summary>
    Task<long> ConvertToInvoiceAsync(CommercialDocSide side, long orderDocId, string invoiceDocNo, DateOnly invoiceDate, string userId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CommercialDocumentLink>> GetLinksFromAsync(Guid companyId, long sourceDocId, CancellationToken cancellationToken = default);

    // ------------------------------------------------------------- picking (W4)

    /// <summary>Creates a Draft picking for a Posted sales order, lines
    /// pre-filled with the remaining-to-deliver quantity per source line.
    /// Re-validates kind/status inside the transaction.</summary>
    Task<long> CreatePickingFromOrderAsync(long salesOrderId, string userId, CancellationToken cancellationToken = default);

    Task<SalesPickingSnapshot?> GetPickingAsync(long pickingId, CancellationToken cancellationToken = default);

    /// <summary>The latest Confirmed picking for an order, or null.</summary>
    Task<SalesPickingSnapshot?> FindConfirmedPickingAsync(Guid companyId, long salesOrderId, CancellationToken cancellationToken = default);

    /// <summary>Confirms a Draft picking with the picked quantity per
    /// source line. A picked quantity beyond the order's remaining-to-
    /// deliver quantity fails with SAL-7012 (PickingExceedsRemaining).</summary>
    Task ConfirmPickingAsync(long pickingId, IReadOnlyDictionary<int, decimal> pickedBySourceLine, string userId, CancellationToken cancellationToken = default);

    Task CancelPickingAsync(long pickingId, string userId, CancellationToken cancellationToken = default);
}
