using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ShouTech.App.Services;
using ShouTech.Application.Services;
using ShouTech.Domain;
using ShouTech.Domain.Entities;
using ShouTech.Infrastructure;
using Xunit;

namespace Kern.Tests;

/// <summary>
/// Phase B 056 — Quality management (migration 056, QualityGate).
/// Pins, against real migrated SQLite databases, the exact posting-path
/// sequence StkPost.ApplyDocument performs:
///   * a receipt of a quality_check_required material lands in the
///     quarantine warehouse (never sellable) and auto-creates a Draft
///     inspection with the template lines snapshotted;
///   * PASS releases the quantity to sellable stock through the
///     valuation engine with value preserved; a partial pass on a
///     batch material splits the balances under the SAME batch id and
///     raises exactly one failure alert;
///   * serial identity survives quarantine + release;
///   * FAIL records the disposition and applies its stock effect
///     (return-to-supplier drains quarantine; nothing leaks sellable);
///   * Stop gate mode refuses a sale of quarantined stock with
///     INV-6012; Warn mode draws from quarantine and records a
///     GateWarning alert; the outgoing gate (Q4) blocks with INV-6013
///     in Stop mode and creates the pending inspection in Warn mode;
///   * non-QC materials behave exactly as before 056;
///   * overdue-inspection data returns the seeded overdue inspection.
/// Execution requires Windows (net8.0-windows testhost); the same
/// scenarios are verified headlessly by
/// ~/workspace/research/fix-step56/quality56_harness.py
/// (see docs/BUILD_PHASEB_056.md).
/// </summary>
public class QualityGateTests
{
    private const string Actor = "qc-test";
    private const string CO = "QC1";
    private const string Prov = "SQLite";
    private const string Date = "2026-11-18";

    [Fact]
    public async Task Receipt_OfQcMaterial_LandsInQuarantine_AndCreatesDraftInspection()
    {
        using var db = await SeededAsync();
        var template = await db.DefineTemplateAsync("M-QC");

        var quarantine = db.Receipt(db.Materials["M-QC"], 100m, "PUR-1", 10m);
        Assert.NotEqual(db.MainWh, quarantine);
        Assert.Equal(0m, db.Balance(db.MainWh, db.Materials["M-QC"]));
        Assert.Equal(100m, db.Balance(quarantine, db.Materials["M-QC"]));

        var pending = await db.Quality.GetInspectionsAsync(CO, pendingOnly: true);
        var inspection = Assert.Single(pending, i => i.RefDocNo == "PUR-1");
        Assert.Equal(100m, inspection.TotalQty);
        Assert.Equal(db.MainWh, inspection.WarehouseId);
        Assert.Equal(quarantine, inspection.QuarantineWarehouseId);
        var full = await db.Quality.GetInspectionAsync(CO, inspection.Id);
        Assert.NotNull(full);
        Assert.Single(full!.Lines);
        Assert.Equal(90m, full.Lines[0].MinValue);
        Assert.Equal(template, full.TemplateId);
    }

    [Fact]
    public async Task Pass_ReleasesQuantity_ToSellable_WithValuePreserved()
    {
        using var db = await SeededAsync();
        await db.DefineTemplateAsync("M-QC");
        var quarantine = db.Receipt(db.Materials["M-QC"], 100m, "PUR-1", 10m);
        var inspection = await db.PendingForAsync("PUR-1");
        var full = await db.Quality.GetInspectionAsync(CO, inspection.Id);
        full!.Lines[0].Readings.Add(new QualityReading { ReadingNo = 1, ReadingNum = 100m });
        await db.Quality.SaveResultsAsync(CO, full, Actor);

        var done = await db.Quality.CompleteAsync(CO, inspection.Id, 100m, 0m, null, Actor, null);
        Assert.Equal(QualityInspectionStatuses.Accepted, done.Status);
        Assert.Equal(100m, db.Balance(db.MainWh, db.Materials["M-QC"]));
        Assert.Equal(0m, db.Balance(quarantine, db.Materials["M-QC"]));
        Assert.Equal(1000m, db.ScalarDec(
            $"SELECT stock_value FROM material_stock_balances WHERE company_id = '{CO}' AND warehouse_id = {db.MainWh} AND material_id = {db.Materials["M-QC"]}"));
    }

