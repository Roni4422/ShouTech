using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ShouTech.App.Reporting;
using ShouTech.App.Services;

namespace ShouTech.App.Views.Reports;

// ---- merged from cod/app/src/Views/Reports/AvgRateWin.cs ----
/// <summary>Per currency and month: min/max/average and debit-weighted average exchange rate from journal entries.</summary>
public sealed class AvgRateWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Acc.AverageCurrencyRate",
        TitleTextKey = "Reports.AverageCurrencyRate",
        Columns = new RptCol[]
        {
            new() { Key = "Currency", HeaderTextKey = "Reports.Col.Currency", Type = RptColType.Text, Width = 90 },
            new() { Key = "Month", HeaderTextKey = "Reports.Col.Month", Type = RptColType.Text, Width = 90 },
            new() { Key = "Entries", HeaderTextKey = "Reports.Col.Count", Type = RptColType.WholeNumber, Width = 90 },
            new() { Key = "MinRate", HeaderTextKey = "Reports.Col.MinRate", Type = RptColType.FractionalNumber, Width = 100, Format = "N4" },
            new() { Key = "MaxRate", HeaderTextKey = "Reports.Col.MaxRate", Type = RptColType.FractionalNumber, Width = 100, Format = "N4" },
            new() { Key = "AvgRate", HeaderTextKey = "Reports.Col.AvgRate", Type = RptColType.FractionalNumber, Width = 100, Format = "N4" },
            new() { Key = "WeightedRate", HeaderTextKey = "Reports.Col.WeightedRate", Type = RptColType.FractionalNumber, Width = 110, Format = "N4" }
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
        var raw = await RptQuery.AverageCurrencyRateAsync(from, to, ct);
        var def = BuildDefinition();
        var rows = RptWinUtil.Map(raw, def.Columns);
        return new RptData
        {
            Columns = def.Columns,
            Rows = rows,
            Totals = RptWinUtil.SumTotals(rows, def.Columns, T("Reports.Totals"), "Currency", "MinRate", "MaxRate", "AvgRate", "WeightedRate")
        };
    }
}

// ---- merged from cod/app/src/Views/Reports/BatchBalWin.cs ----
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

// ---- merged from cod/app/src/Views/Reports/BranchMatWin.cs ----
/// <summary>Current stock balances grouped by branch (warehouse).</summary>
public sealed class BranchMatWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Inv.BranchMaterials",
        TitleTextKey = "Reports.BranchMaterials",
        Columns = new RptCol[]
        {
            new() { Key = "Branch", HeaderTextKey = "Reports.Col.Branch", Type = RptColType.Text, Width = 200 },
            new() { Key = "MaterialCode", HeaderTextKey = "Reports.Col.MaterialCode", Type = RptColType.Text, Width = 110 },
            new() { Key = "MaterialName", HeaderTextKey = "Reports.Col.MaterialName", Type = RptColType.Text, Width = 220 },
            new() { Key = "Qty", HeaderTextKey = "Reports.Col.Qty", Type = RptColType.FractionalNumber, Width = 100, Format = "N3" },
            new() { Key = "AvgCost", HeaderTextKey = "Reports.Col.AvgCost", Type = RptColType.Money, Width = 100, Format = "N4" },
            new() { Key = "Value", HeaderTextKey = "Reports.Col.Value", Type = RptColType.Money, Width = 120 }
        },
        Filters = Array.Empty<RptFlt>()
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var raw = await RptQuery.BranchMaterialsAsync(ct);
        var def = BuildDefinition();
        var rows = RptWinUtil.Map(raw, def.Columns);
        return new RptData
        {
            Columns = def.Columns,
            Rows = rows,
            Totals = RptWinUtil.SumTotals(rows, def.Columns, T("Reports.Totals"), "MaterialName", "AvgCost")
        };
    }
}

// ---- merged from cod/app/src/Views/Reports/DormantWin.cs ----
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

// ---- merged from cod/app/src/Views/Reports/ExpiredWin.cs ----
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

// ---- merged from cod/app/src/Views/Reports/MatCompareWin.cs ----
/// <summary>Two-period comparison of net movement qty and value per material.</summary>
public sealed class MatCompareWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Inv.MaterialComparison",
        TitleTextKey = "Reports.MaterialComparison",
        Columns = new RptCol[]
        {
            new() { Key = "MaterialCode", HeaderTextKey = "Reports.Col.MaterialCode", Type = RptColType.Text, Width = 110 },
            new() { Key = "MaterialName", HeaderTextKey = "Reports.Col.MaterialName", Type = RptColType.Text, Width = 220 },
            new() { Key = "Qty1", HeaderTextKey = "Reports.Col.QtyP1", Type = RptColType.FractionalNumber, Width = 90, Format = "N3" },
            new() { Key = "Value1", HeaderTextKey = "Reports.Col.ValueP1", Type = RptColType.Money, Width = 110 },
            new() { Key = "Qty2", HeaderTextKey = "Reports.Col.QtyP2", Type = RptColType.FractionalNumber, Width = 90, Format = "N3" },
            new() { Key = "Value2", HeaderTextKey = "Reports.Col.ValueP2", Type = RptColType.Money, Width = 110 },
            new() { Key = "QtyDiff", HeaderTextKey = "Reports.Col.QtyDiff", Type = RptColType.FractionalNumber, Width = 90, Format = "N3" },
            new() { Key = "ValueDiff", HeaderTextKey = "Reports.Col.ValueDiff", Type = RptColType.Money, Width = 110 }
        },
        Filters = new RptFlt[]
        {
            new() { Key = "From", LabelTextKey = "Reports.Filter.DateFrom", Kind = RptFltKind.DateFrom, DefaultValue = new DateTime(DateTime.Today.Year, 1, 1) },
            new() { Key = "To", LabelTextKey = "Reports.Filter.DateTo", Kind = RptFltKind.DateTo, DefaultValue = DateTime.Today },
            new() { Key = "From2", LabelTextKey = "Reports.Filter.P2From", Kind = RptFltKind.DateFrom, DefaultValue = new DateTime(DateTime.Today.Year - 1, 1, 1) },
            new() { Key = "To2", LabelTextKey = "Reports.Filter.P2To", Kind = RptFltKind.DateTo, DefaultValue = new DateTime(DateTime.Today.Year - 1, 12, 31) }
        }
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var from = filters.GetDateOr("From", new DateTime(DateTime.Today.Year, 1, 1));
        var to = filters.GetDateOr("To", DateTime.Today);
        var from2 = filters.GetDateOr("From2", new DateTime(DateTime.Today.Year - 1, 1, 1));
        var to2 = filters.GetDateOr("To2", new DateTime(DateTime.Today.Year - 1, 12, 31));
        var raw = await RptQuery.MaterialComparisonAsync(from, to, from2, to2, ct);
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

