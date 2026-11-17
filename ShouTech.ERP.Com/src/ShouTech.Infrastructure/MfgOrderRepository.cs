using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

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
    private const int ListLimit = 1000;

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
            $"SELECT id, doc_no, doc_date, item_code, item_name, qty, unit_code, warehouse_code, overhead_amount, standard_unit_cost, total_cost, unit_cost, status, notes FROM mfg_orders WHERE company_id = @company AND {NotDeleted} ORDER BY doc_no DESC LIMIT {ListLimit}");
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
            $"SELECT id, doc_no, doc_date, item_code, item_name, qty, unit_code, warehouse_code, overhead_amount, standard_unit_cost, total_cost, unit_cost, status, notes FROM mfg_orders WHERE company_id = @company AND doc_no = @doc AND {NotDeleted}");
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

    public async Task<int> GetNextDocNoAsync(string companyId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            "SELECT COALESCE(MAX(doc_no), 0) + 1 FROM mfg_orders WHERE company_id = @company");
        Add(command, "@company", companyId.Trim());
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
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

            if (order.DocNo <= 0)
            {
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
                    "INSERT INTO mfg_orders (id, company_id, doc_no, doc_date, item_code, item_name, qty, unit_code, warehouse_code, overhead_amount, standard_unit_cost, total_cost, unit_cost, status, notes, created_by) VALUES (@id, @company, @doc, @date, @item, @itemName, @qty, @unit, @warehouse, @overhead, @std, @total, @unitCost, @status, @notes, @user)");
                BindOrder(insert, companyId, order, userId);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await using var update = CreateCommand(connection, transaction,
                    $"UPDATE mfg_orders SET doc_no = @doc, doc_date = @date, item_code = @item, item_name = @itemName, qty = @qty, unit_code = @unit, warehouse_code = @warehouse, overhead_amount = @overhead, standard_unit_cost = @std, total_cost = @total, unit_cost = @unitCost, notes = @notes, updated_by = @user, updated_at = {NowExpr} WHERE company_id = @company AND id = @id AND status = 'Draft' AND {NotDeleted}");
                BindOrder(update, companyId, order, userId);
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new InvalidOperationException("Manufacturing order could not be updated (missing, deleted, or no longer draft).");
                await using var deleteLines = CreateCommand(connection, transaction,
                    "DELETE FROM mfg_order_lines WHERE order_id = @id");
                Add(deleteLines, "@id", FormatGuid(order.Id));
                await deleteLines.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (var line in order.Lines)
            {
                line.OrderId = order.Id;
                if (line.Id == Guid.Empty) line.Id = Guid.NewGuid();
                line.Amount = decimal.Round(line.Qty * line.UnitCost, 2);
                await using var insertLine = CreateCommand(connection, transaction,
                    "INSERT INTO mfg_order_lines (id, order_id, line_kind, item_code, item_name, qty, unit_code, unit_cost, standard_unit_cost, amount) VALUES (@id, @order, @kind, @item, @itemName, @qty, @unit, @cost, @std, @amount)");
                Add(insertLine, "@id", FormatGuid(line.Id));
                Add(insertLine, "@order", FormatGuid(order.Id));
                Add(insertLine, "@kind", line.LineKind);
                Add(insertLine, "@item", line.ItemCode.Trim());
                Add(insertLine, "@itemName", (object?)NullIfBlank(line.ItemName) ?? DBNull.Value);
                Add(insertLine, "@qty", line.Qty);
                Add(insertLine, "@unit", (object?)NullIfBlank(line.UnitCode) ?? DBNull.Value);
                Add(insertLine, "@cost", line.UnitCost);
                Add(insertLine, "@std", line.StandardUnitCost);
                Add(insertLine, "@amount", line.Amount);
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
            var rawEngineCosts = new List<(MfgOrderLine Line, decimal Cost)>();
            foreach (var line in order.Lines.Where(l => l.LineKind == MfgLineKinds.Raw))
            {
                var materialId = await ResolveMaterialIdAsync(connection, transaction, companyId, line.ItemCode, cancellationToken).ConfigureAwait(false);
                if (!await IsStockableAsync(connection, transaction, companyId, materialId, cancellationToken).ConfigureAwait(false))
                    continue;
                var onHand = await GetOnHandAsync(connection, transaction, companyId, warehouseId, materialId, cancellationToken).ConfigureAwait(false);
                if (onHand < line.Qty)
                    throw new InvalidOperationException(
                        $"Insufficient stock for material '{line.ItemCode.Trim()}': required {line.Qty}, on hand {onHand} in warehouse {warehouseId}.");
                var consumed = StockValuationEngine.Apply(connection, transaction, _provider,
                    new StockMovementRequest(companyId.Trim(), warehouseId, materialId, dateText,
                        "MFG_CONSUME", documentNo, QtyIn: 0m, QtyOut: line.Qty,
                        Note: $"Manufacturing order {documentNo} consumption"));
                rawEngineCosts.Add((line, -consumed.CostAmount));
            }

            // 2) Produce the finished item at the computed unit cost — unless
            //    the finished item itself is non-stock (a service): the order
            //    still posts its costs and status, but nothing enters stock.
            var finishedId = await ResolveMaterialIdAsync(connection, transaction, companyId, order.ItemCode, cancellationToken).ConfigureAwait(false);
            if (await IsStockableAsync(connection, transaction, companyId, finishedId, cancellationToken).ConfigureAwait(false))
            {
                StockValuationEngine.Apply(connection, transaction, _provider,
                    new StockMovementRequest(companyId.Trim(), warehouseId, finishedId, dateText,
                        "MFG_PRODUCE", documentNo, QtyIn: order.Qty, QtyOut: 0m,
                        ReceiptUnitCost: order.UnitCost,
                        Note: $"Manufacturing order {documentNo} production"));
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

            if (order.Status == MfgOrderStatuses.Posted)
            {
                var warehouseId = await ResolveWarehouseIdAsync(connection, transaction, companyId, order.WarehouseCode, cancellationToken).ConfigureAwait(false);
                var documentNo = $"MO-{order.DocNo}";
                var dateText = DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

                // The produced quantity must still be on hand to reverse out.
                // Non-stockable items (services) were never moved by Post,
                // so there is nothing to reverse for them either.
                var finishedId = await ResolveMaterialIdAsync(connection, transaction, companyId, order.ItemCode, cancellationToken).ConfigureAwait(false);
                if (await IsStockableAsync(connection, transaction, companyId, finishedId, cancellationToken).ConfigureAwait(false))
                {
                    var finishedOnHand = await GetOnHandAsync(connection, transaction, companyId, warehouseId, finishedId, cancellationToken).ConfigureAwait(false);
                    if (finishedOnHand < order.Qty)
                        throw new InvalidOperationException(
                            $"Cannot cancel: produced quantity of '{order.ItemCode.Trim()}' is no longer fully on hand (required {order.Qty}, on hand {finishedOnHand}).");
                    StockValuationEngine.Apply(connection, transaction, _provider,
                        new StockMovementRequest(companyId.Trim(), warehouseId, finishedId, dateText,
                            "MFG_PRODUCE_REV", documentNo, QtyIn: 0m, QtyOut: order.Qty,
                            Note: $"Manufacturing order {documentNo} cancellation"));
                }

                foreach (var line in order.Lines.Where(l => l.LineKind == MfgLineKinds.Raw))
                {
                    var materialId = await ResolveMaterialIdAsync(connection, transaction, companyId, line.ItemCode, cancellationToken).ConfigureAwait(false);
                    if (!await IsStockableAsync(connection, transaction, companyId, materialId, cancellationToken).ConfigureAwait(false))
                        continue;
                    // Raw materials re-enter at their ORIGINAL consumption
                    // rate (the rate the order consumed them at), never at
                    // a re-typed line cost (Step 6 return rule).
                    var originalRate = StockValuationEngine.TryGetDocumentRate(
                        connection, transaction, _provider, companyId.Trim(), documentNo, materialId, warehouseId, issued: true);
                    StockValuationEngine.Apply(connection, transaction, _provider,
                        new StockMovementRequest(companyId.Trim(), warehouseId, materialId, dateText,
                            "MFG_CONSUME_REV", documentNo, QtyIn: line.Qty, QtyOut: 0m,
                            ReceiptUnitCost: line.UnitCost, LinkedRate: originalRate,
                            Note: $"Manufacturing order {documentNo} cancellation"));
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
            $"SELECT id, doc_no, doc_date, item_code, item_name, qty, unit_code, warehouse_code, overhead_amount, standard_unit_cost, total_cost, unit_cost, status, notes FROM mfg_orders WHERE company_id = @company AND id = @id AND {NotDeleted}"))
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
            "SELECT id, order_id, line_kind, item_code, item_name, qty, unit_code, unit_cost, standard_unit_cost, amount FROM mfg_order_lines WHERE order_id = @order ORDER BY item_code");
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
                Amount = ReadDecimal(reader, 9)
            });
        }
        return result;
    }

    private static void ComputeTotals(MfgOrder order)
    {
        foreach (var line in order.Lines)
            line.Amount = decimal.Round(line.Qty * line.UnitCost, 2);
        order.TotalCost = decimal.Round(order.Lines.Sum(l => l.Amount) + order.OverheadAmount, 2);
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
        Notes = ReadString(reader, 13)
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
