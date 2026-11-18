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
using ShouTech.Domain.Entities;
using ShouTech.Infrastructure;
using Xunit;

namespace Kern.Tests;

/// <summary>
/// BUILD Manufacturing (migration 058) — pins, against real migrated
/// SQLite databases:
///   * BOM explosion over two levels is exact (quantities x scrap,
///     unit-converted to base), and a self-referencing BOM (direct or
///     transitive) is refused at save with MFG-1001;
///   * the staged workflow's money: partial issues move WIP exactly at
///     valuation-engine cost (Dr 1211 / Cr 1203), a partial finish
///     receives the finished good at the proportional pool value with
///     the by-product carved out at its cost share, and Close settles
///     the remaining WIP balance to the variance account (406);
///   * cancelling after a partial issue reverses stock and GL to zero;
///   * a completing finish prices the finished good at
///     (materials + overhead - by-product share) / qty;
///   * an MRP-lite planning run nets demand against engine stock and
///     incoming purchase orders, and both conversions create real
///     documents (Draft production order / Draft purchase order).
/// Execution requires Windows (net8.0-windows testhost); the same
/// scenarios are verified headlessly by
/// ~/workspace/research/build-mfg/mfg_harness.py (see
/// docs/BUILD_MANUFACTURING.md).
/// </summary>
public class ManufacturingBuildTests
{
    private const string Actor = "mfg-test";
    private const string Company = "MFG1";

    // ------------------------------------------------------------------
    // (a) BOM explosion + cycle refusal
    // ------------------------------------------------------------------

    [Fact]
    public async Task Bom_Explosion_TwoLevels_ExactLeafRequirements_WithScrapAndUnitConversion()
    {
        using var db = await SeededAsync();
        SeedMaterial(db, 1, "LEAF", "G", 1m, "KG", 1000m, avgCost: 0.01m);
        SeedMaterial(db, 2, "SUB", "PCS", 1m, null, 0m, avgCost: 5m);
        SeedMaterial(db, 3, "FIN", "PCS", 1m, null, 0m);
        SeedBalance(db, materialId: 1, qty: 100000m, avg: 0.01m);
        var boms = new BomRepository("SQLite", db.ConnectionString);

        var subBom = new Bom
        {
            MaterialCode = "SUB", Version = 1, IsActive = true, IsDefault = true,
            OutputQty = 1m, OutputUnit = "PCS",
            Lines = new List<BomLine>
            {
                new() { ComponentCode = "LEAF", Qty = 0.5m, UnitCode = "KG", ScrapPct = 10m }
            }
        };
        await boms.SaveAsync(Company, subBom, Actor);
        // Snapshot: engine base rate 0.01/G x KG equivalent 1000 = 10/KG.
        Assert.Equal(10m, subBom.Lines[0].StandardUnitCost);
        Assert.Equal(5m, subBom.StandardUnitCost);

        var finBom = new Bom
        {
            MaterialCode = "FIN", Version = 1, IsActive = true, IsDefault = true,
            OutputQty = 2m, OutputUnit = "PCS",
            Lines = new List<BomLine>
            {
                new() { ComponentCode = "SUB", Qty = 3m, UnitCode = "PCS" },
                new() { ComponentCode = "LEAF", Qty = 1m, UnitCode = "KG" }
            }
        };
        await boms.SaveAsync(Company, finBom, Actor);

        var explosion = await boms.ExplodeAsync(Company, finBom.Id, 4m);
        var leaf = Assert.Single(explosion.Requirements);
        Assert.Equal("LEAF", leaf.MaterialCode);
        // Direct: 1 KG x (4/2) = 2 KG. Via SUB: 6 SUB x 0.5 KG x 1.1 = 3.3 KG.
        Assert.Equal(5.3m, leaf.Qty);
        Assert.Equal(5300m, leaf.BaseQty);
        Assert.Equal(10m, leaf.StandardUnitCost);
        Assert.Equal(53m, leaf.StandardAmount);
    }