// ---- merged from cod/app/src/Views/Reports/MatListWin.cs ----
/// <summary>
/// Phase 7B - "Material list" / "Search materials" (Inventory > Material reports).
/// Lists every material of the current company with its group, unit,
/// barcode and retail price. Serves as the searchable material catalogue.
/// </summary>
public sealed class MatListWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Inv.MaterialList",
        TitleTextKey = "Inventory.MaterialList",
        Columns = new RptCol[]
        {
            new() { Key = "Code", HeaderTextKey = "MatCard.Code", Type = RptColType.Text, Width = 110 },
            new() { Key = "Name", HeaderTextKey = "MatCard.Name", Type = RptColType.Text, Width = 260 },
            new() { Key = "Group", HeaderTextKey = "MatCard.Group", Type = RptColType.Text, Width = 110 },
            new() { Key = "Unit", HeaderTextKey = "Reports.Col.Unit", Type = RptColType.Text, Width = 80 },
            new() { Key = "Barcode", HeaderTextKey = "MatCard.Barcode", Type = RptColType.Text, Width = 150 },
            new() { Key = "Price", HeaderTextKey = "MatCard.PriceRetail", Type = RptColType.Money, Width = 110, Format = "N2" },
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

        // Both lookups run off the UI thread.
        var groupNames = await Task.Run(() => MatRepo.LoadGroups()
            .Where(g => !g.IsDeleted)
            .ToDictionary(
                g => g.Code,
                g => isArabic || string.IsNullOrWhiteSpace(g.NameEn) ? g.Name : g.NameEn), ct);

        IEnumerable<MatRepo.MatRec> materials = await Task.Run(() => MatRepo.LoadAll(), ct);

        if (search.Length > 0)
        {
            materials = materials.Where(m =>
                m.Code.Contains(search, StringComparison.OrdinalIgnoreCase)
             || m.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
             || (m.NameEn ?? string.Empty).Contains(search, StringComparison.OrdinalIgnoreCase)
             || (m.SearchName ?? string.Empty).Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        if (group.Length > 0)
            materials = materials.Where(m => string.Equals(m.GroupCode, group, StringComparison.Ordinal));

        var rows = materials
            .OrderBy(m => m.Code)
            .Select(m => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["Code"] = m.Code,
                ["Name"] = isArabic || string.IsNullOrWhiteSpace(m.NameEn) ? m.Name : m.NameEn,
                ["Group"] = string.IsNullOrEmpty(m.GroupCode)
                    ? string.Empty
                    : groupNames.GetValueOrDefault(m.GroupCode, m.GroupCode),
                ["Unit"] = m.DefaultUnit ?? m.Unit1 ?? string.Empty,
                ["Barcode"] = m.Unit1Barcode ?? string.Empty,
                ["Price"] = m.PriceRetail,
            })
            .ToList();

        return new RptData
        {
            Columns = BuildDefinition().Columns,
            Rows = rows,
            Totals = new Dictionary<string, object?>
            {
                ["Name"] = T("Reports.Totals"),
                ["Price"] = rows.Sum(x => ToDec(x["Price"])),
            }
        };
    }

    private static decimal ToDec(object? v)
        => v is null ? 0m : Convert.ToDecimal(v, CultureInfo.InvariantCulture);
}

// ---- merged from cod/app/src/Views/Reports/MatMoveChartWin.cs ----
/// <summary>Per-material monthly movement schedule (qty in/out) for a chosen year.</summary>
public sealed class MatMoveChartWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Inv.MaterialMovementChart",
        TitleTextKey = "Reports.MaterialMovementChart",
        Columns = new RptCol[]
        {
            new() { Key = "MaterialCode", HeaderTextKey = "Reports.Col.MaterialCode", Type = RptColType.Text, Width = 110 },
            new() { Key = "MaterialName", HeaderTextKey = "Reports.Col.MaterialName", Type = RptColType.Text, Width = 220 },
            new() { Key = "Month", HeaderTextKey = "Reports.Col.Month", Type = RptColType.Text, Width = 90 },
            new() { Key = "QtyIn", HeaderTextKey = "Reports.Col.QtyIn", Type = RptColType.FractionalNumber, Width = 90, Format = "N3" },
            new() { Key = "QtyOut", HeaderTextKey = "Reports.Col.QtyOut", Type = RptColType.FractionalNumber, Width = 90, Format = "N3" },
            new() { Key = "NetQty", HeaderTextKey = "Reports.Col.NetQty", Type = RptColType.FractionalNumber, Width = 100, Format = "N3" },
            new() { Key = "MoveCount", HeaderTextKey = "Reports.Col.MoveCount", Type = RptColType.WholeNumber, Width = 100 }
        },
        Filters = new RptFlt[]
        {
            new() { Key = "Year", LabelTextKey = "Reports.Filter.Year", Kind = RptFltKind.Text, DefaultValue = DateTime.Today.Year.ToString(CultureInfo.InvariantCulture) }
        }
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var year = RptWinUtil.ParseInt(filters.GetString("Year"), DateTime.Today.Year);
        var raw = await RptQuery.MaterialMovementChartAsync(year, ct);
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

