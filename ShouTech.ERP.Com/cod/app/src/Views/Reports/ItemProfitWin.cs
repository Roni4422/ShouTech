using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ShouTech.App.Reporting;
using ShouTech.App.Services;

namespace ShouTech.App.Views.Reports;

public sealed class ItemProfitWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Sales.ItemProfitability",
        TitleTextKey = "Reports.ItemProfitability",
        Columns = new RptCol[]
        {
            new() { Key = "MaterialCode", HeaderTextKey = "Reports.Col.MaterialCode", Width = 110 },
            new() { Key = "MaterialName", HeaderTextKey = "Reports.Col.MaterialName", Width = 220 },
            new() { Key = "QtySold", HeaderTextKey = "Reports.Col.Qty", Type = RptColType.FractionalNumber, Width = 90, Format = "N3" },
            new() { Key = "Revenue", HeaderTextKey = "Reports.Col.Subtotal", Type = RptColType.Money, Width = 110, Format = "N2" },
            new() { Key = "Tax", HeaderTextKey = "Reports.Col.Tax", Type = RptColType.Money, Width = 90, Format = "N2" },
            new() { Key = "Total", HeaderTextKey = "Reports.Col.Total", Type = RptColType.Money, Width = 110, Format = "N2" },
        },
        Filters = new RptFlt[]
        {
            new() { Key = "From", LabelTextKey = "Reports.Filter.DateFrom", Kind = RptFltKind.DateFrom, DefaultValue = new DateTime(DateTime.Today.Year, 1, 1) },
            new() { Key = "To", LabelTextKey = "Reports.Filter.DateTo", Kind = RptFltKind.DateTo, DefaultValue = DateTime.Today },
        }
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var from = filters.GetDateOr("From", new DateTime(DateTime.Today.Year, 1, 1));
        var to = filters.GetDateOr("To", DateTime.Today);
        var raw = await RptQuery.ItemProfitabilityAsync(from, to, ct);
        var rows = raw.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["MaterialCode"] = r.GetValueOrDefault("MaterialCode"),
            ["MaterialName"] = r.GetValueOrDefault("MaterialName"),
            ["QtySold"] = Dec(r.GetValueOrDefault("QtySold")),
            ["Revenue"] = Dec(r.GetValueOrDefault("Revenue")),
            ["Tax"] = Dec(r.GetValueOrDefault("Tax")),
            ["Total"] = Dec(r.GetValueOrDefault("Total")),
        }).ToList();
        return new RptData
        {
            Columns = BuildDefinition().Columns,
            Rows = rows,
            Totals = new Dictionary<string, object?>
            {
                ["MaterialName"] = T("Reports.Totals"),
                ["QtySold"] = rows.Sum(x => Dec(x["QtySold"])),
                ["Revenue"] = rows.Sum(x => Dec(x["Revenue"])),
                ["Tax"] = rows.Sum(x => Dec(x["Tax"])),
                ["Total"] = rows.Sum(x => Dec(x["Total"])),
            }
        };
    }

    private static decimal Dec(object? v) => v is null ? 0m : Convert.ToDecimal(v, CultureInfo.InvariantCulture);
}
