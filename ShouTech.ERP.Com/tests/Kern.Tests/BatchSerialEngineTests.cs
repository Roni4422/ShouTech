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
/// Phase B 054 — batch/serial tracking (migration 054). Pins, against
/// real migrated SQLite databases, the engine's allocation contract:
///   * FEFO issue allocation (earliest expiry first, receipt order as
///     tie-break) with allocations recorded and per-batch balances exact;
///   * serial lifecycle: receipt of named serials, issue of one named
///     serial, double-issue refused with INV-6007, duplicate receipt
///     refused with INV-6008;
///   * a linked return re-enters the ORIGINAL batch;
///   * an untracked material posts exactly as the pre-054 baseline
///     (no allocation rows at all);
///   * a tracked receipt without batch data fails with INV-6005 and
///     writes nothing;
///   * an expired batch cannot be issued (INV-6010) unless the
///     recorded override is asserted;
///   * BatchSerialService.GetExpiredBatches (the ExpiredWin data rule)
///     returns exactly the expired + near-expiry batches with stock.
/// Execution requires Windows (net8.0-windows testhost); the same
/// scenarios are verified headlessly on Linux by the 054 harness
/// (see docs/BUILD_PHASEB_054.md).
/// </summary>
public class BatchSerialEngineTests
{
    private const string CompanyCode = "B54";

    [Fact]
    public async Task BatchIssue_AllocatesFefo_AllocationsAndBatchBalancesExact()
    {
        using var db = await SeededAsync();
        SeedMaterial(db, 61, hasBatch: true, hasExpiry: true, shelfLifeDays: 365, alertDays: 30);

        // Receipt order is LATE-expiry first: FIFO would take LATE,
        // FEFO must take EARLY first.
        Apply(db, Req(61, "2026-01-01", "PUR", "P-1", qtyIn: 10m, receiptCost: 10m,
            batches: new[] { new BatchAllocationRequest("B-LATE", 10m, "2027-06-01") }));
        Apply(db, Req(61, "2026-01-02", "PUR", "P-2", qtyIn: 10m, receiptCost: 10m,
            batches: new[] { new BatchAllocationRequest("B-EARLY", 10m, "2026-06-01") }));

        var issue = Apply(db, Req(61, "2026-01-03", "SAL", "S-1", qtyOut: 12m));
        var allocs = issue.AllocationRecords;
        Assert.Equal(2, allocs.Count);
        Assert.Equal("B-EARLY", allocs[0].BatchNo);
        Assert.Equal(-10m, allocs[0].Qty);
        Assert.Equal("B-LATE", allocs[1].BatchNo);
        Assert.Equal(-2m, allocs[1].Qty);

        Assert.Equal(0m, BatchBalance(db, "B-EARLY"));
        Assert.Equal(8m, BatchBalance(db, "B-LATE"));
        var (qty, _) = Balance(db, 61);
        Assert.Equal(8m, qty);
    }

    [Fact]
    public async Task SerialIssue_NamedSerialOut_OthersRemain_DoubleIssueRefused()
    {
        using var db = await SeededAsync();
        SeedMaterial(db, 62, hasSerial: true);
        Apply(db, Req(62, "2026-01-01", "PUR", "P-1", qtyIn: 3m, receiptCost: 50m,
            serials: new[] { new SerialSelection("SN-1"), new SerialSelection("SN-2"), new SerialSelection("SN-3") }));

        var issue = Apply(db, Req(62, "2026-01-02", "SAL", "S-1", qtyOut: 1m,
            serials: new[] { new SerialSelection("SN-2") }));
        Assert.Equal("SN-2", issue.AllocationRecords.Single().SerialNo);
        Assert.Equal(-1m, issue.AllocationRecords.Single().Qty);

        // BatchSerialService reads committed data only, so statuses
        // are asserted through the test transaction here; the service
        // direction is covered by the trace/expiry facts below.
        Assert.Equal("Delivered", SerialStatus(db, "SN-2"));
        Assert.Equal("InStock", SerialStatus(db, "SN-1"));
        Assert.Equal("InStock", SerialStatus(db, "SN-3"));

        var ex = Assert.Throws<AppError>(() =>
            Apply(db, Req(62, "2026-01-03", "SAL", "S-2", qtyOut: 1m,
                serials: new[] { new SerialSelection("SN-2") })));
        Assert.Equal(ErrorCodes.SerialUnavailable, ex.Code);
    }