/// <summary>Material movement report.</summary>
public sealed class MatMoveWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Inv.MaterialMoves",
        TitleTextKey = "Reports.MaterialMoves",
        Columns = new RptCol[]
        {
            new() { Key = "MoveDate", HeaderTextKey = "Reports.Col.Date", Type = RptColType.Date, Width = 100 },
            new() { Key = "MoveType", HeaderTextKey = "Reports.Col.MoveType", Type = RptColType.Text, Width = 100 },
            new() { Key = "DocNo", HeaderTextKey = "Reports.Col.DocNo", Type = RptColType.Text, Width = 120 },
            new() { Key = "Warehouse", HeaderTextKey = "Reports.Col.Warehouse", Type = RptColType.Text, Width = 100 },
            new() { Key = "MaterialCode", HeaderTextKey = "Reports.Col.MaterialCode", Type = RptColType.Text, Width = 110 },
            new() { Key = "MaterialName", HeaderTextKey = "Reports.Col.MaterialName", Type = RptColType.Text, Width = 200 },
            new() { Key = "QtyIn", HeaderTextKey = "Reports.Col.QtyIn", Type = RptColType.FractionalNumber, Width = 90, Format = "N3" },
            new() { Key = "QtyOut", HeaderTextKey = "Reports.Col.QtyOut", Type = RptColType.FractionalNumber, Width = 90, Format = "N3" },
            new() { Key = "UnitCost", HeaderTextKey = "Reports.Col.AvgCost", Type = RptColType.Money, Width = 90, Format = "N4" },
            new() { Key = "MoveValue", HeaderTextKey = "Reports.Col.Value", Type = RptColType.Money, Width = 110, Format = "N2" },
            new() { Key = "Balance", HeaderTextKey = "Reports.Col.Balance", Type = RptColType.FractionalNumber, Width = 110, Format = "N3" },
            new() { Key = "Note", HeaderTextKey = "Reports.Col.Note", Type = RptColType.Text, Width = 240 },
        },
        Filters = new RptFlt[]
        {
            new() { Key = "Material", LabelTextKey = "Inventory.MaterialFilter", Kind = RptFltKind.Material },
            new() { Key = "Warehouse", LabelTextKey = "Reports.Filter.Warehouse", Kind = RptFltKind.Warehouse },
            new()
            {
                Key = "Type",
                LabelTextKey = "Reports.Col.MoveType",
                Kind = RptFltKind.Choice,
                Choices = new[]
                {
                    new KeyValuePair<string, string>("", "Reports.Filter.All"),
                    new KeyValuePair<string, string>("PUR", "Inventory.MoveType.Purchase"),
                    new KeyValuePair<string, string>("SAL", "Inventory.MoveType.Sale"),
                    new KeyValuePair<string, string>("PRT", "Inventory.MoveType.PurchaseReturn"),
                    new KeyValuePair<string, string>("SRT", "Inventory.MoveType.SalesReturn"),
                    new KeyValuePair<string, string>("POS", "Inventory.MoveType.PointOfSale"),
                    new KeyValuePair<string, string>("ADJ", "Inventory.MoveType.Adjustment"),
                    new KeyValuePair<string, string>("COUNT_CLOSE", "Inventory.MoveType.StockCount"),
                    new KeyValuePair<string, string>("TRANSFER_IN", "Inventory.MoveType.TransferIn"),
                    new KeyValuePair<string, string>("TRANSFER_OUT", "Inventory.MoveType.TransferOut")
                }
            },
            new() { Key = "Document", LabelTextKey = "Reports.Col.DocNo", Kind = RptFltKind.Text },
            new() { Key = "From", LabelTextKey = "Reports.Filter.DateFrom", Kind = RptFltKind.DateFrom, DefaultValue = new DateTime(DateTime.Today.Year, 1, 1) },
            new() { Key = "To", LabelTextKey = "Reports.Filter.DateTo", Kind = RptFltKind.DateTo, DefaultValue = DateTime.Today },
        }
    };

    protected override IReadOnlyList<KeyValuePair<string, string>> LoadWarehouseChoices()
        => RptQuery.WarehouseChoices();

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var mat = filters.GetString("Material");
        var warehouse = filters.GetString("Warehouse");
        var type = filters.GetString("Type");
        var document = filters.GetString("Document");
        var from = filters.GetDateOr("From", new DateTime(DateTime.Today.Year, 1, 1));
        var to = filters.GetDateOr("To", DateTime.Today);
        var raw = await RptQuery.MaterialMovesAsync(mat, warehouse, type, document, from, to, ct);
        var isArabic = string.Equals(App.CurrentLanguageCode, "ar", StringComparison.OrdinalIgnoreCase);
        var rows = raw.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["MoveDate"] = r.GetValueOrDefault("MoveDate"),
            ["MoveType"] = FormatMoveType(r.GetValueOrDefault("MoveType")?.ToString()),
            ["DocNo"] = r.GetValueOrDefault("DocNo"),
            ["Warehouse"] = FormatWarehouse(
                r.GetValueOrDefault("Warehouse")?.ToString(),
                isArabic
                    ? r.GetValueOrDefault("WarehouseName")?.ToString()
                    : r.GetValueOrDefault("WarehouseNameEn")?.ToString()),
            ["MaterialCode"] = r.GetValueOrDefault("MaterialCode"),
            ["MaterialName"] = isArabic
                ? r.GetValueOrDefault("MaterialName")
                : string.IsNullOrWhiteSpace(r.GetValueOrDefault("MaterialNameEn")?.ToString())
                    ? r.GetValueOrDefault("MaterialName")
                    : r.GetValueOrDefault("MaterialNameEn"),
            ["QtyIn"] = ToDec(r.GetValueOrDefault("QtyIn")),
            ["QtyOut"] = ToDec(r.GetValueOrDefault("QtyOut")),
            ["UnitCost"] = ToDec(r.GetValueOrDefault("UnitCost")),
            ["MoveValue"] = ToDec(r.GetValueOrDefault("MoveValue")),
            ["Balance"] = ToDec(r.GetValueOrDefault("Balance")),
            ["Note"] = r.GetValueOrDefault("Note"),
        }).ToList();

        return new RptData
        {
            Columns = BuildDefinition().Columns,
            Rows = rows,
            Totals = new Dictionary<string, object?>
            {
                ["MaterialName"] = T("Reports.Totals"),
                ["QtyIn"] = rows.Sum(x => ToDec(x["QtyIn"])),
                ["QtyOut"] = rows.Sum(x => ToDec(x["QtyOut"])),
                ["MoveValue"] = rows.Sum(x => ToDec(x["MoveValue"])),
            }
        };
    }

    private static decimal ToDec(object? v)
        => v is null ? 0m : Convert.ToDecimal(v, CultureInfo.InvariantCulture);

    private static string FormatMoveType(string? code)
    {
        var key = code switch
        {
            "PUR" => "Inventory.MoveType.Purchase",
            "SAL" => "Inventory.MoveType.Sale",
            "PRT" => "Inventory.MoveType.PurchaseReturn",
            "SRT" => "Inventory.MoveType.SalesReturn",
            "POS" => "Inventory.MoveType.PointOfSale",
            "ADJ" => "Inventory.MoveType.Adjustment",
            "COUNT_CLOSE" => "Inventory.MoveType.StockCount",
            "TRANSFER_IN" => "Inventory.MoveType.TransferIn",
            "TRANSFER_OUT" => "Inventory.MoveType.TransferOut",
            _ => null
        };

        return key == null ? code ?? string.Empty : T(key);
    }

    private static string FormatWarehouse(string? code, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return code ?? string.Empty;
        return string.IsNullOrWhiteSpace(code) ? name : $"{code} - {name}";
    }
}

