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

    Task<long> CreateDraftAsync(CommercialDocSide side, CommercialDocumentDraft draft, string userId, CancellationToken cancellationToken = default);

    /// <summary>Replaces header fields and lines of a Draft document; throws when not a draft.</summary>
    Task UpdateDraftAsync(CommercialDocSide side, long docId, CommercialDocumentDraft draft, string userId, CancellationToken cancellationToken = default);

    /// <summary>Posts an order/quotation (status flip only — orders have no GL/stock side effects).</summary>
    Task PostOrderAsync(CommercialDocSide side, long docId, string userId, CancellationToken cancellationToken = default);

    /// <summary>Creates and posts a partial goods receipt against a posted purchase order. Stock, GRNI journal, receipt document, and source link commit atomically.</summary>
    /// <summary>Creates a posted supplier invoice from received quantities, records line-level receipt matching and price variance, and clears GRNI atomically.</summary>
    Task<long> CreatePurchaseInvoiceAsync(Guid companyId, long purchaseOrderId, string invoiceDocNo, DateOnly invoiceDate, IReadOnlyList<CommercialLineDraft> invoiceLines, string userId, CancellationToken cancellationToken = default);
    Task<long> CreatePurchaseReceiptAsync(Guid companyId, long purchaseOrderId, string receiptDocNo, DateOnly receiptDate, IReadOnlyList<CommercialLineDraft> receivedLines, string userId, CancellationToken cancellationToken = default);

    /// <summary>Moves a document to Closed / Cancelled / Lost with transition guards.</summary>
    Task SetStatusAsync(CommercialDocSide side, long docId, string newStatus, string userId, CancellationToken cancellationToken = default);

    /// <summary>Next document number for the kind, formatted PREFIX-NNNN (e.g. SO-1001).</summary>
    Task<string> NextDocNoAsync(CommercialDocSide side, Guid companyId, string docKind, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CommercialDocumentSummary>> ListAsync(CommercialDocSide side, Guid companyId, string? docKind, string? status, string? search, DateOnly? from, DateOnly? to, int limit = 500, CancellationToken cancellationToken = default);

    /// <summary>Per-material ordered/fulfilled/billed quantities plus qty-weighted percentages.</summary>
    Task<OrderProgress> GetProgressAsync(CommercialDocSide side, long docId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Converts a posted order into a posted invoice containing only the
    /// remaining unbilled quantities, writes the party-ledger side effect and
    /// the document link — all in one transaction. Returns the invoice id.
    /// </summary>
    Task<long> ConvertToInvoiceAsync(CommercialDocSide side, long orderDocId, string invoiceDocNo, DateOnly invoiceDate, string userId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CommercialDocumentLink>> GetLinksFromAsync(Guid companyId, long sourceDocId, CancellationToken cancellationToken = default);
}