    [Fact]
    public async Task Bom_Save_SelfOrTransitiveCycle_RefusedWithMfg1001()
    {
        using var db = await SeededAsync();
        SeedMaterial(db, 1, "CYC-A", "PCS", 1m, null, 0m);
        SeedMaterial(db, 2, "CYC-B", "PCS", 1m, null, 0m);
        var boms = new BomRepository("SQLite", db.ConnectionString);

        var direct = new Bom
        {
            MaterialCode = "CYC-A", Version = 1, IsActive = true, IsDefault = true,
            OutputQty = 1m, OutputUnit = "PCS",
            Lines = new List<BomLine> { new() { ComponentCode = "CYC-A", Qty = 1m, UnitCode = "PCS" } }
        };
        var error = await Assert.ThrowsAsync<AppError>(() => boms.SaveAsync(Company, direct, Actor));
        Assert.Equal(ErrorCodes.BomCycleDetected, error.Code);

        var aUsesB = new Bom
        {
            MaterialCode = "CYC-A", Version = 1, IsActive = true, IsDefault = true,
            OutputQty = 1m, OutputUnit = "PCS",
            Lines = new List<BomLine> { new() { ComponentCode = "CYC-B", Qty = 1m, UnitCode = "PCS" } }
        };
        await boms.SaveAsync(Company, aUsesB, Actor);
        var bUsesA = new Bom
        {
            MaterialCode = "CYC-B", Version = 1, IsActive = true, IsDefault = true,
            OutputQty = 1m, OutputUnit = "PCS",
            Lines = new List<BomLine> { new() { ComponentCode = "CYC-A", Qty = 1m, UnitCode = "PCS" } }
        };
        var transitive = await Assert.ThrowsAsync<AppError>(() => boms.SaveAsync(Company, bUsesA, Actor));
        Assert.Equal(ErrorCodes.BomCycleDetected, transitive.Code);
    }

    // ------------------------------------------------------------------
    // (b) Staged workflow: WIP + variance exact
    // ------------------------------------------------------------------

    [Fact]
    public async Task StagedOrder_PartialIssues_PartialFinish_Close_VarianceAndWipExact()
    {
        using var db = await SeededAsync();
        SeedMaterial(db, 1, "RM-1", "KG", 1m, null, 0m);
        SeedMaterial(db, 2, "FG-1", "PCS", 1m, null, 0m);
        SeedMaterial(db, 3, "BP-1", "PCS", 1m, null, 0m);
        SeedBalance(db, materialId: 1, qty: 100m, avg: 2m);
        var repo = new MfgOrderRepository("SQLite", db.ConnectionString);

        var order = await SaveStagedOrderAsync(repo, qty: 10m, rawQty: 20m, overhead: 10m, stdUnitCost: 4m, withByProduct: true);
        var rawLine = order.Lines.Single(l => l.LineKind == MfgLineKinds.Raw);
        var bpLine = order.Lines.Single(l => l.LineKind == MfgLineKinds.ByProduct);

        var confirmed = await repo.ConfirmAsync(Company, order.Id, Actor);
        Assert.Equal(MfgOrderStatuses.Confirmed, confirmed.Status);

        var issue1 = await repo.IssueAsync(Company, order.Id, new[] { new MfgIssueLine(rawLine.Id, 10m) }, Actor);
        Assert.Equal(20m, issue1.Amount);            // 10 KG at engine cost 2 (line's entered 999 ignored)
        Assert.Equal(20m, issue1.WipBalance);
        Assert.Equal(90m, OnHand(db, 1));

        var issue2 = await repo.IssueAsync(Company, order.Id, new[] { new MfgIssueLine(rawLine.Id, 10m) }, Actor);
        Assert.Equal(40m, issue2.WipBalance);
        Assert.Equal(80m, OnHand(db, 1));
        Assert.Equal(40m, GlNet(db, "MFG-ISSUE", order.Id, "1211"));   // Dr WIP
        Assert.Equal(-40m, GlNet(db, "MFG-ISSUE", order.Id, "1203"));  // Cr Inventory

        var finish = await repo.FinishAsync(Company, order.Id, 8m, new[] { new MfgReceiptLine(bpLine.Id, 2m) }, Actor);
        Assert.Equal(MfgOrderStatuses.InProcess, finish.Status);
        Assert.Equal(38.4m, finish.Amount);          // pool share for 8/10
        Assert.Equal(9.6m, finish.WipBalance);       // exact remainder
        Assert.Equal(8m, OnHand(db, 2));             // FG units
        Assert.Equal(34.56m, BalanceValue(db, 2));   // FG at accumulated cost less by-product share
        Assert.Equal(2m, OnHand(db, 3));             // by-product units
        Assert.Equal(3.84m, BalanceValue(db, 3));    // 10% cost share of the 38.4 pool share
        Assert.Equal(8m, GlNet(db, "MFG-OVHD", order.Id, "1211"));     // overhead absorbed pro-rata
        Assert.Equal(-8m, GlNet(db, "MFG-OVHD", order.Id, "409"));

        var close = await repo.CloseAsync(Company, order.Id, Actor);
        Assert.Equal(MfgOrderStatuses.Closed, close.Status);
        Assert.Equal(9.6m, close.Amount);            // variance = remaining WIP
        Assert.Equal(0m, close.WipBalance);
        Assert.Equal(9.6m, GlNet(db, "MFG-CLOSE", order.Id, "406"));   // Dr variance
        Assert.Equal(-9.6m, GlNet(db, "MFG-CLOSE", order.Id, "1211")); // Cr WIP

        var loaded = await repo.GetByDocNoAsync(Company, order.DocNo);
        Assert.NotNull(loaded);
        Assert.Equal(9.6m, loaded!.VarianceAmount);
        Assert.Equal(2.56m, loaded.StandardVariance); // 34.56 - 8 x 4.00
        Assert.Equal(0m, loaded.WipBalance);
    }

