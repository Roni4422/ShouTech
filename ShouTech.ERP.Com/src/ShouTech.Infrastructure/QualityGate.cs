using System.Data.Common;
using System.Globalization;
using ShouTech.Domain;
using ShouTech.Domain.Entities;

namespace ShouTech.Infrastructure;

/// <summary>One inspection line's evaluation inputs (criteria snapshot
/// plus the recorded readings), for <see cref="QualityGate.EvaluateLine"/>.</summary>
public sealed record QualityLineEvaluationInput(
    string ParamType, decimal? MinValue, decimal? MaxValue, string? AcceptedValue,
    IReadOnlyList<QualityReading> Readings, string? RecordedResult);

/// <summary>
/// The quality gate (Phase B design section 5, migration 056). Static,
/// in-transaction helpers in the BudgetGuard / PartyCreditGuard
/// pattern, called INSIDE the posting transactions of the stock
/// writers (StkPost for invoice documents, MfgOrderRepository for
/// manufacturing completions, CommercialDocumentRepository for chain
/// deliveries):
///
///   * INCOMING / IN-PROCESS DIVERSION — a receipt of a material with
///     quality_check_required lands in the company's quarantine
///     warehouse (decision Q2) instead of sellable stock, and a Draft
///     inspection is created against the receipt in the same
///     transaction (<see cref="DivertReceipt"/> is called by the
///     writer BEFORE the engine, which then posts to the warehouse
///     the gate returns; <see cref="EnsureInspection"/> records the
///     inspection AFTER the movement, when its batch is known).
///   * COMPLETION — <see cref="CompleteInTransaction"/> releases the
///     accepted quantity quarantine → destination through the ONE
///     StockValuationEngine (a value-exact transfer pair, so 054
///     batches/serials keep their identity) and dispositions the
///     rejected quantity (ReturnToSupplier / Scrap leave quarantine
///     as engine issues; UseAsIs releases like an acceptance; Rework /
///     Quarantine stay). A rejected part always raises a quality
///     alert (Odoo pattern).
///   * THE SALE GATE — stock that exists only in quarantine is not
///     sellable: <see cref="PlanQuarantineDraw"/> refuses the draw in
///     Stop mode (INV-6012) and, in Warn mode, allows it and records
///     a GateWarning alert (the design's Warn semantics: post +
///     surface). QC_GATE_MODE is per company (quality_settings,
///     decision Q1; default Warn).
///   * OUTGOING (decision Q4) — a delivery of a qc_outgoing_required
///     material needs an accepted outgoing inspection: Stop refuses
///     (INV-6013), Warn posts and raises the pending inspection. A
///     REJECTED inspection blocks in both modes (both competitors
///     agree; INV-6011).
///
/// A pre-056 database (no quality tables) disables the gate entirely:
/// every entry point checks table existence first, so posting behaves
/// exactly as before — the pre-056 baseline.
/// </summary>
public static class QualityGate
{
    /// <summary>Days after the receipt date by which a pending
    /// inspection falls due (the overdue worklist's deadline).</summary>
    public const int InspectionDueDays = 3;

    // ------------------------------------------------------------------
    // Material QC profile
    // ------------------------------------------------------------------
    public sealed record QcProfile(
        bool QualityCheckRequired, bool OutgoingRequired,
        Guid? TemplateInId, Guid? TemplateOutId, string Code);

    /// <summary>The material's QC wiring, or an all-off profile when
    /// the material or the 056 columns do not exist.</summary>
    public static QcProfile LoadProfile(
        DbConnection connection, DbTransaction transaction, string provider, string companyId, long materialId)
    {
        var prov = GlPosting.NormalizeProvider(provider);
        var isPg = prov == "PostgreSQL";
        if (!PartyLedger.TableExists(connection, transaction, prov, "quality_inspections"))
            return new QcProfile(false, false, null, null, materialId.ToString(CultureInfo.InvariantCulture));
        var hasOutgoing = PartyLedger.ColumnExists(connection, transaction, prov, "materials", "qc_outgoing_required");
        var cols = hasOutgoing
            ? "code, COALESCE(quality_check_required, 0), COALESCE(qc_outgoing_required, 0), qc_template_in_id, qc_template_out_id"
            : "code, COALESCE(quality_check_required, 0), 0, NULL, NULL";
        foreach (var scoped in new[] { true, false })
        {
            using var cmd = GlPosting.CreateCommand(connection, transaction, scoped
                ? $"SELECT {cols} FROM materials WHERE company_id = @c AND material_id = @m AND is_deleted = {(isPg ? "FALSE" : "0")} LIMIT 1"
                : $"SELECT {cols} FROM materials WHERE material_id = @m AND is_deleted = {(isPg ? "FALSE" : "0")} LIMIT 1");
            if (scoped) GlPosting.Add(cmd, "@c", companyId.Trim());
            GlPosting.Add(cmd, "@m", materialId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) continue;
            return new QcProfile(
                Truthy(reader.GetValue(1)),
                Truthy(reader.GetValue(2)),
                ParseGuidOrNull(reader.GetValue(3)),
                ParseGuidOrNull(reader.GetValue(4)),
                ValuationText.ToText(reader.GetValue(0)) ?? materialId.ToString(CultureInfo.InvariantCulture));
        }
        return new QcProfile(false, false, null, null, materialId.ToString(CultureInfo.InvariantCulture));
    }

