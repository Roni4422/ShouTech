using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.Domain;

namespace ShouTech.Infrastructure;

/// <summary>
/// Recalculate Costs (Fix Phase Step 6, Phase B design section 1,
/// owner decision V2: posting-order valuation + on-demand
/// recalculation, the Al-Ameen regenerate-costs / ERPNext repost
/// pattern made manual and visible).
///
/// Replays material_stock_movements for the requested scope in
/// (movement_date, stock_movement_id) order through the same
/// ValuationMath the posting engine uses, rewriting the valuation
/// columns of every movement from the from-date onward, rebuilding
/// the layer queues and balances, and recording the job in
/// stock_revaluation_jobs with the total value delta. Receipt rates
/// are the movements' recorded actual costs; issues are re-valued
/// from the replayed balance/queue; linked sales returns re-enter at
/// the replayed rate of their original issue. Movements before the
/// from-date are read (to build the opening state) but never
/// rewritten, and the job refuses to run when its range touches a
/// closed fiscal period (INV-6004). GL entries of already-posted
/// documents are NOT restated — see docs/FIX_PHASE_STEP6.md flag F-2.
/// </summary>
public sealed class StockRecalculationService
{
    private readonly string _provider;
    private readonly string _connectionString;

    public StockRecalculationService(string provider, string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("A database connection string is required.", nameof(connectionString));
        _provider = NormalizeProvider(provider);
        _connectionString = connectionString;
    }

    private bool IsPg => _provider == "PostgreSQL";
    private string NotDeleted => IsPg ? "is_deleted = FALSE" : "is_deleted = 0";
    private string NowExpr => IsPg ? "now()" : "datetime('now')";

