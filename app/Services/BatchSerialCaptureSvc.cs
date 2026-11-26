using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Windows;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.App.Reporting.Invoices;
using ShouTech.App.Views;
using ShouTech.Domain;
using ShouTech.Infrastructure;

namespace ShouTech.App.Services;

/// <summary>
/// Posting-time batch/serial capture (Phase B 054): before a document
/// posts, every tracked line is offered the BatchSerialPickerWin —
/// receipts capture batch numbers/expiry and serials, issues confirm
/// or override the FEFO/FIFO suggestion and name their serials (POS
/// included, decision B2). Returns the per-line captures keyed by line
/// number for InvRepo.Post, an empty map when nothing is tracked, or
/// null when the user cancels (the post is aborted, the draft stays).
///
/// Lines the engine can satisfy without capture are skipped: returns
/// carrying an original document (the allocator re-enters the original
/// batches/serials) and stock transfers (allocations copy from the
/// source movement). Inventory-count lines capture against the count
/// DELTA (surplus = receipt of the difference, shortage = issue).
/// </summary>
public static class BatchSerialCaptureSvc
{
    public static IReadOnlyDictionary<int, LineBatchSerialCapture>? CaptureForPosting(
        InvDoc doc, Window owner)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (doc.Kind is InvDocKind.Quotation or InvDocKind.StockTransfer)
            return new Dictionary<int, LineBatchSerialCapture>();

        var isOut = doc.Kind is InvDocKind.SalesInvoice or InvDocKind.PosSale
            or InvDocKind.PurchaseReturn or InvDocKind.StockOut;
        var isReturn = doc.Kind is InvDocKind.SalesReturn or InvDocKind.PurchaseReturn;
        var isCount = doc.Kind == InvDocKind.InventoryCount;