    // ------------------------------------------------------------------
    // (c) Cancel after partial issue reverses everything
    // ------------------------------------------------------------------

    [Fact]
    public async Task StagedOrder_CancelAfterPartialIssue_StockAndGlNetZero()
    {
        using var db = await SeededAsync();
        SeedMaterial(db, 1, "RM-1", "KG", 1m, null, 0m);
        SeedMaterial(db, 2, "FG-1", "PCS", 1m, null, 0m);
        SeedBalance(db, materialId: 1, qty: 100m, avg: 2m);
        var repo = new MfgOrderRepository("SQLite", db.ConnectionString);

        var order = await SaveStagedOrderAsync(repo, qty: 5m, rawQty: 10m, overhead: 0m, stdUnitCost: 0m, withByProduct: false);
        var rawLine = order.Lines.Single(l => l.LineKind == MfgLineKinds.Raw);
        await repo.ConfirmAsync(Company, order.Id, Actor);
        await repo.IssueAsync(Company, order.Id, new[] { new MfgIssueLine(rawLine.Id, 4m) }, Actor);
        Assert.Equal(96m, OnHand(db, 1));

        await repo.CancelAsync(Company, order.Id, Actor);
        Assert.Equal(100m, OnHand(db, 1));
        Assert.Equal(0m, GlNet(db, "MFG-ISSUE", order.Id, "1211", includeReversed: true));
        Assert.Equal(0m, GlNet(db, "MFG-ISSUE", order.Id, "1203", includeReversed: true));

        var loaded = await repo.GetByDocNoAsync(Company, order.DocNo);
        Assert.Equal(MfgOrderStatuses.Cancelled, loaded!.Status);
        Assert.Equal(0m, loaded.WipBalance);
    }

    // ------------------------------------------------------------------
    // (d) By-product at cost share; FG unit cost formula
    // ------------------------------------------------------------------

    [Fact]
    public async Task StagedOrder_CompletingFinish_ByProductShare_FgUnitCostFormula()
    {
        using var db = await SeededAsync();
        SeedMaterial(db, 1, "RM-1", "KG", 1m, null, 0m);
        SeedMaterial(db, 2, "FG-1", "PCS", 1m, null, 0m);
        SeedMaterial(db, 3, "BP-1", "PCS", 1m, null, 0m);
        SeedBalance(db, materialId: 1, qty: 100m, avg: 2m);
        var repo = new MfgOrderRepository("SQLite", db.ConnectionString);

        var order = await SaveStagedOrderAsync(repo, qty: 10m, rawQty: 20m, overhead: 10m, stdUnitCost: 0m, withByProduct: true);
        var rawLine = order.Lines.Single(l => l.LineKind == MfgLineKinds.Raw);
        var bpLine = order.Lines.Single(l => l.LineKind == MfgLineKinds.ByProduct);
        await repo.ConfirmAsync(Company, order.Id, Actor);
        await repo.IssueAsync(Company, order.Id, new[] { new MfgIssueLine(rawLine.Id, 20m) }, Actor);

        var finish = await repo.FinishAsync(Company, order.Id, 10m, new[] { new MfgReceiptLine(bpLine.Id, 2m) }, Actor);
        Assert.Equal(MfgOrderStatuses.Completed, finish.Status);
        Assert.Equal(0m, finish.WipBalance);
        // (materials 40 + overhead 10 - by-product share 5) / 10 = 4.5
        Assert.Equal(45m, BalanceValue(db, 2));
        Assert.Equal(5m, BalanceValue(db, 3));

        var loaded = await repo.GetByDocNoAsync(Company, order.DocNo);
        Assert.Equal(45m, loaded!.TotalCost);
        Assert.Equal(4.5m, loaded.UnitCost);
    }