    // ------------------------------------------------------------------
    // Gate mode + quarantine warehouse
    // ------------------------------------------------------------------
    public static string GetGateMode(
        DbConnection connection, DbTransaction transaction, string provider, string companyId)
    {
        var prov = GlPosting.NormalizeProvider(provider);
        if (!PartyLedger.TableExists(connection, transaction, prov, "quality_settings"))
            return QualityGateModes.Default;
        using var cmd = GlPosting.CreateCommand(connection, transaction,
            "SELECT gate_mode FROM quality_settings WHERE company_id = @c LIMIT 1");
        GlPosting.Add(cmd, "@c", companyId.Trim());
        var value = cmd.ExecuteScalar();
        var mode = value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
        return QualityGateModes.All.Contains(mode ?? string.Empty) ? mode! : QualityGateModes.Default;
    }

    /// <summary>The company's quarantine warehouse (warehouses.
    /// is_quarantine = 1), created on first use inside the caller's
    /// transaction: one flagged warehouse per company, named
    /// "Quarantine". Returns 0 only when the 056 column is missing.</summary>
    public static long EnsureQuarantineWarehouse(
        DbConnection connection, DbTransaction transaction, string provider, string companyId)
    {
        var prov = GlPosting.NormalizeProvider(provider);
        var isPg = prov == "PostgreSQL";
        if (!PartyLedger.ColumnExists(connection, transaction, prov, "warehouses", "is_quarantine"))
            return 0;
        using (var find = GlPosting.CreateCommand(connection, transaction, $"""
            SELECT warehouse_id FROM warehouses
            WHERE company_id = @c AND is_quarantine = {(isPg ? "1" : "1")}
              AND is_deleted = {(isPg ? "FALSE" : "0")} AND is_active = {(isPg ? "TRUE" : "1")}
            ORDER BY warehouse_id LIMIT 1
            """))
        {
            GlPosting.Add(find, "@c", companyId.Trim());
            var found = find.ExecuteScalar();
            if (found is not null and not DBNull)
                return Convert.ToInt64(found, CultureInfo.InvariantCulture);
        }
        var sql = isPg
            ? """
              INSERT INTO warehouses (company_id, warehouse_name_ar, warehouse_name_en, is_active, is_quarantine)
              VALUES (@c, 'Quarantine Warehouse', 'Quarantine', TRUE, 1) RETURNING warehouse_id
              """
            : """
              INSERT INTO warehouses (company_id, warehouse_name_ar, warehouse_name_en, is_active, is_quarantine)
              VALUES (@c, 'Quarantine Warehouse', 'Quarantine', 1, 1); SELECT last_insert_rowid();
              """;
        using var ins = GlPosting.CreateCommand(connection, transaction, sql);
        GlPosting.Add(ins, "@c", companyId.Trim());
        return Convert.ToInt64(ins.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public static long? FindQuarantineWarehouse(
        DbConnection connection, DbTransaction transaction, string provider, string companyId)
    {
        var prov = GlPosting.NormalizeProvider(provider);
        var isPg = prov == "PostgreSQL";
        if (!PartyLedger.ColumnExists(connection, transaction, prov, "warehouses", "is_quarantine"))
            return null;
        using var cmd = GlPosting.CreateCommand(connection, transaction, $"""
            SELECT warehouse_id FROM warehouses
            WHERE company_id = @c AND is_quarantine = 1
              AND is_deleted = {(isPg ? "FALSE" : "0")}
            ORDER BY warehouse_id LIMIT 1
            """);
        GlPosting.Add(cmd, "@c", companyId.Trim());
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    // ------------------------------------------------------------------
    // Incoming diversion + inspection creation
    // ------------------------------------------------------------------
    /// <summary>Creates (or returns the existing) Draft inspection for
    /// one diverted receipt quantity, snapshotting the material's
    /// template lines as the inspection lines. Idempotent per
    /// (document, material, batch): a re-run inside a retried posting
    /// never duplicates the inspection.</summary>
    public static Guid EnsureInspection(
        DbConnection connection, DbTransaction transaction, string provider,
        string companyId, string inspectionType, string refDocKind, string refDocId, string refDocNo,
        long materialId, long? batchId, long destinationWarehouseId, long? quarantineWarehouseId,
        long? partyId, decimal totalQty, string movementDate, string actor)
    {
        var prov = GlPosting.NormalizeProvider(provider);
        var isPg = prov == "PostgreSQL";
        var profile = LoadProfile(connection, transaction, prov, companyId, materialId);
        var templateId = inspectionType == QualityInspectionTypes.Outgoing
            ? profile.TemplateOutId
            : profile.TemplateInId;

        using (var find = GlPosting.CreateCommand(connection, transaction, $"""
            SELECT inspection_id FROM quality_inspections
            WHERE company_id = @c AND ref_doc_kind = @kind AND ref_doc_no = @doc
              AND material_id = @m AND inspection_type = @type
              AND {(isPg ? "COALESCE(batch_id, 0)" : "COALESCE(batch_id, 0)")} = @batch
              AND is_deleted = {(isPg ? "FALSE" : "0")} LIMIT 1
            """))
        {
            GlPosting.Add(find, "@c", companyId.Trim());
            GlPosting.Add(find, "@kind", refDocKind);
            GlPosting.Add(find, "@doc", refDocNo);
            GlPosting.Add(find, "@m", materialId);
            GlPosting.Add(find, "@type", inspectionType);
            GlPosting.Add(find, "@batch", batchId ?? 0L);
            var existing = find.ExecuteScalar();
            if (existing is not null and not DBNull)
                return Guid.Parse(Convert.ToString(existing, CultureInfo.InvariantCulture)!);
        }

        var inspectionId = Guid.NewGuid();
        var dueDate = ComputeDueDate(movementDate);
        using (var ins = GlPosting.CreateCommand(connection, transaction, """
            INSERT INTO quality_inspections
                (inspection_id, company_id, inspection_type, template_id, ref_doc_kind, ref_doc_id,
                 ref_doc_no, material_id, batch_id, warehouse_id, quarantine_warehouse_id, party_id,
                 total_qty, sample_qty, status, due_date, created_by)
            VALUES (@id, @c, @type, @template, @kind, @docId, @doc, @m, @batch, @w, @qw, @party,
                    @qty, @qty, 'Draft', @due, @actor)
            """))
        {
            GlPosting.Add(ins, "@id", GlPosting.FormatGuid(prov, inspectionId));
            GlPosting.Add(ins, "@c", companyId.Trim());
            GlPosting.Add(ins, "@type", inspectionType);
            GlPosting.Add(ins, "@template", templateId is Guid t ? GlPosting.FormatGuid(prov, t) : DBNull.Value);
            GlPosting.Add(ins, "@kind", refDocKind);
            GlPosting.Add(ins, "@docId", refDocId ?? string.Empty);
            GlPosting.Add(ins, "@doc", refDocNo);
            GlPosting.Add(ins, "@m", materialId);
            GlPosting.Add(ins, "@batch", (object?)batchId ?? DBNull.Value);
            GlPosting.Add(ins, "@w", destinationWarehouseId);
            GlPosting.Add(ins, "@qw", (object?)quarantineWarehouseId ?? DBNull.Value);
            GlPosting.Add(ins, "@party", (object?)partyId ?? DBNull.Value);
            GlPosting.Add(ins, "@qty", totalQty);
            GlPosting.Add(ins, "@due", (object?)dueDate ?? DBNull.Value);
            GlPosting.Add(ins, "@actor", actor);
            ins.ExecuteNonQuery();
        }

        if (templateId is Guid tid)
            SnapshotTemplateLines(connection, transaction, prov, inspectionId, tid);
        return inspectionId;
    }

    internal static void SnapshotTemplateLines(
        DbConnection connection, DbTransaction transaction, string prov, Guid inspectionId, Guid templateId)
    {
        var rows = new List<(Guid ParamId, decimal? Min, decimal? Max, string? Accepted, bool Mandatory, int Sort)>();
        using (var cmd = GlPosting.CreateCommand(connection, transaction, """
            SELECT param_id, min_value, max_value, accepted_value, is_mandatory, sort
            FROM quality_template_lines WHERE template_id = @t ORDER BY sort
            """))
        {
            GlPosting.Add(cmd, "@t", GlPosting.FormatGuid(prov, templateId));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((
                    Guid.Parse(Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!),
                    reader.IsDBNull(1) ? null : Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture),
                    reader.IsDBNull(2) ? null : Convert.ToDecimal(reader.GetValue(2), CultureInfo.InvariantCulture),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    Truthy(reader.GetValue(4)),
                    Convert.ToInt32(reader.GetValue(5), CultureInfo.InvariantCulture)));
            }
        }
        var isPg = prov == "PostgreSQL";
        foreach (var row in rows)
        {
            using var ins = GlPosting.CreateCommand(connection, transaction, """
                INSERT INTO quality_inspection_lines
                    (line_id, inspection_id, param_id, min_value, max_value, accepted_value, is_mandatory, sort)
                VALUES (@id, @insp, @param, @min, @max, @acc, @mand, @sort)
                """);
            GlPosting.Add(ins, "@id", GlPosting.FormatGuid(prov, Guid.NewGuid()));
            GlPosting.Add(ins, "@insp", GlPosting.FormatGuid(prov, inspectionId));
            GlPosting.Add(ins, "@param", GlPosting.FormatGuid(prov, row.ParamId));
            GlPosting.Add(ins, "@min", (object?)row.Min ?? DBNull.Value);
            GlPosting.Add(ins, "@max", (object?)row.Max ?? DBNull.Value);
            GlPosting.Add(ins, "@acc", (object?)row.Accepted ?? DBNull.Value);
            GlPosting.Add(ins, "@mand", isPg ? (object)row.Mandatory : (row.Mandatory ? 1 : 0));
            GlPosting.Add(ins, "@sort", row.Sort);
            ins.ExecuteNonQuery();
        }
    }

    // ------------------------------------------------------------------
    // Reading evaluation (the auto pass/fail; pure, unit-testable)
    // ------------------------------------------------------------------
    /// <summary>Auto-evaluates one line from its readings: Numeric =
    /// every numeric reading inside the band; Value = every text
    /// reading equal to the accepted value; PassFail = the recorded
    /// result only. Returns null when the line is unjudged (no usable
    /// readings and no recorded result).</summary>
    public static string? EvaluateLine(QualityLineEvaluationInput input)
    {
        switch (input.ParamType)
        {
            case QualityParamTypes.Numeric:
            {
                var nums = input.Readings.Where(r => r.ReadingNum is not null)
                    .Select(r => r.ReadingNum!.Value).ToList();
                if (nums.Count == 0) return input.RecordedResult;
                var pass = nums.All(n =>
                    (input.MinValue is null || n >= input.MinValue.Value) &&
                    (input.MaxValue is null || n <= input.MaxValue.Value));
                return pass ? "Pass" : "Fail";
            }
            case QualityParamTypes.Value:
            {
                var texts = input.Readings.Where(r => !string.IsNullOrWhiteSpace(r.ReadingText))
                    .Select(r => r.ReadingText!.Trim()).ToList();
                if (texts.Count == 0) return input.RecordedResult;
                if (string.IsNullOrWhiteSpace(input.AcceptedValue)) return input.RecordedResult;
                var pass = texts.All(t => string.Equals(t, input.AcceptedValue!.Trim(), StringComparison.OrdinalIgnoreCase));
                return pass ? "Pass" : "Fail";
            }
            default:
                return input.RecordedResult;
        }
    }

    // ------------------------------------------------------------------
    // Completion: release + disposition through the engine
    // ------------------------------------------------------------------
    internal sealed record InspectionHead(
        Guid Id, string InspectionType, string RefDocKind, string RefDocNo,
        long MaterialId, long? BatchId, long WarehouseId, long? QuarantineWarehouseId,
        long? PartyId, decimal TotalQty, string Status);

    internal static InspectionHead? LoadHead(
        DbConnection connection, DbTransaction transaction, string prov, string companyId, Guid inspectionId)
    {
        var isPg = prov == "PostgreSQL";
        using var cmd = GlPosting.CreateCommand(connection, transaction, $"""
            SELECT inspection_type, ref_doc_kind, ref_doc_no, material_id, batch_id,
                   warehouse_id, quarantine_warehouse_id, party_id, total_qty, status
            FROM quality_inspections
            WHERE company_id = @c AND inspection_id = @id AND is_deleted = {(isPg ? "FALSE" : "0")} LIMIT 1
            """);
        GlPosting.Add(cmd, "@c", companyId.Trim());
        GlPosting.Add(cmd, "@id", GlPosting.FormatGuid(prov, inspectionId));
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new InspectionHead(
            inspectionId,
            reader.GetString(0), reader.GetString(1), reader.GetString(2),
            Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture),
            reader.IsDBNull(4) ? null : Convert.ToInt64(reader.GetValue(4), CultureInfo.InvariantCulture),
            Convert.ToInt64(reader.GetValue(5), CultureInfo.InvariantCulture),
            reader.IsDBNull(6) ? null : Convert.ToInt64(reader.GetValue(6), CultureInfo.InvariantCulture),
            reader.IsDBNull(7) ? null : Convert.ToInt64(reader.GetValue(7), CultureInfo.InvariantCulture),
            Convert.ToDecimal(reader.GetValue(8), CultureInfo.InvariantCulture),
            reader.GetString(9));
    }