        var company = AppState.CurrentCompanyCode ?? string.Empty;
        var cs = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(cs))
            return new Dictionary<int, LineBatchSerialCapture>();

        var result = new Dictionary<int, LineBatchSerialCapture>();
        using DbConnection connection = DatabaseRuntime.IsPostgreSql()
            ? new NpgsqlConnection(cs)
            : new SqliteConnection(DatabaseRuntime.NormalizeSqliteConnectionString(cs));
        connection.Open();
        using var tx = connection.BeginTransaction();

        var service = new BatchSerialService(
            DatabaseRuntime.IsPostgreSql() ? "PostgreSQL" : "SQLite",
            DatabaseRuntime.IsPostgreSql() ? cs : DatabaseRuntime.NormalizeSqliteConnectionString(cs));
        var canOverride = AppHost.Permissions.Has(PermCat.Inventory.BatchExpiredOverride);

        foreach (var line in doc.Lines)
        {
            if (line.Qty == 0) continue;
            var materialId = line.MaterialId
                ?? ResolveMaterialId(connection, tx, company, line.MaterialCode);
            if (materialId is null) continue;

            var flags = LoadFlags(connection, tx, materialId.Value);
            if (flags is null || !flags.Value.Stockable || !flags.Value.Tracked) continue;
            if (isReturn && !string.IsNullOrWhiteSpace(doc.OriginalDocNo)) continue;

            var warehouseId = line.WarehouseId ?? doc.WarehouseId
                ?? DefaultWarehouse(connection, tx, company);
            var (baseQty, _) = StkPost.ToBaseUnits(connection, tx, materialId.Value,
                line.Unit, line.Qty, line.UnitPrice);
            var equivalent = line.Qty != 0 ? baseQty / line.Qty : 1m;

            // Direction + capture quantity (count documents capture
            // against the delta, in the line's unit for batches and in
            // base units for the serial count).
            var lineIsOut = isOut;
            var batchQty = Math.Abs(line.Qty);
            var serialQty = Math.Abs(baseQty);
            if (isCount)
            {
                var current = CurrentQty(connection, tx, company, warehouseId, materialId.Value);
                var delta = baseQty - current;
                if (delta == 0) continue;
                lineIsOut = delta < 0;
                batchQty = Math.Abs(delta) / (equivalent == 0 ? 1m : equivalent);
                serialQty = Math.Abs(delta);
            }

            var needBatch = flags.Value.HasBatch;
            var needSerial = flags.Value.HasSerial;
            if (needSerial && serialQty != decimal.Truncate(serialQty))
            {
                var ex = new AppError(ErrorCodes.BatchAllocationMismatch,
                    $"Material '{line.MaterialCode}' is serial-tracked: the quantity must be a whole number of units.",
                    line.MaterialCode);
                AppHost.Errors.Report(ex.Code, ex, "BatchSerialCaptureSvc.CaptureForPosting");
                MessageBox.Show(owner, ex.Message, T("Common.Error"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return null;
            }

            IReadOnlyList<BatchBalanceRow>? available = null;
            IReadOnlyList<BatchAllocationRequest>? suggestion = null;
            IReadOnlyList<string>? inStockSerials = null;
            if (!lineIsOut)
            {
                // Receipts capture fresh batch/serial identity.
            }
            else
            {
                if (needBatch && warehouseId is not null)
                {
                    available = service.GetBatchBalances(company, materialId.Value, warehouseId.Value)
                        .Where(b => b.QtyOnHand > 0).ToList();
                    try
                    {
                        // The suggestion is expressed in base units;
                        // restate per line unit for the picker.
                        var baseSuggestion = service.SuggestAllocations(company, warehouseId.Value,
                            materialId.Value, Math.Abs(baseQty),
                            doc.DocDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                        suggestion = baseSuggestion
                            .Select(s => new BatchAllocationRequest(s.BatchNo,
                                equivalent == 0 ? s.Qty : s.Qty / equivalent, s.ExpiryDate))
                            .ToList();
                    }
                    catch (AppError)
                    {
                        // Insufficient batch stock: the picker still
                        // opens with the balances; the engine enforces.
                        suggestion = null;
                    }
                }
                if (needSerial && warehouseId is not null)
                    inStockSerials = InStockSerials(connection, tx, company, warehouseId.Value, materialId.Value);
            }

            var label = string.IsNullOrWhiteSpace(line.Description)
                ? line.MaterialCode
                : $"{line.MaterialCode} — {line.Description}";
            var picker = new BatchSerialPickerWin(
                label,
                isReceipt: !lineIsOut,
                batchRequiredQty: needBatch ? batchQty : 0m,
                availableBatches: available,
                suggestion: suggestion,
                serialRequiredCount: needSerial ? (int)serialQty : 0,
                inStockSerials: inStockSerials,
                canOverrideExpired: canOverride)
            {
                Owner = owner
            };
            if (picker.ShowDialog() != true)
                return null;

            result[line.LineNo] = new LineBatchSerialCapture(
                picker.ResultBatches, picker.ResultSerials, picker.ResultAllowExpired);
        }

        return result;
    }

    private static string T(string key) => AppServiceHost.Localization.GetString(key) ?? key;

    private readonly record struct Flags(
        bool Stockable, bool Tracked, bool HasBatch, bool HasSerial);

    private static Flags? LoadFlags(DbConnection connection, DbTransaction tx, long materialId)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = DatabaseRuntime.IsPostgreSql()
            ? "SELECT is_stocked, is_service, has_batch, has_lot, has_serial FROM materials WHERE material_id = $m AND is_deleted = FALSE LIMIT 1;"
            : "SELECT is_stocked, is_service, has_batch, has_lot, has_serial FROM materials WHERE material_id = $m AND is_deleted = 0 LIMIT 1;";
        Add(cmd, "$m", materialId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        bool Flag(int i) => !reader.IsDBNull(i)
            && Convert.ToBoolean(reader.GetValue(i), CultureInfo.InvariantCulture);
        var stocked = Flag(0) && !Flag(1);
        var hasBatch = Flag(2) || Flag(3);
        var hasSerial = Flag(4);
        return new Flags(stocked, hasBatch || hasSerial, hasBatch, hasSerial);
    }

    private static long? ResolveMaterialId(DbConnection connection, DbTransaction tx, string company, string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = DatabaseRuntime.IsPostgreSql()
            ? "SELECT material_id FROM materials WHERE company_id = $c AND code = $code AND is_deleted = FALSE LIMIT 1;"
            : "SELECT material_id FROM materials WHERE company_id = $c AND code = $code AND is_deleted = 0 LIMIT 1;";
        Add(cmd, "$c", company);
        Add(cmd, "$code", code.Trim());
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static int? DefaultWarehouse(DbConnection connection, DbTransaction tx, string company)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = DatabaseRuntime.IsPostgreSql()
            ? "SELECT warehouse_id FROM warehouses WHERE company_id = $c AND is_deleted = FALSE AND is_active = TRUE ORDER BY is_default DESC, warehouse_id LIMIT 1;"
            : "SELECT warehouse_id FROM warehouses WHERE company_id = $c AND is_deleted = 0 AND is_active = 1 ORDER BY is_default DESC, warehouse_id LIMIT 1;";
        Add(cmd, "$c", company);
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static decimal CurrentQty(DbConnection connection, DbTransaction tx, string company, int? warehouseId, long materialId)
    {
        if (warehouseId is null) return 0m;
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = DatabaseRuntime.IsPostgreSql()
            ? "SELECT available_qty FROM material_stock_balances WHERE company_id = $c AND warehouse_id = $w AND material_id = $m AND is_deleted = FALSE LIMIT 1;"
            : "SELECT available_qty FROM material_stock_balances WHERE company_id = $c AND warehouse_id = $w AND material_id = $m AND is_deleted = 0 LIMIT 1;";
        Add(cmd, "$c", company);
        Add(cmd, "$w", warehouseId.Value);
        Add(cmd, "$m", materialId);
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? 0m : Convert.ToDecimal(value, CultureInfo.InvariantCulture);
    }

    private static List<string> InStockSerials(
        DbConnection connection, DbTransaction tx, string company, long warehouseId, long materialId)
    {
        var serials = new List<string>();
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = DatabaseRuntime.IsPostgreSql()
            ? "SELECT serial_no FROM stock_serials WHERE company_id = $c AND material_id = $m AND status = 'InStock' AND current_warehouse_id = $w AND is_deleted = FALSE ORDER BY serial_id;"
            : "SELECT serial_no FROM stock_serials WHERE company_id = $c AND material_id = $m AND status = 'InStock' AND current_warehouse_id = $w AND is_deleted = 0 ORDER BY serial_id;";
        Add(cmd, "$c", company);
        Add(cmd, "$m", materialId);
        Add(cmd, "$w", warehouseId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            serials.Add(Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture) ?? string.Empty);
        return serials;
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
