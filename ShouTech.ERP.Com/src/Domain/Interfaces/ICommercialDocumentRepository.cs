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
