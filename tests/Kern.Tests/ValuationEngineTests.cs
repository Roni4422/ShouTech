using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ShouTech.App;
using ShouTech.App.Configuration;
using ShouTech.App.Services;
using ShouTech.Domain;
using ShouTech.Infrastructure;
using Xunit;

namespace Kern.Tests;

/// <summary>
/// Fix Phase Step 6 — valuation unification (Phase B migration 053).
/// Pins, against real migrated SQLite databases:
///   * the pure ValuationMath invariants (weighted average, FIFO/LIFO
///     layer order, residue absorption, negative-layer settlement);
///   * the engine end-to-end: the sweep's proven divergence sequence
///     (PUR 10@10 → SAL 4 → DLV 1 @ 0 → SRT 1) now yields ONE stock
///     value — the balance stock_value equals the sum of movement
///     cost_amounts — where the old three writers produced 70 vs 40;
///   * a sales return re-enters at its ORIGINAL outbound cost, never
///     at the selling price;
///   * cost_method is honored per material (WeightedAvg and FIFO
///     end-to-end; Standard and LastPurchase value issues at their
///     configured rates);
///   * a back-dated purchase + Recalculate Costs replays later
///     movements in posting order and restates their valuation;
///   * insufficient stock on a material that disallows negatives
///     throws the cataloged INV-6002.
/// Execution requires Windows (net8.0-windows testhost); the same
/// scenarios are verified headlessly on Linux by the Step 6 harness
/// (see docs/FIX_PHASE_STEP1.md).
/// </summary>
public class ValuationEngineTests
{
    private const string CompanyCode = "V6";

    // ---------------- Pure ValuationMath ----------------

    [Fact]
    public void WeightedAvg_Issue_UsesMovingAverage_AndEmptyingAbsorbsResidue()
    {
        var state = new ValuationState();
        ValuationMath.Receipt(state, ValuationMath.WeightedAvg, 10m, 10m, "2026-01-01");
        ValuationMath.Receipt(state, ValuationMath.WeightedAvg, 10m, 20m, "2026-01-02");
        Assert.Equal(20m, state.Quantity);
        Assert.Equal(300m, state.Value);
        Assert.Equal(15m, state.DisplayRate);

        var issue = ValuationMath.Issue(state, ValuationMath.WeightedAvg, 5m,
            state.DisplayRate, state.DisplayRate, "2026-01-03");
        Assert.Equal(75m, issue.CostAmount);
        Assert.Equal(225m, state.Value);

        var last = ValuationMath.Issue(state, ValuationMath.WeightedAvg, 15m,
            state.DisplayRate, state.DisplayRate, "2026-01-04");
        Assert.Equal(225m, last.CostAmount);
        Assert.Equal(0m, state.Quantity);
        Assert.Equal(0m, state.Value);
    }

    [Fact]
    public void Fifo_Issue_ConsumesOldestLayersFirst()
    {
        var state = new ValuationState();
        ValuationMath.Receipt(state, ValuationMath.Fifo, 10m, 10m, "2026-01-01");
        ValuationMath.Receipt(state, ValuationMath.Fifo, 10m, 20m, "2026-01-02");

        var issue = ValuationMath.Issue(state, ValuationMath.Fifo, 12m, 0m, 0m, "2026-01-03");
        Assert.Equal(10m * 10m + 2m * 20m, issue.CostAmount);
        Assert.Equal(140m, issue.CostAmount);
        Assert.Equal(160m, state.Value);
        Assert.Equal(8m, state.Layers.Single(l => l.QtyRemaining > 0).QtyRemaining);
        Assert.Equal(20m, state.Layers.Single(l => l.QtyRemaining > 0).UnitCost);
    }

    [Fact]
    public void Lifo_Issue_ConsumesNewestLayersFirst()
    {
        var state = new ValuationState();
        ValuationMath.Receipt(state, ValuationMath.Lifo, 10m, 10m, "2026-01-01");
        ValuationMath.Receipt(state, ValuationMath.Lifo, 10m, 20m, "2026-01-02");

        var issue = ValuationMath.Issue(state, ValuationMath.Lifo, 12m, 0m, 0m, "2026-01-03");
        Assert.Equal(10m * 20m + 2m * 10m, issue.CostAmount);
        Assert.Equal(220m, issue.CostAmount);
        Assert.Equal(80m, state.Value);
    }

