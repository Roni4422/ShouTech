using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ShouTech.App.Reporting;
using ShouTech.App.Services;

namespace ShouTech.App.Views.Reports;

/// <summary>
/// Period-based cash-box movement report sourced exclusively from posted
/// general-ledger lines. Opening balances include all earlier posted activity.
/// </summary>
public sealed class CashJournalWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Bonds.CashJournal",
        TitleTextKey = "Reports.CashJournal",
        Columns = new RptCol[]
        {
            new() { Key = "EntryDate", HeaderTextKey = "Reports.Col.Date", Type = RptColType.Date, Width = 100 },
            new() { Key = "DocNo", HeaderTextKey = "Reports.Col.DocNo", Type = RptColType.Text, Width = 125 },
            new() { Key = "CashAccountName", HeaderTextKey = "Reports.Col.CashBox", Type = RptColType.Text, Width = 150 },
            new() { Key = "CounterAccount", HeaderTextKey = "Reports.Col.CounterAccount", Type = RptColType.Text, Width = 190 },
            new() { Key = "Description", HeaderTextKey = "Reports.Col.Description", Type = RptColType.Text, Width = 220 },
            new() { Key = "Reference", HeaderTextKey = "Reports.Col.Reference", Type = RptColType.Text, Width = 120 },
            new() { Key = "OpeningBalance", HeaderTextKey = "Reports.Col.OpeningBalance", Type = RptColType.Money, Width = 125 },
            new() { Key = "Receipt", HeaderTextKey = "Reports.Col.Receipts", Type = RptColType.Money, Width = 115 },
            new() { Key = "Payment", HeaderTextKey = "Reports.Col.Payments", Type = RptColType.Money, Width = 115 },
            new() { Key = "Balance", HeaderTextKey = "Reports.Col.Balance", Type = RptColType.Money, Width = 125 },
            new() { Key = "CreatedBy", HeaderTextKey = "Reports.Col.User", Type = RptColType.Text, Width = 110 }
        },
        Filters = new RptFlt[]
        {
            new() { Key = "From", LabelTextKey = "Reports.FromDate", Kind = RptFltKind.DateFrom, DefaultValue = DateTime.Today },
            new() { Key = "To", LabelTextKey = "Reports.ToDate", Kind = RptFltKind.DateTo, DefaultValue = DateTime.Today }
        }
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var today = DateTime.Today;
        var from = filters.GetDateOr("From", today).Date;
        var to = filters.GetDateOr("To", today).Date;
        if (to < from)
            (from, to) = (to, from);

        var definition = BuildDefinition();
        var raw = await RptQuery.CashJournalAsync(from, to, ct);
        var mapped = RptWinUtil.Map(raw, definition.Columns);
        var rows = new List<IReadOnlyDictionary<string, object?>>(mapped.Count);

        foreach (var item in mapped)
        {
            var row = item.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            if (string.Equals(Convert.ToString(row.GetValueOrDefault("DocNo")), "OPENING", StringComparison.Ordinal))
            {
                row["Description"] = T("Reports.OpeningBalance");
                row["DocNo"] = string.Empty;
            }
            rows.Add(row);
        }

        var openingRow = raw.FirstOrDefault(r =>
            string.Equals(Convert.ToString(r.GetValueOrDefault("DocNo")), "OPENING", StringComparison.Ordinal));
        var opening = RptWinUtil.Dec(openingRow?.GetValueOrDefault("Balance"));
        var movements = raw.Where(r =>
            !string.Equals(Convert.ToString(r.GetValueOrDefault("DocNo")), "OPENING", StringComparison.Ordinal)).ToList();
        var receipts = movements.Sum(r => RptWinUtil.Dec(r.GetValueOrDefault("Receipt")));
        var payments = movements.Sum(r => RptWinUtil.Dec(r.GetValueOrDefault("Payment")));
        var closing = movements.Count == 0
            ? opening
            : RptWinUtil.Dec(movements[^1].GetValueOrDefault("Balance"));

        return new RptData
        {
            Columns = definition.Columns,
            Rows = rows,
            Totals = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["Description"] = T("Reports.Totals"),
                ["OpeningBalance"] = opening,
                ["Receipt"] = receipts,
                ["Payment"] = payments,
                ["Balance"] = closing
            }
        };
    }
}