    [Fact]
    public async Task PartialPass_OnBatch_SplitsBalances_KeepsBatchIdentity_AndAlerts()
    {
        using var db = await SeededAsync();
        var quarantine = db.Receipt(db.Materials["M-BATCH"], 50m, "PUR-2", 4m,
            batches: new[] { new BatchAllocationRequest("B-100", 50m) });
        var batchId = db.ScalarLong(
            $"SELECT batch_id FROM stock_batches WHERE company_id = '{CO}' AND batch_no = 'B-100'");
        Assert.Equal(50m, db.BatchBalance(quarantine, batchId));
        Assert.Equal(0m, db.BatchBalance(db.MainWh, batchId));

        var inspection = await db.PendingForAsync("PUR-2");
        Assert.Equal(batchId, inspection.BatchId);
        var done = await db.Quality.CompleteAsync(CO, inspection.Id, 30m, 20m,
            QualityDispositions.Scrap, Actor, null);
        Assert.Equal(QualityInspectionStatuses.PartiallyAccepted, done.Status);
        Assert.Equal(30m, db.BatchBalance(db.MainWh, batchId));
        Assert.Equal(0m, db.BatchBalance(quarantine, batchId));
        Assert.Equal(30m, db.Balance(db.MainWh, db.Materials["M-BATCH"]));
        Assert.NotNull(done.AlertId);
        Assert.Equal(1, db.ScalarLong(
            $"SELECT COUNT(*) FROM quality_alerts WHERE company_id = '{CO}' AND alert_type = 'FailedInspection' AND inspection_id = '{inspection.Id:D}'"));
    }

    [Fact]
    public async Task SerialIdentity_SurvivesQuarantineAndRelease()
    {
        using var db = await SeededAsync();
        var quarantine = db.Receipt(db.Materials["M-SERIAL"], 3m, "PUR-3", 25m,
            serials: new[] { new SerialSelection("S-1"), new SerialSelection("S-2"), new SerialSelection("S-3") });
        Assert.Equal(3, db.ScalarLong(
            $"SELECT COUNT(*) FROM stock_serials WHERE company_id = '{CO}' AND status = 'InStock' AND current_warehouse_id = {quarantine}"));
        var inspection = await db.PendingForAsync("PUR-3");
        await db.Quality.CompleteAsync(CO, inspection.Id, 3m, 0m, null, Actor, null);
        Assert.Equal(3, db.ScalarLong(
            $"SELECT COUNT(*) FROM stock_serials WHERE company_id = '{CO}' AND status = 'InStock' AND current_warehouse_id = {db.MainWh}"));
    }

    [Fact]
    public async Task Fail_ReturnToSupplier_RecordsDisposition_AndDrainsQuarantine()
    {
        using var db = await SeededAsync();
        var quarantine = db.Receipt(db.Materials["M-QC2"], 40m, "PUR-4", 5m);
        var inspection = await db.PendingForAsync("PUR-4");
        var done = await db.Quality.CompleteAsync(CO, inspection.Id, 0m, 40m,
            QualityDispositions.ReturnToSupplier, Actor, "defective");
        Assert.Equal(QualityInspectionStatuses.Rejected, done.Status);
        Assert.Equal(0m, db.Balance(db.MainWh, db.Materials["M-QC2"]));
        Assert.Equal(0m, db.Balance(quarantine, db.Materials["M-QC2"]));
        var stored = await db.Quality.GetInspectionAsync(CO, inspection.Id);
        Assert.Equal(QualityDispositions.ReturnToSupplier, stored!.Disposition);
        Assert.Equal(40m, stored.RejectedQty);
    }

    [Fact]
    public async Task StopMode_RefusesSaleOfQuarantinedStock_WarnMode_DrawsWithAlert()
    {
        using var db = await SeededAsync();
        db.Receipt(db.Materials["M-QC3"], 25m, "PUR-6", 8m);
        var quarantine = db.QuarantineWh();

        await db.Quality.SetGateModeAsync(CO, QualityGateModes.Stop, Actor);
        Assert.Equal(ErrorCodes.QualityQuarantinedSale, db.Sale(db.Materials["M-QC3"], 10m, "SAL-1"));
        Assert.Equal(25m, db.Balance(quarantine, db.Materials["M-QC3"]));

        await db.Quality.SetGateModeAsync(CO, QualityGateModes.Warn, Actor);
        Assert.Null(db.Sale(db.Materials["M-QC3"], 10m, "SAL-2"));
        Assert.Equal(15m, db.Balance(quarantine, db.Materials["M-QC3"]));
        Assert.True(db.ScalarLong(
            $"SELECT COUNT(*) FROM quality_alerts WHERE company_id = '{CO}' AND alert_type = 'GateWarning'") >= 1);
    }

