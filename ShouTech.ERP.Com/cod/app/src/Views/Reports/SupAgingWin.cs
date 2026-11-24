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
/// Supplier payable aging. Two bases share this window (and its
/// AP.SupplierAging report permission):
///  - Entry date (the historical view): supplier-level buckets from
///    party_ledger entry dates, now measured against a user-chosen
///    as-of date instead of always "today" (Y3).
///  - Due date (Y3): one row per OPEN purchase invoice, aged by its
///    due_date (migration 109; doc date fallback for older invoices)
///    into the same four buckets, base-currency amounts.
/// </summary>
public sealed class SupAgingWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "AP.SupplierAging",
        TitleTextKey = "Reports.SupplierAging",
        Columns = new RptCol[]
        {
            new() { Key = "Code", HeaderTextKey = "Reports.Col.PartyCode", Width = 100 },
            new() { Key = "Name", HeaderTextKey = "Reports.Col.PartyName", Width = 220 },
            new() { Key = "InvoiceNo", HeaderTextKey = "Reports.Col.InvoiceNo", Type = RptColType.Text, Width = 110, VisibleByDefault = false },
            new() { Key = "InvoiceDate", HeaderTextKey = "Reports.Col.InvoiceDate", Type = RptColType.Date, Width = 95, VisibleByDefault = false },
            new() { Key = "DueDate", HeaderTextKey = "Reports.Col.DueDate", Type = RptColType.Date, Width = 95, VisibleByDefault = false },
            new() { Key = "DaysOverdue", HeaderTextKey = "Reports.Col.DaysOverdue", Type = RptColType.WholeNumber, Width = 95, VisibleByDefault = false },
            new() { Key = "OpenAmount", HeaderTextKey = "Reports.Col.OpenAmount", Type = RptColType.Money, Width = 110, Format = "N2", VisibleByDefault = false },
            new() { Key = "B0", HeaderTextKey = "Reports.Col.Aging0_30", Type = RptColType.Money, Width = 100, Format = "N2" },
            new() { Key = "B30", HeaderTextKey = "Reports.Col.Aging31_60", Type = RptColType.Money, Width = 100, Format = "N2" },
            new() { Key = "B60", HeaderTextKey = "Reports.Col.Aging61_90", Type = RptColType.Money, Width = 100, Format = "N2" },
            new() { Key = "B90", HeaderTextKey = "Reports.Col.Aging90Plus", Type = RptColType.Money, Width = 100, Format = "N2" },
            new() { Key = "Balance", HeaderTextKey = "Reports.Col.Balance", Type = RptColType.Money, Width = 110, Format = "N2" },
        },
        Filters = new RptFlt[]
        {
            new()
            {
                Key = "Mode", LabelTextKey = "Reports.Filter.AgingBasis", Kind = RptFltKind.Choice,
                Choices = new[]
                {
                    new KeyValuePair<string, string>("EntryDate", "Reports.AgingByEntryDate"),
                    new KeyValuePair<string, string>("DueDate", "Reports.AgingByDueDate")
                },
                DefaultValue = "EntryDate"
            },
            new() { Key = "AsOf", LabelTextKey = "Reports.Filter.AsOf", Kind = RptFltKind.DateTo, DefaultValue = DateTime.Today },
            new() { Key = "Search", LabelTextKey = "Reports.Filter.Search", Kind = RptFltKind.Text }
        }
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var asOf = filters.GetDateOr("AsOf", DateTime.Today).Date;
        var byDueDate = string.Equals(filters.GetString("Mode"), "DueDate", StringComparison.OrdinalIgnoreCase);
        var rows = byDueDate
            ? await LoadDueDateRowsAsync(asOf, ct)
            : await LoadEntryDateRowsAsync(asOf, ct);

        var search = filters.GetString("Search")?.Trim();
        if (!string.IsNullOrWhiteSpace(search))
            rows = rows.Where(row =>
                Contains(row, "Code", search) ||
                Contains(row, "Name", search) ||
                Contains(row, "InvoiceNo", search)).ToList();
        return new RptData
        {
            Columns = BuildDefinition().Columns,
            Rows = rows,
            Totals = new Dictionary<string, object?>
            {
                ["Name"] = T("Reports.Totals"),
                ["OpenAmount"] = rows.Sum(x => Dec(x.GetValueOrDefault("OpenAmount"))),
                ["B0"] = rows.Sum(x => Dec(x.GetValueOrDefault("B0"))),
                ["B30"] = rows.Sum(x => Dec(x.GetValueOrDefault("B30"))),
                ["B60"] = rows.Sum(x => Dec(x.GetValueOrDefault("B60"))),
                ["B90"] = rows.Sum(x => Dec(x.GetValueOrDefault("B90"))),
                ["Balance"] = rows.Sum(x => Dec(x.GetValueOrDefault("Balance"))),
            }
        };
    }

    private static async Task<List<IReadOnlyDictionary<string, object?>>> LoadEntryDateRowsAsync(DateTime asOf, CancellationToken ct)
    {
        var raw = await RptQuery.PartyAgingAsync(customers: false, asOf, ct);
        return raw.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["Code"] = r.GetValueOrDefault("Code"),
            ["Name"] = r.GetValueOrDefault("Name"),
            ["B0"] = Dec(r.GetValueOrDefault("Bucket0_30")),
            ["B30"] = Dec(r.GetValueOrDefault("Bucket31_60")),
            ["B60"] = Dec(r.GetValueOrDefault("Bucket61_90")),
            ["B90"] = Dec(r.GetValueOrDefault("Bucket90Plus")),
            ["Balance"] = Dec(r.GetValueOrDefault("Balance")),
        }).ToList();
    }

    private static async Task<List<IReadOnlyDictionary<string, object?>>> LoadDueDateRowsAsync(DateTime asOf, CancellationToken ct)
    {
        var raw = await RptQuery.PayableDueDateAgingAsync(asOf, ct);
        return raw.Select(r =>
        {
            // Buckets carry the BASE open amount (the entry-date mode's
            // basis); OpenAmount keeps the document-currency figure.
            var openBase = Dec(r.GetValueOrDefault("OpenBaseAmount"));
            var bucket = Convert.ToString(r.GetValueOrDefault("Bucket"), CultureInfo.InvariantCulture);
            return (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["Code"] = r.GetValueOrDefault("SupplierCode"),
                ["Name"] = r.GetValueOrDefault("SupplierName"),
                ["InvoiceNo"] = r.GetValueOrDefault("InvoiceNo"),
                ["InvoiceDate"] = r.GetValueOrDefault("InvoiceDate"),
                ["DueDate"] = r.GetValueOrDefault("DueDate"),
                ["DaysOverdue"] = Convert.ToInt64(r.GetValueOrDefault("DaysOverdue") ?? 0L, CultureInfo.InvariantCulture),
                ["OpenAmount"] = Dec(r.GetValueOrDefault("OpenAmount")),
                ["B0"] = bucket == "Bucket0_30" ? openBase : 0m,
                ["B30"] = bucket == "Bucket31_60" ? openBase : 0m,
                ["B60"] = bucket == "Bucket61_90" ? openBase : 0m,
                ["B90"] = bucket == "Bucket90Plus" ? openBase : 0m,
                ["Balance"] = openBase,
            };
        }).ToList();
    }

    private static bool Contains(IReadOnlyDictionary<string, object?> row, string key, string search)
        => Convert.ToString(row.GetValueOrDefault(key), CultureInfo.InvariantCulture)
            ?.Contains(search, StringComparison.OrdinalIgnoreCase) == true;

    private static decimal Dec(object? v) => v is null ? 0m : Convert.ToDecimal(v, CultureInfo.InvariantCulture);
}