    [Fact]
    public async Task PurchaseReturn_UsesSelectedBatchAndSerialFromExactReceiptLine()
    {
        using var db = await SeededAsync();
        SeedMaterial(db, 64, hasBatch: true, hasSerial: true);

        Apply(db, Req(64, "2026-01-01", "RCV", "RCV-TRACK-01", qtyIn: 1m, receiptCost: 50m,
            batches: new[] { new BatchAllocationRequest("B-LINE-1", 1m) },
            serials: new[] { new SerialSelection("SN-LINE-1", "B-LINE-1") },
            sourceLineId: 101));
        Apply(db, Req(64, "2026-01-01", "RCV", "RCV-TRACK-01", qtyIn: 1m, receiptCost: 50m,
            batches: new[] { new BatchAllocationRequest("B-LINE-2", 1m) },
            serials: new[] { new SerialSelection("SN-LINE-2", "B-LINE-2") },
            sourceLineId: 102));

        var returned = Apply(db, Req(64, "2026-01-02", "RCR", "RCR-TRACK-01", qtyOut: 1m,
            batches: new[] { new BatchAllocationRequest("B-LINE-1", 1m) },
            serials: new[] { new SerialSelection("SN-LINE-1", "B-LINE-1") },
            originalDocNo: "RCV-TRACK-01",
            sourceLineId: 201, originalSourceLineId: 101));

        Assert.Contains(returned.AllocationRecords, x => x.BatchNo == "B-LINE-1" && x.Qty == -1m);
        Assert.Contains(returned.AllocationRecords, x => x.SerialNo == "SN-LINE-1" && x.Qty == -1m);
        Assert.Equal(0m, BatchBalance(db, "B-LINE-1"));
        Assert.Equal(1m, BatchBalance(db, "B-LINE-2"));
        Assert.Equal("Delivered", SerialStatus(db, "SN-LINE-1"));
        Assert.Equal("InStock", SerialStatus(db, "SN-LINE-2"));
        Assert.Equal(101m, Scalar(db,
            "SELECT original_source_document_line_id FROM material_stock_movements WHERE document_no = 'RCR-TRACK-01';"));
    }

    [Fact]
    public async Task SerialReceipt_DuplicateSerial_RefusedWith6008()
    {
        using var db = await SeededAsync();
        SeedMaterial(db, 63, hasSerial: true);
        Apply(db, Req(63, "2026-01-01", "PUR", "P-1", qtyIn: 1m, receiptCost: 50m,
            serials: new[] { new SerialSelection("SN-X") }));
        var ex = Assert.Throws<AppError>(() =>
            Apply(db, Req(63, "2026-01-02", "PUR", "P-2", qtyIn: 1m, receiptCost: 50m,
                serials: new[] { new SerialSelection("SN-X") })));
        Assert.Equal(ErrorCodes.SerialDuplicate, ex.Code);
    }

    [Fact]
    public async Task SalesReturn_ReEntersOriginalBatch()
    {
        using var db = await SeededAsync();
        SeedMaterial(db, 64, hasBatch: true, hasExpiry: true, shelfLifeDays: 365, alertDays: 30);
        Apply(db, Req(64, "2026-01-01", "PUR", "P-1", qtyIn: 10m, receiptCost: 10m,
            batches: new[] { new BatchAllocationRequest("B-1", 10m, "2027-01-01") }));
        Apply(db, Req(64, "2026-01-02", "SAL", "S-1", qtyOut: 4m));
        Assert.Equal(6m, BatchBalance(db, "B-1"));

        var ret = Apply(db, Req(64, "2026-01-03", "SRT", "SRT-1", qtyIn: 2m,
            originalDocNo: "S-1"));
        Assert.Equal("B-1", ret.AllocationRecords.Single().BatchNo);
        Assert.Equal(2m, ret.AllocationRecords.Single().Qty);
        Assert.Equal(8m, BatchBalance(db, "B-1"));
    }

    [Fact]
    public async Task UntrackedMaterial_PostsExactlyAsBaseline_NoAllocations()
    {
        using var db = await SeededAsync();
        SeedMaterial(db, 65);
        Apply(db, Req(65, "2026-01-01", "PUR", "P-1", qtyIn: 10m, receiptCost: 10m));
        var issue = Apply(db, Req(65, "2026-01-02", "SAL", "S-1", qtyOut: 4m));
        Assert.Empty(issue.AllocationRecords);
        Assert.Equal(0m, Scalar(db, "SELECT COUNT(*) FROM stock_movement_allocations"));
        var (qty, value) = Balance(db, 65);
        Assert.Equal(6m, qty);
        Assert.Equal(60m, value);
    }

