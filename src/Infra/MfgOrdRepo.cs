using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.Domain;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;
using ShouTech.Infra;

namespace ShouTech.Infrastructure;

/// <summary>
/// Provider-neutral Manufacturing persistence (SQLite + PostgreSQL):
/// parameterized SQL only, company scoping on every statement,
/// single-transaction writes. Posting follows the StkPost /
/// CommercialDocumentRepository stock convention: on-hand is
/// SUM(qty_in - qty_out) over material_stock_movements (mirrored in
/// material_stock_balances), materials are resolved by company + code and
/// warehouses by id / code / first-active fallback.
/// </summary>
public sealed class MfgOrderRepository : IMfgOrderRepository
{
    /// <summary>All mfg_orders columns in ReadOrder ordinal order
    /// (034 base + 058 staged-workflow columns).</summary>
    private const string OrderColumns =
        "id, doc_no, doc_date, item_code, item_name, qty, unit_code, warehouse_code, overhead_amount, standard_unit_cost, total_cost, unit_cost, status, notes, " +
        "bom_id, bom_version, planned_start_date, planned_end_date, sales_order_doc_id, sales_order_no, cost_center, source_warehouse_code, wip_warehouse_code, target_warehouse_code, scrap_warehouse_code, input_document_kind, output_document_kind, produced_qty, accumulated_cost, wip_balance, variance_amount, standard_variance";

    private readonly string _provider;
    private readonly string _connectionString;

    public MfgOrderRepository(string provider, string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("A database connection string is required.", nameof(connectionString));
        _provider = NormalizeProvider(provider);
        _connectionString = connectionString;
    }

    internal string Provider => _provider;
    internal string ConnectionString => _connectionString;

    private string NotDeleted => _provider == "PostgreSQL" ? "is_deleted = false" : "is_deleted = 0";
    private string NowExpr => _provider == "PostgreSQL" ? "now()" : "datetime('now')";

