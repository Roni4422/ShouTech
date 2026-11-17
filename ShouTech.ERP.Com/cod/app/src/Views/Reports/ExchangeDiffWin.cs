using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ShouTech.App.Reporting;
using ShouTech.App.Services;

namespace ShouTech.App.Views.Reports;

/// <summary>Posted entries whose exchange rate differs from 1: foreign totals restated at the entry rate and the difference.</summary>
public sealed class ExchangeDiffWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Acc.ExchangeRateDifferences",
        TitleTextKey = "Reports.ExchangeRateDifferences",
        Columns = new RptCol[]
        {
            new() { Key = "DocDate", HeaderTextKey = "Reports.Col.Date", Type = RptColType.Date, Width = 100 },
            new() { Key = "DocNo", HeaderTextKey = "Reports.Col.DocNo", Type = RptColType.Text, Width = 130 },
            new() { Key = "Currency", HeaderTextKey = "Reports.Col.Currency", Type = RptColType.Text, Width = 90 },
            new() { Key = "Rate", HeaderTextKey = "Reports.Col.Rate", Type = RptColType.FractionalNumber, Width = 90, Format = "N4" },
            new() { Key = "Debit", HeaderTextKey = "Reports.Col.Debit", Type = RptColType.Money, Width = 120 },
            new() { Key = "Credit", HeaderTextKey = "Reports.Col.Credit", Type = RptColType.Money, Width = 120 },
            new() { Key = "BaseDebit", HeaderTextKey = "Reports.Col.BaseDebit", Type = RptColType.Money, Width = 120 },
            new() { Key = "Difference", HeaderTextKey = "Reports.Col.Difference", Type = RptColType.Money, Width = 120 }
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
        var raw = await RptQuery.ExchangeRateDifferencesAsync(from, to, ct);
        var def = BuildDefinition();
        var rows = RptWinUtil.Map(raw, def.Columns);
        return new RptData
        {
            Columns = def.Columns,
            Rows = rows,
            Totals = RptWinUtil.SumTotals(rows, def.Columns, T("Reports.Totals"), "DocNo", "Rate")
        };
    }
}
