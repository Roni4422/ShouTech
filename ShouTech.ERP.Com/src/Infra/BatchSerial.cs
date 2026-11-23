using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.Domain;

namespace ShouTech.Infrastructure;

/// <summary>One batch slice of a movement, as captured on a document line
/// or proposed by the FEFO/FIFO auto-allocator. Quantities are always in
/// the material's BASE unit (the unit stock is kept in).</summary>
public sealed record BatchAllocationRequest(
    string BatchNo,
    decimal Qty,
    string? ExpiryDate = null,
    string? MfgDate = null,
    string? SupplierBatchNo = null);

/// <summary>One serial number taking part in a movement. A serial-tracked
/// movement carries exactly one selection per unit moved.</summary>
public sealed record SerialSelection(
    string SerialNo,
    string? BatchNo = null,
    string? WarrantyExpiry = null);

/// <summary>One written row of stock_movement_allocations, returned by the
/// engine so callers (transfers, genealogy) can chain movements.</summary>
public sealed record StockAllocationRecord(
    long AllocationId,
    long MovementId,
    long? BatchId,
    string? BatchNo,
    long? SerialId,
    string? SerialNo,
    decimal Qty,
    string? ExpiryDate);

/// <summary>The tracking profile of one material, read from the card
/// flags (migration 013 + removal_strategy from 054). has_batch and
/// has_lot are one concept (batch).</summary>
public sealed record MaterialTracking(
    bool HasBatch,
    bool HasSerial,
    bool HasExpiry,
    string RemovalStrategy,
    int ShelfLifeDays,
    int AlertBeforeExpiryDays)
{
    public bool IsTracked => HasBatch || HasSerial;
    public bool UseFefo => HasExpiry
        || string.Equals(RemovalStrategy, "FEFO", StringComparison.OrdinalIgnoreCase);
}

/// <summary>One batch balance row (batch x warehouse on hand).</summary>
public sealed record BatchBalanceRow(
    long BatchId, string BatchNo, string? SupplierBatchNo,
    string? MfgDate, string? ExpiryDate, string Status,
    long WarehouseId, decimal QtyOnHand);

/// <summary>One expired / near-expiry batch row: the data source of the
/// rebuilt ExpiredWin (real batch expiry from stock_batches, never an
/// inference from movement dates).</summary>
public sealed record ExpiredBatchRow(
    long MaterialId, string MaterialCode, string MaterialName, string Unit,
    long BatchId, string BatchNo, long WarehouseId,
    string ExpiryDate, int DaysRemaining, decimal QtyOnHand,
    int AlertDays, bool IsExpired);

/// <summary>One movement in a batch/serial trace timeline.</summary>
public sealed record TraceMovementRow(
    long MovementId, string MovementDate, string MovementType, string DocumentNo,
    long WarehouseId, decimal QtyIn, decimal QtyOut,
    long? BatchId, string? BatchNo, long? SerialId, string? SerialNo,
    string? ExpiryDate);

/// <summary>
/// The batch/serial allocator (Phase B design section 2, migration 054).
/// Called by <see cref="StockValuationEngine"/> INSIDE the writer's
/// posting transaction, immediately after the movement row is written:
/// it validates the batch/serial dimension of the movement, maintains
/// stock_batches / stock_batch_balances / stock_serials, and writes the
/// stock_movement_allocations bundle rows that make every movement
/// traceable. All failures are cataloged AppErrors (INV-6005..6010);
/// because the allocator runs in the posting transaction, a failure
/// rolls the whole posting back — nothing is written.
///
/// Rules (design section 2 + owner decisions B1-B4):
///   * service / non-stocked materials never allocate (the engine does
///     not call the allocator for them);
///   * a tracked RECEIPT requires explicit batch data (batch number,
///     expiry defaulting to receipt date + shelf_life_days) and exactly
///     one serial per unit — INV-6005 when missing;
///   * a tracked batch ISSUE allocates FEFO (expiry order, then receipt
///     order) for has_expiry / FEFO-strategy materials, FIFO otherwise,
///     unless the caller supplied explicit batch slices, which are
///     honored exactly (the picker override);
///   * a tracked serial ISSUE requires the named serials (POS included,
///     decision B2) unless the caller explicitly requests auto-pick
///     (chain / manufacturing paths, which have no capture UI);
///   * an expired batch cannot be issued (INV-6010, decision B1) unless
///     the caller asserts the recorded override;
///   * returns re-enter / consume the ORIGINAL document's batches and
///     serials when the original document number is supplied;
///   * a transfer-in copies its source movement's allocations.
/// </summary>
internal static class BatchSerialAllocator
{
    public static List<StockAllocationRecord> Apply(
        DbConnection connection, DbTransaction transaction, bool isPg,
        StockMovementRequest request, MaterialTracking tracking, string materialCode,
        long movementId)
    {
        var results = new List<StockAllocationRecord>();
        if (!tracking.IsTracked) return results;

        var company = request.CompanyId.Trim();
        var isReceipt = request.QtyIn > 0;
        var qty = isReceipt ? request.QtyIn : request.QtyOut;

        // Transfer-in: the destination allocations are the source
        // movement's allocations, re-created (design: allocations
        // re-created at destination from the source allocation set).
        if (isReceipt && request.SourceMovementId is long sourceMovementId)
        {
            CopySourceAllocations(connection, transaction, isPg, request, tracking,
                materialCode, movementId, sourceMovementId, results);
            return results;
        }

        // Linked return: re-enter (sales return) or consume (purchase
        // return) the original document's batches/serials.
        if (!string.IsNullOrWhiteSpace(request.OriginalDocumentNo))
        {
            if (TryApplyFromOriginal(connection, transaction, isPg, request, tracking,
                    materialCode, movementId, qty, isReceipt, results))
                return results;
            // The original document carries no allocations (it predates
            // 054): fall through to the normal rules, which demand explicit
            // tracking data rather than silently losing batch/serial identity.
        }

        if (tracking.HasBatch)
        {
            if (isReceipt)
                ApplyBatchReceipt(connection, transaction, isPg, request, tracking,
                    materialCode, movementId, qty, results);
            else
                ApplyBatchIssue(connection, transaction, isPg, request, tracking,
                    materialCode, movementId, qty, results);
        }

        if (tracking.HasSerial)
        {
            if (isReceipt)
                ApplySerialReceipt(connection, transaction, isPg, request, tracking,
                    materialCode, movementId, qty, results);
            else
                ApplySerialIssue(connection, transaction, isPg, request, tracking,
                    materialCode, movementId, qty, results);
        }

        return results;
    }

    // ------------------------------------------------------------------
    // Batch receipts
    // ------------------------------------------------------------------
    private static void ApplyBatchReceipt(
        DbConnection connection, DbTransaction transaction, bool isPg,
        StockMovementRequest request, MaterialTracking tracking, string materialCode,
        long movementId, decimal qty, List<StockAllocationRecord> results)
    {
        var allocations = request.BatchAllocations;
        if (allocations is null || allocations.Count == 0)
            throw new AppError(ErrorCodes.BatchSerialRequired,
                $"Material '{materialCode}' is batch-tracked: a batch number is required on every receipt.",
                materialCode);
        var total = allocations.Sum(a => a.Qty);
        if (total != qty || allocations.Any(a => a.Qty <= 0)
            || allocations.Any(a => string.IsNullOrWhiteSpace(a.BatchNo)))
            throw new AppError(ErrorCodes.BatchAllocationMismatch,
                $"Batch allocations for material '{materialCode}' must be positive and sum to the receipt quantity {qty}.",
                materialCode);

        foreach (var alloc in allocations)
        {
            var batch = GetOrCreateBatch(connection, transaction, isPg, request, tracking, alloc);
            AddBatchBalance(connection, transaction, isPg, request, batch.BatchId, alloc.Qty);
            var id = InsertAllocation(connection, transaction, isPg, movementId,
                batch.BatchId, null, alloc.Qty, batch.ExpiryDate);
            results.Add(new StockAllocationRecord(id, movementId, batch.BatchId,
                batch.BatchNo, null, null, alloc.Qty, batch.ExpiryDate));
        }
    }

