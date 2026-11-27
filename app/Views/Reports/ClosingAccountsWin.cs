using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ShouTech.App.Reporting;
using ShouTech.App.Services;

namespace ShouTech.App.Views.Reports;

// ---- merged from cod/app/src/Views/Reports/AccBalWin.cs ----
/// <summary>Account Balance report.</summary>
public sealed class AccBalWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Fin.AccountBalance",
        TitleTextKey = "Reports.AccountBalance",
        Columns = new RptCol[]
        {
            new() { Key = "Code", HeaderTextKey = "Reports.Col.AccountCode", Type = RptColType.Text, Width = 110 },
            new() { Key = "Name", HeaderTextKey = "Reports.Col.AccountName", Type = RptColType.Text, Width = 260 },
            new() { Key = "Debit", HeaderTextKey = "Reports.Col.Debit", Type = RptColType.Money, Width = 120, Format = "N2" },
            new() { Key = "Credit", HeaderTextKey = "Reports.Col.Credit", Type = RptColType.Money, Width = 120, Format = "N2" },
            new() { Key = "Balance", HeaderTextKey = "Reports.Col.Balance", Type = RptColType.Money, Width = 120, Format = "N2" },
        },
        Filters = new RptFlt[]
        {
            new() { Key = "From", LabelTextKey = "Reports.Filter.DateFrom", Kind = RptFltKind.DateFrom, DefaultValue = new DateTime(DateTime.Today.Year, 1, 1) },
            new() { Key = "To", LabelTextKey = "Reports.Filter.DateTo", Kind = RptFltKind.DateTo, DefaultValue = DateTime.Today },
        }
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var from = filters.GetDateOr("From", new DateTime(DateTime.Today.Year, 1, 1));
        var to = filters.GetDateOr("To", DateTime.Today);
        var raw = await RptQuery.TrialBalanceAsync(from, to, ct);
        var rows = raw.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["Code"] = r.GetValueOrDefault("Code"),
            ["Name"] = PickName(r),
            ["Debit"] = ToDec(r.GetValueOrDefault("Debit")),
            ["Credit"] = ToDec(r.GetValueOrDefault("Credit")),
            ["Balance"] = ToDec(r.GetValueOrDefault("Balance")),
        }).ToList();

        var totals = new Dictionary<string, object?>
        {
            ["Code"] = "", ["Name"] = T("Reports.Totals"),
            ["Debit"] = rows.Sum(x => ToDec(x["Debit"])),
            ["Credit"] = rows.Sum(x => ToDec(x["Credit"])),
            ["Balance"] = rows.Sum(x => ToDec(x["Balance"])),
        };
        return new RptData { Columns = BuildDefinition().Columns, Rows = rows, Totals = totals };
    }

    private static string? PickName(IReadOnlyDictionary<string, object?> r)
    {
        var lang = App.CurrentLanguageCode ?? "ar";
        var name = lang.StartsWith("ar", StringComparison.OrdinalIgnoreCase)
            ? Convert.ToString(r.GetValueOrDefault("Name"), CultureInfo.InvariantCulture)
            : Convert.ToString(r.GetValueOrDefault("NameEn"), CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(name) ? Convert.ToString(r.GetValueOrDefault("Name"), CultureInfo.InvariantCulture) : name;
    }

    private static decimal ToDec(object? v) => v is null ? 0m : Convert.ToDecimal(v, CultureInfo.InvariantCulture);
}

// ---- merged from cod/app/src/Views/Reports/AccCompareWin.cs ----
/// <summary>Two-period comparison of debit/credit/balance per account.</summary>
public sealed class AccCompareWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Acc.AccountComparison",
        TitleTextKey = "Reports.AccountComparison",
        Columns = new RptCol[]
        {
            new() { Key = "AccountCode", HeaderTextKey = "Reports.Col.AccountCode", Type = RptColType.Text, Width = 110 },
            new() { Key = "AccountName", HeaderTextKey = "Reports.Col.AccountName", Type = RptColType.Text, Width = 220 },
            new() { Key = "Debit1", HeaderTextKey = "Reports.Col.DebitP1", Type = RptColType.Money, Width = 110 },
            new() { Key = "Credit1", HeaderTextKey = "Reports.Col.CreditP1", Type = RptColType.Money, Width = 110 },
            new() { Key = "Balance1", HeaderTextKey = "Reports.Col.BalanceP1", Type = RptColType.Money, Width = 110 },
            new() { Key = "Debit2", HeaderTextKey = "Reports.Col.DebitP2", Type = RptColType.Money, Width = 110 },
            new() { Key = "Credit2", HeaderTextKey = "Reports.Col.CreditP2", Type = RptColType.Money, Width = 110 },
            new() { Key = "Balance2", HeaderTextKey = "Reports.Col.BalanceP2", Type = RptColType.Money, Width = 110 },
            new() { Key = "Diff", HeaderTextKey = "Reports.Col.Difference", Type = RptColType.Money, Width = 110 }
        },
        Filters = new RptFlt[]
        {
            new() { Key = "From", LabelTextKey = "Reports.Filter.DateFrom", Kind = RptFltKind.DateFrom, DefaultValue = new DateTime(DateTime.Today.Year, 1, 1) },
            new() { Key = "To", LabelTextKey = "Reports.Filter.DateTo", Kind = RptFltKind.DateTo, DefaultValue = DateTime.Today },
            new() { Key = "From2", LabelTextKey = "Reports.Filter.P2From", Kind = RptFltKind.DateFrom, DefaultValue = new DateTime(DateTime.Today.Year - 1, 1, 1) },
            new() { Key = "To2", LabelTextKey = "Reports.Filter.P2To", Kind = RptFltKind.DateTo, DefaultValue = new DateTime(DateTime.Today.Year - 1, 12, 31) }
        }
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var from = filters.GetDateOr("From", new DateTime(DateTime.Today.Year, 1, 1));
        var to = filters.GetDateOr("To", DateTime.Today);
        var from2 = filters.GetDateOr("From2", new DateTime(DateTime.Today.Year - 1, 1, 1));
        var to2 = filters.GetDateOr("To2", new DateTime(DateTime.Today.Year - 1, 12, 31));
        var raw = await RptQuery.AccountComparisonAsync(from, to, from2, to2, ct);
        var def = BuildDefinition();
        var rows = RptWinUtil.Map(raw, def.Columns);
        return new RptData
        {
            Columns = def.Columns,
            Rows = rows,
            Totals = RptWinUtil.SumTotals(rows, def.Columns, T("Reports.Totals"), "AccountName")
        };
    }
}

