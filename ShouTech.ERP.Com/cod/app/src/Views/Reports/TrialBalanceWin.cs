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
/// Debit/credit/balance per account for the active company.
/// </summary>
public sealed class TrialBalanceWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Fin.TrialBalance",
        TitleTextKey = "Reports.TrialBalance",
        Columns = new RptCol[]
        {
            new() { Key = "Code", HeaderTextKey = "Reports.Col.AccountCode", Type = RptColType.Text, Width = 110 },
            new() { Key = "Name", HeaderTextKey = "Reports.Col.AccountName", Type = RptColType.Text, Width = 260 },
            new() { Key = "Debit", HeaderTextKey = "Reports.Col.Debit", Type = RptColType.Money, Width = 120, Format = "N2" },
            new() { Key = "Credit", HeaderTextKey = "Reports.Col.Credit", Type = RptColType.Money, Width = 120, Format = "N2" },
            new() { Key = "Balance", HeaderTextKey = "Reports.Col.Balance", Type = RptColType.Money, Width = 120, Format = "N2" },
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
        var raw = await RptQuery.TrialBalanceAsync(from, to, ct);
        var lang = App.CurrentLanguageCode ?? "ar";
        var rows = raw.Select(r =>
        {
            var name = lang.StartsWith("ar", StringComparison.OrdinalIgnoreCase)
                ? Convert.ToString(r.GetValueOrDefault("Name"), CultureInfo.InvariantCulture)
                : Convert.ToString(r.GetValueOrDefault("NameEn"), CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(name))
                name = Convert.ToString(r.GetValueOrDefault("Name"), CultureInfo.InvariantCulture);
            return (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["Code"] = r.GetValueOrDefault("Code"),
                ["Name"] = name,
                ["Debit"] = ToDec(r.GetValueOrDefault("Debit")),
                ["Credit"] = ToDec(r.GetValueOrDefault("Credit")),
                ["Balance"] = ToDec(r.GetValueOrDefault("Balance")),
            };
        }).ToList();

        var totals = new Dictionary<string, object?>
        {
            ["Code"] = "",
            ["Name"] = T("Reports.Totals"),
            ["Debit"] = rows.Sum(x => ToDec(x["Debit"])),
            ["Credit"] = rows.Sum(x => ToDec(x["Credit"])),
            ["Balance"] = rows.Sum(x => ToDec(x["Balance"])),
        };

        return new RptData { Columns = BuildDefinition().Columns, Rows = rows, Totals = totals };
    }

    private static decimal ToDec(object? v)
        => v is null ? 0m : Convert.ToDecimal(v, CultureInfo.InvariantCulture);
}
