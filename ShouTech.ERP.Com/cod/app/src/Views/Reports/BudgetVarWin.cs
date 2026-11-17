using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ShouTech.App.Reporting;
using ShouTech.App.Services;

namespace ShouTech.App.Views.Reports;

/// <summary>
/// Budget variance report (Phase B 055, design section 3's
/// BudgetVarWin): budget vs actual vs remaining vs % consumed per
/// account x dimension, with the accumulated-monthly pair alongside —
/// the ERPNext Budget Variance Report pattern. Actuals come from
/// BudgetRepository (the BudgetGuard's own computation over posted
/// journal lines and cost splits), so the report and the posting guard
/// always agree.
/// </summary>
public sealed class BudgetVarWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Acc.BudgetVariance",
        TitleTextKey = "Reports.BudgetVariance",
        Columns = new RptCol[]
        {
            new() { Key = "Budget", HeaderTextKey = "Budget.Col.Budget", Type = RptColType.Text, Width = 170 },
            new() { Key = "FiscalYear", HeaderTextKey = "Budget.FiscalYear", Type = RptColType.Text, Width = 80 },
            new() { Key = "Account", HeaderTextKey = "Budget.Col.Account", Type = RptColType.Text, Width = 200 },
            new() { Key = "Dimension", HeaderTextKey = "Budget.Col.Dimension", Type = RptColType.Text, Width = 120 },
            new() { Key = "AnnualBudget", HeaderTextKey = "Budget.Col.AnnualBudget", Type = RptColType.Money, Width = 120 },
            new() { Key = "AnnualActual", HeaderTextKey = "Budget.Col.AnnualActual", Type = RptColType.Money, Width = 120 },
            new() { Key = "Remaining", HeaderTextKey = "Budget.Col.Remaining", Type = RptColType.Money, Width = 120 },
            new() { Key = "Percent", HeaderTextKey = "Budget.Col.Percent", Type = RptColType.FractionalNumber, Width = 90, Format = "N2" },
            new() { Key = "MonthlyBudget", HeaderTextKey = "Budget.Col.MonthlyBudget", Type = RptColType.Money, Width = 120 },
            new() { Key = "MonthlyActual", HeaderTextKey = "Budget.Col.MonthlyActual", Type = RptColType.Money, Width = 120 }
        },
        Filters = Array.Empty<RptFlt>()
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var raw = await RptQuery.BudgetVarianceAsync(ct);
        var def = BuildDefinition();
        var rows = RptWinUtil.Map(raw, def.Columns);
        return new RptData
        {
            Columns = def.Columns,
            Rows = rows,
            Totals = RptWinUtil.SumTotals(rows, def.Columns, T("Reports.Totals"), "Budget")
        };
    }
}
