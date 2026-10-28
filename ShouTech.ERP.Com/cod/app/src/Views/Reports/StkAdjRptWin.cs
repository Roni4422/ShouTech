using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ShouTech.App.Reporting;
using ShouTech.App.Services;

namespace ShouTech.App.Views.Reports;

public sealed class StkAdjRptWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Inv.StockMovements",
        TitleTextKey = "Reports.StockAdjustment",
        Columns = new RptCol[]
        {
            new() { Key = "MoveDate", HeaderTextKey = "Reports.Col.Date", Type = RptColType.Date, Width = 100 },
            new() { Key = "Warehouse", HeaderTextKey = "Reports.Col.Warehouse", Width = 110 },
            new() { Key = "MoveType", HeaderTextKey = "Reports.Col.MoveType", Width = 80 },
            new() { Key = "DocNo", HeaderTextKey = "Reports.Col.DocNo", Width = 120 },
            new() { Key = "MaterialCode", HeaderTextKey = "Reports.Col.MaterialCode", Width = 110 },
            new() { Key = "MaterialName", HeaderTextKey = "Reports.Col.MaterialName", Width = 200 },
            new() { Key = "QtyIn", HeaderTextKey = "Reports.Col.QtyIn", Type = RptColType.FractionalNumber, Width = 90, Format = "N3" },
            new() { Key = "QtyOut", HeaderTextKey = "Reports.Col.QtyOut", Type = RptColType.FractionalNumber, Width = 90, Format = "N3" },
            new() { Key = "UnitCost", HeaderTextKey = "Reports.Col.AvgCost", Type = RptColType.Money, Width = 90, Format = "N4" },
            new() { Key = "MoveValue", HeaderTextKey = "Reports.Col.Value", Type = RptColType.Money, Width = 110, Format = "N2" },
            new() { Key = "Note", HeaderTextKey = "Reports.Col.Note", Width = 240 },
        },
        Filters = new RptFlt[]
        {
            new() { Key = "Material", LabelTextKey = "Inventory.MaterialFilter", Kind = RptFltKind.Material },
            new() { Key = "Warehouse", LabelTextKey = "Reports.Filter.Warehouse", Kind = RptFltKind.Warehouse },
            new() { Key = "Document", LabelTextKey = "Reports.Col.DocNo", Kind = RptFltKind.Text },
            new() { Key = "From", LabelTextKey = "Reports.Filter.DateFrom", Kind = RptFltKind.DateFrom, DefaultValue = new DateTime(DateTime.Today.Year, 1, 1) },
            new() { Key = "To", LabelTextKey = "Reports.Filter.DateTo", Kind = RptFltKind.DateTo, DefaultValue = DateTime.Today },
        }
    };

    protected override IReadOnlyList<KeyValuePair<string, string>> LoadWarehouseChoices()
        => RptQuery.WarehouseChoices();

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var material = filters.GetString("Material");
        var warehouse = filters.GetString("Warehouse");
        var document = filters.GetString("Document");
        var from = filters.GetDateOr("From", new DateTime(DateTime.Today.Year, 1, 1));
        var to = filters.GetDateOr("To", DateTime.Today);
        var raw = await RptQuery.StockAdjustmentsAsync(material, warehouse, document, from, to, ct);
        var rows = raw.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["MoveDate"] = r.GetValueOrDefault("MoveDate"),
            ["Warehouse"] = FormatWarehouse(r),
            ["MoveType"] = r.GetValueOrDefault("MoveType"),
            ["DocNo"] = r.GetValueOrDefault("DocNo"),
            ["MaterialCode"] = r.GetValueOrDefault("MaterialCode"),
            ["MaterialName"] = r.GetValueOrDefault("MaterialName"),
            ["QtyIn"] = Dec(r.GetValueOrDefault("QtyIn")),
            ["QtyOut"] = Dec(r.GetValueOrDefault("QtyOut")),
            ["UnitCost"] = Dec(r.GetValueOrDefault("UnitCost")),
            ["MoveValue"] = (Dec(r.GetValueOrDefault("QtyIn")) - Dec(r.GetValueOrDefault("QtyOut")))
                * Dec(r.GetValueOrDefault("UnitCost")),
            ["Note"] = r.GetValueOrDefault("Note"),
        }).ToList();
        return new RptData
        {
            Columns = BuildDefinition().Columns,
            Rows = rows,
            Totals = new Dictionary<string, object?>
            {
                ["MaterialName"] = T("Reports.Totals"),
                ["QtyIn"] = rows.Sum(x => Dec(x["QtyIn"])),
                ["QtyOut"] = rows.Sum(x => Dec(x["QtyOut"])),
                ["MoveValue"] = rows.Sum(x => Dec(x["MoveValue"])),
            }
        };
    }

    private static decimal Dec(object? v) => v is null ? 0m : Convert.ToDecimal(v, CultureInfo.InvariantCulture);

    private static string FormatWarehouse(IReadOnlyDictionary<string, object?> row)
    {
        var code = Convert.ToString(row.GetValueOrDefault("Warehouse"), CultureInfo.InvariantCulture) ?? "";
        var nameAr = Convert.ToString(row.GetValueOrDefault("WarehouseName"), CultureInfo.InvariantCulture) ?? "";
        var nameEn = Convert.ToString(row.GetValueOrDefault("WarehouseNameEn"), CultureInfo.InvariantCulture) ?? "";
        var name = string.Equals(App.CurrentLanguageCode, "ar", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(nameEn)
            ? nameAr
            : nameEn;
        return string.IsNullOrWhiteSpace(name) ? code : $"{code} — {name}";
    }
}