    [Fact]
    public async Task TrackedReceipt_WithoutBatchData_Fails6005_AndWritesNothing()
    {
        using var db = await SeededAsync();
        SeedMaterial(db, 66, hasBatch: true, hasExpiry: true, shelfLifeDays: 365, alertDays: 30);
        var ex = Assert.Throws<AppError>(() =>
            Apply(db, Req(66, "2026-01-01", "PUR", "P-1", qtyIn: 5m, receiptCost: 10m)));
        Assert.Equal(ErrorCodes.BatchSerialRequired, ex.Code);
        db.Tx.Rollback();

        // After the posting transaction rolled back, nothing persisted:
        // no movement, no balance, no batch, no allocation.
        using var connection = new SqliteConnection($"Data Source={db.DbPath};");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT (SELECT COUNT(*) FROM material_stock_movements),"
            + " (SELECT COUNT(*) FROM material_stock_balances),"
            + " (SELECT COUNT(*) FROM stock_batches),"
            + " (SELECT COUNT(*) FROM stock_movement_allocations);";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(0, reader.GetInt32(0));
        Assert.Equal(0, reader.GetInt32(1));
        Assert.Equal(0, reader.GetInt32(2));
        Assert.Equal(0, reader.GetInt32(3));
        // TestDb.Dispose already removed the temp root.
    }

    [Fact]
    public async Task ExpiredBatch_IssueBlocked6010_OverrideAllows()
    {
        using var db = await SeededAsync();
        SeedMaterial(db, 67, hasBatch: true, hasExpiry: true, shelfLifeDays: 365, alertDays: 30);
        Apply(db, Req(67, "2026-01-01", "PUR", "P-1", qtyIn: 5m, receiptCost: 10m,
            batches: new[] { new BatchAllocationRequest("B-EXP", 5m, "2026-01-10") }));

        var ex = Assert.Throws<AppError>(() =>
            Apply(db, Req(67, "2026-02-01", "SAL", "S-1", qtyOut: 2m)));
        Assert.Equal(ErrorCodes.BatchExpired, ex.Code);

        var ok = Apply(db, Req(67, "2026-02-01", "SAL", "S-2", qtyOut: 2m, allowExpired: true));
        Assert.Equal(-2m, ok.AllocationRecords.Single().Qty);
        Assert.Equal(3m, BatchBalance(db, "B-EXP"));
    }

    [Fact]
    public async Task BatchReceipt_ExpiryDefaultsToReceiptPlusShelfLife()
    {
        using var db = await SeededAsync();
        SeedMaterial(db, 68, hasBatch: true, hasExpiry: true, shelfLifeDays: 90, alertDays: 10);
        Apply(db, Req(68, "2026-01-01", "PUR", "P-1", qtyIn: 5m, receiptCost: 10m,
            batches: new[] { new BatchAllocationRequest("B-D", 5m) }));
        Assert.Equal("2026-04-01", Text(db,
            "SELECT expiry_date FROM stock_batches WHERE batch_no = 'B-D'"));
    }

    [Fact]
    public async Task ExpiredBatches_ServiceReturnsExactlyExpiredAndNearExpiry()
    {
        var db = await SeededAsync();
        SeedMaterial(db, 69, hasBatch: true, hasExpiry: true, shelfLifeDays: 365, alertDays: 30);
        Apply(db, Req(69, "2025-12-01", "PUR", "P-1", qtyIn: 5m, receiptCost: 10m,
            batches: new[] { new BatchAllocationRequest("B-EXP", 5m, "2026-01-15") }));
        Apply(db, Req(69, "2025-12-01", "PUR", "P-2", qtyIn: 3m, receiptCost: 10m,
            batches: new[] { new BatchAllocationRequest("B-NEAR", 3m, "2026-02-10") }));
        Apply(db, Req(69, "2025-12-01", "PUR", "P-3", qtyIn: 7m, receiptCost: 10m,
            batches: new[] { new BatchAllocationRequest("B-FAR", 7m, "2027-01-01") }));
        db.Commit();

        var service = new BatchSerialService("SQLite", db.ConnectionString);
        var rows = service.GetExpiredBatches(CompanyCode, new DateOnly(2026, 2, 1));
        Assert.Equal(2, rows.Count);
        var expired = Assert.Single(rows, r => r.BatchNo == "B-EXP");
        Assert.True(expired.IsExpired);
        Assert.Equal(5m, expired.QtyOnHand);
        Assert.Equal(-17, expired.DaysRemaining);
        var near = Assert.Single(rows, r => r.BatchNo == "B-NEAR");
        Assert.False(near.IsExpired);
        Assert.Equal(3m, near.QtyOnHand);
        Assert.Equal(9, near.DaysRemaining);
        db.Dispose();
    }

