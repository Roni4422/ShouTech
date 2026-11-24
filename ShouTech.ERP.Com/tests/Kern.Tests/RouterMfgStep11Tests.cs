using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ShouTech.App;
using ShouTech.App.Configuration;
using ShouTech.App.Services;
using ShouTech.App.Views;
using ShouTech.App.Views.Reports;
using ShouTech.Application.Services;
using ShouTech.Domain.Entities;
using ShouTech.Infrastructure;
using Xunit;

namespace Kern.Tests;

/// <summary>
/// Fix Phase Step 11 — router mode-stickiness (sweep S-2/S-3),
/// manufacturing report crashes (MFG-1/MFG-4) and manufacturing unit
/// equivalents (MFG-2). Pins:
///   * the router policy core: window identity is (type, ModeKey) —
///     requesting mode B while mode A is open opens mode B, a same-mode
///     request reactivates, single-mode windows (null keys) keep
///     type-only behaviour;
///   * every MFG report query executes against the real migration-034
///     schema and returns the seeded figures (variance hand-verified);
///     feasibility requirements come back in BASE units;
///   * MFG posting converts quantities through the shared
///     UnitEquivalents converter: 4 × g250 (0.25) consumes exactly
///     1 base unit at engine cost; a 2 × DOZEN finished receipt credits
///     24 base units; cancel restores both sides; a base-unit shortage
///     is refused.
/// All facts run the REAL services against real migrated SQLite
/// databases. Execution requires Windows (net8.0-windows testhost); the
/// same scenarios are verified headlessly by
/// ~/workspace/research/fix-step11/step11_harness.py (65/65 PASS; see
/// docs/FIX_PHASE_STEP1.md).
/// </summary>
public class RouterMfgStep11Tests
{
    private const string Actor = "step11-test";
    private const string Company = "S11";

    // ---------------- router policy (pure core) ----------------

    [Fact]
    public void RouterPolicy_DifferentMode_OpensNew_SameMode_Reactivates()
    {
        var open = new[] { (typeof(InvoiceWin), (string?)"SalesInvoice:new") };

        Assert.Equal(WindowActivationDecision.OpenNew,
            WindowModePolicy.Decide(open, typeof(InvoiceWin), "PurchaseInvoice:new"));
        Assert.Equal(WindowActivationDecision.ReactivateExisting,
            WindowModePolicy.Decide(open, typeof(InvoiceWin), "SalesInvoice:new"));
        // A loaded document is a different mode from a new document.
        Assert.Equal(WindowActivationDecision.OpenNew,
            WindowModePolicy.Decide(open, typeof(InvoiceWin), "SalesInvoice:5"));
        // Same key, different window type: not a match.
        Assert.Equal(WindowActivationDecision.OpenNew,
            WindowModePolicy.Decide(open, typeof(MfgVarianceWin), "SalesInvoice:new"));
    }

    [Fact]
    public void RouterPolicy_SecondFamily_AndSingleMode()
    {
        var open = new[] { (typeof(MfgVarianceWin), (string?)"Total") };
        Assert.Equal(WindowActivationDecision.OpenNew,
            WindowModePolicy.Decide(open, typeof(MfgVarianceWin), "Raw"));
        Assert.Equal(WindowActivationDecision.ReactivateExisting,
            WindowModePolicy.Decide(open, typeof(MfgVarianceWin), "Total"));

        // Single-mode windows carry no key on either side: type-only.
        var plain = new[] { (typeof(MatDirWin), (string?)null) };
        Assert.Equal(WindowActivationDecision.ReactivateExisting,
            WindowModePolicy.Decide(plain, typeof(MatDirWin), null));
        Assert.Equal(WindowActivationDecision.OpenNew,
            WindowModePolicy.Decide(plain, typeof(InvoiceWin), null));
    }

    // ---------------- MFG reports ----------------

