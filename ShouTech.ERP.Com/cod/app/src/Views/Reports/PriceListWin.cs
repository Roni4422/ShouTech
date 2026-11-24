using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ShouTech.App.Reporting;
using ShouTech.App.Services;

namespace ShouTech.App.Views.Reports;

// ---- merged from cod/app/src/Views/Reports/MfgCostsWin.cs ----
/// <summary>Manufactured material costs per production order: actual vs standard cost and variance (mfg_orders, migration 034).</summary>
public sealed class MfgCostsWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Manufacturing.ManufacturedMaterialCosts",
        TitleTextKey = "Reports.ManufacturedMaterialCosts",
        Columns = new RptCol[]
        {
            new() { Key = "DocNo", HeaderTextKey = "Reports.Col.DocNo", Type = RptColType.Text, Width = 120 },
            new() { Key = "DocDate", HeaderTextKey = "Reports.Col.Date", Type = RptColType.Date, Width = 100 },
            new() { Key = "ItemCode", HeaderTextKey = "Reports.Col.ItemCode", Type = RptColType.Text, Width = 130 },
            new() { Key = "Qty", HeaderTextKey = "Reports.Col.Qty", Type = RptColType.FractionalNumber, Width = 90, Format = "N3" },
            new() { Key = "UnitCost", HeaderTextKey = "Reports.Col.UnitCost", Type = RptColType.Money, Width = 110, Format = "N4" },
            new() { Key = "StandardUnitCost", HeaderTextKey = "Reports.Col.StandardCost", Type = RptColType.Money, Width = 110, Format = "N4" },
            new() { Key = "TotalCost", HeaderTextKey = "Reports.Col.TotalCost", Type = RptColType.Money, Width = 130 },
            new() { Key = "StandardTotal", HeaderTextKey = "Reports.Col.StandardTotal", Type = RptColType.Money, Width = 130 },
            new() { Key = "Variance", HeaderTextKey = "Reports.Col.Variance", Type = RptColType.Money, Width = 120 },
            new() { Key = "Status", HeaderTextKey = "Reports.Col.Status", Type = RptColType.Text, Width = 90 }
        },
        Filters = Array.Empty<RptFlt>()
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var raw = await RptQuery.MfgOrderCostsAsync(ct);
        var def = BuildDefinition();
        var rows = RptWinUtil.Map(raw, def.Columns);
        return new RptData
        {
            Columns = def.Columns,
            Rows = rows,
            Totals = RptWinUtil.SumTotals(rows, def.Columns, T("Reports.Totals"), "ItemCode", "UnitCost", "StandardUnitCost")
        };
    }
}

// ---- merged from cod/app/src/Views/Reports/MfgFeasibilityWin.cs ----
/// <summary>
/// Manufacturing feasibility for Draft production orders: required raw /
/// component quantities per order line against current stock on hand;
/// shortage and feasibility are computed from those real quantities.
/// </summary>
public sealed class MfgFeasibilityWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Manufacturing.ManufacturingFeasibility",
        TitleTextKey = "Reports.ManufacturingFeasibility",
        Columns = new RptCol[]
        {
            new() { Key = "DocNo", HeaderTextKey = "Reports.Col.DocNo", Type = RptColType.Text, Width = 120 },
            new() { Key = "ItemCode", HeaderTextKey = "Reports.Col.ItemCode", Type = RptColType.Text, Width = 140 },
            new() { Key = "LineItemCode", HeaderTextKey = "Reports.Col.MaterialCode", Type = RptColType.Text, Width = 140 },
            new() { Key = "Required", HeaderTextKey = "Reports.Col.Required", Type = RptColType.FractionalNumber, Width = 110, Format = "N3" },
            new() { Key = "OnHand", HeaderTextKey = "Reports.Col.OnHand", Type = RptColType.FractionalNumber, Width = 110, Format = "N3" },
            new() { Key = "Shortage", HeaderTextKey = "Reports.Col.Shortage", Type = RptColType.FractionalNumber, Width = 110, Format = "N3" },
            new() { Key = "Status", HeaderTextKey = "Reports.Col.Status", Type = RptColType.Text, Width = 120 },
        },
        Filters = Array.Empty<RptFlt>()
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var raw = await RptQuery.MfgFeasibilityAsync(ct);
        var def = BuildDefinition();
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var r in raw)
        {
            var required = RptWinUtil.Dec(r.GetValueOrDefault("Required"));
            var onHand = RptWinUtil.Dec(r.GetValueOrDefault("OnHand"));
            var shortage = Math.Max(0m, required - onHand);
            rows.Add(new Dictionary<string, object?>
            {
                ["DocNo"] = r.GetValueOrDefault("DocNo"),
                ["ItemCode"] = r.GetValueOrDefault("ItemCode"),
                ["LineItemCode"] = r.GetValueOrDefault("LineItemCode"),
                ["Required"] = required,
                ["OnHand"] = onHand,
                ["Shortage"] = shortage,
                ["Status"] = shortage > 0 ? T("Reports.ShortageStatus") : T("Reports.Feasible"),
            });
        }
        return new RptData
        {
            Columns = def.Columns,
            Rows = rows,
            Totals = RptWinUtil.SumTotals(rows, def.Columns, T("Reports.Totals"), "ItemCode")
        };
    }
}

