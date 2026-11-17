using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ShouTech.App.Reporting;
using ShouTech.App.Services;

namespace ShouTech.App.Views.Reports;

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