// ---- merged from cod/app/src/Views/Reports/AccMoveChartWin.cs ----
/// <summary>Per-account monthly debit/credit schedule.</summary>
public sealed class AccMoveChartWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Acc.AccountMovementChart",
        TitleTextKey = "Reports.AccountMovementChart",
        Columns = new RptCol[]
        {
            new() { Key = "AccountCode", HeaderTextKey = "Reports.Col.AccountCode", Type = RptColType.Text, Width = 110 },
            new() { Key = "AccountName", HeaderTextKey = "Reports.Col.AccountName", Type = RptColType.Text, Width = 240 },
            new() { Key = "Month", HeaderTextKey = "Reports.Col.Month", Type = RptColType.Text, Width = 90 },
            new() { Key = "Debit", HeaderTextKey = "Reports.Col.Debit", Type = RptColType.Money, Width = 120 },
            new() { Key = "Credit", HeaderTextKey = "Reports.Col.Credit", Type = RptColType.Money, Width = 120 },
            new() { Key = "Balance", HeaderTextKey = "Reports.Col.Balance", Type = RptColType.Money, Width = 120 }
        },
        Filters = new RptFlt[]
        {
            new() { Key = "From", LabelTextKey = "Reports.Filter.DateFrom", Kind = RptFltKind.DateFrom, DefaultValue = new DateTime(DateTime.Today.Year, 1, 1) },
            new() { Key = "To", LabelTextKey = "Reports.Filter.DateTo", Kind = RptFltKind.DateTo, DefaultValue = DateTime.Today }
        }
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var from = filters.GetDateOr("From", new DateTime(DateTime.Today.Year, 1, 1));
        var to = filters.GetDateOr("To", DateTime.Today);
        var raw = await RptQuery.AccountMovementChartAsync(from, to, ct);
        var def = BuildDefinition();
        var rows = RptWinUtil.Map(raw, def.Columns);
        return new RptData
        {
            Columns = def.Columns,
            Rows = rows,
            Totals = RptWinUtil.SumTotals(rows, def.Columns, T("Reports.Totals"), "AccountName")
        };
    }
}

// ---- merged from cod/app/src/Views/Reports/AccStmtWin.cs ----
public sealed class AccStmtWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Fin.AccountStatement",
        TitleTextKey = "Reports.AccountStatement",
        Columns = new RptCol[]
        {
            new() { Key = "EntryDate", HeaderTextKey = "Reports.Col.Date", Type = RptColType.Date, Width = 100 },
            new() { Key = "DocNo", HeaderTextKey = "Reports.Col.DocNo", Type = RptColType.Text, Width = 140 },
            new() { Key = "Description", HeaderTextKey = "Reports.Col.Description", Type = RptColType.Text, Width = 280 },
            new() { Key = "Debit", HeaderTextKey = "Reports.Col.Debit", Type = RptColType.Money, Width = 110, Format = "N2" },
            new() { Key = "Credit", HeaderTextKey = "Reports.Col.Credit", Type = RptColType.Money, Width = 110, Format = "N2" },
            new() { Key = "Balance", HeaderTextKey = "Reports.Col.Balance", Type = RptColType.Money, Width = 110, Format = "N2" },
        },
        Filters = new RptFlt[]
        {
            new() { Key = "Account", LabelTextKey = "Reports.Filter.AccountCode", Kind = RptFltKind.Text },
            new() { Key = "From", LabelTextKey = "Reports.Filter.DateFrom", Kind = RptFltKind.DateFrom, DefaultValue = new DateTime(DateTime.Today.Year, 1, 1) },
            new() { Key = "To", LabelTextKey = "Reports.Filter.DateTo", Kind = RptFltKind.DateTo, DefaultValue = DateTime.Today },
        }
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var account = filters.GetString("Account") ?? string.Empty;
        var from = filters.GetDateOr("From", new DateTime(DateTime.Today.Year, 1, 1));
        var to = filters.GetDateOr("To", DateTime.Today);
        var raw = await RptQuery.AccountStatementAsync(account, from, to, ct);

        decimal running = 0m;
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var r in raw)
        {
            var debit = ToDec(r.GetValueOrDefault("Debit"));
            var credit = ToDec(r.GetValueOrDefault("Credit"));
            running += debit - credit;
            rows.Add(new Dictionary<string, object?>
            {
                ["EntryDate"] = r.GetValueOrDefault("EntryDate"),
                ["DocNo"] = r.GetValueOrDefault("DocNo"),
                ["Description"] = r.GetValueOrDefault("Description"),
                ["Debit"] = debit,
                ["Credit"] = credit,
                ["Balance"] = running,
            });
        }

        var totals = new Dictionary<string, object?>
        {
            ["EntryDate"] = "",
            ["DocNo"] = "",
            ["Description"] = T("Reports.Totals"),
            ["Debit"] = rows.Sum(x => ToDec(x["Debit"])),
            ["Credit"] = rows.Sum(x => ToDec(x["Credit"])),
            ["Balance"] = running,
        };

        return new RptData { Columns = BuildDefinition().Columns, Rows = rows, Totals = totals };
    }

    private static decimal ToDec(object? v)
        => v is null ? 0m : Convert.ToDecimal(v, CultureInfo.InvariantCulture);
}

