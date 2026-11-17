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
/// Sales targets vs actuals per customer (targets, migration 036). When no
/// target rows exist for the period — including while migration 036 has not
/// landed yet — the report falls back to real actual sales per party with a
/// zero target, so it is never a placeholder.
/// </summary>
public sealed class TargetsRptWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Customers.TargetsReport",
        TitleTextKey = "Reports.TargetsReport",
        Columns = new RptCol[]
        {
            new() { Key = "PartyCode", HeaderTextKey = "Reports.Col.PartyCode", Type = RptColType.Text, Width = 110 },
            new() { Key = "PartyName", HeaderTextKey = "Reports.Col.PartyName", Type = RptColType.Text, Width = 240 },
            new() { Key = "Target", HeaderTextKey = "Reports.Col.Target", Type = RptColType.Money, Width = 140, Format = "N2" },
            new() { Key = "Actual", HeaderTextKey = "Reports.Col.Actual", Type = RptColType.Money, Width = 140, Format = "N2" },
            new() { Key = "Variance", HeaderTextKey = "Reports.Col.Variance", Type = RptColType.Money, Width = 130, Format = "N2" },
            new() { Key = "AchievementPct", HeaderTextKey = "Reports.Col.AchievementPct", Type = RptColType.Percent, Width = 110 },
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
        var raw = await RptQuery.TargetsAsync(from, to, ct);
        var def = BuildDefinition();
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        if (raw.Count > 0)
        {
            foreach (var r in raw)
            {
                var target = RptWinUtil.Dec(r.GetValueOrDefault("Target"));
                var actual = RptWinUtil.Dec(r.GetValueOrDefault("Actual"));
                rows.Add(Row(r, target, actual));
            }
        }
        else
        {
            var actuals = await RptQuery.SalesByPartyAsync(from, to, ct);
            foreach (var r in actuals)
                rows.Add(Row(r, 0m, RptWinUtil.Dec(r.GetValueOrDefault("Actual"))));
        }
        return new RptData
        {
            Columns = def.Columns,
            Rows = rows,
            Totals = RptWinUtil.SumTotals(rows, def.Columns, T("Reports.Totals"), "PartyName")
        };

        static Dictionary<string, object?> Row(IReadOnlyDictionary<string, object?> r, decimal target, decimal actual)
            => new()
            {
                ["PartyCode"] = r.GetValueOrDefault("PartyCode"),
                ["PartyName"] = r.GetValueOrDefault("PartyName"),
                ["Target"] = target,
                ["Actual"] = actual,
                ["Variance"] = actual - target,
                ["AchievementPct"] = target == 0 ? 0m : actual / target,
            };
    }
}