    [Fact]
    public async Task MfgVariance_AllModes_Execute_WithHandVerifiedFigures()
    {
        using var db = await ReportFixtureAsync();

        var total = await RptQuery.MfgVarianceAsync("Total", CancellationToken.None);
        Assert.Equal(5, total.Count);
        var rm1 = total.Single(r => (string)r["ItemCode"]! == "RM-1" && Convert.ToInt64(r["DocNo"]) == 1);
        Assert.Equal(2m, Dec(rm1, "Variance"));            // 10 − 4×2
        var rm2 = total.Single(r => (string)r["ItemCode"]! == "RM-2" && Convert.ToInt64(r["DocNo"]) == 1);
        Assert.Equal(-1.5m, Dec(rm2, "Variance"));          // 12 − 3×4.5

        var raw = await RptQuery.MfgVarianceAsync("Raw", CancellationToken.None);
        Assert.Equal(4, raw.Count);
        Assert.All(raw, r => Assert.Equal("RAW", (string)r["LineKind"]!));

        var overhead = await RptQuery.MfgVarianceAsync("Overhead", CancellationToken.None);
        var svc = Assert.Single(overhead);
        Assert.Equal("SVC-1", (string)svc["ItemCode"]!);
        Assert.Equal("COST", (string)svc["LineKind"]!);
        Assert.Equal(2m, Dec(svc, "Variance"));             // 20 − 1×18

        var finished = await RptQuery.MfgVarianceAsync("Finished", CancellationToken.None);
        Assert.Equal(2, finished.Count);
        var fin1 = finished.Single(r => Convert.ToInt64(r["DocNo"]) == 1);
        Assert.Equal(12m, Dec(fin1, "Variance"));           // 62 − 10×5
        Assert.Equal("FINISHED", (string)fin1["LineKind"]!);
    }

    [Fact]
    public async Task MfgOrderCosts_Executes_WithSeededVariance()
    {
        using var db = await ReportFixtureAsync();
        var costs = await RptQuery.MfgOrderCostsAsync(CancellationToken.None);
        var order1 = costs.Single(r => Convert.ToInt64(r["DocNo"]) == 1);
        Assert.Equal(12m, Dec(order1, "Variance"));
    }

    [Fact]
    public async Task MfgFeasibility_Executes_AndRestatesRequirementsInBaseUnits()
    {
        using var db = await ReportFixtureAsync();
        var rows = await RptQuery.MfgFeasibilityAsync(CancellationToken.None);
        Assert.Equal(2, rows.Count);
        var rm1 = rows.Single(r => (string)r["LineItemCode"]! == "RM-1");
        Assert.Equal(1m, Dec(rm1, "Required"));             // 4 × g250 × 0.25
        Assert.Equal(0.5m, Dec(rm1, "OnHand"));
        var rm2 = rows.Single(r => (string)r["LineItemCode"]! == "RM-2");
        Assert.Equal(3m, Dec(rm2, "Required"));
        Assert.Equal(10m, Dec(rm2, "OnHand"));
    }

    // ---------------- MFG unit equivalents ----------------

    [Fact]
    public async Task MfgPosting_ConvertsQuantitiesToBaseUnits()
    {
        using var db = await PostingFixtureAsync();
        var service = new MfgOrderService(new MfgOrderRepository("SQLite", db.ConnectionString));

        // Display qty 4 of RM exceeds the 2 KG on hand as a raw number;
        // in base units it needs exactly 1 KG and posts.
        var order = NewOrder(2m, "DOZEN", 4m, "g250", 14m);
        await service.SaveDraftAsync(Company, order, Actor);
        await service.PostAsync(Company, order.Id, Actor);

        Assert.Equal(1m, db.Scalar($"SELECT qty_out FROM material_stock_movements WHERE movement_type='MFG_CONSUME' AND document_no='MO-{order.DocNo}' AND material_id=1"));
        Assert.Equal(-8m, db.Scalar($"SELECT cost_amount FROM material_stock_movements WHERE movement_type='MFG_CONSUME' AND document_no='MO-{order.DocNo}' AND material_id=1"));
        Assert.Equal(1m, db.Scalar("SELECT available_qty FROM material_stock_balances WHERE material_id=1"));
        Assert.Equal(24m, db.Scalar($"SELECT qty_in FROM material_stock_movements WHERE movement_type='MFG_PRODUCE' AND document_no='MO-{order.DocNo}' AND material_id=2"));
        Assert.Equal(1m, db.Scalar($"SELECT unit_cost FROM material_stock_movements WHERE movement_type='MFG_PRODUCE' AND document_no='MO-{order.DocNo}' AND material_id=2"));
        Assert.Equal(24m, db.Scalar("SELECT stock_value FROM material_stock_balances WHERE material_id=2"));

        await service.CancelAsync(Company, order.Id, Actor);
        Assert.Equal(2m, db.Scalar("SELECT available_qty FROM material_stock_balances WHERE material_id=1"));
        Assert.Equal(16m, db.Scalar("SELECT stock_value FROM material_stock_balances WHERE material_id=1"));
        Assert.Equal(0m, db.Scalar("SELECT available_qty FROM material_stock_balances WHERE material_id=2"));
    }

