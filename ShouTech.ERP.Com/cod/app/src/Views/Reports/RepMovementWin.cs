using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ShouTech.App.Reporting;
using ShouTech.App.Services;

namespace ShouTech.App.Views.Reports;

/// <summary>Posted sales documents grouped by sales rep (sales_rep_name on the document header).</summary>
public sealed class RepMovementWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "SalesRep.SalesRepMovement",
        TitleTextKey = "Reports.SalesRepMovement",
        Columns = new RptCol[]
        {
            new() { Key = "Rep", HeaderTextKey = "Reports.Col.Rep", Type = RptColType.Text, Width = 200 },
            new() { Key = "DocCount", HeaderTextKey = "Reports.Col.DocCount", Type = RptColType.WholeNumber, Width = 100 },
            new() { Key = "Subtotal", HeaderTextKey = "Reports.Col.Subtotal", Type = RptColType.Money, Width = 120 },
            new() { Key = "Discount", HeaderTextKey = "Reports.Col.Discount", Type = RptColType.Money, Width = 110 },
            new() { Key = "Tax", HeaderTextKey = "Reports.Col.Tax", Type = RptColType.Money, Width = 110 },
            new() { Key = "Total", HeaderTextKey = "Reports.Col.Total", Type = RptColType.Money, Width = 130 }
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
        var raw = await RptQuery.SalesRepMovementAsync(from, to, ct);
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