// ---- merged from cod/app/src/Views/Reports/BalSheetWin.cs ----
/// <summary>Balance Sheet report.</summary>
public sealed class BalSheetWin : RptBase
{
    // Structured statement: section/subtotal rows must not be flat-summed.
    protected override bool AllowComputedTotals => false;

    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Fin.BalanceSheet",
        TitleTextKey = "Reports.BalanceSheet",
        Columns = new RptCol[]
        {
            new() { Key = "Code", HeaderTextKey = "Reports.Col.AccountCode", Type = RptColType.Text, Width = 110 },
            new() { Key = "Name", HeaderTextKey = "Reports.Col.AccountName", Type = RptColType.Text, Width = 280 },
            new() { Key = "Amount", HeaderTextKey = "Reports.Col.Amount", Type = RptColType.Money, Width = 140, Format = "N2" },
        },
        Filters = new RptFlt[]
        {
            new() { Key = "AsOf", LabelTextKey = "Reports.Filter.DateTo", Kind = RptFltKind.DateTo, DefaultValue = DateTime.Today },
        }
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var asOf = filters.GetDateOr("AsOf", DateTime.Today);
        var raw = await RptQuery.BalanceSheetAsync(asOf, ct);
        var rows = raw.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["Code"] = r.GetValueOrDefault("Code"),
            ["Name"] = PickName(r),
            ["Amount"] = ToDec(r.GetValueOrDefault("Amount")),
        }).ToList();
        return new RptData { Columns = BuildDefinition().Columns, Rows = rows, Totals = null };
    }

    private static string? PickName(IReadOnlyDictionary<string, object?> row)
    {
        var lang = App.CurrentLanguageCode ?? "ar";
        var name = lang.StartsWith("ar", StringComparison.OrdinalIgnoreCase)
            ? Convert.ToString(row.GetValueOrDefault("Name"), CultureInfo.InvariantCulture)
            : Convert.ToString(row.GetValueOrDefault("NameEn"), CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(name)
            ? Convert.ToString(row.GetValueOrDefault("Name"), CultureInfo.InvariantCulture)
            : name;
    }

    private static decimal ToDec(object? v) => v is null ? 0m : Convert.ToDecimal(v, CultureInfo.InvariantCulture);
}

// ---- merged from cod/app/src/Views/Reports/BankBalancesWin.cs ----
/// <summary>
/// Bank balances: posted GL balances of bank accounts. Primary source is the
/// banks table (migration 031) joined through gl_account_code; while that
/// table is absent or empty the report falls back to bank-named GL accounts
/// — the same accounts, read directly — so it always shows real balances.
/// </summary>
public sealed class BankBalancesWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Banks.BankBalances",
        TitleTextKey = "Reports.BankBalances",
        Columns = new RptCol[]
        {
            new() { Key = "AccountCode", HeaderTextKey = "Reports.Col.AccountCode", Type = RptColType.Text, Width = 120 },
            new() { Key = "AccountName", HeaderTextKey = "Reports.Col.AccountName", Type = RptColType.Text, Width = 280 },
            new() { Key = "Debit", HeaderTextKey = "Reports.Col.Debit", Type = RptColType.Money, Width = 130, Format = "N2" },
            new() { Key = "Credit", HeaderTextKey = "Reports.Col.Credit", Type = RptColType.Money, Width = 130, Format = "N2" },
            new() { Key = "Balance", HeaderTextKey = "Reports.Col.Balance", Type = RptColType.Money, Width = 140, Format = "N2" },
        },
        Filters = Array.Empty<RptFlt>()
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var raw = await RptQuery.BankBalancesFromBanksAsync(ct);
        if (raw.Count == 0)
            raw = await RptQuery.BankAccountsGlAsync(ct);
        var def = BuildDefinition();
        var rows = RptWinUtil.Map(raw, def.Columns);
        return new RptData
        {
            Columns = def.Columns,
            Rows = rows,
            Totals = RptWinUtil.SumTotals(rows, def.Columns, T("Reports.Totals"), "AccountName")
        };
    }
}

// ---- merged from cod/app/src/Views/Reports/BankEstimateWin.cs ----
/// <summary>
/// Bank estimate: current bank GL balances plus cheques falling due within
/// the next N days, projected per bank. Cheques (migration 033) carry no GL
/// account link, so due cheques are shown as one honest aggregate row for
/// all banks instead of being invented per bank.
/// </summary>
public sealed class BankEstimateWin : RptBase
{
    protected override bool AllowComputedTotals => false;

    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Banks.BankEstimate",
        TitleTextKey = "Reports.BankEstimate",
        Columns = new RptCol[]
        {
            new() { Key = "Item", HeaderTextKey = "Reports.Col.Item", Type = RptColType.Text, Width = 320 },
            new() { Key = "Balance", HeaderTextKey = "Reports.Col.Balance", Type = RptColType.Money, Width = 140, Format = "N2" },
            new() { Key = "ChequesIn", HeaderTextKey = "Reports.Col.ChequesIn", Type = RptColType.Money, Width = 130, Format = "N2" },
            new() { Key = "ChequesOut", HeaderTextKey = "Reports.Col.ChequesOut", Type = RptColType.Money, Width = 130, Format = "N2" },
            new() { Key = "Projected", HeaderTextKey = "Reports.Col.Projected", Type = RptColType.Money, Width = 150, Format = "N2" },
        },
        Filters = new RptFlt[]
        {
            new() { Key = "Days", LabelTextKey = "Reports.Filter.Days", Kind = RptFltKind.Text, DefaultValue = "30" },
        }
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var days = RptWinUtil.ParseInt(filters.GetString("Days"), 30);
        var banks = await RptQuery.BankBalancesFromBanksAsync(ct);
        if (banks.Count == 0)
            banks = await RptQuery.BankAccountsGlAsync(ct);
        var cheques = await RptQuery.ChequesDueSummaryAsync(days, ct);
        decimal chqIn = 0, chqOut = 0;
        foreach (var c in cheques)
        {
            var dir = Convert.ToString(c.GetValueOrDefault("Direction"), CultureInfo.InvariantCulture);
            if (string.Equals(dir, "IN", StringComparison.OrdinalIgnoreCase)) chqIn = RptWinUtil.Dec(c.GetValueOrDefault("Amount"));
            else if (string.Equals(dir, "OUT", StringComparison.OrdinalIgnoreCase)) chqOut = RptWinUtil.Dec(c.GetValueOrDefault("Amount"));
        }

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        decimal totalBalance = 0;
        foreach (var b in banks)
        {
            var bal = RptWinUtil.Dec(b.GetValueOrDefault("Balance"));
            totalBalance += bal;
            var code = Convert.ToString(b.GetValueOrDefault("AccountCode"), CultureInfo.InvariantCulture) ?? "";
            var name = Convert.ToString(b.GetValueOrDefault("AccountName"), CultureInfo.InvariantCulture) ?? "";
            rows.Add(new Dictionary<string, object?>
            {
                ["Item"] = string.IsNullOrWhiteSpace(name) ? code : $"{code} - {name}",
                ["Balance"] = bal, ["ChequesIn"] = 0m, ["ChequesOut"] = 0m, ["Projected"] = bal,
            });
        }
        rows.Add(new Dictionary<string, object?>
        {
            ["Item"] = T("Reports.AllBanksCheques"),
            ["Balance"] = 0m, ["ChequesIn"] = chqIn, ["ChequesOut"] = chqOut, ["Projected"] = chqIn - chqOut,
        });
        var def = BuildDefinition();
        return new RptData
        {
            Columns = def.Columns,
            Rows = rows,
            Totals = new Dictionary<string, object?>
            {
                ["Item"] = T("Reports.Totals"),
                ["Balance"] = totalBalance,
                ["ChequesIn"] = chqIn,
                ["ChequesOut"] = chqOut,
                ["Projected"] = totalBalance + chqIn - chqOut,
            }
        };
    }
}