    // ------------------------------------------------------------------
    // (e) Planning: netting + conversions
    // ------------------------------------------------------------------

    [Fact]
    public async Task Planning_Run_NetsStockAndIncoming_ConversionsCreateRealDocuments()
    {
        using var db = await SeededAsync();
        SeedMaterial(db, 1, "LEAF", "G", 1m, "KG", 1000m, avgCost: 0.01m);
        SeedMaterial(db, 2, "FIN", "PCS", 1m, null, 0m);
        SeedMaterial(db, 3, "RP-1", "PCS", 1m, null, 0m, minQty: 100m, reorderQty: 250m);
        SeedBalance(db, materialId: 1, qty: 300m, avg: 0.01m);
        SeedBalance(db, materialId: 2, qty: 2m, avg: 12.5m);
        SeedBalance(db, materialId: 3, qty: 40m, avg: 1m);
        var boms = new BomRepository("SQLite", db.ConnectionString);
        await boms.SaveAsync(Company, new Bom
        {
            MaterialCode = "FIN", Version = 1, IsActive = true, IsDefault = true,
            OutputQty = 1m, OutputUnit = "PCS",
            Lines = new List<BomLine> { new() { ComponentCode = "LEAF", Qty = 0.5m, UnitCode = "KG" } }
        }, Actor);
        SeedSalesOrder(db, "FIN", 5m, "PCS");
        SeedPurchaseOrder(db, "LEAF", 200m, "G");

        var planning = new PlanningRepository("SQLite", db.ConnectionString);
        var run = await planning.RunPlanningAsync(Company, Actor);

        var planned = Assert.Single(run.PlannedOrders);
        Assert.Equal("FIN", planned.MaterialCode);
        Assert.Equal(3m, planned.Qty);                       // 5 demanded - 2 on hand
        var leafSuggestion = run.Suggestions.Single(s => s.MaterialCode == "LEAF");
        Assert.Equal(1000m, leafSuggestion.Qty);             // 1500 required - 300 stock - 200 incoming
        var reorderSuggestion = run.Suggestions.Single(s => s.MaterialCode == "RP-1");
        Assert.Equal(210m, reorderSuggestion.Qty);           // max(250, 100-40) demand - 40 on hand

        var feasibility = await planning.CheckFeasibilityAsync(Company, "FIN", 1m);
        Assert.True(feasibility.CanBuild);                   // 500 G needed, 300 + 200 available
        var infeasible = await planning.CheckFeasibilityAsync(Company, "FIN", 5m);
        Assert.False(infeasible.CanBuild);
        Assert.Equal(2000m, infeasible.Rows.Single(r => r.MaterialCode == "LEAF").ShortageBaseQty);

        var orderId = await planning.ConvertPlannedOrderAsync(Company, planned.Id, Actor);
        var orders = new MfgOrderRepository("SQLite", db.ConnectionString);
        var converted = (await orders.GetAllAsync(Company)).Single(o => o.Id == orderId);
        Assert.Equal("FIN", converted.ItemCode);
        Assert.Equal(3m, converted.Qty);
        Assert.Equal(MfgOrderStatuses.Draft, converted.Status);
        Assert.Equal(planned.BomId, converted.BomId);
        Assert.Equal(1.5m, converted.Lines.Single(l => l.ItemCode == "LEAF").Qty);

        var (docId, docNo) = await planning.ConvertSuggestionAsync(Company, leafSuggestion.Id, Actor);
        Assert.True(docId > 0);
        Assert.False(string.IsNullOrWhiteSpace(docNo));
        Assert.Equal(1000m, PurchaseOrderLineQty(db, docId, "LEAF"));
    }

    // ------------------------------------------------------------------
    // Fixtures + helpers
    // ------------------------------------------------------------------

