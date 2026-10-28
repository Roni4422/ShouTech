using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ShouTech.App.Reporting;
using ShouTech.App.Services;

namespace ShouTech.App.Views.Reports;

public sealed class JrnListWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Fin.JournalList",
        TitleTextKey = "Reports.JournalList",
        Columns = new RptCol[]
        {
            new() { Key = "EntryDate", HeaderTextKey = "Reports.Col.Date", Type = RptColType.Date, Width = 100 },
            new() { Key = "DocNo", HeaderTextKey = "Reports.Col.DocNo", Type = RptColType.Text, Width = 160 },
            new() { Key = "Description", HeaderTextKey = "Reports.Col.Description", Type = RptColType.Text, Width = 260 },
            new() { Key = "Status", HeaderTextKey = "Reports.Col.Status", Type = RptColType.Text, Width = 90 },
            new() { Key = "Debit", HeaderTextKey = "Reports.Col.Debit", Type = RptColType.Money, Width = 110, Format = "N2" },
            new() { Key = "Credit", HeaderTextKey = "Reports.Col.Credit", Type = RptColType.Money, Width = 110, Format = "N2" },
            new() { Key = "CreatedBy", HeaderTextKey = "Reports.Col.User", Type = RptColType.Text, Width = 120 },
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
        var raw = await RptQuery.JournalListAsync(from, to, ct);
        var rows = raw.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["EntryDate"] = r.GetValueOrDefault("EntryDate"),
            ["DocNo"] = r.GetValueOrDefault("DocNo"),
            ["Description"] = r.GetValueOrDefault("Description"),
            ["Status"] = r.GetValueOrDefault("Status"),
            ["Debit"] = ToDec(r.GetValueOrDefault("Debit")),
            ["Credit"] = ToDec(r.GetValueOrDefault("Credit")),
            ["CreatedBy"] = r.GetValueOrDefault("CreatedBy"),
        }).ToList();

        return new RptData
        {
            Columns = BuildDefinition().Columns,
            Rows = rows,
            Totals = new Dictionary<string, object?>
            {
                ["Description"] = T("Reports.Totals"),
                ["Debit"] = rows.Sum(x => ToDec(x["Debit"])),
                ["Credit"] = rows.Sum(x => ToDec(x["Credit"])),
            }
        };
    }

    private static decimal ToDec(object? v)
        => v is null ? 0m : Convert.ToDecimal(v, CultureInfo.InvariantCulture);
}
