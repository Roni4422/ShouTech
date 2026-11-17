using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ShouTech.App.Reporting;
using ShouTech.App.Services;

namespace ShouTech.App.Views.Reports;

/// <summary>Per customer: invoiced vs collected from the party ledger, with collection percentage.</summary>
public sealed class CollectionRatesWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Acc.CollectionRates",
        TitleTextKey = "Reports.CollectionRates",
        Columns = new RptCol[]
        {
            new() { Key = "PartyCode", HeaderTextKey = "Reports.Col.PartyCode", Type = RptColType.Text, Width = 100 },
            new() { Key = "PartyName", HeaderTextKey = "Reports.Col.PartyName", Type = RptColType.Text, Width = 220 },
            new() { Key = "Invoiced", HeaderTextKey = "Reports.Col.Invoiced", Type = RptColType.Money, Width = 130 },
            new() { Key = "Collected", HeaderTextKey = "Reports.Col.Collected", Type = RptColType.Money, Width = 130 },
            new() { Key = "Balance", HeaderTextKey = "Reports.Col.Balance", Type = RptColType.Money, Width = 120 },
            new() { Key = "CollectionPct", HeaderTextKey = "Reports.Col.CollectionPct", Type = RptColType.Percent, Width = 110 }
        },
        Filters = Array.Empty<RptFlt>()
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var raw = await RptQuery.CollectionRatesAsync(ct);
        var def = BuildDefinition();
        var rows = RptWinUtil.Map(raw, def.Columns);
        return new RptData
        {
            Columns = def.Columns,
            Rows = rows,
            Totals = RptWinUtil.SumTotals(rows, def.Columns, T("Reports.Totals"), "PartyName")
        };
    }
}