// ---- merged from cod/app/src/Views/Reports/MfgVarianceWin.cs ----
/// <summary>
/// Manufacturing cost variance: actual vs standard cost (migration 034).
/// One class serves four ribbon keys via the mode argument: Total (all
/// lines) / Raw (RAW lines) / Overhead (COST lines — the additional-cost
/// lines; the header overhead lump has no standard and no line variance)
/// / Finished (header-level: total cost vs qty × standard unit cost).
/// Variance = actual amount − qty × standard unit cost throughout.
/// Step 11 re-sourced the queries to the real 034 schema — see
/// RptQuery.MfgVarianceAsync (the previous SQL referenced columns that
/// do not exist and every mode crashed on open, sweep MFG-1).
/// </summary>
public sealed class MfgVarianceWin : RptBase, IWindowMode
{
    private readonly string _mode;

    /// <summary>The variance mode this window reports (Step 11 router
    /// identity: Total / Raw / Overhead / Finished — four different
    /// reports sharing one class).</summary>
    public string Mode => _mode;
    public string ModeKey => _mode;

    public MfgVarianceWin() : this("Total") { }
    public MfgVarianceWin(string mode) => _mode = mode is "Raw" or "Overhead" or "Finished" ? mode : "Total";

    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = _mode switch
        {
            "Raw" => "Manufacturing.RawMaterialCostVariance",
            "Overhead" => "Manufacturing.AdditionalCostVariance",
            "Finished" => "Manufacturing.ManufacturedMaterialCostVariance",
            _ => "Manufacturing.ManufacturingCostVariance"
        },
        TitleTextKey = _mode switch
        {
            "Raw" => "Reports.RawMaterialCostVariance",
            "Overhead" => "Reports.AdditionalCostVariance",
            "Finished" => "Reports.ManufacturedMaterialCostVariance",
            _ => "Reports.ManufacturingCostVariance"
        },
        Columns = new RptCol[]
        {
            new() { Key = "DocNo", HeaderTextKey = "Reports.Col.DocNo", Type = RptColType.Text, Width = 120 },
            new() { Key = "ItemCode", HeaderTextKey = "Reports.Col.ItemCode", Type = RptColType.Text, Width = 140 },
            new() { Key = "LineKind", HeaderTextKey = "Reports.Col.LineKind", Type = RptColType.Text, Width = 120 },
            new() { Key = "Qty", HeaderTextKey = "Reports.Col.Qty", Type = RptColType.FractionalNumber, Width = 100, Format = "N3" },
            new() { Key = "Amount", HeaderTextKey = "Reports.Col.Amount", Type = RptColType.Money, Width = 130, Format = "N2" },
            new() { Key = "StandardAmount", HeaderTextKey = "Reports.Col.StandardTotal", Type = RptColType.Money, Width = 130, Format = "N2" },
            new() { Key = "Variance", HeaderTextKey = "Reports.Col.Variance", Type = RptColType.Money, Width = 130, Format = "N2" },
        },
        Filters = Array.Empty<RptFlt>()
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var raw = await RptQuery.MfgVarianceAsync(_mode, ct);
        var def = BuildDefinition();
        var rows = RptWinUtil.Map(raw, def.Columns);
        return new RptData
        {
            Columns = def.Columns,
            Rows = rows,
            Totals = RptWinUtil.SumTotals(rows, def.Columns, T("Reports.Totals"), "ItemCode")
        };
    }
}

