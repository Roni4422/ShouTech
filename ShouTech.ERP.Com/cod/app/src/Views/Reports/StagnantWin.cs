using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ShouTech.App.Reporting;
using ShouTech.App.Services;

namespace ShouTech.App.Views.Reports;

/// <summary>Stock on hand with no outbound movement within the chosen number of days.</summary>
public sealed class StagnantWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Inv.StagnantMaterials",
        TitleTextKey = "Reports.StagnantMaterials",
        Columns = new RptCol[]
        {
            new() { Key = "Warehouse", HeaderTextKey = "Reports.Col.Warehouse", Type = RptColType.Text, Width = 110 },
            new() { Key = "MaterialCode", HeaderTextKey = "Reports.Col.MaterialCode", Type = RptColType.Text, Width = 110 },
            new() { Key = "MaterialName", HeaderTextKey = "Reports.Col.MaterialName", Type = RptColType.Text, Width = 220 },
            new() { Key = "OnHand", HeaderTextKey = "Reports.Col.OnHand", Type = RptColType.FractionalNumber, Width = 100, Format = "N3" },
            new() { Key = "AvgCost", HeaderTextKey = "Reports.Col.AvgCost", Type = RptColType.Money, Width = 100, Format = "N4" },
            new() { Key = "Value", HeaderTextKey = "Reports.Col.Value", Type = RptColType.Money, Width = 120 },
            new() { Key = "LastOutDate", HeaderTextKey = "Reports.Col.LastMovement", Type = RptColType.Date, Width = 110 }
        },
        Filters = new RptFlt[]
        {
            new() { Key = "Days", LabelTextKey = "Reports.Filter.Days", Kind = RptFltKind.Text, DefaultValue = "90" }
        }
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var days = RptWinUtil.ParseInt(filters.GetString("Days"), 90);
        var raw = await RptQuery.StagnantMaterialsAsync(days, ct);
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
