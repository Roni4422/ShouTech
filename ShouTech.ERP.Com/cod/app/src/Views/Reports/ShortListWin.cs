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
/// Phase 7B - "Shortage list" (Inventory > Movements).
/// Lists every warehouse/material line whose on-hand quantity has fallen
/// to or below its minimum (reorder) level, together with the shortfall
/// quantity and the replenishment value at average cost.
/// </summary>
public sealed class ShortListWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Inv.ShortageList",
        TitleTextKey = "Inventory.ShortageList",
        Columns = new RptCol[]
        {
            new() { Key = "Warehouse", HeaderTextKey = "Reports.Col.Warehouse", Type = RptColType.Text, Width = 120 },
            new() { Key = "MaterialCode", HeaderTextKey = "Reports.Col.MaterialCode", Type = RptColType.Text, Width = 120 },
            new() { Key = "MaterialName", HeaderTextKey = "Reports.Col.MaterialName", Type = RptColType.Text, Width = 240 },
            new() { Key = "Unit", HeaderTextKey = "Reports.Col.Unit", Type = RptColType.Text, Width = 80 },
            new() { Key = "OnHand", HeaderTextKey = "Reports.Col.OnHand", Type = RptColType.FractionalNumber, Width = 110, Format = "N3" },
            new() { Key = "MinQty", HeaderTextKey = "Reports.Col.MinQty", Type = RptColType.FractionalNumber, Width = 110, Format = "N3" },
            new() { Key = "Needed", HeaderTextKey = "Inventory.ShortageNeeded", Type = RptColType.FractionalNumber, Width = 110, Format = "N3" },
            new() { Key = "AvgCost", HeaderTextKey = "Reports.Col.AvgCost", Type = RptColType.Money, Width = 100, Format = "N4" },
            new() { Key = "NeededValue", HeaderTextKey = "Reports.Col.Value", Type = RptColType.Money, Width = 120, Format = "N2" },
        },
        Filters = new RptFlt[]
        {
            new() { Key = "Warehouse", LabelTextKey = "Reports.Filter.Warehouse", Kind = RptFltKind.Warehouse },
            new() { Key = "Material", LabelTextKey = "Inventory.MaterialFilter", Kind = RptFltKind.Material },
            new() { Key = "Group", LabelTextKey = "Inventory.MaterialGroupFilter", Kind = RptFltKind.MaterialGroup },
        }
    };

    protected override IReadOnlyList<KeyValuePair<string, string>> LoadWarehouseChoices()
        => RptQuery.WarehouseChoices();

    protected override IReadOnlyList<KeyValuePair<string, string>> LoadMaterialGroupChoices()
    {
        var choices = new List<KeyValuePair<string, string>>
        {
            new("", T("Reports.Filter.All"))
        };
        choices.AddRange(MatRepo.LoadGroups()
            .Where(group => !group.IsDeleted)
            .Select(group => new KeyValuePair<string, string>(
                group.Code,
                string.Equals(App.CurrentLanguageCode, "ar", StringComparison.OrdinalIgnoreCase)
                    ? group.Name
                    : string.IsNullOrWhiteSpace(group.NameEn) ? group.Name : group.NameEn)));
        return choices;
    }

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var wh = filters.GetString("Warehouse");
        var material = filters.GetString("Material");
        var group = filters.GetString("Group");

        // Pull every balance line and keep only shortage lines client-side,
        // so both "below minimum" and "out of stock" cases are covered.
        var raw = await RptQuery.StockBalancesAsync(wh, material, group, null, ct);

        var rows = raw
            .Select(r => new
            {
                Warehouse = r.GetValueOrDefault("Warehouse"),
                MaterialCode = r.GetValueOrDefault("MaterialCode"),
                MaterialName = r.GetValueOrDefault("MaterialName"),
                Unit = r.GetValueOrDefault("Unit"),
                OnHand = ToDec(r.GetValueOrDefault("OnHand")),
                MinQty = ToDec(r.GetValueOrDefault("MinQty")),
                AvgCost = ToDec(r.GetValueOrDefault("AvgCost")),
            })
            .Where(x => x.MinQty > 0 && x.OnHand <= x.MinQty || x.OnHand < 0)
            .OrderBy(x => x.MaterialCode)
            .Select(x => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["Warehouse"] = x.Warehouse,
                ["MaterialCode"] = x.MaterialCode,
                ["MaterialName"] = x.MaterialName,
                ["Unit"] = x.Unit,
                ["OnHand"] = x.OnHand,
                ["MinQty"] = x.MinQty,
                ["Needed"] = Math.Max(0m, x.MinQty - x.OnHand),
                ["AvgCost"] = x.AvgCost,
                ["NeededValue"] = Math.Max(0m, x.MinQty - x.OnHand) * x.AvgCost,
            })
            .ToList();

        return new RptData
        {
            Columns = BuildDefinition().Columns,
            Rows = rows,
            Totals = new Dictionary<string, object?>
            {
                ["MaterialName"] = T("Reports.Totals"),
                ["Needed"] = rows.Sum(x => ToDec(x["Needed"])),
                ["NeededValue"] = rows.Sum(x => ToDec(x["NeededValue"])),
            }
        };
    }

    private static decimal ToDec(object? v)
        => v is null ? 0m : Convert.ToDecimal(v, CultureInfo.InvariantCulture);
}