    public async Task<RecalculationResult> RecalculateAsync(
        string companyId, long? warehouseId, long? materialId,
        DateOnly fromDate, string? reason, string userId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(companyId))
            throw new ArgumentException("Company is required.", nameof(companyId));
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("User is required.", nameof(userId));
        var company = companyId.Trim();
        var jobId = Guid.NewGuid().ToString("D");
        var fromText = fromDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        await WriteJobAsync(jobId, company, warehouseId, materialId, fromText, reason,
            "Running", userId, null, 0, 0m, cancellationToken).ConfigureAwait(false);
        try
        {
            var (replayed, delta) = await ReplayAsync(company, warehouseId, materialId, fromDate, fromText, jobId, cancellationToken)
                .ConfigureAwait(false);
            await WriteJobAsync(jobId, company, warehouseId, materialId, fromText, reason,
                "Done", userId, DateTime.UtcNow, replayed, delta, cancellationToken).ConfigureAwait(false);
            return new RecalculationResult(jobId, replayed, delta);
        }
        catch (AppError ex)
        {
            await WriteJobFailedAsync(jobId, ex.Code + ": " + ex.Message, cancellationToken).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await WriteJobFailedAsync(jobId, ex.Message, cancellationToken).ConfigureAwait(false);
            throw new AppError(ErrorCodes.RecalculationFailed,
                "The Recalculate Costs job failed.", ex);
        }
    }

    private async Task WriteJobFailedAsync(string jobId, string error, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(CancellationToken.None).ConfigureAwait(false);
        await using var cmd = CreateCommand(connection, null,
            $"UPDATE stock_revaluation_jobs SET status = 'Failed', completed_at = {NowExpr}, error = @error WHERE job_id = @id");
        Add(cmd, "@error", error);
        Add(cmd, "@id", jobId);
        await cmd.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task WriteJobAsync(
        string jobId, string company, long? warehouseId, long? materialId, string fromText,
        string? reason, string status, string userId, DateTime? completedAt,
        int replayed, decimal delta, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = CreateCommand(connection, null, """
            INSERT INTO stock_revaluation_jobs
                (job_id, company_id, warehouse_id, material_id, from_date, reason, status, created_by, movements_replayed, value_delta)
            VALUES (@id, @c, @w, @m, @from, @reason, @status, @user, @replayed, @delta)
            ON CONFLICT (job_id) DO UPDATE SET status = EXCLUDED.status,
                movements_replayed = EXCLUDED.movements_replayed, value_delta = EXCLUDED.value_delta
            """);
        // SQLite supports the same ON CONFLICT upsert syntax.
        Add(cmd, "@id", jobId);
        Add(cmd, "@c", company);
        Add(cmd, "@w", (object?)warehouseId ?? DBNull.Value);
        Add(cmd, "@m", (object?)materialId ?? DBNull.Value);
        Add(cmd, "@from", fromText);
        Add(cmd, "@reason", (object?)reason ?? DBNull.Value);
        Add(cmd, "@status", status);
        Add(cmd, "@user", userId);
        Add(cmd, "@replayed", replayed);
        Add(cmd, "@delta", delta);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (completedAt is not null && status == "Done")
        {
            await using var done = CreateCommand(connection, null,
                $"UPDATE stock_revaluation_jobs SET completed_at = {NowExpr} WHERE job_id = @id");
            Add(done, "@id", jobId);
            await done.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------
    // Replay
    // ------------------------------------------------------------------
    private sealed record MovementRow(
        long Id, string Date, string Type, string DocNo,
        decimal QtyIn, decimal QtyOut, decimal UnitCost);

    private async Task<(int Replayed, decimal Delta)> ReplayAsync(
        string company, long? warehouseFilter, long? materialFilter,
        DateOnly fromDate, string fromText, string jobId, CancellationToken ct)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsurePeriodOpenAsync(connection, transaction, company, fromDate, fromText, ct).ConfigureAwait(false);

            // Cross-warehouse / cross-key lookups the per-key replay
            // needs: transfer-in rates (the matching transfer-out's
            // replayed rate is unknowable per-key, so the recorded
            // out rate is used — engine-written rows carry it as their
            // valuation rate) and return-document originals.
            var transferOutRates = await LoadTransferOutRatesAsync(connection, transaction, company, ct).ConfigureAwait(false);
            var originalDocByReturnDoc = await LoadOriginalDocsAsync(connection, transaction, company, ct).ConfigureAwait(false);

            var keys = new List<(long WarehouseId, long MaterialId)>();
            await using (var cmd = CreateCommand(connection, transaction, $"""
                SELECT DISTINCT warehouse_id, material_id FROM material_stock_movements
                WHERE company_id = @c AND {NotDeleted}
                  AND (@w IS NULL OR warehouse_id = @w) AND (@m IS NULL OR material_id = @m)
                ORDER BY warehouse_id, material_id
                """))
            {
                Add(cmd, "@c", company);
                Add(cmd, "@w", (object?)warehouseFilter ?? DBNull.Value);
                Add(cmd, "@m", (object?)materialFilter ?? DBNull.Value);
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    keys.Add((Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                              Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture)));
            }

            var totalReplayed = 0;
            var totalDelta = 0m;
            var touchedMaterials = new HashSet<long>();
            foreach (var (warehouseId, materialId) in keys)
            {
                var (replayed, delta) = await ReplayKeyAsync(connection, transaction, company,
                    warehouseId, materialId, fromText, jobId, transferOutRates, originalDocByReturnDoc, ct)
                    .ConfigureAwait(false);
                totalReplayed += replayed;
                totalDelta += delta;
                if (replayed > 0) touchedMaterials.Add(materialId);
            }

            foreach (var materialId in touchedMaterials)
                await RefreshMaterialCardAsync(connection, transaction, company, materialId, ct).ConfigureAwait(false);

            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return (totalReplayed, totalDelta);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<(int Replayed, decimal Delta)> ReplayKeyAsync(
        DbConnection connection, DbTransaction transaction, string company,
        long warehouseId, long materialId, string fromText, string jobId,
        IReadOnlyDictionary<(string DocNo, long MaterialId), decimal> transferOutRates,
        IReadOnlyDictionary<string, string> originalDocByReturnDoc,
        CancellationToken ct)
    {
        // Method + card fallbacks for this material.
        string method = ValuationMath.WeightedAvg;
        decimal standardCost = 0m, lastPurchase = 0m, cardAvg = 0m;
        await using (var cmd = CreateCommand(connection, transaction, $"""
            SELECT cost_method, standard_cost, last_purchase_cost, avg_cost FROM materials
            WHERE material_id = @m AND {NotDeleted} LIMIT 1
            """))
        {
            Add(cmd, "@m", materialId);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                method = ValuationMath.NormalizeMethod(ValuationText.ToText(reader.GetValue(0)));
                standardCost = ValuationText.ToDecimal(reader.GetValue(1));
                lastPurchase = ValuationText.ToDecimal(reader.GetValue(2));
                cardAvg = ValuationText.ToDecimal(reader.GetValue(3));
            }
        }

        var movements = new List<MovementRow>();
        await using (var cmd = CreateCommand(connection, transaction, $"""
            SELECT stock_movement_id, movement_date, movement_type, document_no, qty_in, qty_out, unit_cost
            FROM material_stock_movements
            WHERE company_id = @c AND warehouse_id = @w AND material_id = @m AND {NotDeleted}
            ORDER BY movement_date, stock_movement_id
            """))
        {
            Add(cmd, "@c", company);
            Add(cmd, "@w", warehouseId);
            Add(cmd, "@m", materialId);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                movements.Add(new MovementRow(
                    Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                    NormalizeDateText(reader.GetValue(1)),
                    ValuationText.ToText(reader.GetValue(2)) ?? string.Empty,
                    ValuationText.ToText(reader.GetValue(3)) ?? string.Empty,
                    ValuationText.ToDecimal(reader.GetValue(4)),
                    ValuationText.ToDecimal(reader.GetValue(5)),
                    ValuationText.ToDecimal(reader.GetValue(6))));
            }
        }
        if (movements.Count == 0) return (0, 0m);

        decimal balanceBefore = 0m;
        await using (var cmd = CreateCommand(connection, transaction, $"""
            SELECT COALESCE(stock_value, 0) FROM material_stock_balances
            WHERE company_id = @c AND warehouse_id = @w AND material_id = @m AND {NotDeleted} LIMIT 1
            """))
        {
            Add(cmd, "@c", company);
            Add(cmd, "@w", warehouseId);
            Add(cmd, "@m", materialId);
            var value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            balanceBefore = ValuationText.ToDecimal(value);
        }

        var state = new ValuationState();
        // Replayed issue rates per document (for linked returns).
        var issueRateByDoc = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var replayed = 0;

        foreach (var movement in movements)
        {
            var inScope = string.CompareOrdinal(movement.Date, fromText) >= 0;
            decimal valuationRate, costAmount;
            var usedFallback = false;

            if (movement.QtyIn > 0 && movement.QtyOut > 0)
                continue; // a movement is one-sided by construction; skip corrupt rows untouched
            if (movement.QtyIn > 0)
            {
                var rate = ReplayReceiptRate(movement, materialId, method, standardCost, state,
                    transferOutRates, originalDocByReturnDoc, issueRateByDoc);
                var outcome = ValuationMath.Receipt(state, method, movement.QtyIn, rate, movement.Date);
                valuationRate = outcome.ValuationRate;
                costAmount = outcome.CostAmount;
            }
            else if (movement.QtyOut > 0)
            {
                var nonLayerRate = method switch
                {
                    ValuationMath.Standard => standardCost,
                    ValuationMath.LastPurchase => lastPurchase > 0 ? lastPurchase
                        : state.DisplayRate > 0 ? state.DisplayRate : Fallback(cardAvg, lastPurchase, standardCost, state),
                    _ => state.DisplayRate > 0 ? state.DisplayRate : Fallback(cardAvg, lastPurchase, standardCost, state)
                };
                var consumeNewest = movement.Type is "CNT" or "COUNT_CLOSE";
                var outcome = ValuationMath.Issue(state, method, movement.QtyOut, nonLayerRate,
                    Fallback(cardAvg, lastPurchase, standardCost, state), movement.Date, consumeNewest);
                valuationRate = outcome.ValuationRate;
                costAmount = -outcome.CostAmount;
                usedFallback = outcome.UsedFallback;
                issueRateByDoc[movement.DocNo] = valuationRate;
            }
            else
            {
                continue;
            }

            if (!inScope) continue;
            replayed++;
            await using var upd = CreateCommand(connection, transaction, $"""
                UPDATE material_stock_movements
                SET valuation_rate = @rate, cost_amount = @costAmt,
                    balance_qty_after = @balQty, stock_value_after = @stockVal,
                    valuation_method = @method, repost_batch_id = @job
                    {(movement.QtyOut > 0 ? ", unit_cost = @rate" : "")}
                WHERE stock_movement_id = @id
                """);
            Add(upd, "@rate", valuationRate);
            Add(upd, "@costAmt", costAmount);
            Add(upd, "@balQty", state.Quantity);
            Add(upd, "@stockVal", state.Value);
            Add(upd, "@method", usedFallback ? method + "-Fallback" : method);
            Add(upd, "@job", jobId);
            Add(upd, "@id", movement.Id);
            await upd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        if (replayed == 0) return (0, 0m);

        // Rebuild the balance + layer queue from the replayed end state.
        var displayRate = state.Quantity > 0 ? ValuationMath.RoundRate(state.Value / state.Quantity) : 0m;
        await using (var cmd = CreateCommand(connection, transaction, $"""
            INSERT INTO material_stock_balances
                (company_id, warehouse_id, material_id, available_qty, reserved_qty, average_cost, last_cost, stock_value, last_updated)
            VALUES (@c, @w, @m, @qty, 0, @avg, @avg, @val, {NowExpr})
            ON CONFLICT (company_id, warehouse_id, material_id)
            DO UPDATE SET available_qty = @qty, average_cost = @avg, stock_value = @val, last_updated = {NowExpr}
            """))
        {
            Add(cmd, "@c", company);
            Add(cmd, "@w", warehouseId);
            Add(cmd, "@m", materialId);
            Add(cmd, "@qty", state.Quantity);
            Add(cmd, "@avg", displayRate);
            Add(cmd, "@val", state.Value);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        if (ValuationMath.UsesLayers(method))
        {
            await using (var del = CreateCommand(connection, transaction,
                "DELETE FROM stock_cost_layers WHERE company_id = @c AND warehouse_id = @w AND material_id = @m"))
            {
                Add(del, "@c", company);
                Add(del, "@w", warehouseId);
                Add(del, "@m", materialId);
                await del.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            foreach (var layer in state.Layers.Where(l => l.QtyRemaining != 0))
            {
                await using var ins = CreateCommand(connection, transaction, IsPg
                    ? "INSERT INTO stock_cost_layers (company_id, warehouse_id, material_id, source_movement_id, layer_date, qty_in, qty_remaining, unit_cost, is_open) VALUES (@c, @w, @m, NULL, @date, @qin, @rem, @cost, TRUE)"
                    : "INSERT INTO stock_cost_layers (company_id, warehouse_id, material_id, source_movement_id, layer_date, qty_in, qty_remaining, unit_cost, is_open) VALUES (@c, @w, @m, NULL, @date, @qin, @rem, @cost, 1)");
                Add(ins, "@c", company);
                Add(ins, "@w", warehouseId);
                Add(ins, "@m", materialId);
                Add(ins, "@date", layer.LayerDate);
                Add(ins, "@qin", Math.Max(layer.QtyIn, 0m));
                Add(ins, "@rem", layer.QtyRemaining);
                Add(ins, "@cost", layer.UnitCost);
                await ins.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }

        return (replayed, state.Value - balanceBefore);
    }

    private static decimal ReplayReceiptRate(
        MovementRow movement, long movementMaterialId, string method, decimal standardCost, ValuationState state,
        IReadOnlyDictionary<(string DocNo, long MaterialId), decimal> transferOutRates,
        IReadOnlyDictionary<string, string> originalDocByReturnDoc,
        IReadOnlyDictionary<string, decimal> issueRateByDoc)
    {
        if (method == ValuationMath.Standard) return standardCost;
        switch (movement.Type)
        {
            case "SRT":
                // Linked return: re-enter at the original issue's
                // replayed rate. Unlinked: the recorded entry rate for
                // engine rows; legacy rows recorded the selling price,
                // so fall back to the current replayed rate instead.
                if (originalDocByReturnDoc.TryGetValue(movement.DocNo, out var original)
                    && issueRateByDoc.TryGetValue(original, out var originalRate))
                    return originalRate;
                return state.DisplayRate > 0 ? state.DisplayRate : movement.UnitCost;
            case "TRANSFER_IN":
                // In at the matching transfer-out's rate (same document
                // number + material), else the recorded entry cost.
                return transferOutRates.TryGetValue((movement.DocNo, movementMaterialId), out var outRate)
                    ? outRate
                    : movement.UnitCost;
            case "CNT":
            case "COUNT_CLOSE":
                // Surplus enters at the current rate (design); the
                // recorded count cost is only the no-rate fallback.
                return state.DisplayRate > 0 ? state.DisplayRate : movement.UnitCost;
            default:
                // Purchases, stock-in, opening, manufacturing produce:
                // the recorded unit_cost IS the actual entry cost.
                return movement.UnitCost;
        }
    }

    private static decimal Fallback(decimal cardAvg, decimal lastPurchase, decimal standardCost, ValuationState state)
    {
        if (state.DisplayRate > 0) return state.DisplayRate;
        if (cardAvg > 0) return cardAvg;
        if (lastPurchase > 0) return lastPurchase;
        return standardCost;
    }

    private async Task<Dictionary<(string DocNo, long MaterialId), decimal>> LoadTransferOutRatesAsync(
        DbConnection connection, DbTransaction transaction, string company, CancellationToken ct)
    {
        // Keyed by (document_no, material_id); stored in a flat map the
        // caller probes with the movement's own material.
        var map = new Dictionary<(string, long), decimal>();
        await using var cmd = CreateCommand(connection, transaction, $"""
            SELECT document_no, material_id, COALESCE(valuation_rate, unit_cost)
            FROM material_stock_movements
            WHERE company_id = @c AND movement_type = 'TRANSFER_OUT' AND {NotDeleted}
            """);
        Add(cmd, "@c", company);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var doc = ValuationText.ToText(reader.GetValue(0)) ?? string.Empty;
            var material = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
            map[(doc, material)] = ValuationText.ToDecimal(reader.GetValue(2));
        }
        return map;
    }

    private async Task<Dictionary<string, string>> LoadOriginalDocsAsync(
        DbConnection connection, DbTransaction transaction, string company, CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in new[] { "sales_documents", "purchase_documents" })
        {
            await using var cmd = CreateCommand(connection, transaction, $"""
                SELECT doc_no, original_doc_no FROM {table}
                WHERE company_id = @c AND {NotDeleted}
                  AND original_doc_no IS NOT NULL AND original_doc_no <> ''
                """);
            Add(cmd, "@c", company);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var doc = ValuationText.ToText(reader.GetValue(0));
                var original = ValuationText.ToText(reader.GetValue(1));
                if (!string.IsNullOrWhiteSpace(doc) && !string.IsNullOrWhiteSpace(original))
                    map[doc] = original;
            }
        }
        return map;
    }

    private async Task EnsurePeriodOpenAsync(
        DbConnection connection, DbTransaction transaction, string company,
        DateOnly fromDate, string fromText, CancellationToken ct)
    {
        // Fiscal periods key the company by Guid; stock keys it by code.
        // No companies row / no periods at all means the company runs
        // without fiscal setup and there is nothing closed to protect.
        Guid companyGuid = Guid.Empty;
        await using (var cmd = CreateCommand(connection, transaction,
            "SELECT company_id FROM companies WHERE company_code = @c LIMIT 1"))
        {
            Add(cmd, "@c", company);
            var value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (value is not null and not DBNull)
                Guid.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out companyGuid);
        }
        if (companyGuid == Guid.Empty) return;

        await using var check = CreateCommand(connection, transaction, $"""
            SELECT COUNT(*) FROM fiscal_periods fp
            JOIN fiscal_years fy ON fy.fiscal_year_id = fp.fiscal_year_id
            WHERE fp.company_id = @company AND fp.end_date >= @from
              AND (fp.status <> 'OPEN' OR fy.is_closed = {(IsPg ? "TRUE" : "1")})
            """);
        Add(check, "@company", IsPg ? (object)companyGuid : companyGuid.ToString("D"));
        Add(check, "@from", IsPg ? (object)DateTime.SpecifyKind(fromDate.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc) : fromText);
        var closed = Convert.ToInt64(await check.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        if (closed > 0)
            throw new AppError(ErrorCodes.RecalculationClosedPeriod,
                $"Recalculate Costs cannot rewrite movements in a closed fiscal period (from date '{fromText}').",
                fromText);
    }

    private async Task RefreshMaterialCardAsync(
        DbConnection connection, DbTransaction transaction, string company, long materialId, CancellationToken ct)
    {
        decimal qty = 0m, value = 0m;
        await using (var cmd = CreateCommand(connection, transaction, $"""
            SELECT COALESCE(SUM(available_qty), 0), COALESCE(SUM(stock_value), 0)
            FROM material_stock_balances
            WHERE company_id = @c AND material_id = @m AND {NotDeleted}
            """))
        {
            Add(cmd, "@c", company);
            Add(cmd, "@m", materialId);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                qty = ValuationText.ToDecimal(reader.GetValue(0));
                value = ValuationText.ToDecimal(reader.GetValue(1));
            }
        }
        if (qty <= 0) return;
        await using var upd = CreateCommand(connection, transaction,
            "UPDATE materials SET avg_cost = @avg WHERE company_id = @c AND material_id = @m");
        Add(upd, "@avg", ValuationMath.RoundRate(value / qty));
        Add(upd, "@c", company);
        Add(upd, "@m", materialId);
        await upd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static string NormalizeDateText(object? value)
    {
        return value switch
        {
            null or DBNull => string.Empty,
            DateTime dt => dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            _ => Truncate(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty)
        };

        static string Truncate(string s) => s.Length >= 10 ? s[..10] : s;
    }

    private DbConnection CreateConnection() => IsPg
        ? new NpgsqlConnection(_connectionString)
        : new SqliteConnection(_connectionString);

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

    private static string NormalizeProvider(string provider) => provider.Trim().ToLowerInvariant() switch
    {
        "sqlite" or "sqlite3" => "SQLite",
        "postgres" or "postgresql" or "npgsql" => "PostgreSQL",
        _ => throw new NotSupportedException($"Unsupported database provider '{provider}'. Use SQLite or PostgreSQL.")
    };
}

/// <summary>Outcome of a Recalculate Costs run.</summary>
public sealed record RecalculationResult(string JobId, int MovementsReplayed, decimal ValueDelta);
