using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ShouTech.App.Reporting;
using ShouTech.App.Services;

namespace ShouTech.App.Views.Reports;

public sealed class SupBalWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "AP.SupplierBalances",
        TitleTextKey = "Reports.SupplierBalances",
        Columns = new RptCol[]
        {
            new() { Key = "Code", HeaderTextKey = "Reports.Col.PartyCode", Type = RptColType.Text, Width = 110 },
            new() { Key = "Name", HeaderTextKey = "Reports.Col.PartyName", Type = RptColType.Text, Width = 260 },
            new() { Key = "Phone", HeaderTextKey = "Reports.Col.Phone", Type = RptColType.Text, Width = 120 },
            new() { Key = "Balance", HeaderTextKey = "Reports.Col.Balance", Type = RptColType.Money, Width = 130, Format = "N2" },
        },
        Filters = Array.Empty<RptFlt>()
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var lang = App.CurrentLanguageCode ?? "ar";
        var raw = await RptQuery.SupplierBalancesAsync(ct);
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
                ["Phone"] = r.GetValueOrDefault("Phone"),
                ["Balance"] = ToDec(r.GetValueOrDefault("Balance")),
            };
        }).ToList();

        return new RptData
        {
            Columns = BuildDefinition().Columns,
            Rows = rows,
            Totals = new Dictionary<string, object?>
            {
                ["Name"] = T("Reports.Totals"),
                ["Balance"] = rows.Sum(x => ToDec(x["Balance"])),
            }
        };
    }

    private static decimal ToDec(object? v)
        => v is null ? 0m : Convert.ToDecimal(v, CultureInfo.InvariantCulture);
}
