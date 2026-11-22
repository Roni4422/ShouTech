using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ShouTech.Domain.Entities;

namespace ShouTech.Domain.Interfaces;

/// <summary>
/// Persistence for commercial purchase documents (purchase_documents /
/// purchase_document_lines) and of the purchase-side chain links stored in
/// document_links. Mirrors the sales side of ICommercialDocumentRepository
/// (same records, same document_links table), but keyed to the
/// purchase_documents family so the buying chain cannot read or write the
/// sales tables.
/// </summary>
public interface IPurchaseDocumentRepository
{
    /// <summary>Loads a purchase document header with its lines, or null when missing.</summary>
    Task<CommercialDocumentSnapshot?> FindPurchaseByIdAsync(long docId, CancellationToken cancellationToken = default);

    /// <summary>Inserts a purchase document draft (header + lines) and returns its database id.</summary>
    Task<long> CreatePurchaseDraftAsync(CommercialDocumentDraft draft, string userId, CancellationToken cancellationToken = default);

    /// <summary>Moves a purchase document from Draft to Posted with its stock / ledger / GL side effects; throws when not a draft.</summary>
    Task PostPurchaseAsync(long docId, string userId, CancellationToken cancellationToken = default);

    /// <summary>Records that the target document was created from the source document.</summary>
    Task AddLinkAsync(Guid companyId, string sourceDocKind, long sourceDocId, string targetDocKind, long targetDocId, string userId, CancellationToken cancellationToken = default);

    /// <summary>Returns the chain links originating from a source purchase document.</summary>
    Task<IReadOnlyList<CommercialDocumentLink>> GetPurchaseLinksFromAsync(Guid companyId, long sourceDocId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sums the line quantities of posted target documents (targetDocKind) linked from the
    /// source purchase document, grouped by material id, for remaining-fulfillment checks.
    /// </summary>
    Task<IReadOnlyDictionary<long, decimal>> GetPurchaseFulfilledQuantitiesAsync(Guid companyId, long sourceDocId, string targetDocKind, CancellationToken cancellationToken = default);
}
