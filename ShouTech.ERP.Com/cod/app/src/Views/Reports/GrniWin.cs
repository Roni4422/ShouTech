using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ShouTech.App.Reporting;
using ShouTech.App.Services;

namespace ShouTech.App.Views.Reports;

/// <summary>Open goods-received-not-invoiced quantities and their remaining base-currency value.</summary>
public sealed class GrniWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Purchasing.GRNI",
        TitleTextKey = "Reports.GoodsReceivedNotInvoiced",
        Columns = new RptCol[]
        {
            new() { Key = "ReceiptDate", HeaderTextKey = "Reports.Col.Date", Type = RptColType.Date, Width = 95 },
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
        },
        Filters = new RptFlt[]
        {
            new() { Key = "From", LabelTextKey = "Reports.Filter.DateFrom", Kind = RptFltKind.DateFrom, DefaultValue = new DateTime(DateTime.Today.Year, 1, 1) },
            new() { Key = "To", LabelTextKey = "Reports.Filter.DateTo", Kind = RptFltKind.DateTo, DefaultValue = DateTime.Today },
            new() { Key = "Search", LabelTextKey = "Reports.Filter.Search", Kind = RptFltKind.Text },
        }
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var from = filters.GetDateOr("From", new DateTime(DateTime.Today.Year, 1, 1));
        var to = filters.GetDateOr("To", DateTime.Today);
        if (from.Date > to.Date)
            throw new ArgumentException("The start date must not be after the end date.");

        var raw = await RptQuery.GoodsReceivedNotInvoicedAsync(from, to, ct);
        var rows = RptWinUtil.Map(raw, BuildDefinition().Columns);
        var search = filters.GetString("Search")?.Trim();
        if (!string.IsNullOrWhiteSpace(search))
            rows = rows.Where(row =>
                Convert.ToString(row.GetValueOrDefault("ReceiptNo"), CultureInfo.InvariantCulture)?.Contains(search, StringComparison.OrdinalIgnoreCase) == true ||
                Convert.ToString(row.GetValueOrDefault("PurchaseOrderNo"), CultureInfo.InvariantCulture)?.Contains(search, StringComparison.OrdinalIgnoreCase) == true ||
                Convert.ToString(row.GetValueOrDefault("SupplierName"), CultureInfo.InvariantCulture)?.Contains(search, StringComparison.OrdinalIgnoreCase) == true ||
                Convert.ToString(row.GetValueOrDefault("MaterialCode"), CultureInfo.InvariantCulture)?.Contains(search, StringComparison.OrdinalIgnoreCase) == true ||
                Convert.ToString(row.GetValueOrDefault("Description"), CultureInfo.InvariantCulture)?.Contains(search, StringComparison.OrdinalIgnoreCase) == true).ToList();

        return new RptData
        {
            Columns = BuildDefinition().Columns,
            Rows = rows,
            Totals = new Dictionary<string, object?>
            {
                ["Description"] = T("Reports.Totals"),
                ["RemainingQty"] = rows.Sum(row => RptWinUtil.Dec(row.GetValueOrDefault("RemainingQty"))),
                ["RemainingBaseAmount"] = rows.Sum(row => RptWinUtil.Dec(row.GetValueOrDefault("RemainingBaseAmount"))),
            }
        };
    }
}
