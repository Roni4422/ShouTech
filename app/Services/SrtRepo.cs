using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.App.Reporting.Invoices;
using ShouTech.Domain;
using ShouTech.Infrastructure;

namespace ShouTech.App.Services;

/// <summary>
/// Sales-returns completion workflows (W6, migration 100): the return
/// reasons master, the per-company return settings (approval flag +
/// inspection amount threshold), the inspection decision, the
/// SALES.RETURN.APPROVE approval workflow, and the two creation paths
/// that do not start from a posted invoice — authorized no-invoice
/// (walk-in) returns and returns sourced from a posted, uninvoiced
/// delivery note. Document persistence itself stays in InvRepo; this
/// service owns only the workflow state around it (the same split as
/// PosPosting over InvRepo). Permission checks follow the Phase-3
/// service pattern: the caller may inject a permission predicate;
/// the default is the live session's AppHost.Permissions.
/// </summary>
public static class SrtRepo
{
    private static string Company => AppState.CurrentCompanyCode ?? string.Empty;
    private static string Provider => DatabaseRuntime.IsPostgreSql() ? "PostgreSQL" : "SQLite";
    private static bool IsPg => DatabaseRuntime.IsPostgreSql();

    public sealed record SrtReason(
        string Code, string NameEn, string NameAr, string NameFr, string NameTr, string DefaultCondition)
    {
        /// <summary>Display name for a UI language code (master-data
        /// pattern: fall back to the English source).</summary>
        public string DisplayName(string? languageCode) => languageCode?.ToLowerInvariant() switch
        {
            "ar" when !string.IsNullOrWhiteSpace(NameAr) => NameAr,
            "fr" when !string.IsNullOrWhiteSpace(NameFr) => NameFr,
            "tr" when !string.IsNullOrWhiteSpace(NameTr) => NameTr,
            _ => NameEn
        };
    }

    public sealed record SrtSettings(bool ApprovalRequired, decimal? InspectionAmountThreshold);

    /// <summary>The default settings: approval OFF, no amount
    /// threshold (inspection is then required only by Quarantine /
    /// Damaged lines) — the pre-W6 behaviour.</summary>
    public static readonly SrtSettings DefaultSettings = new(false, null);

    /// <summary>A posted delivery note offered as a return source.</summary>
    public sealed record SrtDelivery(
        long DocId, string DocNo, DateTime DocDate, long? PartyId, string PartyCode,
        string PartyName, int? WarehouseId, string WarehouseName, string CurrencyCode,
        decimal ExchangeRate, List<InvLine> Lines);

    // ------------------------------------------------------------------
    // Reasons master
    // ------------------------------------------------------------------

