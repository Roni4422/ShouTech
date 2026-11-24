using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ShouTech.App.Reporting;
using ShouTech.App.Services;

namespace ShouTech.App.Views.Reports;

/// <summary>Supplier ledger statement with base-currency opening and running balances.</summary>
public sealed class SupStatementWin : RptBase
{
    /// <summary>Optional pre-filter (a party code) applied ahead of the
    /// filter bar's Search value (W1: CstCard's Statement button).</summary>
    public string? InitialSearch { get; set; }

    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "AP.SupplierStatement",
        TitleTextKey = "Reports.SupplierStatement",
        Columns = new RptCol[]
        {
            new() { Key = "SupplierCode", HeaderTextKey = "Reports.Col.PartyCode", Type = RptColType.Text, Width = 95 },
            new() { Key = "SupplierName", HeaderTextKey = "Reports.Col.PartyName", Type = RptColType.Text, Width = 180 },
            new() { Key = "EntryDate", HeaderTextKey = "Reports.Col.Date", Type = RptColType.Date, Width = 95 },
            new() { Key = "DocumentNo", HeaderTextKey = "Reports.Col.DocNo", Type = RptColType.Text, Width = 110 },
            new() { Key = "DocumentKind", HeaderTextKey = "Reports.Col.DocKind", Type = RptColType.Text, Width = 75 },
            new() { Key = "Note", HeaderTextKey = "Reports.Col.Description", Type = RptColType.Text, Width = 180 },
            new() { Key = "CurrencyCode", HeaderTextKey = "Reports.Col.Currency", Type = RptColType.Text, Width = 75 },
            new() { Key = "DebitBase", HeaderTextKey = "Reports.Col.DebitBase", Type = RptColType.Money, Width = 105, Format = "N2" },
            new() { Key = "CreditBase", HeaderTextKey = "Reports.Col.CreditBase", Type = RptColType.Money, Width = 105, Format = "N2" },
            new() { Key = "OpeningBalanceBase", HeaderTextKey = "Reports.Col.OpeningBalance", Type = RptColType.Money, Width = 120, Format = "N2" },
            new() { Key = "RunningBalanceBase", HeaderTextKey = "Reports.Col.RunningBalance", Type = RptColType.Money, Width = 125, Format = "N2" },
        },
        Filters = new RptFlt[]
        {
            new() { Key = "From", LabelTextKey = "Reports.Filter.DateFrom", Kind = RptFltKind.DateFrom, DefaultValue = new DateTime(DateTime.Today.Year, 1, 1) },
            new() { Key = "To", LabelTextKey = "Reports.Filter.DateTo", Kind = RptFltKind.DateTo, DefaultValue = DateTime.Today },
            new() { Key = "Search", LabelTextKey = "Reports.Filter.Search", Kind = RptFltKind.Text },
        }
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var from = filters.GetDateOr("From", new DateTime(DateTime.Today.Year, 1, 1));
        var to = filters.GetDateOr("To", DateTime.Today);
        if (from.Date > to.Date)
            throw new ArgumentException("The start date must not be after the end date.");

        var search = string.IsNullOrWhiteSpace(InitialSearch) ? filters.GetString("Search") : InitialSearch;
        var raw = await RptQuery.SupplierStatementAsync(from, to, search, ct);
        var rows = RptWinUtil.Map(raw, BuildDefinition().Columns);
        var openingBySupplier = rows
            .GroupBy(row => Convert.ToString(row.GetValueOrDefault("SupplierCode"), CultureInfo.InvariantCulture) ?? string.Empty)
            .ToDictionary(group => group.Key, group => RptWinUtil.Dec(group.First().GetValueOrDefault("OpeningBalanceBase")), StringComparer.Ordinal);
        var totals = new Dictionary<string, object?>
        {
            ["Note"] = T("Reports.Totals"),
            ["DebitBase"] = rows.Sum(row => RptWinUtil.Dec(row.GetValueOrDefault("DebitBase"))),
            ["CreditBase"] = rows.Sum(row => RptWinUtil.Dec(row.GetValueOrDefault("CreditBase"))),
        };
        if (openingBySupplier.Count > 0)
            totals["OpeningBalanceBase"] = openingBySupplier.Values.Sum();

        return new RptData
        {
            Columns = BuildDefinition().Columns,
            Rows = rows,
            Totals = rows.Count == 0 ? null : totals
        };
    }
}
