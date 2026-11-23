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

    /// <summary>Creates a draft voucher with its charges and receipt-line allocations in one transaction.</summary>
    Task<Guid> CreateDraftAsync(
        Guid companyId, long receiptDocumentId, string documentNumber, DateOnly documentDate,
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
