using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ShouTech.App.Reporting;
using ShouTech.App.Services;

namespace ShouTech.App.Views.Reports;

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