/// <summary>
/// Material GROUP movement report (R3 — the dedicated window behind
/// Inventory.MaterialGroupMovementReport, replacing the router's
/// approximation to the per-material movement report): period stock
/// movement aggregated per material group, with an Ungrouped bucket
/// for materials in no group. A material in several groups
/// contributes to each (membership semantics, see
/// RptQuery.GroupMovesAsync).
/// </summary>
public sealed class GroupMoveWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Inv.GroupMoves",
        TitleTextKey = "Reports.GroupMoves",
        Columns = new RptCol[]
        {
            new() { Key = "GroupCode", HeaderTextKey = "Reports.Col.GroupCode", Type = RptColType.Text, Width = 110 },
            new() { Key = "GroupName", HeaderTextKey = "Reports.Col.GroupName", Type = RptColType.Text, Width = 220 },
            new() { Key = "MaterialCount", HeaderTextKey = "Reports.Col.MaterialCount", Type = RptColType.WholeNumber, Width = 100 },
            new() { Key = "QtyIn", HeaderTextKey = "Reports.Col.QtyIn", Type = RptColType.FractionalNumber, Width = 100, Format = "N3" },
            new() { Key = "QtyOut", HeaderTextKey = "Reports.Col.QtyOut", Type = RptColType.FractionalNumber, Width = 100, Format = "N3" },
            new() { Key = "NetQty", HeaderTextKey = "Reports.Col.NetQty", Type = RptColType.FractionalNumber, Width = 100, Format = "N3" },
            new() { Key = "ValueIn", HeaderTextKey = "Reports.Col.ValueIn", Type = RptColType.Money, Width = 110, Format = "N2" },
            new() { Key = "ValueOut", HeaderTextKey = "Reports.Col.ValueOut", Type = RptColType.Money, Width = 110, Format = "N2" },
            new() { Key = "NetValue", HeaderTextKey = "Reports.Col.NetValue", Type = RptColType.Money, Width = 110, Format = "N2" },
        },
        Filters = new RptFlt[]
        {
            new() { Key = "Warehouse", LabelTextKey = "Reports.Filter.Warehouse", Kind = RptFltKind.Warehouse },
            new() { Key = "From", LabelTextKey = "Reports.Filter.DateFrom", Kind = RptFltKind.DateFrom, DefaultValue = new DateTime(DateTime.Today.Year, 1, 1) },
            new() { Key = "To", LabelTextKey = "Reports.Filter.DateTo", Kind = RptFltKind.DateTo, DefaultValue = DateTime.Today },
        }
    };

    protected override IReadOnlyList<KeyValuePair<string, string>> LoadWarehouseChoices()
        => RptQuery.WarehouseChoices();

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var warehouse = filters.GetString("Warehouse");
        var from = filters.GetDateOr("From", new DateTime(DateTime.Today.Year, 1, 1));
        var to = filters.GetDateOr("To", DateTime.Today);
        var raw = await RptQuery.GroupMovesAsync(warehouse, from, to, ct);
        var isArabic = string.Equals(App.CurrentLanguageCode, "ar", StringComparison.OrdinalIgnoreCase);
        var rows = raw.Select(r =>
        {
            var qtyIn = ToDec(r.GetValueOrDefault("QtyIn"));
            var qtyOut = ToDec(r.GetValueOrDefault("QtyOut"));
            var valueIn = ToDec(r.GetValueOrDefault("ValueIn"));
            var valueOut = ToDec(r.GetValueOrDefault("ValueOut"));
            var code = r.GetValueOrDefault("GroupCode")?.ToString() ?? string.Empty;
            return (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["GroupCode"] = code,
                ["GroupName"] = code.Length == 0
                    ? T("Reports.Ungrouped")
                    : isArabic
                        ? r.GetValueOrDefault("GroupName")
                        : string.IsNullOrWhiteSpace(r.GetValueOrDefault("GroupNameEn")?.ToString())
                            ? r.GetValueOrDefault("GroupName")
                            : r.GetValueOrDefault("GroupNameEn"),
                ["MaterialCount"] = ToDec(r.GetValueOrDefault("MaterialCount")),
                ["QtyIn"] = qtyIn,
                ["QtyOut"] = qtyOut,
                ["NetQty"] = qtyIn - qtyOut,
                ["ValueIn"] = valueIn,
                ["ValueOut"] = valueOut,
                ["NetValue"] = valueIn - valueOut,
            };
        }).ToList();

        return new RptData
        {
            Columns = BuildDefinition().Columns,
            Rows = rows,
            Totals = new Dictionary<string, object?>
            {
                ["GroupName"] = T("Reports.Totals"),
                ["QtyIn"] = rows.Sum(x => ToDec(x["QtyIn"])),
                ["QtyOut"] = rows.Sum(x => ToDec(x["QtyOut"])),
                ["NetQty"] = rows.Sum(x => ToDec(x["NetQty"])),
                ["ValueIn"] = rows.Sum(x => ToDec(x["ValueIn"])),
                ["ValueOut"] = rows.Sum(x => ToDec(x["ValueOut"])),
                ["NetValue"] = rows.Sum(x => ToDec(x["NetValue"])),
            }
        };
    }

    private static decimal ToDec(object? v)
        => v is null ? 0m : Convert.ToDecimal(v, CultureInfo.InvariantCulture);
}

