using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ShouTech.App.Reporting;
using ShouTech.App.Services;

namespace ShouTech.App.Views.Reports;

/// <summary>Per posted sales invoice: revenue, cost at material average cost, profit and margin.</summary>
public sealed class InvoiceProfitsWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Invoices.InvoiceProfits",
        TitleTextKey = "Reports.InvoiceProfits",
        Columns = new RptCol[]
        {
            new() { Key = "DocDate", HeaderTextKey = "Reports.Col.Date", Type = RptColType.Date, Width = 100 },
            new() { Key = "DocNo", HeaderTextKey = "Reports.Col.DocNo", Type = RptColType.Text, Width = 120 },
            new() { Key = "PartyName", HeaderTextKey = "Reports.Col.PartyName", Type = RptColType.Text, Width = 220 },
            new() { Key = "Revenue", HeaderTextKey = "Reports.Col.Revenue", Type = RptColType.Money, Width = 120 },
            new() { Key = "Cost", HeaderTextKey = "Reports.Col.Cost", Type = RptColType.Money, Width = 120 },
            new() { Key = "Profit", HeaderTextKey = "Reports.Col.Profit", Type = RptColType.Money, Width = 120 },
            new() { Key = "MarginPct", HeaderTextKey = "Reports.Col.MarginPct", Type = RptColType.Percent, Width = 90 }
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
        var raw = await RptQuery.InvoiceProfitsAsync(from, to, ct);
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
