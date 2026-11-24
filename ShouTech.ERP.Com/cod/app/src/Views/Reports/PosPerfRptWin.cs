using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ShouTech.App.Reporting;
using ShouTech.App.Services;

namespace ShouTech.App.Views.Reports;

/// <summary>
/// POS performance by terminal and cashier (sales cycle W9,
/// migration 104): for a date range — optionally narrowed to one
/// terminal and/or one cashier — each (terminal, cashier) pair's
/// sales count and gross, returns, discounts given, drawer paid-outs
/// and average basket. Data comes from PosRepository's session
/// aggregation (documents carry their session since 066; paid-outs
/// are session rows since 104) projected through
/// RptQuery.PosPerformanceAsync — never a company-wide guess.
/// Gated on POS.REPORTS.VIEW by the ribbon route like the other POS
/// reports.
/// </summary>
public sealed class PosPerfRptWin : RptBase
{
    protected override string? ScheduleTintName => "Peach";

    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "POS.Performance",
        TitleTextKey = "POS.PerformanceTitle",
        Columns = new RptCol[]
        {
            new() { Key = "Terminal", HeaderTextKey = "POS.Terminal", Type = RptColType.Text, Width = 110 },
            new() { Key = "Cashier", HeaderTextKey = "POS.Cashier", Type = RptColType.Text, Width = 150 },
            new() { Key = "SalesCount", HeaderTextKey = "POS.PerfCol.SalesCount", Type = RptColType.WholeNumber, Width = 90 },
            new() { Key = "GrossSales", HeaderTextKey = "POS.PerfCol.GrossSales", Type = RptColType.Money, Width = 120 },
            new() { Key = "ReturnsCount", HeaderTextKey = "POS.PerfCol.ReturnsCount", Type = RptColType.WholeNumber, Width = 90 },
            new() { Key = "ReturnsTotal", HeaderTextKey = "POS.PerfCol.ReturnsTotal", Type = RptColType.Money, Width = 120 },
            new() { Key = "Discounts", HeaderTextKey = "POS.PerfCol.Discounts", Type = RptColType.Money, Width = 110 },
            new() { Key = "PaidOuts", HeaderTextKey = "POS.PerfCol.PaidOuts", Type = RptColType.Money, Width = 110 },
            new() { Key = "NetSales", HeaderTextKey = "POS.PerfCol.NetSales", Type = RptColType.Money, Width = 120 },
            new() { Key = "AvgBasket", HeaderTextKey = "POS.PerfCol.AvgBasket", Type = RptColType.Money, Width = 110 }
        },
        Filters = new RptFlt[]
        {
            new() { Key = "From", LabelTextKey = "Reports.Filter.DateFrom", Kind = RptFltKind.DateFrom, DefaultValue = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1) },
            new() { Key = "To", LabelTextKey = "Reports.Filter.DateTo", Kind = RptFltKind.DateTo, DefaultValue = DateTime.Today },
            new() { Key = "Terminal", LabelTextKey = "POS.Terminal", Kind = RptFltKind.Text },
            new() { Key = "Cashier", LabelTextKey = "POS.Cashier", Kind = RptFltKind.Text }
        }
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var from = filters.GetDateOr("From", new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1));
        var to = filters.GetDateOr("To", DateTime.Today);
        var raw = await RptQuery.PosPerformanceAsync(from, to,
            filters.GetString("Terminal"), filters.GetString("Cashier"), ct);
        var def = BuildDefinition();
        var rows = RptWinUtil.Map(raw, def.Columns);
        return new RptData
        {
            Columns = def.Columns,
            Rows = rows,
            Totals = RptWinUtil.SumTotals(rows, def.Columns, T("Reports.Totals"), "Cashier", "AvgBasket")
        };
    }
}
