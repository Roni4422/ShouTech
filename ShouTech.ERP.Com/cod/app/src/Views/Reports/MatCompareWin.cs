using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ShouTech.App.Reporting;
using ShouTech.App.Services;

namespace ShouTech.App.Views.Reports;

/// <summary>Two-period comparison of net movement qty and value per material.</summary>
public sealed class MatCompareWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Inv.MaterialComparison",
        TitleTextKey = "Reports.MaterialComparison",
        Columns = new RptCol[]
        {
            new() { Key = "MaterialCode", HeaderTextKey = "Reports.Col.MaterialCode", Type = RptColType.Text, Width = 110 },
            new() { Key = "MaterialName", HeaderTextKey = "Reports.Col.MaterialName", Type = RptColType.Text, Width = 220 },
            new() { Key = "Qty1", HeaderTextKey = "Reports.Col.QtyP1", Type = RptColType.FractionalNumber, Width = 90, Format = "N3" },
            new() { Key = "Value1", HeaderTextKey = "Reports.Col.ValueP1", Type = RptColType.Money, Width = 110 },
            new() { Key = "Qty2", HeaderTextKey = "Reports.Col.QtyP2", Type = RptColType.FractionalNumber, Width = 90, Format = "N3" },
            new() { Key = "Value2", HeaderTextKey = "Reports.Col.ValueP2", Type = RptColType.Money, Width = 110 },
            new() { Key = "QtyDiff", HeaderTextKey = "Reports.Col.QtyDiff", Type = RptColType.FractionalNumber, Width = 90, Format = "N3" },
            new() { Key = "ValueDiff", HeaderTextKey = "Reports.Col.ValueDiff", Type = RptColType.Money, Width = 110 }
        },
        Filters = new RptFlt[]
        {
            new() { Key = "From", LabelTextKey = "Reports.Filter.DateFrom", Kind = RptFltKind.DateFrom, DefaultValue = new DateTime(DateTime.Today.Year, 1, 1) },
            new() { Key = "To", LabelTextKey = "Reports.Filter.DateTo", Kind = RptFltKind.DateTo, DefaultValue = DateTime.Today },
            new() { Key = "From2", LabelTextKey = "Reports.Filter.P2From", Kind = RptFltKind.DateFrom, DefaultValue = new DateTime(DateTime.Today.Year - 1, 1, 1) },
            new() { Key = "To2", LabelTextKey = "Reports.Filter.P2To", Kind = RptFltKind.DateTo, DefaultValue = new DateTime(DateTime.Today.Year - 1, 12, 31) }
        }
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var from = filters.GetDateOr("From", new DateTime(DateTime.Today.Year, 1, 1));
        var to = filters.GetDateOr("To", DateTime.Today);
        var from2 = filters.GetDateOr("From2", new DateTime(DateTime.Today.Year - 1, 1, 1));
        var to2 = filters.GetDateOr("To2", new DateTime(DateTime.Today.Year - 1, 12, 31));
        var raw = await RptQuery.MaterialComparisonAsync(from, to, from2, to2, ct);
        var def = BuildDefinition();
        var rows = RptWinUtil.Map(raw, def.Columns);
        return new RptData
        {
            Columns = def.Columns,
            Rows = rows,
            Totals = RptWinUtil.SumTotals(rows, def.Columns, T("Reports.Totals"), "MaterialName")
        };
    }
}