    /// <summary>Moves <paramref name="qty"/> from the quarantine
    /// warehouse to the destination warehouse as a value-exact engine
    /// transfer pair (the StkPost.PostTransfer pattern), preserving
    /// batch/serial identity through the out-leg's allocations.</summary>
    private static void ReleaseQuantity(
        DbConnection connection, DbTransaction transaction, string provider,
        string companyId, InspectionHead head, decimal qty, string dateText, string actor, string note)
    {
        var quarantineId = head.QuarantineWarehouseId!.Value;
        IReadOnlyList<BatchAllocationRequest>? batchAllocs = null;
        if (head.BatchId is long batchId)
        {
            var batchNo = BatchNo(connection, transaction, batchId);
            if (batchNo is not null)
                batchAllocs = new[] { new BatchAllocationRequest(batchNo, qty) };
        }
        var docNo = $"QC-REL-{head.RefDocNo}";
        var outbound = StockValuationEngine.Apply(connection, transaction, provider,
            new StockMovementRequest(companyId.Trim(), quarantineId, head.MaterialId, dateText,
                "TRANSFER_OUT", docNo, QtyIn: 0m, QtyOut: qty,
                Note: note, BatchAllocations: batchAllocs,
                AutoAllocateSerials: true, Actor: actor));
        StockValuationEngine.Apply(connection, transaction, provider,
            new StockMovementRequest(companyId.Trim(), head.WarehouseId, head.MaterialId, dateText,
                "TRANSFER_IN", docNo, QtyIn: qty, QtyOut: 0m,
                ReceiptCostAmount: -outbound.CostAmount,
                Note: note, SourceMovementId: outbound.MovementId, Actor: actor));
    }

