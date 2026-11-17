using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ShouTech.App.Reporting;
using ShouTech.App.Services;

namespace ShouTech.App.Views.Reports;

/// <summary>
/// Stock by batch x warehouse (Phase B 054, design section 2's
/// BatchBalWin): every stock_batch_balances row with its batch master
/// data (supplier batch number, manufacturing and expiry dates, batch
/// status) — the batch-wise balance history the sweep found absent.
/// </summary>
public sealed class BatchBalWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Inv.BatchBalances",
        TitleTextKey = "Reports.BatchBalances",
        Columns = new RptCol[]
        {
            new() { Key = "MaterialCode", HeaderTextKey = "Reports.Col.MaterialCode", Type = RptColType.Text, Width = 110 },
            new() { Key = "MaterialName", HeaderTextKey = "Reports.Col.MaterialName", Type = RptColType.Text, Width = 200 },
            new() { Key = "Unit", HeaderTextKey = "Reports.Col.Unit", Type = RptColType.Text, Width = 70 },
            new() { Key = "BatchNo", HeaderTextKey = "Reports.Col.BatchNo", Type = RptColType.Text, Width = 120 },
            new() { Key = "SupplierBatchNo", HeaderTextKey = "Reports.Col.SupplierBatchNo", Type = RptColType.Text, Width = 130 },
            new() { Key = "Warehouse", HeaderTextKey = "Reports.Col.Warehouse", Type = RptColType.Text, Width = 110 },
            new() { Key = "MfgDate", HeaderTextKey = "Reports.Col.MfgDate", Type = RptColType.Date, Width = 100 },
            new() { Key = "ExpiryDate", HeaderTextKey = "Reports.Col.ExpiryDate", Type = RptColType.Date, Width = 100 },
            new() { Key = "BatchStatus", HeaderTextKey = "Reports.Col.BatchStatus", Type = RptColType.Text, Width = 90 },
            new() { Key = "OnHand", HeaderTextKey = "Reports.Col.OnHand", Type = RptColType.FractionalNumber, Width = 100, Format = "N3" },
        },
        Filters = Array.Empty<RptFlt>()
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var raw = await RptQuery.BatchBalancesAsync(ct);
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
