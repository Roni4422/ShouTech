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
/// Cash flow statement. Method (indirect approximation, also documented on
/// RptQuery.CashFlowSectionsAsync): posted journal lines touching cash/bank
/// accounts are classified Operating / Investing / Financing by the account
/// type of each entry's first non-cash counterpart line; the schedule adds
/// the net change and the opening/closing cash positions from the same GL.
/// Structured statement: no flat totals.
/// </summary>
public sealed class CashFlowWin : RptBase
{
    protected override bool AllowComputedTotals => false;

    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Acc.CashFlowStatement",
        TitleTextKey = "Reports.CashFlowStatement",
        Columns = new RptCol[]
        {
            new() { Key = "Section", HeaderTextKey = "Reports.Col.Section", Type = RptColType.Text, Width = 220 },
            new() { Key = "Item", HeaderTextKey = "Reports.Col.Item", Type = RptColType.Text, Width = 320 },
            new() { Key = "Amount", HeaderTextKey = "Reports.Col.Amount", Type = RptColType.Money, Width = 160, Format = "N2" },
        },
        Filters = new RptFlt[]
        {
            new() { Key = "From", LabelTextKey = "Reports.Filter.DateFrom", Kind = RptFltKind.DateFrom, DefaultValue = new DateTime(DateTime.Today.Year, 1, 1) },
            new() { Key = "To", LabelTextKey = "Reports.Filter.DateTo", Kind = RptFltKind.DateTo, DefaultValue = DateTime.Today },
        }
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var from = filters.GetDateOr("From", new DateTime(DateTime.Today.Year, 1, 1));
        var to = filters.GetDateOr("To", DateTime.Today);
        var sections = await RptQuery.CashFlowSectionsAsync(from, to, ct);
        var openingRaw = await RptQuery.CashPositionAsync(from.AddDays(-1), ct);
        var closingRaw = await RptQuery.CashPositionAsync(to, ct);
        var opening = openingRaw.Count > 0 ? RptWinUtil.Dec(openingRaw[0].GetValueOrDefault("Balance")) : 0m;
        var closing = closingRaw.Count > 0 ? RptWinUtil.Dec(closingRaw[0].GetValueOrDefault("Balance")) : 0m;

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        void Add(string section, string item, decimal amount)
            => rows.Add(new Dictionary<string, object?> { ["Section"] = section, ["Item"] = item, ["Amount"] = amount });

        decimal net = 0;
        foreach (var s in sections)
        {
            var name = Convert.ToString(s.GetValueOrDefault("Section"), CultureInfo.InvariantCulture) ?? "";
            var amount = RptWinUtil.Dec(s.GetValueOrDefault("Amount"));
            net += amount;
            Add(name, T("Reports.Col.Total"), amount);
        }
        Add("", T("Reports.NetChange"), net);
        Add("", T("Reports.BeginningBalance"), opening);
        Add("", T("Reports.EndingBalance"), closing);
        return new RptData { Columns = BuildDefinition().Columns, Rows = rows, Totals = null };
    }
}
