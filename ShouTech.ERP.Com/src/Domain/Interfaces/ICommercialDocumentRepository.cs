using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ShouTech.Domain.Entities;

namespace ShouTech.Domain.Interfaces;

/// <summary>
/// Persistence for commercial sales documents (sales_documents / sales_document_lines)
/// and the cross-document chain links stored in document_links.
/// </summary>
public interface ICommercialDocumentRepository
{
    /// <summary>Loads a sales document header with its lines, or null when missing.</summary>
    Task<CommercialDocumentSnapshot?> FindSalesByIdAsync(long docId, CancellationToken cancellationToken = default);

    /// <summary>Inserts a sales document draft (header + lines) and returns its database id.</summary>
    Task<long> CreateSalesDraftAsync(CommercialDocumentDraft draft, string userId, CancellationToken cancellationToken = default);

    /// <summary>Creates a sales draft and its source-document link atomically in one database transaction.</summary>
    Task<long> CreateSalesDraftWithLinkAsync(CommercialDocumentDraft draft, string sourceDocKind, long sourceDocId, string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a sales draft linked from MULTIPLE source documents in
    /// one database transaction (W5 consolidated invoices): every source
    /// is re-validated inside the transaction (posted, expected kind,
    /// same company), one document_links row is written per source, a
    /// delivery-sourced invoice additionally gains the SO → SAL link of
    /// each delivery's parent order, and the stored per_delivered /
    /// per_billed of every affected sales order is refreshed in the
    /// same transaction.
    /// </summary>
    Task<long> CreateSalesDraftWithLinksAsync(CommercialDocumentDraft draft, IReadOnlyList<CommercialDocumentSourceRef> sources, string userId, CancellationToken cancellationToken = default);

    /// <summary>Moves a sales document from Draft to Posted; throws when not a draft.</summary>
    /// <summary>Posts a sales draft. The optional W7a price-override
    /// context is supplied only when the posting user holds
    /// SALES.PRICE.OVERRIDE / SALES.DISCOUNT.OVERRIDE; breaches are then
    /// audited in spr_price_overrides instead of blocking (SAL-7018/7019/7020).</summary>
    Task PostSalesAsync(long docId, string userId, CancellationToken cancellationToken = default, PriceOverrideContext? priceOverride = null);

    /// <summary>
    /// Reverses a posted chain document (DLV or SAL) in one transaction:
    /// compensating stock movements at the original movement cost (DLV),
    /// a compensating journal for the document's GL entry, a mirrored
    /// party-ledger entry (SAL), the document is marked Cancelled and a
    /// document_reversals audit row is recorded. Reason and actor are
    /// mandatory. Guards: SAL-7005 (posted downstream invoice on a DLV),
    /// SAL-7006 (paid amount or payment allocations on a SAL),
    /// SAL-7007 (document kind/status is not reversible).
    /// </summary>
    Task ReverseChainDocumentAsync(Guid companyId, long docId, string reason, string actor, CancellationToken cancellationToken = default);

    // ------------------------------------------------- lifecycle (W23, mig 098)

    /// <summary>
    /// Amends a quotation or sales order as a NEW REVISION in one
    /// transaction: the current version (header + lines) is preserved in
    /// sau_document_revisions, the document is rewritten from
    /// <paramref name="amended"/>, revision_no is incremented and the
    /// approval state resets to NotRequired. Allowed for drafts (incl.
    /// approved-but-unposted) and for POSTED orders with no posted
    /// downstream document and no journal entry; a posted order WITH a
    /// journal or posted downstream raises SAL-7016, as does any other
    /// status. A stale <paramref name="expectedRevision"/> (someone else
    /// amended first) raises SAL-7017. Returns the new revision number.
    /// </summary>
    Task<int> AmendSalesDocumentAsync(long docId, CommercialDocumentDraft amended, int expectedRevision, string userId, string? reason = null, CancellationToken cancellationToken = default);

    /// <summary>Returns the preserved prior revisions of a document, oldest first.</summary>
    Task<IReadOnlyList<DocumentRevisionSnapshot>> GetRevisionsAsync(long docId, CancellationToken cancellationToken = default);

    /// <summary>Moves a draft QUO/SO/SAL document to approval Pending (history recorded).</summary>
    Task SubmitForApprovalAsync(long docId, string userId, CancellationToken cancellationToken = default);

    /// <summary>Approves a Pending document (history recorded).</summary>
    Task ApproveDocumentAsync(long docId, string userId, CancellationToken cancellationToken = default);

    /// <summary>Rejects a Pending document; the reason is mandatory and stored.</summary>
    Task RejectDocumentAsync(long docId, string userId, string reason, CancellationToken cancellationToken = default);

    /// <summary>Returns the approval/amendment history of a document, oldest first.</summary>
    Task<IReadOnlyList<ApprovalHistoryEntry>> GetApprovalHistoryAsync(long docId, CancellationToken cancellationToken = default);

    /// <summary>True when the company policy (sau_sales_settings) requires approval before posting.</summary>
    Task<bool> IsApprovalRequiredAsync(Guid companyId, CancellationToken cancellationToken = default);

    /// <summary>Sets the company approval policy flag (sau_sales_settings).</summary>
    Task SetApprovalRequiredAsync(Guid companyId, bool required, string userId, CancellationToken cancellationToken = default);

    /// <summary>Records that the target document was created from the source document.</summary>
    Task AddLinkAsync(Guid companyId, string sourceDocKind, long sourceDocId, string targetDocKind, long targetDocId, string userId, CancellationToken cancellationToken = default);

    /// <summary>Returns the chain links originating from a source sales document.</summary>
    Task<IReadOnlyList<CommercialDocumentLink>> GetSalesLinksFromAsync(Guid companyId, long sourceDocId, CancellationToken cancellationToken = default);

    /// <summary>Returns the chain links pointing TO a target sales document (its sources).</summary>
    Task<IReadOnlyList<CommercialDocumentLink>> GetSalesLinksToAsync(Guid companyId, long targetDocId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sums the line quantities of posted target documents (targetDocKind) linked from the
    /// source document, grouped by material id. Used to compute remaining fulfillment quantities.
    /// </summary>
    Task<IReadOnlyDictionary<long, decimal>> GetFulfilledQuantitiesAsync(Guid companyId, long sourceDocId, string targetDocKind, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sums the line quantities of posted target documents (targetDocKind) linked from the
    /// source document, keyed by SOURCE line number. Chain-created target lines persist
    /// line_no = source line number, so duplicate-material source lines are tracked
    /// independently; quantities on legacy target lines whose line number matches no
    /// source line are distributed over the source lines of the same material in
    /// line-number order (the purchase branch's fallback in OrderDocumentRepository).
    /// </summary>
    Task<IReadOnlyDictionary<int, decimal>> GetFulfilledQuantitiesBySourceLineAsync(Guid companyId, long sourceDocId, string targetDocKind, CancellationToken cancellationToken = default);
}

// ---- merged from src/Domain/Interfaces/IOrderDocumentRepository.cs ----
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

    /// <summary>P1: RFQ supplier invitations (migration 112).</summary>
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

// ---- merged from src/Domain/Interfaces/IPurchaseRequisitionWorkflowRepository.cs ----
public interface IPurchaseRequisitionWorkflowRepository
{
    Task<PurchaseRequisitionWorkflow?> GetAsync(Guid companyId,long requisitionId,CancellationToken ct=default);
    Task SubmitAsync(Guid companyId,long requisitionId,IReadOnlyList<PurchaseRequisitionApprovalStep> steps,string actor,CancellationToken ct=default);
    Task DecideAsync(Guid companyId,long requisitionId,long approvalId,string approver,bool approve,string? reason,CancellationToken ct=default);
    Task CancelAsync(Guid companyId,long requisitionId,string actor,string reason,CancellationToken ct=default);
    Task CloseAsync(Guid companyId,long requisitionId,string actor,CancellationToken ct=default);
    Task MarkConvertedToRfqAsync(Guid companyId,long requisitionId,long rfqId,string actor,CancellationToken ct=default);
    Task<bool> BeginConversionAsync(Guid companyId,long requisitionId,string actor,CancellationToken ct=default);
    Task AbortConversionAsync(Guid companyId,long requisitionId,string actor,string reason,CancellationToken ct=default);
    Task MarkConvertedAsync(Guid companyId,long requisitionId,long purchaseOrderId,string actor,CancellationToken ct=default);
}

// ---- merged from src/Domain/Interfaces/IPurchaseDocumentAttachmentRepository.cs ----
public interface IPurchaseDocumentAttachmentRepository
{
    Task AddAsync(PurchaseDocumentAttachment attachment,CancellationToken ct=default);
    Task<IReadOnlyList<PurchaseDocumentAttachment>> ListAsync(Guid companyId,long documentId,CancellationToken ct=default);
    /// <summary>Generalized owner listing (migration 098): sales documents
    /// (SALDOC, ownerRef = document id) and parties (PARTY, ownerRef = party id).</summary>
    Task<IReadOnlyList<PurchaseDocumentAttachment>> ListByOwnerAsync(Guid companyId,string ownerKind,string ownerRef,CancellationToken ct=default);
    Task<PurchaseDocumentAttachment?> FindAsync(Guid companyId,Guid attachmentId,CancellationToken ct=default);
    Task SoftDeleteAsync(Guid companyId,Guid attachmentId,string actor,CancellationToken ct=default);
    Task RestoreAsync(Guid companyId,Guid attachmentId,CancellationToken ct=default);
    /// <summary>True when a live party with this id exists in the company
    /// (parties are keyed by company CODE; resolved from the Guid here).</summary>
    Task<bool> PartyExistsAsync(Guid companyId,string partyId,CancellationToken ct=default);
}

// ---- merged from src/Domain/Interfaces/IPurchaseMatchPolicyRepository.cs ----
/// <summary>
/// Company-scoped persistence for purchase three-way-match policy and
/// append-only release decisions. Callers must authorize policy changes and
/// releases with the dedicated PURCH.MATCH permissions before invoking writes.
/// </summary>
public interface IPurchaseMatchPolicyRepository
{
    Task<PurchaseMatchPolicy?> GetPolicyAsync(string companyCode, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PurchaseMatchEvaluationSnapshot>> GetRecentEvaluationsAsync(
        string companyCode, int take = 50, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves a policy using optimistic concurrency. expectedVersion is zero
    /// when creating the first policy; otherwise it must equal the current
    /// persisted policy version.
    /// </summary>
    Task SavePolicyAsync(
        string companyCode,
        PurchaseMatchPolicy policy,
        int expectedVersion,
        string actor,
        CancellationToken cancellationToken = default);

    Task<PurchaseMatchEvaluationSnapshot> ReevaluateLatestAsync(
        string companyCode,
        long invoiceDocId,
        string actor,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases only the latest Blocked evaluation for this invoice.
    /// The caller is responsible for checking PURCH.MATCH.OVERRIDE.
    /// </summary>
    Task<Guid> RecordOverrideAsync(
        string companyCode,
        long invoiceDocId,
        Guid evaluationId,
        string actor,
        string reason,
        CancellationToken cancellationToken = default);
}

// ---- merged from src/Domain/Interfaces/IPurchaseLandedCostRepository.cs ----
/// <summary>
/// Transactional supplier landed-cost vouchers (list, draft, post, reversal)
/// over provider-specific tables. Implemented by the Infrastructure layer;
/// the Application layer depends only on this contract.
/// </summary>
public interface IPurchaseLandedCostRepository
{
    Task<IReadOnlyList<PurchaseLandedCostSummary>> ListAsync(
        Guid companyId, string? search = null, CancellationToken cancellationToken = default);

    /// <summary>Posted purchase receipts of the company, for the voucher receipt picker.</summary>
    Task<IReadOnlyList<PurchaseLandedCostReceiptOption>> ListReceiptsAsync(
        Guid companyId, CancellationToken cancellationToken = default);

    /// <summary>Eligible (stock-valued) lines of one posted receipt, for line-level selection.</summary>
    Task<IReadOnlyList<PurchaseLandedCostReceiptLineOption>> ListReceiptLinesAsync(
        Guid companyId, long receiptDocumentId, CancellationToken cancellationToken = default);

    /// <summary>Active company parties, for the per-charge payee picker.</summary>
    Task<IReadOnlyList<PurchaseLandedCostPayeeOption>> ListPayeesAsync(
        Guid companyId, CancellationToken cancellationToken = default);

    /// <summary>Posted supplier documents a Linked charge may consume
    /// (documents already consumed by a posted voucher are excluded).</summary>
    Task<IReadOnlyList<PurchaseLandedCostLinkableDocumentOption>> ListLinkableDocumentsAsync(
        Guid companyId, CancellationToken cancellationToken = default);

    /// <summary>Creates a draft voucher with its charges and receipt-line allocations in one transaction.
    /// A voucher may draw lines from several receipts (migration 108).</summary>
    Task<Guid> CreateDraftAsync(
        Guid companyId, IReadOnlyList<PurchaseLandedCostReceiptSelection> receipts, string documentNumber, DateOnly documentDate,
        string currencyCode, decimal exchangeRate,
        IReadOnlyList<PurchaseLandedCostChargeDraft> charges, string actor,
        CancellationToken cancellationToken = default);

    /// <summary>Posts the draft: revalues cost layers, records COGS for consumed stock, and posts AP/GL atomically.</summary>
    Task<Guid> PostAsync(
        Guid companyId, Guid landedCostId, string actor,
        CancellationToken cancellationToken = default);

    /// <summary>Creates a mirrored reversal voucher, un-applies the valuation adjustments, and posts the reversing journal atomically.</summary>
    Task<Guid> ReverseAsync(
        Guid companyId, Guid landedCostId, string reversalNumber, DateOnly reversalDate,
        string reason, string actor,
        CancellationToken cancellationToken = default);
}
