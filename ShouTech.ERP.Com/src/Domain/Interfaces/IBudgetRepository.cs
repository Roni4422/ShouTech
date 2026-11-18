using ShouTech.Domain.Entities;

namespace ShouTech.Domain.Interfaces;

/// <summary>Budget persistence (migration 055). Budgets live in the
/// journal world, so every method keys the company by its Guid —
/// unlike the master-card repositories, which key by company code.
/// The code-keyed lookups (cost centers, projects) resolve the code
/// from the companies registry inside the repository.</summary>
public interface IBudgetRepository
{
    /// <summary>All live budgets of the company with their lines and
    /// monthly distributions loaded.</summary>
    Task<IReadOnlyList<Budget>> GetAllAsync(Guid companyId, CancellationToken cancellationToken = default);

    /// <summary>Creates or replaces a budget (header + lines +
    /// distributions) in one transaction, and sets the chart budget
    /// opt-in flag on every account that gains a line.</summary>
    Task SaveAsync(Guid companyId, Budget budget, string userId, CancellationToken cancellationToken = default);

    Task SoftDeleteAsync(Guid companyId, Guid budgetId, string userId, CancellationToken cancellationToken = default);

    /// <summary>Moves a budget between Draft / Active / Closed.</summary>
    Task SetStatusAsync(Guid companyId, Guid budgetId, string status, string userId, CancellationToken cancellationToken = default);

    /// <summary>Budget vs actual for every live budget line, computed
    /// by the same actuals implementation the posting guard enforces
    /// (BudgetGuard.GetLineActuals).</summary>
    Task<IReadOnlyList<BudgetVarianceRow>> GetVarianceAsync(Guid companyId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BudgetFiscalYear>> GetFiscalYearsAsync(Guid companyId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BudgetAccountOption>> GetAccountsAsync(Guid companyId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BudgetDimensionOption>> GetCostCentersAsync(Guid companyId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BudgetDimensionOption>> GetProjectsAsync(Guid companyId, CancellationToken cancellationToken = default);
}
