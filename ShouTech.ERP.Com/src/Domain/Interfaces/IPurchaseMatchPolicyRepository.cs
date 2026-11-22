using ShouTech.Domain.Entities;

namespace ShouTech.Domain.Interfaces;

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
