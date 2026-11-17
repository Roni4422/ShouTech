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
/// Bank estimate: current bank GL balances plus cheques falling due within
/// the next N days, projected per bank. Cheques (migration 033) carry no GL
/// account link, so due cheques are shown as one honest aggregate row for
/// all banks instead of being invented per bank.
/// </summary>
public sealed class BankEstimateWin : RptBase
{
    protected override bool AllowComputedTotals => false;

    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Banks.BankEstimate",
        TitleTextKey = "Reports.BankEstimate",
        Columns = new RptCol[]
        {
            new() { Key = "Item", HeaderTextKey = "Reports.Col.Item", Type = RptColType.Text, Width = 320 },
            new() { Key = "Balance", HeaderTextKey = "Reports.Col.Balance", Type = RptColType.Money, Width = 140, Format = "N2" },
            new() { Key = "ChequesIn", HeaderTextKey = "Reports.Col.ChequesIn", Type = RptColType.Money, Width = 130, Format = "N2" },
            new() { Key = "ChequesOut", HeaderTextKey = "Reports.Col.ChequesOut", Type = RptColType.Money, Width = 130, Format = "N2" },
            new() { Key = "Projected", HeaderTextKey = "Reports.Col.Projected", Type = RptColType.Money, Width = 150, Format = "N2" },
        },
        Filters = new RptFlt[]
        {
            new() { Key = "Days", LabelTextKey = "Reports.Filter.Days", Kind = RptFltKind.Text, DefaultValue = "30" },
        }
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var days = RptWinUtil.ParseInt(filters.GetString("Days"), 30);
        var banks = await RptQuery.BankBalancesFromBanksAsync(ct);
        if (banks.Count == 0)
            banks = await RptQuery.BankAccountsGlAsync(ct);
        var cheques = await RptQuery.ChequesDueSummaryAsync(days, ct);
        decimal chqIn = 0, chqOut = 0;
        foreach (var c in cheques)
        {
            var dir = Convert.ToString(c.GetValueOrDefault("Direction"), CultureInfo.InvariantCulture);
            if (string.Equals(dir, "IN", StringComparison.OrdinalIgnoreCase)) chqIn = RptWinUtil.Dec(c.GetValueOrDefault("Amount"));
            else if (string.Equals(dir, "OUT", StringComparison.OrdinalIgnoreCase)) chqOut = RptWinUtil.Dec(c.GetValueOrDefault("Amount"));
        }

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        decimal totalBalance = 0;
        foreach (var b in banks)
        {
            var bal = RptWinUtil.Dec(b.GetValueOrDefault("Balance"));
            totalBalance += bal;
            var code = Convert.ToString(b.GetValueOrDefault("AccountCode"), CultureInfo.InvariantCulture) ?? "";
            var name = Convert.ToString(b.GetValueOrDefault("AccountName"), CultureInfo.InvariantCulture) ?? "";
            rows.Add(new Dictionary<string, object?>
            {
                ["Item"] = string.IsNullOrWhiteSpace(name) ? code : $"{code} - {name}",
                ["Balance"] = bal, ["ChequesIn"] = 0m, ["ChequesOut"] = 0m, ["Projected"] = bal,
            });
        }
        rows.Add(new Dictionary<string, object?>
        {
            ["Item"] = T("Reports.AllBanksCheques"),
            ["Balance"] = 0m, ["ChequesIn"] = chqIn, ["ChequesOut"] = chqOut, ["Projected"] = chqIn - chqOut,
        });
        var def = BuildDefinition();
        return new RptData
        {
            Columns = def.Columns,
            Rows = rows,
            Totals = new Dictionary<string, object?>
            {
                ["Item"] = T("Reports.Totals"),
                ["Balance"] = totalBalance,
                ["ChequesIn"] = chqIn,
                ["ChequesOut"] = chqOut,
                ["Projected"] = totalBalance + chqIn - chqOut,
            }
        };
    }
}