// ---- merged from cod/app/src/Views/Reports/BranchAccMoveWin.cs ----
/// <summary>Posted document totals grouped by branch (branch_name on document headers).</summary>
public sealed class BranchAccMoveWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Acc.BranchAccountMovement",
        TitleTextKey = "Reports.BranchAccountMovement",
        Columns = new RptCol[]
        {
            new() { Key = "Branch", HeaderTextKey = "Reports.Col.Branch", Type = RptColType.Text, Width = 180 },
            new() { Key = "Kind", HeaderTextKey = "Reports.Col.DocKind", Type = RptColType.Text, Width = 80 },
            new() { Key = "DocCount", HeaderTextKey = "Reports.Col.DocCount", Type = RptColType.WholeNumber, Width = 100 },
            new() { Key = "Total", HeaderTextKey = "Reports.Col.Total", Type = RptColType.Money, Width = 140 }
        },
        Filters = new RptFlt[]
        {
            new() { Key = "From", LabelTextKey = "Reports.Filter.DateFrom", Kind = RptFltKind.DateFrom, DefaultValue = new DateTime(DateTime.Today.Year, 1, 1) },
            new() { Key = "To", LabelTextKey = "Reports.Filter.DateTo", Kind = RptFltKind.DateTo, DefaultValue = DateTime.Today }
        }
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var from = filters.GetDateOr("From", new DateTime(DateTime.Today.Year, 1, 1));
        var to = filters.GetDateOr("To", DateTime.Today);
        var raw = await RptQuery.BranchAccountMovementAsync(from, to, ct);
        var def = BuildDefinition();
        var rows = RptWinUtil.Map(raw, def.Columns);
        return new RptData
        {
            Columns = def.Columns,
            Rows = rows,
            Totals = RptWinUtil.SumTotals(rows, def.Columns, T("Reports.Totals"), "Branch")
        };
    }
}

// ---- merged from cod/app/src/Views/Reports/BudgetVarWin.cs ----
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

// ---- merged from cod/app/src/Views/Reports/CashFlowWin.cs ----
/// <summary>
/// Cash flow statement. Method (indirect approximation, also documented on
/// RptQuery.CashFlowSectionsAsync): posted journal lines touching cash/bank
/// accounts are classified Operating / Investing / Financing by the account
/// type of each entry's first non-cash counterpart line; the schedule adds
/// the net change and the opening/closing cash positions from the same GL.
/// Structured statement: no flat totals.
/// </summary>
public sealed class CashFlowWin : RptBase
{
    protected override bool AllowComputedTotals => false;

    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Acc.CashFlowStatement",
        TitleTextKey = "Reports.CashFlowStatement",
        Columns = new RptCol[]
        {
            new() { Key = "Section", HeaderTextKey = "Reports.Col.Section", Type = RptColType.Text, Width = 220 },
            new() { Key = "Item", HeaderTextKey = "Reports.Col.Item", Type = RptColType.Text, Width = 320 },
            new() { Key = "Amount", HeaderTextKey = "Reports.Col.Amount", Type = RptColType.Money, Width = 160, Format = "N2" },
        },
        Filters = new RptFlt[]
        {
            new() { Key = "From", LabelTextKey = "Reports.Filter.DateFrom", Kind = RptFltKind.DateFrom, DefaultValue = new DateTime(DateTime.Today.Year, 1, 1) },
            new() { Key = "To", LabelTextKey = "Reports.Filter.DateTo", Kind = RptFltKind.DateTo, DefaultValue = DateTime.Today },
        }
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var from = filters.GetDateOr("From", new DateTime(DateTime.Today.Year, 1, 1));
        var to = filters.GetDateOr("To", DateTime.Today);
        var sections = await RptQuery.CashFlowSectionsAsync(from, to, ct);
        var openingRaw = await RptQuery.CashPositionAsync(from.AddDays(-1), ct);
        var closingRaw = await RptQuery.CashPositionAsync(to, ct);
        var opening = openingRaw.Count > 0 ? RptWinUtil.Dec(openingRaw[0].GetValueOrDefault("Balance")) : 0m;
        var closing = closingRaw.Count > 0 ? RptWinUtil.Dec(closingRaw[0].GetValueOrDefault("Balance")) : 0m;

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        void Add(string section, string item, decimal amount)
            => rows.Add(new Dictionary<string, object?> { ["Section"] = section, ["Item"] = item, ["Amount"] = amount });

        decimal net = 0;
        foreach (var s in sections)
        {
            var name = Convert.ToString(s.GetValueOrDefault("Section"), CultureInfo.InvariantCulture) ?? "";
            var amount = RptWinUtil.Dec(s.GetValueOrDefault("Amount"));
            net += amount;
            Add(name, T("Reports.Col.Total"), amount);
        }
        Add("", T("Reports.NetChange"), net);
        Add("", T("Reports.BeginningBalance"), opening);
        Add("", T("Reports.EndingBalance"), closing);
        return new RptData { Columns = BuildDefinition().Columns, Rows = rows, Totals = null };
    }
}