    [Fact]
    public async Task Trace_BothDirections_AndRecallList()
    {
        var db = await SeededAsync();
        SeedMaterial(db, 70, hasBatch: true, hasExpiry: true, shelfLifeDays: 365, alertDays: 30);
        var receipt = Apply(db, Req(70, "2026-01-01", "PUR", "P-1", qtyIn: 10m, receiptCost: 10m,
            batches: new[] { new BatchAllocationRequest("B-T", 10m, "2027-01-01") }));
        var issue = Apply(db, Req(70, "2026-01-02", "SAL", "S-1", qtyOut: 3m));
        db.Commit();

        var service = new BatchSerialService("SQLite", db.ConnectionString);
        var batchId = receipt.AllocationRecords.Single().BatchId!.Value;
        var timeline = service.TraceBatch(CompanyCode, batchId);
        Assert.Equal(2, timeline.Count);
        Assert.Equal("P-1", timeline[0].DocumentNo);
        Assert.Equal("S-1", timeline[1].DocumentNo);
        var movementAllocs = service.GetMovementAllocations(issue.MovementId);
        Assert.Equal(batchId, movementAllocs.Single().BatchId);
        Assert.Equal(-3m, movementAllocs.Single().Qty);
        Assert.Single(service.GetBatchRecallList(CompanyCode, batchId));
        db.Dispose();
    }

    // ------------------------------------------------------------------
    // W10 (migration 105): warranty registration derives from the sold
    // serial records above; claims enforce SAL-7026.
    // ------------------------------------------------------------------
    [Fact]
    public async Task Warranty_RegisterFromDocument_UsesCardTerm_IsIdempotent_AndClaimsGateOnExpiry()
    {
        var db = await SeededAsync();
        SeedMaterial(db, 71, hasSerial: true);
        ExecSql(db, "UPDATE materials SET warranty_months = 24 WHERE material_id = 71;");
        Apply(db, Req(71, "2026-01-10", "PUR", "P-W1", qtyIn: 2m, receiptCost: 50m,
            serials: new[] { new SerialSelection("SN-W1"), new SerialSelection("SN-W2") }));
        // The posted invoice the serials leave on (header row only —
        // the scan keys on the document number + its serial movements).
        ExecSql(db, """
            INSERT INTO sales_documents (company_id, doc_kind, doc_no, doc_date, status, party_id, party_name)
            VALUES ('B54', 'SAL', 'S-W1', '2026-01-15', 'Posted', 7, 'Customer One');
            """);
        Apply(db, Req(71, "2026-01-15", "SAL", "S-W1", qtyOut: 2m,
            serials: new[] { new SerialSelection("SN-W1"), new SerialSelection("SN-W2") }));
        db.Commit();

        var service = new WarrantyService("SQLite", db.ConnectionString);
        var first = service.RegisterFromDocument(CompanyCode, null, "S-W1", 12, null, "w10-test");
        Assert.Equal(2, first.Registered);
        Assert.Equal(0, first.Skipped);

        var warranties = service.ListWarranties(CompanyCode);
        Assert.Equal(2, warranties.Count);
        var w1 = Assert.Single(warranties, w => w.SerialNo == "SN-W1");
        Assert.Equal(24, w1.WarrantyMonths);            // card term wins over the 12-month default
        Assert.Equal("2026-01-15", w1.SaleDate);
        Assert.Equal("2028-01-15", w1.ExpiryDate);      // sale date + 24 months, computed once
        Assert.Equal(7, w1.PartyId);
        Assert.Equal("S-W1", w1.SourceDocNo);
        Assert.Equal(WarrantyStatuses.Active, w1.EffectiveStatus(new DateOnly(2027, 1, 1)));
        Assert.Equal(WarrantyStatuses.Expired, w1.EffectiveStatus(new DateOnly(2028, 2, 1)));

        // Idempotent: a re-scan registers nothing twice.
        var second = service.RegisterFromDocument(CompanyCode, null, "S-W1", 12, null, "w10-test");
        Assert.Equal(0, second.Registered);
        Assert.Equal(2, second.Skipped);
        Assert.Equal(2, service.ListWarranties(CompanyCode).Count);

        // A claim inside cover files and decides; deciding twice is SAL-7026.
        var claimId = service.FileClaim(CompanyCode, w1.Id, "Fan stopped", "2026-06-01", "w10-test");
        service.DecideClaim(CompanyCode, claimId, true, WarrantyClaimActions.Repair, "Fan replaced", "w10-test");
        var claim = Assert.Single(service.ListClaims(CompanyCode, w1.Id));
        Assert.Equal(WarrantyClaimDecisions.Approved, claim.Decision);
        Assert.Equal(WarrantyClaimActions.Repair, claim.Action);
        var redecide = Assert.Throws<AppError>(() =>
            service.DecideClaim(CompanyCode, claimId, true, WarrantyClaimActions.Repair, "again", "w10-test"));
        Assert.Equal(ErrorCodes.WarrantyClaimInvalid, redecide.Code);

        // A claim dated after expiry is SAL-7026.
        var expired = Assert.Throws<AppError>(() =>
            service.FileClaim(CompanyCode, w1.Id, "Too late", "2028-02-01", "w10-test"));
        Assert.Equal(ErrorCodes.WarrantyClaimInvalid, expired.Code);
        db.Dispose();
    }