// ---- merged from cod/app/src/Views/Reports/PosCashRptWin.cs ----
/// <summary>POS sales documents (doc_kind POS) by date and warehouse: count, totals, discounts, average ticket.</summary>
public sealed class PosCashRptWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "POS.POSCashReport",
        TitleTextKey = "Reports.PosCashReport",
        Columns = new RptCol[]
        {
            new() { Key = "DocDate", HeaderTextKey = "Reports.Col.Date", Type = RptColType.Date, Width = 100 },
            new() { Key = "Warehouse", HeaderTextKey = "Reports.Col.Warehouse", Type = RptColType.Text, Width = 160 },
            new() { Key = "DocCount", HeaderTextKey = "Reports.Col.DocCount", Type = RptColType.WholeNumber, Width = 90 },
            new() { Key = "Subtotal", HeaderTextKey = "Reports.Col.Subtotal", Type = RptColType.Money, Width = 120 },
            new() { Key = "Discount", HeaderTextKey = "Reports.Col.Discount", Type = RptColType.Money, Width = 110 },
            new() { Key = "Tax", HeaderTextKey = "Reports.Col.Tax", Type = RptColType.Money, Width = 110 },
            new() { Key = "Total", HeaderTextKey = "Reports.Col.Total", Type = RptColType.Money, Width = 130 },
            new() { Key = "AvgTicket", HeaderTextKey = "Reports.Col.AvgTicket", Type = RptColType.Money, Width = 110 }
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
        var raw = await RptQuery.PosCashReportAsync(from, to, ct);
        var def = BuildDefinition();
        var rows = RptWinUtil.Map(raw, def.Columns);
        return new RptData
        {
            Columns = def.Columns,
            Rows = rows,
            Totals = RptWinUtil.SumTotals(rows, def.Columns, T("Reports.Totals"), "Warehouse", "AvgTicket")
        };
    }
}