// ---- merged from cod/app/src/Views/Reports/CashJournalWin.cs ----
/// <summary>Journal entries whose lines touch cash/bank/box accounts (identified by account name, as elsewhere in the app).</summary>
public sealed class CashJournalWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Bonds.CashJournal",
        TitleTextKey = "Reports.CashJournal",
        Columns = new RptCol[]
        {
            new() { Key = "EntryDate", HeaderTextKey = "Reports.Col.Date", Type = RptColType.Date, Width = 100 },
            new() { Key = "DocNo", HeaderTextKey = "Reports.Col.DocNo", Type = RptColType.Text, Width = 130 },
            new() { Key = "Description", HeaderTextKey = "Reports.Col.Description", Type = RptColType.Text, Width = 260 },
            new() { Key = "Reference", HeaderTextKey = "Reports.Col.Reference", Type = RptColType.Text, Width = 120 },
            new() { Key = "Debit", HeaderTextKey = "Reports.Col.Debit", Type = RptColType.Money, Width = 120 },
            new() { Key = "Credit", HeaderTextKey = "Reports.Col.Credit", Type = RptColType.Money, Width = 120 },
            new() { Key = "Status", HeaderTextKey = "Reports.Col.Status", Type = RptColType.Text, Width = 90 }
        },
        Filters = new RptFlt[]
        {
            new() { Key = "From", LabelTextKey = "Reports.Filter.DateFrom", Kind = RptFltKind.DateFrom, DefaultValue = new DateTime(DateTime.Today.Year, 1, 1) },
            new() { Key = "To", LabelTextKey = "Reports.Filter.DateTo", Kind = RptFltKind.DateTo, DefaultValue = DateTime.Today }
        }
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var from = filters.GetDateOr("From", new DateTime(DateTime.Today.Year, 1, 1));
        var to = filters.GetDateOr("To", DateTime.Today);
        var raw = await RptQuery.CashJournalAsync(from, to, ct);
        var def = BuildDefinition();
        var rows = RptWinUtil.Map(raw, def.Columns);
        return new RptData
        {
            Columns = def.Columns,
            Rows = rows,
            Totals = RptWinUtil.SumTotals(rows, def.Columns, T("Reports.Totals"), "Description")
        };
    }
}

/// <summary>
/// Closing accounts schedule (Trading / Operations). One window class serves
/// both ribbon keys; the mode comes from the optional constructor argument
/// (default Trading). Built on the same income-statement query as IncStmtWin:
/// account codes 5x are revenue, 4x are cost of sales, everything else in the
/// 4x-6x range is operating expense. Structured statement: no flat totals.
/// </summary>
public sealed class ClosingAccountsWin : RptBase, IWindowMode
{
    private readonly string _mode;

    /// <summary>The statement this window renders (Step 11 router
    /// identity: Operations vs Trading).</summary>
    public string Mode => _mode;
    public string ModeKey => _mode;

    public ClosingAccountsWin() : this("Trading") { }
    public ClosingAccountsWin(string mode) => _mode = string.Equals(mode, "Operations", StringComparison.OrdinalIgnoreCase) ? "Operations" : "Trading";

    protected override bool AllowComputedTotals => false;

    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = _mode == "Operations" ? "Acc.Operations" : "Acc.Trading",
        TitleTextKey = _mode == "Operations" ? "Reports.OperationsAccount" : "Reports.TradingAccount",
        Columns = new RptCol[]
        {
            new() { Key = "Section", HeaderTextKey = "Reports.Col.Section", Type = RptColType.Text, Width = 150 },
            new() { Key = "Code", HeaderTextKey = "Reports.Col.AccountCode", Type = RptColType.Text, Width = 110 },
            new() { Key = "Name", HeaderTextKey = "Reports.Col.AccountName", Type = RptColType.Text, Width = 300 },
            new() { Key = "Amount", HeaderTextKey = "Reports.Col.Amount", Type = RptColType.Money, Width = 150, Format = "N2" },
        },
        Filters = new RptFlt[]
        {
            new() { Key = "From", LabelTextKey = "Reports.Filter.DateFrom", Kind = RptFltKind.DateFrom, DefaultValue = new DateTime(DateTime.Today.Year, 1, 1) },
            new() { Key = "To", LabelTextKey = "Reports.Filter.DateTo", Kind = RptFltKind.DateTo, DefaultValue = DateTime.Today },
        }
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var from = filters.GetDateOr("From", new DateTime(DateTime.Today.Year, 1, 1));
        var to = filters.GetDateOr("To", DateTime.Today);
        var raw = await RptQuery.IncomeStatementAsync(from, to, ct);
        var accounts = raw.Select(r => new
        {
            Code = Convert.ToString(r.GetValueOrDefault("Code"), CultureInfo.InvariantCulture) ?? "",
            Name = PickName(r),
            Amount = RptWinUtil.Dec(r.GetValueOrDefault("Amount"))
        }).ToList();

        var revenue = accounts.Where(a => a.Code.StartsWith('5')).ToList();
        var cogs = accounts.Where(a => a.Code.StartsWith('4')).ToList();
        var expenses = accounts.Where(a => !a.Code.StartsWith('5') && !a.Code.StartsWith('4')).ToList();

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        void Add(string section, string code, string? name, decimal amount)
            => rows.Add(new Dictionary<string, object?> { ["Section"] = section, ["Code"] = code, ["Name"] = name, ["Amount"] = amount });

