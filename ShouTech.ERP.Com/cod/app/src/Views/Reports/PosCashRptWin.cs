using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ShouTech.App.Reporting;
using ShouTech.App.Services;

namespace ShouTech.App.Views.Reports;

/// <summary>POS sales documents (doc_kind POS) by date and warehouse: count, totals, discounts, average ticket.</summary>
public sealed class PosCashRptWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "POS.POSCashReport",
        TitleTextKey = "Reports.PosCashReport",
        Columns = new RptCol[]
        {
            new() { Key = "DocDate", HeaderTextKey = "Reports.Col.Date", Type = RptColType.Date, Width = 100 },
            new() { Key = "Warehouse", HeaderTextKey = "Reports.Col.Warehouse", Type = RptColType.Text, Width = 160 },
            new() { Key = "DocCount", HeaderTextKey = "Reports.Col.DocCount", Type = RptColType.WholeNumber, Width = 90 },
            new() { Key = "Subtotal", HeaderTextKey = "Reports.Col.Subtotal", Type = RptColType.Money, Width = 120 },
            new() { Key = "Discount", HeaderTextKey = "Reports.Col.Discount", Type = RptColType.Money, Width = 110 },
            new() { Key = "Tax", HeaderTextKey = "Reports.Col.Tax", Type = RptColType.Money, Width = 110 },
            new() { Key = "Total", HeaderTextKey = "Reports.Col.Total", Type = RptColType.Money, Width = 130 },
            new() { Key = "AvgTicket", HeaderTextKey = "Reports.Col.AvgTicket", Type = RptColType.Money, Width = 110 }
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
        var raw = await RptQuery.PosCashReportAsync(from, to, ct);
        var def = BuildDefinition();
        var rows = RptWinUtil.Map(raw, def.Columns);
        return new RptData
        {
            Columns = def.Columns,
            Rows = rows,
            Totals = RptWinUtil.SumTotals(rows, def.Columns, T("Reports.Totals"), "Warehouse", "AvgTicket")
        };
    }
}
