using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ShouTech.App.Reporting;
using ShouTech.App.Services;

namespace ShouTech.App.Views.Reports;

/// <summary>
/// Quality inspection summary (Phase B 056, design section 5's
/// reports): completed inspections per material — or per supplier,
/// the supplier reject-rate report — with accepted / rejected
/// quantities and the reject rate. Data comes from
/// QualityRepository.GetSummaryAsync (the same rows QcInspectWin
/// completes), so the report and the gate always agree. Draft
/// inspections are excluded: a pending inspection is not yet a result.
/// </summary>
public sealed class QcSummaryWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Quality.InspectionSummary",
        TitleTextKey = "Reports.QualitySummary",
        Columns = new RptCol[]
        {
            new() { Key = "Group", HeaderTextKey = "Quality.Col.GroupName", Type = RptColType.Text, Width = 220 },
            new() { Key = "Inspections", HeaderTextKey = "Quality.Col.Inspections", Type = RptColType.WholeNumber, Width = 100 },
            new() { Key = "TotalQty", HeaderTextKey = "Quality.Col.TotalQty", Type = RptColType.FractionalNumber, Width = 110, Format = "N3" },
            new() { Key = "AcceptedQty", HeaderTextKey = "Quality.Col.AcceptedQty", Type = RptColType.FractionalNumber, Width = 110, Format = "N3" },
            new() { Key = "RejectedQty", HeaderTextKey = "Quality.Col.RejectedQty", Type = RptColType.FractionalNumber, Width = 110, Format = "N3" },
            new() { Key = "RejectRate", HeaderTextKey = "Quality.Col.RejectRate", Type = RptColType.FractionalNumber, Width = 100, Format = "N2" }
        },
        Filters = new RptFlt[]
        {
            new()
            {
                Key = "GroupBy", LabelTextKey = "Quality.GroupBy", Kind = RptFltKind.Choice,
                Choices = new[]
                {
                    new KeyValuePair<string, string>("Material", "Quality.GroupByMaterial"),
                    new KeyValuePair<string, string>("Supplier", "Quality.GroupBySupplier")
                },
                DefaultValue = "Material"
            }
        }
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var def = BuildDefinition();
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var service = AppHost.Quality;
        if (service is not null)
        {
            var bySupplier = string.Equals(filters.GetString("GroupBy"), "Supplier", StringComparison.OrdinalIgnoreCase);
            var summary = await service.GetSummaryAsync(CurrentCompanyCode, bySupplier, ct);
            foreach (var r in summary)
            {
                rows.Add(new Dictionary<string, object?>
                {
                    ["Group"] = string.IsNullOrWhiteSpace(r.GroupName) ? r.GroupCode : $"{r.GroupCode} — {r.GroupName}",
                    ["Inspections"] = r.Inspections,
                    ["TotalQty"] = r.TotalQty,
                    ["AcceptedQty"] = r.AcceptedQty,
                    ["RejectedQty"] = r.RejectedQty,
                    ["RejectRate"] = r.RejectRatePct
                });
            }
        }
        return new RptData
        {
            Columns = def.Columns,
            Rows = rows,
            Totals = RptWinUtil.SumTotals(rows, def.Columns, T("Reports.Totals"), "Group")
        };
    }
}
