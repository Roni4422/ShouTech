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
/// Manufacturing feasibility for Draft production orders: required raw /
/// component quantities per order line against current stock on hand;
/// shortage and feasibility are computed from those real quantities.
/// </summary>
public sealed class MfgFeasibilityWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Manufacturing.ManufacturingFeasibility",
        TitleTextKey = "Reports.ManufacturingFeasibility",
        Columns = new RptCol[]
        {
            new() { Key = "DocNo", HeaderTextKey = "Reports.Col.DocNo", Type = RptColType.Text, Width = 120 },
            new() { Key = "ItemCode", HeaderTextKey = "Reports.Col.ItemCode", Type = RptColType.Text, Width = 140 },
            new() { Key = "LineItemCode", HeaderTextKey = "Reports.Col.MaterialCode", Type = RptColType.Text, Width = 140 },
            new() { Key = "Required", HeaderTextKey = "Reports.Col.Required", Type = RptColType.FractionalNumber, Width = 110, Format = "N3" },
            new() { Key = "OnHand", HeaderTextKey = "Reports.Col.OnHand", Type = RptColType.FractionalNumber, Width = 110, Format = "N3" },
            new() { Key = "Shortage", HeaderTextKey = "Reports.Col.Shortage", Type = RptColType.FractionalNumber, Width = 110, Format = "N3" },
            new() { Key = "Status", HeaderTextKey = "Reports.Col.Status", Type = RptColType.Text, Width = 120 },
        },
        Filters = Array.Empty<RptFlt>()
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var raw = await RptQuery.MfgFeasibilityAsync(ct);
        var def = BuildDefinition();
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var r in raw)
        {
            var required = RptWinUtil.Dec(r.GetValueOrDefault("Required"));
            var onHand = RptWinUtil.Dec(r.GetValueOrDefault("OnHand"));
            var shortage = Math.Max(0m, required - onHand);
            rows.Add(new Dictionary<string, object?>
            {
                ["DocNo"] = r.GetValueOrDefault("DocNo"),
                ["ItemCode"] = r.GetValueOrDefault("ItemCode"),
                ["LineItemCode"] = r.GetValueOrDefault("LineItemCode"),
                ["Required"] = required,
                ["OnHand"] = onHand,
                ["Shortage"] = shortage,
                ["Status"] = shortage > 0 ? T("Reports.ShortageStatus") : T("Reports.Feasible"),
            });
        }
        return new RptData
        {
            Columns = def.Columns,
            Rows = rows,
            Totals = RptWinUtil.SumTotals(rows, def.Columns, T("Reports.Totals"), "ItemCode")
        };
    }
}
