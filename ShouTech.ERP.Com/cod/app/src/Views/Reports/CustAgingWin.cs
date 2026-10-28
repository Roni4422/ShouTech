using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ShouTech.App.Reporting;
using ShouTech.App.Services;

namespace ShouTech.App.Views.Reports;

public sealed class CustAgingWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "AR.CustomerAging",
        TitleTextKey = "Reports.CustomerAging",
        Columns = new RptCol[]
        {
            new() { Key = "Code", HeaderTextKey = "Reports.Col.PartyCode", Width = 100 },
            new() { Key = "Name", HeaderTextKey = "Reports.Col.PartyName", Width = 220 },
            new() { Key = "B0", HeaderTextKey = "Reports.Col.Aging0_30", Type = RptColType.Money, Width = 100, Format = "N2" },
            new() { Key = "B30", HeaderTextKey = "Reports.Col.Aging31_60", Type = RptColType.Money, Width = 100, Format = "N2" },
            new() { Key = "B60", HeaderTextKey = "Reports.Col.Aging61_90", Type = RptColType.Money, Width = 100, Format = "N2" },
            new() { Key = "B90", HeaderTextKey = "Reports.Col.Aging90Plus", Type = RptColType.Money, Width = 100, Format = "N2" },
            new() { Key = "Balance", HeaderTextKey = "Reports.Col.Balance", Type = RptColType.Money, Width = 110, Format = "N2" },
        },
        Filters = Array.Empty<RptFlt>()
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var raw = await RptQuery.PartyAgingAsync(customers: true, ct);
        var rows = raw.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["Code"] = r.GetValueOrDefault("Code"),
            ["Name"] = r.GetValueOrDefault("Name"),
            ["B0"] = Dec(r.GetValueOrDefault("Bucket0_30")),
            ["B30"] = Dec(r.GetValueOrDefault("Bucket31_60")),
            ["B60"] = Dec(r.GetValueOrDefault("Bucket61_90")),
            ["B90"] = Dec(r.GetValueOrDefault("Bucket90Plus")),
            ["Balance"] = Dec(r.GetValueOrDefault("Balance")),
        }).ToList();
        return new RptData
        {
            Columns = BuildDefinition().Columns,
            Rows = rows,
            Totals = new Dictionary<string, object?>
            {
                ["Name"] = T("Reports.Totals"),
                ["B0"] = rows.Sum(x => Dec(x["B0"])),
                ["B30"] = rows.Sum(x => Dec(x["B30"])),
                ["B60"] = rows.Sum(x => Dec(x["B60"])),
                ["B90"] = rows.Sum(x => Dec(x["B90"])),
                ["Balance"] = rows.Sum(x => Dec(x["Balance"])),
            }
        };
    }

    private static decimal Dec(object? v) => v is null ? 0m : Convert.ToDecimal(v, CultureInfo.InvariantCulture);
}
