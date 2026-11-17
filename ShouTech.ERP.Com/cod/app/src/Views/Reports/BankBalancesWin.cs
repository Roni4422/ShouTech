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
/// Bank balances: posted GL balances of bank accounts. Primary source is the
/// banks table (migration 031) joined through gl_account_code; while that
/// table is absent or empty the report falls back to bank-named GL accounts
/// — the same accounts, read directly — so it always shows real balances.
/// </summary>
public sealed class BankBalancesWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Banks.BankBalances",
        TitleTextKey = "Reports.BankBalances",
        Columns = new RptCol[]
        {
            new() { Key = "AccountCode", HeaderTextKey = "Reports.Col.AccountCode", Type = RptColType.Text, Width = 120 },
            new() { Key = "AccountName", HeaderTextKey = "Reports.Col.AccountName", Type = RptColType.Text, Width = 280 },
            new() { Key = "Debit", HeaderTextKey = "Reports.Col.Debit", Type = RptColType.Money, Width = 130, Format = "N2" },
            new() { Key = "Credit", HeaderTextKey = "Reports.Col.Credit", Type = RptColType.Money, Width = 130, Format = "N2" },
            new() { Key = "Balance", HeaderTextKey = "Reports.Col.Balance", Type = RptColType.Money, Width = 140, Format = "N2" },
        },
        Filters = Array.Empty<RptFlt>()
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var raw = await RptQuery.BankBalancesFromBanksAsync(ct);
        if (raw.Count == 0)
            raw = await RptQuery.BankAccountsGlAsync(ct);
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