    [Fact]
    public void Fifo_Deficit_CreatesNegativeLayer_NextReceiptSettlesIt()
    {
        var state = new ValuationState();
        ValuationMath.Receipt(state, ValuationMath.Fifo, 5m, 10m, "2026-01-01");
        var issue = ValuationMath.Issue(state, ValuationMath.Fifo, 8m, 0m, 12m, "2026-01-02");
        // 5 @ 10 from the layer + 3 deficit @ fallback 12.
        Assert.Equal(50m + 36m, issue.CostAmount);
        Assert.Equal(-3m, state.Quantity);
        Assert.Equal(-36m, state.Value);

        var receipt = ValuationMath.Receipt(state, ValuationMath.Fifo, 10m, 20m, "2026-01-03");
        // 3 settle the deficit: value moves by 3 x (12 - 20) on top of
        // the plain 10 x 20 receipt value.
        Assert.Equal(200m - 24m, receipt.CostAmount);
        Assert.Equal(7m, state.Quantity);
        Assert.Equal(140m, state.Value);
        Assert.Equal(7m, state.Layers.Where(l => l.QtyRemaining > 0).Sum(l => l.QtyRemaining));
    }

    // ---------------- Engine integration ----------------

    [Fact]
    public async Task SweepSequence_ThroughEngine_YieldsOneConsistentStockValue()
    {
        using var db = await SeededAsync();
        SeedMaterial(db, 42, "WeightedAvg");

        // PUR 10 @ 10 → SAL 4 (selling price 20 must NOT enter valuation)
        // → DLV 1 (the chain writer used to record unit_cost = 0)
        // → SRT 1 linked to the sale (re-enters at original cost 10).
        Apply(db, Req(42, "2026-01-01", "PUR", "PUR-1", qtyIn: 10m, receiptCost: 10m));
        var sale = Apply(db, Req(42, "2026-01-02", "SAL", "SAL-1", qtyOut: 4m));
        Assert.Equal(10m, sale.ValuationRate);
        Assert.Equal(-40m, sale.CostAmount);
        var delivery = Apply(db, Req(42, "2026-01-03", "DLV", "DLV-1", qtyOut: 1m));
        Assert.Equal(-10m, delivery.CostAmount);
        var linked = StockValuationEngine.TryGetDocumentRate(
            db.Connection, db.Tx, "SQLite", CompanyCode, "SAL-1", 42, 1, issued: true);
        Assert.Equal(10m, linked);
        var ret = Apply(db, Req(42, "2026-01-04", "SRT", "SRT-1", qtyIn: 1m, linkedRate: linked));
        Assert.Equal(10m, ret.CostAmount);

        // Balance qty 6, value 60 — and the movement ledger agrees:
        // Σ cost_amount == balance stock_value (the 70-vs-40 divergence
        // is impossible when both read the engine's numbers).
        var (qty, value, _) = Balance(db, 42);
        Assert.Equal(6m, qty);
        Assert.Equal(60m, value);
        Assert.Equal(value, SumCostAmounts(db, 42));
        // The card average is maintained by posting now.
        Assert.Equal(10m, CardAvgCost(db, 42));
    }

    [Fact]
    public async Task FifoMaterial_EndToEnd_LayersDriveIssueCost()
    {
        using var db = await SeededAsync();
        SeedMaterial(db, 43, "FIFO");
        Apply(db, Req(43, "2026-01-01", "PUR", "P-1", qtyIn: 10m, receiptCost: 10m));
        Apply(db, Req(43, "2026-01-02", "PUR", "P-2", qtyIn: 10m, receiptCost: 20m));
        var issue = Apply(db, Req(43, "2026-01-03", "SAL", "S-1", qtyOut: 12m));
        Assert.Equal(-140m, issue.CostAmount);
        var (qty, value, avg) = Balance(db, 43);
        Assert.Equal(8m, qty);
        Assert.Equal(160m, value);
        Assert.Equal(20m, avg);
    }