    [Fact]
    public async Task MfgPosting_BaseUnitShortage_IsRefused()
    {
        using var db = await PostingFixtureAsync();
        var service = new MfgOrderService(new MfgOrderRepository("SQLite", db.ConnectionString));
        var big = NewOrder(1m, "PCS", 400m, "g250", 0m);   // 400 × 0.25 = 100 KG > 2 KG
        await service.SaveDraftAsync(Company, big, Actor);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PostAsync(Company, big.Id, Actor));
        Assert.Equal(2m, db.Scalar("SELECT available_qty FROM material_stock_balances WHERE material_id=1"));
    }

    [Fact]
    public async Task UnitEquivalents_Resolve_TheInvoiceRule()
    {
        using var db = await PostingFixtureAsync();
        using var con = new SqliteConnection(db.ConnectionString);
        con.Open();
        using var tx = con.BeginTransaction();
        Assert.Equal(0.25m, await UnitEquivalents.ResolveAsync(con, tx, Company, 1, "g250", default));
        Assert.Equal(1m, await UnitEquivalents.ResolveAsync(con, tx, Company, 1, "KG", default));
        Assert.Equal(12m, await UnitEquivalents.ResolveAsync(con, tx, Company, 2, "DOZEN", default));
        Assert.Equal(1m, await UnitEquivalents.ResolveAsync(con, tx, Company, 1, "crate", default));
        Assert.Equal(1m, await UnitEquivalents.ResolveAsync(con, tx, Company, 1, null, default));
        var (baseQty, baseCost) = UnitEquivalents.ToBase(4m, 2.5m, 0.25m);
        Assert.Equal(1m, baseQty);
        Assert.Equal(10m, baseCost);
        tx.Rollback();
    }

    // ---------------- fixtures ----------------

    private static MfgOrder NewOrder(decimal fgQty, string fgUnit, decimal lineQty, string lineUnit, decimal overhead) => new()
    {
        DocDate = new DateOnly(2026, 11, 17),
        ItemCode = "FG-1",
        ItemName = "Finished Good",
        Qty = fgQty,
        UnitCode = fgUnit,
        WarehouseCode = "1",
        OverheadAmount = overhead,
        StandardUnitCost = 5m,
        Lines = new List<MfgOrderLine>
        {
            new() { LineKind = MfgLineKinds.Raw, ItemCode = "RM-1", ItemName = "Raw Material", Qty = lineQty, UnitCode = lineUnit, UnitCost = 2.5m, StandardUnitCost = 2.5m }
        }
    };

    private static decimal Dec(IReadOnlyDictionary<string, object?> row, string key)
        => Convert.ToDecimal(row[key], CultureInfo.InvariantCulture);

    private sealed class TestDb : IDisposable
    {
        public required string Root { get; init; }
        public required string ConnectionString { get; init; }
        public required Guid CompanyId { get; init; }

        public decimal Scalar(string sql)
        {
            using var con = new SqliteConnection(ConnectionString);
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = sql;
            var v = cmd.ExecuteScalar();
            return v is null or DBNull ? 0m : Convert.ToDecimal(v, CultureInfo.InvariantCulture);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
        }
    }

    private static async Task<TestDb> NewDbAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "step11-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var dbPath = Path.Combine(root, "step11.db");
        var companyId = Guid.Parse("51100000-0000-4000-8000-000000000011");
        Assert.True((await SchemaMigrator.ApplyAllAsync("SQLite", dbPath)).Success);
        PointAppAt(dbPath);
        AppState.SetCompany(Company, "Step11 Co", "Step11 Co");
        var db = new TestDb { Root = root, ConnectionString = $"Data Source={dbPath};", CompanyId = companyId };
        Exec(db, "INSERT INTO companies (company_id, company_code, company_name_ar, company_name_en) VALUES ($id, $code, 'Step11 Co', 'Step11 Co');",
            ("$id", companyId.ToString("D")), ("$code", Company));
        return db;
    }

    private static async Task<TestDb> ReportFixtureAsync()
    {
        var db = await NewDbAsync();
        Exec(db, """
            INSERT INTO materials (material_id, company_id, code, name_ar, name_en, unit1, unit1_factor, unit2, unit2_factor, units_json)
            VALUES (101, 'S11', 'RM-1', 'Raw 1', 'Raw 1', 'KG', 1, 'g250', 0.25, '[{"UnitName":"KG","Factor":1},{"UnitName":"g250","Factor":0.25}]'),
                   (102, 'S11', 'RM-2', 'Raw 2', 'Raw 2', 'KG', 1, NULL, 1, '[]'),
                   (103, 'S11', 'FG-1', 'Finished', 'Finished', 'PCS', 1, NULL, 1, '[]'),
                   (104, 'S11', 'SVC-1', 'Added cost', 'Added cost', 'PCS', 1, NULL, 1, '[]');
            """);
        Exec(db, "INSERT INTO material_stock_balances (company_id, warehouse_id, material_id, available_qty, average_cost, last_cost, stock_value, is_deleted) VALUES ('S11', 1, 101, 0.5, 8, 8, 4, 0), ('S11', 1, 102, 10, 4, 4, 40, 0);");
        Exec(db, """
            INSERT INTO mfg_orders (id, company_id, doc_no, doc_date, item_code, item_name, qty, unit_code, warehouse_code, overhead_amount, standard_unit_cost, total_cost, unit_cost, status)
            VALUES ('a1100000-0000-4000-8000-000000000001', 'S11', 1, '2026-11-17', 'FG-1', 'Finished', 10, 'PCS', '1', 20, 5, 62, 6.2, 'Posted'),
                   ('a1100000-0000-4000-8000-000000000002', 'S11', 2, '2026-11-17', 'FG-1', 'Finished', 5, 'PCS', '1', 0, 5, 0, 0, 'Draft');
            INSERT INTO mfg_order_lines (id, order_id, line_kind, item_code, item_name, qty, unit_code, unit_cost, standard_unit_cost, amount)
            VALUES ('b1100000-0000-4000-8000-000000000001', 'a1100000-0000-4000-8000-000000000001', 'RAW', 'RM-1', 'Raw 1', 4, 'g250', 2.5, 2, 10),
                   ('b1100000-0000-4000-8000-000000000002', 'a1100000-0000-4000-8000-000000000001', 'RAW', 'RM-2', 'Raw 2', 3, 'KG', 4, 4.5, 12),
                   ('b1100000-0000-4000-8000-000000000003', 'a1100000-0000-4000-8000-000000000001', 'COST', 'SVC-1', 'Added cost', 1, 'PCS', 20, 18, 20),
                   ('b1100000-0000-4000-8000-000000000004', 'a1100000-0000-4000-8000-000000000002', 'RAW', 'RM-1', 'Raw 1', 4, 'g250', 2.5, 2, 10),
                   ('b1100000-0000-4000-8000-000000000005', 'a1100000-0000-4000-8000-000000000002', 'RAW', 'RM-2', 'Raw 2', 3, 'KG', 4, 4.5, 12);
            """);
        return db;
    }

    private static async Task<TestDb> PostingFixtureAsync()
    {
        var db = await NewDbAsync();
        Exec(db, "INSERT INTO warehouses (warehouse_id, company_id, warehouse_code, warehouse_name_en, is_deleted) VALUES (1, 'S11', 1, 'Main', 0);");
        var structure = new AccountingStructureRepository("SQLite", db.ConnectionString);
        await structure.CreateFiscalYearAsync(db.CompanyId, 2026, Actor);
        Exec(db, "INSERT INTO chart_of_accounts (account_id, company_id, account_code, account_name, account_type, is_postable, is_active, is_deleted) VALUES ($id, $c, '1203', 'Merchandise Inventory', 'ASSET', 1, 1, 0);",
            ("$id", Guid.NewGuid().ToString("D")), ("$c", db.CompanyId.ToString("D")));
        Exec(db, """
            INSERT INTO materials (material_id, company_id, code, name_ar, name_en, is_stocked, is_service, item_type, cost_method, allow_negative, is_deleted, unit1, unit1_factor, unit2, unit2_factor, units_json)
            VALUES (1, 'S11', 'RM-1', 'Raw', 'Raw', 1, 0, 'Stock', 'WeightedAvg', 1, 0, 'KG', 1, 'g250', 0.25, '[{"UnitName":"KG","Factor":1},{"UnitName":"g250","Factor":0.25}]'),
                   (2, 'S11', 'FG-1', 'Finished', 'Finished', 1, 0, 'Stock', 'WeightedAvg', 1, 0, 'PCS', 1, 'DOZEN', 12, '[{"UnitName":"PCS","Factor":1},{"UnitName":"DOZEN","Factor":12}]');
            """);
        using (var con = new SqliteConnection(db.ConnectionString))
        {
            con.Open();
            using var tx = con.BeginTransaction();
            StockValuationEngine.Apply(con, tx, "SQLite",
                new StockMovementRequest(Company, 1, 1, "2026-11-01", "OPN", "OPEN-1", QtyIn: 2m, QtyOut: 0m, ReceiptUnitCost: 8m, Note: "Opening stock"));
            tx.Commit();
        }
        return db;
    }

    private static void Exec(TestDb db, string sql, params (string, object)[] ps)
    {
        using var con = new SqliteConnection(db.ConnectionString);
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        cmd.ExecuteNonQuery();
    }

    private static void PointAppAt(string sqlitePath)
    {
        var settings = AppCfgSvc.Current;
        settings.ConnectionStrings["Default"] = new ConnectionStringSettings
        {
            Provider = "SQLite",
            ConnectionString = sqlitePath
        };
        settings.Database ??= new DatabaseSettings();
        settings.Database.DefaultProvider = "SQLite";
    }
}