    // ------------------------------------------------------------------
    // Batch issues (explicit slices, or FEFO/FIFO auto-allocation)
    // ------------------------------------------------------------------
    private static void ApplyBatchIssue(
        DbConnection connection, DbTransaction transaction, bool isPg,
        StockMovementRequest request, MaterialTracking tracking, string materialCode,
        long movementId, decimal qty, List<StockAllocationRecord> results)
    {
        List<(long BatchId, string BatchNo, string? ExpiryDate, decimal Qty)> plan;
        if (request.BatchAllocations is { Count: > 0 } explicitAllocs)
        {
            if (explicitAllocs.Sum(a => a.Qty) != qty || explicitAllocs.Any(a => a.Qty <= 0))
                throw new AppError(ErrorCodes.BatchAllocationMismatch,
                    $"Batch allocations for material '{materialCode}' must be positive and sum to the issue quantity {qty}.",
                    materialCode);
            plan = new List<(long, string, string?, decimal)>();
            foreach (var alloc in explicitAllocs)
            {
                var batch = FindBatch(connection, transaction, isPg, request, alloc.BatchNo)
                    ?? throw new AppError(ErrorCodes.BatchStockInsufficient,
                        $"Batch '{alloc.BatchNo}' does not exist for material '{materialCode}'.",
                        alloc.BatchNo, materialCode);
                EnsureNotExpired(request, tracking, materialCode, batch, request.AllowExpiredBatchIssue);
                var onHand = BatchOnHand(connection, transaction, isPg, request, batch.BatchId);
                if (onHand < alloc.Qty)
                    throw new AppError(ErrorCodes.BatchStockInsufficient,
                        $"Batch '{batch.BatchNo}' holds {onHand} of material '{materialCode}' at this warehouse; {alloc.Qty} were allocated.",
                        batch.BatchNo, materialCode);
                plan.Add((batch.BatchId, batch.BatchNo, batch.ExpiryDate, alloc.Qty));
            }
        }
        else
        {
            plan = PlanAutoAllocation(connection, transaction, isPg, request, tracking,
                materialCode, qty);
        }

        foreach (var (batchId, batchNo, expiry, take) in plan)
        {
            AddBatchBalance(connection, transaction, isPg, request, batchId, -take);
            var id = InsertAllocation(connection, transaction, isPg, movementId,
                batchId, null, -take, expiry);
            results.Add(new StockAllocationRecord(id, movementId, batchId,
                batchNo, null, null, -take, expiry));
        }
    }

    /// <summary>FEFO (expiry ASC, nulls last, then batch creation order)
    /// or FIFO (creation order) over the warehouse's positive batch
    /// balances. Expired batches are skipped unless the override is
    /// asserted; when live batches cannot cover the issue but expired
    /// ones could, the failure is INV-6010, else INV-6006.</summary>
    internal static List<(long BatchId, string BatchNo, string? ExpiryDate, decimal Qty)> PlanAutoAllocation(
        DbConnection connection, DbTransaction transaction, bool isPg,
        StockMovementRequest request, MaterialTracking tracking, string materialCode, decimal qty)
    {
        var candidates = LoadBatchBalances(connection, transaction, isPg, request, tracking);
        var plan = new List<(long, string, string?, decimal)>();
        var remaining = qty;

        bool IsExpired((long BatchId, string BatchNo, string? ExpiryDate, decimal OnHand) c)
            => !string.IsNullOrWhiteSpace(c.ExpiryDate)
               && string.CompareOrdinal(c.ExpiryDate, request.MovementDate) < 0;

        foreach (var c in candidates.Where(c => !IsExpired(c)))
        {
            if (remaining <= 0) break;
            var take = Math.Min(remaining, c.OnHand);
            if (take <= 0) continue;
            plan.Add((c.BatchId, c.BatchNo, c.ExpiryDate, take));
            remaining -= take;
        }

        if (remaining > 0 && request.AllowExpiredBatchIssue)
        {
            foreach (var c in candidates.Where(IsExpired))
            {
                if (remaining <= 0) break;
                var take = Math.Min(remaining, c.OnHand);
                if (take <= 0) continue;
                plan.Add((c.BatchId, c.BatchNo, c.ExpiryDate, take));
                remaining -= take;
            }
        }

        if (remaining > 0)
        {
            var firstExpired = candidates.FirstOrDefault(IsExpired);
            if (firstExpired.BatchNo is not null
                && candidates.Where(IsExpired).Sum(c => c.OnHand) >= remaining)
                throw new AppError(ErrorCodes.BatchExpired,
                    $"Batch '{firstExpired.BatchNo}' of material '{materialCode}' expired on {firstExpired.ExpiryDate}; expired batches cannot be issued without the override.",
                    firstExpired.BatchNo, materialCode, firstExpired.ExpiryDate ?? string.Empty);
            throw new AppError(ErrorCodes.BatchStockInsufficient,
                $"Batch balances at this warehouse cover {qty - remaining} of the {qty} required for material '{materialCode}'.",
                candidates.FirstOrDefault().BatchNo ?? "-", materialCode);
        }
        return plan;
    }

    internal static List<(long BatchId, string BatchNo, string? ExpiryDate, decimal OnHand)> LoadBatchBalances(
        DbConnection connection, DbTransaction transaction, bool isPg,
        StockMovementRequest request, MaterialTracking? tracking = null)
    {
        // FEFO ordering needs the tracking profile; the engine path
        // passes it, the service's suggestion path passes it too.
        var fefo = tracking?.UseFefo ?? false;
        var order = fefo
            ? "CASE WHEN b.expiry_date IS NULL THEN 1 ELSE 0 END, b.expiry_date, b.batch_id"
            : "b.batch_id";
        var rows = new List<(long, string, string?, decimal)>();
        using var cmd = CreateCommand(connection, transaction, $"""
            SELECT b.batch_id, b.batch_no, b.expiry_date, bb.qty_on_hand
            FROM stock_batch_balances bb
            JOIN stock_batches b ON b.batch_id = bb.batch_id
            WHERE bb.company_id = @c AND bb.warehouse_id = @w AND b.material_id = @m
              AND bb.qty_on_hand > 0 AND b.status = 'Active'
              AND b.is_deleted = {(isPg ? "FALSE" : "0")}
            ORDER BY {order}
            """);
        Add(cmd, "@c", request.CompanyId.Trim());
        Add(cmd, "@w", request.WarehouseId);
        Add(cmd, "@m", request.MaterialId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add((Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                ValuationText.ToText(reader.GetValue(1)) ?? string.Empty,
                ValuationText.ToText(reader.GetValue(2)),
                ValuationText.ToDecimal(reader.GetValue(3))));
        }
        return rows;
    }