    private static async Task<MfgOrder> SaveStagedOrderAsync(
        MfgOrderRepository repo, decimal qty, decimal rawQty, decimal overhead, decimal stdUnitCost, bool withByProduct)
    {
        var order = new MfgOrder
        {
            DocDate = new DateOnly(2026, 3, 10),
            ItemCode = "FG-1",
            ItemName = "Finished",
            Qty = qty,
            UnitCode = "PCS",
            WarehouseCode = "1",
            OverheadAmount = overhead,
            StandardUnitCost = stdUnitCost,
            Lines = new List<MfgOrderLine>
            {
                new() { LineKind = MfgLineKinds.Raw, ItemCode = "RM-1", ItemName = "Raw", Qty = rawQty, UnitCode = "KG", UnitCost = 999m, StandardUnitCost = 2m }
            }
        };
        if (withByProduct)
            order.Lines.Add(new MfgOrderLine
            {
                LineKind = MfgLineKinds.ByProduct, ItemCode = "BP-1", ItemName = "By-product",
                Qty = 2m, UnitCode = "PCS", CostSharePct = 10m
            });
        await repo.SaveDraftAsync(Company, order, Actor);
        var loaded = await repo.GetByDocNoAsync(Company, order.DocNo);
        Assert.NotNull(loaded);
        return loaded!;
    }

    private static void SeedMaterial(TestDb db, long id, string code, string unit1, decimal unit1Factor,
        string? unit2, decimal unit2Factor, decimal avgCost = 0m, decimal minQty = 0m, decimal reorderQty = 0m)
    {
        Exec(db, $"""
            INSERT INTO materials (material_id, company_id, code, name_ar, name_en, is_stocked, avg_cost, is_deleted,
                unit1, unit1_factor, unit2, unit2_factor, min_qty, reorder_qty)
            VALUES ({id}, '{Company}', '{code}', '{code}', '{code}', 1, {Num(avgCost)}, 0,
                '{unit1}', {Num(unit1Factor)}, {(unit2 is null ? "NULL" : $"'{unit2}'")}, {Num(unit2Factor)}, {Num(minQty)}, {Num(reorderQty)});
            """);
    }