    /// <summary>Active return reasons for the current company: the
    /// company's own rows shadow the global seeds (company_id = '')
    /// with the same code.</summary>
    public static IReadOnlyList<SrtReason> ListReasons()
    {
        var cs = DatabaseRuntime.GetConnectionString()
            ?? throw new InvalidOperationException("No database connection.");
        using var connection = Open(cs);
        var result = new List<SrtReason>();
        if (!PartyLedger.TableExists(connection, null, Provider, "srt_return_reasons"))
            return result;
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT code, name_en, name_ar, name_fr, name_tr, default_condition
            FROM srt_return_reasons
            WHERE (company_id = $company OR company_id = '')
              AND is_active = {(IsPg ? "TRUE" : "1")} AND is_deleted = {(IsPg ? "FALSE" : "0")}
            ORDER BY CASE WHEN company_id = $company THEN 0 ELSE 1 END, code;
            """;
        Add(cmd, "$company", Company);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var code = ReadText(reader, 0);
            if (!seen.Add(code)) continue; // company row already won
            result.Add(new SrtReason(code, ReadText(reader, 1), ReadText(reader, 2),
                ReadText(reader, 3), ReadText(reader, 4),
                string.IsNullOrWhiteSpace(ReadText(reader, 5)) ? "Good" : ReadText(reader, 5)));
        }
        result.Sort((a, b) => string.Compare(a.Code, b.Code, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    // ------------------------------------------------------------------
    // Company settings
    // ------------------------------------------------------------------

    public static SrtSettings GetSettings()
    {
        var cs = DatabaseRuntime.GetConnectionString()
            ?? throw new InvalidOperationException("No database connection.");
        using var connection = Open(cs);
        return ReadSettings(connection, null);
    }

    internal static SrtSettings ReadSettings(DbConnection connection, DbTransaction? tx)
    {
        if (!PartyLedger.TableExists(connection, tx, Provider, "srt_return_settings"))
            return DefaultSettings;
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT approval_required, inspection_amount_threshold FROM srt_return_settings WHERE company_id = $company;";
        Add(cmd, "$company", Company);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return DefaultSettings;
        var approval = reader.GetValue(0) is bool flag
            ? flag
            : Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture) != 0;
        var threshold = reader.IsDBNull(1) ? (decimal?)null : Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture);
        return new SrtSettings(approval, threshold);
    }

    public static void SaveSettings(SrtSettings settings, string userId)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.InspectionAmountThreshold is < 0m)
            throw new ArgumentOutOfRangeException(nameof(settings), "The inspection amount threshold cannot be negative.");
        var cs = DatabaseRuntime.GetConnectionString()
            ?? throw new InvalidOperationException("No database connection.");
        using var connection = Open(cs);
        using var tx = connection.BeginTransaction();
        if (!PartyLedger.TableExists(connection, tx, Provider, "srt_return_settings"))
            throw new InvalidOperationException("Return settings are unavailable: apply migration 100 first.");
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = IsPg
            ? """
              INSERT INTO srt_return_settings (company_id, approval_required, inspection_amount_threshold, updated_at, updated_by)
              VALUES ($company, $approval, $threshold, NOW(), $user)
              ON CONFLICT (company_id) DO UPDATE SET approval_required = EXCLUDED.approval_required,
                  inspection_amount_threshold = EXCLUDED.inspection_amount_threshold,
                  updated_at = NOW(), updated_by = EXCLUDED.updated_by;
              """
            : """
              INSERT INTO srt_return_settings (company_id, approval_required, inspection_amount_threshold, updated_at, updated_by)
              VALUES ($company, $approval, $threshold, datetime('now'), $user)
              ON CONFLICT (company_id) DO UPDATE SET approval_required = excluded.approval_required,
                  inspection_amount_threshold = excluded.inspection_amount_threshold,
                  updated_at = datetime('now'), updated_by = excluded.updated_by;
              """;
        Add(cmd, "$company", Company);
        Add(cmd, "$approval", settings.ApprovalRequired ? (IsPg ? (object)true : 1) : (IsPg ? (object)false : 0));
        Add(cmd, "$threshold", settings.InspectionAmountThreshold);
        Add(cmd, "$user", userId);
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    // ------------------------------------------------------------------
    // Inspection + approval decisions (on Draft returns)
    // ------------------------------------------------------------------

    /// <summary>Records the inspection decision for a Draft return:
    /// per-line condition confirmations / overrides (keyed by line
    /// number) and the inspector stamp; the header moves to Inspected.
    /// Lines absent from the map keep their entered condition.</summary>
    public static void RecordInspection(long docId, IReadOnlyDictionary<int, string> conditionByLine, string inspector)
    {
        ArgumentNullException.ThrowIfNull(conditionByLine);
        if (string.IsNullOrWhiteSpace(inspector))
            throw new ArgumentException("The inspector is required.", nameof(inspector));
        foreach (var condition in conditionByLine.Values)
            if (condition is not ("Good" or "Quarantine" or "Damaged"))
                throw new ArgumentException($"Unknown return condition '{condition}'.", nameof(conditionByLine));

        var cs = DatabaseRuntime.GetConnectionString()
            ?? throw new InvalidOperationException("No database connection.");
        using var connection = Open(cs);
        using var tx = connection.BeginTransaction();
        EnsureDraftReturn(connection, tx, docId);
        foreach (var (lineNo, condition) in conditionByLine)
        {
            using var upd = connection.CreateCommand();
            upd.Transaction = tx;
            upd.CommandText = "UPDATE sales_document_lines SET return_condition = $condition WHERE doc_id = $id AND line_no = $line;";
            Add(upd, "$condition", condition);
            Add(upd, "$id", docId);
            Add(upd, "$line", lineNo);
            if (upd.ExecuteNonQuery() == 0)
                throw new InvalidOperationException($"Return line {lineNo} does not exist on this return.");
        }
        using (var head = connection.CreateCommand())
        {
            head.Transaction = tx;
            head.CommandText = IsPg
                ? "UPDATE sales_documents SET inspection_status = 'Inspected', inspected_by = $by, inspected_at = NOW() WHERE doc_id = $id AND (company_id = $company OR company_id = $guid);"
                : "UPDATE sales_documents SET inspection_status = 'Inspected', inspected_by = $by, inspected_at = datetime('now') WHERE doc_id = $id AND (company_id = $company OR company_id = $guid);";
            Add(head, "$by", inspector.Trim());
            Add(head, "$id", docId);
            Add(head, "$company", Company);
            Add(head, "$guid", ResolveGuidKey(connection, tx));
            if (head.ExecuteNonQuery() == 0)
                throw new InvalidOperationException("Return document not found in the current company.");
        }
        tx.Commit();
    }

    /// <summary>Approves a Draft return that is awaiting approval.
    /// Requires SALES.RETURN.APPROVE.</summary>
    public static void Approve(long docId, string userId, Func<string, bool>? permissionCheck = null)
    {
        EnsureApprovePermission(permissionCheck);
        var cs = DatabaseRuntime.GetConnectionString()
            ?? throw new InvalidOperationException("No database connection.");
        using var connection = Open(cs);
        using var tx = connection.BeginTransaction();
        EnsureDraftReturn(connection, tx, docId);
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = IsPg
            ? "UPDATE sales_documents SET approval_status = 'Approved', approved_by = $by, approved_at = NOW(), rejection_reason = NULL WHERE doc_id = $id AND (company_id = $company OR company_id = $guid) AND approval_status IN ('PendingApproval', 'Rejected');"
            : "UPDATE sales_documents SET approval_status = 'Approved', approved_by = $by, approved_at = datetime('now'), rejection_reason = NULL WHERE doc_id = $id AND (company_id = $company OR company_id = $guid) AND approval_status IN ('PendingApproval', 'Rejected');";
        Add(cmd, "$by", userId);
        Add(cmd, "$id", docId);
        Add(cmd, "$company", Company);
        Add(cmd, "$guid", ResolveGuidKey(connection, tx));
        if (cmd.ExecuteNonQuery() == 0)
            throw new InvalidOperationException("This return is not awaiting approval (the approval flag may be off, or it was already decided).");
        tx.Commit();
    }

    /// <summary>Rejects a Draft return awaiting approval; a reason is
    /// mandatory. Requires SALES.RETURN.APPROVE.</summary>
    public static void Reject(long docId, string userId, string reason, Func<string, bool>? permissionCheck = null)
    {
        EnsureApprovePermission(permissionCheck);
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("A rejection reason is required.", nameof(reason));
        var cs = DatabaseRuntime.GetConnectionString()
            ?? throw new InvalidOperationException("No database connection.");
        using var connection = Open(cs);
        using var tx = connection.BeginTransaction();
        EnsureDraftReturn(connection, tx, docId);
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = IsPg
            ? "UPDATE sales_documents SET approval_status = 'Rejected', approved_by = $by, approved_at = NOW(), rejection_reason = $reason WHERE doc_id = $id AND (company_id = $company OR company_id = $guid) AND approval_status = 'PendingApproval';"
            : "UPDATE sales_documents SET approval_status = 'Rejected', approved_by = $by, approved_at = datetime('now'), rejection_reason = $reason WHERE doc_id = $id AND (company_id = $company OR company_id = $guid) AND approval_status = 'PendingApproval';";
        Add(cmd, "$by", userId);
        Add(cmd, "$reason", reason.Trim());
        Add(cmd, "$id", docId);
        Add(cmd, "$company", Company);
        Add(cmd, "$guid", ResolveGuidKey(connection, tx));
        if (cmd.ExecuteNonQuery() == 0)
            throw new InvalidOperationException("This return is not awaiting approval.");
        tx.Commit();
    }

    // ------------------------------------------------------------------
    // No-invoice + delivery-sourced creation paths
    // ------------------------------------------------------------------

    /// <summary>Creates a Draft no-invoice (walk-in) return. Permitted
    /// only with SALES.RETURN.APPROVE: the credit goes to the party
    /// ledger only — there is no invoice to settle and no cash refund
    /// (PaidAmount must be 0; InvRepo.Post re-validates the shape).</summary>
    public static long CreateNoInvoiceReturn(InvDoc doc, Func<string, bool>? permissionCheck = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        EnsureApprovePermission(permissionCheck);
        if (doc.Kind != InvDocKind.SalesReturn)
            throw new InvalidOperationException("A no-invoice return must be a sales return document.");
        if (doc.PartyId is null or <= 0)
            throw new InvalidOperationException("A no-invoice return requires the customer it credits.");
        if (doc.PaidAmount != 0m)
            throw new InvalidOperationException("A no-invoice return is a credit note only; cash refunds require an original invoice.");
        doc.OriginalDocId = null;
        doc.OriginalDocKind = null;
        doc.OriginalDocNo = string.Empty;
        doc.NoInvoice = true;
        doc.ReturnSourceKind = "NONE";
        if (string.IsNullOrWhiteSpace(doc.ReturnReasonCode))
            throw new InvalidOperationException("A return reason is required.");
        InvRepo.SaveDraft(doc);
        return doc.DocId;
    }

    /// <summary>Loads a posted delivery note (doc_kind 'DLV') as a
    /// return source: header + lines from the chain document tables.</summary>
    public static SrtDelivery? LoadDelivery(string docNo)
    {
        if (string.IsNullOrWhiteSpace(docNo)) return null;
        var cs = DatabaseRuntime.GetConnectionString()
            ?? throw new InvalidOperationException("No database connection.");
        using var connection = Open(cs);
        var guidKey = ResolveGuidKey(connection, null);
        long docId;
        DateTime docDate;
        long? partyId;
        string partyCode, partyName, currency, warehouseName;
        int? warehouseId;
        decimal exchangeRate;
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT doc_id, doc_date, party_id, party_code, party_name, warehouse_id, warehouse_name, currency_code, exchange_rate
                FROM sales_documents
                WHERE doc_no = $no AND doc_kind = 'DLV' AND status = 'Posted'
                  AND (company_id = $company OR company_id = $guid) AND is_deleted = {(IsPg ? "FALSE" : "0")}
                LIMIT 1;
                """;
            Add(cmd, "$no", docNo.Trim());
            Add(cmd, "$company", Company);
            Add(cmd, "$guid", guidKey);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            docId = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
            docDate = DateTime.Parse(ReadText(reader, 1), CultureInfo.InvariantCulture);
            partyId = reader.IsDBNull(2) ? null : Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture);
            partyCode = ReadText(reader, 3);
            partyName = ReadText(reader, 4);
            warehouseId = reader.IsDBNull(5) ? null : Convert.ToInt32(reader.GetValue(5), CultureInfo.InvariantCulture);
            warehouseName = ReadText(reader, 6);
            currency = ReadText(reader, 7);
            exchangeRate = reader.IsDBNull(8) ? 1m : Convert.ToDecimal(reader.GetValue(8), CultureInfo.InvariantCulture);
        }
        var lines = new List<InvLine>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT line_no, material_id, material_code, description, unit, qty, unit_price,
                       discount_pct, warehouse_id, warehouse_name
                FROM sales_document_lines WHERE doc_id = $id ORDER BY line_no;
                """;
            Add(cmd, "$id", docId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                lines.Add(new InvLine
                {
                    LineNo = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture),
                    MaterialId = reader.IsDBNull(1) ? null : Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                    MaterialCode = ReadText(reader, 2),
                    Description = ReadText(reader, 3),
                    Unit = ReadText(reader, 4),
                    Qty = Convert.ToDecimal(reader.GetValue(5), CultureInfo.InvariantCulture),
                    UnitPrice = Convert.ToDecimal(reader.GetValue(6), CultureInfo.InvariantCulture),
                    DiscountPct = reader.IsDBNull(7) ? 0m : Convert.ToDecimal(reader.GetValue(7), CultureInfo.InvariantCulture),
                    WarehouseId = reader.IsDBNull(8) ? null : Convert.ToInt32(reader.GetValue(8), CultureInfo.InvariantCulture),
                    WarehouseName = ReadText(reader, 9)
                });
            }
        }
        return new SrtDelivery(docId, docNo.Trim(), docDate, partyId, partyCode, partyName,
            warehouseId, warehouseName, currency, exchangeRate, lines);
    }

    /// <summary>Quantities already returned against a delivery, by
    /// delivery line number (posted SRTs with return_source_kind
    /// 'DLV').</summary>
    public static IReadOnlyDictionary<int, decimal> GetAlreadyReturnedForDelivery(long deliveryDocId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deliveryDocId);
        var cs = DatabaseRuntime.GetConnectionString()
            ?? throw new InvalidOperationException("No database connection.");
        using var connection = Open(cs);
        var guidKey = ResolveGuidKey(connection, null);
        var result = new Dictionary<int, decimal>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT line.line_no, COALESCE(SUM(line.qty), 0)
            FROM sales_documents header
            INNER JOIN sales_document_lines line ON line.doc_id = header.doc_id
            WHERE (header.company_id = $company OR header.company_id = $guid)
              AND header.doc_kind = 'SRT' AND header.return_source_kind = 'DLV'
              AND header.original_doc_id = $id AND header.status = 'Posted'
              AND header.is_deleted = {(IsPg ? "FALSE" : "0")}
            GROUP BY line.line_no;
            """;
        Add(cmd, "$company", Company);
        Add(cmd, "$guid", guidKey);
        Add(cmd, "$id", deliveryDocId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result[Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture)] =
                Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture);
        return result;
    }

    /// <summary>Saves a Draft return sourced from a delivery and
    /// restores the delivery's line numbers on the stored lines (the
    /// SalesReturnWin / PosPosting pattern), so cumulative-return
    /// accounting keys on delivery lines.</summary>
    public static long SaveDeliveryReturn(InvDoc doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (doc.Kind != InvDocKind.SalesReturn || doc.ReturnSourceKind != "DLV" || doc.OriginalDocId is not > 0)
            throw new InvalidOperationException("A delivery return must reference its source delivery.");
        if (string.IsNullOrWhiteSpace(doc.ReturnReasonCode))
            throw new InvalidOperationException("A return reason is required.");
        var originalLineNumbers = new List<int>();
        foreach (var line in doc.Lines) originalLineNumbers.Add(line.LineNo);
        InvRepo.SaveDraft(doc);
        InvRepo.RestoreSalesReturnLineNumbers(doc.DocId, originalLineNumbers);
        for (var i = 0; i < doc.Lines.Count; i++)
            doc.Lines[i].LineNo = originalLineNumbers[i];
        return doc.DocId;
    }

    // ------------------------------------------------------------------
    // RMA requests (migration 105, sales Level 3)
    // ------------------------------------------------------------------

    /// <summary>One requested RMA line. SourceLineNo is the line number
    /// on the source invoice / delivery the line refers to (0 when the
    /// RMA has no source document); the remaining fields snapshot what
    /// was requested so the request stays readable even if the source
    /// document is later amended.</summary>
    public sealed record RmaLine(
        int SourceLineNo, long? MaterialId, string MaterialCode, string Description,
        string Unit, decimal Qty, decimal UnitPrice);

    /// <summary>One RMA request (srt_rma_requests). Status flow:
    /// Requested -> Approved / Rejected (SALES.RETURN.APPROVE) ->
    /// Converted, when ConvertRma has created the DRAFT sales return
    /// stamped in LinkedReturnDocId.</summary>
    public sealed record RmaRequest(
        long RmaId, string RmaNo, long? PartyId, string PartyName,
        string SourceKind, long? SourceDocId, string SourceDocNo,
        string ReasonCode, string? Notes, string Status,
        string? RequestedBy, string? DecidedBy, string? DecidedAt, string? DecisionNote,
        long? LinkedReturnDocId, List<RmaLine> Lines);

    public static class RmaStatuses
    {
        public const string Requested = "Requested";
        public const string Approved = "Approved";
        public const string Rejected = "Rejected";
        public const string Converted = "Converted";
    }

    /// <summary>Creates an RMA request in status Requested. The reason
    /// must be a live W6 return reason; an INV / DLV request must name
    /// its source document; a NONE request must name the party. Lines
    /// are stored as one JSON snapshot (migration 098 precedent).</summary>
    public static long CreateRma(
        long? partyId, string partyName, string sourceKind, long? sourceDocId, string sourceDocNo,
        string reasonCode, string? notes, IReadOnlyList<RmaLine> lines, string requestedBy)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (string.IsNullOrWhiteSpace(requestedBy))
            throw new ArgumentException("The requesting user is required.", nameof(requestedBy));
        if (sourceKind is not ("INV" or "DLV" or "NONE"))
            throw new ArgumentException($"Unknown RMA source kind '{sourceKind}'.", nameof(sourceKind));
        if (sourceKind is "INV" or "DLV" && sourceDocId is not > 0)
            throw new InvalidOperationException("An invoice/delivery RMA must reference its source document.");
        if (sourceKind == "NONE" && partyId is not > 0)
            throw new InvalidOperationException("A no-source RMA must name the customer asking for the return.");
        if (lines.Count == 0)
            throw new InvalidOperationException("An RMA request needs at least one requested line.");
        foreach (var line in lines)
        {
            if (line.Qty <= 0m)
                throw new InvalidOperationException("Every RMA line quantity must be positive.");
            if (line.UnitPrice < 0m)
                throw new InvalidOperationException("An RMA line price cannot be negative.");
        }
        var reason = ListReasons().FirstOrDefault(r => string.Equals(r.Code, reasonCode, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Return reason '{reasonCode}' is not a live return reason.");

        var cs = DatabaseRuntime.GetConnectionString()
            ?? throw new InvalidOperationException("No database connection.");
        using var connection = Open(cs);
        using var tx = connection.BeginTransaction();
        if (!PartyLedger.TableExists(connection, tx, Provider, "srt_rma_requests"))
            throw new InvalidOperationException("RMA requests are unavailable: apply migration 105 first.");
        long rmaId;
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = IsPg
                ? """
                  INSERT INTO srt_rma_requests (company_id, party_id, party_name, source_kind, source_doc_id, source_doc_no,
                      reason_code, notes, lines_json, status, requested_by, created_by)
                  VALUES ($company, $party, $partyName, $kind, $docId, $docNo, $reason, $notes, $lines, 'Requested', $user, $user)
                  RETURNING rma_id;
                  """
                : """
                  INSERT INTO srt_rma_requests (company_id, party_id, party_name, source_kind, source_doc_id, source_doc_no,
                      reason_code, notes, lines_json, status, requested_by, created_by)
                  VALUES ($company, $party, $partyName, $kind, $docId, $docNo, $reason, $notes, $lines, 'Requested', $user, $user);
                  SELECT last_insert_rowid();
                  """;
            Add(cmd, "$company", Company);
            Add(cmd, "$party", partyId);
            Add(cmd, "$partyName", partyName?.Trim() ?? string.Empty);
            Add(cmd, "$kind", sourceKind);
            Add(cmd, "$docId", sourceDocId);
            Add(cmd, "$docNo", sourceDocNo?.Trim() ?? string.Empty);
            Add(cmd, "$reason", reason.Code);
            Add(cmd, "$notes", string.IsNullOrWhiteSpace(notes) ? null : notes.Trim());
            Add(cmd, "$lines", JsonSerializer.Serialize(lines));
            Add(cmd, "$user", requestedBy.Trim());
            rmaId = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        using (var stamp = connection.CreateCommand())
        {
            stamp.Transaction = tx;
            stamp.CommandText = "UPDATE srt_rma_requests SET rma_no = $no WHERE rma_id = $id AND company_id = $company;";
            Add(stamp, "$no", $"RMA-{rmaId}");
            Add(stamp, "$id", rmaId);
            Add(stamp, "$company", Company);
            stamp.ExecuteNonQuery();
        }
        tx.Commit();
        return rmaId;
    }

    public static IReadOnlyList<RmaRequest> ListRma(string? status = null)
    {
        var cs = DatabaseRuntime.GetConnectionString()
            ?? throw new InvalidOperationException("No database connection.");
        using var connection = Open(cs);
        var result = new List<RmaRequest>();
        if (!PartyLedger.TableExists(connection, null, Provider, "srt_rma_requests"))
            return result;
        using var cmd = connection.CreateCommand();
        var sql = $"""
            SELECT rma_id, rma_no, party_id, party_name, source_kind, source_doc_id, source_doc_no,
                   reason_code, notes, status, requested_by, decided_by, decided_at, decision_note,
                   linked_return_doc_id, lines_json
            FROM srt_rma_requests
            WHERE company_id = $company AND is_deleted = {(IsPg ? "FALSE" : "0")}
            """;
        if (!string.IsNullOrWhiteSpace(status)) sql += " AND status = $status";
        sql += " ORDER BY created_at DESC, rma_id DESC;";
        cmd.CommandText = sql;
        Add(cmd, "$company", Company);
        if (!string.IsNullOrWhiteSpace(status)) Add(cmd, "$status", status.Trim());
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(ReadRma(reader));
        return result;
    }

    public static RmaRequest? GetRma(long rmaId)
    {
        var cs = DatabaseRuntime.GetConnectionString()
            ?? throw new InvalidOperationException("No database connection.");
        using var connection = Open(cs);
        return ReadRma(connection, null, rmaId);
    }

    /// <summary>Approves or rejects a Requested RMA. Requires
    /// SALES.RETURN.APPROVE (the W6 permission — no duplicate key).
    /// Deciding an RMA that is not Requested is SAL-7025.</summary>
    public static void DecideRma(long rmaId, bool approve, string userId, string? note, Func<string, bool>? permissionCheck = null)
    {
        EnsureApprovePermission(permissionCheck);
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("The deciding user is required.", nameof(userId));
        if (!approve && string.IsNullOrWhiteSpace(note))
            throw new ArgumentException("Rejecting an RMA requires a decision note.", nameof(note));
        var cs = DatabaseRuntime.GetConnectionString()
            ?? throw new InvalidOperationException("No database connection.");
        using var connection = Open(cs);
        using var tx = connection.BeginTransaction();
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = IsPg
            ? """
              UPDATE srt_rma_requests SET status = $status, decided_by = $by, decided_at = NOW()::text,
                  decision_note = $note, updated_by = $by, updated_at = NOW()
              WHERE rma_id = $id AND company_id = $company AND status = 'Requested' AND is_deleted = FALSE;
              """
            : """
              UPDATE srt_rma_requests SET status = $status, decided_by = $by, decided_at = datetime('now'),
                  decision_note = $note, updated_by = $by, updated_at = datetime('now')
              WHERE rma_id = $id AND company_id = $company AND status = 'Requested' AND is_deleted = 0;
              """;
        Add(cmd, "$status", approve ? RmaStatuses.Approved : RmaStatuses.Rejected);
        Add(cmd, "$by", userId.Trim());
        Add(cmd, "$note", string.IsNullOrWhiteSpace(note) ? null : note.Trim());
        Add(cmd, "$id", rmaId);
        Add(cmd, "$company", Company);
        if (cmd.ExecuteNonQuery() == 0)
            throw new AppError(ErrorCodes.RmaStateInvalid,
                "Only a Requested RMA can be decided; this RMA was already decided or converted.",
                rmaId.ToString(CultureInfo.InvariantCulture));
        tx.Commit();
    }

    /// <summary>Converts an Approved RMA into a DRAFT sales return
    /// through the existing returns machinery — the SAME draft paths
    /// SalesReturnWin uses (InvRepo.SaveDraft + line-number restore for
    /// invoice sources, SrtRepo.SaveDeliveryReturn for deliveries,
    /// SrtRepo.CreateNoInvoiceReturn for source-less requests) — and
    /// stamps the link on the RMA. Converting a non-Approved RMA, or
    /// converting twice, is SAL-7025. Requires SALES.RETURN.APPROVE.
    /// The return is a DRAFT: posting (with its inspection / approval /
    /// quantity gates) stays the return window's job.</summary>
    public static long ConvertRma(long rmaId, string userId, Func<string, bool>? permissionCheck = null)
    {
        EnsureApprovePermission(permissionCheck);
        var rma = GetRma(rmaId)
            ?? throw new AppError(ErrorCodes.RmaStateInvalid, "The RMA request does not exist for this company.",
                rmaId.ToString(CultureInfo.InvariantCulture));
        if (rma.Status != RmaStatuses.Approved || rma.LinkedReturnDocId is not null)
            throw new AppError(ErrorCodes.RmaStateInvalid,
                $"RMA {rma.RmaNo} cannot be converted from status '{rma.Status}'" +
                (rma.LinkedReturnDocId is not null ? $" (already converted to return document {rma.LinkedReturnDocId})." : "."),
                rma.RmaNo);

        var reason = ListReasons().FirstOrDefault(r => string.Equals(r.Code, rma.ReasonCode, StringComparison.OrdinalIgnoreCase));
        var doc = new InvDoc
        {
            Kind = InvDocKind.SalesReturn,
            DocNo = InvRepo.NextDocNo(InvDocKind.SalesReturn),
            DocDate = DateTime.Today,
            ReturnReasonCode = rma.ReasonCode,
            PaymentMethod = InvPaymentMethod.Credit,
            Notes = string.IsNullOrWhiteSpace(rma.Notes) ? $"RMA {rma.RmaNo}" : $"RMA {rma.RmaNo}: {rma.Notes}"
        };

        long returnDocId;
        switch (rma.SourceKind)
        {
            case "INV":
            {
                var original = InvRepo.Load(rma.SourceDocId!.Value, InvDocKind.SalesInvoice)
                    ?? throw new InvalidOperationException($"Source invoice {rma.SourceDocNo} was not found.");
                if (original.Status != InvDocStatus.Posted)
                    throw new InvalidOperationException($"Source invoice {rma.SourceDocNo} is not posted; an RMA return needs a posted source.");
                doc.PartyId = original.PartyId;
                doc.PartyCode = original.PartyCode;
                doc.PartyName = original.PartyName;
                doc.WarehouseId = original.WarehouseId;
                doc.WarehouseName = original.WarehouseName;
                doc.CurrencyCode = original.CurrencyCode;
                doc.ExchangeRate = original.ExchangeRate;
                doc.TaxCode = original.TaxCode;
                doc.DefaultTaxRate = original.DefaultTaxRate;
                doc.OriginalDocId = original.DocId;
                doc.OriginalDocKind = InvDocKind.SalesInvoice;
                doc.OriginalDocNo = original.DocNo;
                doc.ReturnSourceKind = "INV";
                foreach (var line in rma.Lines)
                {
                    // Lines normally key on the source line number; a
                    // line without one (warranty-claim RMAs know the
                    // material, not the invoice line) matches the first
                    // source line carrying that material.
                    var source = (line.SourceLineNo > 0
                            ? original.Lines.FirstOrDefault(l => l.LineNo == line.SourceLineNo)
                            : original.Lines.FirstOrDefault(l => l.MaterialId == line.MaterialId))
                        ?? throw new InvalidOperationException(
                            $"RMA line refers to source line {line.SourceLineNo}, which is not on invoice {original.DocNo}.");
                    doc.Lines.Add(new InvLine
                    {
                        LineNo = source.LineNo,
                        MaterialId = source.MaterialId,
                        MaterialCode = source.MaterialCode,
                        Description = source.Description,
                        Unit = source.Unit,
                        Qty = line.Qty,
                        UnitPrice = source.UnitPrice,
                        DiscountPct = source.DiscountPct,
                        TaxCode = source.TaxCode,
                        TaxRate = source.TaxRate,
                        TaxCalculationMode = source.TaxCalculationMode,
                        WarehouseId = source.WarehouseId,
                        WarehouseName = source.WarehouseName,
                        ReturnCondition = reason?.DefaultCondition ?? "Good"
                    });
                }
                var originalLineNumbers = doc.Lines.Select(l => l.LineNo).ToList();
                InvRepo.SaveDraft(doc);
                InvRepo.RestoreSalesReturnLineNumbers(doc.DocId, originalLineNumbers);
                returnDocId = doc.DocId;
                break;
            }
            case "DLV":
            {
                var delivery = LoadDelivery(rma.SourceDocNo)
                    ?? throw new InvalidOperationException($"Source delivery {rma.SourceDocNo} was not found or is not posted.");
                doc.PartyId = delivery.PartyId;
                doc.PartyCode = delivery.PartyCode;
                doc.PartyName = delivery.PartyName;
                doc.WarehouseId = delivery.WarehouseId;
                doc.WarehouseName = delivery.WarehouseName;
                doc.CurrencyCode = delivery.CurrencyCode;
                doc.ExchangeRate = delivery.ExchangeRate;
                doc.OriginalDocId = delivery.DocId;
                doc.OriginalDocNo = delivery.DocNo;
                doc.ReturnSourceKind = "DLV";
                foreach (var line in rma.Lines)
                {
                    var source = (line.SourceLineNo > 0
                            ? delivery.Lines.FirstOrDefault(l => l.LineNo == line.SourceLineNo)
                            : delivery.Lines.FirstOrDefault(l => l.MaterialId == line.MaterialId))
                        ?? throw new InvalidOperationException(
                            $"RMA line refers to source line {line.SourceLineNo}, which is not on delivery {delivery.DocNo}.");
                    doc.Lines.Add(new InvLine
                    {
                        LineNo = source.LineNo,
                        MaterialId = source.MaterialId,
                        MaterialCode = source.MaterialCode,
                        Description = source.Description,
                        Unit = source.Unit,
                        Qty = line.Qty,
                        UnitPrice = source.UnitPrice,
                        DiscountPct = source.DiscountPct,
                        WarehouseId = source.WarehouseId,
                        WarehouseName = source.WarehouseName,
                        ReturnCondition = reason?.DefaultCondition ?? "Good"
                    });
                }
                returnDocId = SaveDeliveryReturn(doc);
                break;
            }
            default: // NONE
            {
                doc.PartyId = rma.PartyId;
                doc.PartyName = rma.PartyName;
                foreach (var line in rma.Lines)
                {
                    if (string.IsNullOrWhiteSpace(line.MaterialCode))
                        throw new InvalidOperationException("A source-less RMA line must name the material being returned.");
                    doc.Lines.Add(new InvLine
                    {
                        LineNo = line.SourceLineNo > 0 ? line.SourceLineNo : doc.Lines.Count + 1,
                        MaterialId = line.MaterialId,
                        MaterialCode = line.MaterialCode,
                        Description = line.Description,
                        Unit = line.Unit,
                        Qty = line.Qty,
                        UnitPrice = line.UnitPrice,
                        ReturnCondition = reason?.DefaultCondition ?? "Good"
                    });
                }
                returnDocId = CreateNoInvoiceReturn(doc, permissionCheck);
                break;
            }
        }

        // Claim the conversion: the guarded UPDATE is the second line
        // of defence against a double convert (the status read above
        // is the first). If it claims nothing, the RMA was converted
        // concurrently — SAL-7025, and the just-created draft remains
        // as an ordinary editable draft the user can delete.
        var cs = DatabaseRuntime.GetConnectionString()
            ?? throw new InvalidOperationException("No database connection.");
        using (var connection = Open(cs))
        using (var tx = connection.BeginTransaction())
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = IsPg
                ? """
                  UPDATE srt_rma_requests SET status = 'Converted', linked_return_doc_id = $doc,
                      updated_by = $by, updated_at = NOW()
                  WHERE rma_id = $id AND company_id = $company AND status = 'Approved'
                    AND linked_return_doc_id IS NULL AND is_deleted = FALSE;
                  """
                : """
                  UPDATE srt_rma_requests SET status = 'Converted', linked_return_doc_id = $doc,
                      updated_by = $by, updated_at = datetime('now')
                  WHERE rma_id = $id AND company_id = $company AND status = 'Approved'
                    AND linked_return_doc_id IS NULL AND is_deleted = 0;
                  """;
            Add(cmd, "$doc", returnDocId);
            Add(cmd, "$by", userId.Trim());
            Add(cmd, "$id", rmaId);
            Add(cmd, "$company", Company);
            if (cmd.ExecuteNonQuery() == 0)
                throw new AppError(ErrorCodes.RmaStateInvalid,
                    $"RMA {rma.RmaNo} was already converted.", rma.RmaNo);
            tx.Commit();
        }
        return returnDocId;
    }

    private static RmaRequest? ReadRma(DbConnection connection, DbTransaction? tx, long rmaId)
    {
        if (!PartyLedger.TableExists(connection, tx, Provider, "srt_rma_requests"))
            return null;
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            SELECT rma_id, rma_no, party_id, party_name, source_kind, source_doc_id, source_doc_no,
                   reason_code, notes, status, requested_by, decided_by, decided_at, decision_note,
                   linked_return_doc_id, lines_json
            FROM srt_rma_requests
            WHERE rma_id = $id AND company_id = $company AND is_deleted = {(IsPg ? "FALSE" : "0")};
            """;
        Add(cmd, "$id", rmaId);
        Add(cmd, "$company", Company);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadRma(reader) : null;
    }

    private static RmaRequest ReadRma(DbDataReader reader)
    {
        var linesJson = ReadText(reader, 15);
        var lines = string.IsNullOrWhiteSpace(linesJson)
            ? new List<RmaLine>()
            : JsonSerializer.Deserialize<List<RmaLine>>(linesJson) ?? new List<RmaLine>();
        return new RmaRequest(
            Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
            ReadText(reader, 1),
            reader.IsDBNull(2) ? null : Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture),
            ReadText(reader, 3),
            ReadText(reader, 4),
            reader.IsDBNull(5) ? null : Convert.ToInt64(reader.GetValue(5), CultureInfo.InvariantCulture),
            ReadText(reader, 6),
            ReadText(reader, 7),
            reader.IsDBNull(8) ? null : ReadText(reader, 8),
            ReadText(reader, 9),
            reader.IsDBNull(10) ? null : ReadText(reader, 10),
            reader.IsDBNull(11) ? null : ReadText(reader, 11),
            reader.IsDBNull(12) ? null : ReadText(reader, 12),
            reader.IsDBNull(13) ? null : ReadText(reader, 13),
            reader.IsDBNull(14) ? null : Convert.ToInt64(reader.GetValue(14), CultureInfo.InvariantCulture),
            lines);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static void EnsureApprovePermission(Func<string, bool>? permissionCheck)
    {
        var check = permissionCheck ?? AppHost.Permissions.Has;
        if (check(PermCat.Sales.ReturnApprove)) return;
        var denied = new UnauthorizedAccessException(
            $"Permission {PermCat.Sales.ReturnApprove} is required for this sales return action.");
        AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, denied, "SrtRepo.EnsureApprovePermission");
        throw denied;
    }

    private static void EnsureDraftReturn(DbConnection connection, DbTransaction tx, long docId)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            SELECT status FROM sales_documents
            WHERE doc_id = $id AND doc_kind = 'SRT'
              AND (company_id = $company OR company_id = $guid) AND is_deleted = {(IsPg ? "FALSE" : "0")}
            {(IsPg ? "FOR UPDATE" : string.Empty)};
            """;
        Add(cmd, "$id", docId);
        Add(cmd, "$company", Company);
        Add(cmd, "$guid", ResolveGuidKey(connection, tx));
        var status = cmd.ExecuteScalar();
        if (status is null or DBNull)
            throw new InvalidOperationException("Return document not found in the current company.");
        if (!string.Equals(Convert.ToString(status, CultureInfo.InvariantCulture), "Draft", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only a Draft return can be inspected or decided; posted returns are immutable.");
    }

    /// <summary>The company's Guid key as stored on chain documents
    /// (InvRepo.ResolveCompanyGuidOrNull pattern, duplicated here so
    /// this service does not reach into InvRepo internals).</summary>
    private static string ResolveGuidKey(DbConnection connection, DbTransaction? tx)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT company_id FROM companies WHERE company_code = $code LIMIT 1;";
            Add(cmd, "$code", Company);
            var value = cmd.ExecuteScalar();
            return value is null or DBNull ? Company : Convert.ToString(value, CultureInfo.InvariantCulture) ?? Company;
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, ex, "SrtRepo.ResolveGuidKey");
            return Company;
        }
    }

    private static DbConnection Open(string connectionString)
    {
        DbConnection connection = IsPg
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

    private static string ReadText(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? string.Empty : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture) ?? string.Empty;
}