    // ------------------------------------------------------------------
    // Serial receipts / issues
    // ------------------------------------------------------------------
    private static void ApplySerialReceipt(
        DbConnection connection, DbTransaction transaction, bool isPg,
        StockMovementRequest request, MaterialTracking tracking, string materialCode,
        long movementId, decimal qty, List<StockAllocationRecord> results)
    {
        var serials = RequireSerials(request, materialCode, qty);
        foreach (var sel in serials)
        {
            var existing = FindSerial(connection, transaction, isPg, request, sel.SerialNo);
            if (existing is not null)
                throw new AppError(ErrorCodes.SerialDuplicate,
                    $"Serial '{sel.SerialNo}' already exists for material '{materialCode}' (status {existing.Status}).",
                    sel.SerialNo, materialCode);
            long? batchId = null;
            string? batchExpiry = null;
            if (!string.IsNullOrWhiteSpace(sel.BatchNo))
            {
                var batch = FindBatch(connection, transaction, isPg, request, sel.BatchNo!);
                batchId = batch?.BatchId;
                batchExpiry = batch?.ExpiryDate;
            }
            var serialId = InsertSerial(connection, transaction, isPg, request, sel, batchId);
            var id = InsertAllocation(connection, transaction, isPg, movementId,
                batchId, serialId, 1m, batchExpiry);
            results.Add(new StockAllocationRecord(id, movementId, batchId,
                sel.BatchNo, serialId, sel.SerialNo, 1m, batchExpiry));
        }
    }

    private static void ApplySerialIssue(
        DbConnection connection, DbTransaction transaction, bool isPg,
        StockMovementRequest request, MaterialTracking tracking, string materialCode,
        long movementId, decimal qty, List<StockAllocationRecord> results)
    {
        IReadOnlyList<SerialSelection> serials;
        if (request.Serials is { Count: > 0 })
        {
            serials = RequireSerials(request, materialCode, qty);
        }
        else if (request.AutoAllocateSerials)
        {
            serials = PickSerials(connection, transaction, isPg, request, materialCode, qty);
        }
        else
        {
            throw new AppError(ErrorCodes.BatchSerialRequired,
                $"Material '{materialCode}' is serial-tracked: the serial numbers are required on every issue.",
                materialCode);
        }

        var newStatus = request.MovementType.StartsWith("MFG", StringComparison.OrdinalIgnoreCase)
            ? "Consumed" : "Delivered";
        foreach (var sel in serials)
        {
            var serial = FindSerial(connection, transaction, isPg, request, sel.SerialNo)
                ?? throw new AppError(ErrorCodes.SerialUnavailable,
                    $"Serial '{sel.SerialNo}' does not exist for material '{materialCode}'.",
                    sel.SerialNo, materialCode);
            if (!string.Equals(serial.Status, "InStock", StringComparison.OrdinalIgnoreCase)
                || serial.WarehouseId != request.WarehouseId)
                throw new AppError(ErrorCodes.SerialUnavailable,
                    $"Serial '{sel.SerialNo}' of material '{materialCode}' is {serial.Status} and cannot be issued from warehouse {request.WarehouseId}.",
                    sel.SerialNo, materialCode);
            UpdateSerialState(connection, transaction, isPg, serial.SerialId,
                newStatus, null, request.PartyId);
            string? expiry = null;
            if (serial.BatchId is long bid)
                expiry = FindBatchById(connection, transaction, isPg, request, bid)?.ExpiryDate;
            var id = InsertAllocation(connection, transaction, isPg, movementId,
                serial.BatchId, serial.SerialId, -1m, expiry);
            results.Add(new StockAllocationRecord(id, movementId, serial.BatchId,
                null, serial.SerialId, sel.SerialNo, -1m, expiry));
        }
    }

    private static IReadOnlyList<SerialSelection> RequireSerials(
        StockMovementRequest request, string materialCode, decimal qty)
    {
        var serials = request.Serials;
        if (serials is null || serials.Count == 0)
            throw new AppError(ErrorCodes.BatchSerialRequired,
                $"Material '{materialCode}' is serial-tracked: one serial number per unit is required.",
                materialCode);
        if (qty != decimal.Truncate(qty) || serials.Count != (int)qty
            || serials.Any(s => string.IsNullOrWhiteSpace(s.SerialNo))
            || serials.Select(s => s.SerialNo.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != serials.Count)
            throw new AppError(ErrorCodes.BatchAllocationMismatch,
                $"Material '{materialCode}' needs exactly {(int)qty} distinct serial numbers for this movement.",
                materialCode);
        return serials;
    }

    private static IReadOnlyList<SerialSelection> PickSerials(
        DbConnection connection, DbTransaction transaction, bool isPg,
        StockMovementRequest request, string materialCode, decimal qty)
    {
        if (qty != decimal.Truncate(qty))
            throw new AppError(ErrorCodes.BatchAllocationMismatch,
                $"Material '{materialCode}' is serial-tracked: the quantity must be a whole number of units.",
                materialCode);
        var picked = new List<SerialSelection>();
        using var cmd = CreateCommand(connection, transaction, $"""
            SELECT serial_no FROM stock_serials
            WHERE company_id = @c AND material_id = @m AND status = 'InStock'
              AND current_warehouse_id = @w AND is_deleted = {(isPg ? "FALSE" : "0")}
            ORDER BY serial_id
            """);
        Add(cmd, "@c", request.CompanyId.Trim());
        Add(cmd, "@m", request.MaterialId);
        Add(cmd, "@w", request.WarehouseId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read() && picked.Count < (int)qty)
            picked.Add(new SerialSelection(ValuationText.ToText(reader.GetValue(0)) ?? string.Empty));
        if (picked.Count < (int)qty)
            throw new AppError(ErrorCodes.SerialUnavailable,
                $"Only {picked.Count} serials of material '{materialCode}' are in stock at this warehouse; {qty} were required.",
                picked.FirstOrDefault()?.SerialNo ?? "-", materialCode);
        return picked;
    }

    // ------------------------------------------------------------------
    // Linked returns (original document's allocations)
    // ------------------------------------------------------------------
    private static bool TryApplyFromOriginal(
        DbConnection connection, DbTransaction transaction, bool isPg,
        StockMovementRequest request, MaterialTracking tracking, string materialCode,
        long movementId, decimal qty, bool isReceipt, List<StockAllocationRecord> results)
    {
        // Original allocations for this material: receipts carry +, issues
        // carry -. A sales return mirrors the original ISSUE; a purchase
        // return mirrors the original RECEIPT. The optional source line
        // prevents two identical materials on one receipt from sharing lots.
        var wantSign = isReceipt ? -1m : 1m;
        var originals = new List<(long? BatchId, string? BatchNo, string? Expiry, long? SerialId, string? SerialNo, decimal Qty)>();
        using (var cmd = CreateCommand(connection, transaction, """
            SELECT a.batch_id, b.batch_no, a.expiry_date, a.serial_id, s.serial_no, a.qty
            FROM stock_movement_allocations a
            JOIN material_stock_movements m
              ON m.stock_movement_id = a.stock_movement_id
            LEFT JOIN stock_batches b ON b.batch_id = a.batch_id
            LEFT JOIN stock_serials s ON s.serial_id = a.serial_id
            WHERE m.company_id = @c AND m.document_no = @doc AND m.material_id = @mat
              AND (@sourceLine IS NULL OR m.source_document_line_id = @sourceLine)
            ORDER BY a.allocation_id
            """))
        {
            Add(cmd, "@c", request.CompanyId.Trim());
            Add(cmd, "@doc", request.OriginalDocumentNo!);
            Add(cmd, "@mat", request.MaterialId);
            Add(cmd, "@sourceLine", (object?)request.OriginalSourceDocumentLineId ?? DBNull.Value);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var signed = ValuationText.ToDecimal(reader.GetValue(5));
                if (Math.Sign(signed) != Math.Sign(wantSign)) continue;
                originals.Add((
                    reader.IsDBNull(0) ? null : Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                    ValuationText.ToText(reader.GetValue(1)),
                    ValuationText.ToText(reader.GetValue(2)),
                    reader.IsDBNull(3) ? null : Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture),
                    ValuationText.ToText(reader.GetValue(4)),
                    Math.Abs(signed)));
            }
        }
        if (originals.Count == 0) return false;
        if (!tracking.HasBatch && !tracking.HasSerial) return false;

