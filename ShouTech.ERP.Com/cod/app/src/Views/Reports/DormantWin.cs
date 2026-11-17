using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ShouTech.App.Reporting;
using ShouTech.App.Services;

namespace ShouTech.App.Views.Reports;

/// <summary>Accounts with a nonzero balance but no posted journal movement within the chosen number of days.</summary>
public sealed class DormantWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Acc.DormantAccounts",
        TitleTextKey = "Reports.DormantAccounts",
        Columns = new RptCol[]
        {
            new() { Key = "AccountCode", HeaderTextKey = "Reports.Col.AccountCode", Type = RptColType.Text, Width = 110 },
            new() { Key = "AccountName", HeaderTextKey = "Reports.Col.AccountName", Type = RptColType.Text, Width = 240 },
            new() { Key = "Debit", HeaderTextKey = "Reports.Col.Debit", Type = RptColType.Money, Width = 120 },
            new() { Key = "Credit", HeaderTextKey = "Reports.Col.Credit", Type = RptColType.Money, Width = 120 },
            new() { Key = "Balance", HeaderTextKey = "Reports.Col.Balance", Type = RptColType.Money, Width = 120 },
            new() { Key = "LastMoveDate", HeaderTextKey = "Reports.Col.LastMovement", Type = RptColType.Date, Width = 110 }
        },
        Filters = new RptFlt[]
        {
            new() { Key = "Days", LabelTextKey = "Reports.Filter.Days", Kind = RptFltKind.Text, DefaultValue = "90" }
        }
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var days = RptWinUtil.ParseInt(filters.GetString("Days"), 90);
        var raw = await RptQuery.DormantAccountsAsync(days, ct);
        var def = BuildDefinition();
        var rows = RptWinUtil.Map(raw, def.Columns);
        return new RptData
        {
            Columns = def.Columns,
            Rows = rows,
            Totals = RptWinUtil.SumTotals(rows, def.Columns, T("Reports.Totals"), "AccountName")
        };
    }
}