    /// <summary>Issues <paramref name="qty"/> out of quarantine
    /// (scrap / return-to-supplier): the stock leaves inventory
    /// visibility entirely; scrapped serials are marked Scrapped.</summary>
    private static void IssueFromQuarantine(
        DbConnection connection, DbTransaction transaction, string provider,
        string companyId, InspectionHead head, decimal qty, string movementType,
        string dateText, string actor, string note, bool markSerialsScrapped)
    {
        var quarantineId = head.QuarantineWarehouseId!.Value;
        IReadOnlyList<BatchAllocationRequest>? batchAllocs = null;
        if (head.BatchId is long batchId)
        {
            var batchNo = BatchNo(connection, transaction, batchId);
            if (batchNo is not null)
                batchAllocs = new[] { new BatchAllocationRequest(batchNo, qty) };
        }
        var result = StockValuationEngine.Apply(connection, transaction, provider,
            new StockMovementRequest(companyId.Trim(), quarantineId, head.MaterialId, dateText,
                movementType, $"QC-{head.RefDocNo}", QtyIn: 0m, QtyOut: qty,
                Note: note, BatchAllocations: batchAllocs,
                AutoAllocateSerials: true, PartyId: head.PartyId, Actor: actor));
        if (markSerialsScrapped)
        {
            var isPg = GlPosting.NormalizeProvider(provider) == "PostgreSQL";
            foreach (var alloc in result.AllocationRecords.Where(a => a.SerialId is not null))
            {
                using var upd = GlPosting.CreateCommand(connection, transaction, $"""
                    UPDATE stock_serials SET status = 'Scrapped', current_warehouse_id = NULL,
                        updated_at = {(isPg ? "now()" : "datetime('now')")} WHERE serial_id = @id
                    """);
                GlPosting.Add(upd, "@id", alloc.SerialId!.Value);
                upd.ExecuteNonQuery();
            }
        }
    }

