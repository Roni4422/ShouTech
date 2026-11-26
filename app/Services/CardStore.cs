using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.App.Views;
using ShouTech.Infrastructure;

namespace ShouTech.App.Services;

/// <summary>
/// Persistence for the master-data card windows (CurCard,
/// RepCard) and the manufacturing order window (MfgWin), backed by the
/// tables created in migration 050 (currencies, currency_rates,
/// sales_reps, sales_rep_targets, manufacturing_orders,
/// manufacturing_order_lines). Party cards live in the canonical
/// parties store (migration 061, MasterCardRepository); bank cards
/// live in the canonical banks store (migration 031, Step 8).
///
/// Every write is parameterized and transactional: a card header and its
/// child rows (currency rates, rep targets, manufacturing lines) are saved
/// in ONE transaction, and deletes are soft deletes (is_deleted) so the
/// recycle bin and audit trail keep working. Company scoping follows the
/// rest of the app layer: the active company code from
/// <see cref="AppState.CurrentCompanyCode"/>.
/// </summary>
public static class CardStore
{
    // ------------------------------------------------------------------
    // Records (one per window field set — every UI field has a property)
    // ------------------------------------------------------------------

    public sealed record MfgLineRec(
        int LineNo, long? MaterialId, string MaterialCode, string MaterialName,
        string UnitCode, decimal Quantity, decimal UnitCost);

    public sealed record MfgOrderRec(
        long Id, string OrderNo, DateTime OrderDate, long? FinishedMaterialId,
        string FinishedMaterialCode, string FinishedMaterialName, decimal Quantity,
        string UnitCode, long? WarehouseId, string WarehouseCode, decimal OverheadCost,
        decimal TotalCost, decimal UnitCost, string Status, IReadOnlyList<MfgLineRec> Lines);

    // Parties: the canonical party master is `parties` (migration 061),
    // served by MasterCardRepository / MasterCardService. The pre-unification
    // CardStore party methods wrote the legacy customers/suppliers tables and
    // had no callers (sweep R-3); they were removed in Step 4 rather than
    // dual-written.

    // ------------------------------------------------------------------
    // Banks: the canonical bank-account store is `banks` (migration
    // 031), served by MasterCardRepository / MasterCardService and used
    // by BnkCard and BankRecWin. The pre-Step-8 CardStore bank path
    // wrote the migration-050 `bank_accounts` table and had zero
    // callers (sweep BNKC-01); it was removed in Step 8 rather than
    // dual-written, and migration 063 copied any legacy rows into
    // `banks`. Do not re-add a second bank store here.
    // ------------------------------------------------------------------

    // ------------------------------------------------------------------
    // Manufacturing orders — MfgWin
    // ------------------------------------------------------------------

