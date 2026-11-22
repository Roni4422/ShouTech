using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.Application.Services;

/// <summary>
/// Application boundary for purchase match settings and exception releases.
/// Authorization is evaluated on every call so a stale window cannot retain
/// privileges after the current user's permission set changes.
/// </summary>
public sealed class PurchaseMatchService
{
    private readonly IPurchaseMatchPolicyRepository _repository;
    private readonly Func<string, bool> _hasPermission;
    private readonly Func<string, string, string, Task>? _audit;

    public PurchaseMatchService(
        IPurchaseMatchPolicyRepository repository,
        Func<string, bool> hasPermission,
        Func<string, string, string, Task>? audit = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _hasPermission = hasPermission ?? throw new ArgumentNullException(nameof(hasPermission));
        _audit = audit;
    }

    public Task<PurchaseMatchPolicy?> GetPolicyAsync(string companyCode, CancellationToken cancellationToken = default)
    {
        RequirePermission("PURCH.MATCH.POLICY.MANAGE");
        return _repository.GetPolicyAsync(RequireCompany(companyCode), cancellationToken);
    }

    public Task<IReadOnlyList<PurchaseMatchEvaluationSnapshot>> GetRecentEvaluationsAsync(
        string companyCode,
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        RequireAnyPermission("PURCH.VIEW", "PURCH.REPORTS.VIEW",
            "PURCH.MATCH.POLICY.MANAGE", "PURCH.MATCH.OVERRIDE");
        return _repository.GetRecentEvaluationsAsync(RequireCompany(companyCode), take, cancellationToken);
    }

    public async Task SavePolicyAsync(
        string companyCode,
        PurchaseMatchPolicy policy,
        int expectedVersion,
        string actor,
        CancellationToken cancellationToken = default)
    {
        RequirePermission("PURCH.MATCH.POLICY.MANAGE");
        RequireActor(actor);
        await _repository.SavePolicyAsync(
            RequireCompany(companyCode), policy, expectedVersion, actor, cancellationToken).ConfigureAwait(false);
        if (_audit is not null)
            await _audit(actor, "Purchase match policy updated",
                $"Company={companyCode}; Enabled={policy.IsEnabled}; ExpectedVersion={expectedVersion}.").ConfigureAwait(false);
    }

    public async Task<Guid> ReleaseBlockedEvaluationAsync(
        string companyCode,
        long invoiceDocId,
        Guid evaluationId,
        string actor,
        string reason,
        CancellationToken cancellationToken = default)
    {
        RequirePermission("PURCH.MATCH.OVERRIDE");
        RequireActor(actor);
        var overrideId = await _repository.RecordOverrideAsync(
            RequireCompany(companyCode), invoiceDocId, evaluationId, actor, reason, cancellationToken).ConfigureAwait(false);
        if (_audit is not null)
            await _audit(actor, "Purchase match block released",
                $"Company={companyCode}; Invoice={invoiceDocId}; Evaluation={evaluationId:D}; Override={overrideId:D}; Reason={reason.Trim()}.").ConfigureAwait(false);
        return overrideId;
    }

    private void RequirePermission(string permission)
    {
        if (!_hasPermission(permission))
            throw new UnauthorizedAccessException($"Permission required: {permission}.");
    }

    private void RequireAnyPermission(params string[] permissions)
    {
        if (!permissions.Any(_hasPermission))
            throw new UnauthorizedAccessException("A purchasing view or match-control permission is required.");
    }

    private static string RequireCompany(string companyCode)
    {
        if (string.IsNullOrWhiteSpace(companyCode))
            throw new ArgumentException("A company code is required.", nameof(companyCode));
        return companyCode.Trim();
    }

    private static void RequireActor(string actor)
    {
        if (string.IsNullOrWhiteSpace(actor))
            throw new ArgumentException("An authenticated actor is required.", nameof(actor));
    }
}