    [Fact]
    public async Task OutgoingGate_StopBlocks_WarnCreatesInspection_AcceptedUnblocks()
    {
        using var db = await SeededAsync();
        db.DirectReceipt(db.Materials["M-OUT"], 20m, 3m);

        await db.Quality.SetGateModeAsync(CO, QualityGateModes.Stop, Actor);
        Assert.Equal(ErrorCodes.QualityOutgoingInspectionRequired,
            db.Sale(db.Materials["M-OUT"], 5m, "SAL-OUT-1"));

        await db.Quality.SetGateModeAsync(CO, QualityGateModes.Warn, Actor);
        Assert.Null(db.Sale(db.Materials["M-OUT"], 5m, "SAL-OUT-1"));
        var outgoing = (await db.Quality.GetInspectionsAsync(CO, pendingOnly: true))
            .Single(i => i.RefDocNo == "SAL-OUT-1" && i.InspectionType == QualityInspectionTypes.Outgoing);
        await db.Quality.CompleteAsync(CO, outgoing.Id, 0m, 0m, null, Actor, null);

        await db.Quality.SetGateModeAsync(CO, QualityGateModes.Stop, Actor);
        Assert.Null(db.Sale(db.Materials["M-OUT"], 5m, "SAL-OUT-1"));
    }

    [Fact]
    public async Task NonQcMaterial_BehavesExactlyAsBefore056()
    {
        using var db = await SeededAsync();
        var warehouse = db.Receipt(db.Materials["M-PLAIN"], 70m, "PUR-7", 2m);
        Assert.Equal(db.MainWh, warehouse);
        Assert.Equal(70m, db.Balance(db.MainWh, db.Materials["M-PLAIN"]));
        Assert.Equal(0, db.ScalarLong(
            $"SELECT COUNT(*) FROM quality_inspections WHERE company_id = '{CO}' AND material_id = {db.Materials["M-PLAIN"]}"));
        Assert.Null(db.Sale(db.Materials["M-PLAIN"], 70m, "SAL-PLAIN"));
    }

    [Fact]
    public async Task OverdueInspections_ReturnTheSeededOverdueInspection()
    {
        using var db = await SeededAsync();
        db.Exec($"""
            INSERT INTO quality_inspections
                (inspection_id, company_id, inspection_type, ref_doc_kind, ref_doc_id, ref_doc_no,
                 material_id, warehouse_id, total_qty, sample_qty, status, due_date, created_by)
            VALUES ('{Guid.NewGuid():D}', '{CO}', 'Incoming', 'PUR', '77', 'OLD-1',
                    {db.Materials["M-PLAIN"]}, {db.MainWh}, 5, 5, 'Draft', '2020-01-01', 'seed');
            """);
        var overdue = await db.Quality.GetOverdueInspectionsAsync(CO, "2026-11-18");
        Assert.Contains(overdue, i => i.RefDocNo == "OLD-1");
        Assert.All(overdue, i => Assert.Equal(QualityInspectionStatuses.Draft, i.Status));
    }

    [Fact]
    public void EvaluateLine_AppliesBandAcceptedValueAndRecordedResult()
    {
        Assert.Equal("Pass", QualityGate.EvaluateLine(new QualityLineEvaluationInput(
            "Numeric", 90m, 110m, null,
            new[] { new QualityReading { ReadingNo = 1, ReadingNum = 100m } }, null)));
        Assert.Equal("Fail", QualityGate.EvaluateLine(new QualityLineEvaluationInput(
            "Numeric", 90m, 110m, null,
            new[] { new QualityReading { ReadingNo = 1, ReadingNum = 130m } }, null)));
        Assert.Equal("Pass", QualityGate.EvaluateLine(new QualityLineEvaluationInput(
            "Value", null, null, "OK",
            new[] { new QualityReading { ReadingNo = 1, ReadingText = "ok" } }, null)));
        Assert.Null(QualityGate.EvaluateLine(new QualityLineEvaluationInput(
            "PassFail", null, null, null, Array.Empty<QualityReading>(), null)));
        Assert.Equal("Fail", QualityGate.EvaluateLine(new QualityLineEvaluationInput(
            "PassFail", null, null, null, Array.Empty<QualityReading>(), "Fail")));
    }

