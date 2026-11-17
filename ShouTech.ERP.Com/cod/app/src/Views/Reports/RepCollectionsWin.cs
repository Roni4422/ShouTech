using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ShouTech.App.Reporting;
using ShouTech.App.Services;

namespace ShouTech.App.Views.Reports;

/// <summary>Collections per sales rep: payment allocations settled against that rep's invoices (party_ledger carries no rep column).</summary>
public sealed class RepCollectionsWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "SalesRep.SalesRepCollections",
        TitleTextKey = "Reports.SalesRepCollections",
        Columns = new RptCol[]
        {
            new() { Key = "Rep", HeaderTextKey = "Reports.Col.Rep", Type = RptColType.Text, Width = 200 },
            new() { Key = "AllocationCount", HeaderTextKey = "Reports.Col.Count", Type = RptColType.WholeNumber, Width = 110 },
            new() { Key = "Collected", HeaderTextKey = "Reports.Col.Collected", Type = RptColType.Money, Width = 140 }
        },
        Filters = new RptFlt[]
        {
            new() { Key = "From", LabelTextKey = "Reports.Filter.DateFrom", Kind = RptFltKind.DateFrom, DefaultValue = new DateTime(DateTime.Today.Year, 1, 1) },
            new() { Key = "To", LabelTextKey = "Reports.Filter.DateTo", Kind = RptFltKind.DateTo, DefaultValue = DateTime.Today }
        }
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var from = filters.GetDateOr("From", new DateTime(DateTime.Today.Year, 1, 1));
        var to = filters.GetDateOr("To", DateTime.Today);
        var raw = await RptQuery.SalesRepCollectionsAsync(from, to, ct);
        var def = BuildDefinition();
        var rows = RptWinUtil.Map(raw, def.Columns);
        return new RptData
        {
            Columns = def.Columns,
            Rows = rows,
            Totals = RptWinUtil.SumTotals(rows, def.Columns, T("Reports.Totals"), "Rep")
        };
    }
}