// ---- merged from cod/app/src/Views/Reports/PosPerfRptWin.cs ----
/// <summary>
/// POS performance by terminal and cashier (sales cycle W9,
/// migration 104): for a date range — optionally narrowed to one
/// terminal and/or one cashier — each (terminal, cashier) pair's
/// sales count and gross, returns, discounts given, drawer paid-outs
/// and average basket. Data comes from PosRepository's session
/// aggregation (documents carry their session since 066; paid-outs
/// are session rows since 104) projected through
/// RptQuery.PosPerformanceAsync — never a company-wide guess.
/// Gated on POS.REPORTS.VIEW by the ribbon route like the other POS
/// reports.
/// </summary>
public sealed class PosPerfRptWin : RptBase
{
    protected override string? ScheduleTintName => "Peach";

    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "POS.Performance",
        TitleTextKey = "POS.PerformanceTitle",
        Columns = new RptCol[]
        {
            new() { Key = "Terminal", HeaderTextKey = "POS.Terminal", Type = RptColType.Text, Width = 110 },
            new() { Key = "Cashier", HeaderTextKey = "POS.Cashier", Type = RptColType.Text, Width = 150 },
            new() { Key = "SalesCount", HeaderTextKey = "POS.PerfCol.SalesCount", Type = RptColType.WholeNumber, Width = 90 },
            new() { Key = "GrossSales", HeaderTextKey = "POS.PerfCol.GrossSales", Type = RptColType.Money, Width = 120 },
            new() { Key = "ReturnsCount", HeaderTextKey = "POS.PerfCol.ReturnsCount", Type = RptColType.WholeNumber, Width = 90 },
            new() { Key = "ReturnsTotal", HeaderTextKey = "POS.PerfCol.ReturnsTotal", Type = RptColType.Money, Width = 120 },
            new() { Key = "Discounts", HeaderTextKey = "POS.PerfCol.Discounts", Type = RptColType.Money, Width = 110 },
            new() { Key = "PaidOuts", HeaderTextKey = "POS.PerfCol.PaidOuts", Type = RptColType.Money, Width = 110 },
            new() { Key = "NetSales", HeaderTextKey = "POS.PerfCol.NetSales", Type = RptColType.Money, Width = 120 },
            new() { Key = "AvgBasket", HeaderTextKey = "POS.PerfCol.AvgBasket", Type = RptColType.Money, Width = 110 }
        },
        Filters = new RptFlt[]
        {
            new() { Key = "From", LabelTextKey = "Reports.Filter.DateFrom", Kind = RptFltKind.DateFrom, DefaultValue = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1) },
            new() { Key = "To", LabelTextKey = "Reports.Filter.DateTo", Kind = RptFltKind.DateTo, DefaultValue = DateTime.Today },
            new() { Key = "Terminal", LabelTextKey = "POS.Terminal", Kind = RptFltKind.Text },
            new() { Key = "Cashier", LabelTextKey = "POS.Cashier", Kind = RptFltKind.Text }
        }
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var from = filters.GetDateOr("From", new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1));
        var to = filters.GetDateOr("To", DateTime.Today);
        var raw = await RptQuery.PosPerformanceAsync(from, to,
            filters.GetString("Terminal"), filters.GetString("Cashier"), ct);
        var def = BuildDefinition();
        var rows = RptWinUtil.Map(raw, def.Columns);
        return new RptData
        {
            Columns = def.Columns,
            Rows = rows,
            Totals = RptWinUtil.SumTotals(rows, def.Columns, T("Reports.Totals"), "Cashier", "AvgBasket")
        };
    }
}

// ---- merged from cod/app/src/Views/Reports/PosZReportWin.cs ----
/// <summary>
/// POS session Z report (Build POS 066): one session's real figures —
/// sales / returns counts and totals, tenders by type, refunds, tax,
/// discounts, and the drawer expectation against the counted close
/// (variance = counted − expected). Data comes from PosRepository's
/// session aggregation over pos_sale_tenders + the session's own
/// documents — never a company-wide day guess. The filter is the
/// session number; blank = the most recent session.
/// </summary>
public sealed class PosZReportWin : RptBase
{
    protected override string? ScheduleTintName => "Peach";

    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "POS.ZReport",
        TitleTextKey = "POS.ZReportTitle",
        Columns = new RptCol[]
        {
            new() { Key = "Item", HeaderTextKey = "POS.ZCol.Item", Type = RptColType.Text, Width = 260 },
            new() { Key = "Count", HeaderTextKey = "POS.ZCol.Count", Type = RptColType.WholeNumber, Width = 100 },
            new() { Key = "Amount", HeaderTextKey = "POS.ZCol.Amount", Type = RptColType.Money, Width = 150 }
        },
        Filters = new RptFlt[]
        {
            new() { Key = "Session", LabelTextKey = "POS.SessionNo", Kind = RptFltKind.Text }
        }
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var def = BuildDefinition();
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var service = AppHost.Pos;
        if (service is not null)
        {
            var wanted = filters.GetString("Session");
            var sessions = await service.GetSessionsAsync(CurrentCompanyCode, ct);
            var session = string.IsNullOrWhiteSpace(wanted)
                ? (sessions.Count > 0 ? sessions[0] : null)
                : sessions.FirstOrDefault(s => string.Equals(s.SessionNo, wanted.Trim(), StringComparison.OrdinalIgnoreCase));
            if (session is not null)
            {
                var z = await service.GetZReportAsync(CurrentCompanyCode, session.Id, ct);
                void Row(string key, int? count, decimal amount) => rows.Add(new Dictionary<string, object?>
                {
                    ["Item"] = T(key), ["Count"] = count, ["Amount"] = amount
                });
                Row("POS.ZSales", z.SalesCount, z.SalesTotal);
                Row("POS.ZReturns", z.ReturnsCount, -z.ReturnsTotal);
                Row("POS.ZNetSales", z.SalesCount - z.ReturnsCount, z.NetSales);
                Row("POS.ZCash", null, z.CashTendered);
                Row("POS.ZCard", null, z.CardTendered);
                Row("POS.ZOnAccount", null, z.OnAccountTendered);
                Row("POS.ZCashRefunded", null, -z.CashRefunded);
                Row("POS.ZCardRefunded", null, -z.CardRefunded);
                Row("POS.ZPaidOuts", z.PaidOutsCount, -z.PaidOutsTotal);
                Row("POS.ZTax", null, z.TaxTotal);
                Row("POS.ZDiscount", null, z.DiscountTotal);
                Row("POS.ZOpening", null, z.OpeningCash);
                Row("POS.ZExpected", null, z.ExpectedCash);
                if (z.CountedCash is decimal counted) Row("POS.ZCounted", null, counted);
                if (z.Variance is decimal variance) Row("POS.ZVariance", null, variance);
            }
        }
        return new RptData
        {
            Columns = def.Columns,
            Rows = rows,
            Totals = null
        };
    }
}

