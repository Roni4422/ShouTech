using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ShouTech.App.Reporting;
using ShouTech.App.Services;

namespace ShouTech.App.Views.Reports;

/// <summary>Manufactured material costs per production order: actual vs standard cost and variance (mfg_orders, migration 034).</summary>
public sealed class MfgCostsWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Manufacturing.ManufacturedMaterialCosts",
        TitleTextKey = "Reports.ManufacturedMaterialCosts",
        Columns = new RptCol[]
        {
            new() { Key = "DocNo", HeaderTextKey = "Reports.Col.DocNo", Type = RptColType.Text, Width = 120 },
            new() { Key = "DocDate", HeaderTextKey = "Reports.Col.Date", Type = RptColType.Date, Width = 100 },
            new() { Key = "ItemCode", HeaderTextKey = "Reports.Col.ItemCode", Type = RptColType.Text, Width = 130 },
            new() { Key = "Qty", HeaderTextKey = "Reports.Col.Qty", Type = RptColType.FractionalNumber, Width = 90, Format = "N3" },
            new() { Key = "UnitCost", HeaderTextKey = "Reports.Col.UnitCost", Type = RptColType.Money, Width = 110, Format = "N4" },
            new() { Key = "StandardUnitCost", HeaderTextKey = "Reports.Col.StandardCost", Type = RptColType.Money, Width = 110, Format = "N4" },
            new() { Key = "TotalCost", HeaderTextKey = "Reports.Col.TotalCost", Type = RptColType.Money, Width = 130 },
            new() { Key = "StandardTotal", HeaderTextKey = "Reports.Col.StandardTotal", Type = RptColType.Money, Width = 130 },
            new() { Key = "Variance", HeaderTextKey = "Reports.Col.Variance", Type = RptColType.Money, Width = 120 },
            new() { Key = "Status", HeaderTextKey = "Reports.Col.Status", Type = RptColType.Text, Width = 90 }
        },
        Filters = Array.Empty<RptFlt>()
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var raw = await RptQuery.MfgOrderCostsAsync(ct);
        var def = BuildDefinition();
        var rows = RptWinUtil.Map(raw, def.Columns);
        return new RptData
        {
            Columns = def.Columns,
            Rows = rows,
            Totals = RptWinUtil.SumTotals(rows, def.Columns, T("Reports.Totals"), "ItemCode", "UnitCost", "StandardUnitCost")
        };
    }
}
