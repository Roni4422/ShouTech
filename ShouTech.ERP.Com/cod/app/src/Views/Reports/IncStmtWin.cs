using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ShouTech.App.Reporting;
using ShouTech.App.Services;

namespace ShouTech.App.Views.Reports;

/// <summary>Income Statement report.</summary>
public sealed class IncStmtWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Fin.IncomeStatement",
        TitleTextKey = "Reports.IncomeStatement",
        Columns = new RptCol[]
        {
            new() { Key = "Code", HeaderTextKey = "Reports.Col.AccountCode", Type = RptColType.Text, Width = 110 },
            new() { Key = "Name", HeaderTextKey = "Reports.Col.AccountName", Type = RptColType.Text, Width = 280 },
            new() { Key = "Amount", HeaderTextKey = "Reports.Col.Amount", Type = RptColType.Money, Width = 140, Format = "N2" },
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
        var raw = await RptQuery.IncomeStatementAsync(from, to, ct);
        var rows = raw.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["Code"] = r.GetValueOrDefault("Code"),
            ["Name"] = PickName(r),
            ["Amount"] = ToDec(r.GetValueOrDefault("Amount")),
        }).ToList();
        return new RptData { Columns = BuildDefinition().Columns, Rows = rows, Totals = null };
    }

    private static string? PickName(IReadOnlyDictionary<string, object?> row)
    {
        var lang = App.CurrentLanguageCode ?? "ar";
        var name = lang.StartsWith("ar", StringComparison.OrdinalIgnoreCase)
            ? Convert.ToString(row.GetValueOrDefault("Name"), CultureInfo.InvariantCulture)
            : Convert.ToString(row.GetValueOrDefault("NameEn"), CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(name)
            ? Convert.ToString(row.GetValueOrDefault("Name"), CultureInfo.InvariantCulture)
            : name;
    }

    private static decimal ToDec(object? v) => v is null ? 0m : Convert.ToDecimal(v, CultureInfo.InvariantCulture);
}
