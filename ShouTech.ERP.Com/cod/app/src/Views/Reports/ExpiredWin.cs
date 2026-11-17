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
/// Expired / expiring materials. Schema fact: no batch or lot expiry dates
/// are stored anywhere in the database — materials only carry the has_expiry
/// flag, shelf_life_days and alert_before_expiry_days. The report therefore
/// lists flagged materials with their last movement date and derives the
/// Note honestly from shelf life relative to that last movement; it never
/// invents a batch expiry date.
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
            new() { Key = "MaterialName", HeaderTextKey = "Reports.Col.MaterialName", Type = RptColType.Text, Width = 220 },
            new() { Key = "Unit", HeaderTextKey = "Reports.Col.Unit", Type = RptColType.Text, Width = 70 },
            new() { Key = "ShelfLifeDays", HeaderTextKey = "Reports.Col.ShelfLifeDays", Type = RptColType.WholeNumber, Width = 110 },
            new() { Key = "AlertDays", HeaderTextKey = "Reports.Col.AlertDays", Type = RptColType.WholeNumber, Width = 100 },
            new() { Key = "LastMoveDate", HeaderTextKey = "Reports.Col.LastMovement", Type = RptColType.Date, Width = 110 },
            new() { Key = "OnHand", HeaderTextKey = "Reports.Col.OnHand", Type = RptColType.FractionalNumber, Width = 100, Format = "N3" },
            new() { Key = "Note", HeaderTextKey = "Reports.Col.Note", Type = RptColType.Text, Width = 320 },
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
            var shelf = (int)RptWinUtil.Dec(r.GetValueOrDefault("ShelfLifeDays"));
            var alert = (int)RptWinUtil.Dec(r.GetValueOrDefault("AlertDays"));
            var last = RptWinUtil.ParseDate(r.GetValueOrDefault("LastMoveDate"));
            string note;
            if (shelf <= 0 || last is null)
                note = T("Reports.ExpiryNoteFlagOnly");
            else
            {
                var expiry = last.Value.Date.AddDays(shelf);
                var daysLeft = (expiry - DateTime.Today).Days;
                note = daysLeft < 0 ? T("Reports.ExpiryNoteElapsed")
                     : daysLeft <= alert ? T("Reports.ExpiryNoteWithinAlert")
                     : T("Reports.ExpiryNoteWithinLife");
            }
            rows.Add(new Dictionary<string, object?>
            {
                ["MaterialCode"] = r.GetValueOrDefault("MaterialCode"),
                ["MaterialName"] = r.GetValueOrDefault("MaterialName"),
                ["Unit"] = r.GetValueOrDefault("Unit"),
                ["ShelfLifeDays"] = shelf,
                ["AlertDays"] = alert,
                ["LastMoveDate"] = r.GetValueOrDefault("LastMoveDate"),
                ["OnHand"] = RptWinUtil.Dec(r.GetValueOrDefault("OnHand")),
                ["Note"] = note,
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
