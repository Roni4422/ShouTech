using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ShouTech.App.Reporting;
using ShouTech.App.Services;

namespace ShouTech.App.Views.Reports;

public sealed class SalListWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Sales.InvoiceList",
        TitleTextKey = "Reports.SalesInvoiceList",
        Columns = new RptCol[]
        {
            new() { Key = "DocDate", HeaderTextKey = "Reports.Col.Date", Type = RptColType.Date, Width = 100 },
            new() { Key = "DocNo", HeaderTextKey = "Reports.Col.DocNo", Type = RptColType.Text, Width = 120 },
            new() { Key = "Kind", HeaderTextKey = "Reports.Col.DocKind", Type = RptColType.Text, Width = 70 },
            new() { Key = "PartyCode", HeaderTextKey = "Reports.Col.PartyCode", Type = RptColType.Text, Width = 100 },
            new() { Key = "PartyName", HeaderTextKey = "Reports.Col.PartyName", Type = RptColType.Text, Width = 200 },
            new() { Key = "Subtotal", HeaderTextKey = "Reports.Col.Subtotal", Type = RptColType.Money, Width = 110, Format = "N2" },
            new() { Key = "Tax", HeaderTextKey = "Reports.Col.Tax", Type = RptColType.Money, Width = 100, Format = "N2" },
            new() { Key = "Total", HeaderTextKey = "Reports.Col.Total", Type = RptColType.Money, Width = 110, Format = "N2" },
            new() { Key = "Status", HeaderTextKey = "Reports.Col.Status", Type = RptColType.Text, Width = 90 },
            new() { Key = "Currency", HeaderTextKey = "Reports.Col.Currency", Type = RptColType.Text, Width = 70 },
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
        var raw = await RptQuery.SalesInvoiceListAsync(from, to, ct);
        var rows = raw.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["DocDate"] = r.GetValueOrDefault("DocDate"),
            ["DocNo"] = r.GetValueOrDefault("DocNo"),
            ["Kind"] = r.GetValueOrDefault("Kind"),
            ["PartyCode"] = r.GetValueOrDefault("PartyCode"),
            ["PartyName"] = r.GetValueOrDefault("PartyName"),
            ["Subtotal"] = ToDec(r.GetValueOrDefault("Subtotal")),
            ["Tax"] = ToDec(r.GetValueOrDefault("Tax")),
            ["Total"] = ToDec(r.GetValueOrDefault("Total")),
            ["Status"] = r.GetValueOrDefault("Status"),
            ["Currency"] = r.GetValueOrDefault("Currency"),
        }).ToList();

        return new RptData
        {
            Columns = BuildDefinition().Columns,
            Rows = rows,
            Totals = new Dictionary<string, object?>
            {
                ["PartyName"] = T("Reports.Totals"),
                ["Subtotal"] = rows.Sum(x => ToDec(x["Subtotal"])),
                ["Tax"] = rows.Sum(x => ToDec(x["Tax"])),
                ["Total"] = rows.Sum(x => ToDec(x["Total"])),
            }
        };
    }

    private static decimal ToDec(object? v)
        => v is null ? 0m : Convert.ToDecimal(v, CultureInfo.InvariantCulture);
}
