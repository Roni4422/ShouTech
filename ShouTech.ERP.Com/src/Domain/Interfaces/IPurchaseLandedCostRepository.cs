using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ShouTech.Domain.Entities;

namespace ShouTech.Domain.Interfaces;

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
