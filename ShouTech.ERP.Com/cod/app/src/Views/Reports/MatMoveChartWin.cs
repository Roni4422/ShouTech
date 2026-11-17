using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ShouTech.App.Reporting;
using ShouTech.App.Services;

namespace ShouTech.App.Views.Reports;

/// <summary>Per-material monthly movement schedule (qty in/out) for a chosen year.</summary>
public sealed class MatMoveChartWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Inv.MaterialMovementChart",
        TitleTextKey = "Reports.MaterialMovementChart",
        Columns = new RptCol[]
        {
            new() { Key = "MaterialCode", HeaderTextKey = "Reports.Col.MaterialCode", Type = RptColType.Text, Width = 110 },
            new() { Key = "MaterialName", HeaderTextKey = "Reports.Col.MaterialName", Type = RptColType.Text, Width = 220 },
            new() { Key = "Month", HeaderTextKey = "Reports.Col.Month", Type = RptColType.Text, Width = 90 },
            new() { Key = "QtyIn", HeaderTextKey = "Reports.Col.QtyIn", Type = RptColType.FractionalNumber, Width = 90, Format = "N3" },
            new() { Key = "QtyOut", HeaderTextKey = "Reports.Col.QtyOut", Type = RptColType.FractionalNumber, Width = 90, Format = "N3" },
            new() { Key = "NetQty", HeaderTextKey = "Reports.Col.NetQty", Type = RptColType.FractionalNumber, Width = 100, Format = "N3" },
            new() { Key = "MoveCount", HeaderTextKey = "Reports.Col.MoveCount", Type = RptColType.WholeNumber, Width = 100 }
        },
        Filters = new RptFlt[]
        {
            new() { Key = "Year", LabelTextKey = "Reports.Filter.Year", Kind = RptFltKind.Text, DefaultValue = DateTime.Today.Year.ToString(CultureInfo.InvariantCulture) }
        }
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var year = RptWinUtil.ParseInt(filters.GetString("Year"), DateTime.Today.Year);
        var raw = await RptQuery.MaterialMovementChartAsync(year, ct);
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