// ---- merged from cod/app/src/Views/Reports/MatStatsWin.cs ----
/// <summary>Per-material statistics: qty sold/purchased, revenue, cost at average cost, and margin.</summary>
public sealed class MatStatsWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Inv.MaterialStatistics",
        TitleTextKey = "Reports.MaterialStatistics",
        Columns = new RptCol[]
        {
            new() { Key = "MaterialCode", HeaderTextKey = "Reports.Col.MaterialCode", Type = RptColType.Text, Width = 110 },
            new() { Key = "MaterialName", HeaderTextKey = "Reports.Col.MaterialName", Type = RptColType.Text, Width = 220 },
            new() { Key = "QtySold", HeaderTextKey = "Reports.Col.QtySold", Type = RptColType.FractionalNumber, Width = 90, Format = "N3" },
            new() { Key = "Revenue", HeaderTextKey = "Reports.Col.Revenue", Type = RptColType.Money, Width = 120 },
            new() { Key = "QtyPurchased", HeaderTextKey = "Reports.Col.QtyPurchased", Type = RptColType.FractionalNumber, Width = 100, Format = "N3" },
            new() { Key = "PurchaseAmount", HeaderTextKey = "Reports.Col.PurchaseAmount", Type = RptColType.Money, Width = 120 },
            new() { Key = "Cost", HeaderTextKey = "Reports.Col.Cost", Type = RptColType.Money, Width = 120 },
            new() { Key = "Margin", HeaderTextKey = "Reports.Col.Margin", Type = RptColType.Money, Width = 120 },
            new() { Key = "MarginPct", HeaderTextKey = "Reports.Col.MarginPct", Type = RptColType.Percent, Width = 90 }
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
        var raw = await RptQuery.MaterialStatisticsAsync(from, to, ct);
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

// ---- merged from cod/app/src/Views/Reports/MostActiveWin.cs ----
/// <summary>Most active materials by movement quantity/value in the period (top N).</summary>
public sealed class MostActiveWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Inv.MostActiveMaterials",
        TitleTextKey = "Reports.MostActiveMaterials",
        Columns = new RptCol[]
        {
            new() { Key = "MaterialCode", HeaderTextKey = "Reports.Col.MaterialCode", Type = RptColType.Text, Width = 110 },
            new() { Key = "MaterialName", HeaderTextKey = "Reports.Col.MaterialName", Type = RptColType.Text, Width = 220 },
            new() { Key = "MoveCount", HeaderTextKey = "Reports.Col.MoveCount", Type = RptColType.WholeNumber, Width = 100 },
            new() { Key = "QtyIn", HeaderTextKey = "Reports.Col.QtyIn", Type = RptColType.FractionalNumber, Width = 90, Format = "N3" },
            new() { Key = "QtyOut", HeaderTextKey = "Reports.Col.QtyOut", Type = RptColType.FractionalNumber, Width = 90, Format = "N3" },
            new() { Key = "TotalQty", HeaderTextKey = "Reports.Col.TotalQty", Type = RptColType.FractionalNumber, Width = 100, Format = "N3" },
            new() { Key = "Value", HeaderTextKey = "Reports.Col.Value", Type = RptColType.Money, Width = 120 }
        },
        Filters = new RptFlt[]
        {
            new() { Key = "From", LabelTextKey = "Reports.Filter.DateFrom", Kind = RptFltKind.DateFrom, DefaultValue = new DateTime(DateTime.Today.Year, 1, 1) },
            new() { Key = "To", LabelTextKey = "Reports.Filter.DateTo", Kind = RptFltKind.DateTo, DefaultValue = DateTime.Today },
            new() { Key = "Limit", LabelTextKey = "Reports.Filter.Limit", Kind = RptFltKind.Text, DefaultValue = "50" }
        }
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var from = filters.GetDateOr("From", new DateTime(DateTime.Today.Year, 1, 1));
        var to = filters.GetDateOr("To", DateTime.Today);
        var limit = RptWinUtil.ParseInt(filters.GetString("Limit"), 50);
        var raw = await RptQuery.MostActiveMaterialsAsync(limit, from, to, ct);
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

// ---- merged from cod/app/src/Views/Reports/NegativeStockWin.cs ----
/// <summary>Materials/warehouses whose movement balance is negative.</summary>
public sealed class NegativeStockWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Inv.NegativeOutput",
        TitleTextKey = "Reports.NegativeOutput",
        Columns = new RptCol[]
        {
            new() { Key = "Warehouse", HeaderTextKey = "Reports.Col.Warehouse", Type = RptColType.Text, Width = 110 },
            new() { Key = "MaterialCode", HeaderTextKey = "Reports.Col.MaterialCode", Type = RptColType.Text, Width = 110 },
            new() { Key = "MaterialName", HeaderTextKey = "Reports.Col.MaterialName", Type = RptColType.Text, Width = 220 },
            new() { Key = "Balance", HeaderTextKey = "Reports.Col.Balance", Type = RptColType.FractionalNumber, Width = 110, Format = "N3" },
            new() { Key = "AvgCost", HeaderTextKey = "Reports.Col.AvgCost", Type = RptColType.Money, Width = 100, Format = "N4" },
            new() { Key = "Value", HeaderTextKey = "Reports.Col.Value", Type = RptColType.Money, Width = 120 }
        },
        Filters = Array.Empty<RptFlt>()
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var raw = await RptQuery.NegativeStockAsync(ct);
        var def = BuildDefinition();
        var rows = RptWinUtil.Map(raw, def.Columns);
        return new RptData
        {
            Columns = def.Columns,
            Rows = rows,
            Totals = RptWinUtil.SumTotals(rows, def.Columns, T("Reports.Totals"), "MaterialName", "AvgCost")
        };
    }
}

// ---- merged from cod/app/src/Views/Reports/PackingListWin.cs ----
/// <summary>Packing list per posted sales/delivery document: qty with net/gross weights from material cards.</summary>
public sealed class PackingListWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Invoices.PackingList",
        TitleTextKey = "Reports.PackingList",
        Columns = new RptCol[]
        {
            new() { Key = "DocDate", HeaderTextKey = "Reports.Col.Date", Type = RptColType.Date, Width = 100 },
            new() { Key = "DocNo", HeaderTextKey = "Reports.Col.DocNo", Type = RptColType.Text, Width = 120 },
            new() { Key = "PartyName", HeaderTextKey = "Reports.Col.PartyName", Type = RptColType.Text, Width = 200 },
            new() { Key = "MaterialCode", HeaderTextKey = "Reports.Col.MaterialCode", Type = RptColType.Text, Width = 110 },
            new() { Key = "MaterialName", HeaderTextKey = "Reports.Col.MaterialName", Type = RptColType.Text, Width = 200 },
            new() { Key = "Qty", HeaderTextKey = "Reports.Col.Qty", Type = RptColType.FractionalNumber, Width = 90, Format = "N3" },
            new() { Key = "Unit", HeaderTextKey = "Reports.Col.Unit", Type = RptColType.Text, Width = 70 },
            new() { Key = "WeightNet", HeaderTextKey = "Reports.Col.WeightNet", Type = RptColType.FractionalNumber, Width = 110, Format = "N3" },
            new() { Key = "WeightGross", HeaderTextKey = "Reports.Col.WeightGross", Type = RptColType.FractionalNumber, Width = 110, Format = "N3" }
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
        var raw = await RptQuery.PackingListAsync(from, to, ct);
        var def = BuildDefinition();
        var rows = RptWinUtil.Map(raw, def.Columns);
        return new RptData
        {
            Columns = def.Columns,
            Rows = rows,
            Totals = RptWinUtil.SumTotals(rows, def.Columns, T("Reports.Totals"), "PartyName")
        };
    }
}

