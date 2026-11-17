using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ShouTech.App.Reporting;
using ShouTech.App.Services;

namespace ShouTech.App.Views.Reports;


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
