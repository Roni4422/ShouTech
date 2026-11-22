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
/// Open goods-received-not-invoiced quantities and their remaining base-currency
/// value, aged from the receipt date as of the selected reporting date.
/// </summary>
public sealed class GrniWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Purchasing.GRNI",
        TitleTextKey = "Reports.GoodsReceivedNotInvoiced",
        Columns = new RptCol[]
        {
            new() { Key = "ReceiptDate", HeaderTextKey = "Reports.Col.Date", Type = RptColType.Date, Width = 95 },
            new() { Key = "AgeDays", HeaderTextKey = "Reports.Col.AgeDays", Type = RptColType.WholeNumber, Width = 80 },
            new() { Key = "ReceiptNo", HeaderTextKey = "Reports.Col.ReceiptNo", Type = RptColType.Text, Width = 105 },
            new() { Key = "PurchaseOrderNo", HeaderTextKey = "Reports.Col.PurchaseOrder", Type = RptColType.Text, Width = 105 },
            new() { Key = "SupplierName", HeaderTextKey = "Reports.Col.PartyName", Type = RptColType.Text, Width = 180 },
            new() { Key = "MaterialCode", HeaderTextKey = "Reports.Col.MaterialCode", Type = RptColType.Text, Width = 110 },
            new() { Key = "Description", HeaderTextKey = "Reports.Col.Description", Type = RptColType.Text, Width = 190 },
            new() { Key = "ReceivedQty", HeaderTextKey = "Reports.Col.ReceivedQty", Type = RptColType.FractionalNumber, Width = 90, Format = "N3" },
            new() { Key = "InvoicedQty", HeaderTextKey = "Reports.Col.InvoicedQty", Type = RptColType.FractionalNumber, Width = 90, Format = "N3" },
            new() { Key = "ReturnedQty", HeaderTextKey = "Reports.Col.ReturnedQty", Type = RptColType.FractionalNumber, Width = 90, Format = "N3" },
            new() { Key = "RemainingQty", HeaderTextKey = "Reports.Col.RemainingQty", Type = RptColType.FractionalNumber, Width = 95, Format = "N3" },
            new() { Key = "RemainingBaseAmount", HeaderTextKey = "Reports.Col.RemainingBaseAmount", Type = RptColType.Money, Width = 125, Format = "N2" },
            new() { Key = "Bucket0_30", HeaderTextKey = "Reports.Col.Aging0_30", Type = RptColType.Money, Width = 105, Format = "N2" },
            new() { Key = "Bucket31_60", HeaderTextKey = "Reports.Col.Aging31_60", Type = RptColType.Money, Width = 105, Format = "N2" },
            new() { Key = "Bucket61_90", HeaderTextKey = "Reports.Col.Aging61_90", Type = RptColType.Money, Width = 105, Format = "N2" },
            new() { Key = "Bucket90Plus", HeaderTextKey = "Reports.Col.Aging90Plus", Type = RptColType.Money, Width = 105, Format = "N2" },
        },
        Filters = new RptFlt[]
        {
            new() { Key = "AsOf", LabelTextKey = "Reports.Filter.AsOf", Kind = RptFltKind.DateTo, DefaultValue = DateTime.Today },
            new() { Key = "Search", LabelTextKey = "Reports.Filter.Search", Kind = RptFltKind.Text },
        }
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var asOf = filters.GetDateOr("AsOf", DateTime.Today).Date;
        var raw = await RptQuery.GoodsReceivedNotInvoicedAsync(DateTime.MinValue, asOf, ct);
        var rows = RptWinUtil.Map(raw, BuildDefinition().Columns.Take(12).ToArray());
        var agedRows = new List<IReadOnlyDictionary<string, object?>>(rows.Count);

        foreach (var row in rows)
        {
            var receiptDate = RptWinUtil.ParseDate(row.GetValueOrDefault("ReceiptDate"))
                ?? throw new InvalidOperationException("A posted goods receipt has an invalid document date.");
            var ageDays = Math.Max(0, (asOf - receiptDate.Value.Date).Days);
            var remainingAmount = RptWinUtil.Dec(row.GetValueOrDefault("RemainingBaseAmount"));
            var values = new Dictionary<string, object?>(row, StringComparer.Ordinal)
            {
                ["AgeDays"] = ageDays,
                ["Bucket0_30"] = ageDays <= 30 ? remainingAmount : 0m,
                ["Bucket31_60"] = ageDays is > 30 and <= 60 ? remainingAmount : 0m,
                ["Bucket61_90"] = ageDays is > 60 and <= 90 ? remainingAmount : 0m,
                ["Bucket90Plus"] = ageDays > 90 ? remainingAmount : 0m,
            };
            agedRows.Add(values);
        }

        var search = filters.GetString("Search")?.Trim();
        if (!string.IsNullOrWhiteSpace(search))
            agedRows = agedRows.Where(row =>
                Convert.ToString(row.GetValueOrDefault("ReceiptNo"), CultureInfo.InvariantCulture)?.Contains(search, StringComparison.OrdinalIgnoreCase) == true ||
                Convert.ToString(row.GetValueOrDefault("PurchaseOrderNo"), CultureInfo.InvariantCulture)?.Contains(search, StringComparison.OrdinalIgnoreCase) == true ||
                Convert.ToString(row.GetValueOrDefault("SupplierName"), CultureInfo.InvariantCulture)?.Contains(search, StringComparison.OrdinalIgnoreCase) == true ||
                Convert.ToString(row.GetValueOrDefault("MaterialCode"), CultureInfo.InvariantCulture)?.Contains(search, StringComparison.OrdinalIgnoreCase) == true ||
                Convert.ToString(row.GetValueOrDefault("Description"), CultureInfo.InvariantCulture)?.Contains(search, StringComparison.OrdinalIgnoreCase) == true).ToList();

        return new RptData
        {
            Columns = BuildDefinition().Columns,
            Rows = agedRows,
            Totals = new Dictionary<string, object?>
            {
                ["Description"] = T("Reports.Totals"),
                ["RemainingQty"] = agedRows.Sum(row => RptWinUtil.Dec(row.GetValueOrDefault("RemainingQty"))),
                ["RemainingBaseAmount"] = agedRows.Sum(row => RptWinUtil.Dec(row.GetValueOrDefault("RemainingBaseAmount"))),
                ["Bucket0_30"] = agedRows.Sum(row => RptWinUtil.Dec(row.GetValueOrDefault("Bucket0_30"))),
                ["Bucket31_60"] = agedRows.Sum(row => RptWinUtil.Dec(row.GetValueOrDefault("Bucket31_60"))),
                ["Bucket61_90"] = agedRows.Sum(row => RptWinUtil.Dec(row.GetValueOrDefault("Bucket61_90"))),
                ["Bucket90Plus"] = agedRows.Sum(row => RptWinUtil.Dec(row.GetValueOrDefault("Bucket90Plus"))),
            }
        };
    }
}
