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

            // 1) Validate + consume every RAW line.
            foreach (var line in order.Lines.Where(l => l.LineKind == MfgLineKinds.Raw))
            {
                var materialId = await ResolveMaterialIdAsync(connection, transaction, companyId, line.ItemCode, cancellationToken).ConfigureAwait(false);
                var onHand = await GetOnHandAsync(connection, transaction, companyId, warehouseId, materialId, cancellationToken).ConfigureAwait(false);
                if (onHand < line.Qty)
                    throw new InvalidOperationException(
                        $"Insufficient stock for material '{line.ItemCode.Trim()}': required {line.Qty}, on hand {onHand} in warehouse {warehouseId}.");
                await InsertMovementAsync(connection, transaction, companyId, warehouseId, materialId, dateText,
                    "MFG_CONSUME", documentNo, qtyIn: 0m, qtyOut: line.Qty, unitCost: line.UnitCost,
                    note: $"Manufacturing order {documentNo} consumption", cancellationToken).ConfigureAwait(false);
                await AdjustBalanceAsync(connection, transaction, companyId, warehouseId, materialId, delta: -line.Qty, unitCost: line.UnitCost, cancellationToken).ConfigureAwait(false);
            }

            // 2) Produce the finished item at the computed unit cost.
            var finishedId = await ResolveMaterialIdAsync(connection, transaction, companyId, order.ItemCode, cancellationToken).ConfigureAwait(false);
            await InsertMovementAsync(connection, transaction, companyId, warehouseId, finishedId, dateText,
                "MFG_PRODUCE", documentNo, qtyIn: order.Qty, qtyOut: 0m, unitCost: order.UnitCost,
                note: $"Manufacturing order {documentNo} production", cancellationToken).ConfigureAwait(false);
            await AdjustBalanceAsync(connection, transaction, companyId, warehouseId, finishedId, delta: order.Qty, unitCost: order.UnitCost, cancellationToken).ConfigureAwait(false);

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
                var finishedId = await ResolveMaterialIdAsync(connection, transaction, companyId, order.ItemCode, cancellationToken).ConfigureAwait(false);
                var finishedOnHand = await GetOnHandAsync(connection, transaction, companyId, warehouseId, finishedId, cancellationToken).ConfigureAwait(false);
                if (finishedOnHand < order.Qty)
                    throw new InvalidOperationException(
                        $"Cannot cancel: produced quantity of '{order.ItemCode.Trim()}' is no longer fully on hand (required {order.Qty}, on hand {finishedOnHand}).");
                await InsertMovementAsync(connection, transaction, companyId, warehouseId, finishedId, dateText,
                    "MFG_PRODUCE_REV", documentNo, qtyIn: 0m, qtyOut: order.Qty, unitCost: order.UnitCost,
                    note: $"Manufacturing order {documentNo} cancellation", cancellationToken).ConfigureAwait(false);
                await AdjustBalanceAsync(connection, transaction, companyId, warehouseId, finishedId, delta: -order.Qty, unitCost: order.UnitCost, cancellationToken).ConfigureAwait(false);

                foreach (var line in order.Lines.Where(l => l.LineKind == MfgLineKinds.Raw))
                {
                    var materialId = await ResolveMaterialIdAsync(connection, transaction, companyId, line.ItemCode, cancellationToken).ConfigureAwait(false);
                    await InsertMovementAsync(connection, transaction, companyId, warehouseId, materialId, dateText,
                        "MFG_CONSUME_REV", documentNo, qtyIn: line.Qty, qtyOut: 0m, unitCost: line.UnitCost,
                        note: $"Manufacturing order {documentNo} cancellation", cancellationToken).ConfigureAwait(false);
                    await AdjustBalanceAsync(connection, transaction, companyId, warehouseId, materialId, delta: line.Qty, unitCost: line.UnitCost, cancellationToken).ConfigureAwait(false);
                }
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

    private async Task InsertMovementAsync(DbConnection connection, DbTransaction transaction, string companyId, long warehouseId, long materialId,
        string dateText, string movementType, string documentNo, decimal qtyIn, decimal qtyOut, decimal unitCost, string note, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction,
            "INSERT INTO material_stock_movements (company_id, warehouse_id, material_id, movement_date, movement_type, document_no, qty_in, qty_out, unit_cost, note) VALUES (@company, @warehouse, @material, @date, @type, @doc, @in, @out, @cost, @note)");
        Add(command, "@company", companyId.Trim());
        Add(command, "@warehouse", warehouseId);
        Add(command, "@material", materialId);
        Add(command, "@date", dateText);
        Add(command, "@type", movementType);
        Add(command, "@doc", documentNo);
        Add(command, "@in", qtyIn);
        Add(command, "@out", qtyOut);
        Add(command, "@cost", unitCost);
        Add(command, "@note", note);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task AdjustBalanceAsync(DbConnection connection, DbTransaction transaction, string companyId, long warehouseId, long materialId,
        decimal delta, decimal unitCost, CancellationToken cancellationToken)
    {
        long? balanceId = null;
        decimal available = 0m, average = 0m;
        await using (var read = CreateCommand(connection, transaction,
            $"SELECT stock_balance_id, available_qty, average_cost FROM material_stock_balances WHERE company_id = @company AND warehouse_id = @warehouse AND material_id = @material AND {NotDeleted} LIMIT 1"))
        {
            Add(read, "@company", companyId.Trim());
            Add(read, "@warehouse", warehouseId);
            Add(read, "@material", materialId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                balanceId = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
                available = Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture);
                average = reader.IsDBNull(2) ? 0m : Convert.ToDecimal(reader.GetValue(2), CultureInfo.InvariantCulture);
            }
        }

        var newQty = available + delta;
        var newAverage = average;
        if (delta > 0)
        {
            var valuedQty = Math.Max(available, 0m);
            var totalQty = valuedQty + delta;
            newAverage = totalQty == 0 ? unitCost : decimal.Round(((valuedQty * average) + (delta * unitCost)) / totalQty, 6, MidpointRounding.AwayFromZero);
        }

        if (balanceId is null)
        {
            await using var insert = CreateCommand(connection, transaction,
                "INSERT INTO material_stock_balances (company_id, warehouse_id, material_id, available_qty, reserved_qty, average_cost, last_cost, last_updated) VALUES (@company, @warehouse, @material, @qty, 0, @avg, @cost, " + NowExpr + ")");
            Add(insert, "@company", companyId.Trim());
            Add(insert, "@warehouse", warehouseId);
            Add(insert, "@material", materialId);
            Add(insert, "@qty", newQty);
            Add(insert, "@avg", newAverage);
            Add(insert, "@cost", unitCost);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await using var update = CreateCommand(connection, transaction,
                $"UPDATE material_stock_balances SET available_qty = @qty, average_cost = @avg, last_cost = @cost, last_updated = {NowExpr} WHERE stock_balance_id = @id");
            Add(update, "@qty", newQty);
            Add(update, "@avg", newAverage);
            Add(update, "@cost", unitCost);
            Add(update, "@id", balanceId.Value);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
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

    private static string NormalizeProvider(string provider) => provider.Trim().ToLowerInvariant() switch
    {
        "sqlite" or "sqlite3" => "SQLite",
        "postgres" or "postgresql" or "npgsql" => "PostgreSQL",
        _ => throw new NotSupportedException($"Unsupported database provider '{provider}'. Use SQLite or PostgreSQL.")
    };
}
