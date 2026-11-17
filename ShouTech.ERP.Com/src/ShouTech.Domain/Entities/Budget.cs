namespace ShouTech.Domain.Entities;

/// <summary>What a budget is measured against (migration 055). The value
/// is stored as text on budgets.budget_against and validated in the
/// service layer. CostCenter budgets are enforced per cost-center split,
/// Account budgets per account total; Project budgets are definable in
/// v1 (decision BG3) but cannot be enforced at posting because journal
/// lines carry no project attribution — see BudgetGuard.</summary>
public static class BudgetAgainstKinds
{
    public const string CostCenter = "CostCenter";
    public const string Account = "Account";
    public const string Project = "Project";

    public static readonly IReadOnlyList<string> All = new[] { CostCenter, Account, Project };
}

/// <summary>Budget lifecycle (migration 055). Only Active budgets are
/// enforced by BudgetGuard (the ERPNext rule: a draft budget is never
/// checked); Closed budgets are kept for variance history.</summary>
public static class BudgetStatuses
{
    public const string Draft = "Draft";
    public const string Active = "Active";
    public const string Closed = "Closed";

    public static readonly IReadOnlyList<string> All = new[] { Draft, Active, Closed };
}

/// <summary>Per-horizon control actions (migration 055): the ERPNext
/// Stop / Warn / Ignore matrix, configured independently for the annual
/// total and for the accumulated monthly total.</summary>
public static class BudgetActions
{
    public const string Stop = "Stop";
    public const string Warn = "Warn";
    public const string Ignore = "Ignore";

    public static readonly IReadOnlyList<string> All = new[] { Stop, Warn, Ignore };
}

/// <summary>A budget header: one fiscal year of control for one company,
/// against cost centers, accounts, or projects.</summary>
public sealed class Budget
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>The fiscal_years primary key (INTEGER/BIGINT identity on
    /// both providers — budgets key by the year's id, not its number).</summary>
    public long FiscalYearId { get; set; }
    /// <summary>Display label resolved by the repository (e.g. "2026").</summary>
    public string FiscalYearLabel { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string BudgetAgainst { get; set; } = BudgetAgainstKinds.CostCenter;
    public string Status { get; set; } = BudgetStatuses.Draft;
    public string ActionAnnual { get; set; } = BudgetActions.Stop;
    public string ActionMonthly { get; set; } = BudgetActions.Warn;
    public string? Notes { get; set; }
    public List<BudgetLine> Lines { get; set; } = new();
}

/// <summary>One budgeted (account x dimension) amount. CostCenterId /
/// ProjectId are Guids of cost_centers.id / projects.project_id in text
/// form (the master-card id convention); both are null for an
/// Account-against line.</summary>
public sealed class BudgetLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BudgetId { get; set; }
    public Guid AccountId { get; set; }
    public string AccountCode { get; set; } = string.Empty;
    public string? AccountName { get; set; }
    public string? CostCenterId { get; set; }
    public string? CostCenterCode { get; set; }
    public string? ProjectId { get; set; }
    public string? ProjectCode { get; set; }
    public decimal AnnualAmount { get; set; }
    /// <summary>Monthly distribution amounts keyed by period number
    /// (1-12). Empty = equal split of the annual amount.</summary>
    public Dictionary<int, decimal> PeriodAmounts { get; set; } = new();

    /// <summary>The line's dimension code for display (cost center or
    /// project, whichever the budget is against).</summary>
    public string? DimensionCode => CostCenterCode ?? ProjectCode;
}

/// <summary>One budget-vs-actual row (the BudgetVarWin / service answer
/// to "budget, actual, remaining, % consumed" per account x dimension x
/// fiscal year, with the accumulated-monthly pair alongside).</summary>
public sealed record BudgetVarianceRow(
    Guid BudgetId,
    string BudgetName,
    string FiscalYearLabel,
    Guid BudgetLineId,
    string AccountCode,
    string? AccountName,
    string? DimensionCode,
    string? DimensionName,
    decimal AnnualBudget,
    decimal AnnualActual,
    decimal MonthlyBudgetToDate,
    decimal MonthlyActualToDate)
{
    public decimal AnnualRemaining => AnnualBudget - AnnualActual;
    public decimal PercentConsumed => AnnualBudget == 0m
        ? (AnnualActual > 0m ? 100m : 0m)
        : Math.Round(AnnualActual * 100m / AnnualBudget, 2, MidpointRounding.AwayFromZero);
}

/// <summary>A fiscal year as the budget editor offers it: the
/// fiscal_years identity plus its display label and date span.</summary>
public sealed record BudgetFiscalYear(long Id, string Label, string StartDate, string EndDate);

/// <summary>An account as the budget line editor offers it (canonical
/// chart, postable + active only), with its budget opt-in flag.</summary>
public sealed record BudgetAccountOption(Guid Id, string Code, string Name, bool BudgetFlagged);

/// <summary>A cost center or project as the budget line editor offers
/// it: master id (text form), code, display name.</summary>
public sealed record BudgetDimensionOption(string Id, string Code, string Name);

/// <summary>A budget overrun the guard allowed through in Warn mode:
/// returned up through the posting call so the posting window can show
/// it after the entry posts (the Warned event row is the durable
/// record in budget_control_events).</summary>
public sealed record BudgetWarning(
    Guid BudgetId,
    string BudgetName,
    Guid BudgetLineId,
    string AccountCode,
    string Horizon,
    decimal BudgetAmount,
    decimal ActualBefore,
    decimal EntryAmount,
    decimal Overage);

/// <summary>One Stop-mode violation, captured by the guard before it
/// throws so the repository boundary can record the Blocked event in
/// budget_control_events after the posting transaction rolls back
/// (an in-transaction row would roll back with the posting).</summary>
public sealed record BudgetViolation(
    Guid BudgetId,
    string BudgetName,
    Guid BudgetLineId,
    string AccountCode,
    string Horizon,
    decimal BudgetAmount,
    decimal ActualBefore,
    decimal EntryAmount,
    decimal Overage,
    string ErrorCode)
{
    /// <summary>The entry whose posting was refused (filled by the
    /// guard, which knows the entry; the line-level evaluation does
    /// not need it).</summary>
    public Guid JournalEntryId { get; init; }
}