// ---- merged from cod/app/src/Views/Reports/PriceHistoryWin.cs ----
/// <summary>Price history per material from posted sales and purchase document lines.</summary>
public sealed class PriceHistoryWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Inv.MaterialPriceHistory",
        TitleTextKey = "Reports.MaterialPriceHistory",
        Columns = new RptCol[]
        {
            new() { Key = "DocDate", HeaderTextKey = "Reports.Col.Date", Type = RptColType.Date, Width = 100 },
            new() { Key = "DocNo", HeaderTextKey = "Reports.Col.DocNo", Type = RptColType.Text, Width = 120 },
            new() { Key = "Kind", HeaderTextKey = "Reports.Col.DocKind", Type = RptColType.Text, Width = 70 },
            new() { Key = "PartyName", HeaderTextKey = "Reports.Col.PartyName", Type = RptColType.Text, Width = 200 },
            new() { Key = "MaterialCode", HeaderTextKey = "Reports.Col.MaterialCode", Type = RptColType.Text, Width = 110 },
            new() { Key = "MaterialName", HeaderTextKey = "Reports.Col.MaterialName", Type = RptColType.Text, Width = 200 },
            new() { Key = "Qty", HeaderTextKey = "Reports.Col.Qty", Type = RptColType.FractionalNumber, Width = 90, Format = "N3" },
            new() { Key = "UnitPrice", HeaderTextKey = "Reports.Col.UnitPrice", Type = RptColType.Money, Width = 100, Format = "N4" },
            new() { Key = "Total", HeaderTextKey = "Reports.Col.Total", Type = RptColType.Money, Width = 120 }
        },
        Filters = new RptFlt[]
        {
            new() { Key = "From", LabelTextKey = "Reports.Filter.DateFrom", Kind = RptFltKind.DateFrom, DefaultValue = new DateTime(DateTime.Today.Year, 1, 1) },
            new() { Key = "To", LabelTextKey = "Reports.Filter.DateTo", Kind = RptFltKind.DateTo, DefaultValue = DateTime.Today },
            new() { Key = "Material", LabelTextKey = "Inventory.MaterialFilter", Kind = RptFltKind.Material }
        }
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var from = filters.GetDateOr("From", new DateTime(DateTime.Today.Year, 1, 1));
        var to = filters.GetDateOr("To", DateTime.Today);
        var mat = filters.GetString("Material");
        var raw = await RptQuery.PriceHistoryAsync(mat, from, to, ct);
        var def = BuildDefinition();
        var rows = RptWinUtil.Map(raw, def.Columns);
        return new RptData
        {
            Columns = def.Columns,
            Rows = rows,
            Totals = RptWinUtil.SumTotals(rows, def.Columns, T("Reports.Totals"), "PartyName", "UnitPrice")
        };
    }
}

// ---- merged from cod/app/src/Views/Reports/ServiceMatWin.cs ----
/// <summary>Service materials (is_service) with their movement totals and sales revenue.</summary>
public sealed class ServiceMatWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Inv.ServiceMaterials",
        TitleTextKey = "Reports.ServiceMaterials",
        Columns = new RptCol[]
        {
            new() { Key = "MaterialCode", HeaderTextKey = "Reports.Col.MaterialCode", Type = RptColType.Text, Width = 110 },
            new() { Key = "MaterialName", HeaderTextKey = "Reports.Col.MaterialName", Type = RptColType.Text, Width = 240 },
            new() { Key = "Unit", HeaderTextKey = "Reports.Col.Unit", Type = RptColType.Text, Width = 70 },
            new() { Key = "MoveCount", HeaderTextKey = "Reports.Col.MoveCount", Type = RptColType.WholeNumber, Width = 100 },
            new() { Key = "QtyIn", HeaderTextKey = "Reports.Col.QtyIn", Type = RptColType.FractionalNumber, Width = 90, Format = "N3" },
            new() { Key = "QtyOut", HeaderTextKey = "Reports.Col.QtyOut", Type = RptColType.FractionalNumber, Width = 90, Format = "N3" },
            new() { Key = "Revenue", HeaderTextKey = "Reports.Col.Revenue", Type = RptColType.Money, Width = 130 }
        },
        Filters = Array.Empty<RptFlt>()
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var raw = await RptQuery.ServiceMaterialsAsync(ct);
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

// ---- merged from cod/app/src/Views/Reports/ShortListWin.cs ----
/// <summary>
/// Phase 7B - "Shortage list" (Inventory > Movements).
/// Lists every warehouse/material line whose on-hand quantity has fallen
/// to or below its minimum (reorder) level, together with the shortfall
/// quantity and the replenishment value at average cost.
/// </summary>
public sealed class ShortListWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Inv.ShortageList",
        TitleTextKey = "Inventory.ShortageList",
        Columns = new RptCol[]
        {
            new() { Key = "Warehouse", HeaderTextKey = "Reports.Col.Warehouse", Type = RptColType.Text, Width = 120 },
            new() { Key = "MaterialCode", HeaderTextKey = "Reports.Col.MaterialCode", Type = RptColType.Text, Width = 120 },
            new() { Key = "MaterialName", HeaderTextKey = "Reports.Col.MaterialName", Type = RptColType.Text, Width = 240 },
            new() { Key = "Unit", HeaderTextKey = "Reports.Col.Unit", Type = RptColType.Text, Width = 80 },
            new() { Key = "OnHand", HeaderTextKey = "Reports.Col.OnHand", Type = RptColType.FractionalNumber, Width = 110, Format = "N3" },
            new() { Key = "MinQty", HeaderTextKey = "Reports.Col.MinQty", Type = RptColType.FractionalNumber, Width = 110, Format = "N3" },
            new() { Key = "Needed", HeaderTextKey = "Inventory.ShortageNeeded", Type = RptColType.FractionalNumber, Width = 110, Format = "N3" },
            new() { Key = "AvgCost", HeaderTextKey = "Reports.Col.AvgCost", Type = RptColType.Money, Width = 100, Format = "N4" },
            new() { Key = "NeededValue", HeaderTextKey = "Reports.Col.Value", Type = RptColType.Money, Width = 120, Format = "N2" },
        },
        Filters = new RptFlt[]
        {
            new() { Key = "Warehouse", LabelTextKey = "Reports.Filter.Warehouse", Kind = RptFltKind.Warehouse },
            new() { Key = "Material", LabelTextKey = "Inventory.MaterialFilter", Kind = RptFltKind.Material },
            new() { Key = "Group", LabelTextKey = "Inventory.MaterialGroupFilter", Kind = RptFltKind.MaterialGroup },
        }
    };

    protected override IReadOnlyList<KeyValuePair<string, string>> LoadWarehouseChoices()
        => RptQuery.WarehouseChoices();

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
        var wh = filters.GetString("Warehouse");
        var material = filters.GetString("Material");
        var group = filters.GetString("Group");

        // Pull every balance line and keep only shortage lines client-side,
        // so both "below minimum" and "out of stock" cases are covered.
        var raw = await RptQuery.StockBalancesAsync(wh, material, group, null, ct);

        var rows = raw
            .Select(r => new
            {
                Warehouse = r.GetValueOrDefault("Warehouse"),
                MaterialCode = r.GetValueOrDefault("MaterialCode"),
                MaterialName = r.GetValueOrDefault("MaterialName"),
                Unit = r.GetValueOrDefault("Unit"),
                OnHand = ToDec(r.GetValueOrDefault("OnHand")),
                MinQty = ToDec(r.GetValueOrDefault("MinQty")),
                AvgCost = ToDec(r.GetValueOrDefault("AvgCost")),
            })
            .Where(x => x.MinQty > 0 && x.OnHand <= x.MinQty || x.OnHand < 0)
            .OrderBy(x => x.MaterialCode)
            .Select(x => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["Warehouse"] = x.Warehouse,
                ["MaterialCode"] = x.MaterialCode,
                ["MaterialName"] = x.MaterialName,
                ["Unit"] = x.Unit,
                ["OnHand"] = x.OnHand,
                ["MinQty"] = x.MinQty,
                ["Needed"] = Math.Max(0m, x.MinQty - x.OnHand),
                ["AvgCost"] = x.AvgCost,
                ["NeededValue"] = Math.Max(0m, x.MinQty - x.OnHand) * x.AvgCost,
            })
            .ToList();

        return new RptData
        {
            Columns = BuildDefinition().Columns,
            Rows = rows,
            Totals = new Dictionary<string, object?>
            {
                ["MaterialName"] = T("Reports.Totals"),
                ["Needed"] = rows.Sum(x => ToDec(x["Needed"])),
                ["NeededValue"] = rows.Sum(x => ToDec(x["NeededValue"])),
            }
        };
    }

    private static decimal ToDec(object? v)
        => v is null ? 0m : Convert.ToDecimal(v, CultureInfo.InvariantCulture);
}

