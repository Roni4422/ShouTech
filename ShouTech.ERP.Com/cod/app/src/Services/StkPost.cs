using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.App.Reporting.Invoices;
using ShouTech.App.Views;

namespace ShouTech.App.Services;

/// <summary>
/// Posts inventory effects for commercial documents.
/// Sales: qty out; Sales return: qty in; Purchase: qty in; Purchase return: qty out.
/// Updates the material stock ledger and appends movement audit rows.
/// </summary>
public static class StkPost
{
    public sealed record StockTransferLine(
        long MaterialId,
        string MaterialCode,
        decimal Quantity);

    public sealed record StockCountLine(
        long MaterialId,
        string MaterialCode,
        decimal CountedQty,
        decimal UnitCost);

    public sealed record MaterialHistoryMoveResult(int DocumentLines, int Movements)
    {
        public int Total => DocumentLines + Movements;
    }

    public sealed class MaterialHistoryMoveException : InvalidOperationException
    {
        public string ErrorCode { get; }

        public MaterialHistoryMoveException(string errorCode, string message, Exception? innerException = null)
            : base(message, innerException)
        {
            ErrorCode = errorCode;
        }
    }

    public static void ApplyDocument(DbConnection connection, DbTransaction tx, InvDoc doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (doc.Lines.Count == 0) return;

        if (doc.Kind == InvDocKind.Quotation) return;

        var company = AppState.CurrentCompanyCode ?? string.Empty;
        var isOut = doc.Kind is InvDocKind.SalesInvoice or InvDocKind.PosSale or InvDocKind.PurchaseReturn or InvDocKind.StockOut;
        var moveType = doc.Kind switch
        {
            InvDocKind.SalesInvoice => "SAL",
            InvDocKind.SalesReturn => "SRT",
            InvDocKind.PosSale => "POS",
            InvDocKind.PurchaseInvoice => "PUR",
            InvDocKind.PurchaseReturn => "PRT",
            InvDocKind.StockIn => "ENT",
            InvDocKind.StockOut => "EXT",
            InvDocKind.StockTransfer => "TRN",
            InvDocKind.OpeningStock => "OPN",
            InvDocKind.InventoryCount => "CNT",
            _ => "ADJ"
        };

        foreach (var line in doc.Lines)
        {
            if (line.Qty == 0) continue;
            var productId = line.MaterialId;
            if (productId is null or 0)
            {
                productId = ResolveMaterialId(connection, tx, company, line.MaterialCode);
                if (productId is null)
                    throw new InvalidOperationException(
                        $"Material '{line.MaterialCode}' does not exist in the current company.");
            }

            var warehouseId = line.WarehouseId ?? doc.WarehouseId ?? EnsureDefaultWarehouse(connection, tx, company);
            EnsureWarehouseIsActive(connection, tx, company, warehouseId);
            if (!TryGetStockableMaterial(
                    connection, tx, company, productId.Value, line.MaterialCode, out var allowNegative))
                continue;
            var qtyIn = isOut ? 0m : line.Qty;
            var qtyOut = isOut ? line.Qty : 0m;
            var unitCost = line.UnitPrice;

            // Movement row
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO material_stock_movements
                    (company_id, warehouse_id, material_id, movement_date, movement_type, document_no,
                     qty_in, qty_out, unit_cost, note)
                    VALUES
                    ($c, $w, $p, $d, $t, $doc, $qin, $qout, $cost, $note);
                    """;
                Add(cmd, "$c", company);
                Add(cmd, "$w", warehouseId);
                Add(cmd, "$p", productId.Value);
                Add(cmd, "$d", DatabaseRuntime.IsPostgreSql()
                    ? DateTime.SpecifyKind(doc.DocDate.Date, DateTimeKind.Utc)
                    : doc.DocDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                Add(cmd, "$t", moveType);
                Add(cmd, "$doc", doc.DocNo);
                Add(cmd, "$qin", qtyIn);
                Add(cmd, "$qout", qtyOut);
                Add(cmd, "$cost", unitCost);
                Add(cmd, "$note", line.Description);
                cmd.ExecuteNonQuery();
            }

            // Upsert balance
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    SELECT stock_balance_id, available_qty, average_cost
                    FROM material_stock_balances
                    WHERE company_id=$c AND warehouse_id=$w AND material_id=$p
                      AND is_deleted=0
                    LIMIT 1;
                    """;
                if (DatabaseRuntime.IsPostgreSql())
                {
                    cmd.CommandText = """
                        SELECT stock_balance_id, available_qty, average_cost
                        FROM material_stock_balances
                        WHERE company_id=$c AND warehouse_id=$w AND material_id=$p
                          AND is_deleted=FALSE
                        LIMIT 1
                        FOR UPDATE;
                        """;
                }
                Add(cmd, "$c", company);
                Add(cmd, "$w", warehouseId);
                Add(cmd, "$p", productId.Value);
                long? balId = null;
                decimal avail = 0, avg = 0;
                using (var reader = cmd.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        balId = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
                        avail = Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture);
                        avg = reader.IsDBNull(2) ? 0 : Convert.ToDecimal(reader.GetValue(2), CultureInfo.InvariantCulture);
                    }
                }

                var newQty = avail + qtyIn - qtyOut;
                if (newQty < 0 && !allowNegative)
                    throw new InvalidOperationException(
                        $"Insufficient stock for material '{line.MaterialCode}'.");