        foreach (var a in revenue) Add(T("Reports.Col.Revenue"), a.Code, a.Name, a.Amount);
        var totalRevenue = revenue.Sum(a => a.Amount);
        Add(T("Reports.Col.Revenue"), "", T("Reports.Col.Total"), totalRevenue);
        foreach (var a in cogs) Add(T("Reports.Col.Cost"), a.Code, a.Name, a.Amount);
        var totalCogs = cogs.Sum(a => a.Amount);
        Add(T("Reports.Col.Cost"), "", T("Reports.Col.Total"), totalCogs);
        var gross = totalRevenue - totalCogs;
        Add(T("Reports.Col.Profit"), "", T("Reports.Col.Profit"), gross);
        if (_mode == "Operations")
        {
            foreach (var a in expenses) Add(T("Reports.Col.CostCenter"), a.Code, a.Name, a.Amount);
            var totalExp = expenses.Sum(a => a.Amount);
            Add(T("Reports.Col.CostCenter"), "", T("Reports.Col.Total"), totalExp);
            Add(T("Reports.Col.Profit"), "", T("Reports.Col.Profit"), gross - totalExp);
        }
        return new RptData { Columns = BuildDefinition().Columns, Rows = rows, Totals = null };
    }

    private static string? PickName(IReadOnlyDictionary<string, object?> row)
    {
        var lang = App.CurrentLanguageCode ?? "en";
        var name = lang.StartsWith("ar", StringComparison.OrdinalIgnoreCase)
            ? Convert.ToString(row.GetValueOrDefault("Name"), CultureInfo.InvariantCulture)
            : Convert.ToString(row.GetValueOrDefault("NameEn"), CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(name)
            ? Convert.ToString(row.GetValueOrDefault("Name"), CultureInfo.InvariantCulture)
            : name;
    }
}

// ---- merged from cod/app/src/Views/Reports/CostCenterRptWin.cs ----
/// <summary>Posted journal lines grouped by cost center: debit, credit, balance.</summary>
public sealed class CostCenterRptWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Acc.CostCenterReport",
        TitleTextKey = "Reports.CostCenterReport",
        Columns = new RptCol[]
        {
            new() { Key = "CostCenter", HeaderTextKey = "Reports.Col.CostCenter", Type = RptColType.Text, Width = 180 },
            new() { Key = "Debit", HeaderTextKey = "Reports.Col.Debit", Type = RptColType.Money, Width = 120 },
            new() { Key = "Credit", HeaderTextKey = "Reports.Col.Credit", Type = RptColType.Money, Width = 120 },
            new() { Key = "Balance", HeaderTextKey = "Reports.Col.Balance", Type = RptColType.Money, Width = 120 },
            new() { Key = "LineCount", HeaderTextKey = "Reports.Col.Count", Type = RptColType.WholeNumber, Width = 90 }
        },
        Filters = new RptFlt[]
        {
            new() { Key = "From", LabelTextKey = "Reports.Filter.DateFrom", Kind = RptFltKind.DateFrom, DefaultValue = new DateTime(DateTime.Today.Year, 1, 1) },
            new() { Key = "To", LabelTextKey = "Reports.Filter.DateTo", Kind = RptFltKind.DateTo, DefaultValue = DateTime.Today }
        }
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var from = filters.GetDateOr("From", new DateTime(DateTime.Today.Year, 1, 1));
        var to = filters.GetDateOr("To", DateTime.Today);
        var raw = await RptQuery.CostCenterReportAsync(from, to, ct);
        var def = BuildDefinition();
        var rows = RptWinUtil.Map(raw, def.Columns);
        return new RptData
        {
            Columns = def.Columns,
            Rows = rows,
            Totals = RptWinUtil.SumTotals(rows, def.Columns, T("Reports.Totals"), "CostCenter")
        };
    }
}

// ---- merged from cod/app/src/Views/Reports/CostCenterTrialWin.cs ----
/// <summary>Trial balance per cost center and account.</summary>
public sealed class CostCenterTrialWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Acc.CostCenterBalance",
        TitleTextKey = "Reports.CostCenterBalance",
        Columns = new RptCol[]
        {
            new() { Key = "CostCenter", HeaderTextKey = "Reports.Col.CostCenter", Type = RptColType.Text, Width = 150 },
            new() { Key = "AccountCode", HeaderTextKey = "Reports.Col.AccountCode", Type = RptColType.Text, Width = 110 },
            new() { Key = "AccountName", HeaderTextKey = "Reports.Col.AccountName", Type = RptColType.Text, Width = 240 },
            new() { Key = "Debit", HeaderTextKey = "Reports.Col.Debit", Type = RptColType.Money, Width = 120 },
            new() { Key = "Credit", HeaderTextKey = "Reports.Col.Credit", Type = RptColType.Money, Width = 120 },
            new() { Key = "Balance", HeaderTextKey = "Reports.Col.Balance", Type = RptColType.Money, Width = 120 }
        },
        Filters = new RptFlt[]
        {
            new() { Key = "From", LabelTextKey = "Reports.Filter.DateFrom", Kind = RptFltKind.DateFrom, DefaultValue = new DateTime(DateTime.Today.Year, 1, 1) },
            new() { Key = "To", LabelTextKey = "Reports.Filter.DateTo", Kind = RptFltKind.DateTo, DefaultValue = DateTime.Today }
        }
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var from = filters.GetDateOr("From", new DateTime(DateTime.Today.Year, 1, 1));
        var to = filters.GetDateOr("To", DateTime.Today);
        var raw = await RptQuery.CostCenterTrialAsync(from, to, ct);
        var def = BuildDefinition();
        var rows = RptWinUtil.Map(raw, def.Columns);
        return new RptData
        {
            Columns = def.Columns,
            Rows = rows,
            Totals = RptWinUtil.SumTotals(rows, def.Columns, T("Reports.Totals"), "AccountName")
        };
    }
}