// ---- merged from cod/app/src/Views/Reports/StagnantWin.cs ----
/// <summary>Stock on hand with no outbound movement within the chosen number of days.</summary>
public sealed class StagnantWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Inv.StagnantMaterials",
        TitleTextKey = "Reports.StagnantMaterials",
        Columns = new RptCol[]
        {
            new() { Key = "Warehouse", HeaderTextKey = "Reports.Col.Warehouse", Type = RptColType.Text, Width = 110 },
            new() { Key = "MaterialCode", HeaderTextKey = "Reports.Col.MaterialCode", Type = RptColType.Text, Width = 110 },
            new() { Key = "MaterialName", HeaderTextKey = "Reports.Col.MaterialName", Type = RptColType.Text, Width = 220 },
            new() { Key = "OnHand", HeaderTextKey = "Reports.Col.OnHand", Type = RptColType.FractionalNumber, Width = 100, Format = "N3" },
            new() { Key = "AvgCost", HeaderTextKey = "Reports.Col.AvgCost", Type = RptColType.Money, Width = 100, Format = "N4" },
            new() { Key = "Value", HeaderTextKey = "Reports.Col.Value", Type = RptColType.Money, Width = 120 },
            new() { Key = "LastOutDate", HeaderTextKey = "Reports.Col.LastMovement", Type = RptColType.Date, Width = 110 }
        },
        Filters = new RptFlt[]
        {
            new() { Key = "Days", LabelTextKey = "Reports.Filter.Days", Kind = RptFltKind.Text, DefaultValue = "90" }
        }
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var days = RptWinUtil.ParseInt(filters.GetString("Days"), 90);
        var raw = await RptQuery.StagnantMaterialsAsync(days, ct);
        var def = BuildDefinition();
        var rows = RptWinUtil.Map(raw, def.Columns);
        return new RptData
        {
            Columns = def.Columns,
            Rows = rows,
            Totals = RptWinUtil.SumTotals(rows, def.Columns, T("Reports.Totals"), "MaterialName", "AvgCost")
        };
    }
}

// ---- merged from cod/app/src/Views/Reports/StkAdjRptWin.cs ----
public sealed class StkAdjRptWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Inv.StockMovements",
        TitleTextKey = "Reports.StockAdjustment",
        Columns = new RptCol[]
        {
            new() { Key = "MoveDate", HeaderTextKey = "Reports.Col.Date", Type = RptColType.Date, Width = 100 },
            new() { Key = "Warehouse", HeaderTextKey = "Reports.Col.Warehouse", Width = 110 },
            new() { Key = "MoveType", HeaderTextKey = "Reports.Col.MoveType", Width = 80 },
            new() { Key = "DocNo", HeaderTextKey = "Reports.Col.DocNo", Width = 120 },
            new() { Key = "MaterialCode", HeaderTextKey = "Reports.Col.MaterialCode", Width = 110 },
            new() { Key = "MaterialName", HeaderTextKey = "Reports.Col.MaterialName", Width = 200 },
            new() { Key = "QtyIn", HeaderTextKey = "Reports.Col.QtyIn", Type = RptColType.FractionalNumber, Width = 90, Format = "N3" },
            new() { Key = "QtyOut", HeaderTextKey = "Reports.Col.QtyOut", Type = RptColType.FractionalNumber, Width = 90, Format = "N3" },
            new() { Key = "UnitCost", HeaderTextKey = "Reports.Col.AvgCost", Type = RptColType.Money, Width = 90, Format = "N4" },
            new() { Key = "MoveValue", HeaderTextKey = "Reports.Col.Value", Type = RptColType.Money, Width = 110, Format = "N2" },
            new() { Key = "Note", HeaderTextKey = "Reports.Col.Note", Width = 240 },
        },
        Filters = new RptFlt[]
        {
            new() { Key = "Material", LabelTextKey = "Inventory.MaterialFilter", Kind = RptFltKind.Material },
            new() { Key = "Warehouse", LabelTextKey = "Reports.Filter.Warehouse", Kind = RptFltKind.Warehouse },
            new() { Key = "Document", LabelTextKey = "Reports.Col.DocNo", Kind = RptFltKind.Text },
            new() { Key = "From", LabelTextKey = "Reports.Filter.DateFrom", Kind = RptFltKind.DateFrom, DefaultValue = new DateTime(DateTime.Today.Year, 1, 1) },
            new() { Key = "To", LabelTextKey = "Reports.Filter.DateTo", Kind = RptFltKind.DateTo, DefaultValue = DateTime.Today },
        }
    };

    protected override IReadOnlyList<KeyValuePair<string, string>> LoadWarehouseChoices()
        => RptQuery.WarehouseChoices();

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var material = filters.GetString("Material");
        var warehouse = filters.GetString("Warehouse");
        var document = filters.GetString("Document");
        var from = filters.GetDateOr("From", new DateTime(DateTime.Today.Year, 1, 1));
        var to = filters.GetDateOr("To", DateTime.Today);
        var raw = await RptQuery.StockAdjustmentsAsync(material, warehouse, document, from, to, ct);
        var rows = raw.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["MoveDate"] = r.GetValueOrDefault("MoveDate"),
            ["Warehouse"] = FormatWarehouse(r),
            ["MoveType"] = r.GetValueOrDefault("MoveType"),
            ["DocNo"] = r.GetValueOrDefault("DocNo"),
            ["MaterialCode"] = r.GetValueOrDefault("MaterialCode"),
            ["MaterialName"] = r.GetValueOrDefault("MaterialName"),
            ["QtyIn"] = Dec(r.GetValueOrDefault("QtyIn")),
            ["QtyOut"] = Dec(r.GetValueOrDefault("QtyOut")),
            ["UnitCost"] = Dec(r.GetValueOrDefault("UnitCost")),
            ["MoveValue"] = (Dec(r.GetValueOrDefault("QtyIn")) - Dec(r.GetValueOrDefault("QtyOut")))
                * Dec(r.GetValueOrDefault("UnitCost")),
            ["Note"] = r.GetValueOrDefault("Note"),
        }).ToList();
        return new RptData
        {
            Columns = BuildDefinition().Columns,
            Rows = rows,
            Totals = new Dictionary<string, object?>
            {
                ["MaterialName"] = T("Reports.Totals"),
                ["QtyIn"] = rows.Sum(x => Dec(x["QtyIn"])),
                ["QtyOut"] = rows.Sum(x => Dec(x["QtyOut"])),
                ["MoveValue"] = rows.Sum(x => Dec(x["MoveValue"])),
            }
        };
    }

    private static decimal Dec(object? v) => v is null ? 0m : Convert.ToDecimal(v, CultureInfo.InvariantCulture);

    private static string FormatWarehouse(IReadOnlyDictionary<string, object?> row)
    {
        var code = Convert.ToString(row.GetValueOrDefault("Warehouse"), CultureInfo.InvariantCulture) ?? "";
        var nameAr = Convert.ToString(row.GetValueOrDefault("WarehouseName"), CultureInfo.InvariantCulture) ?? "";
        var nameEn = Convert.ToString(row.GetValueOrDefault("WarehouseNameEn"), CultureInfo.InvariantCulture) ?? "";
        var name = string.Equals(App.CurrentLanguageCode, "ar", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(nameEn)
            ? nameAr
            : nameEn;
        return string.IsNullOrWhiteSpace(name) ? code : $"{code} — {name}";
    }
}