                decimal newAvg = avg;
                if (qtyIn > 0)
                {
                    var valuedQty = Math.Max(avail, 0);
                    var totalVal = (valuedQty * avg) + (qtyIn * unitCost);
                    var totalQty = valuedQty + qtyIn;
                    newAvg = totalQty == 0 ? unitCost : decimal.Round(totalVal / totalQty, 6, MidpointRounding.AwayFromZero);
                }

                if (balId is null)
                {
                    using var ins = connection.CreateCommand();
                    ins.Transaction = tx;
                    ins.CommandText = """
                        INSERT INTO material_stock_balances
                        (company_id, warehouse_id, material_id, available_qty, reserved_qty,
                         average_cost, last_cost, last_updated, is_deleted)
                        VALUES
                        ($c, $w, $p, $qty, 0, $avg, $cost, datetime('now'), 0);
                        """;
                    if (DatabaseRuntime.IsPostgreSql())
                    {
                        ins.CommandText = """
                            INSERT INTO material_stock_balances
                            (company_id, warehouse_id, material_id, available_qty, reserved_qty,
                             average_cost, last_cost, last_updated, is_deleted)
                            VALUES
                            ($c, $w, $p, $qty, 0, $avg, $cost, NOW(), FALSE);
                            """;
                    }
                    Add(ins, "$c", company);
                    Add(ins, "$w", warehouseId);
                    Add(ins, "$p", productId.Value);
                    Add(ins, "$qty", newQty);
                    Add(ins, "$avg", newAvg);
                    Add(ins, "$cost", unitCost);
                    ins.ExecuteNonQuery();
                }
                else
                {
                    using var upd = connection.CreateCommand();
                    upd.Transaction = tx;
                    upd.CommandText = """
                        UPDATE material_stock_balances
                        SET available_qty=$qty, average_cost=$avg, last_cost=$cost, last_updated=datetime('now')
                        WHERE stock_balance_id=$id;
                        """;
                    if (DatabaseRuntime.IsPostgreSql())
                    {
                        upd.CommandText = """
                            UPDATE material_stock_balances
                            SET available_qty=$qty, average_cost=$avg, last_cost=$cost, last_updated=NOW()
                            WHERE stock_balance_id=$id;
                            """;
                    }
                    Add(upd, "$qty", newQty);
                    Add(upd, "$avg", newAvg);
                    Add(upd, "$cost", unitCost);
                    Add(upd, "$id", balId.Value);
                    upd.ExecuteNonQuery();
                }
            }
        }
    }

    public static int PostCount(
        long warehouseId,
        DateTime countDate,
        string reason,
        IReadOnlyCollection<StockCountLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(warehouseId);
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("A stock count reason is required.", nameof(reason));
        if (lines.Count == 0)
            throw new ArgumentException("There are no stock count lines to post.", nameof(lines));

        var company = AppState.CurrentCompanyCode?.Trim();
        if (string.IsNullOrWhiteSpace(company))
            throw new InvalidOperationException("No current company is selected.");

        var materialIds = new HashSet<long>();
        foreach (var line in lines)
        {
            if (line.MaterialId <= 0 || string.IsNullOrWhiteSpace(line.MaterialCode))
                throw new ArgumentException("Every stock count line must identify a material.", nameof(lines));
            if (line.CountedQty < 0)
                throw new ArgumentOutOfRangeException(nameof(lines), "Counted quantity cannot be negative.");
            if (line.UnitCost < 0)
                throw new ArgumentOutOfRangeException(nameof(lines), "Unit cost cannot be negative.");
            if (decimal.Round(line.UnitCost, 6) != line.UnitCost)
                throw new ArgumentOutOfRangeException(nameof(lines), "Unit cost supports up to six decimal places.");
            if (decimal.Round(line.CountedQty, 4) != line.CountedQty)
                throw new ArgumentOutOfRangeException(nameof(lines), "Counted quantity supports up to four decimal places.");
            if (!materialIds.Add(line.MaterialId))
                throw new ArgumentException($"Material '{line.MaterialCode}' appears more than once.", nameof(lines));
        }

        MatRepo.EnsureSchema();
        var connectionString = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("No connection to the current company database.");

        using DbConnection connection = DatabaseRuntime.IsPostgreSql()
            ? new NpgsqlConnection(connectionString)
            : new SqliteConnection(DatabaseRuntime.NormalizeSqliteConnectionString(connectionString));
        connection.Open();
        using DbTransaction tx = DatabaseRuntime.IsPostgreSql()
            ? connection.BeginTransaction(System.Data.IsolationLevel.Serializable)
            : connection.BeginTransaction();

        EnsureWarehouseIsActive(connection, tx, company, warehouseId);
        var documentNo = $"CNT-{countDate:yyyyMMdd}-{Guid.NewGuid():N}";
        var date = countDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var posted = 0;

        foreach (var line in lines)
        {
            EnsureMaterialExists(connection, tx, company, line.MaterialId, line.MaterialCode);

            long? balanceId;
            decimal currentQty;
            decimal currentCost;
            using (var read = connection.CreateCommand())
            {
                read.Transaction = tx;
                read.CommandText = DatabaseRuntime.IsPostgreSql()
                    ? """
                      SELECT stock_balance_id, available_qty, average_cost
                      FROM material_stock_balances
                      WHERE company_id=$c AND warehouse_id=$w AND material_id=$m AND is_deleted=FALSE
                      LIMIT 1
                      FOR UPDATE;
                      """
                    : """
                      SELECT stock_balance_id, available_qty, average_cost
                      FROM material_stock_balances
                      WHERE company_id=$c AND warehouse_id=$w AND material_id=$m AND is_deleted=0
                      LIMIT 1;
                      """;
                Add(read, "$c", company);
                Add(read, "$w", warehouseId);
                Add(read, "$m", line.MaterialId);
                using var reader = read.ExecuteReader();
                if (reader.Read())
                {
                    balanceId = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
                    currentQty = Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture);
                    currentCost = reader.IsDBNull(2)
                        ? line.UnitCost
                        : Convert.ToDecimal(reader.GetValue(2), CultureInfo.InvariantCulture);
                }
                else
                {
                    balanceId = null;
                    currentQty = 0;
                    currentCost = line.UnitCost;
                }
            }

            var delta = line.CountedQty - currentQty;
            var qtyIn = Math.Max(delta, 0);
            var qtyOut = Math.Max(-delta, 0);
            var averageCost = line.CountedQty == 0
                ? 0
                : delta > 0
                    ? currentQty <= 0
                        ? line.UnitCost
                        : decimal.Round(
                            ((currentQty * currentCost) + (delta * line.UnitCost)) / line.CountedQty,
                            6,
                            MidpointRounding.AwayFromZero)
                    : currentCost;

            using (var movement = connection.CreateCommand())
            {
                movement.Transaction = tx;
                movement.CommandText = """
                    INSERT INTO material_stock_movements
                    (company_id, warehouse_id, material_id, movement_date, movement_type, document_no,
                     qty_in, qty_out, unit_cost, note)
                    VALUES ($c, $w, $m, $d, 'COUNT_CLOSE', $doc, $qin, $qout, $cost, $note);
                    """;
                Add(movement, "$c", company);
                Add(movement, "$w", warehouseId);
                Add(movement, "$m", line.MaterialId);
                Add(movement, "$d", DatabaseRuntime.IsPostgreSql()
                    ? DateTime.SpecifyKind(countDate.Date, DateTimeKind.Utc)
                    : date);
                Add(movement, "$doc", documentNo);
                Add(movement, "$qin", qtyIn);
                Add(movement, "$qout", qtyOut);
                Add(movement, "$cost", line.UnitCost);
                Add(movement, "$note", reason.Trim());
                movement.ExecuteNonQuery();
            }

            if (delta != 0 && balanceId is long id)
            {
                using var update = connection.CreateCommand();
                update.Transaction = tx;
                update.CommandText = DatabaseRuntime.IsPostgreSql()
                    ? """
                      UPDATE material_stock_balances
                      SET available_qty=$qty, average_cost=$avg, last_cost=$cost, last_updated=NOW()
                      WHERE stock_balance_id=$id;
                      """
                    : """
                      UPDATE material_stock_balances
                      SET available_qty=$qty, average_cost=$avg, last_cost=$cost, last_updated=datetime('now')
                      WHERE stock_balance_id=$id;
                      """;
                Add(update, "$qty", line.CountedQty);
                Add(update, "$avg", averageCost);
                Add(update, "$cost", line.UnitCost);
                Add(update, "$id", id);
                if (update.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException($"Stock balance for '{line.MaterialCode}' changed while posting.");
            }
            else if (delta != 0)
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = tx;
                insert.CommandText = DatabaseRuntime.IsPostgreSql()
                    ? """
                      INSERT INTO material_stock_balances
                      (company_id, warehouse_id, material_id, available_qty, average_cost, last_cost, last_updated)
                      VALUES ($c, $w, $m, $qty, $avg, $cost, NOW());
                      """
                    : """
                      INSERT INTO material_stock_balances
                      (company_id, warehouse_id, material_id, available_qty, average_cost, last_cost, last_updated)
                      VALUES ($c, $w, $m, $qty, $avg, $cost, datetime('now'));
                      """;
                Add(insert, "$c", company);
                Add(insert, "$w", warehouseId);
                Add(insert, "$m", line.MaterialId);
                Add(insert, "$qty", line.CountedQty);
                Add(insert, "$avg", averageCost);
                Add(insert, "$cost", line.UnitCost);
                insert.ExecuteNonQuery();
            }

            if (delta != 0)
                posted++;
        }

        tx.Commit();
        return posted;
    }

    public static MaterialHistoryMoveResult MoveMaterialHistory(long sourceMaterialId, long targetMaterialId)
    {
        if (sourceMaterialId <= 0 || targetMaterialId <= 0 || sourceMaterialId == targetMaterialId)
            throw new MaterialHistoryMoveException("INV-MRG-001", "Choose two different valid materials.");

        var company = AppState.CurrentCompanyCode?.Trim();
        if (string.IsNullOrWhiteSpace(company))
            throw new MaterialHistoryMoveException("INV-MRG-002", "No current company is selected.");

        MatRepo.EnsureSchema();
        DocSchema.Ensure();
        var connectionString = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new MaterialHistoryMoveException("INV-MRG-002", "No connection to the current company database.");

        try
        {
            using DbConnection connection = DatabaseRuntime.IsPostgreSql()
                ? new NpgsqlConnection(connectionString)
                : new SqliteConnection(DatabaseRuntime.NormalizeSqliteConnectionString(connectionString));
            connection.Open();
            using DbTransaction tx = DatabaseRuntime.IsPostgreSql()
                ? connection.BeginTransaction(System.Data.IsolationLevel.Serializable)
                : connection.BeginTransaction();

            var materials = new Dictionary<long, MaterialIdentity>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = tx;
                command.CommandText = DatabaseRuntime.IsPostgreSql()
                    ? """
                      SELECT material_id, code, name_ar, name_en, name_print_ar, name_print_en,
                             COALESCE(NULLIF(TRIM(default_unit), ''),
                                      NULLIF(TRIM(sales_unit), ''),
                                      NULLIF(TRIM(purchase_unit), ''), '')
                      FROM materials
                      WHERE company_id=$c AND is_deleted=FALSE AND material_id IN ($source, $target)
                      ORDER BY material_id
                      FOR UPDATE;
                      """
                    : """
                      SELECT material_id, code, name_ar, name_en, name_print_ar, name_print_en,
                             COALESCE(NULLIF(TRIM(default_unit), ''),
                                      NULLIF(TRIM(sales_unit), ''),
                                      NULLIF(TRIM(purchase_unit), ''), '')
                      FROM materials
                      WHERE company_id=$c AND is_deleted=0 AND material_id IN ($source, $target)
                      ORDER BY material_id;
                      """;
                Add(command, "$c", company);
                Add(command, "$source", sourceMaterialId);
                Add(command, "$target", targetMaterialId);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var material = new MaterialIdentity(
                        Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) ?? "",
                        Convert.ToString(reader.GetValue(2), CultureInfo.InvariantCulture) ?? "",
                        Convert.ToString(reader.GetValue(3), CultureInfo.InvariantCulture) ?? "",
                        Convert.ToString(reader.GetValue(4), CultureInfo.InvariantCulture) ?? "",
                        Convert.ToString(reader.GetValue(5), CultureInfo.InvariantCulture) ?? "",
                        Convert.ToString(reader.GetValue(6), CultureInfo.InvariantCulture) ?? "");
                    materials[Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture)] = material;
                }
            }

            if (!materials.TryGetValue(sourceMaterialId, out var source) ||
                !materials.TryGetValue(targetMaterialId, out var target))
                throw new MaterialHistoryMoveException("INV-MRG-001", "Both materials must be active in the current company.");
            if (string.IsNullOrWhiteSpace(source.Unit) ||
                !string.Equals(source.Unit.Trim(), target.Unit.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new MaterialHistoryMoveException("INV-MRG-003", "The materials must use the same unit.");

            var documentLines = 0;
            foreach (var lineTable in new[] { "sales_document_lines", "purchase_document_lines" })
            {
                using var command = connection.CreateCommand();
                command.Transaction = tx;
                command.CommandText = $"""
                    UPDATE {lineTable}
                    SET material_id=$target, material_code=$targetCode,
                        description=CASE
                            WHEN description=$sourceNameAr THEN $targetNameAr
                            WHEN description=$sourceNameEn THEN $targetNameEn
                            WHEN description=$sourcePrintNameAr THEN $targetPrintNameAr
                            WHEN description=$sourcePrintNameEn THEN $targetPrintNameEn
                            WHEN description=$sourceCode THEN $targetCode
                            ELSE description
                        END
                    WHERE material_id=$source
                      AND doc_id IN (
                          SELECT doc_id FROM {(lineTable == "sales_document_lines" ? "sales_documents" : "purchase_documents")}
                          WHERE company_id=$c);
                    """;
                Add(command, "$target", targetMaterialId);
                Add(command, "$targetCode", target.Code);
                Add(command, "$targetNameAr", target.Name);
                Add(command, "$targetNameEn", target.NameEn);
                Add(command, "$targetPrintNameAr", target.PrintNameAr);
                Add(command, "$targetPrintNameEn", target.PrintNameEn);
                Add(command, "$sourceNameAr", source.Name);
                Add(command, "$sourceNameEn", source.NameEn);
                Add(command, "$sourcePrintNameAr", source.PrintNameAr);
                Add(command, "$sourcePrintNameEn", source.PrintNameEn);
                Add(command, "$sourceCode", source.Code);
                Add(command, "$source", sourceMaterialId);
                Add(command, "$c", company);
                documentLines += command.ExecuteNonQuery();
            }

            int movements;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = tx;
                command.CommandText = """
                    UPDATE material_stock_movements
                    SET material_id=$target
                    WHERE company_id=$c AND material_id=$source;
                    """;
                Add(command, "$target", targetMaterialId);
                Add(command, "$source", sourceMaterialId);
                Add(command, "$c", company);
                movements = command.ExecuteNonQuery();
            }

            if (documentLines == 0 && movements == 0)
                throw new MaterialHistoryMoveException("INV-MRG-004", "No material history was found to move.");

            MoveMaterialBalances(connection, tx, company, sourceMaterialId, targetMaterialId);
            tx.Commit();
            return new MaterialHistoryMoveResult(documentLines, movements);
        }
        catch (MaterialHistoryMoveException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new MaterialHistoryMoveException(
                "INV-MRG-005",
                "The material history move failed and was rolled back.",
                ex);
        }
    }

    private static void MoveMaterialBalances(
        DbConnection connection,
        DbTransaction tx,
        string company,
        long sourceMaterialId,
        long targetMaterialId)
    {
        var balances = new Dictionary<(long WarehouseId, long MaterialId), MaterialBalance>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = tx;
            command.CommandText = DatabaseRuntime.IsPostgreSql()
                ? """
                  SELECT stock_balance_id, warehouse_id, material_id, available_qty, reserved_qty,
                         average_cost, last_cost, is_deleted
                  FROM material_stock_balances
                  WHERE company_id=$c AND material_id IN ($source, $target)
                  FOR UPDATE;
                  """
                : """
                  SELECT stock_balance_id, warehouse_id, material_id, available_qty, reserved_qty,
                         average_cost, last_cost, is_deleted
                  FROM material_stock_balances
                  WHERE company_id=$c AND material_id IN ($source, $target);
                  """;
            Add(command, "$c", company);
            Add(command, "$source", sourceMaterialId);
            Add(command, "$target", targetMaterialId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var balance = new MaterialBalance
                {
                    Id = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                    WarehouseId = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                    MaterialId = Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture),
                    Available = Convert.ToDecimal(reader.GetValue(3), CultureInfo.InvariantCulture),
                    Reserved = Convert.ToDecimal(reader.GetValue(4), CultureInfo.InvariantCulture),
                    AverageCost = Convert.ToDecimal(reader.GetValue(5), CultureInfo.InvariantCulture),
                    LastCost = Convert.ToDecimal(reader.GetValue(6), CultureInfo.InvariantCulture),
                    IsDeleted = Convert.ToBoolean(reader.GetValue(7), CultureInfo.InvariantCulture)
                };
                balances[(balance.WarehouseId, balance.MaterialId)] = balance;
            }
        }

        foreach (var sourceBalance in balances.Values
                     .Where(x => x.MaterialId == sourceMaterialId && !x.IsDeleted))
        {
            balances.TryGetValue((sourceBalance.WarehouseId, targetMaterialId), out var targetBalance);
            var targetActive = targetBalance is not null && !targetBalance.IsDeleted;
            var available = sourceBalance.Available + (targetActive ? targetBalance!.Available : 0m);
            var reserved = sourceBalance.Reserved + (targetActive ? targetBalance!.Reserved : 0m);
            var value = sourceBalance.Available * sourceBalance.AverageCost +
                        (targetActive ? targetBalance!.Available * targetBalance.AverageCost : 0m);
            var averageCost = available == 0
                ? (targetActive ? targetBalance!.AverageCost : sourceBalance.AverageCost)
                : decimal.Round(value / available, 6, MidpointRounding.AwayFromZero);
            var lastCost = targetActive ? targetBalance!.LastCost : sourceBalance.LastCost;
            var now = DatabaseRuntime.IsPostgreSql()
                ? "NOW()"
                : "datetime('now')";

            if (targetBalance is null)
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = tx;
                insert.CommandText = $"""
                    INSERT INTO material_stock_balances
                    (company_id, warehouse_id, material_id, available_qty, reserved_qty,
                     average_cost, last_cost, last_updated, is_deleted)
                    VALUES ($c, $w, $target, $available, $reserved, $average, $last, {now},
                            {(DatabaseRuntime.IsPostgreSql() ? "FALSE" : "0")});
                    """;
                Add(insert, "$c", company);
                Add(insert, "$w", sourceBalance.WarehouseId);
                Add(insert, "$target", targetMaterialId);
                Add(insert, "$available", available);
                Add(insert, "$reserved", reserved);
                Add(insert, "$average", averageCost);
                Add(insert, "$last", lastCost);
                insert.ExecuteNonQuery();
            }
            else
            {
                using var updateTarget = connection.CreateCommand();
                updateTarget.Transaction = tx;
                updateTarget.CommandText = $"""
                    UPDATE material_stock_balances
                    SET available_qty=$available, reserved_qty=$reserved,
                        average_cost=$average, last_cost=$last, last_updated={now},
                        is_deleted={(DatabaseRuntime.IsPostgreSql() ? "FALSE" : "0")}
                    WHERE stock_balance_id=$id;
                    """;
                Add(updateTarget, "$available", available);
                Add(updateTarget, "$reserved", reserved);
                Add(updateTarget, "$average", averageCost);
                Add(updateTarget, "$last", lastCost);
                Add(updateTarget, "$id", targetBalance.Id);
                updateTarget.ExecuteNonQuery();
            }

            using var clearSource = connection.CreateCommand();
            clearSource.Transaction = tx;
            clearSource.CommandText = $"""
                UPDATE material_stock_balances
                SET available_qty=0, reserved_qty=0, last_updated={now},
                    is_deleted={(DatabaseRuntime.IsPostgreSql() ? "TRUE" : "1")}
                WHERE stock_balance_id=$id;
                """;
            Add(clearSource, "$id", sourceBalance.Id);
            clearSource.ExecuteNonQuery();
        }
    }

    private sealed class MaterialBalance
    {
        public long Id { get; init; }
        public long WarehouseId { get; init; }
        public long MaterialId { get; init; }
        public decimal Available { get; init; }
        public decimal Reserved { get; init; }
        public decimal AverageCost { get; init; }
        public decimal LastCost { get; init; }
        public bool IsDeleted { get; init; }
    }

    private sealed record MaterialIdentity(
        string Code,
        string Name,
        string NameEn,
        string PrintNameAr,
        string PrintNameEn,
        string Unit);

    public static string PostTransfer(
        long sourceWarehouseId,
        long targetWarehouseId,
        DateTime transferDate,
        string reason,
        IReadOnlyCollection<StockTransferLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceWarehouseId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetWarehouseId);
        if (sourceWarehouseId == targetWarehouseId)
            throw new StockTransferException("INV-TRF-001", "Source and destination warehouses must be different.");
        if (string.IsNullOrWhiteSpace(reason))
            throw new StockTransferException("INV-TRF-001", "A transfer reason is required.");
        if (lines.Count == 0)
            throw new StockTransferException("INV-TRF-001", "Add at least one material quantity to transfer.");

        var company = AppState.CurrentCompanyCode?.Trim();
        if (string.IsNullOrWhiteSpace(company))
            throw new StockTransferException("INV-TRF-002", "No current company is selected.");

        var materialIds = new HashSet<long>();
        foreach (var line in lines)
        {
            if (line.MaterialId <= 0 || string.IsNullOrWhiteSpace(line.MaterialCode) ||
                line.Quantity <= 0 || decimal.Round(line.Quantity, 4) != line.Quantity)
                throw new StockTransferException(
                    "INV-TRF-001",
                    $"Enter a valid positive quantity (up to four decimals) for '{line.MaterialCode}'.");
            if (!materialIds.Add(line.MaterialId))
                throw new StockTransferException(
                    "INV-TRF-001",
                    $"Material '{line.MaterialCode}' appears more than once.");
        }

        MatRepo.EnsureSchema();
        var connectionString = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new StockTransferException("INV-TRF-002", "No connection to the current company database.");

        try
        {
            using DbConnection connection = DatabaseRuntime.IsPostgreSql()
                ? new NpgsqlConnection(connectionString)
                : new SqliteConnection(DatabaseRuntime.NormalizeSqliteConnectionString(connectionString));
            connection.Open();
            using DbTransaction tx = DatabaseRuntime.IsPostgreSql()
                ? connection.BeginTransaction(System.Data.IsolationLevel.Serializable)
                : connection.BeginTransaction();

            EnsureWarehouseIsActive(connection, tx, company, sourceWarehouseId);
            EnsureWarehouseIsActive(connection, tx, company, targetWarehouseId);

            var documentNo = string.Create(
                CultureInfo.InvariantCulture,
                $"TRF-{transferDate:yyyyMMdd}-{Guid.NewGuid():N}");
            object movementDate = DatabaseRuntime.IsPostgreSql()
                ? DateTime.SpecifyKind(transferDate.Date, DateTimeKind.Utc)
                : transferDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            foreach (var line in lines)
            {
                _ = EnsureMaterialExists(
                    connection, tx, company, line.MaterialId, line.MaterialCode);

                var source = ReadBalance(
                    connection, tx, company, sourceWarehouseId, line.MaterialId);
                if (source is null || source.Value.Quantity < line.Quantity)
                    throw new StockTransferException(
                        "INV-TRF-004",
                        $"Insufficient stock for material '{line.MaterialCode}'.");

                var target = ReadBalance(
                    connection, tx, company, targetWarehouseId, line.MaterialId);
                var sourceCost = source.Value.AverageCost > 0
                    ? source.Value.AverageCost
                    : source.Value.LastCost;
                var targetQty = target?.Quantity ?? 0m;
                var targetCost = target?.AverageCost ?? sourceCost;
                var newTargetQty = targetQty + line.Quantity;
                var newTargetCost = targetQty <= 0
                    ? sourceCost
                    : decimal.Round(
                        ((targetQty * targetCost) + (line.Quantity * sourceCost)) / newTargetQty,
                        6,
                        MidpointRounding.AwayFromZero);

                using (var updateSource = connection.CreateCommand())
                {
                    updateSource.Transaction = tx;
                    updateSource.CommandText = DatabaseRuntime.IsPostgreSql()
                        ? """
                          UPDATE material_stock_balances
                          SET available_qty=available_qty-$qty, last_updated=NOW()
                          WHERE stock_balance_id=$id AND available_qty >= $qty AND is_deleted=FALSE;
                          """
                        : """
                          UPDATE material_stock_balances
                          SET available_qty=available_qty-$qty, last_updated=datetime('now')
                          WHERE stock_balance_id=$id AND available_qty >= $qty AND is_deleted=0;
                          """;
                    Add(updateSource, "$qty", line.Quantity);
                    Add(updateSource, "$id", source.Value.BalanceId);
                    if (updateSource.ExecuteNonQuery() != 1)
                        throw new StockTransferException(
                            "INV-TRF-004",
                            $"Available stock for material '{line.MaterialCode}' changed. Refresh and retry.");
                }

                if (target is null)
                {
                    using var insertTarget = connection.CreateCommand();
                    insertTarget.Transaction = tx;
                    insertTarget.CommandText = DatabaseRuntime.IsPostgreSql()
                        ? """
                          INSERT INTO material_stock_balances
                          (company_id, warehouse_id, material_id, available_qty, average_cost, last_cost, last_updated)
                          VALUES ($c, $w, $m, $qty, $cost, $cost, NOW());
                          """
                        : """
                          INSERT INTO material_stock_balances
                          (company_id, warehouse_id, material_id, available_qty, average_cost, last_cost, last_updated)
                          VALUES ($c, $w, $m, $qty, $cost, $cost, datetime('now'));
                          """;
                    Add(insertTarget, "$c", company);
                    Add(insertTarget, "$w", targetWarehouseId);
                    Add(insertTarget, "$m", line.MaterialId);
                    Add(insertTarget, "$qty", line.Quantity);
                    Add(insertTarget, "$cost", sourceCost);
                    insertTarget.ExecuteNonQuery();
                }
                else
                {
                    using var updateTarget = connection.CreateCommand();
                    updateTarget.Transaction = tx;
                    updateTarget.CommandText = DatabaseRuntime.IsPostgreSql()
                        ? """
                          UPDATE material_stock_balances
                          SET available_qty=$qty, average_cost=$avg, last_cost=$cost, last_updated=NOW()
                          WHERE stock_balance_id=$id;
                          """
                        : """
                          UPDATE material_stock_balances
                          SET available_qty=$qty, average_cost=$avg, last_cost=$cost, last_updated=datetime('now')
                          WHERE stock_balance_id=$id;
                          """;
                    Add(updateTarget, "$qty", newTargetQty);
                    Add(updateTarget, "$avg", newTargetCost);
                    Add(updateTarget, "$cost", sourceCost);
                    Add(updateTarget, "$id", target.Value.BalanceId);
                    if (updateTarget.ExecuteNonQuery() != 1)
                        throw new StockTransferException(
                            "INV-TRF-005",
                            $"Destination balance for '{line.MaterialCode}' changed while posting.");
                }

                InsertTransferMovement(
                    connection, tx, company, sourceWarehouseId, line.MaterialId,
                    movementDate, "TRANSFER_OUT", documentNo, line.Quantity,
                    sourceCost, $"{reason.Trim()} → {targetWarehouseId.ToString(CultureInfo.InvariantCulture)}");
                InsertTransferMovement(
                    connection, tx, company, targetWarehouseId, line.MaterialId,
                    movementDate, "TRANSFER_IN", documentNo, line.Quantity,
                    sourceCost, $"{reason.Trim()} ← {sourceWarehouseId.ToString(CultureInfo.InvariantCulture)}");
            }

            tx.Commit();
            return documentNo;
        }
        catch (StockTransferException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new StockTransferException(
                "INV-TRF-005",
                "The material transfer failed and was rolled back.",
                ex);
        }
    }

    private static (long BalanceId, decimal Quantity, decimal AverageCost, decimal LastCost)? ReadBalance(
        DbConnection connection,
        DbTransaction tx,
        string company,
        long warehouseId,
        long materialId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = DatabaseRuntime.IsPostgreSql()
            ? """
              SELECT stock_balance_id, available_qty, average_cost, last_cost
              FROM material_stock_balances
              WHERE company_id=$c AND warehouse_id=$w AND material_id=$m AND is_deleted=FALSE
              LIMIT 1 FOR UPDATE;
              """
            : """
              SELECT stock_balance_id, available_qty, average_cost, last_cost
              FROM material_stock_balances
              WHERE company_id=$c AND warehouse_id=$w AND material_id=$m AND is_deleted=0
              LIMIT 1;
              """;
        Add(command, "$c", company);
        Add(command, "$w", warehouseId);
        Add(command, "$m", materialId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        return (
            Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
            Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture),
            reader.IsDBNull(2) ? 0 : Convert.ToDecimal(reader.GetValue(2), CultureInfo.InvariantCulture),
            reader.IsDBNull(3) ? 0 : Convert.ToDecimal(reader.GetValue(3), CultureInfo.InvariantCulture));
    }

    private static void InsertTransferMovement(
        DbConnection connection,
        DbTransaction tx,
        string company,
        long warehouseId,
        long materialId,
        object movementDate,
        string movementType,
        string documentNo,
        decimal quantity,
        decimal unitCost,
        string note)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO material_stock_movements
            (company_id, warehouse_id, material_id, movement_date, movement_type, document_no,
             qty_in, qty_out, unit_cost, note)
            VALUES ($c, $w, $m, $d, $t, $doc, $qin, $qout, $cost, $note);
            """;
        Add(command, "$c", company);
        Add(command, "$w", warehouseId);
        Add(command, "$m", materialId);
        Add(command, "$d", movementDate);
        Add(command, "$t", movementType);
        Add(command, "$doc", documentNo);
        Add(command, "$qin", movementType == "TRANSFER_IN" ? quantity : 0m);
        Add(command, "$qout", movementType == "TRANSFER_OUT" ? quantity : 0m);
        Add(command, "$cost", unitCost);
        Add(command, "$note", note);
        command.ExecuteNonQuery();
    }

    private static void EnsureWarehouseIsActive(
        DbConnection connection,
        DbTransaction tx,
        string company,
        long warehouseId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = DatabaseRuntime.IsPostgreSql()
            ? """
              SELECT COUNT(1) FROM warehouses
              WHERE company_id=$c AND warehouse_id=$w
                AND is_deleted=FALSE AND is_active=TRUE;
              """
            : """
              SELECT COUNT(1) FROM warehouses
              WHERE company_id=$c AND warehouse_id=$w
                AND is_deleted=0 AND is_active=1;
              """;
        Add(command, "$c", company);
        Add(command, "$w", warehouseId);
        if (Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
            throw new InvalidOperationException("The selected warehouse is unavailable or inactive.");
    }

    private static bool EnsureMaterialExists(
        DbConnection connection,
        DbTransaction tx,
        string company,
        long materialId,
        string? materialCode = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        var postgres = DatabaseRuntime.IsPostgreSql();
        var stockable = postgres
            ? "is_deleted=FALSE AND is_stocked=TRUE AND is_service=FALSE"
            : "is_deleted=0 AND is_stocked=1 AND is_service=0";
        var codeFilter = string.IsNullOrWhiteSpace(materialCode) ? "" : "AND code=$code";
        command.CommandText = $"""
            SELECT allow_negative FROM materials
            WHERE company_id=$c AND material_id=$m AND {stockable}
              {codeFilter};
            """;
        Add(command, "$c", company);
        Add(command, "$m", materialId);
        if (!string.IsNullOrWhiteSpace(materialCode))
            Add(command, "$code", materialCode.Trim());
        var value = command.ExecuteScalar();
        if (value is null or DBNull)
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(materialCode)
                    ? $"Material {materialId} is no longer available for stock."
                    : $"Material '{materialCode}' is no longer available for stock.");
        return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
    }

    private static bool TryGetStockableMaterial(
        DbConnection connection,
        DbTransaction tx,
        string company,
        long materialId,
        string? materialCode,
        out bool allowNegative)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        var postgres = DatabaseRuntime.IsPostgreSql();
        var active = postgres ? "is_deleted=FALSE" : "is_deleted=0";
        var codeFilter = string.IsNullOrWhiteSpace(materialCode) ? "" : "AND code=$code";
        command.CommandText = $"""
            SELECT is_stocked, is_service, allow_negative
            FROM materials
            WHERE company_id=$c AND material_id=$m AND {active}
              {codeFilter};
            """;
        Add(command, "$c", company);
        Add(command, "$m", materialId);
        if (!string.IsNullOrWhiteSpace(materialCode))
            Add(command, "$code", materialCode.Trim());

        using var reader = command.ExecuteReader();
        if (!reader.Read())
            throw new InvalidOperationException(
                $"Material '{materialCode ?? materialId.ToString(CultureInfo.InvariantCulture)}' does not exist in the current company.");

        if (!Convert.ToBoolean(reader.GetValue(0), CultureInfo.InvariantCulture) ||
            Convert.ToBoolean(reader.GetValue(1), CultureInfo.InvariantCulture))
        {
            allowNegative = false;
            return false;
        }

        allowNegative = Convert.ToBoolean(reader.GetValue(2), CultureInfo.InvariantCulture);
        return true;
    }

    private static long? ResolveMaterialId(DbConnection connection, DbTransaction tx, string company, string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = DatabaseRuntime.IsPostgreSql()
            ? """
              SELECT material_id FROM materials
              WHERE company_id=$c AND code=$code AND is_deleted=FALSE LIMIT 1;
              """
            : """
              SELECT material_id FROM materials
              WHERE company_id=$c AND code=$code AND is_deleted=0 LIMIT 1;
              """;
        Add(cmd, "$c", company);
        Add(cmd, "$code", code.Trim());
        var v = cmd.ExecuteScalar();
        return v is null or DBNull ? null : Convert.ToInt64(v, CultureInfo.InvariantCulture);
    }

    private static long EnsureDefaultWarehouse(DbConnection connection, DbTransaction tx, string company)
    {
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = DatabaseRuntime.IsPostgreSql()
                ? """
                  SELECT warehouse_id FROM warehouses
                  WHERE company_id=$c AND is_deleted=FALSE
                  ORDER BY warehouse_id LIMIT 1;
                  """
                : """
                  SELECT warehouse_id FROM warehouses
                  WHERE company_id=$c AND is_deleted=0
                  ORDER BY warehouse_id LIMIT 1;
                  """;
            Add(cmd, "$c", company);
            var v = cmd.ExecuteScalar();
            if (v is not null and not DBNull)
                return Convert.ToInt64(v, CultureInfo.InvariantCulture);
        }

        using (var ins = connection.CreateCommand())
        {
            ins.Transaction = tx;
            if (DatabaseRuntime.IsPostgreSql())
            {
                ins.CommandText = """
                    INSERT INTO warehouses (company_id, warehouse_code, warehouse_name_ar, is_active)
                    VALUES ($c, 'MAIN', 'Main Warehouse', TRUE) RETURNING warehouse_id;
                    """;
            }
            else
            {
                ins.CommandText = """
                    INSERT INTO warehouses (company_id, warehouse_code, warehouse_name_ar, is_active)
                    VALUES ($c, 'MAIN', 'Main Warehouse', 1);
                    SELECT last_insert_rowid();
                    """;
            }
            Add(ins, "$c", company);
            return Convert.ToInt64(ins.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        command.Parameters.Add(p);
    }

    public sealed class StockTransferException : InvalidOperationException
    {
        public string ErrorCode { get; }

        public StockTransferException(string errorCode, string message, Exception? innerException = null)
            : base(message, innerException)
        {
            ErrorCode = errorCode;
        }
    }
}
