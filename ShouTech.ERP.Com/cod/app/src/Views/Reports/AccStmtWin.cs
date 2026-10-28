using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ShouTech.App.Reporting;
using ShouTech.App.Services;

namespace ShouTech.App.Views.Reports;

public sealed class AccStmtWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Fin.AccountStatement",
        TitleTextKey = "Reports.AccountStatement",
        Columns = new RptCol[]
        {
            new() { Key = "EntryDate", HeaderTextKey = "Reports.Col.Date", Type = RptColType.Date, Width = 100 },
            new() { Key = "DocNo", HeaderTextKey = "Reports.Col.DocNo", Type = RptColType.Text, Width = 140 },
            new() { Key = "Description", HeaderTextKey = "Reports.Col.Description", Type = RptColType.Text, Width = 280 },
            new() { Key = "Debit", HeaderTextKey = "Reports.Col.Debit", Type = RptColType.Money, Width = 110, Format = "N2" },
            new() { Key = "Credit", HeaderTextKey = "Reports.Col.Credit", Type = RptColType.Money, Width = 110, Format = "N2" },
            new() { Key = "Balance", HeaderTextKey = "Reports.Col.Balance", Type = RptColType.Money, Width = 110, Format = "N2" },
        },
        Filters = new RptFlt[]
        {
            new() { Key = "Account", LabelTextKey = "Reports.Filter.AccountCode", Kind = RptFltKind.Text },
            new() { Key = "From", LabelTextKey = "Reports.Filter.DateFrom", Kind = RptFltKind.DateFrom, DefaultValue = new DateTime(DateTime.Today.Year, 1, 1) },
            new() { Key = "To", LabelTextKey = "Reports.Filter.DateTo", Kind = RptFltKind.DateTo, DefaultValue = DateTime.Today },
        }
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var account = filters.GetString("Account") ?? string.Empty;
        var from = filters.GetDateOr("From", new DateTime(DateTime.Today.Year, 1, 1));
        var to = filters.GetDateOr("To", DateTime.Today);
        var raw = await RptQuery.AccountStatementAsync(account, from, to, ct);

        decimal running = 0m;
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var r in raw)
        {
            var debit = ToDec(r.GetValueOrDefault("Debit"));
            var credit = ToDec(r.GetValueOrDefault("Credit"));
            running += debit - credit;
            rows.Add(new Dictionary<string, object?>
            {
                ["EntryDate"] = r.GetValueOrDefault("EntryDate"),
                ["DocNo"] = r.GetValueOrDefault("DocNo"),
                ["Description"] = r.GetValueOrDefault("Description"),
                ["Debit"] = debit,
                ["Credit"] = credit,
                ["Balance"] = running,
            });
        }

        var totals = new Dictionary<string, object?>
        {
            ["EntryDate"] = "",
            ["DocNo"] = "",
            ["Description"] = T("Reports.Totals"),
            ["Debit"] = rows.Sum(x => ToDec(x["Debit"])),
            ["Credit"] = rows.Sum(x => ToDec(x["Credit"])),
            ["Balance"] = running,
        };

        return new RptData { Columns = BuildDefinition().Columns, Rows = rows, Totals = totals };
    }

    private static decimal ToDec(object? v)
        => v is null ? 0m : Convert.ToDecimal(v, CultureInfo.InvariantCulture);
}