    // ------------------------------------------------------------------
    // Fixture + posting-path simulations (the StkPost gate sequence)
    // ------------------------------------------------------------------
    private sealed class TestDb : IDisposable
    {
        public required string Root { get; init; }
        public required string DbPath { get; init; }
        public required long MainWh { get; init; }
        public required Dictionary<string, long> Materials { get; init; }
        public required QualityService Quality { get; init; }
        public string Cs => $"Data Source={DbPath};";

        public long Receipt(long materialId, decimal qty, string docNo, decimal unitCost,
            IReadOnlyList<BatchAllocationRequest>? batches = null,
            IReadOnlyList<SerialSelection>? serials = null)
        {
            using var c = new SqliteConnection(Cs);
            c.Open();
            using var tx = c.BeginTransaction();
            var profile = QualityGate.LoadProfile(c, tx, Prov, CO, materialId);
            var eff = MainWh;
            var diverted = false;
            if (profile.QualityCheckRequired)
            {
                var q = QualityGate.EnsureQuarantineWarehouse(c, tx, Prov, CO);
                if (q > 0) { eff = q; diverted = true; }
            }
            var res = StockValuationEngine.Apply(c, tx, Prov, new StockMovementRequest(
                CO, eff, materialId, Date, "PUR", docNo, qty, 0m,
                ReceiptUnitCost: unitCost, BatchAllocations: batches, Serials: serials,
                PartyId: 42, Actor: Actor));
            if (diverted)
            {
                var batchId = res.AllocationRecords.FirstOrDefault(a => a.BatchId is not null)?.BatchId;
                QualityGate.EnsureInspection(c, tx, Prov, CO, QualityInspectionTypes.Incoming,
                    "PUR", "1", docNo, materialId, batchId, MainWh, eff, 42, qty, Date, Actor);
            }
            tx.Commit();
            return eff;
        }

        public void DirectReceipt(long materialId, decimal qty, decimal unitCost)
        {
            using var c = new SqliteConnection(Cs);
            c.Open();
            using var tx = c.BeginTransaction();
            StockValuationEngine.Apply(c, tx, Prov, new StockMovementRequest(
                CO, MainWh, materialId, Date, "PUR", "PUR-DIRECT", qty, 0m,
                ReceiptUnitCost: unitCost, Actor: Actor));
            tx.Commit();
        }

        public string? Sale(long materialId, decimal qty, string docNo)
        {
            using var c = new SqliteConnection(Cs);
            c.Open();
            using var tx = c.BeginTransaction();
            try
            {
                var profile = QualityGate.LoadProfile(c, tx, Prov, CO, materialId);
                if (profile.OutgoingRequired)
                    QualityGate.CheckOutgoing(c, tx, Prov, CO, "SAL", "9", docNo, materialId, MainWh, 7, Date, Actor);
                var draw = QualityGate.PlanQuarantineDraw(c, tx, Prov, CO, MainWh, materialId, qty, docNo, Actor);
                if (draw > 0)
                {
                    var qWh = QualityGate.FindQuarantineWarehouse(c, tx, Prov, CO)!.Value;
                    var sellablePart = qty - draw;
                    if (sellablePart > 0)
                        StockValuationEngine.Apply(c, tx, Prov, new StockMovementRequest(
                            CO, MainWh, materialId, Date, "SAL", docNo, 0m, sellablePart, Actor: Actor));
                    StockValuationEngine.Apply(c, tx, Prov, new StockMovementRequest(
                        CO, qWh, materialId, Date, "SAL", docNo, 0m, draw, AutoAllocateSerials: true, Actor: Actor));
                }
                else
                {
                    StockValuationEngine.Apply(c, tx, Prov, new StockMovementRequest(
                        CO, MainWh, materialId, Date, "SAL", docNo, 0m, qty, Actor: Actor));
                }
                tx.Commit();
                return null;
            }
            catch (AppError ex)
            {
                tx.Rollback();
                return ex.Code;
            }
        }

