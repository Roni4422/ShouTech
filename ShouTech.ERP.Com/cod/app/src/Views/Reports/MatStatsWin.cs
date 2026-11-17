using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ShouTech.App.Reporting;
using ShouTech.App.Services;

namespace ShouTech.App.Views.Reports;

/// <summary>Per-material statistics: qty sold/purchased, revenue, cost at average cost, and margin.</summary>
public sealed class MatStatsWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Inv.MaterialStatistics",
        TitleTextKey = "Reports.MaterialStatistics",
        Columns = new RptCol[]
        {
            new() { Key = "MaterialCode", HeaderTextKey = "Reports.Col.MaterialCode", Type = RptColType.Text, Width = 110 },
            new() { Key = "MaterialName", HeaderTextKey = "Reports.Col.MaterialName", Type = RptColType.Text, Width = 220 },
            new() { Key = "QtySold", HeaderTextKey = "Reports.Col.QtySold", Type = RptColType.FractionalNumber, Width = 90, Format = "N3" },
            new() { Key = "Revenue", HeaderTextKey = "Reports.Col.Revenue", Type = RptColType.Money, Width = 120 },
            new() { Key = "QtyPurchased", HeaderTextKey = "Reports.Col.QtyPurchased", Type = RptColType.FractionalNumber, Width = 100, Format = "N3" },
            new() { Key = "PurchaseAmount", HeaderTextKey = "Reports.Col.PurchaseAmount", Type = RptColType.Money, Width = 120 },
            new() { Key = "Cost", HeaderTextKey = "Reports.Col.Cost", Type = RptColType.Money, Width = 120 },
            new() { Key = "Margin", HeaderTextKey = "Reports.Col.Margin", Type = RptColType.Money, Width = 120 },
            new() { Key = "MarginPct", HeaderTextKey = "Reports.Col.MarginPct", Type = RptColType.Percent, Width = 90 }
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
        var raw = await RptQuery.MaterialStatisticsAsync(from, to, ct);
        var def = BuildDefinition();
        var rows = RptWinUtil.Map(raw, def.Columns);
        return new RptData
        {
            Columns = def.Columns,
            Rows = rows,
            Totals = RptWinUtil.SumTotals(rows, def.Columns, T("Reports.Totals"), "MaterialName")
        };
    }
}
