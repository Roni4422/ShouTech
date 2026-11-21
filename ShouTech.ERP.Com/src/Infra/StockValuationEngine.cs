using System.Data.Common;
using System.Globalization;
using ShouTech.Domain;

namespace ShouTech.Infrastructure;

/// <summary>
/// One movement to value and record. Exactly one of QtyIn / QtyOut is
/// positive. Receipt pricing is resolved by the caller into at most one
/// of: ReceiptCostAmount (exact value — transfers in), LinkedRate (the
/// original movement's rate — linked returns), ReceiptUnitCost (actual
/// cost). With none of them the engine values the receipt at the
/// current rate (unlinked returns, count surpluses), falling back to
/// the count/document cost the caller supplied as ReceiptUnitCost when
/// no current rate exists. Issues never take a price from the caller:
/// their rate comes from the balance / layer queue only — a selling
/// price can never enter stock valuation again (sweep RC-1).
/// </summary>
public sealed record StockMovementRequest(
    string CompanyId,
    long WarehouseId,
    long MaterialId,
    string MovementDate,
    string MovementType,
    string DocumentNo,
    decimal QtyIn,
    decimal QtyOut,
    decimal? ReceiptUnitCost = null,
    decimal? LinkedRate = null,
    decimal? ReceiptCostAmount = null,
    string? Note = null,
    // ---- Batch/serial dimension (Phase B 054) -------------------------
    // All optional: an untracked material ignores them, and every
    // pre-054 caller compiles and behaves exactly as before.
    IReadOnlyList<BatchAllocationRequest>? BatchAllocations = null,
    IReadOnlyList<SerialSelection>? Serials = null,
    string? OriginalDocumentNo = null,
    long? SourceMovementId = null,
    bool AutoAllocateSerials = false,
    bool AllowExpiredBatchIssue = false,
    long? PartyId = null,
    string? Actor = null);

public sealed record StockMovementResult(
    long MovementId,
    decimal ValuationRate,
    decimal CostAmount,
    decimal BalanceQtyAfter,
    decimal StockValueAfter,
    string ValuationMethod,
    IReadOnlyList<StockAllocationRecord>? Allocations = null)
{
    /// <summary>The batch/serial allocations written for this movement
    /// (stock_movement_allocations); empty for untracked materials.</summary>
    public IReadOnlyList<StockAllocationRecord> AllocationRecords
        => Allocations ?? Array.Empty<StockAllocationRecord>();
}

