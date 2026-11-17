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
/// Invoice age schedule: open amounts of posted sales invoices bucketed by
/// age (0-30 / 31-60 / 61-90 / 90+ days from the invoice date) per customer.
/// Bucketing is done here from the real per-invoice open amounts so the
/// report works identically on SQLite and PostgreSQL.
/// </summary>
public sealed class InvoiceAgeWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Inv.InvoiceAge",
        TitleTextKey = "Reports.InvoiceAge",
        Columns = new RptCol[]
        {
            new() { Key = "PartyCode", HeaderTextKey = "Reports.Col.PartyCode", Type = RptColType.Text, Width = 110 },
            new() { Key = "PartyName", HeaderTextKey = "Reports.Col.PartyName", Type = RptColType.Text, Width = 240 },
            new() { Key = "Bucket0_30", HeaderTextKey = "Reports.Col.Aging0_30", Type = RptColType.Money, Width = 120, Format = "N2" },
            new() { Key = "Bucket31_60", HeaderTextKey = "Reports.Col.Aging31_60", Type = RptColType.Money, Width = 120, Format = "N2" },
            new() { Key = "Bucket61_90", HeaderTextKey = "Reports.Col.Aging61_90", Type = RptColType.Money, Width = 120, Format = "N2" },
            new() { Key = "Bucket90Plus", HeaderTextKey = "Reports.Col.Aging90Plus", Type = RptColType.Money, Width = 120, Format = "N2" },
            new() { Key = "Total", HeaderTextKey = "Reports.Col.Total", Type = RptColType.Money, Width = 130, Format = "N2" },
            new() { Key = "InvoiceCount", HeaderTextKey = "Reports.Col.DocCount", Type = RptColType.WholeNumber, Width = 100 },
        },
        Filters = Array.Empty<RptFlt>()
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var raw = await RptQuery.InvoiceAgeInvoicesAsync(ct);
        var byParty = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var r in raw)
        {
            var code = Convert.ToString(r.GetValueOrDefault("PartyCode"), CultureInfo.InvariantCulture) ?? "";
            var name = Convert.ToString(r.GetValueOrDefault("PartyName"), CultureInfo.InvariantCulture) ?? "";
            var key = code + "|" + name;
            if (!byParty.TryGetValue(key, out var row))
            {
                row = new Dictionary<string, object?>
                {
                    ["PartyCode"] = code, ["PartyName"] = name,
                    ["Bucket0_30"] = 0m, ["Bucket31_60"] = 0m, ["Bucket61_90"] = 0m, ["Bucket90Plus"] = 0m,
                    ["Total"] = 0m, ["InvoiceCount"] = 0
                };
                byParty[key] = row;
                order.Add(key);
            }
            var open = RptWinUtil.Dec(r.GetValueOrDefault("OpenAmount"));
            var date = RptWinUtil.ParseDate(r.GetValueOrDefault("DocDate")) ?? DateTime.Today;
            var age = Math.Max(0, (DateTime.Today - date.Date).Days);
            var bucket = age <= 30 ? "Bucket0_30" : age <= 60 ? "Bucket31_60" : age <= 90 ? "Bucket61_90" : "Bucket90Plus";
            row[bucket] = RptWinUtil.Dec(row[bucket]) + open;
            row["Total"] = RptWinUtil.Dec(row["Total"]) + open;
            row["InvoiceCount"] = (int)row["InvoiceCount"]! + 1;
        }
        var rows = order.Select(k => (IReadOnlyDictionary<string, object?>)byParty[k]).ToList();
        var def = BuildDefinition();
        return new RptData
        {
            Columns = def.Columns,
            Rows = rows,
            Totals = RptWinUtil.SumTotals(rows, def.Columns, T("Reports.Totals"), "PartyName")
        };
    }
}