        public async Task<Guid> DefineTemplateAsync(string materialCode)
        {
            var param = new QualityParameter
            { Code = "TEMP", NameEn = "Temperature", ParamType = QualityParamTypes.Numeric, Unit = "C" };
            await Quality.SaveParameterAsync(CO, param, Actor);
            var template = new QualityTemplate
            {
                Code = "T-IN", NameEn = "Incoming standard", InspectionType = QualityInspectionTypes.Incoming,
                Lines = new List<QualityTemplateLine>
                { new() { ParamId = param.Id, MinValue = 90m, MaxValue = 110m, IsMandatory = true, Sort = 0 } }
            };
            await Quality.SaveTemplateAsync(CO, template, Actor);
            await Quality.SetMaterialQualityAsync(CO, Materials[materialCode], true, false, template.Id, null, Actor);
            return template.Id;
        }

        public async Task<QualityInspection> PendingForAsync(string docNo)
            => (await Quality.GetInspectionsAsync(CO, pendingOnly: true)).Single(i => i.RefDocNo == docNo);

        public long QuarantineWh() => ScalarLong(
            $"SELECT warehouse_id FROM warehouses WHERE company_id = '{CO}' AND is_quarantine = 1");
        public decimal Balance(long warehouseId, long materialId) => ScalarDec(
            $"SELECT COALESCE(available_qty, 0) FROM material_stock_balances WHERE company_id = '{CO}' AND warehouse_id = {warehouseId} AND material_id = {materialId} AND is_deleted = 0");
        public decimal BatchBalance(long warehouseId, long batchId) => ScalarDec(
            $"SELECT COALESCE(qty_on_hand, 0) FROM stock_batch_balances WHERE company_id = '{CO}' AND warehouse_id = {warehouseId} AND batch_id = {batchId}");
        public long ScalarLong(string sql)
        {
            using var c = new SqliteConnection(Cs);
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
        public decimal ScalarDec(string sql)
        {
            using var c = new SqliteConnection(Cs);
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToDecimal(cmd.ExecuteScalar());
        }
        public void Exec(string sql)
        {
            using var c = new SqliteConnection(Cs);
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(Root, recursive: true);
        }
    }

    private static async Task<TestDb> SeededAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "quality-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var dbPath = Path.Combine(root, "quality.db");
        var cs = $"Data Source={dbPath};";
        Assert.True((await SchemaMigrator.ApplyAllAsync("SQLite", dbPath)).Success);

        long mainWh;
        var materials = new Dictionary<string, long>();
        using (var c = new SqliteConnection(cs))
        {
            c.Open();
            void Exec(string sql)
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
            long Scalar(string sql)
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = sql;
                return Convert.ToInt64(cmd.ExecuteScalar());
            }
            Exec($"INSERT INTO warehouses (company_id, warehouse_name_ar, warehouse_name_en, is_active) VALUES ('{CO}', 'Main', 'Main', 1);");
            mainWh = Scalar($"SELECT warehouse_id FROM warehouses WHERE company_id = '{CO}'");
            foreach (var (code, qc, batch, serial, outgoing) in new[]
            {
                ("M-QC", 1, 0, 0, 0), ("M-QC2", 1, 0, 0, 0), ("M-QC3", 1, 0, 0, 0),
                ("M-BATCH", 1, 1, 0, 0), ("M-SERIAL", 1, 0, 1, 0),
                ("M-PLAIN", 0, 0, 0, 0), ("M-OUT", 0, 0, 0, 1)
            })
            {
                Exec($"""
                    INSERT INTO materials (company_id, code, name_ar, name_en, cost_method,
                                           quality_check_required, has_batch, has_serial, qc_outgoing_required)
                    VALUES ('{CO}', '{code}', '{code}', '{code}', 'WeightedAvg', {qc}, {batch}, {serial}, {outgoing});
                    """);
                materials[code] = Scalar($"SELECT material_id FROM materials WHERE company_id = '{CO}' AND code = '{code}'");
            }
        }

        return new TestDb
        {
            Root = root,
            DbPath = dbPath,
            MainWh = mainWh,
            Materials = materials,
            Quality = new QualityService(new QualityRepository(Prov, cs))
        };
    }
}