        var batchOriginals = originals.Where(o => o.BatchId is not null && o.SerialId is null).ToList();
        var serialOriginals = originals.Where(o => o.SerialId is not null).ToList();
        if (tracking.HasBatch && batchOriginals.Sum(o => o.Qty) < qty) return false;
        if (tracking.HasSerial && serialOriginals.Count < qty) return false;
        if (tracking.HasBatch && tracking.HasSerial &&
            ((request.BatchAllocations is { Count: > 0 }) != (request.Serials is { Count: > 0 })))
            throw new AppError(ErrorCodes.BatchAllocationMismatch,
                $"Material '{materialCode}' is both batch- and serial-tracked; select both dimensions on a linked return.",
                materialCode);

        var signedQty = isReceipt ? qty : -qty;
        var batchPlan = new List<(long BatchId, string BatchNo, string? Expiry, decimal Qty)>();
        if (tracking.HasBatch)
        {
            if (request.BatchAllocations is { Count: > 0 } explicitBatches)
            {
                if (explicitBatches.Any(x => x.Qty <= 0m || string.IsNullOrWhiteSpace(x.BatchNo))
                    || explicitBatches.Sum(x => x.Qty) != qty)
                    throw new AppError(ErrorCodes.BatchAllocationMismatch,
                        $"Batch selections for material '{materialCode}' must be positive and sum to {qty}.",
                        materialCode);
                foreach (var requested in explicitBatches)
                {
                    var left = requested.Qty;
                    foreach (var source in batchOriginals.Where(o =>
                        string.Equals(o.BatchNo, requested.BatchNo.Trim(), StringComparison.OrdinalIgnoreCase)))
                    {
                        if (left <= 0m) break;
                        var take = Math.Min(left, source.Qty);
                        if (take > 0m)
                        {
                            batchPlan.Add((source.BatchId!.Value, source.BatchNo ?? requested.BatchNo,
                                source.Expiry, take));
                            left -= take;
                        }
                    }
                    if (left > 0m)
                        throw new AppError(ErrorCodes.BatchAllocationMismatch,
                            $"Batch '{requested.BatchNo}' was not received on the selected source line in the requested quantity.",
                            requested.BatchNo, materialCode);
                }
            }
            else
            {
                var left = qty;
                foreach (var source in batchOriginals)
                {
                    if (left <= 0m) break;
                    var take = Math.Min(left, source.Qty);
                    if (take > 0m)
                    {
                        batchPlan.Add((source.BatchId!.Value, source.BatchNo ?? string.Empty, source.Expiry, take));
                        left -= take;
                    }
                }
                if (left > 0m) return false;
            }
        }

        var serialPlan = new List<(long? BatchId, long SerialId, string SerialNo, string? Expiry)>();
        if (tracking.HasSerial)
        {
            if (request.Serials is { Count: > 0 })
            {
                var validated = RequireSerials(request, materialCode, qty);
                foreach (var selected in validated)
                {
                    var source = serialOriginals.FirstOrDefault(o =>
                        string.Equals(o.SerialNo, selected.SerialNo.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (source.SerialId is null)
                        throw new AppError(ErrorCodes.SerialUnavailable,
                            $"Serial '{selected.SerialNo}' was not part of the selected source receipt line.",
                            selected.SerialNo, materialCode);
                    serialPlan.Add((source.BatchId, source.SerialId.Value,
                        source.SerialNo ?? selected.SerialNo.Trim(), source.Expiry));
                }
            }
            else
            {
                if (qty != decimal.Truncate(qty)) return false;
                foreach (var source in serialOriginals.Take((int)qty))
                    serialPlan.Add((source.BatchId, source.SerialId!.Value,
                        source.SerialNo ?? string.Empty, source.Expiry));
            }
            if (serialPlan.Count != (int)qty) return false;
        }

        if (tracking.HasBatch && tracking.HasSerial)
        {
            var batchTotals = batchPlan.GroupBy(x => x.BatchId).ToDictionary(g => g.Key, g => g.Sum(x => x.Qty));
            var serialTotals = serialPlan.Where(x => x.BatchId is not null)
                .GroupBy(x => x.BatchId!.Value).ToDictionary(g => g.Key, g => (decimal)g.Count());
            if (batchTotals.Any(x => serialTotals.GetValueOrDefault(x.Key) != x.Value)
                || serialTotals.Any(x => batchTotals.GetValueOrDefault(x.Key) != x.Value))
                throw new AppError(ErrorCodes.BatchAllocationMismatch,
                    $"Selected batches and serials for material '{materialCode}' do not describe the same units.",
                    materialCode);
        }

        foreach (var (batchId, batchNo, expiry, take) in batchPlan)
        {
            if (!isReceipt)
            {
                var onHand = BatchOnHand(connection, transaction, isPg, request, batchId);
                if (onHand < take)
                    throw new AppError(ErrorCodes.BatchStockInsufficient,
                        $"Batch '{batchNo}' holds {onHand} of material '{materialCode}'; the return needs {take}.",
                        batchNo, materialCode);
            }
            AddBatchBalance(connection, transaction, isPg, request, batchId, signedQty > 0 ? take : -take);
            var id = InsertAllocation(connection, transaction, isPg, movementId,
                batchId, null, signedQty > 0 ? take : -take, expiry);
            results.Add(new StockAllocationRecord(id, movementId, batchId,
                batchNo, null, null, signedQty > 0 ? take : -take, expiry));
        }

        foreach (var selected in serialPlan)
        {
            if (isReceipt)
            {
                UpdateSerialState(connection, transaction, isPg, selected.SerialId,
                    "InStock", request.WarehouseId, null);
            }
            else
            {
                var serial = FindSerialById(connection, transaction, isPg, request, selected.SerialId);
                if (serial is null || !string.Equals(serial.Status, "InStock", StringComparison.OrdinalIgnoreCase)
                    || serial.WarehouseId != request.WarehouseId)
                    throw new AppError(ErrorCodes.SerialUnavailable,
                        $"Serial '{selected.SerialNo}' of material '{materialCode}' is not in stock for the return.",
                        selected.SerialNo, materialCode);
                UpdateSerialState(connection, transaction, isPg, selected.SerialId,
                    "Delivered", null, request.PartyId);
            }
            var id = InsertAllocation(connection, transaction, isPg, movementId,
                selected.BatchId, selected.SerialId, signedQty > 0 ? 1m : -1m, selected.Expiry);
            results.Add(new StockAllocationRecord(id, movementId, selected.BatchId,
                null, selected.SerialId, selected.SerialNo, signedQty > 0 ? 1m : -1m, selected.Expiry));
        }

        return (!tracking.HasBatch || batchPlan.Sum(x => x.Qty) == qty)
            && (!tracking.HasSerial || serialPlan.Count == (int)qty);
    }

    // ------------------------------------------------------------------
    // Transfers: copy the source movement's allocations
    // ------------------------------------------------------------------
    private static void CopySourceAllocations(
        DbConnection connection, DbTransaction transaction, bool isPg,
        StockMovementRequest request, MaterialTracking tracking, string materialCode,
        long movementId, long sourceMovementId, List<StockAllocationRecord> results)
    {
        var source = new List<(long? BatchId, string? BatchNo, string? Expiry, long? SerialId, string? SerialNo, decimal Qty)>();
        using (var cmd = CreateCommand(connection, transaction, """
            SELECT a.batch_id, b.batch_no, a.expiry_date, a.serial_id, s.serial_no, a.qty
            FROM stock_movement_allocations a
            LEFT JOIN stock_batches b ON b.batch_id = a.batch_id
            LEFT JOIN stock_serials s ON s.serial_id = a.serial_id
            WHERE a.stock_movement_id = @src
            ORDER BY a.allocation_id
            """))
        {
            Add(cmd, "@src", sourceMovementId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                source.Add((
                    reader.IsDBNull(0) ? null : Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                    ValuationText.ToText(reader.GetValue(1)),
                    ValuationText.ToText(reader.GetValue(2)),
                    reader.IsDBNull(3) ? null : Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture),
                    ValuationText.ToText(reader.GetValue(4)),
                    ValuationText.ToDecimal(reader.GetValue(5))));
            }
        }
        if (source.Count == 0)
            throw new AppError(ErrorCodes.BatchSerialRequired,
                $"Material '{materialCode}' is tracked but its transfer source movement carries no allocations.",
                materialCode);

        foreach (var s in source)
        {
            var inQty = Math.Abs(s.Qty);
            if (s.BatchId is not null && s.SerialId is null)
                AddBatchBalance(connection, transaction, isPg, request, s.BatchId.Value, inQty);
            if (s.SerialId is not null)
                UpdateSerialState(connection, transaction, isPg, s.SerialId.Value,
                    "InStock", request.WarehouseId, null);
            var id = InsertAllocation(connection, transaction, isPg, movementId,
                s.BatchId, s.SerialId, inQty, s.Expiry);
            results.Add(new StockAllocationRecord(id, movementId, s.BatchId,
                s.BatchNo, s.SerialId, s.SerialNo, inQty, s.Expiry));
        }
    }