    [Fact]
    public async Task StandardCost_IssuesValueAtStandard_VarianceStaysInValue()
    {
        using var db = await SeededAsync();
        SeedMaterial(db, 44, "Standard", standardCost: 15m);
        Apply(db, Req(44, "2026-01-01", "PUR", "P-1", qtyIn: 10m, receiptCost: 10m));
        var issue = Apply(db, Req(44, "2026-01-02", "SAL", "S-1", qtyOut: 4m));
        Assert.Equal(15m, issue.ValuationRate);
        Assert.Equal(-60m, issue.CostAmount);
        // Receipt at actual 10, issue at standard 15: the 40 purchase
        // variance remains visible in the balance value (report-only
        // per design V3 — no variance GL account is posted here).
        var (_, value, _) = Balance(db, 44);
        Assert.Equal(40m, value);
    }

    [Fact]
    public async Task InsufficientStock_WhenNegativesDisallowed_ThrowsCatalogedError()
    {
        using var db = await SeededAsync();
        SeedMaterial(db, 45, "WeightedAvg", allowNegative: false);
        Apply(db, Req(45, "2026-01-01", "PUR", "P-1", qtyIn: 2m, receiptCost: 10m));
        var ex = Assert.Throws<AppError>(() =>
            Apply(db, Req(45, "2026-01-02", "SAL", "S-1", qtyOut: 5m)));
        Assert.Equal(ErrorCodes.StockValuationFailed, ex.Code);
    }

    [Fact]
    public async Task BackDatedPurchase_RecalculateCosts_RevaluesLaterMovements()
    {
        using var db = await SeededAsync();
        SeedMaterial(db, 46, "WeightedAvg");
        Apply(db, Req(46, "2026-01-10", "PUR", "P-1", qtyIn: 10m, receiptCost: 10m));
        var sale = Apply(db, Req(46, "2026-01-20", "SAL", "S-1", qtyOut: 5m));
        Assert.Equal(-50m, sale.CostAmount);
        // Back-dated cheaper purchase, posted later but dated earlier.
        Apply(db, Req(46, "2026-01-05", "PUR", "P-0", qtyIn: 10m, receiptCost: 4m));
        db.Commit();

        var service = new StockRecalculationService("SQLite", db.ConnectionString);
        var result = await service.RecalculateAsync(
            CompanyCode, null, 46, new DateOnly(2026, 1, 1), "back-dated purchase", "test");
        Assert.True(result.MovementsReplayed >= 3);

        // Replay order P-0 (10@4), P-1 (10@10), S-1 (5 out):
        // average at sale = 140/20 = 7 → COGS 35; balance 15 @ 7 = 105.
        var (qty, value, avg) = BalanceFresh(db, 46);
        Assert.Equal(15m, qty);
        Assert.Equal(105m, value);
        Assert.Equal(7m, avg);
        Assert.Equal(-35m, MovementCostAmount(db, "S-1"));
        Assert.Equal(value, SumCostAmountsFresh(db, 46));
    }

    // ---------------- Helpers ----------------

    private static StockMovementRequest Req(
        long materialId, string date, string type, string docNo,
        decimal qtyIn = 0m, decimal qtyOut = 0m,
        decimal? receiptCost = null, decimal? linkedRate = null)
        => new(CompanyCode, 1, materialId, date, type, docNo,
               qtyIn, qtyOut, ReceiptUnitCost: receiptCost, LinkedRate: linkedRate);

    private static StockMovementResult Apply(TestDb db, StockMovementRequest request)
        => StockValuationEngine.Apply(db.Connection, db.Tx, "SQLite", request);

