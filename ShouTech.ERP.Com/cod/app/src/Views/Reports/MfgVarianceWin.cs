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
/// Manufacturing cost variance: actual vs standard cost on mfg_order_lines
/// (migration 034). One class serves four ribbon keys via the mode argument:
/// Total / Raw / Overhead / Finished (default Total). Variance = actual
/// amount − qty × standard unit cost, grouped per order and line.
/// </summary>
public sealed class MfgVarianceWin : RptBase
{
    private readonly string _mode;
    public MfgVarianceWin() : this("Total") { }
    public MfgVarianceWin(string mode) => _mode = mode is "Raw" or "Overhead" or "Finished" ? mode : "Total";

    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = _mode switch
        {
            "Raw" => "Manufacturing.RawMaterialCostVariance",
            "Overhead" => "Manufacturing.AdditionalCostVariance",
            "Finished" => "Manufacturing.ManufacturedMaterialCostVariance",
            _ => "Manufacturing.ManufacturingCostVariance"
        },
        TitleTextKey = _mode switch
        {
            "Raw" => "Reports.RawMaterialCostVariance",
            "Overhead" => "Reports.AdditionalCostVariance",
            "Finished" => "Reports.ManufacturedMaterialCostVariance",
            _ => "Reports.ManufacturingCostVariance"
        },
        Columns = new RptCol[]
        {
            new() { Key = "DocNo", HeaderTextKey = "Reports.Col.DocNo", Type = RptColType.Text, Width = 120 },
            new() { Key = "ItemCode", HeaderTextKey = "Reports.Col.ItemCode", Type = RptColType.Text, Width = 140 },
            new() { Key = "LineKind", HeaderTextKey = "Reports.Col.LineKind", Type = RptColType.Text, Width = 120 },
            new() { Key = "Qty", HeaderTextKey = "Reports.Col.Qty", Type = RptColType.FractionalNumber, Width = 100, Format = "N3" },
            new() { Key = "Amount", HeaderTextKey = "Reports.Col.Amount", Type = RptColType.Money, Width = 130, Format = "N2" },
            new() { Key = "StandardAmount", HeaderTextKey = "Reports.Col.StandardTotal", Type = RptColType.Money, Width = 130, Format = "N2" },
            new() { Key = "Variance", HeaderTextKey = "Reports.Col.Variance", Type = RptColType.Money, Width = 130, Format = "N2" },
        },
        Filters = Array.Empty<RptFlt>()
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var raw = await RptQuery.MfgVarianceAsync(_mode, ct);
        var def = BuildDefinition();
        var rows = RptWinUtil.Map(raw, def.Columns);
        return new RptData
        {
            Columns = def.Columns,
            Rows = rows,
            Totals = RptWinUtil.SumTotals(rows, def.Columns, T("Reports.Totals"), "ItemCode")
        };
    }
}