// ---- merged from cod/app/src/Views/Reports/EndOfPeriodWin.cs ----
/// <summary>Stock balances as of a date: movements summed up to the chosen date, valued at movement cost.</summary>
public sealed class EndOfPeriodWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Inv.EndOfPeriod",
        TitleTextKey = "Reports.EndOfPeriod",
        Columns = new RptCol[]
        {
            new() { Key = "MaterialCode", HeaderTextKey = "Reports.Col.MaterialCode", Type = RptColType.Text, Width = 110 },
            new() { Key = "MaterialName", HeaderTextKey = "Reports.Col.MaterialName", Type = RptColType.Text, Width = 220 },
            new() { Key = "Warehouse", HeaderTextKey = "Reports.Col.Warehouse", Type = RptColType.Text, Width = 110 },
            new() { Key = "Unit", HeaderTextKey = "Reports.Col.Unit", Type = RptColType.Text, Width = 70 },
            new() { Key = "QtyIn", HeaderTextKey = "Reports.Col.QtyIn", Type = RptColType.FractionalNumber, Width = 90, Format = "N3" },
            new() { Key = "QtyOut", HeaderTextKey = "Reports.Col.QtyOut", Type = RptColType.FractionalNumber, Width = 90, Format = "N3" },
            new() { Key = "Balance", HeaderTextKey = "Reports.Col.Balance", Type = RptColType.FractionalNumber, Width = 100, Format = "N3" },
            new() { Key = "AvgCost", HeaderTextKey = "Reports.Col.AvgCost", Type = RptColType.Money, Width = 100, Format = "N4" },
            new() { Key = "Value", HeaderTextKey = "Reports.Col.Value", Type = RptColType.Money, Width = 120 }
        },
        Filters = new RptFlt[]
        {
            new() { Key = "AsOf", LabelTextKey = "Reports.Filter.AsOf", Kind = RptFltKind.DateTo, DefaultValue = DateTime.Today }
        }
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var asOf = filters.GetDateOr("AsOf", DateTime.Today);
        var raw = await RptQuery.EndOfPeriodAsync(asOf, ct);
        var def = BuildDefinition();
        var rows = RptWinUtil.Map(raw, def.Columns);
        return new RptData
        {
            Columns = def.Columns,
            Rows = rows,
            Totals = RptWinUtil.SumTotals(rows, def.Columns, T("Reports.Totals"), "MaterialName", "AvgCost")
        };
    }
}

// ---- merged from cod/app/src/Views/Reports/ExchangeDiffWin.cs ----
/// <summary>Posted entries whose exchange rate differs from 1: foreign totals restated at the entry rate and the difference.</summary>
public sealed class ExchangeDiffWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Acc.ExchangeRateDifferences",
        TitleTextKey = "Reports.ExchangeRateDifferences",
        Columns = new RptCol[]
        {
            new() { Key = "DocDate", HeaderTextKey = "Reports.Col.Date", Type = RptColType.Date, Width = 100 },
            new() { Key = "DocNo", HeaderTextKey = "Reports.Col.DocNo", Type = RptColType.Text, Width = 130 },
            new() { Key = "Currency", HeaderTextKey = "Reports.Col.Currency", Type = RptColType.Text, Width = 90 },
            new() { Key = "Rate", HeaderTextKey = "Reports.Col.Rate", Type = RptColType.FractionalNumber, Width = 90, Format = "N4" },
            new() { Key = "Debit", HeaderTextKey = "Reports.Col.Debit", Type = RptColType.Money, Width = 120 },
            new() { Key = "Credit", HeaderTextKey = "Reports.Col.Credit", Type = RptColType.Money, Width = 120 },
            new() { Key = "BaseDebit", HeaderTextKey = "Reports.Col.BaseDebit", Type = RptColType.Money, Width = 120 },
            new() { Key = "Difference", HeaderTextKey = "Reports.Col.Difference", Type = RptColType.Money, Width = 120 }
        },
        Filters = new RptFlt[]
        {
            new() { Key = "From", LabelTextKey = "Reports.Filter.DateFrom", Kind = RptFltKind.DateFrom, DefaultValue = new DateTime(DateTime.Today.Year, 1, 1) },
            new() { Key = "To", LabelTextKey = "Reports.Filter.DateTo", Kind = RptFltKind.DateTo, DefaultValue = DateTime.Today }
        }
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var from = filters.GetDateOr("From", new DateTime(DateTime.Today.Year, 1, 1));
        var to = filters.GetDateOr("To", DateTime.Today);
        var raw = await RptQuery.ExchangeRateDifferencesAsync(from, to, ct);
        var def = BuildDefinition();
        var rows = RptWinUtil.Map(raw, def.Columns);
        return new RptData
        {
            Columns = def.Columns,
            Rows = rows,
            Totals = RptWinUtil.SumTotals(rows, def.Columns, T("Reports.Totals"), "DocNo", "Rate")
        };
    }
}

// ---- merged from cod/app/src/Views/Reports/IncStmtWin.cs ----
/// <summary>Income Statement report.</summary>
public sealed class IncStmtWin : RptBase
{
    // Structured statement: section/subtotal rows must not be flat-summed.
    protected override bool AllowComputedTotals => false;

    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Fin.IncomeStatement",
        TitleTextKey = "Reports.IncomeStatement",
        Columns = new RptCol[]
        {
            new() { Key = "Code", HeaderTextKey = "Reports.Col.AccountCode", Type = RptColType.Text, Width = 110 },
            new() { Key = "Name", HeaderTextKey = "Reports.Col.AccountName", Type = RptColType.Text, Width = 280 },
            new() { Key = "Amount", HeaderTextKey = "Reports.Col.Amount", Type = RptColType.Money, Width = 140, Format = "N2" },
        },
        Filters = new RptFlt[]
        {
            new() { Key = "From", LabelTextKey = "Reports.Filter.DateFrom", Kind = RptFltKind.DateFrom, DefaultValue = new DateTime(DateTime.Today.Year, 1, 1) },
            new() { Key = "To", LabelTextKey = "Reports.Filter.DateTo", Kind = RptFltKind.DateTo, DefaultValue = DateTime.Today },
        }
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var from = filters.GetDateOr("From", new DateTime(DateTime.Today.Year, 1, 1));
        var to = filters.GetDateOr("To", DateTime.Today);
        var raw = await RptQuery.IncomeStatementAsync(from, to, ct);
        var rows = raw.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["Code"] = r.GetValueOrDefault("Code"),
            ["Name"] = PickName(r),
            ["Amount"] = ToDec(r.GetValueOrDefault("Amount")),
        }).ToList();
        return new RptData { Columns = BuildDefinition().Columns, Rows = rows, Totals = null };
    }

    private static string? PickName(IReadOnlyDictionary<string, object?> row)
    {
        var lang = App.CurrentLanguageCode ?? "ar";
        var name = lang.StartsWith("ar", StringComparison.OrdinalIgnoreCase)
            ? Convert.ToString(row.GetValueOrDefault("Name"), CultureInfo.InvariantCulture)
            : Convert.ToString(row.GetValueOrDefault("NameEn"), CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(name)
            ? Convert.ToString(row.GetValueOrDefault("Name"), CultureInfo.InvariantCulture)
            : name;
    }

    private static decimal ToDec(object? v) => v is null ? 0m : Convert.ToDecimal(v, CultureInfo.InvariantCulture);
}