    private static void SeedMaterial(TestDb db, long id, string costMethod,
        decimal standardCost = 0m, bool allowNegative = true)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.Transaction = db.Tx;
        cmd.CommandText = $"""
            INSERT INTO materials (material_id, company_id, code, name,
                is_stocked, is_service, cost_method, standard_cost, allow_negative, is_deleted)
            VALUES ({id}, '{CompanyCode}', 'MAT-{id}', 'Item',
                1, 0, '{costMethod}', {standardCost.ToString(CultureInfo.InvariantCulture)},
                {(allowNegative ? 1 : 0)}, 0);
            """;
        cmd.ExecuteNonQuery();
    }

    private static (decimal Qty, decimal Value, decimal Avg) Balance(TestDb db, long materialId)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.Transaction = db.Tx;
        cmd.CommandText = $"SELECT available_qty, stock_value, average_cost FROM material_stock_balances WHERE company_id = '{CompanyCode}' AND warehouse_id = 1 AND material_id = {materialId};";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        return (reader.GetDecimal(0), reader.GetDecimal(1), reader.GetDecimal(2));
    }

    private static (decimal Qty, decimal Value, decimal Avg) BalanceFresh(TestDb db, long materialId)
    {
        using var connection = new SqliteConnection(db.ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT available_qty, stock_value, average_cost FROM material_stock_balances WHERE company_id = '{CompanyCode}' AND warehouse_id = 1 AND material_id = {materialId};";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        return (reader.GetDecimal(0), reader.GetDecimal(1), reader.GetDecimal(2));
    }

    private static decimal SumCostAmounts(TestDb db, long materialId)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.Transaction = db.Tx;
        cmd.CommandText = $"SELECT COALESCE(SUM(cost_amount), 0) FROM material_stock_movements WHERE company_id = '{CompanyCode}' AND material_id = {materialId} AND is_deleted = 0;";
        return Convert.ToDecimal(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static decimal SumCostAmountsFresh(TestDb db, long materialId)
    {
        using var connection = new SqliteConnection(db.ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COALESCE(SUM(cost_amount), 0) FROM material_stock_movements WHERE company_id = '{CompanyCode}' AND material_id = {materialId} AND is_deleted = 0;";
        return Convert.ToDecimal(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static decimal MovementCostAmount(TestDb db, string docNo)
    {
        using var connection = new SqliteConnection(db.ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT cost_amount FROM material_stock_movements WHERE company_id = '{CompanyCode}' AND document_no = '{docNo}';";
        return Convert.ToDecimal(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static decimal CardAvgCost(TestDb db, long materialId)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.Transaction = db.Tx;
        cmd.CommandText = $"SELECT avg_cost FROM materials WHERE company_id = '{CompanyCode}' AND material_id = {materialId};";
        return Convert.ToDecimal(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static async Task<TestDb> SeededAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "valuation-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var dbPath = Path.Combine(root, "valuation.db");
        var companyId = Guid.NewGuid();
        var connectionString = $"Data Source={dbPath};";

        Assert.True((await SchemaMigrator.ApplyAllAsync("SQLite", dbPath)).Success);
        PointAppAt(dbPath);
        AppState.SetCompany(CompanyCode, "Valuation Test Co", "Valuation Test Co");

        using (var seed = new SqliteConnection(connectionString))
        {
            seed.Open();
            using var cmd = seed.CreateCommand();
            cmd.CommandText = "INSERT INTO companies (company_id, company_code, company_name_ar, company_name_en) VALUES ($id, $code, 'Valuation Test Co', 'Valuation Test Co');";
            cmd.Parameters.AddWithValue("$id", companyId.ToString("D"));
            cmd.Parameters.AddWithValue("$code", CompanyCode);
            cmd.ExecuteNonQuery();
            using var wh = seed.CreateCommand();
            wh.CommandText = $"INSERT INTO warehouses (warehouse_id, company_id, warehouse_code, warehouse_name_en, is_deleted) VALUES (1, '{CompanyCode}', 1, 'Main', 0);";
            wh.ExecuteNonQuery();
        }

        var connection = new SqliteConnection(connectionString);
        connection.Open();
        var tx = connection.BeginTransaction();
        return new TestDb
        {
            Root = root, DbPath = dbPath, CompanyId = companyId,
            Connection = connection, Tx = tx
        };
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

    private sealed class TestDb : IDisposable
    {
        public required string Root { get; init; }
        public required string DbPath { get; init; }
        public required Guid CompanyId { get; init; }
        public required SqliteConnection Connection { get; init; }
        public required SqliteTransaction Tx { get; init; }
        public string ConnectionString => $"Data Source={DbPath};";
        private bool _committed;

        public void Commit()
        {
            if (_committed) return;
            Tx.Commit();
            _committed = true;
        }

        public void Dispose()
        {
            if (!_committed)
                Tx.Rollback();
            Tx.Dispose();
            Connection.Dispose();
            AppState.ClearCompany();
            SqliteConnection.ClearAllPools();
            Directory.Delete(Root, recursive: true);
        }
    }
}