    /// <summary>Completes a Draft inspection inside the caller's
    /// transaction: validates the quantity split, moves stock per the
    /// disposition, writes the terminal status, and raises the
    /// failure alert for the rejected part. Returns the alert id.</summary>
    public static Guid? CompleteInTransaction(
        DbConnection connection, DbTransaction transaction, string provider,
        string companyId, Guid inspectionId, decimal acceptedQty, decimal rejectedQty,
        string? disposition, string inspector, string? notes, string dateText)
    {
        var prov = GlPosting.NormalizeProvider(provider);
        var isPg = prov == "PostgreSQL";
        var head = LoadHead(connection, transaction, prov, companyId, inspectionId)
            ?? throw new AppError(ErrorCodes.QualityInspectionStateInvalid,
                "The inspection does not exist in the current company.",
                inspectionId.ToString("D"));
        if (head.Status != QualityInspectionStatuses.Draft)
            throw new AppError(ErrorCodes.QualityInspectionStateInvalid,
                $"Inspection for document '{head.RefDocNo}' is already completed with status '{head.Status}'.",
                head.RefDocNo, head.Status);
        if (acceptedQty < 0 || rejectedQty < 0 || acceptedQty + rejectedQty != head.TotalQty)
            throw new AppError(ErrorCodes.QualityInspectionQtyMismatch,
                $"Accepted ({acceptedQty}) + rejected ({rejectedQty}) must equal the inspected quantity {head.TotalQty} for document '{head.RefDocNo}'.",
                head.RefDocNo);
        if (rejectedQty > 0 && string.IsNullOrWhiteSpace(disposition))
            throw new AppError(ErrorCodes.QualityInspectionQtyMismatch,
                $"A disposition is required for the rejected quantity of document '{head.RefDocNo}'.",
                head.RefDocNo);
        if (rejectedQty > 0 && !QualityDispositions.All.Contains(disposition!))
            throw new AppError(ErrorCodes.QualityInspectionQtyMismatch,
                $"Unknown disposition '{disposition}' for document '{head.RefDocNo}'.",
                head.RefDocNo);

        var status = QualityInspectionStatuses.FromQuantities(acceptedQty, rejectedQty);
        var holdsStock = head.QuarantineWarehouseId is not null && head.TotalQty > 0
            && head.InspectionType != QualityInspectionTypes.Outgoing;

        if (holdsStock)
        {
            if (acceptedQty > 0)
                ReleaseQuantity(connection, transaction, prov, companyId, head, acceptedQty,
                    dateText, inspector, $"Quality release {head.RefDocNo}");
            if (rejectedQty > 0)
            {
                switch (disposition)
                {
                    case QualityDispositions.UseAsIs:
                        ReleaseQuantity(connection, transaction, prov, companyId, head, rejectedQty,
                            dateText, inspector, $"Quality use-as-is release {head.RefDocNo}");
                        break;
                    case QualityDispositions.Scrap:
                        IssueFromQuarantine(connection, transaction, prov, companyId, head, rejectedQty,
                            "QCS", dateText, inspector, $"Quality scrap {head.RefDocNo}", markSerialsScrapped: true);
                        break;
                    case QualityDispositions.ReturnToSupplier:
                        IssueFromQuarantine(connection, transaction, prov, companyId, head, rejectedQty,
                            "QCR", dateText, inspector, $"Quality return to supplier {head.RefDocNo}", markSerialsScrapped: false);
                        break;
                    default:
                        // Rework / Quarantine: the quantity stays in the
                        // quarantine warehouse under the recorded
                        // disposition - visible, valued, unsellable.
                        break;
                }
            }
        }

        using (var upd = GlPosting.CreateCommand(connection, transaction, $"""
            UPDATE quality_inspections
            SET status = @status, accepted_qty = @acc, rejected_qty = @rej,
                disposition = @disp, inspector = @insp, inspected_at = @at,
                notes = @notes, updated_at = {(isPg ? "now()" : "datetime('now')")}, updated_by = @insp
            WHERE company_id = @c AND inspection_id = @id
            """))
        {
            GlPosting.Add(upd, "@status", status);
            GlPosting.Add(upd, "@acc", acceptedQty);
            GlPosting.Add(upd, "@rej", rejectedQty);
            GlPosting.Add(upd, "@disp", (object?)disposition ?? DBNull.Value);
            GlPosting.Add(upd, "@insp", inspector);
            GlPosting.Add(upd, "@at", dateText);
            GlPosting.Add(upd, "@notes", (object?)notes ?? DBNull.Value);
            GlPosting.Add(upd, "@c", companyId.Trim());
            GlPosting.Add(upd, "@id", GlPosting.FormatGuid(prov, inspectionId));
            upd.ExecuteNonQuery();
        }

        Guid? alertId = null;
        if (rejectedQty > 0)
        {
            alertId = Guid.NewGuid();
            InsertAlert(connection, transaction, prov, companyId, alertId.Value,
                QualityAlertTypes.FailedInspection, inspectionId, head.MaterialId, head.BatchId, head.PartyId,
                $"Inspection of document '{head.RefDocNo}' rejected {rejectedQty} (disposition: {disposition}).",
                owner: inspector, dueDate: ComputeDueDate(dateText), actor: inspector);
        }
        return alertId;
    }

