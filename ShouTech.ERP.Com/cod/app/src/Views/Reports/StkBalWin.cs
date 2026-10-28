using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ShouTech.App.Reporting;
using ShouTech.App.Services;

namespace ShouTech.App.Views.Reports;

/// <summary>Warehouse stock balances with inventory valuation.</summary>
public sealed class StkBalWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Inv.StockBalances",
        TitleTextKey = "Reports.StockBalances",
        Columns = new RptCol[]
        {
            new() { Key = "Warehouse", HeaderTextKey = "Reports.Col.Warehouse", Type = RptColType.Text, Width = 120 },
            new() { Key = "MaterialCode", HeaderTextKey = "Reports.Col.MaterialCode", Type = RptColType.Text, Width = 120 },
            new() { Key = "MaterialName", HeaderTextKey = "Reports.Col.MaterialName", Type = RptColType.Text, Width = 240 },
            new() { Key = "Unit", HeaderTextKey = "Reports.Col.Unit", Type = RptColType.Text, Width = 80 },
            new() { Key = "Qty", HeaderTextKey = "Reports.Col.Qty", Type = RptColType.FractionalNumber, Width = 100, Format = "N3" },
            new() { Key = "Reserved", HeaderTextKey = "Reports.Col.Reserved", Type = RptColType.FractionalNumber, Width = 100, Format = "N3" },
            new() { Key = "OnHand", HeaderTextKey = "Reports.Col.OnHand", Type = RptColType.FractionalNumber, Width = 110, Format = "N3" },
            new() { Key = "MinQty", HeaderTextKey = "Reports.Col.MinQty", Type = RptColType.FractionalNumber, Width = 110, Format = "N3" },
            new() { Key = "AvgCost", HeaderTextKey = "Reports.Col.AvgCost", Type = RptColType.Money, Width = 100, Format = "N4" },
            new() { Key = "Value", HeaderTextKey = "Reports.Col.Value", Type = RptColType.Money, Width = 120, Format = "N2" },
        },
        Filters = new RptFlt[]
        {
            new() { Key = "Warehouse", LabelTextKey = "Reports.Filter.Warehouse", Kind = RptFltKind.Warehouse },
            new() { Key = "Material", LabelTextKey = "Inventory.MaterialFilter", Kind = RptFltKind.Material },
            new() { Key = "Group", LabelTextKey = "Inventory.MaterialGroupFilter", Kind = RptFltKind.MaterialGroup },
            new()
            {
                Key = "State",
                LabelTextKey = "Inventory.StockStatusFilter",
                Kind = RptFltKind.Choice,
                Choices = new[]
                {
                    new KeyValuePair<string, string>("", "Reports.Filter.All"),
                    new KeyValuePair<string, string>("IN_STOCK", "Inventory.StockStatus.InStock"),
                    new KeyValuePair<string, string>("OUT_OF_STOCK", "Inventory.StockStatus.OutOfStock"),
                    new KeyValuePair<string, string>("REORDER", "Inventory.StockStatus.Reorder"),
                    new KeyValuePair<string, string>("NEGATIVE", "Inventory.StockStatus.Negative")
                }
            }
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
        var state = filters.GetString("State");
        var raw = await RptQuery.StockBalancesAsync(wh, material, group, state, ct);
        var rows = raw.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["Warehouse"] = r.GetValueOrDefault("Warehouse"),
            ["MaterialCode"] = r.GetValueOrDefault("MaterialCode"),
            ["MaterialName"] = r.GetValueOrDefault("MaterialName"),
            ["Unit"] = r.GetValueOrDefault("Unit"),
            ["Qty"] = ToDec(r.GetValueOrDefault("Qty")),
            ["Reserved"] = ToDec(r.GetValueOrDefault("Reserved")),
            ["OnHand"] = ToDec(r.GetValueOrDefault("OnHand")),
            ["MinQty"] = ToDec(r.GetValueOrDefault("MinQty")),
            ["AvgCost"] = ToDec(r.GetValueOrDefault("AvgCost")),
            ["Value"] = ToDec(r.GetValueOrDefault("Value")),
        }).ToList();

        return new RptData
        {
            Columns = BuildDefinition().Columns,
            Rows = rows,
            Totals = new Dictionary<string, object?>
            {
                ["MaterialName"] = T("Reports.Totals"),
                ["Qty"] = rows.Sum(x => ToDec(x["Qty"])),
                ["Reserved"] = rows.Sum(x => ToDec(x["Reserved"])),
                ["OnHand"] = rows.Sum(x => ToDec(x["OnHand"])),
                ["Value"] = rows.Sum(x => ToDec(x["Value"])),
            }
        };
    }

    private static decimal ToDec(object? v)
        => v is null ? 0m : Convert.ToDecimal(v, CultureInfo.InvariantCulture);
}
