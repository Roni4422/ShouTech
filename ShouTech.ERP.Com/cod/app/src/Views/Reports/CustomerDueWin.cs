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
/// Customers with an outstanding balance, sorted by oldest ledger date, with
/// the age in days of the oldest open entry. Serves both ribbon keys
/// Customers.CustomerDue and Customers.SearchDues. The query runs on
/// party_ledger + the canonical parties master (migration 061).
/// </summary>
public sealed class CustomerDueWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Customers.CustomerDue",
        TitleTextKey = "Reports.CustomerDue",
        Columns = new RptCol[]
        {
            new() { Key = "PartyCode", HeaderTextKey = "Reports.Col.PartyCode", Type = RptColType.Text, Width = 110 },
            new() { Key = "PartyName", HeaderTextKey = "Reports.Col.PartyName", Type = RptColType.Text, Width = 240 },
            new() { Key = "Phone", HeaderTextKey = "Reports.Col.Phone", Type = RptColType.Text, Width = 120 },
            new() { Key = "Invoiced", HeaderTextKey = "Reports.Col.Invoiced", Type = RptColType.Money, Width = 130, Format = "N2" },
            new() { Key = "Collected", HeaderTextKey = "Reports.Col.Collected", Type = RptColType.Money, Width = 130, Format = "N2" },
            new() { Key = "Balance", HeaderTextKey = "Reports.Col.Balance", Type = RptColType.Money, Width = 130, Format = "N2" },
            new() { Key = "OldestDate", HeaderTextKey = "Reports.Col.OldestDue", Type = RptColType.Date, Width = 110 },
            new() { Key = "AgeDays", HeaderTextKey = "Reports.Col.AgeDays", Type = RptColType.WholeNumber, Width = 100 },
        },
        Filters = Array.Empty<RptFlt>()
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var raw = await RptQuery.CustomerDueAsync(ct);
        var def = BuildDefinition();
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var r in raw)
        {
            var oldest = RptWinUtil.ParseDate(r.GetValueOrDefault("OldestDate"));
            rows.Add(new Dictionary<string, object?>
            {
                ["PartyCode"] = r.GetValueOrDefault("PartyCode"),
                ["PartyName"] = r.GetValueOrDefault("PartyName"),
                ["Phone"] = r.GetValueOrDefault("Phone"),
                ["Invoiced"] = RptWinUtil.Dec(r.GetValueOrDefault("Invoiced")),
                ["Collected"] = RptWinUtil.Dec(r.GetValueOrDefault("Collected")),
                ["Balance"] = RptWinUtil.Dec(r.GetValueOrDefault("Balance")),
                ["OldestDate"] = r.GetValueOrDefault("OldestDate"),
                ["AgeDays"] = oldest is null ? 0 : Math.Max(0, (DateTime.Today - oldest.Value.Date).Days),
            });
        }
        return new RptData
        {
            Columns = def.Columns,
            Rows = rows,
            Totals = RptWinUtil.SumTotals(rows, def.Columns, T("Reports.Totals"), "PartyName", "AgeDays")
        };
    }
}
