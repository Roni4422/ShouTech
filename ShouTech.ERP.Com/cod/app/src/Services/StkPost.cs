using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.App.Reporting.Invoices;
using ShouTech.App.Views;
using ShouTech.Domain;
using ShouTech.Domain.Entities;
using ShouTech.Infrastructure;

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
        decimal UnitCost,
        // Batch/serial capture for tracked materials (Phase B 054):
        // a surplus receipt needs them exactly like any receipt; a
        // shortage without them allocates FEFO/FIFO in the engine.
        IReadOnlyList<BatchAllocationRequest>? BatchAllocations = null,
        IReadOnlyList<SerialSelection>? Serials = null);

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

    // Period-close guard (docs/PERIOD_CLOSE_STUDY.md SS3): stock-only
    // postings never resolve a GL period, so they consult the same fiscal
    // calendar here. Refuses with ACC-5021 naming the period when the
    // movement date falls inside a CLOSED/LOCKED period (or a closed
    // fiscal year); silent when no period covers the date, so companies
    // that never defined fiscal years keep posting stock as before.
    private static void EnsurePeriodOpenForStock(DbConnection connection, DbTransaction tx, DateTime date)
    {
        var companyCode = AppState.CurrentCompanyCode?.Trim();
        if (string.IsNullOrWhiteSpace(companyCode))
            return; // callers validate the current company separately
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT company_id FROM companies WHERE company_code = @code";
        var codeParam = cmd.CreateParameter();
        codeParam.ParameterName = "@code";
        codeParam.Value = companyCode;
        cmd.Parameters.Add(codeParam);
        var value = cmd.ExecuteScalar();
        if (value is null || value is DBNull)
            return;
        var companyId = value is Guid parsed
            ? parsed
            : Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!);
        GlPosting.EnsureOpenPeriodForDate(
            connection, tx, ProviderName, companyId, DateOnly.FromDateTime(date));
    }

    public static void ApplyDocument(DbConnection connection, DbTransaction tx, InvDoc doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (doc.Lines.Count == 0) return;

        if (doc.Kind == InvDocKind.Quotation) return;

        // Step 5 kind guard (defense in depth — InvRepo.Post blocks these
        // kinds before this point): a stock transfer applied as a generic
        // document posted every line as qty-IN at the single header
        // warehouse, creating stock from nothing (sweep I-6), and an
        // inventory count added the full counted quantity instead of the
        // delta (I-7). Transfers post through PostTransfer (two-sided)
        // and counts through PostCount (delta-based); this generic
        // document path must never move stock for them.
        if (doc.Kind is InvDocKind.StockTransfer or InvDocKind.InventoryCount) return;

        EnsurePeriodOpenForStock(connection, tx, doc.DocDate);

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

            // Unit equivalence (owner definition, 2026-11-17): stock is kept in
            // the material's FIRST (base) unit. A line entered in another unit
            // moves base_qty = line_qty x equivalent(chosen unit), and its unit
            // price (quoted per chosen unit) is restated per base unit so the
            // movement's qty x unit_cost stays the line's value.
            var equivalent = ResolveUnitEquivalent(connection, tx, company, productId.Value, line.Unit);
            var baseQty = line.Qty * equivalent;
            var qtyIn = isOut ? 0m : baseQty;
            var qtyOut = isOut ? baseQty : 0m;
            var unitCost = equivalent == 1m
                ? line.UnitPrice
                : decimal.Round(line.UnitPrice / equivalent, 6, MidpointRounding.AwayFromZero);

            // Quality gate (Phase B 056): the material's QC profile
            // decides diversion (incoming), the outgoing check, and
            // whether a sale may draw from quarantine. A pre-056
            // database disables the gate (all-off profile).
            var qcProfile = QualityGate.LoadProfile(connection, tx, ProviderName, company, productId.Value);
            var actor = AppState.CurrentUser?.Username ?? string.Empty;
            var dateText = doc.DocDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            // Outgoing gate (decision Q4): a delivery/sale of an
            // outgoing-required material needs an accepted outgoing
            // inspection (Stop refuses, Warn posts + raises it; a
            // Rejected inspection blocks in both modes).
            if (qtyOut > 0 && qcProfile.OutgoingRequired
                && doc.Kind is InvDocKind.SalesInvoice or InvDocKind.PosSale or InvDocKind.StockOut)
            {
                QualityGate.CheckOutgoing(connection, tx, ProviderName, company,
                    moveType, doc.DocId.ToString(CultureInfo.InvariantCulture), doc.DocNo ?? string.Empty,
                    productId.Value, warehouseId, doc.PartyId, dateText, actor);
            }

            // Sale gate: stock that exists only in quarantine is not
            // sellable. PlanQuarantineDraw returns the Warn-mode
            // shortfall the sale may draw from quarantine (0 = no
            // draw) and throws INV-6012 in Stop mode. Explicit batch
            // captures are honored exactly, so the draw applies only
            // to lines without them.
            var quarantineDraw = 0m;
            long quarantineWarehouseId = 0;
            if (qtyOut > 0 && line.BatchAllocations.Count == 0 && line.Serials.Count == 0
                && doc.Kind is InvDocKind.SalesInvoice or InvDocKind.PosSale or InvDocKind.StockOut)
            {
                quarantineDraw = QualityGate.PlanQuarantineDraw(connection, tx, ProviderName,
                    company, warehouseId, productId.Value, qtyOut, doc.DocNo ?? string.Empty, actor);
                if (quarantineDraw > 0)
                    quarantineWarehouseId = QualityGate.FindQuarantineWarehouse(connection, tx, ProviderName, company) ?? 0;
            }

            // Availability guard (unchanged message/behaviour): the
            // engine carries the same check as a backstop (INV-6002),
            // but this path has always failed with this exact error.
            // A Warn-mode quarantine draw covers its shortfall from the
            // quarantine warehouse instead (two movements below).
            if (qtyOut > 0 && !allowNegative && quarantineDraw == 0)
            {
                var available = ReadAvailableQty(connection, tx, company, warehouseId, productId.Value);
                if (available - qtyOut < 0)
                    throw new InvalidOperationException(
                        $"Insufficient stock for material '{line.MaterialCode}'.");
            }

            // Valuation (Step 6, Phase B 053): the ONE StockValuationEngine
            // writes the movement (with its valuation record), the balance
            // and the layer queue. Receipts enter at their actual cost
            // (unitCost above); issues are valued by the engine from the
            // balance/queue per the material's cost_method — the line's
            // selling price NEVER values an outbound movement again, and
            // a linked sales return re-enters at the original sale's
            // issue rate (sweep RC-1).
            decimal? linkedRate = null;
            string? originalDocNo = null;
            if (doc.Kind is InvDocKind.SalesReturn or InvDocKind.PurchaseReturn)
            {
                originalDocNo = ResolveOriginalDocNo(connection, tx, doc);
                if (doc.Kind == InvDocKind.SalesReturn && !string.IsNullOrWhiteSpace(originalDocNo))
                    linkedRate = StockValuationEngine.TryGetDocumentRate(
                        connection, tx, ProviderName, company, originalDocNo,
                        productId.Value, warehouseId, issued: true);
            }

            // Batch/serial dimension (Phase B 054): the line's captured
            // batches/serials travel with the movement into the engine,
            // whose allocator validates and records them. Batch slices
            // were captured in the line's chosen unit, so they are
            // restated per base unit with the same equivalent as the
            // line quantity; returns carry the original document number
            // so the allocator re-enters / consumes the ORIGINAL
            // batches and serials where the line captured none.
            IReadOnlyList<BatchAllocationRequest>? batchAllocs = null;
            if (line.BatchAllocations.Count > 0)
            {
                batchAllocs = line.BatchAllocations
                    .Select(b => new BatchAllocationRequest(
                        b.BatchNo, b.Qty * equivalent, b.ExpiryDate, b.MfgDate, b.SupplierBatchNo))
                    .ToList();
            }
            IReadOnlyList<SerialSelection>? serials =
                line.Serials.Count > 0 ? line.Serials : null;

            // W6 condition routing (migration 100) for sales returns:
            // a DAMAGED line never re-enters stock — no movement is
            // written, so valuation and the Inventory GL both stay
            // put and the original sale's COGS stands as the loss.
            // (Service / non-stock materials already returned above
            // via TryGetStockableMaterial: they never move either.)
            if (qtyIn > 0 && doc.Kind == InvDocKind.SalesReturn && line.ReturnCondition == "Damaged")
                continue;

            // Incoming diversion (Phase B 056, decision Q2): a receipt
            // of a QC-required material lands in the QUARANTINE
            // warehouse — not in sellable stock — and a Draft
            // inspection is created against the receipt below, in this
            // same transaction. Completion releases it (QualityGate).
            var effectiveWarehouseId = warehouseId;
            var diverted = false;
            if (qtyIn > 0 && qcProfile.QualityCheckRequired)
            {
                var quarantineId = QualityGate.EnsureQuarantineWarehouse(connection, tx, ProviderName, company);
                if (quarantineId > 0)
                {
                    effectiveWarehouseId = quarantineId;
                    diverted = true;
                }
            }
            else if (qtyIn > 0 && doc.Kind == InvDocKind.SalesReturn && line.ReturnCondition == "Quarantine")
            {
                // W6: a return line inspected/routed as Quarantine
                // lands in the same quarantine warehouse. The return's
                // OWN inspection decision (migration 100) governs it,
                // so no QC inspection row is created here (diverted
                // stays false); the linked original rate above was
                // resolved against the document warehouse, as with the
                // QC diversion.
                var quarantineId = QualityGate.EnsureQuarantineWarehouse(connection, tx, ProviderName, company);
                if (quarantineId > 0)
                    effectiveWarehouseId = quarantineId;
            }

            if (quarantineDraw > 0)
            {
                // Warn-mode sale split: the sellable part leaves the
                // document warehouse; the shortfall leaves quarantine
                // (the gate already recorded its GateWarning alert).
                var sellablePart = qtyOut - quarantineDraw;
                if (sellablePart > 0)
                {
                    StockValuationEngine.Apply(connection, tx, ProviderName,
                        new StockMovementRequest(
                            company, warehouseId, productId.Value, dateText,
                            moveType, doc.DocNo ?? string.Empty,
                            0m, sellablePart,
                            Note: line.Description,
                            BatchAllocations: batchAllocs,
                            Serials: serials,
                            OriginalDocumentNo: originalDocNo,
                            AllowExpiredBatchIssue: line.AllowExpiredBatchIssue,
                            PartyId: doc.PartyId,
                            Actor: actor));
                }
                StockValuationEngine.Apply(connection, tx, ProviderName,
                    new StockMovementRequest(
                        company, quarantineWarehouseId, productId.Value, dateText,
                        moveType, doc.DocNo ?? string.Empty,
                        0m, quarantineDraw,
                        Note: $"{line.Description} (QC quarantine draw, Warn gate)".Trim(),
                        AutoAllocateSerials: true,
                        PartyId: doc.PartyId,
                        Actor: actor));
                continue;
            }

            var movement = StockValuationEngine.Apply(connection, tx, ProviderName,
                new StockMovementRequest(
                    company, effectiveWarehouseId, productId.Value,
                    doc.DocDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    moveType, doc.DocNo ?? string.Empty,
                    qtyIn, qtyOut,
                    ReceiptUnitCost: qtyIn > 0 && doc.Kind != InvDocKind.SalesReturn ? unitCost : null,
                    LinkedRate: linkedRate,
                    Note: line.Description,
                    BatchAllocations: batchAllocs,
                    Serials: serials,
                    OriginalDocumentNo: originalDocNo,
                    AllowExpiredBatchIssue: line.AllowExpiredBatchIssue,
                    PartyId: doc.PartyId,
                    Actor: AppState.CurrentUser?.Username));

            if (diverted)
            {
                var batchId = movement.AllocationRecords
                    .FirstOrDefault(a => a.BatchId is not null)?.BatchId;
                QualityGate.EnsureInspection(connection, tx, ProviderName, company,
                    QualityInspectionTypes.Incoming, moveType,
                    doc.DocId.ToString(CultureInfo.InvariantCulture), doc.DocNo ?? string.Empty,
                    productId.Value, batchId, warehouseId, effectiveWarehouseId,
                    doc.PartyId, qtyIn, dateText, actor);
            }
        }
    }

    /// <summary>
    /// Step 9: restates a document line's quantity and unit price in the
    /// material's BASE unit — the conversion the dedicated engines
    /// (PostTransfer / PostCount) require, since they take base-unit
    /// quantities. Uses the exact rule <see cref="ApplyDocument"/>
    /// applies to invoice documents: base_qty = qty × equivalent(chosen
    /// unit), and the unit price (quoted per chosen unit) is restated
    /// per base unit so qty × cost keeps the line's value.
    /// </summary>
    public static (decimal BaseQty, decimal BaseUnitCost) ToBaseUnits(
        DbConnection connection, DbTransaction tx, long materialId, string? unit, decimal qty, decimal unitPrice)
    {
        var company = AppState.CurrentCompanyCode ?? string.Empty;
        var equivalent = ResolveUnitEquivalent(connection, tx, company, materialId, unit);
        var baseQty = qty * equivalent;
        var baseUnitCost = equivalent == 1m
            ? unitPrice
            : decimal.Round(unitPrice / equivalent, 6, MidpointRounding.AwayFromZero);
        return (baseQty, baseUnitCost);
    }

    private static string ProviderName => DatabaseRuntime.IsPostgreSql() ? "PostgreSQL" : "SQLite";

    private static decimal ReadAvailableQty(
        DbConnection connection, DbTransaction tx, string company, long warehouseId, long materialId)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = DatabaseRuntime.IsPostgreSql()
            ? "SELECT available_qty FROM material_stock_balances WHERE company_id=$c AND warehouse_id=$w AND material_id=$m AND is_deleted=FALSE LIMIT 1;"
            : "SELECT available_qty FROM material_stock_balances WHERE company_id=$c AND warehouse_id=$w AND material_id=$m AND is_deleted=0 LIMIT 1;";
        Add(cmd, "$c", company);
        Add(cmd, "$w", warehouseId);
        Add(cmd, "$m", materialId);
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? 0m : Convert.ToDecimal(value, CultureInfo.InvariantCulture);
    }

    /// <summary>The original document number of a return document, from
    /// the in-memory document when the caller set it, else from the
    /// stored header (InvRepo.Post loads headers without the original_*
    /// columns, so the reversal's link lives only in storage there).</summary>
    private static string? ResolveOriginalDocNo(DbConnection connection, DbTransaction tx, InvDoc doc)
    {
        if (!string.IsNullOrWhiteSpace(doc.OriginalDocNo)) return doc.OriginalDocNo;
        if (doc.DocId <= 0) return null;
        var table = doc.Kind.IsSales() ? "sales_documents" : "purchase_documents";
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT original_doc_no FROM {table} WHERE doc_id=$id AND company_id=$c LIMIT 1;";
        Add(cmd, "$id", doc.DocId);
        Add(cmd, "$c", AppState.CurrentCompanyCode ?? string.Empty);
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Resolves the equivalence of a document line's chosen unit relative to
    /// the material's first (base) unit, from the unit rows saved on the
    /// material card (units_json carries the complete list; the
    /// unit1..unit5 column pairs are the fallback for legacy rows).
    /// Rules: no unit chosen, the base unit itself, an unknown or free-typed
    /// unit, or a stored equivalent of 0 or less all resolve to 1 — the
    /// quantity is then already expressed in base units.
    /// </summary>
    private static decimal ResolveUnitEquivalent(
        DbConnection connection, DbTransaction tx, string company, long materialId, string? unitName)
    {
        if (string.IsNullOrWhiteSpace(unitName)) return 1m;
        var wanted = unitName.Trim();

        var units = new List<(string Name, decimal Factor)>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT unit1, unit1_factor, unit2, unit2_factor, unit3, unit3_factor,
                       unit4, unit4_factor, unit5, unit5_factor, units_json
                FROM materials
                WHERE company_id=$c AND material_id=$m;
                """;
            Add(cmd, "$c", company);
            Add(cmd, "$m", materialId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return 1m;

            var json = reader.IsDBNull(10) ? null : reader.GetValue(10)?.ToString();
            if (!string.IsNullOrWhiteSpace(json) && json.Trim() != "[]")
            {
                try
                {
                    var rows = System.Text.Json.JsonSerializer.Deserialize<List<UnitFactorRow>>(json)
                               ?? new List<UnitFactorRow>();
                    foreach (var row in rows)
                        if (!string.IsNullOrWhiteSpace(row.UnitName))
                            units.Add((row.UnitName.Trim(), row.Factor ?? 0m));
                }
                catch (Exception exJson)
                {
                    // Malformed legacy JSON must not block posting: report it
                    // and fall back to the unit column pairs below.
                    AppHost.Errors.Report(ErrorCodes.InventoryOperationFailed, exJson,
                        "StkPost.ResolveUnitEquivalent");
                    units.Clear();
                }
            }

            if (units.Count == 0)
            {
                for (var i = 0; i < 5; i++)
                {
                    var name = reader.IsDBNull(i * 2) ? null : reader.GetValue(i * 2)?.ToString();
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var factor = reader.IsDBNull(i * 2 + 1)
                        ? 0m
                        : Convert.ToDecimal(reader.GetValue(i * 2 + 1), CultureInfo.InvariantCulture);
                    units.Add((name.Trim(), factor));
                }
            }
        }

        if (units.Count == 0) return 1m;

        // The first unit is the base unit: its equivalent is 1 by definition.
        if (string.Equals(wanted, units[0].Name, StringComparison.OrdinalIgnoreCase)) return 1m;

        foreach (var (name, factor) in units)
            if (string.Equals(wanted, name, StringComparison.OrdinalIgnoreCase))
                return factor > 0 ? factor : 1m;

        return 1m;
    }

    /// <summary>One unit row as serialized into materials.units_json by the material card.</summary>
    private sealed class UnitFactorRow
    {
        public string? UnitName { get; set; }
        public decimal? Factor { get; set; }
    }

    public static int PostCount(
        long warehouseId,
        DateTime countDate,
        string reason,
        IReadOnlyCollection<StockCountLine> lines,
        string? operationKey = null)
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

        // A committed operation must remain recoverable even if the warehouse
        // is later deactivated. Check the durable receipt before mutable
        // master-data validation so a retry can safely finalize its document.
        if (!string.IsNullOrWhiteSpace(operationKey)
            && TryReadCompletedOperation(connection, tx, company, operationKey, "InventoryCount",
                out _, out var completedMovementCount))
            return completedMovementCount;
        EnsurePeriodOpenForStock(connection, tx, countDate);
        EnsureWarehouseIsActive(connection, tx, company, warehouseId);

        var documentNo = $"CNT-{countDate:yyyyMMdd}-{Guid.NewGuid():N}";
        var date = countDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var posted = 0;

        foreach (var line in lines)
        {
            EnsureMaterialExists(connection, tx, company, line.MaterialId, line.MaterialCode);

            decimal currentQty;
            decimal currentCost;
            using (var read = connection.CreateCommand())
            {
                read.Transaction = tx;
                read.CommandText = DatabaseRuntime.IsPostgreSql()
                    ? """
                      SELECT available_qty, average_cost
                      FROM material_stock_balances
                      WHERE company_id=$c AND warehouse_id=$w AND material_id=$m AND is_deleted=FALSE
                      LIMIT 1
                      FOR UPDATE;
                      """
                    : """
                      SELECT available_qty, average_cost
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
                    currentQty = Convert.ToDecimal(reader.GetValue(0), CultureInfo.InvariantCulture);
                    currentCost = reader.IsDBNull(1)
                        ? line.UnitCost
                        : Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture);
                }
                else
                {
                    currentQty = 0;
                    currentCost = line.UnitCost;
                }
            }

            var delta = line.CountedQty - currentQty;
            if (delta == 0)
                continue; // no stock effect; a zero movement is not recorded

            // Valuation (Step 6, Phase B 053): the delta goes through
            // the ONE engine. A surplus enters at the current rate when
            // stock is on hand (LinkedRate = the balance's display
            // average), else at the count sheet's cost; a shortage is
            // valued by the engine per cost_method (FIFO counts consume
            // the newest layers first, per the design).
            StockValuationEngine.Apply(connection, tx, ProviderName,
                new StockMovementRequest(
                    company, warehouseId, line.MaterialId, date,
                    "COUNT_CLOSE", documentNo,
                    QtyIn: Math.Max(delta, 0), QtyOut: Math.Max(-delta, 0),
                    ReceiptUnitCost: line.UnitCost,
                    LinkedRate: delta > 0 && currentQty > 0 ? currentCost : null,
                    Note: reason.Trim(),
                    BatchAllocations: line.BatchAllocations,
                    Serials: line.Serials,
                    Actor: AppState.CurrentUser?.Username));
            posted++;
        }

        if (!string.IsNullOrWhiteSpace(operationKey))
            RecordCompletedOperation(connection, tx, company, operationKey, "InventoryCount", documentNo, posted);

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
                         average_cost, last_cost, is_deleted, stock_value
                  FROM material_stock_balances
                  WHERE company_id=$c AND material_id IN ($source, $target)
                  FOR UPDATE;
                  """
                : """
                  SELECT stock_balance_id, warehouse_id, material_id, available_qty, reserved_qty,
                         average_cost, last_cost, is_deleted, stock_value
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
                    IsDeleted = Convert.ToBoolean(reader.GetValue(7), CultureInfo.InvariantCulture),
                    StockValue = reader.IsDBNull(8) ? 0m : Convert.ToDecimal(reader.GetValue(8), CultureInfo.InvariantCulture)
                };
                balances[(balance.WarehouseId, balance.MaterialId)] = balance;
            }
        }

        // Step 6: the FIFO/LIFO layer queue follows the material, like
        // the movements above — otherwise the target material would
        // inherit balances whose layers still belong to the source.
        using (var layers = connection.CreateCommand())
        {
            layers.Transaction = tx;
            layers.CommandText = """
                UPDATE stock_cost_layers
                SET material_id=$target
                WHERE company_id=$c AND material_id=$source;
                """;
            Add(layers, "$target", targetMaterialId);
            Add(layers, "$source", sourceMaterialId);
            Add(layers, "$c", company);
            layers.ExecuteNonQuery();
        }

        foreach (var sourceBalance in balances.Values
                     .Where(x => x.MaterialId == sourceMaterialId && !x.IsDeleted))
        {
            balances.TryGetValue((sourceBalance.WarehouseId, targetMaterialId), out var targetBalance);
            var targetActive = targetBalance is not null && !targetBalance.IsDeleted;
            var available = sourceBalance.Available + (targetActive ? targetBalance!.Available : 0m);
            var reserved = sourceBalance.Reserved + (targetActive ? targetBalance!.Reserved : 0m);
            // Step 6: merge the engine-maintained stock_value, not a
            // re-derived qty x average (the two are the same only while
            // every writer was the engine).
            var value = sourceBalance.StockValue + (targetActive ? targetBalance!.StockValue : 0m);
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
                     average_cost, last_cost, stock_value, last_updated, is_deleted)
                    VALUES ($c, $w, $target, $available, $reserved, $average, $last, $value, {now},
                            {(DatabaseRuntime.IsPostgreSql() ? "FALSE" : "0")});
                    """;
                Add(insert, "$c", company);
                Add(insert, "$w", sourceBalance.WarehouseId);
                Add(insert, "$target", targetMaterialId);
                Add(insert, "$available", available);
                Add(insert, "$reserved", reserved);
                Add(insert, "$average", averageCost);
                Add(insert, "$last", lastCost);
                Add(insert, "$value", value);
                insert.ExecuteNonQuery();
            }
            else
            {
                using var updateTarget = connection.CreateCommand();
                updateTarget.Transaction = tx;
                updateTarget.CommandText = $"""
                    UPDATE material_stock_balances
                    SET available_qty=$available, reserved_qty=$reserved,
                        average_cost=$average, last_cost=$last, stock_value=$value, last_updated={now},
                        is_deleted={(DatabaseRuntime.IsPostgreSql() ? "FALSE" : "0")}
                    WHERE stock_balance_id=$id;
                    """;
                Add(updateTarget, "$available", available);
                Add(updateTarget, "$reserved", reserved);
                Add(updateTarget, "$average", averageCost);
                Add(updateTarget, "$last", lastCost);
                Add(updateTarget, "$value", value);
                Add(updateTarget, "$id", targetBalance.Id);
                updateTarget.ExecuteNonQuery();
            }

            using var clearSource = connection.CreateCommand();
            clearSource.Transaction = tx;
            clearSource.CommandText = $"""
                UPDATE material_stock_balances
                SET available_qty=0, reserved_qty=0, stock_value=0, last_updated={now},
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
        public decimal StockValue { get; init; }
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
        IReadOnlyCollection<StockTransferLine> lines,
        string? operationKey = null)
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

            // A durable receipt takes precedence over current warehouse
            // activation state: the stock transaction has already committed.
            if (!string.IsNullOrWhiteSpace(operationKey)
                && TryReadCompletedOperation(connection, tx, company, operationKey, "StockTransfer",
                    out var completedDocumentNo, out _))
                return completedDocumentNo;
            EnsurePeriodOpenForStock(connection, tx, transferDate);
            EnsureWarehouseIsActive(connection, tx, company, sourceWarehouseId);
            EnsureWarehouseIsActive(connection, tx, company, targetWarehouseId);

            var documentNo = string.Create(
                CultureInfo.InvariantCulture,
                $"TRF-{transferDate:yyyyMMdd}-{Guid.NewGuid():N}");
            var movementDate = transferDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

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

                // Valuation (Step 6, Phase B 053): both legs go through
                // the ONE engine. Out at the source's method rate; in at
                // the EXACT same value (ReceiptCostAmount), so a transfer
                // never creates or destroys stock value, and FIFO layers
                // move with the stock at identical rates.
                var outbound = StockValuationEngine.Apply(connection, tx, ProviderName,
                    new StockMovementRequest(
                        company, sourceWarehouseId, line.MaterialId, movementDate,
                        "TRANSFER_OUT", documentNo,
                        QtyIn: 0m, QtyOut: line.Quantity,
                        Note: $"{reason.Trim()} → {targetWarehouseId.ToString(CultureInfo.InvariantCulture)}",
                        Actor: AppState.CurrentUser?.Username));
                // Batch/serial (054): the in-leg re-creates the out-leg's
                // allocations at the destination (SourceMovementId), so
                // batches keep their identity and serials keep their
                // numbers across the transfer.
                StockValuationEngine.Apply(connection, tx, ProviderName,
                    new StockMovementRequest(
                        company, targetWarehouseId, line.MaterialId, movementDate,
                        "TRANSFER_IN", documentNo,
                        QtyIn: line.Quantity, QtyOut: 0m,
                        ReceiptCostAmount: -outbound.CostAmount,
                        Note: $"{reason.Trim()} ← {sourceWarehouseId.ToString(CultureInfo.InvariantCulture)}",
                        SourceMovementId: outbound.MovementId,
                        Actor: AppState.CurrentUser?.Username));
            }

            if (!string.IsNullOrWhiteSpace(operationKey))
                RecordCompletedOperation(connection, tx, company, operationKey, "StockTransfer",
                    documentNo, lines.Count * 2);

            tx.Commit();
            return documentNo;
        }
        catch (StockTransferException)
        {
            throw;
        }
        catch (AppError)
        {
            // Catalog errors (e.g. ACC-5021, posting into a closed period)
            // reach the caller unmasked — wrapping them in the generic
            // transfer failure would hide the catalog code and its
            // localized message from the user.
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

    public static bool IsOperationCompleted(string operationKey, string expectedType)
    {
        if (string.IsNullOrWhiteSpace(operationKey))
            throw new ArgumentException("An operation key is required.", nameof(operationKey));
        if (string.IsNullOrWhiteSpace(expectedType))
            throw new ArgumentException("An operation type is required.", nameof(expectedType));

        var company = AppState.CurrentCompanyCode?.Trim();
        if (string.IsNullOrWhiteSpace(company))
            throw new InvalidOperationException("No current company is selected.");
        var connectionString = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("No connection to the current company database.");

        using DbConnection connection = DatabaseRuntime.IsPostgreSql()
            ? new NpgsqlConnection(connectionString)
            : new SqliteConnection(DatabaseRuntime.NormalizeSqliteConnectionString(connectionString));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT operation_type
            FROM stock_posting_operations
            WHERE company_id=$company AND operation_key=$key;
            """;
        Add(command, "$company", company);
        Add(command, "$key", operationKey);
        var value = command.ExecuteScalar();
        if (value is null or DBNull) return false;
        var actualType = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        if (!string.Equals(actualType, expectedType, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The stock posting operation key is already registered for a different operation type.");
        return true;
    }

    private static bool TryReadCompletedOperation(
        DbConnection connection,
        DbTransaction tx,
        string company,
        string operationKey,
        string expectedType,
        out string documentNo,
        out int movementCount)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT operation_type, document_no, movement_count
            FROM stock_posting_operations
            WHERE company_id=$company AND operation_key=$key;
            """;
        Add(command, "$company", company);
        Add(command, "$key", operationKey);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            documentNo = string.Empty;
            movementCount = 0;
            return false;
        }

        var actualType = Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture) ?? string.Empty;
        if (!string.Equals(actualType, expectedType, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The stock posting operation key is already registered for a different operation type.");

        documentNo = Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) ?? string.Empty;
        movementCount = Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture);
        return true;
    }

    private static void RecordCompletedOperation(
        DbConnection connection,
        DbTransaction tx,
        string company,
        string operationKey,
        string operationType,
        string documentNo,
        int movementCount)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO stock_posting_operations
                (company_id, operation_key, operation_type, document_no, movement_count, created_at)
            VALUES ($company, $key, $type, $documentNo, $count, CURRENT_TIMESTAMP);
            """;
        Add(command, "$company", company);
        Add(command, "$key", operationKey);
        Add(command, "$type", operationType);
        Add(command, "$documentNo", documentNo);
        Add(command, "$count", movementCount);
        command.ExecuteNonQuery();
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
            SELECT is_stocked, is_service, allow_negative, item_type
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

        // Service-material rule (owner, 2026-11-17): a service material is
        // non-stock and must never move stock. The card keeps the type and
        // the flags in agreement, but a legacy or imported row may carry
        // the Service TYPE with inconsistent flags — honor any of the
        // three views here so such a row still cannot post stock.
        var itemType = reader.IsDBNull(3) ? null : reader.GetValue(3)?.ToString();
        if (!Convert.ToBoolean(reader.GetValue(0), CultureInfo.InvariantCulture) ||
            Convert.ToBoolean(reader.GetValue(1), CultureInfo.InvariantCulture) ||
            string.Equals(itemType, "Service", StringComparison.OrdinalIgnoreCase))
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
