using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ShouTech.App.Reporting;
using ShouTech.App.Services;

namespace ShouTech.App.Views.Reports;

/// <summary>
/// Expired / near-expiry materials BY BATCH (Phase B 054 rebuild).
/// Every row is a real batch from stock_batches with stock on hand in
/// stock_batch_balances: its recorded expiry_date has passed, or falls
/// within the material's alert_before_expiry_days. The pre-054 version
/// inferred an expiry from the last movement date + shelf life because
/// no batch expiry existed anywhere; that inference is retired — this
/// window never fabricates a date. The data rule lives in
/// RptQuery.ExpiredMaterialsAsync and is mirrored by
/// BatchSerialService.GetExpiredBatches (the headless-provable copy).
/// </summary>
public sealed class ExpiredWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Inv.ExpiredMaterials",
        TitleTextKey = "Reports.ExpiredMaterials",
        Columns = new RptCol[]
        {
            new() { Key = "MaterialCode", HeaderTextKey = "Reports.Col.MaterialCode", Type = RptColType.Text, Width = 110 },
            new() { Key = "MaterialName", HeaderTextKey = "Reports.Col.MaterialName", Type = RptColType.Text, Width = 200 },
            new() { Key = "Unit", HeaderTextKey = "Reports.Col.Unit", Type = RptColType.Text, Width = 70 },
            new() { Key = "BatchNo", HeaderTextKey = "Reports.Col.BatchNo", Type = RptColType.Text, Width = 120 },
            new() { Key = "Warehouse", HeaderTextKey = "Reports.Col.Warehouse", Type = RptColType.Text, Width = 110 },
            new() { Key = "ExpiryDate", HeaderTextKey = "Reports.Col.ExpiryDate", Type = RptColType.Date, Width = 110 },
            new() { Key = "DaysRemaining", HeaderTextKey = "Reports.Col.DaysRemaining", Type = RptColType.WholeNumber, Width = 110 },
            new() { Key = "OnHand", HeaderTextKey = "Reports.Col.OnHand", Type = RptColType.FractionalNumber, Width = 100, Format = "N3" },
            new() { Key = "Note", HeaderTextKey = "Reports.Col.Note", Type = RptColType.Text, Width = 200 },
        },
        Filters = Array.Empty<RptFlt>()
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var raw = await RptQuery.ExpiredMaterialsAsync(ct);
        var def = BuildDefinition();
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var r in raw)
        {
            var isExpired = (int)RptWinUtil.Dec(r.GetValueOrDefault("IsExpired")) != 0;
            rows.Add(new Dictionary<string, object?>
            {
                ["MaterialCode"] = r.GetValueOrDefault("MaterialCode"),
                ["MaterialName"] = r.GetValueOrDefault("MaterialName"),
                ["Unit"] = r.GetValueOrDefault("Unit"),
                ["BatchNo"] = r.GetValueOrDefault("BatchNo"),
                ["Warehouse"] = r.GetValueOrDefault("Warehouse"),
                ["ExpiryDate"] = r.GetValueOrDefault("ExpiryDate"),
                ["DaysRemaining"] = (int)RptWinUtil.Dec(r.GetValueOrDefault("DaysRemaining")),
                ["OnHand"] = RptWinUtil.Dec(r.GetValueOrDefault("OnHand")),
                ["Note"] = T(isExpired ? "Reports.ExpiryNoteExpired" : "Reports.ExpiryNoteNearExpiry"),
            });
        }
        return new RptData
        {
            Columns = def.Columns,
            Rows = rows,
            Totals = RptWinUtil.SumTotals(rows, def.Columns, T("Reports.Totals"), "MaterialName")
        };
    }
}