/// <summary>
/// Phase 7B - "Price list" (Inventory > Movements).
/// Lists every material with its full price ladder (retail, wholesale,
/// special, minimum) and price currency, so the sales price catalogue
/// can be reviewed and printed per group or material.
/// </summary>
public sealed class PriceListWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Inv.PriceList",
        TitleTextKey = "Inventory.PriceList",
        Columns = new RptCol[]
        {
            new() { Key = "Code", HeaderTextKey = "MatCard.Code", Type = RptColType.Text, Width = 110 },
            new() { Key = "Name", HeaderTextKey = "MatCard.Name", Type = RptColType.Text, Width = 250 },
            new() { Key = "Unit", HeaderTextKey = "Reports.Col.Unit", Type = RptColType.Text, Width = 80 },
            new() { Key = "Retail", HeaderTextKey = "MatCard.PriceRetail", Type = RptColType.Money, Width = 110, Format = "N2" },
            new() { Key = "Wholesale", HeaderTextKey = "MatCard.PriceWholesale", Type = RptColType.Money, Width = 110, Format = "N2" },
            new() { Key = "Special", HeaderTextKey = "MatCard.PriceSpecial", Type = RptColType.Money, Width = 110, Format = "N2" },
            new() { Key = "Min", HeaderTextKey = "MatCard.PriceMin", Type = RptColType.Money, Width = 110, Format = "N2" },
            new() { Key = "Currency", HeaderTextKey = "Reports.Col.Currency", Type = RptColType.Text, Width = 80 },
        },
        Filters = new RptFlt[]
        {
            new() { Key = "Material", LabelTextKey = "Inventory.MaterialFilter", Kind = RptFltKind.Material },
            new() { Key = "Group", LabelTextKey = "Inventory.MaterialGroupFilter", Kind = RptFltKind.MaterialGroup },
        }
    };

    protected override IReadOnlyList<KeyValuePair<string, string>> LoadMaterialGroupChoices()
    {
        var choices = new List<KeyValuePair<string, string>>
        {
            new("", T("Reports.Filter.All"))
        };
        choices.AddRange(MatRepo.LoadGroups()
            .Where(group => !group.IsDeleted)
            .Select(group => new KeyValuePair<string, string>(
                group.Code,
                string.Equals(App.CurrentLanguageCode, "ar", StringComparison.OrdinalIgnoreCase)
                    ? group.Name
                    : string.IsNullOrWhiteSpace(group.NameEn) ? group.Name : group.NameEn)));
        return choices;
    }

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var search = (filters.GetString("Material") ?? string.Empty).Trim();
        var group = filters.GetString("Group") ?? string.Empty;

        var isArabic = string.Equals(
            App.CurrentLanguageCode, "ar", StringComparison.OrdinalIgnoreCase);

        IEnumerable<MatRepo.MatRec> materials = await Task.Run(() => MatRepo.LoadAll(), ct);

        if (search.Length > 0)
        {
            materials = materials.Where(m =>
                m.Code.Contains(search, StringComparison.OrdinalIgnoreCase)
             || m.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
             || (m.NameEn ?? string.Empty).Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        if (group.Length > 0)
            materials = materials.Where(m => string.Equals(m.GroupCode, group, StringComparison.Ordinal));

        var rows = materials
            .OrderBy(m => m.Code)
            .Select(m => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["Code"] = m.Code,
                ["Name"] = isArabic || string.IsNullOrWhiteSpace(m.NameEn) ? m.Name : m.NameEn,
                ["Unit"] = m.DefaultUnit ?? m.Unit1 ?? string.Empty,
                ["Retail"] = m.PriceRetail,
                ["Wholesale"] = m.PriceWholesale,
                ["Special"] = m.PriceSpecial,
                ["Min"] = m.PriceMin,
                ["Currency"] = m.PriceCurrency ?? string.Empty,
            })
            .ToList();

        return new RptData
        {
            Columns = BuildDefinition().Columns,
            Rows = rows,
            Totals = new Dictionary<string, object?>
            {
                ["Name"] = T("Reports.Totals"),
                ["Retail"] = rows.Sum(x => ToDec(x["Retail"])),
            }
        };
    }

    private static decimal ToDec(object? v)
        => v is null ? 0m : Convert.ToDecimal(v, CultureInfo.InvariantCulture);
}

