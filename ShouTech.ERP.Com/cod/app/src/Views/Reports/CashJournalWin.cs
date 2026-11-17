using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ShouTech.App.Reporting;
using ShouTech.App.Services;

namespace ShouTech.App.Views.Reports;

/// <summary>Journal entries whose lines touch cash/bank/box accounts (identified by account name, as elsewhere in the app).</summary>
public sealed class CashJournalWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Bonds.CashJournal",
        TitleTextKey = "Reports.CashJournal",
        Columns = new RptCol[]
        {
            new() { Key = "EntryDate", HeaderTextKey = "Reports.Col.Date", Type = RptColType.Date, Width = 100 },
            new() { Key = "DocNo", HeaderTextKey = "Reports.Col.DocNo", Type = RptColType.Text, Width = 130 },
            new() { Key = "Description", HeaderTextKey = "Reports.Col.Description", Type = RptColType.Text, Width = 260 },
            new() { Key = "Reference", HeaderTextKey = "Reports.Col.Reference", Type = RptColType.Text, Width = 120 },
            new() { Key = "Debit", HeaderTextKey = "Reports.Col.Debit", Type = RptColType.Money, Width = 120 },
            new() { Key = "Credit", HeaderTextKey = "Reports.Col.Credit", Type = RptColType.Money, Width = 120 },
            new() { Key = "Status", HeaderTextKey = "Reports.Col.Status", Type = RptColType.Text, Width = 90 }
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
        var raw = await RptQuery.CashJournalAsync(from, to, ct);
        var def = BuildDefinition();
        var rows = RptWinUtil.Map(raw, def.Columns);
        return new RptData
        {
            Columns = def.Columns,
            Rows = rows,
            Totals = RptWinUtil.SumTotals(rows, def.Columns, T("Reports.Totals"), "Description")
        };
    }
}