/// <summary>
/// THE stock valuation engine (Fix Phase Step 6, Phase B design
/// section 1, migration 053). Every stock writer — StkPost (invoice
/// documents, counts, transfers), CommercialDocumentRepository
/// (delivery notes), MfgOrderRepository and CardStore (manufacturing) —
/// delegates balance mutation to this class inside its own posting
/// transaction; no private costing math remains in any writer.
///
/// The engine owns, per movement: the movement row extended with its
/// valuation record (valuation_rate, cost_amount, balance_qty_after,
/// stock_value_after, valuation_method), the balance row (available
/// quantity, average_cost display rate, stock_value), the FIFO/LIFO
/// layer queue, and the material card's avg_cost / last_purchase_cost —
/// so the stock-balance report and the profit reports read ONE
/// consistent valuation (sweep RC-1/RC-2). Synchronous ADO, like
/// GlPosting, so sync app-layer writers and async infrastructure
/// repositories share it without moving their commit boundary.
/// </summary>
public static class StockValuationEngine
{
    // ------------------------------------------------------------------
    // Public entry point
    // ------------------------------------------------------------------
    public static StockMovementResult Apply(
        DbConnection connection, DbTransaction transaction, string provider,
        StockMovementRequest request)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.CompanyId))
            throw new ArgumentException("Company is required.", nameof(request));
        if ((request.QtyIn > 0) == (request.QtyOut > 0))
            throw new ArgumentException("A movement carries exactly one of qty_in / qty_out.", nameof(request));
        var prov = NormalizeProvider(provider);
        var company = request.CompanyId.Trim();
        var isPg = prov == "PostgreSQL";

        var material = LoadMaterial(connection, transaction, isPg, company, request.MaterialId);
        var method = ValuationMath.NormalizeMethod(material.CostMethod);
        var balance = LoadBalance(connection, transaction, isPg, company, request.WarehouseId, request.MaterialId);

        var state = new ValuationState
        {
            Quantity = balance?.Quantity ?? 0m,
            Value = balance?.Value ?? 0m
        };
        // A balance row written outside the engine (a pre-053 database
        // the migration backfill could not reach, a fixture, an import)
        // can carry quantity and an average but a zero stock_value. The
        // row's own declared valuation is qty x average — the same rule
        // migration 053 backfills with — so the engine adopts it rather
        // than valuing the stock at zero.
        if (balance is not null && state.Value == 0m && state.Quantity != 0m && balance.AverageCost > 0m)
            state.Value = ValuationMath.RoundMoney(state.Quantity * balance.AverageCost);
        if (ValuationMath.UsesLayers(method))
        {
            foreach (var layer in LoadLayers(connection, transaction, isPg, company, request.WarehouseId, request.MaterialId))
                state.Layers.Add(layer);
            // Legacy balance with no queue (possible when a balance was
            // created outside the engine before 053's backfill, e.g. a
            // history merge): open the queue at the display rate so the
            // first engine issue has layers to consume.
            if (state.Layers.Count == 0 && state.Quantity > 0)
            {
                var seedRate = state.DisplayRate > 0 ? state.DisplayRate : FallbackRate(material, state);
                var seed = new CostLayerState
                {
                    LayerDate = request.MovementDate,
                    QtyIn = state.Quantity,
                    QtyRemaining = state.Quantity,
                    UnitCost = seedRate
                };
                seed.LayerId = InsertLayer(connection, transaction, isPg, company,
                    request.WarehouseId, request.MaterialId, null, seed);
                state.Layers.Add(seed);
            }
        }

        var isReceipt = request.QtyIn > 0;
        var qty = isReceipt ? request.QtyIn : request.QtyOut;
        decimal valuationRate, costAmount;
        var usedFallback = false;
        CostLayerState? pendingLayer = null;

        if (isReceipt)
        {
            decimal rate;
            if (request.ReceiptCostAmount is decimal exactAmount)
            {
                rate = qty > 0 ? ValuationMath.RoundRate(exactAmount / qty) : 0m;
                var outcome = ValuationMath.Receipt(state, method, qty, rate, request.MovementDate);
                // Force the exact transferred value (no value creation
                // or destruction on a transfer): adjust by the delta the
                // rounded rate introduced.
                var delta = ValuationMath.RoundMoney(exactAmount) - outcome.CostAmount;
                if (delta != 0m)
                {
                    state.Value += delta;
                    outcome = outcome with { CostAmount = ValuationMath.RoundMoney(exactAmount) };
                }
                (valuationRate, costAmount) = (outcome.ValuationRate, outcome.CostAmount);
            }
            else
            {
                rate = ResolveReceiptRate(request, method, material, state);
                var outcome = ValuationMath.Receipt(state, method, qty, rate, request.MovementDate);
                (valuationRate, costAmount) = (outcome.ValuationRate, outcome.CostAmount);
            }
            pendingLayer = state.Layers.LastOrDefault(l => l.LayerId == 0 && l.QtyRemaining > 0);
        }
        else
        {
            if (state.Quantity - qty < 0 && !material.AllowNegative)
                throw new AppError(ErrorCodes.StockValuationFailed,
                    $"Insufficient stock for material '{material.Code}': on hand {state.Quantity}, issue {qty}.",
                    material.Code);
            var nonLayerRate = method switch
            {
                ValuationMath.Standard => material.StandardCost,
                ValuationMath.LastPurchase => material.LastPurchaseCost > 0
                    ? material.LastPurchaseCost
                    : state.DisplayRate > 0 ? state.DisplayRate : FallbackRate(material, state),
                _ => state.DisplayRate > 0 ? state.DisplayRate : FallbackRate(material, state)
            };
            var consumeNewest = string.Equals(request.MovementType, "CNT", StringComparison.OrdinalIgnoreCase)
                || string.Equals(request.MovementType, "COUNT_CLOSE", StringComparison.OrdinalIgnoreCase);
            var outcome = ValuationMath.Issue(state, method, qty, nonLayerRate,
                FallbackRate(material, state), request.MovementDate, consumeNewest);
            valuationRate = outcome.ValuationRate;
            costAmount = -outcome.CostAmount;
            usedFallback = outcome.UsedFallback;
            pendingLayer = state.Layers.LastOrDefault(l => l.LayerId == 0);
        }

        var methodLabel = usedFallback ? method + "-Fallback" : method;
        var movementId = InsertMovement(connection, transaction, isPg, company, request, isReceipt,
            valuationRate, isReceipt ? costAmount : costAmount, state, methodLabel);

        // Batch/serial dimension (Phase B 054): for a tracked, stocked,
        // non-service material the allocator validates the movement's
        // batches/serials, maintains batch balances and serial states,
        // and writes the allocation bundle rows — inside this same
        // transaction, so a cataloged allocation failure (INV-6005..
        // 6010) rolls the whole posting back. Untracked materials skip
        // the allocator entirely: pre-054 behavior is the baseline.
        IReadOnlyList<StockAllocationRecord> allocations = Array.Empty<StockAllocationRecord>();
        if (material.Tracking.IsTracked && !material.IsService && material.IsStocked)
        {
            allocations = BatchSerialAllocator.Apply(connection, transaction, isPg,
                request, material.Tracking, material.Code, movementId);
        }

        UpsertBalance(connection, transaction, isPg, company, request.WarehouseId, request.MaterialId,
            balance?.Id, balance?.AverageCost ?? 0m, state, valuationRate);
        PersistLayers(connection, transaction, isPg, company, request.WarehouseId, request.MaterialId,
            state, movementId, pendingLayer);
        UpdateMaterialCard(connection, transaction, isPg, material, request, isReceipt, valuationRate);

        return new StockMovementResult(movementId, valuationRate,
            isReceipt ? costAmount : costAmount, state.Quantity, state.Value, methodLabel,
            allocations);
    }

    /// <summary>
    /// The average rate at which a document's movements for one material
    /// were valued: for an issued document (a sale) the original COGS
    /// rate; for a received document (a purchase) the receipt rate.
    /// Linked returns re-enter / leave at exactly this rate, never at a
    /// selling price. Returns null when the document has no valued
    /// movements for the material (legacy rows use their unit_cost,
    /// which the 053 backfill copied into cost_amount).
    /// </summary>
    public static decimal? TryGetDocumentRate(
        DbConnection connection, DbTransaction transaction, string provider,
        string companyId, string documentNo, long materialId, long warehouseId, bool issued)
    {
        var isPg = NormalizeProvider(provider) == "PostgreSQL";
        using var cmd = CreateCommand(connection, transaction, $"""
            SELECT COALESCE(SUM(ABS(COALESCE(cost_amount, 0))), 0),
                   COALESCE(SUM({(issued ? "qty_out" : "qty_in")}), 0)
            FROM material_stock_movements
            WHERE company_id = @c AND document_no = @doc AND material_id = @m
              AND warehouse_id = @w AND {(issued ? "qty_out" : "qty_in")} > 0
              AND is_deleted = {(isPg ? "FALSE" : "0")}
            """);
        Add(cmd, "@c", companyId.Trim());
        Add(cmd, "@doc", documentNo);
        Add(cmd, "@m", materialId);
        Add(cmd, "@w", warehouseId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        var amount = ValuationText.ToDecimal(reader.GetValue(0));
        var qty = ValuationText.ToDecimal(reader.GetValue(1));
        return qty > 0 ? ValuationMath.RoundRate(amount / qty) : null;
    }

    // ------------------------------------------------------------------
    // Rate resolution
    // ------------------------------------------------------------------
    private static decimal ResolveReceiptRate(
        StockMovementRequest request, string method, MaterialInfo material, ValuationState state)
    {
        if (request.LinkedRate is decimal linked) return linked;
        // Keep the actual acquisition cost on receipts. Standard costing is
        // applied to issues; the difference remains visible as inventory
        // value variance until the related quantity is consumed.
        if (request.ReceiptUnitCost is decimal actual && actual > 0) return actual;
        if (method == ValuationMath.Standard) return material.StandardCost;
        // Unlinked return / count surplus / zero-cost receipt: enter at
        // the current rate; the caller's cost is only the last resort
        // when no current rate exists at all.
        if (state.DisplayRate > 0) return state.DisplayRate;
        if (request.ReceiptUnitCost is decimal fallbackCost) return fallbackCost;
        return FallbackRate(material, state);
    }

    /// <summary>The design's fallback chain for a movement that has no
    /// rate of its own: balance/card average, then last purchase cost,
    /// then standard cost, then 0.</summary>
    private static decimal FallbackRate(MaterialInfo material, ValuationState state)
    {
        if (state.DisplayRate > 0) return state.DisplayRate;
        if (material.CardAvgCost > 0) return material.CardAvgCost;
        if (material.LastPurchaseCost > 0) return material.LastPurchaseCost;
        if (material.StandardCost > 0) return material.StandardCost;
        return 0m;
    }

    // ------------------------------------------------------------------
    // Loading
    // ------------------------------------------------------------------
    private sealed record MaterialInfo(
        string CompanyKey, string Code, string? CostMethod, decimal StandardCost,
        decimal LastPurchaseCost, decimal CardAvgCost, bool AllowNegative,
        MaterialTracking Tracking, bool IsService, bool IsStocked);

    private static MaterialInfo LoadMaterial(
        DbConnection connection, DbTransaction transaction, bool isPg, string company, long materialId)
    {
        // Primary keying is (company, material) — the stock world's
        // company key. The chain repository keys its documents by the
        // company Guid while material cards carry the company code, so a
        // material-only fallback keeps that path working exactly as its
        // own stockable check already does.
        foreach (var scoped in new[] { true, false })
        {
            using var cmd = CreateCommand(connection, transaction, scoped
                ? $"SELECT company_id, code, cost_method, standard_cost, last_purchase_cost, avg_cost, allow_negative, has_batch, has_lot, has_serial, has_expiry, removal_strategy, shelf_life_days, alert_before_expiry_days, is_service, is_stocked FROM materials WHERE company_id = @c AND material_id = @m AND is_deleted = {(isPg ? "FALSE" : "0")} LIMIT 1"
                : $"SELECT company_id, code, cost_method, standard_cost, last_purchase_cost, avg_cost, allow_negative, has_batch, has_lot, has_serial, has_expiry, removal_strategy, shelf_life_days, alert_before_expiry_days, is_service, is_stocked FROM materials WHERE material_id = @m AND is_deleted = {(isPg ? "FALSE" : "0")} LIMIT 1");
            if (scoped) Add(cmd, "@c", company);
            Add(cmd, "@m", materialId);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                bool Flag(int i) => !reader.IsDBNull(i)
                    && Convert.ToBoolean(reader.GetValue(i), CultureInfo.InvariantCulture);
                var tracking = new MaterialTracking(
                    HasBatch: Flag(7) || Flag(8),
                    HasSerial: Flag(9),
                    HasExpiry: Flag(10),
                    RemovalStrategy: ValuationText.ToText(reader.GetValue(11)) ?? "FIFO",
                    ShelfLifeDays: reader.IsDBNull(12) ? 0 : Convert.ToInt32(reader.GetValue(12), CultureInfo.InvariantCulture),
                    AlertBeforeExpiryDays: reader.IsDBNull(13) ? 0 : Convert.ToInt32(reader.GetValue(13), CultureInfo.InvariantCulture));
                return new MaterialInfo(
                    ValuationText.ToText(reader.GetValue(0)) ?? company,
                    ValuationText.ToText(reader.GetValue(1)) ?? materialId.ToString(CultureInfo.InvariantCulture),
                    ValuationText.ToText(reader.GetValue(2)),
                    ValuationText.ToDecimal(reader.GetValue(3)),
                    ValuationText.ToDecimal(reader.GetValue(4)),
                    ValuationText.ToDecimal(reader.GetValue(5)),
                    Flag(6),
                    tracking,
                    IsService: Flag(14),
                    // A legacy row with a NULL is_stocked is stocked
                    // (the column is NOT NULL DEFAULT 1 since 013).
                    IsStocked: reader.IsDBNull(15) || Flag(15));
            }
        }
        throw new AppError(ErrorCodes.StockValuationFailed,
            $"Material {materialId} does not exist in the current company.", materialId.ToString(CultureInfo.InvariantCulture));
    }

    private sealed record BalanceInfo(long Id, decimal Quantity, decimal Value, decimal AverageCost);

    private static BalanceInfo? LoadBalance(
        DbConnection connection, DbTransaction transaction, bool isPg,
        string company, long warehouseId, long materialId)
    {
        using var cmd = CreateCommand(connection, transaction, isPg
            ? "SELECT stock_balance_id, available_qty, stock_value, average_cost FROM material_stock_balances WHERE company_id = @c AND warehouse_id = @w AND material_id = @m AND is_deleted = FALSE LIMIT 1 FOR UPDATE"
            : "SELECT stock_balance_id, available_qty, stock_value, average_cost FROM material_stock_balances WHERE company_id = @c AND warehouse_id = @w AND material_id = @m AND is_deleted = 0 LIMIT 1");
        Add(cmd, "@c", company);
        Add(cmd, "@w", warehouseId);
        Add(cmd, "@m", materialId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new BalanceInfo(
            Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
            ValuationText.ToDecimal(reader.GetValue(1)),
            ValuationText.ToDecimal(reader.GetValue(2)),
            ValuationText.ToDecimal(reader.GetValue(3)));
    }

    private static List<CostLayerState> LoadLayers(
        DbConnection connection, DbTransaction transaction, bool isPg,
        string company, long warehouseId, long materialId)
    {
        var layers = new List<CostLayerState>();
        using var cmd = CreateCommand(connection, transaction, $"""
            SELECT layer_id, layer_date, qty_in, qty_remaining, unit_cost
            FROM stock_cost_layers
            WHERE company_id = @c AND warehouse_id = @w AND material_id = @m
              AND is_open = {(isPg ? "TRUE" : "1")} AND qty_remaining <> 0
            ORDER BY layer_date, layer_id
            """);
        Add(cmd, "@c", company);
        Add(cmd, "@w", warehouseId);
        Add(cmd, "@m", materialId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            layers.Add(new CostLayerState
            {
                LayerId = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                LayerDate = ValuationText.ToText(reader.GetValue(1)) ?? string.Empty,
                QtyIn = ValuationText.ToDecimal(reader.GetValue(2)),
                QtyRemaining = ValuationText.ToDecimal(reader.GetValue(3)),
                UnitCost = ValuationText.ToDecimal(reader.GetValue(4))
            });
        }
        return layers;
    }

    // ------------------------------------------------------------------
    // Writing
    // ------------------------------------------------------------------
    private static long InsertMovement(
        DbConnection connection, DbTransaction transaction, bool isPg, string company,
        StockMovementRequest request, bool isReceipt, decimal valuationRate, decimal costAmount,
        ValuationState state, string methodLabel)
    {
        var sql = isPg
            ? """
              INSERT INTO material_stock_movements
                  (company_id, warehouse_id, material_id, movement_date, movement_type, document_no,
                   qty_in, qty_out, unit_cost, note,
                   balance_qty_after, valuation_rate, stock_value_after, cost_amount, valuation_method)
              VALUES (@c, @w, @m, @d, @t, @doc, @qin, @qout, @cost, @note,
                      @balQty, @rate, @stockVal, @costAmt, @method)
              RETURNING stock_movement_id
              """
            : """
              INSERT INTO material_stock_movements
                  (company_id, warehouse_id, material_id, movement_date, movement_type, document_no,
                   qty_in, qty_out, unit_cost, note,
                   balance_qty_after, valuation_rate, stock_value_after, cost_amount, valuation_method)
              VALUES (@c, @w, @m, @d, @t, @doc, @qin, @qout, @cost, @note,
                      @balQty, @rate, @stockVal, @costAmt, @method);
              SELECT last_insert_rowid();
              """;
        using var cmd = CreateCommand(connection, transaction, sql);
        Add(cmd, "@c", company);
        Add(cmd, "@w", request.WarehouseId);
        Add(cmd, "@m", request.MaterialId);
        Add(cmd, "@d", isPg
            ? (object)DateTime.SpecifyKind(DateTime.Parse(request.MovementDate, CultureInfo.InvariantCulture).Date, DateTimeKind.Utc)
            : request.MovementDate);
        Add(cmd, "@t", request.MovementType);
        Add(cmd, "@doc", request.DocumentNo);
        Add(cmd, "@qin", request.QtyIn);
        Add(cmd, "@qout", request.QtyOut);
        // unit_cost keeps its historical meaning for receipts (the cost
        // the stock entered at) and becomes the valuation rate for
        // issues (the cost it left at) — never a selling price.
        Add(cmd, "@cost", valuationRate);
        Add(cmd, "@note", (object?)request.Note ?? DBNull.Value);
        Add(cmd, "@balQty", state.Quantity);
        Add(cmd, "@rate", valuationRate);
        Add(cmd, "@stockVal", state.Value);
        Add(cmd, "@costAmt", costAmount);
        Add(cmd, "@method", methodLabel);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void UpsertBalance(
        DbConnection connection, DbTransaction transaction, bool isPg, string company,
        long warehouseId, long materialId, long? balanceId, decimal previousAverage,
        ValuationState state, decimal lastRate)
    {
        // The display rate is value / quantity while stock is on hand.
        // At zero or negative quantity the previous average is kept (the
        // historical average_cost behaviour) — the value itself is exact
        // (zero at zero quantity), so reports stay consistent.
        var displayRate = state.Quantity > 0
            ? ValuationMath.RoundRate(state.Value / state.Quantity)
            : previousAverage > 0 ? previousAverage : lastRate;
        if (balanceId is null)
        {
            using var ins = CreateCommand(connection, transaction, isPg
                ? "INSERT INTO material_stock_balances (company_id, warehouse_id, material_id, available_qty, reserved_qty, average_cost, last_cost, stock_value, last_updated, is_deleted) VALUES (@c, @w, @m, @qty, 0, @avg, @cost, @val, NOW(), FALSE)"
                : "INSERT INTO material_stock_balances (company_id, warehouse_id, material_id, available_qty, reserved_qty, average_cost, last_cost, stock_value, last_updated, is_deleted) VALUES (@c, @w, @m, @qty, 0, @avg, @cost, @val, datetime('now'), 0)");
            Add(ins, "@c", company);
            Add(ins, "@w", warehouseId);
            Add(ins, "@m", materialId);
            Add(ins, "@qty", state.Quantity);
            Add(ins, "@avg", displayRate);
            Add(ins, "@cost", lastRate);
            Add(ins, "@val", state.Value);
            ins.ExecuteNonQuery();
        }
        else
        {
            using var upd = CreateCommand(connection, transaction, isPg
                ? "UPDATE material_stock_balances SET available_qty = @qty, average_cost = @avg, last_cost = @cost, stock_value = @val, last_updated = NOW() WHERE stock_balance_id = @id"
                : "UPDATE material_stock_balances SET available_qty = @qty, average_cost = @avg, last_cost = @cost, stock_value = @val, last_updated = datetime('now') WHERE stock_balance_id = @id");
            Add(upd, "@qty", state.Quantity);
            Add(upd, "@avg", displayRate);
            Add(upd, "@cost", lastRate);
            Add(upd, "@val", state.Value);
            Add(upd, "@id", balanceId.Value);
            upd.ExecuteNonQuery();
        }
    }

    private static void PersistLayers(
        DbConnection connection, DbTransaction transaction, bool isPg, string company,
        long warehouseId, long materialId, ValuationState state, long movementId,
        CostLayerState? pendingLayer)
    {
        foreach (var layer in state.Layers)
        {
            if (layer.LayerId == 0)
            {
                if (ReferenceEquals(layer, pendingLayer) || layer.QtyRemaining != 0)
                    layer.LayerId = InsertLayer(connection, transaction, isPg, company,
                        warehouseId, materialId, movementId, layer);
                continue;
            }
            using var upd = CreateCommand(connection, transaction, isPg
                ? "UPDATE stock_cost_layers SET qty_remaining = @rem, is_open = @open WHERE layer_id = @id"
                : "UPDATE stock_cost_layers SET qty_remaining = @rem, is_open = @open WHERE layer_id = @id");
            Add(upd, "@rem", layer.QtyRemaining);
            Add(upd, "@open", isPg ? (object)(layer.QtyRemaining != 0) : (layer.QtyRemaining != 0 ? 1 : 0));
            Add(upd, "@id", layer.LayerId);
            upd.ExecuteNonQuery();
        }
    }

    private static long InsertLayer(
        DbConnection connection, DbTransaction transaction, bool isPg, string company,
        long warehouseId, long materialId, long? sourceMovementId, CostLayerState layer)
    {
        var sql = isPg
            ? "INSERT INTO stock_cost_layers (company_id, warehouse_id, material_id, source_movement_id, layer_date, qty_in, qty_remaining, unit_cost, is_open) VALUES (@c, @w, @m, @src, @date, @qin, @rem, @cost, TRUE) RETURNING layer_id"
            : "INSERT INTO stock_cost_layers (company_id, warehouse_id, material_id, source_movement_id, layer_date, qty_in, qty_remaining, unit_cost, is_open) VALUES (@c, @w, @m, @src, @date, @qin, @rem, @cost, 1); SELECT last_insert_rowid();";
        using var cmd = CreateCommand(connection, transaction, sql);
        Add(cmd, "@c", company);
        Add(cmd, "@w", warehouseId);
        Add(cmd, "@m", materialId);
        Add(cmd, "@src", (object?)sourceMovementId ?? DBNull.Value);
        Add(cmd, "@date", layer.LayerDate);
        Add(cmd, "@qin", layer.QtyIn);
        Add(cmd, "@rem", layer.QtyRemaining);
        Add(cmd, "@cost", layer.UnitCost);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Keeps the material card in step with the balances (sweep RC-2):
    /// avg_cost becomes the company-wide value-weighted rate over all
    /// warehouses, and a purchase receipt maintains last_purchase_cost
    /// (the LastPurchase method's rate source). Updates the card row in
    /// its own company keying (the key the row was loaded under).
    /// </summary>
    private static void UpdateMaterialCard(
        DbConnection connection, DbTransaction transaction, bool isPg,
        MaterialInfo material, StockMovementRequest request, bool isReceipt, decimal valuationRate)
    {
        decimal totalQty = 0m, totalValue = 0m;
        using (var cmd = CreateCommand(connection, transaction, $"""
            SELECT COALESCE(SUM(available_qty), 0), COALESCE(SUM(stock_value), 0)
            FROM material_stock_balances
            WHERE company_id = @c AND material_id = @m AND is_deleted = {(isPg ? "FALSE" : "0")}
            """))
        {
            Add(cmd, "@c", request.CompanyId.Trim());
            Add(cmd, "@m", request.MaterialId);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                totalQty = ValuationText.ToDecimal(reader.GetValue(0));
                totalValue = ValuationText.ToDecimal(reader.GetValue(1));
            }
        }

        var isPurchaseReceipt = isReceipt
            && (string.Equals(request.MovementType, "PUR", StringComparison.OrdinalIgnoreCase)
                || string.Equals(request.MovementType, "RCV", StringComparison.OrdinalIgnoreCase));
        if (totalQty <= 0 && !isPurchaseReceipt) return;

        var sets = new List<string>();
        if (totalQty > 0) sets.Add("avg_cost = @avg");
        if (isPurchaseReceipt) sets.Add("last_purchase_cost = @last");
        if (sets.Count == 0) return;
        using var upd = CreateCommand(connection, transaction,
            $"UPDATE materials SET {string.Join(", ", sets)} WHERE company_id = @c AND material_id = @m");
        if (totalQty > 0) Add(upd, "@avg", ValuationMath.RoundRate(totalValue / totalQty));
        if (isPurchaseReceipt) Add(upd, "@last", valuationRate);
        Add(upd, "@c", material.CompanyKey);
        Add(upd, "@m", request.MaterialId);
        upd.ExecuteNonQuery();
    }

    // ------------------------------------------------------------------
    // ADO helpers (GlPosting conventions)
    // ------------------------------------------------------------------
    private static DbCommand CreateCommand(DbConnection connection, DbTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
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

    private static string NormalizeProvider(string provider) => provider.Trim().ToLowerInvariant() switch
    {
        "sqlite" or "sqlite3" => "SQLite",
        "postgres" or "postgresql" or "npgsql" => "PostgreSQL",
        _ => throw new NotSupportedException($"Unsupported database provider '{provider}'. Use SQLite or PostgreSQL.")
    };
}
