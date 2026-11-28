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
///
/// Two views share this window (and its Purchasing.GRNI report permission):
///  - Detail: every open receipt line, with the GL 2203 reconciliation
///    populated on the rows themselves. The From filter is unbounded by
///    default and, when set, bounds the detail AND the reconciliation
///    alike, so the tie-out can always be explained by the rows shown.
///  - Exceptions (Y2): only what needs action — (a) open receipt lines at
///    or past a user-set age threshold, (b) posted purchase invoices with
///    no purchase_receipt_invoice_matches row (invoiced without receipt,
///    the manual InvoiceWin path), and (c) periods whose cumulative
///    GL 2203 tie-out difference is not zero.
///
/// PERIOD-END CONTRACT (owner report §5/§11.4): detail, exceptions and
/// reconciliation are ONE workbench on ONE boundary — the Period-end
/// (as of) filter date. Receipts, invoice matches, receipt returns and
/// GL 2203 entries are all cut at that same date in every query this
/// window runs, so an as-of period-end run reproduces that period-end
/// exactly, with no period+1 activity leaking into either side.
/// </summary>
public sealed class GrniWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Purchasing.GRNI",
        TitleTextKey = "Reports.GoodsReceivedNotInvoiced",
        Columns = new RptCol[]
        {
            new() { Key = "ExceptionKind", HeaderTextKey = "Reports.Col.ExceptionKind", Type = RptColType.Text, Width = 190, VisibleByDefault = false },
            new() { Key = "Period", HeaderTextKey = "Reports.Col.Period", Type = RptColType.Text, Width = 85, VisibleByDefault = false },
            new() { Key = "ReceiptDate", HeaderTextKey = "Reports.Col.Date", Type = RptColType.Date, Width = 95 },
            new() { Key = "AgeDays", HeaderTextKey = "Reports.Col.AgeDays", Type = RptColType.WholeNumber, Width = 80 },
            new() { Key = "ReceiptNo", HeaderTextKey = "Reports.Col.ReceiptNo", Type = RptColType.Text, Width = 105 },
            new() { Key = "InvoiceNo", HeaderTextKey = "Reports.Col.InvoiceNo", Type = RptColType.Text, Width = 105, VisibleByDefault = false },
            new() { Key = "InvoiceDate", HeaderTextKey = "Reports.Col.InvoiceDate", Type = RptColType.Date, Width = 95, VisibleByDefault = false },
            new() { Key = "PurchaseOrderNo", HeaderTextKey = "Reports.Col.PurchaseOrder", Type = RptColType.Text, Width = 105 },
            new() { Key = "SupplierName", HeaderTextKey = "Reports.Col.PartyName", Type = RptColType.Text, Width = 180 },
            new() { Key = "MaterialCode", HeaderTextKey = "Reports.Col.MaterialCode", Type = RptColType.Text, Width = 110 },
            new() { Key = "Description", HeaderTextKey = "Reports.Col.Description", Type = RptColType.Text, Width = 190 },
            new() { Key = "ReceivedQty", HeaderTextKey = "Reports.Col.ReceivedQty", Type = RptColType.FractionalNumber, Width = 90, Format = "N3" },
            new() { Key = "InvoicedQty", HeaderTextKey = "Reports.Col.InvoicedQty", Type = RptColType.FractionalNumber, Width = 90, Format = "N3" },
            new() { Key = "ReturnedQty", HeaderTextKey = "Reports.Col.ReturnedQty", Type = RptColType.FractionalNumber, Width = 90, Format = "N3" },
            new() { Key = "RemainingQty", HeaderTextKey = "Reports.Col.RemainingQty", Type = RptColType.FractionalNumber, Width = 95, Format = "N3" },
            new() { Key = "RemainingBaseAmount", HeaderTextKey = "Reports.Col.RemainingBaseAmount", Type = RptColType.Money, Width = 125, Format = "N2" },
            new() { Key = "ExceptionAmount", HeaderTextKey = "Reports.Col.ExceptionAmount", Type = RptColType.Money, Width = 125, Format = "N2", VisibleByDefault = false },
            new() { Key = "Bucket0_30", HeaderTextKey = "Reports.Col.Aging0_30", Type = RptColType.Money, Width = 105, Format = "N2" },
            new() { Key = "Bucket31_60", HeaderTextKey = "Reports.Col.Aging31_60", Type = RptColType.Money, Width = 105, Format = "N2" },
            new() { Key = "Bucket61_90", HeaderTextKey = "Reports.Col.Aging61_90", Type = RptColType.Money, Width = 105, Format = "N2" },
            new() { Key = "Bucket90Plus", HeaderTextKey = "Reports.Col.Aging90Plus", Type = RptColType.Money, Width = 105, Format = "N2" },
            new() { Key = "OpenReceiptBase", HeaderTextKey = "Reports.Col.OpenReceiptBase", Type = RptColType.Money, Width = 135, Format = "N2", VisibleByDefault = false },
            new() { Key = "GlBalanceBase", HeaderTextKey = "Reports.Col.GrniGlBalanceCompany", Type = RptColType.Money, Width = 125, Format = "N2" },
            new() { Key = "DifferenceBase", HeaderTextKey = "Reports.Col.GrniDifferenceCompany", Type = RptColType.Money, Width = 135, Format = "N2" },
            new() { Key = "AgeBand", HeaderTextKey = "Reports.Col.AgeBand", Type = RptColType.Text, Width = 105 },
        },
        Filters = new RptFlt[]
        {
            new()
            {
                Key = "Mode", LabelTextKey = "Reports.Filter.ViewMode", Kind = RptFltKind.Choice,
                Choices = new[]
                {
                    new KeyValuePair<string, string>("Detail", "Reports.GrniModeDetail"),
                    new KeyValuePair<string, string>("Exceptions", "Reports.GrniModeExceptions")
                },
                DefaultValue = "Detail"
            },
            new() { Key = "From", LabelTextKey = "Reports.Filter.DateFrom", Kind = RptFltKind.DateFrom },
            new() { Key = "AsOf", LabelTextKey = "Reports.Filter.PeriodEndAsOf", Kind = RptFltKind.DateTo, DefaultValue = DateTime.Today },
            new() { Key = "AgeThresholdDays", LabelTextKey = "Reports.Filter.AgeThresholdDays", Kind = RptFltKind.Text, DefaultValue = "90" },
            new() { Key = "Search", LabelTextKey = "Reports.Filter.Search", Kind = RptFltKind.Text },
        }
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var asOf = filters.GetDateOr("AsOf", DateTime.Today).Date;
        // Defect fix (Y2): no silent −1 year cutoff. An empty From means
        // "all open receipts, however old"; a set From bounds the detail
        // rows AND the reconciliation identically (see RptQuery).
        var from = filters.GetDate("From")?.Date;
        if (from.HasValue && from.Value > asOf)
            throw new InvalidOperationException("The GRNI start date cannot be after the as-of date.");
        var exceptions = string.Equals(filters.GetString("Mode"), "Exceptions", StringComparison.OrdinalIgnoreCase);

        var raw = await RptQuery.GoodsReceivedNotInvoicedAsync(from ?? DateTime.MinValue, asOf, ct);
        var reconciliation = await RptQuery.GoodsReceivedNotInvoicedReconciliationAsync(from, asOf, ct);
        // Reconciliation may return one row per account or receipt group.
        // Sum every row so the report never silently drops all but the first group.
        var glBalanceBase = reconciliation.Sum(row => RptWinUtil.Dec(row.GetValueOrDefault("GlBalanceBase")));
        var differenceBase = reconciliation.Sum(row => RptWinUtil.Dec(row.GetValueOrDefault("DifferenceBase")));

        var detailRows = BuildDetailRows(raw, asOf);

        List<Dictionary<string, object?>> rows;
        if (!exceptions)
        {
            // Defect fix (Y2): the GL balance / difference columns are
            // populated on every row from the reconciliation query, not
            // left empty with the values stranded in the totals line.
            foreach (var row in detailRows)
            {
                row["GlBalanceBase"] = glBalanceBase;
                row["DifferenceBase"] = differenceBase;
            }
            rows = detailRows;
        }
        else
        {
            rows = await BuildExceptionRowsAsync(filters, detailRows, asOf, ct);
        }

        var search = filters.GetString("Search")?.Trim();
        if (!string.IsNullOrWhiteSpace(search))
            rows = rows.Where(row =>
                Contains(row, "ReceiptNo", search) ||
                Contains(row, "InvoiceNo", search) ||
                Contains(row, "PurchaseOrderNo", search) ||
                Contains(row, "SupplierName", search) ||
                Contains(row, "MaterialCode", search) ||
                Contains(row, "Description", search) ||
                Contains(row, "Period", search)).ToList();

        return new RptData
        {
            Columns = BuildDefinition().Columns,
            Rows = rows,
            Totals = new Dictionary<string, object?>
            {
                ["Description"] = T("Reports.Totals"),
                ["RemainingQty"] = rows.Sum(row => RptWinUtil.Dec(row.GetValueOrDefault("RemainingQty"))),
                ["RemainingBaseAmount"] = rows.Sum(row => RptWinUtil.Dec(row.GetValueOrDefault("RemainingBaseAmount"))),
                ["ExceptionAmount"] = rows.Sum(row => RptWinUtil.Dec(row.GetValueOrDefault("ExceptionAmount"))),
                ["Bucket0_30"] = rows.Sum(row => RptWinUtil.Dec(row.GetValueOrDefault("Bucket0_30"))),
                ["Bucket31_60"] = rows.Sum(row => RptWinUtil.Dec(row.GetValueOrDefault("Bucket31_60"))),
                ["Bucket61_90"] = rows.Sum(row => RptWinUtil.Dec(row.GetValueOrDefault("Bucket61_90"))),
                ["Bucket90Plus"] = rows.Sum(row => RptWinUtil.Dec(row.GetValueOrDefault("Bucket90Plus"))),
                ["GlBalanceBase"] = glBalanceBase,
                ["DifferenceBase"] = differenceBase,
            }
        };
    }

    private List<Dictionary<string, object?>> BuildDetailRows(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> raw, DateTime asOf)
    {
        var reportColumns = BuildDefinition().Columns;
        // The detail query returns exactly these receipt columns; Map
        // projects by key and normalizes the numeric ones.
        var queryOrder = new[]
        {
            "ReceiptDate", "ReceiptNo", "PurchaseOrderNo", "SupplierName", "MaterialCode", "Description",
            "ReceivedQty", "InvoicedQty", "ReturnedQty", "RemainingQty", "RemainingBaseAmount"
        };
        var ordered = queryOrder.Select(k => reportColumns.First(c => c.Key == k)).ToArray();
        var mapped = RptWinUtil.Map(raw, ordered);
        var agedRows = new List<Dictionary<string, object?>>(mapped.Count);

        foreach (var row in mapped)
        {
            var receiptDate = RptWinUtil.ParseDate(row.GetValueOrDefault("ReceiptDate"))
                ?? throw new InvalidOperationException("A posted goods receipt has an invalid document date.");
            var ageDays = Math.Max(0, (asOf - receiptDate.Date).Days);
            var remainingAmount = RptWinUtil.Dec(row.GetValueOrDefault("RemainingBaseAmount"));
            var values = row.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            values["AgeDays"] = ageDays;
            values["AgeBand"] = ageDays <= 30 ? T("Reports.Col.Aging0_30")
                : ageDays <= 60 ? T("Reports.Col.Aging31_60")
                : ageDays <= 90 ? T("Reports.Col.Aging61_90")
                : T("Reports.Col.Aging90Plus");
            values["Bucket0_30"] = ageDays <= 30 ? remainingAmount : 0m;
            values["Bucket31_60"] = ageDays is > 30 and <= 60 ? remainingAmount : 0m;
            values["Bucket61_90"] = ageDays is > 60 and <= 90 ? remainingAmount : 0m;
            values["Bucket90Plus"] = ageDays > 90 ? remainingAmount : 0m;
            if (RptWinUtil.Dec(values.GetValueOrDefault("RemainingQty")) > 0m)
                agedRows.Add(values);
        }
        return agedRows;
    }

    private static async Task<List<Dictionary<string, object?>>> BuildExceptionRowsAsync(
        RptFltValues filters, List<Dictionary<string, object?>> detailRows, DateTime asOf, CancellationToken ct)
    {
        var thresholdText = filters.GetString("AgeThresholdDays")?.Trim();
        if (!int.TryParse(thresholdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var thresholdDays) || thresholdDays < 0)
            throw new InvalidOperationException("The GRNI exception age threshold must be a whole number of days, zero or more.");

        var rows = new List<Dictionary<string, object?>>();

        // (a) Received, not invoiced, at or past the age threshold.
        foreach (var row in detailRows)
        {
            if (RptWinUtil.Dec(row.GetValueOrDefault("AgeDays")) < thresholdDays)
                continue;
            var exception = new Dictionary<string, object?>(row, StringComparer.Ordinal);
            exception["ExceptionKind"] = T("Reports.ExceptionAgedReceipt");
            exception["ExceptionAmount"] = RptWinUtil.Dec(row.GetValueOrDefault("RemainingBaseAmount"));
            rows.Add(exception);
        }

        // (b) Invoiced without receipt: posted purchase invoices with no
        // purchase_receipt_invoice_matches row at all.
        var invoiced = await RptQuery.InvoicedWithoutReceiptAsync(asOf, ct);
        foreach (var invoice in invoiced)
        {
            var invoiceDate = RptWinUtil.ParseDate(invoice.GetValueOrDefault("InvoiceDate"));
            rows.Add(new Dictionary<string, object?>
            {
                ["ExceptionKind"] = T("Reports.ExceptionInvoicedNoReceipt"),
                ["InvoiceNo"] = invoice.GetValueOrDefault("InvoiceNo"),
                ["InvoiceDate"] = invoice.GetValueOrDefault("InvoiceDate"),
                ["SupplierName"] = invoice.GetValueOrDefault("SupplierName"),
                ["AgeDays"] = invoiceDate.HasValue ? Math.Max(0, (asOf - invoiceDate.Value.Date).Days) : 0,
                ["ExceptionAmount"] = RptWinUtil.Dec(invoice.GetValueOrDefault("InvoiceBaseAmount")),
            });
        }

        // (c) GL 2203 tie-out per period: a period is an exception while
        // the CUMULATIVE difference (receipts side vs GL side) is not zero.
        var periods = await RptQuery.GrniTieOutByPeriodAsync(asOf, ct);
        decimal cumulativeReceipts = 0m, cumulativeGl = 0m;
        foreach (var period in periods)
        {
            cumulativeReceipts += RptWinUtil.Dec(period.GetValueOrDefault("ReceiptBase"));
            cumulativeGl += RptWinUtil.Dec(period.GetValueOrDefault("GlMovementBase"));
            var difference = cumulativeReceipts - cumulativeGl;
            if (Math.Abs(difference) <= 0.005m)
                continue;
            rows.Add(new Dictionary<string, object?>
            {
                ["ExceptionKind"] = T("Reports.ExceptionGlTieOut"),
                ["Period"] = period.GetValueOrDefault("Period"),
                ["OpenReceiptBase"] = cumulativeReceipts,
                ["GlBalanceBase"] = cumulativeGl,
                ["DifferenceBase"] = difference,
                ["ExceptionAmount"] = difference,
            });
        }
        return rows;
    }

    private static bool Contains(IReadOnlyDictionary<string, object?> row, string key, string search)
        => Convert.ToString(row.GetValueOrDefault(key), CultureInfo.InvariantCulture)
            ?.Contains(search, StringComparison.OrdinalIgnoreCase) == true;
}