    internal static void InsertAlert(
        DbConnection connection, DbTransaction transaction, string prov, string companyId,
        Guid alertId, string alertType, Guid? inspectionId, long? materialId, long? batchId,
        long? partyId, string message, string? owner, string? dueDate, string actor)
    {
        using var cmd = GlPosting.CreateCommand(connection, transaction, """
            INSERT INTO quality_alerts
                (alert_id, company_id, alert_type, inspection_id, material_id, batch_id,
                 party_id, message, status, owner, due_date, created_by)
            VALUES (@id, @c, @type, @insp, @m, @b, @party, @msg, 'Open', @owner, @due, @actor)
            """);
        GlPosting.Add(cmd, "@id", GlPosting.FormatGuid(prov, alertId));
        GlPosting.Add(cmd, "@c", companyId.Trim());
        GlPosting.Add(cmd, "@type", alertType);
        GlPosting.Add(cmd, "@insp", inspectionId is Guid g ? GlPosting.FormatGuid(prov, g) : DBNull.Value);
        GlPosting.Add(cmd, "@m", (object?)materialId ?? DBNull.Value);
        GlPosting.Add(cmd, "@b", (object?)batchId ?? DBNull.Value);
        GlPosting.Add(cmd, "@party", (object?)partyId ?? DBNull.Value);
        GlPosting.Add(cmd, "@msg", message);
        GlPosting.Add(cmd, "@owner", (object?)owner ?? DBNull.Value);
        GlPosting.Add(cmd, "@due", (object?)dueDate ?? DBNull.Value);
        GlPosting.Add(cmd, "@actor", actor);
        cmd.ExecuteNonQuery();
    }