    [Fact]
    public async Task Warranty_ManualRegistration_Void_AndReturnClaimLink()
    {
        var db = await SeededAsync();
        SeedMaterial(db, 72, hasSerial: true);
        Apply(db, Req(72, "2026-02-01", "PUR", "P-W2", qtyIn: 1m, receiptCost: 80m,
            serials: new[] { new SerialSelection("SN-W3") }));
        Apply(db, Req(72, "2026-02-03", "SAL", "S-W2", qtyOut: 1m,
            serials: new[] { new SerialSelection("SN-W3") }));
        db.Commit();

        var service = new WarrantyService("SQLite", db.ConnectionString);
        var manual = service.RegisterManual(CompanyCode, "SN-W3", 6, null, "w10-test");
        Assert.Equal("S-W2", manual.SourceDocNo);       // from the serial's last outbound movement
        Assert.Equal("2026-02-03", manual.SaleDate);
        Assert.Equal("2026-08-03", manual.ExpiryDate);
        // Idempotent: the same serial returns the same warranty.
        Assert.Equal(manual.Id, service.RegisterManual(CompanyCode, "SN-W3", 6, null, "w10-test").Id);
        Assert.Throws<InvalidOperationException>(() =>
            service.RegisterManual(CompanyCode, "SN-UNKNOWN", 6, null, "w10-test"));

        // A Return approval needs the RMA link (SAL-7026 without it).
        var claimId = service.FileClaim(CompanyCode, manual.Id, "Dead on arrival", "2026-02-10", "w10-test");
        var noRma = Assert.Throws<AppError>(() =>
            service.DecideClaim(CompanyCode, claimId, true, WarrantyClaimActions.Return, "Send it back", "w10-test"));
        Assert.Equal(ErrorCodes.WarrantyClaimInvalid, noRma.Code);
        service.DecideClaim(CompanyCode, claimId, true, WarrantyClaimActions.Return, "Send it back", "w10-test", rmaId: 42);
        Assert.Equal(42, Assert.Single(service.ListClaims(CompanyCode, manual.Id)).RmaId);

        // Void is terminal: no further claims (SAL-7026).
        service.VoidWarranty(CompanyCode, manual.Id, "w10-test");
        var voided = Assert.Throws<AppError>(() =>
            service.FileClaim(CompanyCode, manual.Id, "Another", "2026-02-11", "w10-test"));
        Assert.Equal(ErrorCodes.WarrantyClaimInvalid, voided.Code);
        db.Dispose();
    }

    private static void ExecSql(TestDb db, string sql)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.Transaction = db.Tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // ---------------- Helpers ----------------