    private static void SeedBalance(TestDb db, long materialId, decimal qty, decimal avg)
    {
        // Opening stock goes through the REAL valuation engine (a receipt
        // movement at an exact total cost): on-hand in this codebase is
        // movement-derived, and the engine maintains balances + layers.
        using var connection = new SqliteConnection(db.ConnectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        StockValuationEngine.Apply(connection, transaction, "SQLite",
            new StockMovementRequest(Company, 1, materialId, "2026-01-01", "OPENING", $"OPEN-{materialId}",
                QtyIn: qty, QtyOut: 0m, ReceiptCostAmount: qty * avg, Note: "Test opening stock", Actor: Actor));
        transaction.Commit();
    }

    private static void SeedSalesOrder(TestDb db, string materialCode, decimal qty, string unit)
    {
        Exec(db, $"""
            INSERT INTO sales_documents (company_id, doc_kind, doc_no, doc_date, status, delivery_date)
            VALUES ('{db.CompanyId:D}', 'SO', 'SO-1', '2026-03-10', 'Posted', '2026-03-20');
            """);
        Exec(db, $"""
            INSERT INTO sales_document_lines (doc_id, line_no, material_code, description, unit, qty)
            VALUES ((SELECT doc_id FROM sales_documents WHERE doc_no = 'SO-1'), 1, '{materialCode}', '{materialCode}', '{unit}', {Num(qty)});
            """);
    }

    private static void SeedPurchaseOrder(TestDb db, string materialCode, decimal qty, string unit)
    {
        Exec(db, $"""
            INSERT INTO purchase_documents (company_id, doc_kind, doc_no, doc_date, status)
            VALUES ('{db.CompanyId:D}', 'PO', 'PO-1', '2026-03-10', 'Posted');
            """);
        Exec(db, $"""
            INSERT INTO purchase_document_lines (doc_id, line_no, material_code, description, unit, qty)
            VALUES ((SELECT doc_id FROM purchase_documents WHERE doc_no = 'PO-1'), 1, '{materialCode}', '{materialCode}', '{unit}', {Num(qty)});
            """);
    }

    private static decimal OnHand(TestDb db, long materialId)
        => Scalar(db, $"SELECT COALESCE(available_qty, 0) FROM material_stock_balances WHERE company_id = '{Company}' AND warehouse_id = 1 AND material_id = {materialId}");

    private static decimal BalanceValue(TestDb db, long materialId)
        => Scalar(db, $"SELECT COALESCE(available_qty * average_cost, 0) FROM material_stock_balances WHERE company_id = '{Company}' AND warehouse_id = 1 AND material_id = {materialId}");

    private static decimal PurchaseOrderLineQty(TestDb db, long docId, string materialCode)
        => Scalar(db, $"SELECT COALESCE(qty, 0) FROM purchase_document_lines WHERE doc_id = {docId} AND material_code = '{materialCode}'");

    /// <summary>Net (debit - credit) posted to one account by all journal
    /// entries of a staged source type for one order. With
    /// includeReversed, reversal entries (which carry the same source
    /// type via GlPosting's reverse path) are counted too, so a fully
    /// reversed flow nets to exactly zero.</summary>
    private static decimal GlNet(TestDb db, string sourceType, Guid orderId, string accountCode, bool includeReversed = false)
    {
        using var connection = new SqliteConnection(db.ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT COALESCE(SUM(l.debit - l.credit), 0)
            FROM journal_entry_lines l
            JOIN journal_entries e ON e.journal_entry_id = l.journal_entry_id
            JOIN chart_of_accounts a ON a.account_id = l.account_id
            WHERE e.source_doc_type = $type AND e.source_doc_id LIKE $prefix
              AND a.account_code = $account AND ($all = 1 OR e.status = 1);
            """;
        cmd.Parameters.AddWithValue("$type", sourceType);
        cmd.Parameters.AddWithValue("$prefix", orderId.ToString("D") + "%");
        cmd.Parameters.AddWithValue("$account", accountCode);
        cmd.Parameters.AddWithValue("$all", includeReversed ? 1 : 0);
        return Convert.ToDecimal(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static decimal Scalar(TestDb db, string sql)
    {
        using var connection = new SqliteConnection(db.ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? 0m : Convert.ToDecimal(value, CultureInfo.InvariantCulture);
    }

    private static void Exec(TestDb db, string sql)
    {
        using var connection = new SqliteConnection(db.ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string Num(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private sealed class TestDb : IDisposable
    {
        public required string Root { get; init; }
        public required string DbPath { get; init; }
        public required Guid CompanyId { get; init; }
        public string ConnectionString => $"Data Source={DbPath};";
        public void Dispose()
        {
            AppState.ClearCompany();
            SqliteConnection.ClearAllPools();
            Directory.Delete(Root, recursive: true);
        }
    }

    private static async Task<TestDb> SeededAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "mfg-build-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var dbPath = Path.Combine(root, "mfg.db");
        var companyId = Guid.NewGuid();
        var connectionString = $"Data Source={dbPath};";

        Assert.True((await SchemaMigrator.ApplyAllAsync("SQLite", dbPath)).Success);
        PointAppAt(dbPath);
        AppState.SetCompany(Company, "MFG Test Co", "MFG Test Co");

        var db = new TestDb { Root = root, DbPath = dbPath, CompanyId = companyId };
        Exec(db, $"""
            INSERT INTO companies (company_id, company_code, company_name_ar, company_name_en)
            VALUES ('{companyId:D}', '{Company}', 'MFG Test Co', 'MFG Test Co');
            """);
        Exec(db, """
            INSERT INTO warehouses (warehouse_id, company_id, warehouse_code, warehouse_name_ar, warehouse_name_en, is_active, is_deleted)
            VALUES (1, 'MFG1', 1, 'Main', 'Main', 1, 0);
            """);

        var structure = new AccountingStructureRepository("SQLite", connectionString);
        await structure.CreateFiscalYearAsync(companyId, 2026, Actor);
        foreach (var (code, name) in new[]
        {
            ("1203", "Inventory"), ("1211", "Work in Progress"), ("1212", "Finished Goods"),
            ("401", "COGS"), ("406", "Inventory Variances and Adjustments"),
            ("409", "Applied Manufacturing Overhead")
        })
        {
            AccRepo.Save(null, new AccRepo.AccRec(
                string.Empty, code, null, name, name, "Postable", "Active", "Low", "Unspecified",
                false, false, false, false, false, false, 0m, string.Empty, 0m, 0m, 0m),
                renumberDescendants: false);
        }
        return db;
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