    // ------------------------------------------------------------------
    // The sale gate + outgoing gate
    // ------------------------------------------------------------------
    public static decimal BalanceQty(
        DbConnection connection, DbTransaction transaction, string provider,
        string companyId, long warehouseId, long materialId)
    {
        var isPg = GlPosting.NormalizeProvider(provider) == "PostgreSQL";
        using var cmd = GlPosting.CreateCommand(connection, transaction, $"""
            SELECT COALESCE(available_qty, 0) FROM material_stock_balances
            WHERE company_id = @c AND warehouse_id = @w AND material_id = @m
              AND is_deleted = {(isPg ? "FALSE" : "0")} LIMIT 1
            """);
        GlPosting.Add(cmd, "@c", companyId.Trim());
        GlPosting.Add(cmd, "@w", warehouseId);
        GlPosting.Add(cmd, "@m", materialId);
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? 0m : Convert.ToDecimal(value, CultureInfo.InvariantCulture);
    }

    /// <summary>Decides whether an issue of <paramref name="qty"/> from
    /// <paramref name="warehouseId"/> may draw its shortfall from
    /// quarantine. Returns 0 when sellable stock covers the issue (or
    /// quarantine cannot cover the shortfall — the caller's normal
    /// insufficient-stock failure then applies). In Stop mode a
    /// coverable shortfall is refused with INV-6012; in Warn mode the
    /// shortfall is returned and a GateWarning alert is recorded.</summary>
    public static decimal PlanQuarantineDraw(
        DbConnection connection, DbTransaction transaction, string provider,
        string companyId, long warehouseId, long materialId, decimal qty,
        string documentNo, string actor)
    {
        var prov = GlPosting.NormalizeProvider(provider);
        if (!PartyLedger.TableExists(connection, transaction, prov, "quality_inspections"))
            return 0m;
        var sellable = BalanceQty(connection, transaction, prov, companyId, warehouseId, materialId);
        if (sellable >= qty) return 0m;
        var quarantineId = FindQuarantineWarehouse(connection, transaction, prov, companyId);
        if (quarantineId is null || quarantineId.Value == warehouseId) return 0m;
        var shortfall = qty - sellable;
        var quarantined = BalanceQty(connection, transaction, prov, companyId, quarantineId.Value, materialId);
        if (quarantined < shortfall) return 0m;

        var profile = LoadProfile(connection, transaction, prov, companyId, materialId);
        var mode = GetGateMode(connection, transaction, prov, companyId);
        if (mode == QualityGateModes.Stop)
            throw new AppError(ErrorCodes.QualityQuarantinedSale,
                $"Material '{profile.Code}' has {shortfall} of the requested {qty} in quarantine awaiting quality inspection; quarantined stock is not sellable in Stop gate mode (document '{documentNo}').",
                profile.Code, documentNo);
        InsertAlert(connection, transaction, prov, companyId, Guid.NewGuid(),
            QualityAlertTypes.GateWarning, null, materialId, null, null,
            $"Sale of document '{documentNo}' drew {shortfall} of material '{profile.Code}' from quarantine under Warn gate mode.",
            owner: actor, dueDate: null, actor: actor);
        return shortfall;
    }

