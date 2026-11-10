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

    /// <summary>Moves a sales document from Draft to Posted; throws when not a draft.</summary>
    Task PostSalesAsync(long docId, string userId, CancellationToken cancellationToken = default);

    /// <summary>Records that the target document was created from the source document.</summary>
    Task AddLinkAsync(Guid companyId, string sourceDocKind, long sourceDocId, string targetDocKind, long targetDocId, string userId, CancellationToken cancellationToken = default);

    /// <summary>Returns the chain links originating from a source sales document.</summary>
    Task<IReadOnlyList<CommercialDocumentLink>> GetSalesLinksFromAsync(Guid companyId, long sourceDocId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sums the line quantities of posted target documents (targetDocKind) linked from the
    /// source document, grouped by material id. Used to compute remaining fulfillment quantities.
    /// </summary>
    Task<IReadOnlyDictionary<long, decimal>> GetFulfilledQuantitiesAsync(Guid companyId, long sourceDocId, string targetDocKind, CancellationToken cancellationToken = default);
}
