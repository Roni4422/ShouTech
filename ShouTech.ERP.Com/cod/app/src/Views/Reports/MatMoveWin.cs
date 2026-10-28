using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ShouTech.App.Reporting;
using ShouTech.App.Services;

namespace ShouTech.App.Views.Reports;

/// <summary>Material movement report.</summary>
public sealed class MatMoveWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Inv.MaterialMoves",
        TitleTextKey = "Reports.MaterialMoves",
        Columns = new RptCol[]
        {
            new() { Key = "MoveDate", HeaderTextKey = "Reports.Col.Date", Type = RptColType.Date, Width = 100 },
            new() { Key = "MoveType", HeaderTextKey = "Reports.Col.MoveType", Type = RptColType.Text, Width = 100 },
            new() { Key = "DocNo", HeaderTextKey = "Reports.Col.DocNo", Type = RptColType.Text, Width = 120 },
            new() { Key = "Warehouse", HeaderTextKey = "Reports.Col.Warehouse", Type = RptColType.Text, Width = 100 },
            new() { Key = "MaterialCode", HeaderTextKey = "Reports.Col.MaterialCode", Type = RptColType.Text, Width = 110 },
            new() { Key = "MaterialName", HeaderTextKey = "Reports.Col.MaterialName", Type = RptColType.Text, Width = 200 },
            new() { Key = "QtyIn", HeaderTextKey = "Reports.Col.QtyIn", Type = RptColType.FractionalNumber, Width = 90, Format = "N3" },
            new() { Key = "QtyOut", HeaderTextKey = "Reports.Col.QtyOut", Type = RptColType.FractionalNumber, Width = 90, Format = "N3" },
            new() { Key = "UnitCost", HeaderTextKey = "Reports.Col.AvgCost", Type = RptColType.Money, Width = 90, Format = "N4" },
            new() { Key = "MoveValue", HeaderTextKey = "Reports.Col.Value", Type = RptColType.Money, Width = 110, Format = "N2" },
            new() { Key = "Balance", HeaderTextKey = "Reports.Col.Balance", Type = RptColType.FractionalNumber, Width = 110, Format = "N3" },
            new() { Key = "Note", HeaderTextKey = "Reports.Col.Note", Type = RptColType.Text, Width = 240 },
        },
        Filters = new RptFlt[]
        {
            new() { Key = "Material", LabelTextKey = "Inventory.MaterialFilter", Kind = RptFltKind.Material },
            new() { Key = "Warehouse", LabelTextKey = "Reports.Filter.Warehouse", Kind = RptFltKind.Warehouse },
            new()
            {
                Key = "Type",
                LabelTextKey = "Reports.Col.MoveType",
                Kind = RptFltKind.Choice,
                Choices = new[]
                {
                    new KeyValuePair<string, string>("", "Reports.Filter.All"),
                    new KeyValuePair<string, string>("PUR", "Inventory.MoveType.Purchase"),
                    new KeyValuePair<string, string>("SAL", "Inventory.MoveType.Sale"),
                    new KeyValuePair<string, string>("PRT", "Inventory.MoveType.PurchaseReturn"),
                    new KeyValuePair<string, string>("SRT", "Inventory.MoveType.SalesReturn"),
                    new KeyValuePair<string, string>("POS", "Inventory.MoveType.PointOfSale"),
                    new KeyValuePair<string, string>("ADJ", "Inventory.MoveType.Adjustment"),
                    new KeyValuePair<string, string>("COUNT_CLOSE", "Inventory.MoveType.StockCount"),
                    new KeyValuePair<string, string>("TRANSFER_IN", "Inventory.MoveType.TransferIn"),
                    new KeyValuePair<string, string>("TRANSFER_OUT", "Inventory.MoveType.TransferOut")
                }
            },
            new() { Key = "Document", LabelTextKey = "Reports.Col.DocNo", Kind = RptFltKind.Text },
            new() { Key = "From", LabelTextKey = "Reports.Filter.DateFrom", Kind = RptFltKind.DateFrom, DefaultValue = new DateTime(DateTime.Today.Year, 1, 1) },
            new() { Key = "To", LabelTextKey = "Reports.Filter.DateTo", Kind = RptFltKind.DateTo, DefaultValue = DateTime.Today },
        }
    };

    protected override IReadOnlyList<KeyValuePair<string, string>> LoadWarehouseChoices()
        => RptQuery.WarehouseChoices();

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var mat = filters.GetString("Material");
        var warehouse = filters.GetString("Warehouse");
        var type = filters.GetString("Type");
        var document = filters.GetString("Document");
        var from = filters.GetDateOr("From", new DateTime(DateTime.Today.Year, 1, 1));
        var to = filters.GetDateOr("To", DateTime.Today);
        var raw = await RptQuery.MaterialMovesAsync(mat, warehouse, type, document, from, to, ct);
        var isArabic = string.Equals(App.CurrentLanguageCode, "ar", StringComparison.OrdinalIgnoreCase);
        var rows = raw.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["MoveDate"] = r.GetValueOrDefault("MoveDate"),
            ["MoveType"] = FormatMoveType(r.GetValueOrDefault("MoveType")?.ToString()),
            ["DocNo"] = r.GetValueOrDefault("DocNo"),
            ["Warehouse"] = FormatWarehouse(
                r.GetValueOrDefault("Warehouse")?.ToString(),
                isArabic
                    ? r.GetValueOrDefault("WarehouseName")?.ToString()
                    : r.GetValueOrDefault("WarehouseNameEn")?.ToString()),
            ["MaterialCode"] = r.GetValueOrDefault("MaterialCode"),
            ["MaterialName"] = isArabic
                ? r.GetValueOrDefault("MaterialName")
                : string.IsNullOrWhiteSpace(r.GetValueOrDefault("MaterialNameEn")?.ToString())
                    ? r.GetValueOrDefault("MaterialName")
                    : r.GetValueOrDefault("MaterialNameEn"),
            ["QtyIn"] = ToDec(r.GetValueOrDefault("QtyIn")),
            ["QtyOut"] = ToDec(r.GetValueOrDefault("QtyOut")),
            ["UnitCost"] = ToDec(r.GetValueOrDefault("UnitCost")),
            ["MoveValue"] = ToDec(r.GetValueOrDefault("MoveValue")),
            ["Balance"] = ToDec(r.GetValueOrDefault("Balance")),
            ["Note"] = r.GetValueOrDefault("Note"),
        }).ToList();

        return new RptData
        {
            Columns = BuildDefinition().Columns,
            Rows = rows,
            Totals = new Dictionary<string, object?>
            {
                ["MaterialName"] = T("Reports.Totals"),
                ["QtyIn"] = rows.Sum(x => ToDec(x["QtyIn"])),
                ["QtyOut"] = rows.Sum(x => ToDec(x["QtyOut"])),
                ["MoveValue"] = rows.Sum(x => ToDec(x["MoveValue"])),
            }
        };
    }

    private static decimal ToDec(object? v)
        => v is null ? 0m : Convert.ToDecimal(v, CultureInfo.InvariantCulture);

    private static string FormatMoveType(string? code)
    {
        var key = code switch
        {
            "PUR" => "Inventory.MoveType.Purchase",
            "SAL" => "Inventory.MoveType.Sale",
            "PRT" => "Inventory.MoveType.PurchaseReturn",
            "SRT" => "Inventory.MoveType.SalesReturn",
            "POS" => "Inventory.MoveType.PointOfSale",
            "ADJ" => "Inventory.MoveType.Adjustment",
            "COUNT_CLOSE" => "Inventory.MoveType.StockCount",
            "TRANSFER_IN" => "Inventory.MoveType.TransferIn",
            "TRANSFER_OUT" => "Inventory.MoveType.TransferOut",
            _ => null
        };

        return key == null ? code ?? string.Empty : T(key);
    }

    private static string FormatWarehouse(string? code, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return code ?? string.Empty;
        return string.IsNullOrWhiteSpace(code) ? name : $"{code} - {name}";
    }
}