    private static StockMovementRequest Req(
        long materialId, string date, string type, string docNo,
        decimal qtyIn = 0m, decimal qtyOut = 0m,
        decimal? receiptCost = null,
        IReadOnlyList<BatchAllocationRequest>? batches = null,
        IReadOnlyList<SerialSelection>? serials = null,
        string? originalDocNo = null, bool allowExpired = false,
        long? sourceLineId = null, long? originalSourceLineId = null)
        => new(CompanyCode, 1, materialId, date, type, docNo,
               qtyIn, qtyOut, ReceiptUnitCost: receiptCost,
               BatchAllocations: batches, Serials: serials,
               OriginalDocumentNo: originalDocNo, AllowExpiredBatchIssue: allowExpired,
               SourceDocumentLineId: sourceLineId,
               OriginalSourceDocumentLineId: originalSourceLineId);

    private static StockMovementResult Apply(TestDb db, StockMovementRequest request)
        => StockValuationEngine.Apply(db.Connection, db.Tx, "SQLite", request);

    private static void SeedMaterial(TestDb db, long id,
        bool hasBatch = false, bool hasSerial = false, bool hasExpiry = false,
        int shelfLifeDays = 0, int alertDays = 0)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.Transaction = db.Tx;
        cmd.CommandText = $"""
            INSERT INTO materials (material_id, company_id, code, name_ar, name_en,
                is_stocked, is_service, cost_method, allow_negative, is_deleted,
                has_batch, has_serial, has_expiry, shelf_life_days, alert_before_expiry_days,
                removal_strategy)
            VALUES ({id}, '{CompanyCode}', 'MAT-{id}', 'Item', 'Item',
                1, 0, 'WeightedAvg', 1, 0,
                {(hasBatch ? 1 : 0)}, {(hasSerial ? 1 : 0)}, {(hasExpiry ? 1 : 0)},
                {shelfLifeDays}, {alertDays}, 'FIFO');
            """;
        cmd.ExecuteNonQuery();
    }

    private static (decimal Qty, decimal Value) Balance(TestDb db, long materialId)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.Transaction = db.Tx;
        cmd.CommandText = $"SELECT available_qty, stock_value FROM material_stock_balances WHERE company_id = '{CompanyCode}' AND warehouse_id = 1 AND material_id = {materialId};";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        return (reader.GetDecimal(0), reader.GetDecimal(1));
    }

    private static decimal BatchBalance(TestDb db, string batchNo)
        => Scalar(db, $"""
            SELECT COALESCE(bb.qty_on_hand, 0) FROM stock_batch_balances bb
            JOIN stock_batches b ON b.batch_id = bb.batch_id
            WHERE b.batch_no = '{batchNo}' AND bb.warehouse_id = 1;
            """);

    private static string SerialStatus(TestDb db, string serialNo)
        => Text(db, $"SELECT status FROM stock_serials WHERE serial_no = '{serialNo}'")!;

    private static decimal Scalar(TestDb db, string sql)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.Transaction = db.Tx;
        cmd.CommandText = sql;
        return Convert.ToDecimal(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static string? Text(TestDb db, string sql)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.Transaction = db.Tx;
        cmd.CommandText = sql;
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static async Task<TestDb> SeededAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "batch54-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var dbPath = Path.Combine(root, "batch54.db");
        var companyId = Guid.NewGuid();
        var connectionString = $"Data Source={dbPath};";

        Assert.True((await SchemaMigrator.ApplyAllAsync("SQLite", dbPath)).Success);
        // 054 storage exists on the migrated schema.
        using (var probe = new SqliteConnection(connectionString))
        {
            probe.Open();
            using var cmd = probe.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('stock_batches','stock_batch_balances','stock_serials','stock_movement_allocations');";
            Assert.Equal(4, Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture));
        }

        PointAppAt(dbPath);
        AppState.SetCompany(CompanyCode, "Batch54 Test Co", "Batch54 Test Co");

        using (var seed = new SqliteConnection(connectionString))
        {
            seed.Open();
            using var cmd = seed.CreateCommand();
            cmd.CommandText = "INSERT INTO companies (company_id, company_code, company_name_ar, company_name_en) VALUES ($id, $code, 'Batch54 Test Co', 'Batch54 Test Co');";
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
        private bool _disposed;

        public void Commit()
        {
            if (_committed) return;
            Tx.Commit();
            _committed = true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (!_committed)
            {
                try { Tx.Rollback(); } catch { /* already completed */ }
            }
            Tx.Dispose();
            Connection.Dispose();
            AppState.ClearCompany();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
