using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.Application.Services;

/// <summary>
/// Budgets application service (Phase B 055). All invariants are
/// validated here before the repository is touched (the repository
/// re-checks company scope and account ownership inside its transaction
/// as a second line of defence):
///   * a budget names a fiscal year of its company and carries at least
///     one line;
///   * a line's dimension matches the budget's BudgetAgainst kind
///     (CostCenter lines carry a center and no project, Account lines
///     carry neither, Project lines carry a project and no center) and
///     no two lines of a budget share the same (account, dimension);
///   * a monthly distribution, when present, covers periods 1-12 with
///     non-negative amounts summing exactly to the line's annual amount
///     (no rows at all = the equal-split rule the guard applies).
/// </summary>
public sealed class BudgetService
{
    private readonly IBudgetRepository _repository;
    private readonly Func<string, bool> _hasPermission;

    public BudgetService(IBudgetRepository repository, Func<string, bool> hasPermission)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _hasPermission = hasPermission ?? throw new ArgumentNullException(nameof(hasPermission));
    }

    public Task<IReadOnlyList<Budget>> GetAllAsync(Guid companyId, CancellationToken cancellationToken = default)
    {
        RequirePermission("ACC.BUDGET.VIEW");
        return _repository.GetAllAsync(RequireCompany(companyId), cancellationToken);
    }

    public Task<IReadOnlyList<BudgetVarianceRow>> GetVarianceAsync(Guid companyId, CancellationToken cancellationToken = default)
    {
        RequirePermission("ACC.BUDGET.VIEW");
        return _repository.GetVarianceAsync(RequireCompany(companyId), cancellationToken);
    }

    public Task<IReadOnlyList<BudgetFiscalYear>> GetFiscalYearsAsync(Guid companyId, CancellationToken cancellationToken = default)
    {
        RequirePermission("ACC.BUDGET.VIEW");
        return _repository.GetFiscalYearsAsync(RequireCompany(companyId), cancellationToken);
    }

    public Task<IReadOnlyList<BudgetAccountOption>> GetAccountsAsync(Guid companyId, CancellationToken cancellationToken = default)
    {
        RequirePermission("ACC.BUDGET.VIEW");
        return _repository.GetAccountsAsync(RequireCompany(companyId), cancellationToken);
    }

    public Task<IReadOnlyList<BudgetDimensionOption>> GetCostCentersAsync(Guid companyId, CancellationToken cancellationToken = default)
    {
        RequirePermission("ACC.BUDGET.VIEW");
        return _repository.GetCostCentersAsync(RequireCompany(companyId), cancellationToken);
    }

    public Task<IReadOnlyList<BudgetDimensionOption>> GetProjectsAsync(Guid companyId, CancellationToken cancellationToken = default)
    {
        RequirePermission("ACC.BUDGET.VIEW");
        return _repository.GetProjectsAsync(RequireCompany(companyId), cancellationToken);
    }

    public async Task<Guid> SaveAsync(Guid companyId, Budget budget, string userId, CancellationToken cancellationToken = default)
    {
        RequirePermission("ACC.BUDGET.EDIT");
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(budget);
        if (string.IsNullOrWhiteSpace(budget.Name))
            throw new InvalidOperationException("Budget name is required.");
        if (budget.FiscalYearId <= 0)
            throw new InvalidOperationException("A fiscal year is required.");
        if (!BudgetAgainstKinds.All.Contains(budget.BudgetAgainst))
            throw new InvalidOperationException($"Unknown budget-against kind '{budget.BudgetAgainst}'.");
        if (!BudgetStatuses.All.Contains(budget.Status))
            throw new InvalidOperationException($"Unknown budget status '{budget.Status}'.");
        if (!BudgetActions.All.Contains(budget.ActionAnnual))
            throw new InvalidOperationException($"Unknown annual action '{budget.ActionAnnual}'.");
        if (!BudgetActions.All.Contains(budget.ActionMonthly))
            throw new InvalidOperationException($"Unknown monthly action '{budget.ActionMonthly}'.");
        if (budget.Lines.Count == 0)
            throw new InvalidOperationException("A budget requires at least one line.");

        var seen = new HashSet<(Guid, string, string)>();
        foreach (var line in budget.Lines)
        {
            if (line.AccountId == Guid.Empty)
                throw new InvalidOperationException("Every budget line requires an account.");
            if (line.AnnualAmount < 0)
                throw new InvalidOperationException("Budget line amounts cannot be negative.");
            switch (budget.BudgetAgainst)
            {
                case BudgetAgainstKinds.CostCenter:
                    if (string.IsNullOrWhiteSpace(line.CostCenterId))
                        throw new InvalidOperationException("A cost-center budget line requires a cost center.");
                    if (!string.IsNullOrWhiteSpace(line.ProjectId))
                        throw new InvalidOperationException("A cost-center budget line cannot carry a project.");
                    break;
                case BudgetAgainstKinds.Account:
                    if (!string.IsNullOrWhiteSpace(line.CostCenterId) || !string.IsNullOrWhiteSpace(line.ProjectId))
                        throw new InvalidOperationException("An account budget line carries no cost center or project.");
                    break;
                case BudgetAgainstKinds.Project:
                    if (string.IsNullOrWhiteSpace(line.ProjectId))
                        throw new InvalidOperationException("A project budget line requires a project.");
                    if (!string.IsNullOrWhiteSpace(line.CostCenterId))
                        throw new InvalidOperationException("A project budget line cannot carry a cost center.");
                    break;
            }
            var key = (line.AccountId,
                line.CostCenterId?.Trim() ?? string.Empty,
                line.ProjectId?.Trim() ?? string.Empty);
            if (!seen.Add(key))
                throw new InvalidOperationException(
                    $"Duplicate budget line for account '{line.AccountCode}' with the same dimension.");
            foreach (var (periodNo, amount) in line.PeriodAmounts)
            {
                if (periodNo is < 1 or > 12)
                    throw new InvalidOperationException("Monthly distribution periods must be between 1 and 12.");
                if (amount < 0)
                    throw new InvalidOperationException("Monthly distribution amounts cannot be negative.");
            }
            if (line.PeriodAmounts.Count > 0 &&
                Math.Abs(line.PeriodAmounts.Values.Sum() - line.AnnualAmount) > 0.01m)
                throw new InvalidOperationException(
                    $"The monthly distribution of account '{line.AccountCode}' must sum to its annual amount ({line.AnnualAmount}).");
        }

        budget.Name = budget.Name.Trim();
        await _repository.SaveAsync(companyId, budget, RequireUser(userId), cancellationToken).ConfigureAwait(false);
        return budget.Id;
    }

    public Task DeleteAsync(Guid companyId, Guid budgetId, string userId, CancellationToken cancellationToken = default)
    {
        RequirePermission("ACC.BUDGET.EDIT");
        if (budgetId == Guid.Empty)
            throw new ArgumentException("Budget is required.", nameof(budgetId));
        return _repository.SoftDeleteAsync(RequireCompany(companyId), budgetId, RequireUser(userId), cancellationToken);
    }

    public Task SetStatusAsync(Guid companyId, Guid budgetId, string status, string userId, CancellationToken cancellationToken = default)
    {
        RequirePermission("ACC.BUDGET.ACTIVATE");
        if (budgetId == Guid.Empty)
            throw new ArgumentException("Budget is required.", nameof(budgetId));
        if (!BudgetStatuses.All.Contains(status))
            throw new InvalidOperationException($"Unknown budget status '{status}'.");
        return _repository.SetStatusAsync(RequireCompany(companyId), budgetId, status, RequireUser(userId), cancellationToken);
    }

    private void RequirePermission(string permission)
    {
        if (!_hasPermission(permission))
            throw new UnauthorizedAccessException($"Permission required: {permission}.");
    }

    private static Guid RequireCompany(Guid companyId)
        => companyId == Guid.Empty
            ? throw new ArgumentException("Company is required.", nameof(companyId))
            : companyId;

    private static string RequireUser(string userId)
        => string.IsNullOrWhiteSpace(userId)
            ? throw new ArgumentException("User is required.", nameof(userId))
            : userId.Trim();
}
