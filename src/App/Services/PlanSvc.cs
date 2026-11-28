using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.Application.Services;

/// <summary>
/// MRP-lite planning service (migration 058): thin validation over
/// PlanningRepository. Feasibility answers from the SAME explosion the
/// planning run uses, so "can we build X?" and the plan can never
/// disagree.
/// </summary>
public sealed class PlanningService
{
    private readonly IPlanningRepository _repository;
    public PlanningService(IPlanningRepository repository)
        => _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    internal IPlanningRepository Repository => _repository;

    public Task<PlanningRun> RunPlanningAsync(string companyId, string userId, CancellationToken cancellationToken = default)
        => _repository.RunPlanningAsync(RequireCompany(companyId), RequireUser(userId), cancellationToken);

    public Task<IReadOnlyList<PlanningRun>> GetRunsAsync(string companyId, CancellationToken cancellationToken = default)
        => _repository.GetRunsAsync(RequireCompany(companyId), cancellationToken);

    public Task<PlanningRun?> GetRunAsync(string companyId, Guid runId, CancellationToken cancellationToken = default)
    {
        if (runId == Guid.Empty) throw new ArgumentException("Planning run is required.", nameof(runId));
        return _repository.GetRunAsync(RequireCompany(companyId), runId, cancellationToken);
    }

    public Task<FeasibilityResult> CheckFeasibilityAsync(string companyId, string materialCode, decimal qty, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(materialCode)) throw new ArgumentException("Material is required.", nameof(materialCode));
        if (qty <= 0) throw new InvalidOperationException("The quantity must be greater than zero.");
        return _repository.CheckFeasibilityAsync(RequireCompany(companyId), materialCode.Trim(), qty, cancellationToken);
    }

    public Task<Guid> ConvertPlannedOrderAsync(string companyId, Guid plannedOrderId, string userId, CancellationToken cancellationToken = default)
    {
        if (plannedOrderId == Guid.Empty) throw new ArgumentException("Planned order is required.", nameof(plannedOrderId));
        return _repository.ConvertPlannedOrderAsync(RequireCompany(companyId), plannedOrderId, RequireUser(userId), cancellationToken);
    }

    public Task<(long DocId, string DocNo)> ConvertSuggestionAsync(string companyId, Guid suggestionId, string userId, CancellationToken cancellationToken = default)
    {
        if (suggestionId == Guid.Empty) throw new ArgumentException("Purchase suggestion is required.", nameof(suggestionId));
        return _repository.ConvertSuggestionAsync(RequireCompany(companyId), suggestionId, RequireUser(userId), cancellationToken);
    }

    public Task CancelPlannedOrderAsync(string companyId, Guid plannedOrderId, string userId, CancellationToken cancellationToken = default)
    {
        if (plannedOrderId == Guid.Empty) throw new ArgumentException("Planned order is required.", nameof(plannedOrderId));
        return _repository.CancelPlannedOrderAsync(RequireCompany(companyId), plannedOrderId, RequireUser(userId), cancellationToken);
    }

    public Task CancelSuggestionAsync(string companyId, Guid suggestionId, string userId, CancellationToken cancellationToken = default)
    {
        if (suggestionId == Guid.Empty) throw new ArgumentException("Purchase suggestion is required.", nameof(suggestionId));
        return _repository.CancelSuggestionAsync(RequireCompany(companyId), suggestionId, RequireUser(userId), cancellationToken);
    }

    private static string RequireCompany(string companyId)
    {
        if (string.IsNullOrWhiteSpace(companyId))
            throw new ArgumentException("Company is required.", nameof(companyId));
        return companyId;
    }

    private static string RequireUser(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new InvalidOperationException("A current user is required for audited writes.");
        return userId;
    }
}