    public static MfgOrderRec? LoadOrder(string orderNo)
    {
        if (string.IsNullOrWhiteSpace(orderNo)) return null;
        var cs = ConnectionString();
        if (cs is null) return null;
        using var connection = Open(cs);
        MfgOrderRec rec;
        long orderId;
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT order_id, order_no, order_date, finished_material_id, finished_material_code,
                       finished_material_name, quantity, unit_code, warehouse_id, warehouse_code,
                       overhead_cost, total_cost, unit_cost, status
                FROM manufacturing_orders
                WHERE company_id=$c AND order_no=$no AND is_deleted=0
                LIMIT 1;
                """;
            Add(cmd, "$c", Company());
            Add(cmd, "$no", orderNo.Trim());
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            orderId = GetLong(reader, 0);
            rec = new MfgOrderRec(orderId, GetText(reader, 1),
                DateTime.TryParse(GetText(reader, 2), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : DateTime.Today,
                GetNullableLong(reader, 3), GetText(reader, 4), GetText(reader, 5), GetDec(reader, 6),
                GetText(reader, 7), GetNullableLong(reader, 8), GetText(reader, 9), GetDec(reader, 10),
                GetDec(reader, 11), GetDec(reader, 12), GetText(reader, 13), Array.Empty<MfgLineRec>());
        }

        var lines = new List<MfgLineRec>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT line_no, material_id, material_code, material_name, unit_code, quantity, unit_cost
                FROM manufacturing_order_lines
                WHERE company_id=$c AND order_id=$id
                ORDER BY line_no;
                """;
            Add(cmd, "$c", Company());
            Add(cmd, "$id", orderId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                lines.Add(new MfgLineRec((int)GetDec(reader, 0), GetNullableLong(reader, 1),
                    GetText(reader, 2), GetText(reader, 3), GetText(reader, 4),
                    GetDec(reader, 5), GetDec(reader, 6)));
            }
        }
        return rec with { Lines = lines };
    }

    /// <summary>
    /// Saves a manufacturing order (header + all lines) in one transaction.
    /// Posted orders are immutable here — Save refuses them so stock
    /// movements can never diverge from the saved lines.
    /// </summary>
    public static long SaveOrder(MfgOrderRec rec, string actor)
    {
        var cs = ConnectionString() ?? throw new InvalidOperationException("No database connection is configured.");
        using var connection = Open(cs);
        using var tx = connection.BeginTransaction();
        long? existing = FindId(connection, tx, "manufacturing_orders", "order_id", "order_no", rec.OrderNo);
        if (existing.HasValue)
        {
            using var chk = connection.CreateCommand();
            chk.Transaction = tx;
            chk.CommandText = "SELECT status FROM manufacturing_orders WHERE company_id=$c AND order_id=$id;";
            Add(chk, "$c", Company());
            Add(chk, "$id", existing.Value);
            var status = Convert.ToString(chk.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (string.Equals(status, "Posted", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A posted manufacturing order cannot be edited.");
        }

        long id;
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            if (existing.HasValue)
            {
                cmd.CommandText = $"""
                    UPDATE manufacturing_orders SET order_date=$date, finished_material_id=$fmid,
                        finished_material_code=$fmcode, finished_material_name=$fmname, quantity=$qty,
                        unit_code=$unit, warehouse_id=$whid, warehouse_code=$whcode,
                        overhead_cost=$over, total_cost=$total, unit_cost=$ucost,
                        updated_by=$actor, updated_at={Now()}
                    WHERE company_id=$c AND order_id=$id AND is_deleted=0;
                    """;
                Add(cmd, "$id", existing.Value);
            }
            else
            {
                cmd.CommandText = """
                    INSERT INTO manufacturing_orders
                        (company_id, order_no, order_date, finished_material_id, finished_material_code,
                         finished_material_name, quantity, unit_code, warehouse_id, warehouse_code,
                         overhead_cost, total_cost, unit_cost, status, created_by, updated_by)
                    VALUES ($c, $no, $date, $fmid, $fmcode, $fmname, $qty, $unit, $whid, $whcode,
                            $over, $total, $ucost, 'Draft', $actor, $actor)
                    RETURNING order_id;
                    """;
                Add(cmd, "$no", rec.OrderNo);
            }
            Add(cmd, "$c", Company());
            Add(cmd, "$date", rec.OrderDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            Add(cmd, "$fmid", rec.FinishedMaterialId);
            Add(cmd, "$fmcode", rec.FinishedMaterialCode);
            Add(cmd, "$fmname", rec.FinishedMaterialName);
            Add(cmd, "$qty", rec.Quantity);
            Add(cmd, "$unit", rec.UnitCode);
            Add(cmd, "$whid", rec.WarehouseId);
            Add(cmd, "$whcode", rec.WarehouseCode);
            Add(cmd, "$over", rec.OverheadCost);
            Add(cmd, "$total", rec.TotalCost);
            Add(cmd, "$ucost", rec.UnitCost);
            Add(cmd, "$actor", actor);
            if (existing.HasValue) { cmd.ExecuteNonQuery(); id = existing.Value; }
            else id = Convert.ToInt64(cmd.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
        }

        using (var del = connection.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM manufacturing_order_lines WHERE company_id=$c AND order_id=$id;";
            Add(del, "$c", Company());
            Add(del, "$id", id);
            del.ExecuteNonQuery();
        }
        foreach (var line in rec.Lines)
        {
            using var ins = connection.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT INTO manufacturing_order_lines
                    (company_id, order_id, line_no, material_id, material_code, material_name,
                     unit_code, quantity, unit_cost, total_cost)
                VALUES ($c, $id, $no, $mid, $mcode, $mname, $unit, $qty, $ucost, $total);
                """;
            Add(ins, "$c", Company());
            Add(ins, "$id", id);
            Add(ins, "$no", line.LineNo);
            Add(ins, "$mid", line.MaterialId);
            Add(ins, "$mcode", line.MaterialCode);
            Add(ins, "$mname", line.MaterialName);
            Add(ins, "$unit", line.UnitCode);
            Add(ins, "$qty", line.Quantity);
            Add(ins, "$ucost", line.UnitCost);
            Add(ins, "$total", line.Quantity * line.UnitCost);
            ins.ExecuteNonQuery();
        }
        tx.Commit();
        return id;
    }

    /// <summary>
    /// Posts a saved draft order in ONE transaction: consumes the raw
    /// materials (qty_out) and receives the finished good (qty_in at the
    /// computed unit cost) through the ONE StockValuationEngine
    /// (Step 6, Phase B 053 — this class's private weighted-average copy
    /// is gone), then flips the order to Posted.
    /// </summary>
    public static void PostOrder(long orderId, string actor)
    {
        var order = LoadOrderById(orderId)
            ?? throw new InvalidOperationException("The manufacturing order was not found.");
        if (string.Equals(order.Status, "Posted", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The manufacturing order is already posted.");
        if (order.WarehouseId is null)
            throw new InvalidOperationException("The order warehouse could not be resolved; posting requires a warehouse.");
        if (order.FinishedMaterialId is null)
            throw new InvalidOperationException("The finished material could not be resolved; posting requires a material card.");

        var cs = ConnectionString() ?? throw new InvalidOperationException("No database connection is configured.");
        using var connection = Open(cs);
        using var tx = connection.BeginTransaction();
        var dateText = order.OrderDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var provider = DatabaseRuntime.IsPostgreSql() ? "PostgreSQL" : "SQLite";

        // Unit equivalents (Step 11, sweep MFG-2): stock moves in BASE
        // units, converted by the SAME StkPost.ToBaseUnits the invoice
        // path uses — a line in a non-base unit moves qty × equivalent,
        // and the finished receipt credits the base quantity at the
        // total cost restated per base unit.
        foreach (var line in order.Lines)
        {
            if (line.MaterialId is null || line.Quantity <= 0) continue;
            var (baseQty, _) = StkPost.ToBaseUnits(connection, tx, line.MaterialId.Value,
                line.UnitCode, line.Quantity, line.UnitCost);
            // Batch/serial (054): raw batches auto-allocate FEFO/FIFO;
            // serials auto-pick (no capture UI on this path).
            StockValuationEngine.Apply(connection, tx, provider,
                new StockMovementRequest(Company(), order.WarehouseId.Value, line.MaterialId.Value,
                    dateText, "MFG-OUT", order.OrderNo,
                    QtyIn: 0m, QtyOut: baseQty, Note: line.MaterialName,
                    AutoAllocateSerials: true));
        }

        var (finishedBaseQty, finishedBaseCost) = StkPost.ToBaseUnits(connection, tx,
            order.FinishedMaterialId.Value, order.UnitCode, order.Quantity, order.UnitCost);
        // Batch/serial (054): finished batch named after the order;
        // finished serials system-generated, mirroring MfgOrderRepository.
        var finishedTracking = BatchSerialCapture.LoadTracking(connection, tx, provider,
            Company(), order.FinishedMaterialId.Value);
        IReadOnlyList<BatchAllocationRequest>? finishedBatches = null;
        IReadOnlyList<SerialSelection>? finishedSerials = null;
        if (finishedTracking.HasBatch)
            finishedBatches = new[] { new BatchAllocationRequest(order.OrderNo, finishedBaseQty) };
        if (finishedTracking.HasSerial)
        {
            if (finishedBaseQty != decimal.Truncate(finishedBaseQty))
                throw new InvalidOperationException(
                    "The finished material is serial-tracked: the produced quantity must be a whole number of units.");
            finishedSerials = Enumerable.Range(1, (int)finishedBaseQty)
                .Select(i => new SerialSelection($"{order.OrderNo}-{i}"))
                .ToList();
        }
        StockValuationEngine.Apply(connection, tx, provider,
            new StockMovementRequest(Company(), order.WarehouseId.Value, order.FinishedMaterialId.Value,
                dateText, "MFG-IN", order.OrderNo,
                QtyIn: finishedBaseQty, QtyOut: 0m, ReceiptUnitCost: finishedBaseCost,
                Note: order.FinishedMaterialName,
                BatchAllocations: finishedBatches, Serials: finishedSerials));

        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"""
                UPDATE manufacturing_orders SET status='Posted', updated_by=$actor, updated_at={Now()}
                WHERE company_id=$c AND order_id=$id AND is_deleted=0;
                """;
            Add(cmd, "$c", Company());
            Add(cmd, "$id", orderId);
            Add(cmd, "$actor", actor);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private static MfgOrderRec? LoadOrderById(long orderId)
    {
        var cs = ConnectionString();
        if (cs is null) return null;
        using var connection = Open(cs);
        string? orderNo = null;
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT order_no FROM manufacturing_orders WHERE company_id=$c AND order_id=$id AND is_deleted=0 LIMIT 1;";
            Add(cmd, "$c", Company());
            Add(cmd, "$id", orderId);
            var value = cmd.ExecuteScalar();
            if (value is not null and not DBNull) orderNo = Convert.ToString(value, CultureInfo.InvariantCulture);
        }
        return orderNo is null ? null : LoadOrder(orderNo);
    }

    /// <summary>Resolves a material code to (id, name) in the active company; null when unknown.</summary>
    public static (long Id, string Name)? ResolveMaterial(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var cs = ConnectionString();
        if (cs is null) return null;
        try
        {
            using var connection = Open(cs);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT material_id, name FROM materials
                WHERE company_id=$c AND code=$code AND is_deleted=0
                LIMIT 1;
                """;
            Add(cmd, "$c", Company());
            Add(cmd, "$code", code.Trim());
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            return (GetLong(reader, 0), GetText(reader, 1));
        }
        catch (Exception ex)
        {
            AppHost.Logging?.LogError("CardStore", $"ResolveMaterial failed | Code={code}", ex);
            return null;
        }
    }

    /// <summary>
    /// Resolves a warehouse by its numeric code text (warehouses.warehouse_code)
    /// or by exact name; null when unknown.
    /// </summary>
    public static long? ResolveWarehouseId(string codeOrName)
    {
        if (string.IsNullOrWhiteSpace(codeOrName)) return null;
        var cs = ConnectionString();
        if (cs is null) return null;
        try
        {
            using var connection = Open(cs);
            using var cmd = connection.CreateCommand();
            if (long.TryParse(codeOrName.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
            {
                cmd.CommandText = """
                    SELECT warehouse_id FROM warehouses
                    WHERE company_id=$c AND warehouse_code=$code AND is_deleted=0
                    LIMIT 1;
                    """;
                Add(cmd, "$code", numeric);
            }
            else
            {
                cmd.CommandText = """
                    SELECT warehouse_id FROM warehouses
                    WHERE company_id=$c AND (warehouse_name_ar=$code OR warehouse_name_en=$code) AND is_deleted=0
                    LIMIT 1;
                    """;
                Add(cmd, "$code", codeOrName.Trim());
            }
            Add(cmd, "$c", Company());
            var value = cmd.ExecuteScalar();
            return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            AppHost.Logging?.LogError("CardStore", $"ResolveWarehouseId failed | CodeOrName={codeOrName}", ex);
            return null;
        }
    }

    // ------------------------------------------------------------------
    // Shared helpers (TaxSvc/RptQuery conventions)
    // ------------------------------------------------------------------

    private static string? ConnectionString()
    {
        var cs = DatabaseRuntime.GetConnectionString();
        return string.IsNullOrWhiteSpace(cs) ? null : cs;
    }

    private static string Company() => AppState.CurrentCompanyCode ?? string.Empty;

    private static string Now() => DatabaseRuntime.IsPostgreSql() ? "now()" : "datetime('now')";

    private static DbConnection Open(string connectionString)
    {
        DbConnection connection = DatabaseRuntime.IsPostgreSql()
            ? new NpgsqlConnection(connectionString)
            : new SqliteConnection(DatabaseRuntime.NormalizeSqliteConnectionString(connectionString));
        connection.Open();
        return connection;
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        command.Parameters.Add(p);
    }

    private static long? FindId(DbConnection connection, DbTransaction tx,
        string table, string idColumn, string codeColumn, string code)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT {idColumn} FROM {table} WHERE company_id=$c AND {codeColumn}=$code AND is_deleted=0 LIMIT 1;";
        Add(cmd, "$c", Company());
        Add(cmd, "$code", code);
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static string GetText(DbDataReader reader, int i)
        => reader.IsDBNull(i) ? string.Empty : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? string.Empty;

    private static decimal GetDec(DbDataReader reader, int i)
        => reader.IsDBNull(i) ? 0m : Convert.ToDecimal(reader.GetValue(i), CultureInfo.InvariantCulture);

    private static long GetLong(DbDataReader reader, int i)
        => reader.IsDBNull(i) ? 0 : Convert.ToInt64(reader.GetValue(i), CultureInfo.InvariantCulture);

    private static long? GetNullableLong(DbDataReader reader, int i)
        => reader.IsDBNull(i) ? null : Convert.ToInt64(reader.GetValue(i), CultureInfo.InvariantCulture);
}