    // ------------------------------------------------------------------
    // Queries
    // ------------------------------------------------------------------
    public async Task<IReadOnlyList<MfgOrder>> GetAllAsync(string companyId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            $"SELECT {OrderColumns} FROM mfg_orders WHERE company_id = @company AND {NotDeleted} ORDER BY doc_no DESC");
        Add(command, "@company", companyId.Trim());
        var result = new List<MfgOrder>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(ReadOrder(reader));
        foreach (var order in result)
            order.Lines = await ReadLinesAsync(connection, order.Id, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<MfgOrder?> GetByDocNoAsync(string companyId, int docNo, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            $"SELECT {OrderColumns} FROM mfg_orders WHERE company_id = @company AND doc_no = @doc AND {NotDeleted}");
        Add(command, "@company", companyId.Trim());
        Add(command, "@doc", docNo);
        MfgOrder? order = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                order = ReadOrder(reader);
        }
        if (order is not null)
            order.Lines = await ReadLinesAsync(connection, order.Id, cancellationToken).ConfigureAwait(false);
        return order;
    }

    /// <summary>Returns a non-consuming preview for the next manufacturing order number.</summary>
    public async Task<int> GetNextDocNoAsync(string companyId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        // Preview only: never touch the sequence allocator while a user is
        // merely opening or resetting the entry form. SaveDraftAsync assigns
        // the definitive number under its write transaction.
        await using var command = CreateCommand(connection, null,
            "SELECT COALESCE(MAX(doc_no), 0) + 1 FROM mfg_orders WHERE company_id = @company");
        Add(command, "@company", companyId.Trim());
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return checked(Convert.ToInt32(value, CultureInfo.InvariantCulture));
    }

    // ------------------------------------------------------------------
    // Save draft
    // ------------------------------------------------------------------
    public async Task SaveDraftAsync(string companyId, MfgOrder order, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(order);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string? existingStatus = null;
            await using (var check = CreateCommand(connection, transaction,
                "SELECT status FROM mfg_orders WHERE company_id = @company AND id = @id"))
            {
                Add(check, "@company", companyId.Trim());
                Add(check, "@id", FormatGuid(order.Id));
                var value = await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                existingStatus = value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
            }
            if (existingStatus is not null && existingStatus != MfgOrderStatuses.Draft)
                throw new InvalidOperationException($"Manufacturing order in status '{existingStatus}' cannot be edited; cancel and re-create it.");

            if (existingStatus is null)
            {
                // The number shown by the form is only a preview. Allocate
                // the definitive value inside this save transaction so two
                // users opening New simultaneously cannot collide.
                await using var next = CreateCommand(connection, transaction,
                    "SELECT COALESCE(MAX(doc_no), 0) + 1 FROM mfg_orders WHERE company_id = @company");
                Add(next, "@company", companyId.Trim());
                order.DocNo = Convert.ToInt32(await next.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            }
            else
            {
                await using var dup = CreateCommand(connection, transaction,
                    $"SELECT COUNT(*) FROM mfg_orders WHERE company_id = @company AND doc_no = @doc AND id <> @id AND {NotDeleted}");
                Add(dup, "@company", companyId.Trim());
                Add(dup, "@doc", order.DocNo);
                Add(dup, "@id", FormatGuid(order.Id));
                if (Convert.ToInt64(await dup.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) > 0)
                    throw new InvalidOperationException($"Manufacturing order number {order.DocNo} already exists for this company.");
            }

            ComputeTotals(order);
            if (existingStatus is null)
            {
                await using var insert = CreateCommand(connection, transaction,
                    "INSERT INTO mfg_orders (id, company_id, doc_no, doc_date, item_code, item_name, qty, unit_code, warehouse_code, overhead_amount, standard_unit_cost, total_cost, unit_cost, status, notes, bom_id, bom_version, planned_start_date, planned_end_date, sales_order_doc_id, sales_order_no, cost_center, source_warehouse_code, wip_warehouse_code, target_warehouse_code, scrap_warehouse_code, input_document_kind, output_document_kind, produced_qty, accumulated_cost, wip_balance, variance_amount, standard_variance, created_by) VALUES (@id, @company, @doc, @date, @item, @itemName, @qty, @unit, @warehouse, @overhead, @std, @total, @unitCost, @status, @notes, @bomId, @bomVersion, @plannedStart, @plannedEnd, @soDocId, @soNo, @costCenter, @srcWhs, @wipWhs, @tgtWhs, @scrWhs, @inputKind, @outputKind, @producedQty, @accumulated, @wipBalance, @variance, @stdVariance, @user)");
                BindOrder(insert, companyId, order, userId);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await using var update = CreateCommand(connection, transaction,
                    $"UPDATE mfg_orders SET doc_no = @doc, doc_date = @date, item_code = @item, item_name = @itemName, qty = @qty, unit_code = @unit, warehouse_code = @warehouse, overhead_amount = @overhead, standard_unit_cost = @std, total_cost = @total, unit_cost = @unitCost, notes = @notes, bom_id = @bomId, bom_version = @bomVersion, planned_start_date = @plannedStart, planned_end_date = @plannedEnd, sales_order_doc_id = @soDocId, sales_order_no = @soNo, cost_center = @costCenter, source_warehouse_code = @srcWhs, wip_warehouse_code = @wipWhs, target_warehouse_code = @tgtWhs, scrap_warehouse_code = @scrWhs, input_document_kind = @inputKind, output_document_kind = @outputKind, produced_qty = @producedQty, accumulated_cost = @accumulated, wip_balance = @wipBalance, variance_amount = @variance, standard_variance = @stdVariance, updated_by = @user, updated_at = {NowExpr} WHERE company_id = @company AND id = @id AND status = 'Draft' AND {NotDeleted}");
                BindOrder(update, companyId, order, userId);
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new InvalidOperationException("Manufacturing order could not be updated (missing, deleted, or no longer draft).");
                await using var deleteLines = CreateCommand(connection, transaction,
                    "DELETE FROM mfg_order_lines WHERE order_id = @id");
                Add(deleteLines, "@id", FormatGuid(order.Id));
                await deleteLines.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var lineNo = 0;
            foreach (var line in order.Lines)
            {
                line.OrderId = order.Id;
                line.LineNo = ++lineNo;
                if (line.Id == Guid.Empty) line.Id = Guid.NewGuid();
                line.Amount = decimal.Round(line.Qty * line.UnitCost, 2);
                await using var insertLine = CreateCommand(connection, transaction,
                    "INSERT INTO mfg_order_lines (id, order_id, line_no, line_kind, item_code, item_name, qty, unit_code, unit_cost, standard_unit_cost, amount, consumed_qty, produced_qty, warehouse_code, cost_share_pct, debit_account_code, credit_account_code, cost_center) VALUES (@id, @order, @lineNo, @kind, @item, @itemName, @qty, @unit, @cost, @std, @amount, @consumed, @produced, @warehouse, @share, @debit, @credit, @costCenter)");
                Add(insertLine, "@id", FormatGuid(line.Id));
                Add(insertLine, "@order", FormatGuid(order.Id));
                Add(insertLine, "@lineNo", line.LineNo);
                Add(insertLine, "@kind", line.LineKind);
                Add(insertLine, "@item", line.ItemCode.Trim());
                Add(insertLine, "@itemName", (object?)NullIfBlank(line.ItemName) ?? DBNull.Value);
                Add(insertLine, "@qty", line.Qty);
                Add(insertLine, "@unit", (object?)NullIfBlank(line.UnitCode) ?? DBNull.Value);
                Add(insertLine, "@cost", line.UnitCost);
                Add(insertLine, "@std", line.StandardUnitCost);
                Add(insertLine, "@amount", line.Amount);
                Add(insertLine, "@consumed", line.ConsumedQty);
                Add(insertLine, "@produced", line.ProducedQty);
                Add(insertLine, "@warehouse", (object?)NullIfBlank(line.WarehouseCode) ?? DBNull.Value);
                Add(insertLine, "@share", line.CostSharePct);
                Add(insertLine, "@debit", (object?)NullIfBlank(line.DebitAccountCode) ?? DBNull.Value);
                Add(insertLine, "@credit", (object?)NullIfBlank(line.CreditAccountCode) ?? DBNull.Value);
                Add(insertLine, "@costCenter", (object?)NullIfBlank(line.CostCenter) ?? DBNull.Value);
                await insertLine.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    // ------------------------------------------------------------------
    // Post: consume raw + produce finished in ONE transaction.
    // ------------------------------------------------------------------
    public async Task PostAsync(string companyId, Guid orderId, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        if (orderId == Guid.Empty) throw new ArgumentException("Order is required.", nameof(orderId));
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var order = await LoadOrderAsync(connection, transaction, companyId, orderId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Manufacturing order was not found for this company.");
            if (order.Status != MfgOrderStatuses.Draft)
                throw new InvalidOperationException($"Only a draft manufacturing order can be posted (current status: '{order.Status}').");
            if (order.Lines.Count == 0)
                throw new InvalidOperationException("A manufacturing order requires at least one raw-material line.");

            var warehouseId = await ResolveWarehouseIdAsync(connection, transaction, companyId, order.WarehouseCode, cancellationToken).ConfigureAwait(false);
            var documentNo = $"MO-{order.DocNo}";
            var dateText = order.DocDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            ComputeTotals(order);

            // 1) Validate + consume every RAW line through the ONE
            //    valuation engine (Step 6, Phase B 053) — the private
            //    moving-average copy this repository used to carry is
            //    gone; consumption is costed per material cost_method.
            //    A non-stockable line (service material — owner rule,
            //    2026-11-17) contributes its cost to the order totals
            //    but never moves stock.
            //    Unit equivalents (Step 11, sweep MFG-2): stock moves in
            //    BASE units — a line expressed in a non-base unit consumes
            //    qty × equivalent(that unit), resolved by the SAME
            //    UnitEquivalents rule the invoice/chain paths use. The
            //    line keeps its display unit and display quantity on the
            //    order; only the stock crossing is converted. The
            //    availability guard compares base quantities, because
            //    on-hand is a base-unit figure.
            var rawEngineCosts = new List<(MfgOrderLine Line, decimal Cost)>();
            foreach (var line in order.Lines.Where(l => l.LineKind == MfgLineKinds.Raw))
            {
                var materialId = await ResolveMaterialIdAsync(connection, transaction, companyId, line.ItemCode, cancellationToken).ConfigureAwait(false);
                if (!await IsStockableAsync(connection, transaction, companyId, materialId, cancellationToken).ConfigureAwait(false))
                    continue;
                var equivalent = await UnitEquivalents.ResolveAsync(connection, transaction, companyId, materialId, line.UnitCode, cancellationToken).ConfigureAwait(false);
                var baseQty = line.Qty * equivalent;
                var onHand = await GetOnHandAsync(connection, transaction, companyId, warehouseId, materialId, cancellationToken).ConfigureAwait(false);
                if (onHand < baseQty)
                    throw new InvalidOperationException(
                        $"Insufficient stock for material '{line.ItemCode.Trim()}': required {baseQty} (base units), on hand {onHand} in warehouse {warehouseId}.");
                // Batch/serial (054): raw batch issues auto-allocate
                // FEFO/FIFO in the engine and the allocations are the
                // genealogy link (raw batch -> this order -> finished
                // batch); serial raw auto-picks in-stock serials.
                var consumed = StockValuationEngine.Apply(connection, transaction, _provider,
                    new StockMovementRequest(companyId.Trim(), warehouseId, materialId, dateText,
                        "MFG_CONSUME", documentNo, QtyIn: 0m, QtyOut: baseQty,
                        Note: $"Manufacturing order {documentNo} consumption",
                        AutoAllocateSerials: true, Actor: userId));
                rawEngineCosts.Add((line, -consumed.CostAmount));
            }

            // 2) Produce the finished item at the computed unit cost — unless
            //    the finished item itself is non-stock (a service): the order
            //    still posts its costs and status, but nothing enters stock.
            //    The receipt also crosses into stock in BASE units: the
            //    produced base quantity is order.Qty × equivalent(finished
            //    unit), and the receipt cost is the order's total cost
            //    restated per BASE unit (TotalCost / base qty), so the
            //    finished stock carries exactly the order's total cost.
            var finishedId = await ResolveMaterialIdAsync(connection, transaction, companyId, order.ItemCode, cancellationToken).ConfigureAwait(false);
            if (await IsStockableAsync(connection, transaction, companyId, finishedId, cancellationToken).ConfigureAwait(false))
            {
                var finishedEquivalent = await UnitEquivalents.ResolveAsync(connection, transaction, companyId, finishedId, order.UnitCode, cancellationToken).ConfigureAwait(false);
                var finishedBaseQty = order.Qty * finishedEquivalent;
                var finishedBaseUnitCost = finishedEquivalent == 1m
                    ? order.UnitCost
                    : decimal.Round(order.TotalCost / finishedBaseQty, 4, MidpointRounding.AwayFromZero);
                // Batch/serial (054): a batch-tracked finished item is
                // produced INTO a batch named after the order (design:
                // batch no defaults to the order's doc no) — together
                // with the raw allocations above this is the batch
                // genealogy. A serial-tracked finished item receives
                // system-generated serials MO-<doc>-<n> (Phenix allows
                // system-generated serials), one per base unit.
                var finishedTracking = BatchSerialCapture.LoadTracking(
                    connection, transaction, _provider, companyId.Trim(), finishedId);
                IReadOnlyList<BatchAllocationRequest>? finishedBatches = null;
                IReadOnlyList<SerialSelection>? finishedSerials = null;
                if (finishedTracking.HasBatch)
                    finishedBatches = new[] { new BatchAllocationRequest(documentNo, finishedBaseQty) };
                if (finishedTracking.HasSerial)
                {
                    if (finishedBaseQty != decimal.Truncate(finishedBaseQty))
                        throw new InvalidOperationException(
                            $"Finished material '{order.ItemCode.Trim()}' is serial-tracked: the produced quantity must be a whole number of units.");
                    finishedSerials = Enumerable.Range(1, (int)finishedBaseQty)
                        .Select(i => new SerialSelection($"{documentNo}-{i}"))
                        .ToList();
                }
                // Quality gate (Phase B 056): a QC-required finished
                // item is produced INTO the quarantine warehouse, not
                // sellable stock, and an InProcess inspection is
                // created against the order in this transaction;
                // completion releases it to the order warehouse.
                var qcProfile = QualityGate.LoadProfile(connection, transaction, _provider, companyId.Trim(), finishedId);
                var produceWarehouseId = warehouseId;
                long? quarantineId = null;
                if (qcProfile.QualityCheckRequired)
                {
                    var q = QualityGate.EnsureQuarantineWarehouse(connection, transaction, _provider, companyId.Trim());
                    if (q > 0)
                    {
                        quarantineId = q;
                        produceWarehouseId = q;
                    }
                }
                var produced = StockValuationEngine.Apply(connection, transaction, _provider,
                    new StockMovementRequest(companyId.Trim(), produceWarehouseId, finishedId, dateText,
                        "MFG_PRODUCE", documentNo, QtyIn: finishedBaseQty, QtyOut: 0m,
                        ReceiptUnitCost: finishedBaseUnitCost,
                        Note: $"Manufacturing order {documentNo} production",
                        BatchAllocations: finishedBatches,
                        Serials: finishedSerials, Actor: userId));
                if (quarantineId is not null)
                {
                    var batchId = produced.AllocationRecords
                        .FirstOrDefault(a => a.BatchId is not null)?.BatchId;
                    QualityGate.EnsureInspection(connection, transaction, _provider, companyId.Trim(),
                        QualityInspectionTypes.InProcess, "MFG", order.Id.ToString("D"), documentNo,
                        finishedId, batchId, warehouseId, quarantineId, null,
                        finishedBaseQty, dateText, userId);
                }
            }

            // 3) Persist computed costs + status (compare-and-swap on Draft).
            await using var update = CreateCommand(connection, transaction,
                $"UPDATE mfg_orders SET total_cost = @total, unit_cost = @unitCost, status = 'Posted', updated_by = @user, updated_at = {NowExpr} WHERE company_id = @company AND id = @id AND status = 'Draft' AND {NotDeleted}");
            Add(update, "@total", order.TotalCost);
            Add(update, "@unitCost", order.UnitCost);
            Add(update, "@user", userId);
            Add(update, "@company", companyId.Trim());
            Add(update, "@id", FormatGuid(order.Id));
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("Manufacturing order changed while posting.");
            order.Status = MfgOrderStatuses.Posted;

            // Real GL posting (Step 3, J-2): completion transfers the raw
            // materials' value into finished inventory in this same
            // transaction — Dr Inventory (finished item's acc_inventory)
            // / Cr Inventory (each raw item's acc_inventory). Step 6:
            // the amounts are the valuation engine's consumption costs
            // (per material cost_method), not the order lines' entered
            // unit costs, so GL and the stock layer agree. Overhead
            // stays in the expense accounts where it was incurred.
            var rawTotal = decimal.Round(rawEngineCosts.Sum(r => r.Cost), 2);
            if (rawTotal > 0)
            {
                var companyGuid = ResolveCompanyGuid(connection, transaction, companyId);
                var glLines = new List<GlLineRequest>
                {
                    new(MaterialInventoryAccount(connection, transaction, companyId, order.ItemCode), rawTotal, 0m)
                };
                foreach (var (rawLine, rawCost) in rawEngineCosts.Where(r => r.Cost > 0))
                    glLines.Add(new GlLineRequest(
                        MaterialInventoryAccount(connection, transaction, companyId, rawLine.ItemCode), 0m, decimal.Round(rawCost, 2)));
                GlPosting.PostInTransaction(connection, transaction, _provider,
                    new GlDocumentRequest(companyGuid, order.DocDate,
                        $"MFG {documentNo}", "MFG", order.Id.ToString("D"), documentNo,
                        "SAR", 1m, glLines),
                    userId);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    // ------------------------------------------------------------------
    // Cancel: draft -> status only; posted -> reversal movements.
    // ------------------------------------------------------------------
    public async Task CancelAsync(string companyId, Guid orderId, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        if (orderId == Guid.Empty) throw new ArgumentException("Order is required.", nameof(orderId));
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var order = await LoadOrderAsync(connection, transaction, companyId, orderId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Manufacturing order was not found for this company.");
            if (order.Status == MfgOrderStatuses.Cancelled)
                throw new InvalidOperationException("Manufacturing order is already cancelled.");
            if (order.Status == MfgOrderStatuses.Closed)
                throw StageError(order, "cancel");

            // Staged orders (058): reverse exactly what the stages
            // posted — produced finished goods and by-products out,
            // consumed raw materials back in at their original rates
            // and into their original batches/serials, then every
            // staged GL entry reversed through the shared path. Scrap
            // receipts are NOT reversed: scrap is zero-value waste and
            // its record stands (the Cancel event notes it).
            if (order.Status is MfgOrderStatuses.Confirmed or MfgOrderStatuses.InProcess or MfgOrderStatuses.Completed)
            {
                var cancelDocNo = $"MO-{order.DocNo}";
                var cancelDate = DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                var targetWarehouseId = await ResolveWarehouseIdAsync(connection, transaction, companyId, order.EffectiveTargetWarehouse, cancellationToken).ConfigureAwait(false);

                if (order.ProducedQty > 0)
                {
                    var finishedId = await ResolveMaterialIdAsync(connection, transaction, companyId, order.ItemCode, cancellationToken).ConfigureAwait(false);
                    if (await IsStockableAsync(connection, transaction, companyId, finishedId, cancellationToken).ConfigureAwait(false))
                    {
                        var finishedEquivalent = await UnitEquivalents.ResolveAsync(connection, transaction, companyId, finishedId, order.UnitCode, cancellationToken).ConfigureAwait(false);
                        var finishedBaseQty = order.ProducedQty * finishedEquivalent;
                        var produceWarehouseId = targetWarehouseId;
                        var onHand = await GetOnHandAsync(connection, transaction, companyId, produceWarehouseId, finishedId, cancellationToken).ConfigureAwait(false);
                        if (onHand < finishedBaseQty)
                        {
                            // QC-quarantined output sits in quarantine.
                            var qcProfile = QualityGate.LoadProfile(connection, transaction, _provider, companyId.Trim(), finishedId);
                            var quarantineId = qcProfile.QualityCheckRequired
                                ? QualityGate.EnsureQuarantineWarehouse(connection, transaction, _provider, companyId.Trim())
                                : 0;
                            if (quarantineId > 0 && await GetOnHandAsync(connection, transaction, companyId, quarantineId, finishedId, cancellationToken).ConfigureAwait(false) >= finishedBaseQty)
                                produceWarehouseId = quarantineId;
                            else
                                throw new InvalidOperationException(
                                    $"Cannot cancel: produced quantity of '{order.ItemCode.Trim()}' is no longer fully on hand (required {finishedBaseQty} base units, on hand {onHand}).");
                        }
                        StockValuationEngine.Apply(connection, transaction, _provider,
                            new StockMovementRequest(companyId.Trim(), produceWarehouseId, finishedId, cancelDate,
                                "MFG_PRODUCE_REV", cancelDocNo, QtyIn: 0m, QtyOut: finishedBaseQty,
                                Note: $"Manufacturing order {cancelDocNo} cancellation",
                                AutoAllocateSerials: true, Actor: userId));
                    }
                }

                foreach (var line in order.Lines.Where(l => l.LineKind == MfgLineKinds.ByProduct && l.ProducedQty > 0))
                {
                    var materialId = await ResolveMaterialIdAsync(connection, transaction, companyId, line.ItemCode, cancellationToken).ConfigureAwait(false);
                    if (!await IsStockableAsync(connection, transaction, companyId, materialId, cancellationToken).ConfigureAwait(false))
                        continue;
                    var equivalent = await UnitEquivalents.ResolveAsync(connection, transaction, companyId, materialId, line.UnitCode, cancellationToken).ConfigureAwait(false);
                    var baseQty = line.ProducedQty * equivalent;
                    var onHand = await GetOnHandAsync(connection, transaction, companyId, targetWarehouseId, materialId, cancellationToken).ConfigureAwait(false);
                    if (onHand < baseQty)
                        throw new InvalidOperationException(
                            $"Cannot cancel: produced by-product '{line.ItemCode.Trim()}' is no longer fully on hand (required {baseQty} base units, on hand {onHand}).");
                    StockValuationEngine.Apply(connection, transaction, _provider,
                        new StockMovementRequest(companyId.Trim(), targetWarehouseId, materialId, cancelDate,
                            "MFG_PRODUCE_REV", cancelDocNo, QtyIn: 0m, QtyOut: baseQty,
                            Note: $"Manufacturing order {cancelDocNo} cancellation (by-product)",
                            AutoAllocateSerials: true, Actor: userId));
                }

                foreach (var line in order.Lines.Where(l => l.LineKind == MfgLineKinds.Raw && l.ConsumedQty > 0))
                {
                    var materialId = await ResolveMaterialIdAsync(connection, transaction, companyId, line.ItemCode, cancellationToken).ConfigureAwait(false);
                    if (!await IsStockableAsync(connection, transaction, companyId, materialId, cancellationToken).ConfigureAwait(false))
                        continue;
                    var warehouseId = await ResolveWarehouseIdAsync(connection, transaction, companyId,
                        line.WarehouseCode ?? order.EffectiveSourceWarehouse, cancellationToken).ConfigureAwait(false);
                    var equivalent = await UnitEquivalents.ResolveAsync(connection, transaction, companyId, materialId, line.UnitCode, cancellationToken).ConfigureAwait(false);
                    var baseQty = line.ConsumedQty * equivalent;
                    var originalRate = OriginalIssueRate(connection, transaction, companyId, $"{cancelDocNo}-I", materialId, warehouseId);
                    var (batches, serials) = RebuildIssueAllocations(connection, transaction, companyId, $"{cancelDocNo}-I", materialId);
                    StockValuationEngine.Apply(connection, transaction, _provider,
                        new StockMovementRequest(companyId.Trim(), warehouseId, materialId, cancelDate,
                            "MFG_CONSUME_REV", cancelDocNo, QtyIn: baseQty, QtyOut: 0m,
                            ReceiptUnitCost: line.UnitCost, LinkedRate: originalRate,
                            Note: $"Manufacturing order {cancelDocNo} cancellation",
                            BatchAllocations: batches, Serials: serials, Actor: userId));
                }

                var companyGuid = ResolveCompanyGuid(connection, transaction, companyId);
                ReverseStagedGl(connection, transaction, companyGuid, order, userId);
                order.WipBalance = 0m;
                await using (var zeroWip = CreateCommand(connection, transaction,
                    "UPDATE mfg_orders SET wip_balance = 0 WHERE company_id = @company AND id = @id"))
                {
                    Add(zeroWip, "@company", companyId.Trim());
                    Add(zeroWip, "@id", FormatGuid(order.Id));
                    await zeroWip.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                await InsertEventAsync(connection, transaction, companyId, order, "Cancel", DateOnly.FromDateTime(DateTime.Today), order.ProducedQty, 0m, null,
                    "Staged cancel: stock + GL reversed; scrap receipts stand (zero-value waste).", userId, cancellationToken).ConfigureAwait(false);
            }

            if (order.Status == MfgOrderStatuses.Posted)
            {
                var warehouseId = await ResolveWarehouseIdAsync(connection, transaction, companyId, order.WarehouseCode, cancellationToken).ConfigureAwait(false);
                var documentNo = $"MO-{order.DocNo}";
                var dateText = DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

                // The produced quantity must still be on hand to reverse out.
                // Non-stockable items (services) were never moved by Post,
                // so there is nothing to reverse for them either. The
                // reversal crosses in BASE units exactly as Post did
                // (Step 11): the finished guard and movement use
                // order.Qty × equivalent(finished unit).
                var finishedId = await ResolveMaterialIdAsync(connection, transaction, companyId, order.ItemCode, cancellationToken).ConfigureAwait(false);
                if (await IsStockableAsync(connection, transaction, companyId, finishedId, cancellationToken).ConfigureAwait(false))
                {
                    var finishedEquivalent = await UnitEquivalents.ResolveAsync(connection, transaction, companyId, finishedId, order.UnitCode, cancellationToken).ConfigureAwait(false);
                    var finishedBaseQty = order.Qty * finishedEquivalent;
                    var finishedOnHand = await GetOnHandAsync(connection, transaction, companyId, warehouseId, finishedId, cancellationToken).ConfigureAwait(false);
                    if (finishedOnHand < finishedBaseQty)
                        throw new InvalidOperationException(
                            $"Cannot cancel: produced quantity of '{order.ItemCode.Trim()}' is no longer fully on hand (required {finishedBaseQty} base units, on hand {finishedOnHand}).");
                    // Batch/serial (054): the reversal consumes the
                    // order's own finished batch (FEFO finds only it)
                    // and auto-picks the produced serials.
                    StockValuationEngine.Apply(connection, transaction, _provider,
                        new StockMovementRequest(companyId.Trim(), warehouseId, finishedId, dateText,
                            "MFG_PRODUCE_REV", documentNo, QtyIn: 0m, QtyOut: finishedBaseQty,
                            Note: $"Manufacturing order {documentNo} cancellation",
                            AutoAllocateSerials: true, Actor: userId));
                }

                foreach (var line in order.Lines.Where(l => l.LineKind == MfgLineKinds.Raw))
                {
                    var materialId = await ResolveMaterialIdAsync(connection, transaction, companyId, line.ItemCode, cancellationToken).ConfigureAwait(false);
                    if (!await IsStockableAsync(connection, transaction, companyId, materialId, cancellationToken).ConfigureAwait(false))
                        continue;
                    // Raw materials re-enter at their ORIGINAL consumption
                    // rate (the rate the order consumed them at), never at
                    // a re-typed line cost (Step 6 return rule) — in BASE
                    // units, symmetric with the consumption (Step 11); the
                    // fallback receipt cost is the line cost restated per
                    // base unit for the case the linked rate is missing.
                    var equivalent = await UnitEquivalents.ResolveAsync(connection, transaction, companyId, materialId, line.UnitCode, cancellationToken).ConfigureAwait(false);
                    var (baseQty, baseUnitCost) = UnitEquivalents.ToBase(line.Qty, line.UnitCost, equivalent);
                    var originalRate = StockValuationEngine.TryGetDocumentRate(
                        connection, transaction, _provider, companyId.Trim(), documentNo, materialId, warehouseId, issued: true);
                    // Batch/serial (054): raw re-enters its ORIGINAL
                    // batches/serials via the order's document number.
                    StockValuationEngine.Apply(connection, transaction, _provider,
                        new StockMovementRequest(companyId.Trim(), warehouseId, materialId, dateText,
                            "MFG_CONSUME_REV", documentNo, QtyIn: baseQty, QtyOut: 0m,
                            ReceiptUnitCost: baseUnitCost, LinkedRate: originalRate,
                            Note: $"Manufacturing order {documentNo} cancellation",
                            OriginalDocumentNo: documentNo, Actor: userId));
                }
            }

            // GL reversal (Step 3): cancelling a posted order reverses
            // its completion entry through the shared Reverse path, in
            // this same transaction. Orders posted before Step 3 have no
            // GL entry and simply have nothing to reverse.
            if (order.Status == MfgOrderStatuses.Posted)
            {
                var companyGuid = ResolveCompanyGuid(connection, transaction, companyId);
                var glEntryId = GlPosting.FindEntryBySource(connection, transaction, _provider, companyGuid, "MFG", order.Id.ToString("D"));
                if (glEntryId is not null)
                    GlPosting.ReverseInTransaction(connection, transaction, _provider, glEntryId.Value, userId,
                        $"Manufacturing order {order.DocNo} cancelled");
            }

            await using var update = CreateCommand(connection, transaction,
                $"UPDATE mfg_orders SET status = 'Cancelled', updated_by = @user, updated_at = {NowExpr} WHERE company_id = @company AND id = @id AND status = @from AND {NotDeleted}");
            Add(update, "@user", userId);
            Add(update, "@company", companyId.Trim());
            Add(update, "@id", FormatGuid(order.Id));
            Add(update, "@from", order.Status);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("Manufacturing order changed while cancelling.");

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    // ==================================================================
    // Staged workflow (migration 058): Confirm -> Issue -> Finish ->
    // Close, WIP + variance GL through GlPosting. Every stage is ONE
    // transaction. GL mapping (canonical template codes, GlAccounts):
    //   Issue : Dr WIP (1211, fallback Inventory 1203)
    //           / Cr Inventory (per component acc_inventory, fallback 1203)
    //           service-line costs ride the same entry as
    //           Cr Applied Overhead (409, fallback COGS 401).
    //   Finish: overhead absorption Dr WIP / Cr Applied Overhead (409->401);
    //           receipt Dr Finished Goods (1212 -> material acc_inventory
    //           -> 1203) + Dr by-product inventory / Cr WIP at the pool.
    //   Close : remaining WIP settled Dr/Cr Variance (406 -> 401) vs WIP.
    // ==================================================================

    public async Task<MfgStageResult> ConfirmAsync(string companyId, Guid orderId, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        if (orderId == Guid.Empty) throw new ArgumentException("Order is required.", nameof(orderId));
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var order = await LoadOrderAsync(connection, transaction, companyId, orderId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Manufacturing order was not found for this company.");
            if (order.Status != MfgOrderStatuses.Draft)
                throw StageError(order, "confirm");
            if (order.Lines.Count == 0)
                throw new InvalidOperationException("A manufacturing order requires at least one raw-material line.");

            // Staged actuals start from zero at Confirm: the header's
            // TotalCost/UnitCost up to now are the DRAFT ESTIMATE
            // (lines x entered unit costs + overhead, ComputeTotals),
            // while the staged flow accumulates ACTUAL finished-goods
            // value receipt by receipt. Carrying the estimate into the
            // actuals would corrupt every downstream figure (unit cost,
            // standard variance at Close).
            order.TotalCost = 0m;
            order.UnitCost = 0m;
            order.AccumulatedCost = 0m;
            order.WipBalance = 0m;
            order.ProducedQty = 0m;

            var expenseLines = order.Lines.Where(l => l.LineKind == MfgLineKinds.Cost && l.Amount > 0m).ToList();
            var configuredExpenses = expenseLines.Where(l =>
                !string.IsNullOrWhiteSpace(l.DebitAccountCode) && !string.IsNullOrWhiteSpace(l.CreditAccountCode)).ToList();
            if (order.BomId.HasValue && configuredExpenses.Count != expenseLines.Count)
                throw new InvalidOperationException("Every template production expense must have debit and credit accounts before confirmation.");
            if (configuredExpenses.Count > 0)
            {
                var companyGuid = ResolveCompanyGuid(connection, transaction, companyId);
                var expenseJournalLines = new List<GlLineRequest>();
                foreach (var expense in configuredExpenses)
                {
                    var amount = decimal.Round(expense.Amount, 2, MidpointRounding.AwayFromZero);
                    if (amount <= 0m) continue;
                    var costCenter = NullIfBlank(expense.CostCenter) ?? NullIfBlank(order.CostCenter);
                    expenseJournalLines.Add(new GlLineRequest(expense.DebitAccountCode!.Trim(), amount, 0m, costCenter, expense.ItemName));
                    expenseJournalLines.Add(new GlLineRequest(expense.CreditAccountCode!.Trim(), 0m, amount, costCenter, expense.ItemName));
                }
                if (expenseJournalLines.Count > 0)
                {
                    var expenseDocNo = $"MO-{order.DocNo}-EXP";
                    GlPosting.PostInTransaction(connection, transaction, _provider,
                        new GlDocumentRequest(companyGuid, order.DocDate, $"Manufacturing expenses {expenseDocNo}",
                            "MFG-EXP", order.Id.ToString("D"), expenseDocNo, "SAR", 1m, expenseJournalLines),
                        userId);
                }
            }

            await UpdateOrderStageAsync(connection, transaction, companyId, order, MfgOrderStatuses.Draft, MfgOrderStatuses.Confirmed, userId, cancellationToken).ConfigureAwait(false);
            await InsertEventAsync(connection, transaction, companyId, order, "Confirm", order.DocDate, 0m, 0m, null, null, userId, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new MfgStageResult(order.Id, MfgOrderStatuses.Confirmed, 0m, null, order.WipBalance, order.ProducedQty);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<MfgStageResult> IssueAsync(string companyId, Guid orderId, IReadOnlyList<MfgIssueLine> issues, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        if (orderId == Guid.Empty) throw new ArgumentException("Order is required.", nameof(orderId));
        ArgumentNullException.ThrowIfNull(issues);
        if (issues.Count == 0) throw new InvalidOperationException("At least one issue line is required.");
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var order = await LoadOrderAsync(connection, transaction, companyId, orderId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Manufacturing order was not found for this company.");
            if (order.Status is not (MfgOrderStatuses.Confirmed or MfgOrderStatuses.InProcess))
                throw StageError(order, "issue materials");
            var expectedStatus = order.Status;
            var seq = await NextEventSeqAsync(connection, transaction, order.Id, cancellationToken).ConfigureAwait(false);
            var documentNo = $"MO-{order.DocNo}";
            var issueDocNo = $"{documentNo}-I{seq}";
            var dateText = order.DocDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var companyGuid = ResolveCompanyGuid(connection, transaction, companyId);
            var wipAccount = PickAccount(connection, transaction, companyGuid, GlAccounts.WorkInProgress, GlAccounts.Inventory);
            var overheadAccount = PickAccount(connection, transaction, companyGuid, GlAccounts.AppliedManufacturingOverhead, GlAccounts.Cogs);

            // Validate aggregate quantities, not each request in isolation:
            // a forged caller could otherwise repeat one line ID and split
            // an over-issue across individually valid rows.
            foreach (var group in issues.GroupBy(i => i.LineId))
            {
                var line = order.Lines.FirstOrDefault(l => l.Id == group.Key)
                    ?? throw new InvalidOperationException("An issue line does not belong to this order.");
                var requested = group.Sum(i => i.Qty);
                if (requested > line.RemainingToIssue)
                    throw new AppError(ErrorCodes.MfgIssueExceedsRequired,
                        $"Issuing {requested} of '{line.ItemCode.Trim()}' exceeds the remaining required quantity {line.RemainingToIssue}.",
                        line.ItemCode.Trim(), requested.ToString(CultureInfo.InvariantCulture));
            }

            var stockCost = 0m;
            var serviceCost = 0m;
            var glLines = new List<GlLineRequest>();
            var issuedQtyTotal = 0m;
            foreach (var issue in issues)
            {
                if (issue.Qty <= 0)
                    throw new InvalidOperationException("Issue quantities must be greater than zero.");
                var line = order.Lines.FirstOrDefault(l => l.Id == issue.LineId)
                    ?? throw new InvalidOperationException("An issue line does not belong to this order.");
                if (line.LineKind != MfgLineKinds.Raw)
                    throw new InvalidOperationException($"Only raw-material lines can be issued ('{line.ItemCode}' is {line.LineKind}).");
                if (issue.Qty > line.RemainingToIssue)
                    throw new AppError(ErrorCodes.MfgIssueExceedsRequired,
                        $"Issuing {issue.Qty} of '{line.ItemCode.Trim()}' exceeds the remaining required quantity {line.RemainingToIssue}.",
                        line.ItemCode.Trim(), issue.Qty.ToString(CultureInfo.InvariantCulture));
                var materialId = await ResolveMaterialIdAsync(connection, transaction, companyId, line.ItemCode, cancellationToken).ConfigureAwait(false);
                issuedQtyTotal += issue.Qty;
                if (!await IsStockableAsync(connection, transaction, companyId, materialId, cancellationToken).ConfigureAwait(false))
                {
                    // Service material: cost enters WIP at its entered
                    // rate with no stock movement (owner service rule);
                    // its GL leg credits Applied Overhead in this entry.
                    var entered = decimal.Round(issue.Qty * line.UnitCost, 2);
                    serviceCost += entered;
                    line.ConsumedQty += issue.Qty;
                    continue;
                }
                var warehouseId = await ResolveWarehouseIdAsync(connection, transaction, companyId,
                    line.WarehouseCode ?? order.EffectiveSourceWarehouse, cancellationToken).ConfigureAwait(false);
                var equivalent = await UnitEquivalents.ResolveAsync(connection, transaction, companyId, materialId, line.UnitCode, cancellationToken).ConfigureAwait(false);
                var baseQty = issue.Qty * equivalent;
                var onHand = await GetOnHandAsync(connection, transaction, companyId, warehouseId, materialId, cancellationToken).ConfigureAwait(false);
                if (onHand < baseQty)
                    throw new InvalidOperationException(
                        $"Insufficient stock for material '{line.ItemCode.Trim()}': required {baseQty} (base units), on hand {onHand} in warehouse {warehouseId}.");
                // 054: batch issues auto-allocate FEFO/FIFO inside the
                // engine; serial issues auto-pick in-stock serials. The
                // allocations written are the genealogy link.
                var consumed = StockValuationEngine.Apply(connection, transaction, _provider,
                    new StockMovementRequest(companyId.Trim(), warehouseId, materialId, dateText,
                        "MFG_CONSUME", issueDocNo, QtyIn: 0m, QtyOut: baseQty,
                        Note: $"Manufacturing order {documentNo} issue {seq}",
                        AutoAllocateSerials: true, Actor: userId));
                var cost = -consumed.CostAmount;
                stockCost += cost;
                glLines.Add(new GlLineRequest(
                    MaterialInventoryAccount(connection, transaction, companyId, line.ItemCode), 0m, decimal.Round(cost, 2)));
                line.ConsumedQty += issue.Qty;
            }

            var total = decimal.Round(stockCost + serviceCost, 2);
            string? journalDocNo = null;
            if (total > 0)
            {
                var lines = new List<GlLineRequest>
                {
                    new(wipAccount, total, 0m, NullIfBlank(order.CostCenter))
                };
                lines.AddRange(glLines);
                if (serviceCost > 0)
                    lines.Add(new GlLineRequest(overheadAccount, 0m, decimal.Round(serviceCost, 2)));
                var posted = GlPosting.PostInTransaction(connection, transaction, _provider,
                    new GlDocumentRequest(companyGuid, order.DocDate,
                        $"MFG issue {issueDocNo}", "MFG-ISSUE", order.Id.ToString("D") + "#" + seq, issueDocNo,
                        "SAR", 1m, lines),
                    userId);
                journalDocNo = posted.DocumentNumber;
            }

            foreach (var issue in issues)
            {
                var line = order.Lines.First(l => l.Id == issue.LineId);
                await UpdateLineActualAsync(connection, transaction, line, cancellationToken).ConfigureAwait(false);
            }
            order.AccumulatedCost = decimal.Round(order.AccumulatedCost + total, 2);
            order.WipBalance = decimal.Round(order.WipBalance + total, 2);
            order.Status = MfgOrderStatuses.InProcess;
            await UpdateOrderStageAsync(connection, transaction, companyId, order, expectedStatus, MfgOrderStatuses.InProcess, userId, cancellationToken).ConfigureAwait(false);
            await InsertEventAsync(connection, transaction, companyId, order, "Issue", order.DocDate, issuedQtyTotal, total, journalDocNo, issueDocNo, userId, cancellationToken, seq).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new MfgStageResult(order.Id, order.Status, total, journalDocNo, order.WipBalance, order.ProducedQty);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<MfgStageResult> FinishAsync(string companyId, Guid orderId, decimal finishedQty, IReadOnlyList<MfgReceiptLine> receipts, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        if (orderId == Guid.Empty) throw new ArgumentException("Order is required.", nameof(orderId));
        receipts ??= Array.Empty<MfgReceiptLine>();
        if (finishedQty <= 0) throw new InvalidOperationException("The finished quantity must be greater than zero.");
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var order = await LoadOrderAsync(connection, transaction, companyId, orderId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Manufacturing order was not found for this company.");
            if (order.Status != MfgOrderStatuses.InProcess)
                throw StageError(order, "finish production");
            var remainingPlanned = order.Qty - order.ProducedQty;
            if (finishedQty > remainingPlanned)
                throw new AppError(ErrorCodes.MfgFinishExceedsPlanned,
                    $"Receiving {finishedQty} of '{order.ItemCode.Trim()}' exceeds the remaining planned quantity {remainingPlanned}.",
                    order.ItemCode.Trim(), finishedQty.ToString(CultureInfo.InvariantCulture));

            // Apply the planned-quantity ceiling across the whole request;
            // duplicate line IDs must not bypass it by passing individually.
            foreach (var group in receipts.GroupBy(r => r.LineId))
            {
                var line = order.Lines.FirstOrDefault(l => l.Id == group.Key)
                    ?? throw new InvalidOperationException("A receipt line does not belong to this order.");
                var requested = group.Sum(r => r.Qty);
                if (requested <= 0 || line.ProducedQty + requested > line.Qty)
                    throw new AppError(ErrorCodes.MfgFinishExceedsPlanned,
                        $"Receiving {requested} of '{line.ItemCode.Trim()}' exceeds the remaining planned quantity {line.Qty - line.ProducedQty}.",
                        line.ItemCode.Trim(), requested.ToString(CultureInfo.InvariantCulture));
            }

            var receiptLines = new List<(MfgOrderLine Line, decimal Qty)>();
            foreach (var receipt in receipts)
            {
                if (receipt.Qty <= 0)
                    throw new InvalidOperationException("Receipt quantities must be greater than zero.");
                var line = order.Lines.FirstOrDefault(l => l.Id == receipt.LineId)
                    ?? throw new InvalidOperationException("A receipt line does not belong to this order.");
                if (line.LineKind is not (MfgLineKinds.ByProduct or MfgLineKinds.Scrap))
                    throw new InvalidOperationException($"Only by-product / scrap lines can be received at finish ('{line.ItemCode}' is {line.LineKind}).");
                if (line.ProducedQty + receipt.Qty > line.Qty)
                    throw new AppError(ErrorCodes.MfgFinishExceedsPlanned,
                        $"Receiving {receipt.Qty} of '{line.ItemCode.Trim()}' exceeds the line's planned quantity {line.Qty}.",
                        line.ItemCode.Trim(), receipt.Qty.ToString(CultureInfo.InvariantCulture));
                receiptLines.Add((line, receipt.Qty));
            }

            var seq = await NextEventSeqAsync(connection, transaction, order.Id, cancellationToken).ConfigureAwait(false);
            var documentNo = $"MO-{order.DocNo}";
            var finishDocNo = $"{documentNo}-F{seq}";
            var dateText = order.DocDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var companyGuid = ResolveCompanyGuid(connection, transaction, companyId);
            var wipAccount = PickAccount(connection, transaction, companyGuid, GlAccounts.WorkInProgress, GlAccounts.Inventory);
            var overheadAccount = PickAccount(connection, transaction, companyGuid, GlAccounts.AppliedManufacturingOverhead, GlAccounts.Cogs);
            var fgAccount = FinishedGoodsAccount(connection, transaction, companyGuid, companyId, order.ItemCode);

            // Overhead absorption: header overhead + COST lines are
            // absorbed pro-rata to production; the completing receipt
            // takes the exact remainder so absorption is exact in total.
            var absorbable = decimal.Round(order.OverheadAmount + order.Lines
                .Where(l => l.LineKind == MfgLineKinds.Cost).Sum(l => l.Amount), 2);
            var issuedTotal = await SumEventAmountsAsync(connection, transaction, order.Id, "Issue", cancellationToken).ConfigureAwait(false);
            var absorbedBefore = decimal.Round(order.AccumulatedCost - issuedTotal, 2);
            var completesOrder = order.ProducedQty + finishedQty == order.Qty;
            var overheadShare = absorbable <= 0 ? 0m
                : completesOrder ? decimal.Round(absorbable - absorbedBefore, 2)
                : decimal.Round(absorbable * finishedQty / order.Qty, 2);
            if (overheadShare < 0) overheadShare = 0m;

            string? journalDocNo = null;
            if (overheadShare > 0)
            {
                var absorptionLines = new List<GlLineRequest>
                {
                    new(wipAccount, overheadShare, 0m, NullIfBlank(order.CostCenter))
                };
                var costLines = order.Lines.Where(l => l.LineKind == MfgLineKinds.Cost && l.Amount > 0m).ToList();
                var headerAllocation = absorbable > 0m
                    ? decimal.Round(overheadShare * order.OverheadAmount / absorbable, 2, MidpointRounding.AwayFromZero)
                    : 0m;
                headerAllocation = Math.Clamp(headerAllocation, 0m, overheadShare);
                var expenseAllocationTarget = decimal.Round(overheadShare - headerAllocation, 2, MidpointRounding.AwayFromZero);
                var expenseBase = costLines.Sum(l => l.Amount);
                var allocatedExpense = 0m;
                for (var index = 0; index < costLines.Count; index++)
                {
                    var expense = costLines[index];
                    var amount = index == costLines.Count - 1
                        ? expenseAllocationTarget - allocatedExpense
                        : expenseBase > 0m
                            ? decimal.Round(expenseAllocationTarget * expense.Amount / expenseBase, 2, MidpointRounding.AwayFromZero)
                            : 0m;
                    if (amount <= 0m) continue;
                    allocatedExpense += amount;
                    var account = NullIfBlank(expense.DebitAccountCode) ?? overheadAccount;
                    absorptionLines.Add(new GlLineRequest(account, 0m, amount,
                        NullIfBlank(expense.CostCenter) ?? NullIfBlank(order.CostCenter), expense.ItemName));
                }
                if (headerAllocation > 0m)
                    absorptionLines.Add(new GlLineRequest(overheadAccount, 0m, headerAllocation, NullIfBlank(order.CostCenter)));
                var posted = GlPosting.PostInTransaction(connection, transaction, _provider,
                    new GlDocumentRequest(companyGuid, order.DocDate,
                        $"MFG overhead {finishDocNo}", "MFG-OVHD", order.Id.ToString("D") + "#" + seq, finishDocNo,
                        "SAR", 1m, absorptionLines),
                    userId);
                journalDocNo = posted.DocumentNumber;
            }

            // The receipt pool: WIP on hand plus this receipt's absorbed
            // overhead. The completing receipt takes the whole pool.
            var pool = decimal.Round(order.WipBalance + overheadShare, 2);
            var fgPool = completesOrder ? pool
                : decimal.Round(pool * finishedQty / remainingPlanned, 2);

            // By-products take their cost share out of the pool
            // (ERPNext model); the finished good keeps the remainder, so
            // FG unit cost = (materials + overhead - by-product share)
            // / qty for the receipt.
            var byProducts = receiptLines.Where(r => r.Line.LineKind == MfgLineKinds.ByProduct).ToList();
            var sharePct = byProducts.Sum(r => r.Line.CostSharePct);
            if (sharePct > 100m)
                throw new InvalidOperationException("By-product cost shares cannot exceed 100% in one receipt.");
            var bpTotal = decimal.Round(fgPool * sharePct / 100m, 2);
            var bpValues = new List<decimal>();
            var bpValueByLine = new Dictionary<Guid, decimal>();
            var bpAssigned = 0m;
            for (var i = 0; i < byProducts.Count; i++)
            {
                var value = i == byProducts.Count - 1
                    ? bpTotal - bpAssigned
                    : decimal.Round(fgPool * byProducts[i].Line.CostSharePct / 100m, 2);
                bpValues.Add(value);
                bpValueByLine[byProducts[i].Line.Id] = value;
                bpAssigned += value;
            }
            var fgValue = decimal.Round(fgPool - bpTotal, 2);

            var targetWarehouseId = await ResolveWarehouseIdAsync(connection, transaction, companyId,
                order.EffectiveTargetWarehouse, cancellationToken).ConfigureAwait(false);

            // ---- Stock: finished good receipt at the exact pool value.
            var finishedId = await ResolveMaterialIdAsync(connection, transaction, companyId, order.ItemCode, cancellationToken).ConfigureAwait(false);
            if (await IsStockableAsync(connection, transaction, companyId, finishedId, cancellationToken).ConfigureAwait(false))
            {
                var finishedEquivalent = await UnitEquivalents.ResolveAsync(connection, transaction, companyId, finishedId, order.UnitCode, cancellationToken).ConfigureAwait(false);
                var finishedBaseQty = finishedQty * finishedEquivalent;
                var finishedTracking = BatchSerialCapture.LoadTracking(
                    connection, transaction, _provider, companyId.Trim(), finishedId);
                IReadOnlyList<BatchAllocationRequest>? finishedBatches = null;
                IReadOnlyList<SerialSelection>? finishedSerials = null;
                if (finishedTracking.HasBatch)
                    finishedBatches = new[] { new BatchAllocationRequest(documentNo, finishedBaseQty) };
                if (finishedTracking.HasSerial)
                {
                    if (finishedBaseQty != decimal.Truncate(finishedBaseQty))
                        throw new InvalidOperationException(
                            $"Finished material '{order.ItemCode.Trim()}' is serial-tracked: the produced quantity must be a whole number of units.");
                    var producedBaseBefore = order.ProducedQty * finishedEquivalent;
                    finishedSerials = Enumerable.Range(1, (int)finishedBaseQty)
                        .Select(i => new SerialSelection($"{documentNo}-{(int)producedBaseBefore + i}"))
                        .ToList();
                }
                // Quality gate (056): QC-required output is produced
                // into quarantine, exactly like the one-step Post.
                var qcProfile = QualityGate.LoadProfile(connection, transaction, _provider, companyId.Trim(), finishedId);
                var produceWarehouseId = targetWarehouseId;
                long? quarantineId = null;
                if (qcProfile.QualityCheckRequired)
                {
                    var q = QualityGate.EnsureQuarantineWarehouse(connection, transaction, _provider, companyId.Trim());
                    if (q > 0) { quarantineId = q; produceWarehouseId = q; }
                }
                var produced = StockValuationEngine.Apply(connection, transaction, _provider,
                    new StockMovementRequest(companyId.Trim(), produceWarehouseId, finishedId, dateText,
                        "MFG_PRODUCE", finishDocNo, QtyIn: finishedBaseQty, QtyOut: 0m,
                        ReceiptCostAmount: fgValue,
                        Note: $"Manufacturing order {documentNo} finish {seq}",
                        BatchAllocations: finishedBatches,
                        Serials: finishedSerials, Actor: userId));
                if (quarantineId is not null)
                {
                    var batchId = produced.AllocationRecords.FirstOrDefault(a => a.BatchId is not null)?.BatchId;
                    QualityGate.EnsureInspection(connection, transaction, _provider, companyId.Trim(),
                        QualityInspectionTypes.InProcess, "MFG", order.Id.ToString("D"), finishDocNo,
                        finishedId, batchId, targetWarehouseId, quarantineId, null,
                        finishedBaseQty, dateText, userId);
                }
            }

            // ---- Stock: by-product receipts at their share value, and
            // scrap receipts at zero value into the scrap warehouse.
            for (var i = 0; i < receiptLines.Count; i++)
            {
                var (line, qty) = receiptLines[i];
                var materialId = await ResolveMaterialIdAsync(connection, transaction, companyId, line.ItemCode, cancellationToken).ConfigureAwait(false);
                if (!await IsStockableAsync(connection, transaction, companyId, materialId, cancellationToken).ConfigureAwait(false))
                {
                    line.ProducedQty += qty;
                    continue;
                }
                var equivalent = await UnitEquivalents.ResolveAsync(connection, transaction, companyId, materialId, line.UnitCode, cancellationToken).ConfigureAwait(false);
                var baseQty = qty * equivalent;
                var isScrap = line.LineKind == MfgLineKinds.Scrap;
                var outputWarehouse = NullIfBlank(line.WarehouseCode) ??
                    (isScrap ? order.EffectiveScrapWarehouse : order.EffectiveTargetWarehouse);
                var warehouseId = await ResolveWarehouseIdAsync(connection, transaction, companyId,
                    outputWarehouse, cancellationToken).ConfigureAwait(false);
                var tracking = BatchSerialCapture.LoadTracking(connection, transaction, _provider, companyId.Trim(), materialId);
                IReadOnlyList<BatchAllocationRequest>? batches = null;
                IReadOnlyList<SerialSelection>? serials = null;
                if (tracking.HasBatch)
                    batches = new[] { new BatchAllocationRequest(finishDocNo, baseQty) };
                if (tracking.HasSerial)
                {
                    if (baseQty != decimal.Truncate(baseQty))
                        throw new InvalidOperationException(
                            $"Material '{line.ItemCode.Trim()}' is serial-tracked: the received quantity must be a whole number of units.");
                    serials = Enumerable.Range(1, (int)baseQty)
                        .Select(n => new SerialSelection($"{finishDocNo}-{line.ItemCode.Trim()}-{n}"))
                        .ToList();
                }
                var value = isScrap ? 0m : bpValueByLine.GetValueOrDefault(line.Id);
                StockValuationEngine.Apply(connection, transaction, _provider,
                    new StockMovementRequest(companyId.Trim(), warehouseId, materialId, dateText,
                        isScrap ? "MFG_SCRAP" : "MFG_PRODUCE", finishDocNo, QtyIn: baseQty, QtyOut: 0m,
                        ReceiptCostAmount: value,
                        Note: $"Manufacturing order {documentNo} {(isScrap ? "scrap" : "by-product")} {seq}",
                        BatchAllocations: batches, Serials: serials, Actor: userId));
                line.ProducedQty += qty;
            }

            // ---- GL: receipt entry Dr FG (+ Dr by-products) / Cr WIP.
            if (fgPool > 0)
            {
                var glLines = new List<GlLineRequest>();
                if (fgValue > 0) glLines.Add(new GlLineRequest(fgAccount, fgValue, 0m));
                for (var i = 0; i < byProducts.Count; i++)
                {
                    if (bpValues[i] <= 0) continue;
                    glLines.Add(new GlLineRequest(
                        MaterialInventoryAccount(connection, transaction, companyId, byProducts[i].Line.ItemCode),
                        bpValues[i], 0m));
                }
                glLines.Add(new GlLineRequest(wipAccount, 0m, fgPool, NullIfBlank(order.CostCenter)));
                var posted = GlPosting.PostInTransaction(connection, transaction, _provider,
                    new GlDocumentRequest(companyGuid, order.DocDate,
                        $"MFG finish {finishDocNo}", "MFG-FINISH", order.Id.ToString("D") + "#" + seq, finishDocNo,
                        "SAR", 1m, glLines),
                    userId);
                journalDocNo = posted.DocumentNumber;
            }

            foreach (var (line, _) in receiptLines)
                await UpdateLineActualAsync(connection, transaction, line, cancellationToken).ConfigureAwait(false);
            order.ProducedQty += finishedQty;
            order.AccumulatedCost = decimal.Round(order.AccumulatedCost + overheadShare, 2);
            order.WipBalance = decimal.Round(pool - fgPool, 2);
            order.TotalCost = decimal.Round(order.TotalCost + fgValue, 2);
            order.UnitCost = order.ProducedQty > 0
                ? decimal.Round(order.TotalCost / order.ProducedQty, 4, MidpointRounding.AwayFromZero)
                : 0m;
            order.Status = order.ProducedQty >= order.Qty ? MfgOrderStatuses.Completed : MfgOrderStatuses.InProcess;
            await UpdateOrderStageAsync(connection, transaction, companyId, order, MfgOrderStatuses.InProcess, order.Status, userId, cancellationToken).ConfigureAwait(false);
            await InsertEventAsync(connection, transaction, companyId, order, "Finish", order.DocDate, finishedQty, fgPool, journalDocNo, finishDocNo, userId, cancellationToken, seq).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new MfgStageResult(order.Id, order.Status, fgPool, journalDocNo, order.WipBalance, order.ProducedQty);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<MfgStageResult> CloseAsync(string companyId, Guid orderId, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        if (orderId == Guid.Empty) throw new ArgumentException("Order is required.", nameof(orderId));
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var order = await LoadOrderAsync(connection, transaction, companyId, orderId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Manufacturing order was not found for this company.");
            if (order.Status is not (MfgOrderStatuses.InProcess or MfgOrderStatuses.Completed))
                throw StageError(order, "close");
            var expectedCloseStatus = order.Status;
            var seq = await NextEventSeqAsync(connection, transaction, order.Id, cancellationToken).ConfigureAwait(false);
            var documentNo = $"MO-{order.DocNo}";
            var closeDocNo = $"{documentNo}-C{seq}";
            var companyGuid = ResolveCompanyGuid(connection, transaction, companyId);
            var wipAccount = PickAccount(connection, transaction, companyGuid, GlAccounts.WorkInProgress, GlAccounts.Inventory);
            var varianceAccount = PickAccount(connection, transaction, companyGuid, GlAccounts.ManufacturingVariance, GlAccounts.Cogs);

            var variance = decimal.Round(order.WipBalance, 2);
            var standardVariance = order.StandardUnitCost > 0
                ? decimal.Round(order.TotalCost - order.ProducedQty * order.StandardUnitCost, 2)
                : 0m;
            string? journalDocNo = null;
            if (variance != 0m)
            {
                var amount = Math.Abs(variance);
                var lines = variance > 0
                    ? new GlLineRequest[]
                    {
                        new(varianceAccount, amount, 0m, NullIfBlank(order.CostCenter)),
                        new(wipAccount, 0m, amount)
                    }
                    : new GlLineRequest[]
                    {
                        new(wipAccount, amount, 0m, NullIfBlank(order.CostCenter)),
                        new(varianceAccount, 0m, amount)
                    };
                var posted = GlPosting.PostInTransaction(connection, transaction, _provider,
                    new GlDocumentRequest(companyGuid, order.DocDate,
                        $"MFG close {closeDocNo}", "MFG-CLOSE", order.Id.ToString("D") + "#" + seq, closeDocNo,
                        "SAR", 1m, lines),
                    userId);
                journalDocNo = posted.DocumentNumber;
            }

            order.VarianceAmount = variance;
            order.StandardVariance = standardVariance;
            order.WipBalance = 0m;
            order.Status = MfgOrderStatuses.Closed;
            await UpdateOrderStageAsync(connection, transaction, companyId, order, expectedCloseStatus, MfgOrderStatuses.Closed, userId, cancellationToken).ConfigureAwait(false);
            await InsertEventAsync(connection, transaction, companyId, order, "Close", order.DocDate, order.ProducedQty, variance, journalDocNo, closeDocNo, userId, cancellationToken, seq).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new MfgStageResult(order.Id, order.Status, variance, journalDocNo, 0m, order.ProducedQty);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IReadOnlyList<MfgProductionEvent>> GetEventsAsync(string companyId, Guid orderId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            "SELECT id, order_id, event_type, event_date, seq, qty, amount, journal_doc_no, notes, created_by FROM mfg_production_events WHERE company_id = @company AND order_id = @order ORDER BY seq");
        Add(command, "@company", companyId.Trim());
        Add(command, "@order", FormatGuid(orderId));
        var result = new List<MfgProductionEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new MfgProductionEvent(
                ReadGuid(reader, 0), ReadGuid(reader, 1),
                ReadString(reader, 2) ?? string.Empty, ReadString(reader, 3) ?? string.Empty,
                Convert.ToInt32(reader.GetValue(4), CultureInfo.InvariantCulture),
                ReadDecimal(reader, 5), ReadDecimal(reader, 6),
                ReadString(reader, 7), ReadString(reader, 8), ReadString(reader, 9)));
        }
        return result;
    }

    // ------------------------------------------------------------------
    // Staged helpers
    // ------------------------------------------------------------------
    private static AppError StageError(MfgOrder order, string action)
        => new(ErrorCodes.MfgOrderStageInvalid,
            $"Manufacturing order {order.DocNo} cannot {action} in status '{order.Status}'.",
            order.DocNo.ToString(CultureInfo.InvariantCulture), order.Status);

    /// <summary>Step-3 optional-leg account pick: the manufacturing code
    /// when the company's chart carries it postable, else the fallback.</summary>
    private string PickAccount(DbConnection connection, DbTransaction transaction, Guid companyGuid, string preferred, string fallback)
        => GlPosting.TryResolveAccount(connection, transaction, _provider, companyGuid, preferred) is not null
            ? preferred
            : fallback;

    /// <summary>The finished-goods account for an order: the finished
    /// material's own acc_inventory when postable, else Finished Goods
    /// 1212, else the canonical Inventory 1203.</summary>
    private string FinishedGoodsAccount(DbConnection connection, DbTransaction transaction, Guid companyGuid, string companyId, string itemCode)
    {
        var configured = MaterialInventoryAccount(connection, transaction, companyId, itemCode);
        if (!string.Equals(configured, GlAccounts.Inventory, StringComparison.OrdinalIgnoreCase)
            && GlPosting.TryResolveAccount(connection, transaction, _provider, companyGuid, configured) is not null)
            return configured;
        return PickAccount(connection, transaction, companyGuid, GlAccounts.FinishedGoods, GlAccounts.Inventory);
    }

    private async Task<int> NextEventSeqAsync(DbConnection connection, DbTransaction transaction, Guid orderId, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction,
            "SELECT COALESCE(MAX(seq), 0) + 1 FROM mfg_production_events WHERE order_id = @order");
        Add(command, "@order", FormatGuid(orderId));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private async Task<decimal> SumEventAmountsAsync(DbConnection connection, DbTransaction transaction, Guid orderId, string eventType, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction,
            "SELECT COALESCE(SUM(amount), 0) FROM mfg_production_events WHERE order_id = @order AND event_type = @type");
        Add(command, "@order", FormatGuid(orderId));
        Add(command, "@type", eventType);
        return Convert.ToDecimal(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private async Task InsertEventAsync(DbConnection connection, DbTransaction transaction, string companyId, MfgOrder order,
        string eventType, DateOnly eventDate, decimal qty, decimal amount, string? journalDocNo, string? notes,
        string userId, CancellationToken cancellationToken, int? seqOverride = null)
    {
        var seq = seqOverride ?? await NextEventSeqAsync(connection, transaction, order.Id, cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, transaction,
            "INSERT INTO mfg_production_events (id, company_id, order_id, event_type, event_date, seq, qty, amount, journal_doc_no, notes, created_by) VALUES (@id, @company, @order, @type, @date, @seq, @qty, @amount, @journal, @notes, @user)");
        Add(command, "@id", FormatGuid(Guid.NewGuid()));
        Add(command, "@company", companyId.Trim());
        Add(command, "@order", FormatGuid(order.Id));
        Add(command, "@type", eventType);
        Add(command, "@date", eventDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add(command, "@seq", seq);
        Add(command, "@qty", qty);
        Add(command, "@amount", amount);
        Add(command, "@journal", (object?)journalDocNo ?? DBNull.Value);
        Add(command, "@notes", (object?)notes ?? DBNull.Value);
        Add(command, "@user", userId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Persists the stage-owned header fields (status, produced,
    /// accumulated, WIP, totals, variance) with a status CAS guard.</summary>
    private async Task UpdateOrderStageAsync(DbConnection connection, DbTransaction transaction, string companyId, MfgOrder order, string expectedStatus, string newStatus, string userId, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction,
            $"UPDATE mfg_orders SET status = @status, produced_qty = @produced, accumulated_cost = @accumulated, wip_balance = @wip, total_cost = @total, unit_cost = @unitCost, variance_amount = @variance, standard_variance = @stdVariance, updated_by = @user, updated_at = {NowExpr} WHERE company_id = @company AND id = @id AND status = @expected AND {NotDeleted}");
        Add(command, "@expected", expectedStatus);
        Add(command, "@status", newStatus);
        Add(command, "@produced", order.ProducedQty);
        Add(command, "@accumulated", order.AccumulatedCost);
        Add(command, "@wip", order.WipBalance);
        Add(command, "@total", order.TotalCost);
        Add(command, "@unitCost", order.UnitCost);
        Add(command, "@variance", order.VarianceAmount);
        Add(command, "@stdVariance", order.StandardVariance);
        Add(command, "@user", userId);
        Add(command, "@company", companyId.Trim());
        Add(command, "@id", FormatGuid(order.Id));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("Manufacturing order changed while applying the stage action.");
        order.Status = newStatus;
    }

    private async Task UpdateLineActualAsync(DbConnection connection, DbTransaction transaction, MfgOrderLine line, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction,
            "UPDATE mfg_order_lines SET consumed_qty = @consumed, produced_qty = @produced WHERE id = @id AND order_id = @order");
        Add(command, "@consumed", line.ConsumedQty);
        Add(command, "@produced", line.ProducedQty);
        Add(command, "@id", FormatGuid(line.Id));
        Add(command, "@order", FormatGuid(line.OrderId));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("Manufacturing order line changed or no longer belongs to this order.");
    }

    /// <summary>Reverses every staged GL entry of an order (issue /
    /// finish / overhead / close) through the shared Reverse path.
    /// Called by Cancel for staged orders, inside the cancel
    /// transaction, after the stock reversals.</summary>
    private void ReverseStagedGl(DbConnection connection, DbTransaction transaction, Guid companyGuid, MfgOrder order, string userId)
    {
        var ids = new List<Guid>();
        using (var find = CreateCommand(connection, transaction,
            "SELECT journal_entry_id FROM journal_entries WHERE company_id = @company AND source_doc_type IN ('MFG-ISSUE','MFG-FINISH','MFG-OVHD','MFG-CLOSE') AND source_doc_id LIKE @prefix AND reversal_of_entry_id IS NULL AND status = 1"))
        {
            Add(find, "@company", _provider == "PostgreSQL" ? (object)companyGuid : companyGuid.ToString("D"));
            Add(find, "@prefix", order.Id.ToString("D") + "#%");
            using var reader = find.ExecuteReader();
            while (reader.Read())
                ids.Add(Guid.Parse(Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!));
        }
        foreach (var id in ids)
            GlPosting.ReverseInTransaction(connection, transaction, _provider, id, userId,
                $"Manufacturing order {order.DocNo} cancelled");
    }

    /// <summary>The original consumption rate of one material across all
    /// of the order's issue documents (for cancel re-entry at the
    /// original rate, the Step-6 return rule).</summary>
    private decimal? OriginalIssueRate(DbConnection connection, DbTransaction transaction, string companyId, string docPrefix, long materialId, long warehouseId)
    {
        using var command = CreateCommand(connection, transaction, $"""
            SELECT COALESCE(SUM(ABS(COALESCE(cost_amount, 0))), 0), COALESCE(SUM(qty_out), 0)
            FROM material_stock_movements
            WHERE company_id = @c AND document_no LIKE @prefix AND material_id = @m
              AND warehouse_id = @w AND qty_out > 0 AND is_deleted = {(_provider == "PostgreSQL" ? "FALSE" : "0")}
            """);
        Add(command, "@c", companyId.Trim());
        Add(command, "@prefix", docPrefix + "%");
        Add(command, "@m", materialId);
        Add(command, "@w", warehouseId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var amount = Convert.ToDecimal(reader.GetValue(0), CultureInfo.InvariantCulture);
        var qty = Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture);
        return qty > 0 ? decimal.Round(amount / qty, 6, MidpointRounding.AwayFromZero) : null;
    }

    /// <summary>Rebuilds the batch / serial allocations a cancel
    /// re-entry must carry so raw materials return to their ORIGINAL
    /// batches and serials (054 genealogy): aggregates the order's
    /// issue allocations for one material.</summary>
    private (IReadOnlyList<BatchAllocationRequest>? Batches, IReadOnlyList<SerialSelection>? Serials)
        RebuildIssueAllocations(DbConnection connection, DbTransaction transaction, string companyId, string docPrefix, long materialId)
    {
        var batches = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var serials = new List<string>();
        using var command = CreateCommand(connection, transaction, $"""
            SELECT b.batch_no, a.qty, s.serial_no
            FROM stock_movement_allocations a
            JOIN material_stock_movements m ON m.stock_movement_id = a.stock_movement_id
            LEFT JOIN stock_batches b ON b.batch_id = a.batch_id
            LEFT JOIN stock_serials s ON s.serial_id = a.serial_id
            WHERE m.company_id = @c AND m.material_id = @mat AND m.document_no LIKE @prefix
              AND m.qty_out > 0 AND m.is_deleted = {(_provider == "PostgreSQL" ? "FALSE" : "0")}
            """);
        Add(command, "@c", companyId.Trim());
        Add(command, "@mat", materialId);
        Add(command, "@prefix", docPrefix + "%");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var batchNo = reader.IsDBNull(0) ? null : reader.GetString(0);
            var qty = Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture);
            var serialNo = reader.IsDBNull(2) ? null : reader.GetString(2);
            if (batchNo is not null)
                batches[batchNo] = batches.GetValueOrDefault(batchNo) + Math.Abs(qty);
            if (serialNo is not null && !serials.Contains(serialNo))
                serials.Add(serialNo);
        }
        return (batches.Count > 0 ? batches.Select(kv => new BatchAllocationRequest(kv.Key, kv.Value)).ToList() : null,
                serials.Count > 0 ? serials.Select(s => new SerialSelection(s)).ToList() : null);
    }

    // ------------------------------------------------------------------
    // Stock helpers (StkPost convention)
    // ------------------------------------------------------------------
    /// <summary>
    /// Whether the material is stock-tracked, mirroring the guard the
    /// invoice posting applies: not stocked, flagged as a service, or
    /// typed as a Service (any one of the three views the material card
    /// keeps in agreement) means non-stock. Service materials contribute
    /// cost to an order but never move stock (owner rule, 2026-11-17).
    /// </summary>
    private async Task<bool> IsStockableAsync(DbConnection connection, DbTransaction transaction, string companyId, long materialId, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction,
            $"SELECT is_stocked, is_service, item_type FROM materials WHERE company_id = @company AND material_id = @material AND {NotDeleted} LIMIT 1");
        Add(command, "@company", companyId.Trim());
        Add(command, "@material", materialId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return false;

        var isStocked = !reader.IsDBNull(0) && Convert.ToBoolean(reader.GetValue(0), CultureInfo.InvariantCulture);
        var isService = !reader.IsDBNull(1) && Convert.ToBoolean(reader.GetValue(1), CultureInfo.InvariantCulture);
        var itemType = reader.IsDBNull(2) ? null : reader.GetValue(2)?.ToString();
        return isStocked
               && !isService
               && !string.Equals(itemType, "Service", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<long> ResolveMaterialIdAsync(DbConnection connection, DbTransaction transaction, string companyId, string itemCode, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(itemCode))
            throw new InvalidOperationException("A material code is required for stock posting.");
        await using var command = CreateCommand(connection, transaction,
            $"SELECT material_id FROM materials WHERE company_id = @company AND code = @code AND {NotDeleted} LIMIT 1");
        Add(command, "@company", companyId.Trim());
        Add(command, "@code", itemCode.Trim());
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is null or DBNull)
            throw new InvalidOperationException($"Material '{itemCode.Trim()}' does not exist in the current company.");
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private async Task<long> ResolveWarehouseIdAsync(DbConnection connection, DbTransaction transaction, string companyId, string? warehouseCode, CancellationToken cancellationToken)
    {
        var code = NullIfBlank(warehouseCode);
        if (code is not null && long.TryParse(code, NumberStyles.Integer, CultureInfo.InvariantCulture, out var asId))
        {
            await using var byId = CreateCommand(connection, transaction,
                $"SELECT warehouse_id FROM warehouses WHERE company_id = @company AND warehouse_id = @id AND {NotDeleted} LIMIT 1");
            Add(byId, "@company", companyId.Trim());
            Add(byId, "@id", asId);
            var value = await byId.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (value is not null and not DBNull)
                return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        if (code is not null)
        {
            await using var byCode = CreateCommand(connection, transaction,
                $"SELECT warehouse_id FROM warehouses WHERE company_id = @company AND CAST(warehouse_code AS TEXT) = @code AND {NotDeleted} ORDER BY warehouse_id LIMIT 1");
            Add(byCode, "@company", companyId.Trim());
            Add(byCode, "@code", code);
            var value = await byCode.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (value is not null and not DBNull)
                return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        // Fallback: first live warehouse of the company (StkPost default pattern).
        await using var first = CreateCommand(connection, transaction,
            $"SELECT warehouse_id FROM warehouses WHERE company_id = @company AND {NotDeleted} ORDER BY warehouse_id LIMIT 1");
        Add(first, "@company", companyId.Trim());
        var fallback = await first.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (fallback is not null and not DBNull)
            return Convert.ToInt64(fallback, CultureInfo.InvariantCulture);
        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(code)
                ? "No warehouse exists for this company; create a warehouse before posting a manufacturing order."
                : $"Warehouse '{code}' was not found for this company.");
    }

    private async Task<decimal> GetOnHandAsync(DbConnection connection, DbTransaction transaction, string companyId, long warehouseId, long materialId, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction,
            $"SELECT COALESCE(SUM(qty_in - qty_out), 0) FROM material_stock_movements WHERE company_id = @company AND warehouse_id = @warehouse AND material_id = @material AND {NotDeleted}");
        Add(command, "@company", companyId.Trim());
        Add(command, "@warehouse", warehouseId);
        Add(command, "@material", materialId);
        return Convert.ToDecimal(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    // ------------------------------------------------------------------
    // Loading / mapping helpers
    // ------------------------------------------------------------------
    private async Task<MfgOrder?> LoadOrderAsync(DbConnection connection, DbTransaction transaction, string companyId, Guid orderId, CancellationToken cancellationToken)
    {
        MfgOrder? order = null;
        await using (var command = CreateCommand(connection, transaction,
            $"SELECT {OrderColumns} FROM mfg_orders WHERE company_id = @company AND id = @id AND {NotDeleted}{(_provider == "PostgreSQL" ? " FOR UPDATE" : string.Empty)}"))
        {
            Add(command, "@company", companyId.Trim());
            Add(command, "@id", FormatGuid(orderId));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                order = ReadOrder(reader);
        }
        if (order is not null)
            order.Lines = await ReadLinesAsync(connection, order.Id, cancellationToken, transaction).ConfigureAwait(false);
        return order;
    }

    private async Task<List<MfgOrderLine>> ReadLinesAsync(DbConnection connection, Guid orderId, CancellationToken cancellationToken, DbTransaction? transaction = null)
    {
        await using var command = CreateCommand(connection, transaction,
            "SELECT id, order_id, line_kind, item_code, item_name, qty, unit_code, unit_cost, standard_unit_cost, amount, line_no, consumed_qty, produced_qty, warehouse_code, cost_share_pct, debit_account_code, credit_account_code, cost_center FROM mfg_order_lines WHERE order_id = @order ORDER BY line_no, item_code");
        Add(command, "@order", FormatGuid(orderId));
        var result = new List<MfgOrderLine>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new MfgOrderLine
            {
                Id = ReadGuid(reader, 0),
                OrderId = ReadGuid(reader, 1),
                LineKind = ReadString(reader, 2) ?? MfgLineKinds.Raw,
                ItemCode = ReadString(reader, 3) ?? string.Empty,
                ItemName = ReadString(reader, 4),
                Qty = ReadDecimal(reader, 5),
                UnitCode = ReadString(reader, 6),
                UnitCost = ReadDecimal(reader, 7),
                StandardUnitCost = ReadDecimal(reader, 8),
                Amount = ReadDecimal(reader, 9),
                LineNo = reader.IsDBNull(10) ? 0 : Convert.ToInt32(reader.GetValue(10), CultureInfo.InvariantCulture),
                ConsumedQty = ReadDecimal(reader, 11),
                ProducedQty = ReadDecimal(reader, 12),
                WarehouseCode = ReadString(reader, 13),
                CostSharePct = ReadDecimal(reader, 14),
                DebitAccountCode = ReadString(reader, 15),
                CreditAccountCode = ReadString(reader, 16),
                CostCenter = ReadString(reader, 17)
            });
        }
        return result;
    }

    private static void ComputeTotals(MfgOrder order)
    {
        foreach (var line in order.Lines)
            if (line.LineKind is MfgLineKinds.Raw or MfgLineKinds.Cost)
                line.Amount = decimal.Round(line.Qty * line.UnitCost, 2);
            else
                line.Amount = 0m;
        order.TotalCost = decimal.Round(order.Lines
            .Where(l => l.LineKind is MfgLineKinds.Raw or MfgLineKinds.Cost)
            .Sum(l => l.Amount) + order.OverheadAmount, 2);
        order.UnitCost = order.Qty > 0 ? decimal.Round(order.TotalCost / order.Qty, 4) : 0m;
    }

    private void BindOrder(DbCommand command, string companyId, MfgOrder order, string userId)
    {
        Add(command, "@id", FormatGuid(order.Id));
        Add(command, "@company", companyId.Trim());
        Add(command, "@doc", order.DocNo);
        Add(command, "@date", order.DocDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add(command, "@item", order.ItemCode.Trim());
        Add(command, "@itemName", (object?)NullIfBlank(order.ItemName) ?? DBNull.Value);
        Add(command, "@qty", order.Qty);
        Add(command, "@unit", string.IsNullOrWhiteSpace(order.UnitCode) ? "PCS" : order.UnitCode.Trim());
        Add(command, "@warehouse", (object?)NullIfBlank(order.WarehouseCode) ?? DBNull.Value);
        Add(command, "@overhead", order.OverheadAmount);
        Add(command, "@std", order.StandardUnitCost);
        Add(command, "@total", order.TotalCost);
        Add(command, "@unitCost", order.UnitCost);
        Add(command, "@status", MfgOrderStatuses.Draft);
        Add(command, "@notes", (object?)NullIfBlank(order.Notes) ?? DBNull.Value);
        Add(command, "@bomId", order.BomId is { } bomId ? FormatGuid(bomId) : DBNull.Value);
        Add(command, "@bomVersion", (object?)order.BomVersion ?? DBNull.Value);
        Add(command, "@plannedStart", (object?)order.PlannedStartDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? DBNull.Value);
        Add(command, "@plannedEnd", (object?)order.PlannedEndDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? DBNull.Value);
        Add(command, "@soDocId", (object?)order.SalesOrderDocId ?? DBNull.Value);
        Add(command, "@soNo", (object?)NullIfBlank(order.SalesOrderNo) ?? DBNull.Value);
        Add(command, "@costCenter", (object?)NullIfBlank(order.CostCenter) ?? DBNull.Value);
        Add(command, "@srcWhs", (object?)NullIfBlank(order.SourceWarehouseCode) ?? DBNull.Value);
        Add(command, "@wipWhs", (object?)NullIfBlank(order.WipWarehouseCode) ?? DBNull.Value);
        Add(command, "@tgtWhs", (object?)NullIfBlank(order.TargetWarehouseCode) ?? DBNull.Value);
        Add(command, "@scrWhs", (object?)NullIfBlank(order.ScrapWarehouseCode) ?? DBNull.Value);
        Add(command, "@inputKind", string.IsNullOrWhiteSpace(order.InputDocumentKind) ? "MATERIAL_ISSUE" : order.InputDocumentKind.Trim());
        Add(command, "@outputKind", string.IsNullOrWhiteSpace(order.OutputDocumentKind) ? "MATERIAL_RECEIPT" : order.OutputDocumentKind.Trim());
        Add(command, "@producedQty", order.ProducedQty);
        Add(command, "@accumulated", order.AccumulatedCost);
        Add(command, "@wipBalance", order.WipBalance);
        Add(command, "@variance", order.VarianceAmount);
        Add(command, "@stdVariance", order.StandardVariance);
        Add(command, "@user", userId);
    }

    private static MfgOrder ReadOrder(DbDataReader reader) => new()
    {
        Id = ReadGuid(reader, 0),
        DocNo = Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture),
        DocDate = ReadDate(reader, 2) ?? DateOnly.FromDateTime(DateTime.Today),
        ItemCode = ReadString(reader, 3) ?? string.Empty,
        ItemName = ReadString(reader, 4),
        Qty = ReadDecimal(reader, 5),
        UnitCode = ReadString(reader, 6) ?? "PCS",
        WarehouseCode = ReadString(reader, 7),
        OverheadAmount = ReadDecimal(reader, 8),
        StandardUnitCost = ReadDecimal(reader, 9),
        TotalCost = ReadDecimal(reader, 10),
        UnitCost = ReadDecimal(reader, 11),
        Status = ReadString(reader, 12) ?? MfgOrderStatuses.Draft,
        Notes = ReadString(reader, 13),
        BomId = reader.IsDBNull(14) ? null : ReadGuid(reader, 14),
        BomVersion = reader.IsDBNull(15) ? null : Convert.ToInt32(reader.GetValue(15), CultureInfo.InvariantCulture),
        PlannedStartDate = ReadDate(reader, 16),
        PlannedEndDate = ReadDate(reader, 17),
        SalesOrderDocId = reader.IsDBNull(18) ? null : Convert.ToInt64(reader.GetValue(18), CultureInfo.InvariantCulture),
        SalesOrderNo = ReadString(reader, 19),
        CostCenter = ReadString(reader, 20),
        SourceWarehouseCode = ReadString(reader, 21),
        WipWarehouseCode = ReadString(reader, 22),
        TargetWarehouseCode = ReadString(reader, 23),
        ScrapWarehouseCode = ReadString(reader, 24),
        InputDocumentKind = ReadString(reader, 25) ?? "MATERIAL_ISSUE",
        OutputDocumentKind = ReadString(reader, 26) ?? "MATERIAL_RECEIPT",
        ProducedQty = ReadDecimal(reader, 27),
        AccumulatedCost = ReadDecimal(reader, 28),
        WipBalance = ReadDecimal(reader, 29),
        VarianceAmount = ReadDecimal(reader, 30),
        StandardVariance = ReadDecimal(reader, 31)
    };

    private static void RequireCompany(string companyId)
    {
        if (string.IsNullOrWhiteSpace(companyId))
            throw new ArgumentException("Company is required.", nameof(companyId));
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private DbConnection CreateConnection() => _provider == "SQLite"
        ? new SqliteConnection(_connectionString)
        : new NpgsqlConnection(_connectionString);

    private static DbCommand CreateCommand(DbConnection connection, DbTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        if (transaction != null) command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private object FormatGuid(Guid value) => _provider == "PostgreSQL" ? value : value.ToString("D");

    private static Guid ReadGuid(DbDataReader reader, int ordinal)
    {
        var value = reader.GetValue(ordinal);
        return value switch
        {
            Guid g => g,
            string s when Guid.TryParse(s, out var parsed) => parsed,
            _ => Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!)
        };
    }

    private static string? ReadString(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static decimal ReadDecimal(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? 0m : Convert.ToDecimal(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static DateOnly? ReadDate(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        var value = reader.GetValue(ordinal);
        return value switch
        {
            DateOnly d => d,
            DateTime dt => DateOnly.FromDateTime(dt),
            string s when s.Length >= 10 && DateOnly.TryParse(s[..10], CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

        /// <summary>Resolves the company Guid for GL posting from the
    /// company CODE this repository is keyed by.</summary>
    private Guid ResolveCompanyGuid(DbConnection connection, DbTransaction transaction, string companyCode)
    {
        using var cmd = CreateCommand(connection, transaction,
            "SELECT company_id FROM companies WHERE company_code = @code");
        Add(cmd, "@code", companyCode.Trim());
        var value = cmd.ExecuteScalar();
        if (value is null or DBNull || !Guid.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var guid) || guid == Guid.Empty)
            throw new InvalidOperationException($"Company '{companyCode.Trim()}' could not be resolved for GL posting.");
        return guid;
    }

    /// <summary>The material's acc_inventory code when it holds a code,
    /// else the canonical inventory account.</summary>
    private string MaterialInventoryAccount(DbConnection connection, DbTransaction transaction, string companyCode, string itemCode)
    {
        using var cmd = CreateCommand(connection, transaction,
            $"SELECT acc_inventory FROM materials WHERE company_id = @company AND code = @code AND {NotDeleted} LIMIT 1");
        Add(cmd, "@company", companyCode.Trim());
        Add(cmd, "@code", itemCode.Trim());
        var value = cmd.ExecuteScalar();
        var configured = value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(configured) ? GlAccounts.Inventory : configured.Trim();
    }

private static string NormalizeProvider(string provider) => provider.Trim().ToLowerInvariant() switch
    {
        "sqlite" or "sqlite3" => "SQLite",
        "postgres" or "postgresql" or "npgsql" => "PostgreSQL",
        _ => throw new NotSupportedException($"Unsupported database provider '{provider}'. Use SQLite or PostgreSQL.")
    };
}