    // ------------------------------------------------------------------
    // Batch / serial row helpers
    // ------------------------------------------------------------------
    private sealed record BatchRow(long BatchId, string BatchNo, string? ExpiryDate);

    private static BatchRow GetOrCreateBatch(
        DbConnection connection, DbTransaction transaction, bool isPg,
        StockMovementRequest request, MaterialTracking tracking, BatchAllocationRequest alloc)
    {
        var existing = FindBatch(connection, transaction, isPg, request, alloc.BatchNo);
        if (existing is not null) return existing;

        var expiry = alloc.ExpiryDate;
        if (string.IsNullOrWhiteSpace(expiry) && tracking.HasExpiry && tracking.ShelfLifeDays > 0
            && DateTime.TryParseExact(request.MovementDate, "yyyy-MM-dd",
                   CultureInfo.InvariantCulture, DateTimeStyles.None, out var receiptDate))
            expiry = receiptDate.AddDays(tracking.ShelfLifeDays)
                .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var sql = isPg
            ? """
              INSERT INTO stock_batches
                  (company_id, material_id, batch_no, supplier_batch_no, mfg_date, expiry_date,
                   source_doc_kind, source_doc_no, supplier_party_id, status, created_by)
              VALUES (@c, @m, @no, @sup, @mfg, @exp, @kind, @doc, @party, 'Active', @actor)
              RETURNING batch_id
              """
            : """
              INSERT INTO stock_batches
                  (company_id, material_id, batch_no, supplier_batch_no, mfg_date, expiry_date,
                   source_doc_kind, source_doc_no, supplier_party_id, status, created_by)
              VALUES (@c, @m, @no, @sup, @mfg, @exp, @kind, @doc, @party, 'Active', @actor);
              SELECT last_insert_rowid();
              """;
        using var cmd = CreateCommand(connection, transaction, sql);
        Add(cmd, "@c", request.CompanyId.Trim());
        Add(cmd, "@m", request.MaterialId);
        Add(cmd, "@no", alloc.BatchNo.Trim());
        Add(cmd, "@sup", (object?)alloc.SupplierBatchNo ?? DBNull.Value);
        Add(cmd, "@mfg", (object?)alloc.MfgDate ?? DBNull.Value);
        Add(cmd, "@exp", (object?)expiry ?? DBNull.Value);
        Add(cmd, "@kind", request.MovementType);
        Add(cmd, "@doc", request.DocumentNo);
        Add(cmd, "@party", (object?)request.PartyId ?? DBNull.Value);
        Add(cmd, "@actor", (object?)request.Actor ?? DBNull.Value);
        var id = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        return new BatchRow(id, alloc.BatchNo.Trim(), expiry);
    }

    private static BatchRow? FindBatch(
        DbConnection connection, DbTransaction transaction, bool isPg,
        StockMovementRequest request, string batchNo)
    {
        using var cmd = CreateCommand(connection, transaction, $"""
            SELECT batch_id, batch_no, expiry_date FROM stock_batches
            WHERE company_id = @c AND material_id = @m AND batch_no = @no
              AND is_deleted = {(isPg ? "FALSE" : "0")} LIMIT 1
            """);
        Add(cmd, "@c", request.CompanyId.Trim());
        Add(cmd, "@m", request.MaterialId);
        Add(cmd, "@no", batchNo.Trim());
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new BatchRow(
            Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
            ValuationText.ToText(reader.GetValue(1)) ?? batchNo,
            ValuationText.ToText(reader.GetValue(2)));
    }