// ---- merged from cod/app/src/Views/Reports/StkBalWin.cs ----
/// <summary>Warehouse stock balances with inventory valuation.</summary>
public sealed class StkBalWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Inv.StockBalances",
        TitleTextKey = "Reports.StockBalances",
        Columns = new RptCol[]
        {
            new() { Key = "Warehouse", HeaderTextKey = "Reports.Col.Warehouse", Type = RptColType.Text, Width = 120 },
            new() { Key = "MaterialCode", HeaderTextKey = "Reports.Col.MaterialCode", Type = RptColType.Text, Width = 120 },
            new() { Key = "MaterialName", HeaderTextKey = "Reports.Col.MaterialName", Type = RptColType.Text, Width = 240 },
            new() { Key = "Unit", HeaderTextKey = "Reports.Col.Unit", Type = RptColType.Text, Width = 80 },
            new() { Key = "Qty", HeaderTextKey = "Reports.Col.Qty", Type = RptColType.FractionalNumber, Width = 100, Format = "N3" },
            new() { Key = "Reserved", HeaderTextKey = "Reports.Col.Reserved", Type = RptColType.FractionalNumber, Width = 100, Format = "N3" },
            new() { Key = "OnHand", HeaderTextKey = "Reports.Col.OnHand", Type = RptColType.FractionalNumber, Width = 110, Format = "N3" },
            new() { Key = "MinQty", HeaderTextKey = "Reports.Col.MinQty", Type = RptColType.FractionalNumber, Width = 110, Format = "N3" },
            new() { Key = "AvgCost", HeaderTextKey = "Reports.Col.AvgCost", Type = RptColType.Money, Width = 100, Format = "N4" },
            new() { Key = "Value", HeaderTextKey = "Reports.Col.Value", Type = RptColType.Money, Width = 120, Format = "N2" },
        },
        Filters = new RptFlt[]
        {
            new() { Key = "Warehouse", LabelTextKey = "Reports.Filter.Warehouse", Kind = RptFltKind.Warehouse },
            new() { Key = "Material", LabelTextKey = "Inventory.MaterialFilter", Kind = RptFltKind.Material },
            new() { Key = "Group", LabelTextKey = "Inventory.MaterialGroupFilter", Kind = RptFltKind.MaterialGroup },
            new()
            {
                Key = "State",
                LabelTextKey = "Inventory.StockStatusFilter",
                Kind = RptFltKind.Choice,
                Choices = new[]
                {
                    new KeyValuePair<string, string>("", "Reports.Filter.All"),
                    new KeyValuePair<string, string>("IN_STOCK", "Inventory.StockStatus.InStock"),
                    new KeyValuePair<string, string>("OUT_OF_STOCK", "Inventory.StockStatus.OutOfStock"),
                    new KeyValuePair<string, string>("REORDER", "Inventory.StockStatus.Reorder"),
                    new KeyValuePair<string, string>("NEGATIVE", "Inventory.StockStatus.Negative")
                }
            }
        }
    };

    protected override IReadOnlyList<KeyValuePair<string, string>> LoadWarehouseChoices()
        => RptQuery.WarehouseChoices();

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
        var wh = filters.GetString("Warehouse");
        var material = filters.GetString("Material");
        var group = filters.GetString("Group");
        var state = filters.GetString("State");
        var raw = await RptQuery.StockBalancesAsync(wh, material, group, state, ct);
        var rows = raw.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["Warehouse"] = r.GetValueOrDefault("Warehouse"),
            ["MaterialCode"] = r.GetValueOrDefault("MaterialCode"),
            ["MaterialName"] = r.GetValueOrDefault("MaterialName"),
            ["Unit"] = r.GetValueOrDefault("Unit"),
            ["Qty"] = ToDec(r.GetValueOrDefault("Qty")),
            ["Reserved"] = ToDec(r.GetValueOrDefault("Reserved")),
            ["OnHand"] = ToDec(r.GetValueOrDefault("OnHand")),
            ["MinQty"] = ToDec(r.GetValueOrDefault("MinQty")),
            ["AvgCost"] = ToDec(r.GetValueOrDefault("AvgCost")),
            ["Value"] = ToDec(r.GetValueOrDefault("Value")),
        }).ToList();

        return new RptData
        {
            Columns = BuildDefinition().Columns,
            Rows = rows,
            Totals = new Dictionary<string, object?>
            {
                ["MaterialName"] = T("Reports.Totals"),
                ["Qty"] = rows.Sum(x => ToDec(x["Qty"])),
                ["Reserved"] = rows.Sum(x => ToDec(x["Reserved"])),
                ["OnHand"] = rows.Sum(x => ToDec(x["OnHand"])),
                ["Value"] = rows.Sum(x => ToDec(x["Value"])),
            }
        };
    }

    private static decimal ToDec(object? v)
        => v is null ? 0m : Convert.ToDecimal(v, CultureInfo.InvariantCulture);
}
