using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ShouTech.App.Reporting;
using ShouTech.App.Services;

namespace ShouTech.App.Views.Reports;

/// <summary>Per currency and month: min/max/average and debit-weighted average exchange rate from journal entries.</summary>
public sealed class AvgRateWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Acc.AverageCurrencyRate",
        TitleTextKey = "Reports.AverageCurrencyRate",
        Columns = new RptCol[]
        {
            new() { Key = "Currency", HeaderTextKey = "Reports.Col.Currency", Type = RptColType.Text, Width = 90 },
            new() { Key = "Month", HeaderTextKey = "Reports.Col.Month", Type = RptColType.Text, Width = 90 },
            new() { Key = "Entries", HeaderTextKey = "Reports.Col.Count", Type = RptColType.WholeNumber, Width = 90 },
            new() { Key = "MinRate", HeaderTextKey = "Reports.Col.MinRate", Type = RptColType.FractionalNumber, Width = 100, Format = "N4" },
            new() { Key = "MaxRate", HeaderTextKey = "Reports.Col.MaxRate", Type = RptColType.FractionalNumber, Width = 100, Format = "N4" },
            new() { Key = "AvgRate", HeaderTextKey = "Reports.Col.AvgRate", Type = RptColType.FractionalNumber, Width = 100, Format = "N4" },
            new() { Key = "WeightedRate", HeaderTextKey = "Reports.Col.WeightedRate", Type = RptColType.FractionalNumber, Width = 110, Format = "N4" }
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
        var raw = await RptQuery.AverageCurrencyRateAsync(from, to, ct);
        var def = BuildDefinition();
        var rows = RptWinUtil.Map(raw, def.Columns);
        return new RptData
        {
            Columns = def.Columns,
            Rows = rows,
            Totals = RptWinUtil.SumTotals(rows, def.Columns, T("Reports.Totals"), "Currency", "MinRate", "MaxRate", "AvgRate", "WeightedRate")
        };
    }
}