    /// <summary>The outgoing gate (decision Q4): returns true when a
    /// pending outgoing inspection was raised in Warn mode. Throws
    /// INV-6011 when a REJECTED outgoing inspection exists for the
    /// document (both modes), INV-6013 in Stop mode when no accepted
    /// outgoing inspection exists.</summary>
    public static bool CheckOutgoing(
        DbConnection connection, DbTransaction transaction, string provider,
        string companyId, string refDocKind, string refDocId, string refDocNo,
        long materialId, long warehouseId, long? partyId, string movementDate, string actor)
    {
        var prov = GlPosting.NormalizeProvider(provider);
        var isPg = prov == "PostgreSQL";
        if (!PartyLedger.TableExists(connection, transaction, prov, "quality_inspections"))
            return false;
        int CountByStatus(string status)
        {
            using var cmd = GlPosting.CreateCommand(connection, transaction, $"""
                SELECT COUNT(*) FROM quality_inspections
                WHERE company_id = @c AND inspection_type = 'Outgoing' AND ref_doc_no = @doc
                  AND material_id = @m AND status = @status AND is_deleted = {(isPg ? "FALSE" : "0")}
                """);
            GlPosting.Add(cmd, "@c", companyId.Trim());
            GlPosting.Add(cmd, "@doc", refDocNo);
            GlPosting.Add(cmd, "@m", materialId);
            GlPosting.Add(cmd, "@status", status);
            return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        var profile = LoadProfile(connection, transaction, prov, companyId, materialId);
        if (CountByStatus(QualityInspectionStatuses.Rejected) > 0)
            throw new AppError(ErrorCodes.QualityGateBlocked,
                $"An outgoing quality inspection for material '{profile.Code}' on document '{refDocNo}' was REJECTED; the delivery is blocked.",
                profile.Code, refDocNo);
        if (CountByStatus(QualityInspectionStatuses.Accepted) > 0
            || CountByStatus(QualityInspectionStatuses.PartiallyAccepted) > 0)
            return false;
        var mode = GetGateMode(connection, transaction, prov, companyId);
        if (mode == QualityGateModes.Stop)
            throw new AppError(ErrorCodes.QualityOutgoingInspectionRequired,
                $"Material '{profile.Code}' requires an accepted outgoing quality inspection before document '{refDocNo}' can be delivered (Stop gate mode).",
                profile.Code, refDocNo);
        EnsureInspection(connection, transaction, prov, companyId,
            QualityInspectionTypes.Outgoing, refDocKind, refDocId, refDocNo,
            materialId, null, warehouseId, null, partyId, 0m, movementDate, actor);
        InsertAlert(connection, transaction, prov, companyId, Guid.NewGuid(),
            QualityAlertTypes.GateWarning, null, materialId, null, partyId,
            $"Document '{refDocNo}' was delivered without an outgoing inspection for material '{profile.Code}' under Warn gate mode.",
            owner: actor, dueDate: ComputeDueDate(movementDate), actor: actor);
        return true;
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------
    private static string? BatchNo(DbConnection connection, DbTransaction transaction, long batchId)
    {
        using var cmd = GlPosting.CreateCommand(connection, transaction,
            "SELECT batch_no FROM stock_batches WHERE batch_id = @b LIMIT 1");
        GlPosting.Add(cmd, "@b", batchId);
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    internal static string? ComputeDueDate(string? dateText)
    {
        if (!string.IsNullOrWhiteSpace(dateText)
            && DateTime.TryParseExact(dateText.Trim(), "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return date.AddDays(InspectionDueDays).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return null;
    }

    internal static bool Truthy(object? value) => value is not null and not DBNull
        && Convert.ToBoolean(value, CultureInfo.InvariantCulture);

    internal static Guid? ParseGuidOrNull(object? value)
    {
        if (value is null or DBNull) return null;
        if (value is Guid g) return g;
        var text = Convert.ToString(value, CultureInfo.InvariantCulture);
        return Guid.TryParse(text, out var parsed) ? parsed : null;
    }
}