    private static BatchRow? FindBatchById(
        DbConnection connection, DbTransaction transaction, bool isPg,
        StockMovementRequest request, long batchId)
    {
        using var cmd = CreateCommand(connection, transaction, """
            SELECT batch_id, batch_no, expiry_date FROM stock_batches WHERE batch_id = @id LIMIT 1
            """);
        Add(cmd, "@id", batchId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new BatchRow(
            Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
            ValuationText.ToText(reader.GetValue(1)) ?? string.Empty,
            ValuationText.ToText(reader.GetValue(2)));
    }

    private static void EnsureNotExpired(
        StockMovementRequest request, MaterialTracking tracking, string materialCode,
        BatchRow batch, bool allowOverride)
    {
        if (allowOverride) return;
        if (!string.IsNullOrWhiteSpace(batch.ExpiryDate)
            && string.CompareOrdinal(batch.ExpiryDate, request.MovementDate) < 0)
            throw new AppError(ErrorCodes.BatchExpired,
                $"Batch '{batch.BatchNo}' of material '{materialCode}' expired on {batch.ExpiryDate}; expired batches cannot be issued without the override.",
                batch.BatchNo, materialCode, batch.ExpiryDate);
    }

    private static decimal BatchOnHand(
        DbConnection connection, DbTransaction transaction, bool isPg,
        StockMovementRequest request, long batchId)
    {
        using var cmd = CreateCommand(connection, transaction, """
            SELECT COALESCE(qty_on_hand, 0) FROM stock_batch_balances
            WHERE company_id = @c AND warehouse_id = @w AND batch_id = @b LIMIT 1
            """);
        Add(cmd, "@c", request.CompanyId.Trim());
        Add(cmd, "@w", request.WarehouseId);
        Add(cmd, "@b", batchId);
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? 0m : ValuationText.ToDecimal(value);
    }

    private static void AddBatchBalance(
        DbConnection connection, DbTransaction transaction, bool isPg,
        StockMovementRequest request, long batchId, decimal delta)
    {
        var now = isPg ? "now()" : "datetime('now')";
        using var cmd = CreateCommand(connection, transaction, $"""
            INSERT INTO stock_batch_balances (company_id, warehouse_id, batch_id, qty_on_hand, last_updated)
            VALUES (@c, @w, @b, @q, {now})
            ON CONFLICT(company_id, warehouse_id, batch_id)
            DO UPDATE SET qty_on_hand = stock_batch_balances.qty_on_hand + @q, last_updated = {now}
            """);
        Add(cmd, "@c", request.CompanyId.Trim());
        Add(cmd, "@w", request.WarehouseId);
        Add(cmd, "@b", batchId);
        Add(cmd, "@q", delta);
        cmd.ExecuteNonQuery();
    }

    private sealed record SerialRow(long SerialId, string Status, long? WarehouseId, long? BatchId);

    private static SerialRow? FindSerial(
        DbConnection connection, DbTransaction transaction, bool isPg,
        StockMovementRequest request, string serialNo)
    {
        using var cmd = CreateCommand(connection, transaction, $"""
            SELECT serial_id, status, current_warehouse_id, batch_id FROM stock_serials
            WHERE company_id = @c AND material_id = @m AND serial_no = @no
              AND is_deleted = {(isPg ? "FALSE" : "0")} LIMIT 1
            """);
        Add(cmd, "@c", request.CompanyId.Trim());
        Add(cmd, "@m", request.MaterialId);
        Add(cmd, "@no", serialNo.Trim());
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new SerialRow(
            Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
            ValuationText.ToText(reader.GetValue(1)) ?? string.Empty,
            reader.IsDBNull(2) ? null : Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture),
            reader.IsDBNull(3) ? null : Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture));
    }

    private static SerialRow? FindSerialById(
        DbConnection connection, DbTransaction transaction, bool isPg,
        StockMovementRequest request, long serialId)
    {
        using var cmd = CreateCommand(connection, transaction, """
            SELECT serial_id, status, current_warehouse_id, batch_id FROM stock_serials WHERE serial_id = @id LIMIT 1
            """);
        Add(cmd, "@id", serialId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new SerialRow(
            Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
            ValuationText.ToText(reader.GetValue(1)) ?? string.Empty,
            reader.IsDBNull(2) ? null : Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture),
            reader.IsDBNull(3) ? null : Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture));
    }

    private static long InsertSerial(
        DbConnection connection, DbTransaction transaction, bool isPg,
        StockMovementRequest request, SerialSelection sel, long? batchId)
    {
        var sql = isPg
            ? """
              INSERT INTO stock_serials
                  (company_id, material_id, serial_no, batch_id, status, current_warehouse_id,
                   warranty_expiry, source_doc_kind, source_doc_no, created_by)
              VALUES (@c, @m, @no, @b, 'InStock', @w, @warranty, @kind, @doc, @actor)
              RETURNING serial_id
              """
            : """
              INSERT INTO stock_serials
                  (company_id, material_id, serial_no, batch_id, status, current_warehouse_id,
                   warranty_expiry, source_doc_kind, source_doc_no, created_by)
              VALUES (@c, @m, @no, @b, 'InStock', @w, @warranty, @kind, @doc, @actor);
              SELECT last_insert_rowid();
              """;
        using var cmd = CreateCommand(connection, transaction, sql);
        Add(cmd, "@c", request.CompanyId.Trim());
        Add(cmd, "@m", request.MaterialId);
        Add(cmd, "@no", sel.SerialNo.Trim());
        Add(cmd, "@b", (object?)batchId ?? DBNull.Value);
        Add(cmd, "@w", request.WarehouseId);
        Add(cmd, "@warranty", (object?)sel.WarrantyExpiry ?? DBNull.Value);
        Add(cmd, "@kind", request.MovementType);
        Add(cmd, "@doc", request.DocumentNo);
        Add(cmd, "@actor", (object?)request.Actor ?? DBNull.Value);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void UpdateSerialState(
        DbConnection connection, DbTransaction transaction, bool isPg,
        long serialId, string status, long? warehouseId, long? partyId)
    {
        var now = isPg ? "now()" : "datetime('now')";
        using var cmd = CreateCommand(connection, transaction, $"""
            UPDATE stock_serials
            SET status = @s, current_warehouse_id = @w, party_id = @p, updated_at = {now}
            WHERE serial_id = @id
            """);
        Add(cmd, "@s", status);
        Add(cmd, "@w", (object?)warehouseId ?? DBNull.Value);
        Add(cmd, "@p", (object?)partyId ?? DBNull.Value);
        Add(cmd, "@id", serialId);
        cmd.ExecuteNonQuery();
    }

    private static long InsertAllocation(
        DbConnection connection, DbTransaction transaction, bool isPg,
        long movementId, long? batchId, long? serialId, decimal qty, string? expiry)
    {
        var sql = isPg
            ? """
              INSERT INTO stock_movement_allocations (stock_movement_id, batch_id, serial_id, qty, expiry_date)
              VALUES (@mov, @b, @s, @q, @exp) RETURNING allocation_id
              """
            : """
              INSERT INTO stock_movement_allocations (stock_movement_id, batch_id, serial_id, qty, expiry_date)
              VALUES (@mov, @b, @s, @q, @exp); SELECT last_insert_rowid();
              """;
        using var cmd = CreateCommand(connection, transaction, sql);
        Add(cmd, "@mov", movementId);
        Add(cmd, "@b", (object?)batchId ?? DBNull.Value);
        Add(cmd, "@s", (object?)serialId ?? DBNull.Value);
        Add(cmd, "@q", qty);
        Add(cmd, "@exp", (object?)expiry ?? DBNull.Value);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    // ------------------------------------------------------------------
    // ADO helpers (StockValuationEngine conventions)
    // ------------------------------------------------------------------
    internal static DbCommand CreateCommand(DbConnection connection, DbTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    internal static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

/// <summary>In-transaction tracking lookup for posting paths that must
/// shape a <see cref="StockMovementRequest"/> before calling the engine
/// (manufacturing finished goods, card-store orders): reads the
/// material's card flags on the caller's own connection/transaction.</summary>
public static class BatchSerialCapture
{
    public static MaterialTracking LoadTracking(
        DbConnection connection, DbTransaction transaction, string provider,
        string companyId, long materialId)
    {
        var isPg = provider.Trim().Equals("PostgreSQL", StringComparison.OrdinalIgnoreCase)
            || provider.Trim().Equals("npgsql", StringComparison.OrdinalIgnoreCase);
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"""
            SELECT COALESCE(has_batch, 0), COALESCE(has_lot, 0), COALESCE(has_serial, 0),
                   COALESCE(has_expiry, 0), COALESCE(removal_strategy, 'FIFO'),
                   COALESCE(shelf_life_days, 0), COALESCE(alert_before_expiry_days, 0)
            FROM materials WHERE company_id = @c AND material_id = @m
              AND is_deleted = {(isPg ? "FALSE" : "0")} LIMIT 1
            """;
        var p1 = cmd.CreateParameter(); p1.ParameterName = "@c"; p1.Value = companyId.Trim(); cmd.Parameters.Add(p1);
        var p2 = cmd.CreateParameter(); p2.ParameterName = "@m"; p2.Value = materialId; cmd.Parameters.Add(p2);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return new MaterialTracking(false, false, false, "FIFO", 0, 0);
        bool Flag(int i) => !reader.IsDBNull(i)
            && Convert.ToBoolean(reader.GetValue(i), CultureInfo.InvariantCulture);
        return new MaterialTracking(
            Flag(0) || Flag(1), Flag(2), Flag(3),
            ValuationText.ToText(reader.GetValue(4)) ?? "FIFO",
            reader.IsDBNull(5) ? 0 : Convert.ToInt32(reader.GetValue(5), CultureInfo.InvariantCulture),
            reader.IsDBNull(6) ? 0 : Convert.ToInt32(reader.GetValue(6), CultureInfo.InvariantCulture));
    }
}

/// <summary>
/// Batch/serial read + suggestion service (Phase B 054): FEFO/FIFO
/// picking suggestions, batch balances, both traceability directions
/// (batch/serial to every movement + current location; movement to its
/// batches/serials), the recall list (every party that received units
/// of a batch), and the expired / near-expiry batch list that backs
/// the rebuilt ExpiredWin. Opens its own connections, like
/// StockRecalculationService; all queries are parameterized.
/// </summary>
public sealed class BatchSerialService
{
    private readonly string _provider;
    private readonly string _connectionString;

    public BatchSerialService(string provider, string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("A database connection string is required.", nameof(connectionString));
        _provider = NormalizeProvider(provider);
        _connectionString = connectionString;
    }

    private bool IsPg => _provider == "PostgreSQL";
    private string NotDeleted => IsPg ? "is_deleted = FALSE" : "is_deleted = 0";

    private DbConnection OpenConnection()
    {
        DbConnection connection = IsPg
            ? new NpgsqlConnection(_connectionString)
            : new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    /// <summary>The material's tracking profile, or an untracked profile
    /// when the material does not exist.</summary>
    public MaterialTracking GetTracking(string companyId, long materialId)
    {
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT COALESCE(has_batch, 0), COALESCE(has_lot, 0), COALESCE(has_serial, 0),
                   COALESCE(has_expiry, 0), COALESCE(removal_strategy, 'FIFO'),
                   COALESCE(shelf_life_days, 0), COALESCE(alert_before_expiry_days, 0)
            FROM materials WHERE company_id = @c AND material_id = @m AND {NotDeleted} LIMIT 1
            """;
        Add(cmd, "@c", companyId.Trim());
        Add(cmd, "@m", materialId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return new MaterialTracking(false, false, false, "FIFO", 0, 0);
        return new MaterialTracking(
            Truthy(reader.GetValue(0)) || Truthy(reader.GetValue(1)),
            Truthy(reader.GetValue(2)),
            Truthy(reader.GetValue(3)),
            ValuationText.ToText(reader.GetValue(4)) ?? "FIFO",
            Convert.ToInt32(reader.GetValue(5), CultureInfo.InvariantCulture),
            Convert.ToInt32(reader.GetValue(6), CultureInfo.InvariantCulture));
    }

    /// <summary>FEFO/FIFO suggestion for an issue: the slices the
    /// auto-allocator would take, without writing anything.</summary>
    public IReadOnlyList<BatchAllocationRequest> SuggestAllocations(
        string companyId, long warehouseId, long materialId, decimal qty, string movementDate)
    {
        var tracking = GetTracking(companyId, materialId);
        using var connection = OpenConnection();
        using var tx = connection.BeginTransaction();
        var request = new StockMovementRequest(companyId.Trim(), warehouseId, materialId,
            movementDate, "SAL", string.Empty, 0m, qty);
        var plan = BatchSerialAllocator.PlanAutoAllocation(connection, tx, IsPg, request,
            tracking, materialId.ToString(CultureInfo.InvariantCulture), qty);
        tx.Rollback();
        return plan.Select(p => new BatchAllocationRequest(p.BatchNo, p.Qty, p.ExpiryDate)).ToList();
    }

    /// <summary>Batch balances for one material (optionally one
    /// warehouse), positive and zero rows alike.</summary>
    public IReadOnlyList<BatchBalanceRow> GetBatchBalances(
        string companyId, long materialId, long? warehouseId = null)
    {
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        if (warehouseId is not null)
        {
            cmd.CommandText = $"""
                SELECT b.batch_id, b.batch_no, b.supplier_batch_no, b.mfg_date, b.expiry_date,
                       b.status, bb.warehouse_id, bb.qty_on_hand
                FROM stock_batch_balances bb
                JOIN stock_batches b ON b.batch_id = bb.batch_id
                WHERE bb.company_id = @c AND b.material_id = @m AND bb.warehouse_id = @w
                  AND b.is_deleted = {(IsPg ? "FALSE" : "0")}
                ORDER BY b.batch_id, bb.warehouse_id
                """;
            Add(cmd, "@w", warehouseId.Value);
        }
        else
        {
            cmd.CommandText = $"""
                SELECT b.batch_id, b.batch_no, b.supplier_batch_no, b.mfg_date, b.expiry_date,
                       b.status, bb.warehouse_id, bb.qty_on_hand
                FROM stock_batch_balances bb
                JOIN stock_batches b ON b.batch_id = bb.batch_id
                WHERE bb.company_id = @c AND b.material_id = @m
                  AND b.is_deleted = {(IsPg ? "FALSE" : "0")}
                ORDER BY b.batch_id, bb.warehouse_id
                """;
        }
        Add(cmd, "@c", companyId.Trim());
        Add(cmd, "@m", materialId);
        var rows = new List<BatchBalanceRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new BatchBalanceRow(
                Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                ValuationText.ToText(reader.GetValue(1)) ?? string.Empty,
                ValuationText.ToText(reader.GetValue(2)),
                ValuationText.ToText(reader.GetValue(3)),
                ValuationText.ToText(reader.GetValue(4)),
                ValuationText.ToText(reader.GetValue(5)) ?? "Active",
                Convert.ToInt64(reader.GetValue(6), CultureInfo.InvariantCulture),
                ValuationText.ToDecimal(reader.GetValue(7))));
        }
        return rows;
    }

    /// <summary>Expired and near-expiry batches with stock on hand:
    /// expiry_date before the as-of date (expired), or within the
    /// material's alert_before_expiry_days window (near expiry). This
    /// is the exact data source of the rebuilt ExpiredWin.</summary>
    public IReadOnlyList<ExpiredBatchRow> GetExpiredBatches(string companyId, DateOnly asOf)
    {
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT m.material_id, m.code, m.name_ar, COALESCE(m.default_unit, ''),
                   b.batch_id, b.batch_no, bb.warehouse_id, b.expiry_date,
                   bb.qty_on_hand, COALESCE(m.alert_before_expiry_days, 0)
            FROM stock_batch_balances bb
            JOIN stock_batches b ON b.batch_id = bb.batch_id
            JOIN materials m ON m.company_id = b.company_id AND m.material_id = b.material_id
            WHERE bb.company_id = @c AND bb.qty_on_hand > 0
              AND b.is_deleted = {(IsPg ? "FALSE" : "0")} AND m.is_deleted = {(IsPg ? "FALSE" : "0")}
              AND b.expiry_date IS NOT NULL AND b.expiry_date <> ''
            ORDER BY b.expiry_date, m.code, b.batch_no
            """;
        Add(cmd, "@c", companyId.Trim());
        var rows = new List<ExpiredBatchRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var expiryText = ValuationText.ToText(reader.GetValue(7)) ?? string.Empty;
            if (!DateOnly.TryParseExact(expiryText, "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiry))
                continue;
            var alertDays = Convert.ToInt32(reader.GetValue(9), CultureInfo.InvariantCulture);
            var daysRemaining = expiry.DayNumber - asOf.DayNumber;
            var isExpired = daysRemaining < 0;
            if (!isExpired && daysRemaining > alertDays) continue;
            rows.Add(new ExpiredBatchRow(
                Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                ValuationText.ToText(reader.GetValue(1)) ?? string.Empty,
                ValuationText.ToText(reader.GetValue(2)) ?? string.Empty,
                ValuationText.ToText(reader.GetValue(3)) ?? string.Empty,
                Convert.ToInt64(reader.GetValue(4), CultureInfo.InvariantCulture),
                ValuationText.ToText(reader.GetValue(5)) ?? string.Empty,
                Convert.ToInt64(reader.GetValue(6), CultureInfo.InvariantCulture),
                expiryText, daysRemaining,
                ValuationText.ToDecimal(reader.GetValue(8)),
                alertDays, isExpired));
        }
        return rows;
    }

    /// <summary>Trace direction 1 (batch): every movement the batch took
    /// part in, in movement order.</summary>
    public IReadOnlyList<TraceMovementRow> TraceBatch(string companyId, long batchId)
        => Trace(companyId, "a.batch_id = @id", batchId);

    /// <summary>Trace direction 1 (serial): every movement the serial
    /// took part in, in movement order.</summary>
    public IReadOnlyList<TraceMovementRow> TraceSerial(string companyId, long serialId)
        => Trace(companyId, "a.serial_id = @id", serialId);

    private IReadOnlyList<TraceMovementRow> Trace(string companyId, string filter, long id)
    {
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT m.stock_movement_id, m.movement_date, m.movement_type, m.document_no,
                   m.warehouse_id, m.qty_in, m.qty_out,
                   a.batch_id, b.batch_no, a.serial_id, s.serial_no, a.expiry_date
            FROM stock_movement_allocations a
            JOIN material_stock_movements m ON m.stock_movement_id = a.stock_movement_id
            LEFT JOIN stock_batches b ON b.batch_id = a.batch_id
            LEFT JOIN stock_serials s ON s.serial_id = a.serial_id
            WHERE m.company_id = @c AND {filter}
            ORDER BY m.movement_date, m.stock_movement_id, a.allocation_id
            """;
        Add(cmd, "@c", companyId.Trim());
        Add(cmd, "@id", id);
        var rows = new List<TraceMovementRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new TraceMovementRow(
                Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                ValuationText.ToText(reader.GetValue(1)) ?? string.Empty,
                ValuationText.ToText(reader.GetValue(2)) ?? string.Empty,
                ValuationText.ToText(reader.GetValue(3)) ?? string.Empty,
                Convert.ToInt64(reader.GetValue(4), CultureInfo.InvariantCulture),
                ValuationText.ToDecimal(reader.GetValue(5)),
                ValuationText.ToDecimal(reader.GetValue(6)),
                reader.IsDBNull(7) ? null : Convert.ToInt64(reader.GetValue(7), CultureInfo.InvariantCulture),
                ValuationText.ToText(reader.GetValue(8)),
                reader.IsDBNull(9) ? null : Convert.ToInt64(reader.GetValue(9), CultureInfo.InvariantCulture),
                ValuationText.ToText(reader.GetValue(10)),
                ValuationText.ToText(reader.GetValue(11))));
        }
        return rows;
    }

    /// <summary>Trace direction 2: the batches/serials of one movement.</summary>
    public IReadOnlyList<StockAllocationRecord> GetMovementAllocations(long movementId)
    {
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT a.allocation_id, a.stock_movement_id, a.batch_id, b.batch_no,
                   a.serial_id, s.serial_no, a.qty, a.expiry_date
            FROM stock_movement_allocations a
            LEFT JOIN stock_batches b ON b.batch_id = a.batch_id
            LEFT JOIN stock_serials s ON s.serial_id = a.serial_id
            WHERE a.stock_movement_id = @mov
            ORDER BY a.allocation_id
            """;
        Add(cmd, "@mov", movementId);
        var rows = new List<StockAllocationRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new StockAllocationRecord(
                Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                reader.IsDBNull(2) ? null : Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture),
                ValuationText.ToText(reader.GetValue(3)),
                reader.IsDBNull(4) ? null : Convert.ToInt64(reader.GetValue(4), CultureInfo.InvariantCulture),
                ValuationText.ToText(reader.GetValue(5)),
                ValuationText.ToDecimal(reader.GetValue(6)),
                ValuationText.ToText(reader.GetValue(7))));
        }
        return rows;
    }

    /// <summary>The serial's current state: status and location (the
    /// "current location" half of trace direction 1 for serials).</summary>
    public (string Status, long? WarehouseId, long? PartyId)? GetSerialState(
        string companyId, long materialId, string serialNo)
    {
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT status, current_warehouse_id, party_id FROM stock_serials
            WHERE company_id = @c AND material_id = @m AND serial_no = @no
              AND is_deleted = {(IsPg ? "FALSE" : "0")} LIMIT 1
            """;
        Add(cmd, "@c", companyId.Trim());
        Add(cmd, "@m", materialId);
        Add(cmd, "@no", serialNo.Trim());
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return (ValuationText.ToText(reader.GetValue(0)) ?? string.Empty,
            reader.IsDBNull(1) ? null : Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
            reader.IsDBNull(2) ? null : Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture));
    }

    /// <summary>The recall list for a batch: every document that issued
    /// units of it (sales / delivery movements), with quantities — the
    /// starting point of a product recall.</summary>
    public IReadOnlyList<TraceMovementRow> GetBatchRecallList(string companyId, long batchId)
        => TraceBatch(companyId, batchId).Where(r => r.QtyOut > 0).ToList();

    private static bool Truthy(object? value)
        => value is not null and not DBNull
           && Convert.ToBoolean(value, CultureInfo.InvariantCulture);

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static string NormalizeProvider(string provider) => provider.Trim().ToLowerInvariant() switch
    {
        "sqlite" or "sqlite3" => "SQLite",
        "postgres" or "postgresql" or "npgsql" => "PostgreSQL",
        _ => throw new NotSupportedException($"Unsupported database provider '{provider}'. Use SQLite or PostgreSQL.")
    };
}