// ---- merged from cod/app/src/Views/Reports/QcSummaryWin.cs ----
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

// ---- merged from cod/app/src/Views/Reports/TargetsRptWin.cs ----
/// <summary>
/// Sales targets vs actuals per customer (targets, migration 036). When no
/// target rows exist for the period — including while migration 036 has not
/// landed yet — the report falls back to real actual sales per party with a
/// zero target, so it is never a placeholder.
/// </summary>
public sealed class TargetsRptWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Customers.TargetsReport",
        TitleTextKey = "Reports.TargetsReport",
        Columns = new RptCol[]
        {
            new() { Key = "PartyCode", HeaderTextKey = "Reports.Col.PartyCode", Type = RptColType.Text, Width = 110 },
            new() { Key = "PartyName", HeaderTextKey = "Reports.Col.PartyName", Type = RptColType.Text, Width = 240 },
            new() { Key = "Target", HeaderTextKey = "Reports.Col.Target", Type = RptColType.Money, Width = 140, Format = "N2" },
            new() { Key = "Actual", HeaderTextKey = "Reports.Col.Actual", Type = RptColType.Money, Width = 140, Format = "N2" },
            new() { Key = "Variance", HeaderTextKey = "Reports.Col.Variance", Type = RptColType.Money, Width = 130, Format = "N2" },
            new() { Key = "AchievementPct", HeaderTextKey = "Reports.Col.AchievementPct", Type = RptColType.Percent, Width = 110 },
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
        var raw = await RptQuery.TargetsAsync(from, to, ct);
        var def = BuildDefinition();
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        if (raw.Count > 0)
        {
            foreach (var r in raw)
            {
                var target = RptWinUtil.Dec(r.GetValueOrDefault("Target"));
                var actual = RptWinUtil.Dec(r.GetValueOrDefault("Actual"));
                rows.Add(Row(r, target, actual));
            }
        }
        else
        {
            var actuals = await RptQuery.SalesByPartyAsync(from, to, ct);
            foreach (var r in actuals)
                rows.Add(Row(r, 0m, RptWinUtil.Dec(r.GetValueOrDefault("Actual"))));
        }
        return new RptData
        {
            Columns = def.Columns,
            Rows = rows,
            Totals = RptWinUtil.SumTotals(rows, def.Columns, T("Reports.Totals"), "PartyName")
        };

        static Dictionary<string, object?> Row(IReadOnlyDictionary<string, object?> r, decimal target, decimal actual)
            => new()
            {
                ["PartyCode"] = r.GetValueOrDefault("PartyCode"),
                ["PartyName"] = r.GetValueOrDefault("PartyName"),
                ["Target"] = target,
                ["Actual"] = actual,
                ["Variance"] = actual - target,
                ["AchievementPct"] = target == 0 ? 0m : actual / target,
            };
    }
}