// ---- merged from cod/app/src/Views/Reports/JrnListWin.cs ----
public sealed class JrnListWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Fin.JournalList",
        TitleTextKey = "Reports.JournalList",
        Columns = new RptCol[]
        {
            new() { Key = "EntryDate", HeaderTextKey = "Reports.Col.Date", Type = RptColType.Date, Width = 100 },
            new() { Key = "DocNo", HeaderTextKey = "Reports.Col.DocNo", Type = RptColType.Text, Width = 160 },
            new() { Key = "Description", HeaderTextKey = "Reports.Col.Description", Type = RptColType.Text, Width = 260 },
            new() { Key = "Status", HeaderTextKey = "Reports.Col.Status", Type = RptColType.Text, Width = 90 },
            new() { Key = "Debit", HeaderTextKey = "Reports.Col.Debit", Type = RptColType.Money, Width = 110, Format = "N2" },
            new() { Key = "Credit", HeaderTextKey = "Reports.Col.Credit", Type = RptColType.Money, Width = 110, Format = "N2" },
            new() { Key = "CreatedBy", HeaderTextKey = "Reports.Col.User", Type = RptColType.Text, Width = 120 },
        },
        Filters = new RptFlt[]
        {
            new() { Key = "From", LabelTextKey = "Reports.Filter.DateFrom", Kind = RptFltKind.DateFrom, DefaultValue = new DateTime(DateTime.Today.Year, 1, 1) },
            new() { Key = "To", LabelTextKey = "Reports.Filter.DateTo", Kind = RptFltKind.DateTo, DefaultValue = DateTime.Today },
        }
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var from = filters.GetDateOr("From", new DateTime(DateTime.Today.Year, 1, 1));
        var to = filters.GetDateOr("To", DateTime.Today);
        var raw = await RptQuery.JournalListAsync(from, to, ct);
        var rows = raw.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["EntryDate"] = r.GetValueOrDefault("EntryDate"),
            ["DocNo"] = r.GetValueOrDefault("DocNo"),
            ["Description"] = r.GetValueOrDefault("Description"),
            ["Status"] = r.GetValueOrDefault("Status"),
            ["Debit"] = ToDec(r.GetValueOrDefault("Debit")),
            ["Credit"] = ToDec(r.GetValueOrDefault("Credit")),
            ["CreatedBy"] = r.GetValueOrDefault("CreatedBy"),
        }).ToList();

        return new RptData
        {
            Columns = BuildDefinition().Columns,
            Rows = rows,
            Totals = new Dictionary<string, object?>
            {
                ["Description"] = T("Reports.Totals"),
                ["Debit"] = rows.Sum(x => ToDec(x["Debit"])),
                ["Credit"] = rows.Sum(x => ToDec(x["Credit"])),
            }
        };
    }

    private static decimal ToDec(object? v)
        => v is null ? 0m : Convert.ToDecimal(v, CultureInfo.InvariantCulture);
}

// ---- merged from cod/app/src/Views/Reports/TrialBalanceWin.cs ----
/// <summary>
/// Debit/credit/balance per account for the active company.
/// </summary>
public sealed class TrialBalanceWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Fin.TrialBalance",
        TitleTextKey = "Reports.TrialBalance",
        Columns = new RptCol[]
        {
            new() { Key = "Code", HeaderTextKey = "Reports.Col.AccountCode", Type = RptColType.Text, Width = 110 },
            new() { Key = "Name", HeaderTextKey = "Reports.Col.AccountName", Type = RptColType.Text, Width = 260 },
            new() { Key = "Debit", HeaderTextKey = "Reports.Col.Debit", Type = RptColType.Money, Width = 120, Format = "N2" },
            new() { Key = "Credit", HeaderTextKey = "Reports.Col.Credit", Type = RptColType.Money, Width = 120, Format = "N2" },
            new() { Key = "Balance", HeaderTextKey = "Reports.Col.Balance", Type = RptColType.Money, Width = 120, Format = "N2" },
        },
        Filters = new RptFlt[]
        {
            new() { Key = "From", LabelTextKey = "Reports.Filter.DateFrom", Kind = RptFltKind.DateFrom, DefaultValue = new DateTime(DateTime.Today.Year, 1, 1) },
            new() { Key = "To", LabelTextKey = "Reports.Filter.DateTo", Kind = RptFltKind.DateTo, DefaultValue = DateTime.Today },
        }
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var from = filters.GetDateOr("From", new DateTime(DateTime.Today.Year, 1, 1));
        var to = filters.GetDateOr("To", DateTime.Today);
        var raw = await RptQuery.TrialBalanceAsync(from, to, ct);
        var lang = App.CurrentLanguageCode ?? "ar";
        var rows = raw.Select(r =>
        {
            var name = lang.StartsWith("ar", StringComparison.OrdinalIgnoreCase)
                ? Convert.ToString(r.GetValueOrDefault("Name"), CultureInfo.InvariantCulture)
                : Convert.ToString(r.GetValueOrDefault("NameEn"), CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(name))
                name = Convert.ToString(r.GetValueOrDefault("Name"), CultureInfo.InvariantCulture);
            return (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["Code"] = r.GetValueOrDefault("Code"),
                ["Name"] = name,
                ["Debit"] = ToDec(r.GetValueOrDefault("Debit")),
                ["Credit"] = ToDec(r.GetValueOrDefault("Credit")),
                ["Balance"] = ToDec(r.GetValueOrDefault("Balance")),
            };
        }).ToList();

        var totals = new Dictionary<string, object?>
        {
            ["Code"] = "",
            ["Name"] = T("Reports.Totals"),
            ["Debit"] = rows.Sum(x => ToDec(x["Debit"])),
            ["Credit"] = rows.Sum(x => ToDec(x["Credit"])),
            ["Balance"] = rows.Sum(x => ToDec(x["Balance"])),
        };

        return new RptData { Columns = BuildDefinition().Columns, Rows = rows, Totals = totals };
    }

    private static decimal ToDec(object? v)
        => v is null ? 0m : Convert.ToDecimal(v, CultureInfo.InvariantCulture);
}
