using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ShouTech.App.Reporting;
using ShouTech.App.Services;

namespace ShouTech.App.Views.Reports;

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
