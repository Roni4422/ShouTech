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
/// Persistence for commercial invoices (sales / purchase).
/// Posted documents are immutable; corrections use returns (accounting standard).
/// </summary>
public static class InvRepo
{
    private static string Company => AppState.CurrentCompanyCode ?? string.Empty;

    public static string NextDocNo(InvDocKind kind)
    {
        DocSchema.Ensure();
        var cs = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException(
                "A company database must be connected before allocating a document number.");

        try
        {
            using var connection = Open(cs);
            using var transaction = connection is SqliteConnection sqliteConnection
                ? sqliteConnection.BeginTransaction(deferred: false)
                : connection.BeginTransaction();
            var number = AllocateDocNo(connection, transaction, kind);
            transaction.Commit();
            return number;
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.InventoryOperationFailed, ex, "InvRepo.NextDocNo");
            throw new InvalidOperationException(
                "A new document number could not be allocated. Check the database connection and retry.",
                ex);
        }
    }

    private static string AllocateDocNo(
        DbConnection connection,
        DbTransaction transaction,
        InvDocKind kind)
    {
        var table = kind.IsSales() ? "sales_documents" : "purchase_documents";
        var prefix = kind switch
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
            InvDocKind.Quotation => "QUO",
            _ => "DOC"
        };
        var year = DateTime.Today.Year;
        var documentPrefix = $"{prefix}-{year}-";
        var suffixStart = documentPrefix.Length + 1;
        var numericSuffixPredicate = DatabaseRuntime.IsPostgreSql()
            ? "SUBSTR(doc_no, $suffixStart) ~ '^[0-9]+$'"
            : "SUBSTR(doc_no, $suffixStart) GLOB '[0-9]*' AND SUBSTR(doc_no, $suffixStart) NOT GLOB '*[^0-9]*'";

        using (var maxCommand = connection.CreateCommand())
        {
            maxCommand.Transaction = transaction;
            maxCommand.CommandText = $"""
                SELECT COALESCE(MAX(CAST(SUBSTR(doc_no, $suffixStart) AS BIGINT)), 0)
                FROM {table}
                WHERE company_id = $company
                  AND doc_kind = $kind
                  AND doc_no LIKE $pattern
                  AND {numericSuffixPredicate};
                """;
            Add(maxCommand, "$company", Company);
            Add(maxCommand, "$kind", prefix);
            Add(maxCommand, "$pattern", $"{documentPrefix}%");
            Add(maxCommand, "$suffixStart", suffixStart);
            var maximum = Convert.ToInt64(
                maxCommand.ExecuteScalar() ?? 0,
                CultureInfo.InvariantCulture);
            if (maximum >= 99999999)
                throw new InvalidOperationException(
                    "The document number sequence has reached its supported limit.");

            var next = maximum + 1;
            var advanceExpression = DatabaseRuntime.IsPostgreSql()
                ? "GREATEST(document_number_sequences.last_value + 1, EXCLUDED.last_value)"
                : "MAX(document_number_sequences.last_value + 1, excluded.last_value)";

            using var sequenceCommand = connection.CreateCommand();
            sequenceCommand.Transaction = transaction;
            sequenceCommand.CommandText = $"""
                INSERT INTO document_number_sequences
                    (company_id, doc_kind, doc_year, last_value)
                VALUES ($company, $kind, $year, $next)
                ON CONFLICT (company_id, doc_kind, doc_year)
                DO UPDATE SET last_value = {advanceExpression}
                RETURNING last_value;
                """;
            Add(sequenceCommand, "$company", Company);
            Add(sequenceCommand, "$kind", prefix);
            Add(sequenceCommand, "$year", year);
            Add(sequenceCommand, "$next", next);
            var allocated = Convert.ToInt64(
                sequenceCommand.ExecuteScalar() ?? 0,
                CultureInfo.InvariantCulture);
            if (allocated <= 0 || allocated > 99999999)
                throw new InvalidOperationException(
                    "The document number sequence returned an invalid value.");

            return $"{documentPrefix}{allocated.ToString("D4", CultureInfo.InvariantCulture)}";
        }
    }

    /// <summary>
    /// W7a: the acting user's price-override context, derived from the
    /// session permissions exactly like OrdWin's credit override — the
    /// context exists only when the user holds SALES.PRICE.OVERRIDE
    /// and/or SALES.DISCOUNT.OVERRIDE, and the guard audits its use.
    /// </summary>
    private static PriceOverrideContext? CurrentPriceOverride()
    {
        var actor = AppState.CurrentUser?.Username ?? string.Empty;
        if (string.IsNullOrWhiteSpace(actor))
            return null;
        var price = AppHost.Permissions.Has(PermCat.Sales.PriceOverride);
        var discount = AppHost.Permissions.Has(PermCat.Sales.DiscountOverride);
        return price || discount ? new PriceOverrideContext(actor, price, discount) : null;
    }

    /// <summary>
    /// W7a BXGY at native save: earned free quantities are appended as
    /// zero-price companion lines (stock + COGS move at zero revenue
    /// through StkPost, which values issues from the balance, never
    /// from the selling price). Regenerated companions are replaced,
    /// never doubled (the resolver's expansion rule).
    /// </summary>
    private static void ExpandBonusLines(DbConnection connection, DbTransaction tx, InvDoc doc)
    {
        var provider = DatabaseRuntime.IsPostgreSql() ? "PostgreSQL" : "SQLite";
        var drafts = doc.Lines.Select(l => new CommercialLineDraft(
            l.MaterialId, l.MaterialCode, l.Description, l.Unit, l.Qty, l.UnitPrice,
            l.DiscountPct, l.TaxRate, l.WarehouseId, l.LineNo)).ToList();
        var expanded = SalesPriceResolver.Default.ExpandBonusLines(
            connection, tx, provider, Company, doc.PartyCode, drafts);
        // ExpandBonusLines keeps the surviving originals first (in
        // order) and appends the free lines; rebuild the doc lines from
        // the surviving originals plus the appended companions.
        var surviving = new List<InvLine>();
        var paidMaterials = new HashSet<long>(doc.Lines
            .Where(l => l.MaterialId is > 0 && l.UnitPrice > 0m)
            .Select(l => l.MaterialId!.Value));
        foreach (var line in doc.Lines)
        {
            if (line.UnitPrice == 0m && line.MaterialId is > 0 && paidMaterials.Contains(line.MaterialId.Value))
                continue; // regenerated below
            surviving.Add(line);
        }
        foreach (var free in expanded.Skip(surviving.Count))
        {
            surviving.Add(new InvLine
            {
                MaterialId = free.MaterialId,
                MaterialCode = free.MaterialCode ?? string.Empty,
                Description = free.Description,
                Unit = free.Unit ?? string.Empty,
                Qty = free.Qty,
                UnitPrice = 0m,
                DiscountPct = 0m,
                TaxRate = free.TaxRate,
                WarehouseId = free.WarehouseId is null ? null : (int?)free.WarehouseId,
            });
        }
        doc.Lines = surviving;
        doc.RenumberLines();
    }

    /// <summary>W7a guard for the native sales-invoice path (save + post).</summary>
    private static void ValidateSalesPrices(DbConnection connection, DbTransaction tx, InvDoc doc, long? docId)
    {
        var provider = DatabaseRuntime.IsPostgreSql() ? "PostgreSQL" : "SQLite";
        SalesPriceGuard.Validate(connection, tx, provider, Company, doc.KindCode, doc.DocNo, docId,
            doc.Lines.Select(l => new PriceGuardLine(
                l.LineNo, l.MaterialId, l.MaterialCode, l.Qty, l.UnitPrice, l.DiscountPct)).ToList(),
            CurrentPriceOverride());
    }

    public static long SaveDraft(InvDoc doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (doc.Status != InvDocStatus.Draft)
            throw new InvalidOperationException("Only Draft documents can be overwritten. Posted or in-progress documents require reconciliation or a return document.");

        DocSchema.Ensure();
        doc.RenumberLines();
        doc.RecalcLineTaxesFromDefault();

        var cs = DatabaseRuntime.GetConnectionString()
            ?? throw new InvalidOperationException("No database connection.");
        using var connection = Open(cs);
        using var tx = connection is SqliteConnection sqliteConnection
            ? sqliteConnection.BeginTransaction(deferred: false)
            : connection.BeginTransaction();

        var isSales = doc.IsSalesSide;
        var headerTable = isSales ? "sales_documents" : "purchase_documents";
        var lineTable = isSales ? "sales_document_lines" : "purchase_document_lines";

        // W6 (migration 100): sales-return workflow stamps. Computed
        // here, at save time, from the line conditions and the company
        // settings, so every creation path (window, SrtRepo, POS,
        // reversals) stores a consistent inspection / approval state
        // that InvRepo.Post then enforces. Pre-100 databases (columns
        // absent) keep the legacy behaviour untouched.
        var w6Provider = DatabaseRuntime.IsPostgreSql() ? "PostgreSQL" : "SQLite";
        var hasW6Header = PartyLedger.ColumnExists(connection, tx, w6Provider, headerTable, "return_reason_code");
        var hasW6Lines = PartyLedger.ColumnExists(connection, tx, w6Provider, lineTable, "return_condition");
        // W5 (migration 102): export invoice header fields exist only
        // on the SALES header table; pre-102 databases keep the legacy
        // behaviour untouched.
        var hasExportHeader = isSales && PartyLedger.ColumnExists(connection, tx, w6Provider, headerTable, "is_export");
        if (doc.Kind == InvDocKind.SalesReturn && hasW6Header)
        {
            var settings = SrtRepo.ReadSettings(connection, tx);
            var needsInspection = doc.Lines.Any(l => l.ReturnCondition is "Quarantine" or "Damaged")
                || (settings.InspectionAmountThreshold is decimal threshold && doc.InvoiceValue >= threshold);
            if (doc.InspectionStatus != "Inspected")
                doc.InspectionStatus = needsInspection ? "PendingInspection" : "NotRequired";
            if (settings.ApprovalRequired)
            {
                // An Approved stamp survives re-saves; a Rejected
                // return that is edited goes back to PendingApproval
                // (resubmission), as does any undecided one.
                if (doc.ApprovalStatus != "Approved")
                    doc.ApprovalStatus = "PendingApproval";
            }
            else if (doc.ApprovalStatus == "PendingApproval")
            {
                doc.ApprovalStatus = "NotRequired";
            }
            if (string.IsNullOrWhiteSpace(doc.ReturnSourceKind))
                doc.ReturnSourceKind = doc.NoInvoice ? "NONE" : "INV";
        }

        EnsureUniqueSupplierInvoiceReference(connection, tx, doc);

        // W7a (sales invoices only): BXGY free lines are earned at
        // save, and the price/margin/discount guard validates the
        // lines before anything is written (SAL-7018/7019/7020).
        if (isSales && doc.Kind == InvDocKind.SalesInvoice)
        {
            ExpandBonusLines(connection, tx, doc);
            ValidateSalesPrices(connection, tx, doc, doc.DocId > 0 ? doc.DocId : null);
        }

        long docId = doc.DocId;
        if (docId <= 0)
        {
            if (string.IsNullOrWhiteSpace(doc.DocNo))
                doc.DocNo = AllocateDocNo(connection, tx, doc.Kind);

            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                // W6 columns ride at the end of the column list when
                // migration 100 has been applied (see the stamp block
                // above); the parameter values are bound after
                // BindHeader under the same condition.
                var w6Columns = hasW6Header
                    ? ", return_reason_code, return_source_kind, no_invoice, inspection_status, approval_status"
                    : string.Empty;
                var w6Values = hasW6Header
                    ? ", $rreason, $rsource, $noinv, $insp, $appr"
                    : string.Empty;
                var exportColumns = hasExportHeader
                    ? ", is_export, incoterm, port_of_loading, port_of_discharge, country_of_origin, customs_ref, vessel_flight, gross_weight, net_weight"
                    : string.Empty;
                var exportValues = hasExportHeader
                    ? ", $isexp, $incoterm, $pload, $pdisch, $origin, $customs, $vessel, $grossw, $netw"
                    : string.Empty;
                cmd.CommandText = $"""
                    INSERT INTO {headerTable}
                    (company_id, doc_kind, doc_no, doc_date, party_id, party_code, party_name, warehouse_id,
                     warehouse_name, currency_code, exchange_rate, subtotal, discount_amount, extra_amount,
                     tax_amount, grand_total, paid_amount, status, tax_code, tax_rate, notes,
                     payment_method, receipt_no, reference_no, supplier_credit_note_ref, branch_name, sales_rep_name, cost_center,
                     party_account_name, goods_account_name,
                     original_doc_id, original_doc_kind, original_doc_no, journal_doc_no,
                     target_warehouse_id, target_warehouse_name, valid_till,
                     created_by, is_deleted{w6Columns}{exportColumns})
                    VALUES
                    ($company, $kind, $no, $date, $party, $pcode, $pname, $wh,
                     $whname, $cur, $fx, $sub, $disc, $extra,
                     $tax, $grand, $paid, 'Draft', $tcode, $trate, $notes,
                     $pay, $receipt, $ref, $cnref, $branch, $rep, $cc,
                     $pacc, $gacc,
                     $origId, $origKind, $origNo, $jrn,
                     $twh, $twhname, $validTill,
                     $user, 0{w6Values}{exportValues});
                    SELECT last_insert_rowid();
                    """;
                if (DatabaseRuntime.IsPostgreSql())
                {
                    cmd.CommandText = $"""
                        INSERT INTO {headerTable}
                        (company_id, doc_kind, doc_no, doc_date, party_id, party_code, party_name, warehouse_id,
                         warehouse_name, currency_code, exchange_rate, subtotal, discount_amount, extra_amount,
                         tax_amount, grand_total, paid_amount, status, tax_code, tax_rate, notes,
                         payment_method, receipt_no, reference_no, branch_name, sales_rep_name, cost_center,
                         party_account_name, goods_account_name,
                         original_doc_id, original_doc_kind, original_doc_no, journal_doc_no,
                         target_warehouse_id, target_warehouse_name, valid_till,
                         created_by, is_deleted{w6Columns}{exportColumns})
                        VALUES
                        ($company, $kind, $no, $date, $party, $pcode, $pname, $wh,
                         $whname, $cur, $fx, $sub, $disc, $extra,
                         $tax, $grand, $paid, 'Draft', $tcode, $trate, $notes,
                         $pay, $receipt, $ref, $cnref, $branch, $rep, $cc,
                         $pacc, $gacc,
                         $origId, $origKind, $origNo, $jrn,
                         $twh, $twhname, $validTill,
                         $user, 0{w6Values}{exportValues})
                        RETURNING doc_id;
                        """;
                }
                BindHeader(cmd, doc);
                if (hasW6Header) BindW6Header(cmd, doc);
                if (hasExportHeader) BindExportHeader(cmd, doc);
                docId = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }
        else
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = $"""
                    UPDATE {headerTable} SET
                      doc_date=$date, party_id=$party, party_code=$pcode, party_name=$pname,
                      warehouse_id=$wh, warehouse_name=$whname,
                      currency_code=$cur, exchange_rate=$fx, subtotal=$sub,
                      discount_amount=$disc, extra_amount=$extra, tax_amount=$tax,
                      grand_total=$grand, paid_amount=$paid, tax_code=$tcode, tax_rate=$trate, notes=$notes,
                      payment_method=$pay, receipt_no=$receipt, reference_no=$ref, supplier_credit_note_ref=$cnref, branch_name=$branch,
                      sales_rep_name=$rep, cost_center=$cc, party_account_name=$pacc, goods_account_name=$gacc,
                      original_doc_id=$origId, original_doc_kind=$origKind, original_doc_no=$origNo, journal_doc_no=$jrn,
                      target_warehouse_id=$twh, target_warehouse_name=$twhname, valid_till=$validTill
                      {(hasW6Header ? ", return_reason_code=$rreason, return_source_kind=$rsource, no_invoice=$noinv, inspection_status=$insp, approval_status=$appr" : string.Empty)}
                      {(hasExportHeader ? ", is_export=$isexp, incoterm=$incoterm, port_of_loading=$pload, port_of_discharge=$pdisch, country_of_origin=$origin, customs_ref=$customs, vessel_flight=$vessel, gross_weight=$grossw, net_weight=$netw" : string.Empty)}
                    WHERE doc_id=$id AND (company_id=$company OR company_id=$guid) AND status='Draft' AND is_deleted=0;
                    """;
                BindHeader(cmd, doc);
                if (hasW6Header) BindW6Header(cmd, doc);
                if (hasExportHeader) BindExportHeader(cmd, doc);
                Add(cmd, "$guid", ResolveCompanyGuidOrNull(connection, tx)?.ToString("D") ?? Company);
                Add(cmd, "$id", docId);
                var affected = cmd.ExecuteNonQuery();
                if (affected == 0)
                    throw new InvalidOperationException("Document not found or not editable.");
            }

            using (var del = connection.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = $"DELETE FROM {lineTable} WHERE doc_id=$id;";
                Add(del, "$id", docId);
                del.ExecuteNonQuery();
            }
        }

        if (!isSales && doc.Kind == InvDocKind.PurchaseInvoice)
            StampPurchaseDueDate(connection, tx, docId, doc);

        foreach (var line in doc.Lines)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"""
                INSERT INTO {lineTable}
                (doc_id, line_no, material_id, material_code, description, unit, qty, unit_price,
                 discount_pct, tax_code, tax_rate, tax_calculation_mode, line_subtotal, line_tax, line_total, warehouse_id, warehouse_name
                 {(hasW6Lines ? ", return_condition, return_reason_code" : string.Empty)})
                VALUES
                ($doc, $no, $mid, $mcode, $desc, $unit, $qty, $price,
                 $disc, $tcode, $trate, $taxMode, $sub, $ltax, $ltot, $wh, $whname
                 {(hasW6Lines ? ", $rcond, $rreason" : string.Empty)});
                """;
            if (hasW6Lines)
            {
                Add(cmd, "$rcond", string.IsNullOrWhiteSpace(line.ReturnCondition) ? "Good" : line.ReturnCondition);
                Add(cmd, "$rreason", line.ReturnReasonCode);
            }
            Add(cmd, "$doc", docId);
            Add(cmd, "$no", line.LineNo);
            Add(cmd, "$mid", line.MaterialId);
            Add(cmd, "$mcode", line.MaterialCode);
            Add(cmd, "$desc", line.Description);
            Add(cmd, "$unit", line.Unit);
            Add(cmd, "$qty", line.Qty);
            Add(cmd, "$price", line.UnitPrice);
            Add(cmd, "$disc", line.DiscountPct);
            Add(cmd, "$tcode", line.TaxCode ?? string.Empty);
            Add(cmd, "$trate", line.TaxRate);
            Add(cmd, "$taxMode", line.TaxCalculationMode);
            Add(cmd, "$sub", line.LineSubtotal);
            Add(cmd, "$ltax", line.LineTax);
            Add(cmd, "$ltot", line.LineTotal);
            Add(cmd, "$wh", line.WarehouseId ?? doc.WarehouseId);
            Add(cmd, "$whname", string.IsNullOrWhiteSpace(line.WarehouseName) ? doc.WarehouseName : line.WarehouseName);
            cmd.ExecuteNonQuery();
        }

        // W1 (migration 095): a SAL invoice's due date is doc_date + the
        // party's credit_days, set at save when not already set (the
        // native InvoiceWin path; the chain path sets it in
        // CommercialDocumentRepository).
        if (isSales && doc.KindCode == "SAL" && doc.PartyId is not null)
        {
            try
            {
                var provider = DatabaseRuntime.IsPostgreSql() ? "PostgreSQL" : "SQLite";
                if (PartyLedger.ColumnExists(connection, tx, provider, headerTable, "due_date"))
                {
                    var due = PartyLedger.ComputeDueDate(connection, tx, provider, Company,
                        doc.PartyId.Value, DateOnly.FromDateTime(doc.DocDate));
                    if (due is not null)
                    {
                        using var dueCmd = connection.CreateCommand();
                        dueCmd.Transaction = tx;
                        dueCmd.CommandText = $"UPDATE {headerTable} SET due_date=$due WHERE doc_id=$id AND due_date IS NULL;";
                        Add(dueCmd, "$due", due.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                        Add(dueCmd, "$id", docId);
                        dueCmd.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception exDue)
            {
                AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, exDue, "InvRepo.SaveDraft.DueDate");
            }
        }

        tx.Commit();
        doc.DocId = docId;
        doc.Status = InvDocStatus.Draft;
        return docId;
    }

    // ------------------------------------------- approval workflow (W23, mig 098)

    private static string Provider => DatabaseRuntime.IsPostgreSql() ? "PostgreSQL" : "SQLite";

    /// <summary>True when sau_sales_settings requires approval for the
    /// current company (keyed by company CODE; default OFF).</summary>
    public static bool IsSalesApprovalRequired()
    {
        DocSchema.Ensure();
        var cs = DatabaseRuntime.GetConnectionString()
            ?? throw new InvalidOperationException("No database connection.");
        using var connection = Open(cs);
        using var tx = connection.BeginTransaction();
        var required = IsSalesApprovalRequired(connection, tx);
        tx.Commit();
        return required;
    }

    private static bool IsSalesApprovalRequired(DbConnection connection, DbTransaction tx)
    {
        if (!PartyLedger.ColumnExists(connection, tx, Provider, "sau_sales_settings", "require_approval"))
            return false;
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT require_approval FROM sau_sales_settings WHERE company_code=$company";
        Add(cmd, "$company", Company);
        var value = cmd.ExecuteScalar();
        if (value is null or DBNull) return false;
        return value is bool flag ? flag : Convert.ToInt32(value, CultureInfo.InvariantCulture) != 0;
    }

    /// <summary>The stored approval state of a native sales document
    /// (NotRequired when the column predates migration 098).</summary>
    public static string GetSalesApprovalState(long docId)
    {
        DocSchema.Ensure();
        var cs = DatabaseRuntime.GetConnectionString()
            ?? throw new InvalidOperationException("No database connection.");
        using var connection = Open(cs);
        using var tx = connection.BeginTransaction();
        var state = ReadSalesApprovalState(connection, tx, docId, out _, out _);
        tx.Commit();
        return state;
    }

    private static string ReadSalesApprovalState(DbConnection connection, DbTransaction tx, long docId, out string docKind, out string status)
    {
        docKind = string.Empty;
        status = string.Empty;
        if (!PartyLedger.ColumnExists(connection, tx, Provider, "sales_documents", "approval_state"))
            return ShouTech.Domain.Entities.SalesApprovalState.NotRequired;
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT approval_state, doc_kind, status FROM sales_documents
            WHERE doc_id=$id AND (company_id=$company OR company_id=$guid) AND is_deleted=0
            """;
        Add(cmd, "$id", docId);
        Add(cmd, "$company", Company);
        Add(cmd, "$guid", ResolveCompanyGuidOrNull(connection, tx)?.ToString("D") ?? Company);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            throw new InvalidOperationException("Document not found.");
        docKind = reader.GetString(1);
        status = reader.GetString(2);
        return reader.IsDBNull(0) ? ShouTech.Domain.Entities.SalesApprovalState.NotRequired : reader.GetString(0);
    }

    private static void EnsureSalesApprovalGate(DbConnection connection, DbTransaction tx, long docId, string docNo)
    {
        var state = ReadSalesApprovalState(connection, tx, docId, out _, out _);
        if (state != ShouTech.Domain.Entities.SalesApprovalState.Approved && IsSalesApprovalRequired(connection, tx))
            throw new AppError(ErrorCodes.ApprovalStateInvalid,
                $"Document '{docNo}' requires approval before posting; its approval state is '{state}'.",
                docNo, state);
    }

    /// <summary>Submits a native sales invoice/quotation draft for approval.</summary>
    public static void SubmitSalesForApproval(long docId, string userId) =>
        TransitionSalesApproval(docId, userId,
            new[] { ShouTech.Domain.Entities.SalesApprovalState.NotRequired, ShouTech.Domain.Entities.SalesApprovalState.Rejected },
            ShouTech.Domain.Entities.SalesApprovalState.Pending, ShouTech.Domain.Entities.SalesApprovalAction.Submitted, null);

    /// <summary>Approves a Pending native sales invoice/quotation.</summary>
    public static void ApproveSalesDocument(long docId, string userId) =>
        TransitionSalesApproval(docId, userId,
            new[] { ShouTech.Domain.Entities.SalesApprovalState.Pending },
            ShouTech.Domain.Entities.SalesApprovalState.Approved, ShouTech.Domain.Entities.SalesApprovalAction.Approved, null);

    /// <summary>Rejects a Pending native sales invoice/quotation; reason mandatory.</summary>
    public static void RejectSalesDocument(long docId, string userId, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A rejection reason is required.", nameof(reason));
        TransitionSalesApproval(docId, userId,
            new[] { ShouTech.Domain.Entities.SalesApprovalState.Pending },
            ShouTech.Domain.Entities.SalesApprovalState.Rejected, ShouTech.Domain.Entities.SalesApprovalAction.Rejected, reason.Trim());
    }

    private static void TransitionSalesApproval(long docId, string userId, string[] allowedFrom, string toState, string action, string? reason)
    {
        if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("User is required.", nameof(userId));
        DocSchema.Ensure();
        var cs = DatabaseRuntime.GetConnectionString()
            ?? throw new InvalidOperationException("No database connection.");
        using var connection = Open(cs);
        using var tx = connection.BeginTransaction();
        var fromState = ReadSalesApprovalState(connection, tx, docId, out var docKind, out var status);
        string companyKey;
        string docNo;
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT company_id, doc_no FROM sales_documents WHERE doc_id=$id AND is_deleted=0";
            Add(cmd, "$id", docId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) throw new InvalidOperationException("Document not found.");
            companyKey = Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!;
            docNo = reader.GetString(1);
        }
        if (docKind is not ("QUO" or "SO" or "SAL")
            || !string.Equals(status, "Draft", StringComparison.OrdinalIgnoreCase)
            || Array.IndexOf(allowedFrom, fromState) < 0)
            throw new AppError(ErrorCodes.ApprovalStateInvalid,
                $"Document '{docNo}' in status '{status}' with approval state '{fromState}' cannot perform '{action}'.",
                docNo, fromState);
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE sales_documents SET approval_state=$to WHERE doc_id=$id AND approval_state=$from AND is_deleted=0";
            Add(cmd, "$to", toState);
            Add(cmd, "$id", docId);
            Add(cmd, "$from", fromState);
            if (cmd.ExecuteNonQuery() != 1)
                throw new AppError(ErrorCodes.ApprovalStateInvalid,
                    $"Document '{docNo}' changed approval state concurrently; '{action}' was not applied.",
                    docNo, fromState);
        }
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO sau_approval_history
                    (history_id, company_id, doc_id, doc_kind, action, from_state, to_state, actor, reason)
                VALUES ($id, $company, $doc, $kind, $action, $from, $to, $actor, $reason)
                """;
            Add(cmd, "$id", Guid.NewGuid().ToString("D"));
            Add(cmd, "$company", companyKey);
            Add(cmd, "$doc", docId);
            Add(cmd, "$kind", docKind);
            Add(cmd, "$action", action);
            Add(cmd, "$from", fromState);
            Add(cmd, "$to", toState);
            Add(cmd, "$actor", userId.Trim());
            Add(cmd, "$reason", (object?)reason ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public static void Post(long docId, InvDocKind kind, string userId,
        IReadOnlyDictionary<int, LineBatchSerialCapture>? batchSerialByLine = null,
        PosPostingContext? pos = null,
        ShouTech.Domain.Entities.CreditOverrideContext? creditOverride = null)
    {
        DocSchema.Ensure();
        MatRepo.EnsureSchema();

        // Step 9 (Step 5 flag F-1): StockTransfer / InventoryCount post
        // through their DEDICATED engines — StkPost.PostTransfer
        // (two-sided, value-conserving) and StkPost.PostCount
        // (delta-only) — never through the generic flow below, whose
        // single-warehouse qty-in fabricated stock for both kinds
        // (sweep I-6/I-7). Step 5 blocked them here with ACC-5014
        // rather than let them fabricate; now that InvoiceWin's modes
        // are wired to the engines, the block is replaced by the route.
        if (kind is InvDocKind.StockTransfer or InvDocKind.InventoryCount)
        {
            PostDedicatedEngineDocument(docId, kind, userId, batchSerialByLine);
            return;
        }

        var isSales = kind.IsSales();
        var headerTable = isSales ? "sales_documents" : "purchase_documents";
        var cs = DatabaseRuntime.GetConnectionString()
            ?? throw new InvalidOperationException("No database connection.");

        // DLV-linked invoices use the commercial-chain posting path:
        // delivery already issued stock, so the invoice must not issue it again.
        if (kind == InvDocKind.SalesInvoice && IsDeliveryLinkedSalesInvoice(docId))
        {
            var chainRepository = new CommercialDocumentRepository(
                DatabaseRuntime.IsPostgreSql() ? "PostgreSQL" : "SQLite", cs);
            chainRepository.PostSalesAsync(docId, userId).GetAwaiter().GetResult();
            return;
        }

        using var connection = Open(cs);
        using var tx = connection.BeginTransaction();
        // Step 9: dual company key (code or Guid), as in Load.
        var guidKey = ResolveCompanyGuidOrNull(connection, tx)?.ToString("D") ?? Company;

        // W6 (migration 100): the return workflow columns ride along
        // when present; a pre-100 database posts exactly as before.
        var postProvider = DatabaseRuntime.IsPostgreSql() ? "PostgreSQL" : "SQLite";
        var hasW6Header = PartyLedger.ColumnExists(connection, tx, postProvider, headerTable, "return_reason_code");
        var hasW6Lines = PartyLedger.ColumnExists(connection, tx, postProvider,
            isSales ? "sales_document_lines" : "purchase_document_lines", "return_condition");

        InvDoc? doc;
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"""
                SELECT doc_id, doc_kind, doc_no, doc_date, party_id, party_name, warehouse_id,
                       currency_code, exchange_rate, subtotal, tax_amount, grand_total, status, tax_rate, party_code,
                       discount_amount, extra_amount,
                       paid_amount, payment_method, cost_center,
                       original_doc_id, original_doc_kind, original_doc_no
                       {(hasW6Header ? ", return_reason_code, return_source_kind, no_invoice, inspection_status, approval_status" : string.Empty)}
                FROM {headerTable}
                WHERE doc_id=$id AND (company_id=$company OR company_id=$guid) AND is_deleted=0;
                """;
            Add(cmd, "$id", docId);
            Add(cmd, "$company", Company);
            Add(cmd, "$guid", guidKey);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                throw new InvalidOperationException("Document not found.");
            var storedKind = reader.GetString(reader.GetOrdinal("doc_kind"));
            var expectedKind = new InvDoc { Kind = kind }.KindCode;
            if (!string.Equals(storedKind, expectedKind, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Document kind mismatch. The stored document is '{storedKind}', not '{expectedKind}'.");
            if (string.Equals(reader.GetString(reader.GetOrdinal("status")), "Posted", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Document is already posted.");
            doc = new InvDoc
            {
                DocId = docId,
                Kind = kind,
                DocNo = reader.GetString(reader.GetOrdinal("doc_no")),
                DocDate = DateTime.Parse(reader.GetString(reader.GetOrdinal("doc_date")), CultureInfo.InvariantCulture),
                PartyId = reader.IsDBNull(reader.GetOrdinal("party_id")) ? null : Convert.ToInt64(reader.GetValue(reader.GetOrdinal("party_id")), CultureInfo.InvariantCulture),
                PartyCode = reader.IsDBNull(reader.GetOrdinal("party_code")) ? "" : reader.GetString(reader.GetOrdinal("party_code")),
                PartyName = reader.IsDBNull(reader.GetOrdinal("party_name")) ? "" : reader.GetString(reader.GetOrdinal("party_name")),
                WarehouseId = reader.IsDBNull(reader.GetOrdinal("warehouse_id")) ? null : Convert.ToInt32(reader.GetValue(reader.GetOrdinal("warehouse_id")), CultureInfo.InvariantCulture),
                CurrencyCode = reader.GetString(reader.GetOrdinal("currency_code")),
                ExchangeRate = Convert.ToDecimal(reader.GetValue(reader.GetOrdinal("exchange_rate")), CultureInfo.InvariantCulture),
                PaidAmount = reader.IsDBNull(reader.GetOrdinal("paid_amount")) ? 0m : Convert.ToDecimal(reader.GetValue(reader.GetOrdinal("paid_amount")), CultureInfo.InvariantCulture),
                PaymentMethod = ParsePaymentMethod(reader.IsDBNull(reader.GetOrdinal("payment_method")) ? null : reader.GetString(reader.GetOrdinal("payment_method"))),
                CostCenter = reader.IsDBNull(reader.GetOrdinal("cost_center")) ? "" : reader.GetString(reader.GetOrdinal("cost_center")),
                OriginalDocId = reader.IsDBNull(reader.GetOrdinal("original_doc_id"))
                    ? null : Convert.ToInt64(reader.GetValue(reader.GetOrdinal("original_doc_id")), CultureInfo.InvariantCulture),
                OriginalDocKind = ParseDocKind(reader.IsDBNull(reader.GetOrdinal("original_doc_kind"))
                    ? null : reader.GetString(reader.GetOrdinal("original_doc_kind"))),
                OriginalDocNo = reader.IsDBNull(reader.GetOrdinal("original_doc_no"))
                    ? "" : reader.GetString(reader.GetOrdinal("original_doc_no")),
                // Step 5 (sweep I-1): the STORED header totals are the
                // amounts of record for posting. Before this fix Post
                // selected subtotal/tax_amount/grand_total and discarded
                // them, then debited doc.GrandTotal — recomputed from the
                // lines with LinesDiscountAmount/LinesExtraAmount still
                // at their 0 defaults — so a document storing 1,050
                // (1,000 + 150 tax − 100 header discount) debited 1,150
                // to party_ledger, the GL and the credit guard.
                LinesDiscountAmount = reader.IsDBNull(reader.GetOrdinal("discount_amount")) ? 0m : Convert.ToDecimal(reader.GetValue(reader.GetOrdinal("discount_amount")), CultureInfo.InvariantCulture),
                LinesExtraAmount = reader.IsDBNull(reader.GetOrdinal("extra_amount")) ? 0m : Convert.ToDecimal(reader.GetValue(reader.GetOrdinal("extra_amount")), CultureInfo.InvariantCulture),
                StoredSubtotal = reader.IsDBNull(reader.GetOrdinal("subtotal")) ? null : Convert.ToDecimal(reader.GetValue(reader.GetOrdinal("subtotal")), CultureInfo.InvariantCulture),
                StoredTaxAmount = reader.IsDBNull(reader.GetOrdinal("tax_amount")) ? null : Convert.ToDecimal(reader.GetValue(reader.GetOrdinal("tax_amount")), CultureInfo.InvariantCulture),
                StoredGrandTotal = reader.IsDBNull(reader.GetOrdinal("grand_total")) ? null : Convert.ToDecimal(reader.GetValue(reader.GetOrdinal("grand_total")), CultureInfo.InvariantCulture),
            };
            if (hasW6Header)
            {
                doc.ReturnReasonCode = ReadString(reader, "return_reason_code");
                doc.ReturnSourceKind = ReadString(reader, "return_source_kind");
                var noInvoiceValue = reader.GetValue(reader.GetOrdinal("no_invoice"));
                doc.NoInvoice = noInvoiceValue is bool b ? b : Convert.ToInt32(noInvoiceValue, CultureInfo.InvariantCulture) != 0;
                doc.InspectionStatus = string.IsNullOrWhiteSpace(ReadString(reader, "inspection_status")) ? "NotRequired" : ReadString(reader, "inspection_status");
                doc.ApprovalStatus = string.IsNullOrWhiteSpace(ReadString(reader, "approval_status")) ? "NotRequired" : ReadString(reader, "approval_status");
            }
        }

        // Approval gate (W23, migration 098): only when the company policy
        // requires approval (sau_sales_settings, default OFF) must a native
        // sales invoice / quotation be Approved before it posts — the same
        // SAL-7016 contract the chain repository enforces.
        if (isSales && kind is InvDocKind.SalesInvoice or InvDocKind.Quotation)
            EnsureSalesApprovalGate(connection, tx, docId, doc.DocNo);

        // The legacy InvoiceWin/InvRepo purchase path has no PO/receipt
        // lineage and can settle a bill immediately during posting. When the
        // company enables three-way matching, block this path at the repository
        // boundary so callers cannot bypass the commercial-chain evaluation or
        // its payment gate. The company can still use legacy manual invoices
        // while the control is explicitly disabled.
        if (kind == InvDocKind.PurchaseInvoice)
        {
            using var matchPolicy = connection.CreateCommand();
            matchPolicy.Transaction = tx;
            matchPolicy.CommandText = """
                SELECT is_enabled
                FROM purchase_match_policies
                WHERE company_code = $company
                LIMIT 1;
                """;
            Add(matchPolicy, "$company", Company);
            var policyValue = matchPolicy.ExecuteScalar();
            if (policyValue is not null and not DBNull &&
                Convert.ToBoolean(policyValue, CultureInfo.InvariantCulture))
            {
                throw new InvalidOperationException(
                    "This company requires purchase-order and goods-receipt matching. Create the supplier invoice from the purchasing order workflow; legacy manual invoice posting is blocked.");
            }
        }

        // Kind guard (Step 5, sweep I-2): the effects a kind may have are
        // defined ONCE in InvPostingPolicy and enforced here, at the
        // repository layer, so every caller (InvoiceWin, PosCheckoutWin,
        // any future caller) is safe. Unknown kinds are blocked outright.
        // StockTransfer / InventoryCount never reach this point — they
        // are routed to their dedicated engines at the top of Post
        // (Step 9); the generic flow's single-warehouse qty-in is what
        // fabricated stock for them (sweep I-6/I-7).
        var effects = InvPostingPolicy.For(kind);
        if (effects.Blocked)
            throw new AppError(
                ErrorCodes.DocumentKindNotPostable,
                $"Document kind '{kind}' cannot be posted through the invoice posting path.",
                kind.ToString());

        // POS session / tender gate (Build POS 066): when the caller
        // supplies a POS context (the PosWin / PosCheckoutWin /
        // PosReturnWin path — the only till path), the sale or return
        // must belong to an OPEN session, or to an explicitly enabled
        // session-less mode, and its tenders must settle the stored
        // total exactly. The check runs INSIDE the posting transaction,
        // before stock / GL / ledger writes, so a refused sale posts
        // nothing at all (POS-1001 / POS-1004). Direct InvRepo posts
        // without a context keep the pre-066 engine behaviour — the
        // session rule binds the till, and PosService refuses earlier
        // still; see docs/BUILD_POS.md §2.
        if (pos is not null)
            ValidatePosContext(connection, tx, doc, pos);

        // Load lines for inventory posting within this connection/transaction.
        {
            var lineTable = isSales ? "sales_document_lines" : "purchase_document_lines";
            using var lineCmd = connection.CreateCommand();
            lineCmd.Transaction = tx;
            lineCmd.CommandText = $"""
                SELECT line_no, material_id, material_code, description, unit, qty, unit_price,
                       discount_pct, tax_code, tax_rate, tax_calculation_mode, warehouse_id
                       {(hasW6Lines ? ", return_condition, return_reason_code" : string.Empty)}
                FROM {lineTable} WHERE doc_id=$id ORDER BY line_no;
                """;
            Add(lineCmd, "$id", docId);
            using var lr = lineCmd.ExecuteReader();
            while (lr.Read())
            {
                doc.Lines.Add(new InvLine
                {
                    LineNo = Convert.ToInt32(lr.GetValue(0), CultureInfo.InvariantCulture),
                    MaterialId = lr.IsDBNull(1) ? null : Convert.ToInt64(lr.GetValue(1), CultureInfo.InvariantCulture),
                    MaterialCode = lr.IsDBNull(2) ? "" : Convert.ToString(lr.GetValue(2), CultureInfo.InvariantCulture) ?? "",
                    Description = Convert.ToString(lr.GetValue(3), CultureInfo.InvariantCulture) ?? "",
                    Unit = lr.IsDBNull(4) ? "" : Convert.ToString(lr.GetValue(4), CultureInfo.InvariantCulture) ?? "",
                    Qty = Convert.ToDecimal(lr.GetValue(5), CultureInfo.InvariantCulture),
                    UnitPrice = Convert.ToDecimal(lr.GetValue(6), CultureInfo.InvariantCulture),
                    DiscountPct = Convert.ToDecimal(lr.GetValue(7), CultureInfo.InvariantCulture),
                    TaxCode = lr.IsDBNull(8) ? string.Empty : Convert.ToString(lr.GetValue(8), CultureInfo.InvariantCulture) ?? string.Empty,
                    TaxRate = Convert.ToDecimal(lr.GetValue(9), CultureInfo.InvariantCulture),
                    TaxCalculationMode = lr.IsDBNull(10) ? "Exclusive" : Convert.ToString(lr.GetValue(10), CultureInfo.InvariantCulture) ?? "Exclusive",
                    WarehouseId = lr.IsDBNull(11) ? null : Convert.ToInt32(lr.GetValue(11), CultureInfo.InvariantCulture),
                    ReturnCondition = hasW6Lines && !lr.IsDBNull(12) ? Convert.ToString(lr.GetValue(12), CultureInfo.InvariantCulture) ?? "Good" : "Good",
                    ReturnReasonCode = hasW6Lines && !lr.IsDBNull(13) ? Convert.ToString(lr.GetValue(13), CultureInfo.InvariantCulture) : null,
                });
            }
        }

        // W7a pricing guard at POST for native sales invoices
        // (SAL-7018/7019/7020), inside the posting transaction before
        // any stock/GL/ledger write.
        if (kind == InvDocKind.SalesInvoice)
            ValidateSalesPrices(connection, tx, doc, docId);

        (decimal ReceiptBase, decimal InvoiceBase) unwoundChain = (0m, 0m);
        if (kind == InvDocKind.SalesReturn)
        {
            // W6 source branching: a no-invoice (walk-in) return and a
            // delivery-sourced return validate against their own
            // shapes; everything else keeps the Part I invoice
            // validation (which now raises SAL-7021 on over-return).
            if (doc.NoInvoice)
                ValidateNoInvoiceSalesReturn(doc);
            else if (string.Equals(doc.ReturnSourceKind, "DLV", StringComparison.OrdinalIgnoreCase))
                ValidateDeliverySalesReturn(connection, tx, guidKey, doc);
            else
                ValidateSalesReturnAgainstOriginal(connection, tx, guidKey, doc);

            // W6 gates: inspection (SAL-7022) and — when the company
            // flag is on — approval. POS-context returns settle at the
            // till in one step and generated full reversals are system
            // documents; neither is held by the back-office gates.
            if (pos is null && !IsGeneratedFullReversal(connection, tx, doc))
                EnforceSalesReturnGates(connection, tx, doc);
        }
        if (kind == InvDocKind.PurchaseReturn)
        {
            ValidateAndAllocatePurchaseReturnAgainstOriginal(connection, tx, guidKey, doc, userId);
            // I4: posting the debit note unwinds the mig-078 matches
            // and the PO's stored per_billed for the returned lines, in
            // this same transaction and under the same original-invoice
            // lock the allocation step holds. The unwound base totals
            // feed BuildGlLines, which mirrors the original chain
            // invoice's GRNI clearing for the unwound portion.
            unwoundChain = UnwindPurchaseReturnMatches(connection, tx, guidKey, doc, userId);
        }

        // Batch/serial capture (Phase B 054): merge the picker's
        // per-line selections (keyed by line number) into the loaded
        // lines so StkPost hands them to the valuation engine's
        // allocator. Lines without a capture entry post untracked —
        // for a tracked material the allocator then fails the posting
        // with INV-6005 rather than skipping silently.
        if (batchSerialByLine is not null)
        {
            foreach (var line in doc.Lines)
            {
                if (batchSerialByLine.TryGetValue(line.LineNo, out var capture))
                {
                    line.BatchAllocations = capture.Batches;
                    line.Serials = capture.Serials;
                    line.AllowExpiredBatchIssue = capture.AllowExpiredBatchIssue;
                }
            }
        }

        // Stored-total discrepancy report (Step 5): if the lines and the
        // header adjustments in storage no longer recompute to the stored
        // grand_total (a hand-edited or legacy-corrupted row), the STORED
        // total still wins for every posting consumer below — and the
        // disagreement is reported through the error catalog (ACC-5013),
        // never silently recomputed into a different posted number.
        if (doc.StoredGrandTotal is decimal storedGrand)
        {
            var recomputed = doc.GrandTotal;
            if (recomputed != storedGrand)
                AppHost.Errors.Report(
                    ErrorCodes.DocumentTotalMismatch,
                    new InvalidOperationException(
                        $"Document '{doc.DocNo}' stored grand_total {storedGrand} != recomputed {recomputed} (lines subtotal {doc.Subtotal} - discount {doc.LinesDiscountAmount} + extra {doc.LinesExtraAmount} + tax {doc.TaxAmount}); posting uses the stored total."),
                    "InvRepo.Post");
        }

        // Step 7 (J-5): resolve the document's effective exchange rate
        // ONCE, through the one path (FxRates): the stored document rate
        // first, the currency_rates master where the document carries
        // none (stored rate <= 0), 1 for base-currency documents — and a
        // foreign-currency document with no rate anywhere fails here
        // with ACC-5015, before stock, GL, or ledger writes. Every
        // consumer below (GL via GlPosting's own resolution of the same
        // inputs, the party ledger, the credit guard) uses this rate,
        // so the document can never post at face value in one ledger
        // and converted in another.
        var fxProvider = DatabaseRuntime.IsPostgreSql() ? "PostgreSQL" : "SQLite";
        var fx = FxRates.ResolveByCompanyCode(connection, tx, fxProvider,
            Company, doc.CurrencyCode, DateOnly.FromDateTime(doc.DocDate), doc.ExchangeRate);

        // Inventory effects FIRST (Step 6): the valuation engine writes
        // the document's movements inside this transaction, and the GL
        // COGS legs below are built from those movements' cost_amount —
        // the engine's per-method valuation — instead of a balance
        // average read before posting. Same transaction throughout, so
        // a GL failure still rolls the stock back with everything else.
        if (effects.Stock)
            StkPost.ApplyDocument(connection, tx, doc);

        // Real GL posting (Step 3, J-2): build the document's journal lines
        // from the document's STORED totals (Step 5) and post them INSIDE
        // this transaction. The journal number that used to be fabricated
        // as "JV-" + DocNo is now the REAL entry's document number,
        // returned by the posting engine. Kinds with no GL effect
        // (quotation, opening stock) build no lines and keep an empty
        // journal number.
        // Withholding tax (W1, migration 099): evaluated ONCE here, on
        // the VAT-excluding base (stored grand total − stored tax), so
        // the GL legs, the party ledger and the tax register all carry
        // the same figure. A purchase return that reverses a withheld
        // invoice mirrors the ORIGINAL's stored withholding exactly.
        var withholding = ShouTech.Infrastructure.WithholdingTax.None;
        (decimal Amount, string? Account, string? Code, decimal Rate, decimal Base) reversalWithholding = (0m, null, null, 0m, 0m);
        if (kind == InvDocKind.PurchaseInvoice)
        {
            var provider = DatabaseRuntime.IsPostgreSql() ? "PostgreSQL" : "SQLite";
            string? selection = null;
            if (ShouTech.Infrastructure.PartyLedger.ColumnExists(connection, tx, provider, "purchase_documents", "withholding_tax_code"))
            {
                using var sel = connection.CreateCommand();
                sel.Transaction = tx;
                sel.CommandText = "SELECT withholding_tax_code FROM purchase_documents WHERE doc_id=$id;";
                Add(sel, "$id", docId);
                var selValue = sel.ExecuteScalar();
                selection = selValue is null or DBNull ? null : Convert.ToString(selValue, CultureInfo.InvariantCulture);
            }
            withholding = ShouTech.Infrastructure.WithholdingTax.Evaluate(
                connection, tx, provider, Company, ResolveCompanyGuid(connection, tx),
                doc.PartyId, DateOnly.FromDateTime(doc.DocDate),
                doc.PostingTotal - doc.PostingTaxAmount, selection, excludeDocId: docId);
            if (withholding.Amount > doc.PostingTotal)
                throw new AppError(ErrorCodes.WithholdingExceedsPayable,
                    $"Withholding {withholding.Amount} on invoice '{doc.DocNo}' exceeds the amount payable to the supplier.", doc.DocNo);
        }
        else if (kind == InvDocKind.PurchaseReturn && doc.OriginalDocId is long withheldOriginalId)
        {
            var provider = DatabaseRuntime.IsPostgreSql() ? "PostgreSQL" : "SQLite";
            reversalWithholding = ShouTech.Infrastructure.WithholdingTax.ReadStored(
                connection, tx, provider, withheldOriginalId);
        }

        var glLines = effects.Gl ? BuildGlLines(connection, tx, doc, fx.Rate, withholding, reversalWithholding, unwoundChain) : new List<ShouTech.Domain.Entities.GlLineRequest>();
        string journalNo = string.Empty;
        if (glLines.Count > 0)
        {
            var companyGuid = ResolveCompanyGuid(connection, tx);
            var glResult = GlPosting.PostInTransaction(connection, tx,
                DatabaseRuntime.IsPostgreSql() ? "PostgreSQL" : "SQLite",
                new ShouTech.Domain.Entities.GlDocumentRequest(
                    companyGuid,
                    DateOnly.FromDateTime(doc.DocDate),
                    $"{doc.KindCode} {doc.DocNo} - {doc.PartyName}".Trim(),
                    doc.KindCode,
                    docId.ToString(CultureInfo.InvariantCulture),
                    doc.DocNo ?? docId.ToString(CultureInfo.InvariantCulture),
                    doc.CurrencyCode,
                    fx.Rate,
                    glLines),
                userId);
            journalNo = glResult.DocumentNumber;
        }
        doc.JournalDocNo = journalNo;

        // Tax register rows are written in the same transaction as the
        // journal and document status. A failure rolls back the entire post.
        WriteTaxTransactions(connection, tx, doc, journalNo, userId, fx.Rate);

        // Withholding (W1): persist the evaluated outcome on the header
        // and register it in tax_transactions (line_no 0, WITHHOLDING) —
        // including a below-threshold evaluation with amount 0. A return
        // reversing a withheld invoice writes the NEGATIVE mirror row
        // under its own document; the original rows stay immutable.
        if (withholding.Evaluated)
        {
            var provider = DatabaseRuntime.IsPostgreSql() ? "PostgreSQL" : "SQLite";
            ShouTech.Infrastructure.WithholdingTax.PersistOutcome(connection, tx, provider, docId, withholding);
            ShouTech.Infrastructure.WithholdingTax.WriteRegisterRow(
                connection, tx, provider, Company, withholding.CountryCode,
                doc.KindCode, docId, doc.DocNo ?? string.Empty, withholding, doc.CurrencyCode, fx.Rate,
                journalNo, userId, DateOnly.FromDateTime(doc.DocDate));
        }
        else if (reversalWithholding.Amount > 0m && reversalWithholding.Code is not null)
        {
            var provider = DatabaseRuntime.IsPostgreSql() ? "PostgreSQL" : "SQLite";
            string mirrorCountry;
            using (var settings = connection.CreateCommand())
            {
                settings.Transaction = tx;
                settings.CommandText = "SELECT country_code FROM company_tax_settings WHERE company_code=$company LIMIT 1;";
                Add(settings, "$company", Company);
                mirrorCountry = Convert.ToString(settings.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
            }
            var mirror = new ShouTech.Infrastructure.WithholdingTax.Outcome(
                true, reversalWithholding.Code, reversalWithholding.Rate, reversalWithholding.Base,
                reversalWithholding.Base, reversalWithholding.Amount, reversalWithholding.Account, mirrorCountry);
            ShouTech.Infrastructure.WithholdingTax.WriteRegisterRow(
                connection, tx, provider, Company, mirrorCountry,
                doc.KindCode, docId, doc.DocNo ?? string.Empty, mirror, doc.CurrencyCode, fx.Rate,
                journalNo, userId, DateOnly.FromDateTime(doc.DocDate), sign: -1);
        }

        using (var upd = connection.CreateCommand())
        {
            upd.Transaction = tx;

            upd.CommandText = $"""
                UPDATE {headerTable}
                SET status='Posted', posted_by=$user, posted_at=datetime('now'), journal_doc_no=$jrn
                WHERE doc_id=$id AND (company_id=$company OR company_id=$guid) AND status='Draft';
                """;
            if (DatabaseRuntime.IsPostgreSql())
                upd.CommandText = $"""
                    UPDATE {headerTable}
                    SET status='Posted', posted_by=$user, posted_at=NOW(), journal_doc_no=$jrn
                    WHERE doc_id=$id AND (company_id=$company OR company_id=$guid) AND status='Draft';
                    """;
            Add(upd, "$user", userId);
            Add(upd, "$id", docId);
            Add(upd, "$company", Company);
            Add(upd, "$guid", guidKey);
            Add(upd, "$jrn", journalNo);
            if (upd.ExecuteNonQuery() == 0)
                throw new InvalidOperationException("Post failed — document may already be posted.");
        }

        // POS tenders + session link (Build POS 066): written in THIS
        // transaction, so a posted POS document and its tenders commit
        // or roll back together — there is no posted-but-tenderless
        // state. Refund rows of a return already carry negative
        // amounts (PosPosting), which is what makes the session's
        // expected cash a plain SUM in the Z report.
        if (pos is not null)
            WritePosTenders(connection, tx, doc, pos, userId);

        // POS refunds must reverse the original at-posting settlement in
        // this same transaction. Keeping this inside the document posting
        // boundary prevents a committed return from being left with stale
        // customer settlement/allocation balances after a process crash.
        if (pos is not null
            && doc.Kind == InvDocKind.SalesReturn
            && doc.OriginalDocKind == InvDocKind.PosSale
            && doc.OriginalDocId is long originalPosDocId
            && doc.PartyId is long originalPartyId
            && doc.PaidAmount > 0m)
        {
            UnsettleOriginalOnPosReturn(
                connection, tx, guidKey, originalPosDocId, originalPartyId,
                doc, doc.PaidAmount);
        }

        // Party ledger: sales increase receivable (debit customer),
        // purchases increase payable — ONLY for kinds whose policy carries
        // a party-ledger effect (Step 5). Before this gate the block ran
        // for ANY kind with a PartyId: a QUOTATION created a real
        // receivable and stock documents credited a supplier (sweep I-2).
        // The amount is the STORED grand total (doc.PostingTotal) — the
        // same figure the GL above and the credit guard below consume.
        // G16/G17: a sales return's ledger id is captured so the credit
        // note can be auto-allocated to its origin invoice below.
        // W6: a delivery-sourced return (DLV) has NO party-ledger
        // effect — nothing was ever billed, so there is no receivable
        // to credit; its only GL is the 1215 relief in BuildGlLines.
        long? salesReturnLedgerId = null;
        var isDeliveryReturn = kind == InvDocKind.SalesReturn
            && string.Equals(doc.ReturnSourceKind, "DLV", StringComparison.OrdinalIgnoreCase);
        if (effects.PartyLedger && !isDeliveryReturn && doc.PartyId is long partyId)
        {
            var partyKind = isSales ? "C" : "S";
            decimal debit = 0, credit = 0;
            if (isSales)
            {
                if (kind == InvDocKind.SalesReturn) credit = doc.PostingTotal;
                else debit = doc.PostingTotal;
            }
            else
            {
                if (kind == InvDocKind.PurchaseReturn) debit = doc.PostingTotal;
                else credit = doc.PostingTotal;
            }

            // Credit-limit gate (ACC-5006): only a movement that increases
            // the party's exposure is checked (customer debit / supplier
            // credit). Returns reduce the balance and never block. A
            // violation aborts the whole posting transaction. The guard is
            // fed the SAME stored total that is about to post (Step 5) —
            // before, it received the recomputed lines-only figure.
            // Step 7: the guard compares in BASE currency (it sums
            // base_debit/base_credit), so it is fed the document's base
            // exposure — stored total x the effective rate resolved
            // above — never the raw foreign total.
            var exposureIncrease = effects.CreditGuard ? (partyKind == "C" ? debit : credit) : 0m;
            // W23 carry-over: the credit override context is non-null only
            // when the posting user confirmed the override in InvoiceWin
            // AND holds SALES.CREDIT.OVERRIDE — the guard then audits the
            // breach in credit_overrides instead of hard-blocking ACC-5006
            // (the OrdWin pattern from W1, now on the native path too).
            PartyCreditGuard.EnsureWithinLimit(
                connection, tx,
                DatabaseRuntime.IsPostgreSql() ? "PostgreSQL" : "SQLite",
                Company, partyKind, partyId,
                string.IsNullOrWhiteSpace(doc.PartyCode) ? null : doc.PartyCode,
                FxRates.ToBase(exposureIncrease, fx.Rate), "$",
                creditOverride is null ? null : creditOverride with { DocKind = doc.KindCode, DocNo = doc.DocNo });

            // Withholding (W1): the supplier's ledger balance is NET —
            // a purchase invoice credits (gross − withheld) because the
            // withheld part is owed to the tax authority, not the
            // supplier; a return reversing a withheld invoice debits the
            // same net figure, restoring the balance exactly. The
            // credit guard above still sees the GROSS exposure (the
            // total liability the invoice creates across AP + WHT).
            var ledgerDebit = kind == InvDocKind.PurchaseReturn && reversalWithholding.Amount > 0m
                ? debit - reversalWithholding.Amount : debit;
            var ledgerCredit = kind == InvDocKind.PurchaseInvoice && withholding.Amount > 0m
                ? credit - withholding.Amount : credit;

            // Step 7: the ledger row carries its base amounts alongside
            // the transaction amounts (migration 062), at the same
            // effective rate the GL used.
            var ledgerHasBase = PartyLedger.ColumnExists(connection, tx, fxProvider, "party_ledger", "base_debit");
            if (kind == InvDocKind.SalesReturn)
            {
                // G16/G17: the credit note's own ledger row is written
                // through the shared PartyLedger writer so its id is
                // known — it becomes the allocation SOURCE that settles
                // the origin invoice (and, later, other open invoices
                // through PayRecWin). Amounts and base conversion are
                // identical to the raw insert below (same stored total,
                // same effective rate).
                salesReturnLedgerId = PartyLedger.WriteEntry(
                    connection, tx, fxProvider,
                    Company, partyKind, partyId,
                    doc.KindCode, docId, doc.DocNo,
                    doc.DocDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    debit, credit, doc.CurrencyCode,
                    "Auto post",
                    exchangeRate: fx.Rate);
            }
            else
            {
            using var led = connection.CreateCommand();
            led.Transaction = tx;
            led.CommandText = ledgerHasBase
                ? """
                INSERT INTO party_ledger
                (company_id, party_kind, party_id, doc_kind, doc_id, doc_no, entry_date, debit, credit, currency_code, note, base_debit, base_credit, exchange_rate)
                VALUES
                ($company, $pk, $pid, $dk, $did, $dno, $date, $debit, $credit, $cur, $note, $baseDebit, $baseCredit, $rate);
                """
                : """
                INSERT INTO party_ledger
                (company_id, party_kind, party_id, doc_kind, doc_id, doc_no, entry_date, debit, credit, currency_code, note)
                VALUES
                ($company, $pk, $pid, $dk, $did, $dno, $date, $debit, $credit, $cur, $note);
                """;
            Add(led, "$company", Company);
            Add(led, "$pk", partyKind);
            Add(led, "$pid", partyId);
            Add(led, "$dk", doc.KindCode);
            Add(led, "$did", docId);
            Add(led, "$dno", doc.DocNo);
            Add(led, "$date", doc.DocDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            Add(led, "$debit", ledgerDebit);
            Add(led, "$credit", ledgerCredit);
            Add(led, "$cur", doc.CurrencyCode);
            Add(led, "$note", "Auto post");
            if (ledgerHasBase)
            {
                Add(led, "$baseDebit", FxRates.ToBase(ledgerDebit, fx.Rate));
                Add(led, "$baseCredit", FxRates.ToBase(ledgerCredit, fx.Rate));
                Add(led, "$rate", fx.Rate);
            }
            led.ExecuteNonQuery();
            }

            // Settlement at posting (Step 4, sweep B-03): money collected
            // on the document itself (paid_amount — POS cash sales carry
            // the full total) is a real party_ledger settlement entry in
            // this same transaction, pre-allocated to this document, so
            // the party balance falls by exactly the collected amount and
            // PayRecWin never sees it as an unallocated payment. The GL
            // cash/bank leg for the same money was posted by Step 3's
            // GlPosting above — the two stay consistent because both are
            // driven by the same paid_amount in the same transaction.
            // Returns are not payments and never take this path.
            if (effects.Settlement && doc.PaidAmount > 0m && kind is InvDocKind.SalesInvoice or InvDocKind.PurchaseInvoice or InvDocKind.PosSale)
            {
                // Withholding (W1): money collected at posting can only
                // settle the NET payable — the withheld part is never
                // payable to the supplier.
                var settled = Math.Min(doc.PaidAmount,
                    kind == InvDocKind.PurchaseInvoice ? doc.PostingTotal - withholding.Amount : doc.PostingTotal);
                if (settled > 0m)
                {
                    var (payDebit, payCredit) = PartyLedger.SettlementSides(partyKind, settled);
                    var paymentLedgerId = PartyLedger.WriteEntry(
                        connection, tx,
                        DatabaseRuntime.IsPostgreSql() ? "PostgreSQL" : "SQLite",
                        Company, partyKind, partyId,
                        isSales ? "RCV" : "PAY", docId, doc.DocNo,
                        doc.DocDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        payDebit, payCredit, doc.CurrencyCode,
                        "Collected at document posting",
                        exchangeRate: fx.Rate);

                    using var alloc = connection.CreateCommand();
                    alloc.Transaction = tx;
                    alloc.CommandText = """
                        INSERT INTO payment_allocations
                            (allocation_id, company_id, party_kind, party_id, payment_ledger_id, doc_table, doc_id, doc_no, amount, allocated_by, note)
                        VALUES
                            ($id, $company, $pk, $pid, $payment, $table, $did, $dno, $amount, $user, $note);
                        """;
                    Add(alloc, "$id", Guid.NewGuid().ToString("D"));
                    Add(alloc, "$company", Company);
                    Add(alloc, "$pk", partyKind);
                    Add(alloc, "$pid", partyId);
                    Add(alloc, "$payment", paymentLedgerId);
                    Add(alloc, "$table", isSales ? "SALES" : "PURCHASE");
                    Add(alloc, "$did", docId);
                    Add(alloc, "$dno", doc.DocNo);
                    Add(alloc, "$amount", settled);
                    Add(alloc, "$user", userId);
                    Add(alloc, "$note", "Collected at document posting");
                    alloc.ExecuteNonQuery();
                }
            }
        }

        // G16/G17/G18: an ordinary sales return (native window path —
        // not a POS-context return, whose unsettling above already
        // mirrors its settlement, and not a generated full reversal,
        // whose UnsettleOriginalOnReversal owns the original's
        // settlement) settles its origin invoice in THIS transaction:
        // the cash-refunded part leaves as a party-ledger debit mirror
        // (the POS-return pattern), and the remaining net credit is
        // auto-allocated from the credit note to the origin invoice so
        // its paid_amount — hence its open amount — falls by exactly
        // the credit the GL took off Receivable.
        if (kind == InvDocKind.SalesReturn
            && pos is null
            && salesReturnLedgerId is long creditNoteLedgerId
            && doc.OriginalDocKind == InvDocKind.SalesInvoice
            && doc.OriginalDocId is long originDocId
            && doc.PartyId is long returnPartyId
            && !IsGeneratedFullReversal(connection, tx, doc))
        {
            SettleSalesReturnAgainstOriginal(
                connection, tx, guidKey, doc, creditNoteLedgerId,
                originDocId, returnPartyId, fx.Rate, userId);
        }

        // (Inventory effects were applied before the GL block above —
        // Step 6 ordering, so COGS reads the engine's movement costs.)

        // W8: unified document event (migration 103), same transaction.
        if (doc is not null)
            DocEvents.Write(connection, tx, postProvider, guidKey,
                isSales ? "SALES" : "PURCHASE", doc.KindCode, docId, doc.DocNo,
                DocEventTypes.Posted, userId,
                $"grand_total={doc.GrandTotal.ToString(CultureInfo.InvariantCulture)} {doc.CurrencyCode}");

        tx.Commit();
    }

    /// <summary>
    /// G16/G17/G18 settlement of an ordinary sales return against its
    /// origin invoice, inside the return's posting transaction:
    /// 1. A cash/bank refund (PaidAmount &gt; 0) writes a party-ledger
    ///    DEBIT mirror on the return document — the exact mirror the
    ///    POS-return path writes for a refunded settlement — so the
    ///    party's net credit from this return is total − refund,
    ///    matching the GL (Cr cash/bank for the refund, Cr Receivable
    ///    only for the remainder, per BuildGlLines).
    /// 2. The remaining net credit is auto-allocated to the origin
    ///    invoice: a payment_allocations row whose SOURCE is the credit
    ///    note's own party-ledger entry (the G17 model — the same row
    ///    PayRecWin can later allocate to other invoices), capped at
    ///    the origin's open amount, and the origin's paid_amount rises
    ///    by the allocated amount in the same transaction. Document
    ///    position and party-ledger position therefore agree.
    /// </summary>
    private static void SettleSalesReturnAgainstOriginal(
        DbConnection connection, DbTransaction tx, string companyGuidKey,
        InvDoc returnDoc, long creditNoteLedgerId,
        long originDocId, long partyId, decimal effectiveRate, string userId)
    {
        var provider = DatabaseRuntime.IsPostgreSql() ? "PostgreSQL" : "SQLite";
        var notDeleted = provider == "PostgreSQL" ? "is_deleted = FALSE" : "is_deleted = 0";

        string originCompanyKey;
        string originDocNo;
        decimal originTotal, originPaid;
        using (var origin = connection.CreateCommand())
        {
            origin.Transaction = tx;
            origin.CommandText = $"""
                SELECT CAST(company_id AS TEXT), doc_no, grand_total, paid_amount
                FROM sales_documents
                WHERE doc_id = $id AND (company_id = $company OR company_id = $guid)
                  AND status = 'Posted' AND {notDeleted}
                {(provider == "PostgreSQL" ? "FOR UPDATE" : string.Empty)}
                """;
            Add(origin, "$id", originDocId);
            Add(origin, "$company", Company);
            Add(origin, "$guid", companyGuidKey);
            using var reader = origin.ExecuteReader();
            if (!reader.Read())
                throw new InvalidOperationException("The origin invoice was not found as posted in the current company; the return was rolled back.");
            originCompanyKey = Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture) ?? Company;
            originDocNo = Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) ?? string.Empty;
            originTotal = Convert.ToDecimal(reader.GetValue(2), CultureInfo.InvariantCulture);
            originPaid = Convert.ToDecimal(reader.GetValue(3), CultureInfo.InvariantCulture);
        }

        var refund = Math.Min(Math.Max(returnDoc.PaidAmount, 0m), returnDoc.PostingTotal);
        if (refund > 0m)
        {
            PartyLedger.WriteEntry(
                connection, tx, provider,
                Company, "C", partyId,
                returnDoc.KindCode, returnDoc.DocId, returnDoc.DocNo,
                returnDoc.DocDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                refund, 0m, returnDoc.CurrencyCode,
                "Sales return refund paid to customer",
                exchangeRate: effectiveRate);
        }

        var netCredit = returnDoc.PostingTotal - refund;
        var openAmount = Math.Max(0m, originTotal - originPaid);
        var allocate = Math.Min(netCredit, openAmount);
        if (allocate <= 0m) return;

        var hasDocumentCompany = PartyLedger.ColumnExists(
            connection, tx, provider, "payment_allocations", "document_company_id");
        using (var alloc = connection.CreateCommand())
        {
            alloc.Transaction = tx;
            alloc.CommandText = hasDocumentCompany
                ? """
                  INSERT INTO payment_allocations
                      (allocation_id, company_id, document_company_id, party_kind, party_id, payment_ledger_id, doc_table, doc_id, doc_no, amount, allocated_by, note)
                  VALUES
                      ($id, $company, $documentCompany, 'C', $party, $payment, 'SALES', $did, $dno, $amount, $user, $note);
                  """
                : """
                  INSERT INTO payment_allocations
                      (allocation_id, company_id, party_kind, party_id, payment_ledger_id, doc_table, doc_id, doc_no, amount, allocated_by, note)
                  VALUES
                      ($id, $company, 'C', $party, $payment, 'SALES', $did, $dno, $amount, $user, $note);
                  """;
            Add(alloc, "$id", Guid.NewGuid().ToString("D"));
            Add(alloc, "$company", Company);
            if (hasDocumentCompany)
                Add(alloc, "$documentCompany", originCompanyKey);
            Add(alloc, "$party", partyId);
            Add(alloc, "$payment", creditNoteLedgerId);
            Add(alloc, "$did", originDocId);
            Add(alloc, "$dno", originDocNo);
            Add(alloc, "$amount", allocate);
            Add(alloc, "$user", userId);
            Add(alloc, "$note", $"Credit note {returnDoc.DocNo} auto-allocated to origin invoice");
            alloc.ExecuteNonQuery();
        }

        using (var settle = connection.CreateCommand())
        {
            settle.Transaction = tx;
            settle.CommandText = $"""
                UPDATE sales_documents
                SET paid_amount = paid_amount + $amount
                WHERE doc_id = $id AND CAST(company_id AS TEXT) = $documentCompany AND {notDeleted};
                """;
            Add(settle, "$amount", allocate);
            Add(settle, "$id", originDocId);
            Add(settle, "$documentCompany", originCompanyKey);
            if (settle.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("The origin invoice could not be settled by the credit note; the return was rolled back.");
        }
    }

    // ---------------------------------------------------------------
    // Build POS 066: session / tender gate + tender writes
    // ---------------------------------------------------------------

    /// <summary>
    /// Mirrors only the settlement created when the original POS sale was
    /// posted. The refund, mirrored party-ledger entry, original paid amount
    /// and the exact originating allocation are updated under the caller's
    /// document transaction. External receipt allocations are deliberately
    /// preserved.
    /// </summary>
    private static void UnsettleOriginalOnPosReturn(
        DbConnection connection, DbTransaction tx, string companyGuidKey,
        long originalDocId, long partyId, InvDoc returnDoc,
        decimal refundCollected)
    {
        if (refundCollected <= 0m) return;

        var provider = DatabaseRuntime.IsPostgreSql() ? "PostgreSQL" : "SQLite";
        var notDeleted = provider == "PostgreSQL" ? "is_deleted = FALSE" : "is_deleted = 0";
        decimal originalPaid;
        using (var original = connection.CreateCommand())
        {
            original.Transaction = tx;
            original.CommandText = provider == "PostgreSQL"
                ? $"""
                   SELECT paid_amount
                   FROM sales_documents
                   WHERE doc_id=$id AND (company_id=$company OR company_id=$guid)
                     AND status='Posted' AND {notDeleted}
                   FOR UPDATE;
                   """
                : $"""
                   SELECT paid_amount
                   FROM sales_documents
                   WHERE doc_id=$id AND (company_id=$company OR company_id=$guid)
                     AND status='Posted' AND {notDeleted};
                   """;
            Add(original, "$id", originalDocId);
            Add(original, "$company", Company);
            Add(original, "$guid", companyGuidKey);
            var value = original.ExecuteScalar();
            if (value is null or DBNull)
                throw new InvalidOperationException("The original POS sale is not posted in the current company.");
            originalPaid = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        }

        if (refundCollected > originalPaid)
            throw new InvalidOperationException(
                $"The POS refund {refundCollected} exceeds the original sale's remaining collected amount {originalPaid}.");

        var hasBase = PartyLedger.ColumnExists(connection, tx, provider, "party_ledger", "base_debit");
        var select = $"""
            SELECT ledger_id, debit, credit, currency_code{(hasBase ? ", base_debit, base_credit, exchange_rate" : string.Empty)}
            FROM party_ledger
            WHERE company_id=$company AND party_id=$party AND doc_id=$original
              AND doc_kind='RCV'
            ORDER BY ledger_id;
            """;
        var settlements = new List<(long LedgerId, decimal Debit, decimal Credit, string Currency,
            decimal? BaseDebit, decimal? BaseCredit, decimal Rate)>();
        using (var find = connection.CreateCommand())
        {
            find.Transaction = tx;
            find.CommandText = select;
            Add(find, "$company", Company);
            Add(find, "$party", partyId);
            Add(find, "$original", originalDocId);
            using var reader = find.ExecuteReader();
            while (reader.Read())
            {
                settlements.Add((
                    Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                    Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture),
                    Convert.ToDecimal(reader.GetValue(2), CultureInfo.InvariantCulture),
                    reader.IsDBNull(3) ? returnDoc.CurrencyCode : reader.GetString(3),
                    hasBase && !reader.IsDBNull(4) ? Convert.ToDecimal(reader.GetValue(4), CultureInfo.InvariantCulture) : null,
                    hasBase && !reader.IsDBNull(5) ? Convert.ToDecimal(reader.GetValue(5), CultureInfo.InvariantCulture) : null,
                    hasBase && !reader.IsDBNull(6) ? Convert.ToDecimal(reader.GetValue(6), CultureInfo.InvariantCulture) : returnDoc.ExchangeRate));
            }
        }

        var remaining = refundCollected;
        foreach (var settlement in settlements)
        {
            if (remaining <= 0m) break;
            var settledAmount = Math.Max(settlement.Debit, settlement.Credit);
            var mirrorAmount = Math.Min(settledAmount, remaining);
            if (mirrorAmount <= 0m) continue;

            var (mirrorDebit, mirrorCredit) = settlement.Credit >= settlement.Debit
                ? (mirrorAmount, 0m)
                : (0m, mirrorAmount);

            if (settlement.BaseDebit.HasValue && settlement.BaseCredit.HasValue)
            {
                PartyLedger.WriteEntry(
                    connection, tx, provider, Company, "C", partyId,
                    returnDoc.KindCode, returnDoc.DocId, returnDoc.DocNo,
                    returnDoc.DocDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    mirrorDebit, mirrorCredit, settlement.Currency,
                    $"POS return refund of settlement collected at posting for {returnDoc.OriginalDocNo}",
                    exchangeRate: settlement.Rate,
                    baseDebit: settlement.BaseCredit,
                    baseCredit: settlement.BaseDebit);
            }
            else
            {
                PartyLedger.WriteEntry(
                    connection, tx, provider, Company, "C", partyId,
                    returnDoc.KindCode, returnDoc.DocId, returnDoc.DocNo,
                    returnDoc.DocDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    mirrorDebit, mirrorCredit, settlement.Currency,
                    $"POS return refund of settlement collected at posting for {returnDoc.OriginalDocNo}",
                    exchangeRate: settlement.Rate);
            }

            // Only reduce the allocation created by this exact settlement
            // ledger row. Allocations from later cash-receipt vouchers must
            // not be consumed by a POS refund.
            using (var allocation = connection.CreateCommand())
            {
                allocation.Transaction = tx;
                allocation.CommandText = """
                    UPDATE payment_allocations
                    SET amount = amount - $refund
                    WHERE company_id=$company AND doc_id=$original
                      AND doc_table='SALES' AND payment_ledger_id=$payment
                      AND amount >= $refund;
                    """;
                Add(allocation, "$refund", mirrorAmount);
                Add(allocation, "$company", Company);
                Add(allocation, "$original", originalDocId);
                Add(allocation, "$payment", settlement.LedgerId);
                if (allocation.ExecuteNonQuery() == 0)
                    throw new InvalidOperationException(
                        "The original POS settlement allocation is missing or smaller than the refund; the return was rolled back.");
            }
            using (var removeEmpty = connection.CreateCommand())
            {
                removeEmpty.Transaction = tx;
                removeEmpty.CommandText = """
                    DELETE FROM payment_allocations
                    WHERE company_id=$company AND doc_id=$original
                      AND doc_table='SALES' AND payment_ledger_id=$payment
                      AND amount <= 0;
                    """;
                Add(removeEmpty, "$company", Company);
                Add(removeEmpty, "$original", originalDocId);
                Add(removeEmpty, "$payment", settlement.LedgerId);
                removeEmpty.ExecuteNonQuery();
            }

            remaining -= mirrorAmount;
        }

        if (remaining > 0m)
            throw new InvalidOperationException(
                $"The original POS sale has no matching at-posting settlement for refund remainder {remaining}; the return was rolled back.");

        using (var updateOriginal = connection.CreateCommand())
        {
            updateOriginal.Transaction = tx;
            updateOriginal.CommandText = """
                UPDATE sales_documents
                SET paid_amount = paid_amount - $refund
                WHERE doc_id=$id AND (company_id=$company OR company_id=$guid)
                  AND status='Posted' AND paid_amount >= $refund;
                """;
            Add(updateOriginal, "$refund", refundCollected);
            Add(updateOriginal, "$id", originalDocId);
            Add(updateOriginal, "$company", Company);
            Add(updateOriginal, "$guid", companyGuidKey);
            if (updateOriginal.ExecuteNonQuery() != 1)
                throw new InvalidOperationException(
                    "The original POS sale settlement changed during refund posting; the return was rolled back.");
        }
    }

    /// <summary>Validates the POS context inside the posting
    /// transaction: the session must exist and be Open for this
    /// company (or session-less mode must be explicitly enabled), and
    /// the tenders must settle the document's STORED total exactly.
    /// Also stamps the transient split onto the loaded document so
    /// BuildGlLines posts the cash/card settlement legs honestly.</summary>
    private static void ValidatePosContext(DbConnection connection, DbTransaction tx, InvDoc doc, PosPostingContext pos)
    {
        var notDeleted = DatabaseRuntime.IsPostgreSql() ? "is_deleted = false" : "is_deleted = 0";
        if (pos.SessionId is Guid sessionId)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"SELECT status FROM pos_sessions WHERE company_id=$company AND id=$id AND {notDeleted};";
            Add(cmd, "$company", Company);
            Add(cmd, "$id", sessionId.ToString("D"));
            var status = cmd.ExecuteScalar();
            if (status is null or DBNull)
                throw new AppError(ErrorCodes.PosNoOpenSession,
                    "The POS session for this sale was not found for the current company.", sessionId.ToString("D"));
            if (!string.Equals(Convert.ToString(status, CultureInfo.InvariantCulture), "Open", StringComparison.Ordinal))
                throw new AppError(ErrorCodes.PosNoOpenSession,
                    "The POS session for this sale is not open; no sale can post outside an open session.", sessionId.ToString("D"));
        }
        else
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT allow_sessionless FROM pos_settings WHERE company_id=$company;";
            Add(cmd, "$company", Company);
            var allowed = cmd.ExecuteScalar();
            var isAllowed = allowed is not null and not DBNull && Convert.ToBoolean(allowed, CultureInfo.InvariantCulture);
            if (!isAllowed)
                throw new AppError(ErrorCodes.PosNoOpenSession,
                    "No POS session is open and this company has not enabled session-less selling.");
        }

        if (doc.Kind == InvDocKind.PosSale)
        {
            ShouTech.Domain.Entities.PosTenderMath.ValidateSaleTenders(doc.PostingTotal, pos.Tenders, doc.PartyId is not null, pos.ExchangeCredit);
            var collected = ShouTech.Domain.Entities.PosTenderMath.CollectedAmount(pos.Tenders);
            if (ShouTech.Domain.Entities.PosTenderMath.Round(doc.PaidAmount) != ShouTech.Domain.Entities.PosTenderMath.Round(collected))
                throw new AppError(ErrorCodes.PosTenderMismatch,
                    $"The document paid_amount {doc.PaidAmount} does not equal the collected tenders {collected} (cash + card).");
            doc.PosCashApplied = ShouTech.Domain.Entities.PosTenderMath.CashApplied(pos.Tenders);
            doc.PosCardApplied = ShouTech.Domain.Entities.PosTenderMath.CardApplied(pos.Tenders);
        }
        else if (doc.Kind == InvDocKind.SalesReturn)
        {
            // Refund rows carry NEGATIVE amounts; their magnitudes must
            // settle the return total, and the collected refund must
            // equal the return document's paid_amount (its GL mirror).
            foreach (var tender in pos.Tenders)
                if (tender.Amount > 0)
                    throw new AppError(ErrorCodes.PosTenderInvalid,
                        "A POS return tender is a refund and must carry a negative amount.");
            var total = -pos.Tenders.Sum(t => t.Amount);
            var collected = -(ShouTech.Domain.Entities.PosTenderMath.CashApplied(pos.Tenders) + ShouTech.Domain.Entities.PosTenderMath.CardApplied(pos.Tenders));
            if (ShouTech.Domain.Entities.PosTenderMath.Round(total) != ShouTech.Domain.Entities.PosTenderMath.Round(doc.PostingTotal))
                throw new AppError(ErrorCodes.PosTenderMismatch,
                    $"Refund tenders {total} do not settle the return total {doc.PostingTotal}.");
            if (ShouTech.Domain.Entities.PosTenderMath.Round(collected) != ShouTech.Domain.Entities.PosTenderMath.Round(doc.PaidAmount))
                throw new AppError(ErrorCodes.PosTenderMismatch,
                    $"The return paid_amount {doc.PaidAmount} does not equal the refunded cash + card {collected}.");
            doc.PosCashApplied = -ShouTech.Domain.Entities.PosTenderMath.CashApplied(pos.Tenders);
            doc.PosCardApplied = -ShouTech.Domain.Entities.PosTenderMath.CardApplied(pos.Tenders);
        }
    }

    /// <summary>Writes the context's tender rows and the document's
    /// session link inside the posting transaction (the PosRepository
    /// INSERT shape — one shape, two writers, indistinguishable rows).</summary>
    private static void WritePosTenders(DbConnection connection, DbTransaction tx, InvDoc doc, PosPostingContext pos, string userId)
    {
        foreach (var tender in pos.Tenders)
        {
            if (tender.Amount == 0m && tender.TenderType != ShouTech.Domain.Entities.PosTenderTypes.Cash) continue;
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO pos_sale_tenders
                    (id, company_id, session_id, doc_id, doc_no, doc_kind, tender_type,
                     amount, tendered_amount, change_amount, card_ref, created_by)
                VALUES ($id, $company, $session, $doc, $docno, $kind, $type,
                        $amount, $tendered, $change, $ref, $user);
                """;
            Add(cmd, "$id", Guid.NewGuid().ToString("D"));
            Add(cmd, "$company", Company);
            Add(cmd, "$session", pos.SessionId?.ToString("D"));
            Add(cmd, "$doc", doc.DocId);
            Add(cmd, "$docno", doc.DocNo);
            Add(cmd, "$kind", doc.KindCode);
            Add(cmd, "$type", tender.TenderType);
            Add(cmd, "$amount", tender.Amount);
            Add(cmd, "$tendered", tender.TenderedAmount);
            Add(cmd, "$change", tender.ChangeAmount);
            Add(cmd, "$ref", string.IsNullOrWhiteSpace(tender.CardRef) ? null : tender.CardRef.Trim());
            Add(cmd, "$user", userId);
            cmd.ExecuteNonQuery();
        }
        if (pos.SessionId is Guid sessionId)
        {
            using var link = connection.CreateCommand();
            link.Transaction = tx;
            link.CommandText = """
                UPDATE sales_documents SET pos_session_id=$session
                WHERE doc_id=$id AND (company_id=$company OR company_id=$guid);
                """;
            Add(link, "$session", sessionId.ToString("D"));
            Add(link, "$id", doc.DocId);
            Add(link, "$company", Company);
            Add(link, "$guid", ResolveCompanyGuidOrNull(connection, tx)?.ToString("D") ?? Company);
            link.ExecuteNonQuery();
        }
    }

    // ---------------------------------------------------------------
    // Step 9: dedicated-engine posting for StockTransfer / InventoryCount
    // ---------------------------------------------------------------

    /// <summary>
    /// Posts a StockTransfer or InventoryCount document through its
    /// dedicated engine (StkPost.PostTransfer / StkPost.PostCount).
    /// The engines own their connection + transaction and take no
    /// document id, so the document's status flip cannot share their
    /// transaction; it follows the claim → post → compensate pattern
    /// Step 8 established for cheque lifecycle events:
    ///   1. CLAIM: the draft flips to Posted (guarded on Draft). A
    ///      second post attempt now fails honestly instead of moving
    ///      stock twice.
    ///   2. ENGINE: the transfer/count posts. Its transaction is
    ///      atomic — a failure moves no stock at all.
    ///   3. COMPENSATE: if the engine failed, the claim is reverted to
    ///      Draft so the user can correct and retry; if the revert
    ///      itself fails, an explicit stranded-state error is thrown
    ///      rather than swallowing either failure.
    /// Transfers carry no GL and no party effect (stock value is
    /// conserved inside the inventory account); counts adjust stock
    /// only — exactly the effects InvPostingPolicy records for them.
    /// </summary>
    private static void PostDedicatedEngineDocument(long docId, InvDocKind kind, string userId,
        IReadOnlyDictionary<int, LineBatchSerialCapture>? batchSerialByLine = null)
    {
        var doc = Load(docId, kind)
            ?? throw new InvalidOperationException("Document not found.");
        if (doc.Status == InvDocStatus.Posted)
            throw new InvalidOperationException("Document is already posted.");
        if (doc.Status is not (InvDocStatus.Draft or InvDocStatus.Posting))
            throw new InvalidOperationException("Only Draft or recoverable Posting documents can be posted.");
        if (doc.Lines.Count == 0)
            throw new InvalidOperationException("The document has no lines to post.");
        if (batchSerialByLine is not null)
        {
            foreach (var line in doc.Lines)
            {
                if (batchSerialByLine.TryGetValue(line.LineNo, out var capture))
                {
                    line.BatchAllocations = capture.Batches;
                    line.Serials = capture.Serials;
                    line.AllowExpiredBatchIssue = capture.AllowExpiredBatchIssue;
                }
            }
        }

        var operationKey = $"INV:{kind}:{docId.ToString(CultureInfo.InvariantCulture)}";
        var wasAlreadyClaimed = doc.Status == InvDocStatus.Posting;
        if (wasAlreadyClaimed && !HasDedicatedPostClaim(docId, kind, operationKey))
            throw new InvalidOperationException(
                $"Document '{doc.DocNo}' is in Posting without a matching durable recovery key. It may have been posted by an earlier application version; reconcile its stock movements before retrying.");
        if (!wasAlreadyClaimed)
            ClaimDedicatedPost(docId, kind, userId, operationKey);

        string engineDocNo;
        try
        {
            if (kind == InvDocKind.StockTransfer)
            {
                engineDocNo = PostTransferForDocument(doc, operationKey);
            }
            else
            {
                PostCountForDocument(doc, operationKey);
                engineDocNo = string.Empty;
            }
        }
        catch (Exception engineFailure)
        {
            var operationType = kind == InvDocKind.StockTransfer ? "StockTransfer" : "InventoryCount";
            if (StkPost.IsOperationCompleted(operationKey, operationType))
                throw new InvalidOperationException(
                    $"Stock effects for document '{doc.DocNo}' were committed, but posting recovery could not continue. The document remains Posting; do not retry manually. Reconcile it before further processing.",
                    engineFailure);

            try
            {
                RevertDedicatedClaim(docId, kind);
            }
            catch (Exception revertFailure)
            {
                throw new InvalidOperationException(
                    $"Posting document '{doc.DocNo}' failed ({engineFailure.Message}) and its Posting claim could not be reverted to Draft. Reconcile the document before retrying.",
                    revertFailure);
            }
            throw;
        }

        // The stock engine has committed its own transaction. Never revert
        // to Draft after this point: doing so would allow a retry to duplicate
        // stock. If finalization fails or the process stops here, the durable
        // Posting state exposes the reconciliation requirement instead of
        // falsely presenting the document as Posted or Draft.
        try
        {
            CompleteDedicatedPost(docId, kind, userId);
        }
        catch (Exception finalizeFailure)
        {
            throw new InvalidOperationException(
                $"Stock effects for document '{doc.DocNo}' were committed, but its posting state could not be finalized. The document remains Posting; do not retry. Reconcile it before further processing.",
                finalizeFailure);
        }

        if (!string.IsNullOrEmpty(engineDocNo))
            LinkEngineDocumentNo(docId, kind, engineDocNo);
    }

    private static string PostTransferForDocument(InvDoc doc, string operationKey)
    {
        if (doc.WarehouseId is not int sourceId)
            throw new StkPost.StockTransferException("INV-TRF-001", "A source warehouse is required for a stock transfer.");
        if (doc.TargetWarehouseId is not int targetId)
            throw new StkPost.StockTransferException("INV-TRF-001", "A destination warehouse is required for a stock transfer. Set the destination warehouse on the document and save it before posting.");
        var reason = string.IsNullOrWhiteSpace(doc.Notes) ? doc.DocNo : doc.Notes.Trim();

        var cs = DatabaseRuntime.GetConnectionString()
            ?? throw new InvalidOperationException("No database connection.");
        var lines = new List<StkPost.StockTransferLine>();
        using (var connection = Open(cs))
        using (var tx = connection.BeginTransaction())
        {
            foreach (var line in doc.Lines)
            {
                if (line.Qty == 0) continue;
                var materialId = ResolveLineMaterialId(connection, tx, line);
                var (baseQty, _) = StkPost.ToBaseUnits(connection, tx, materialId, line.Unit, line.Qty, line.UnitPrice);
                if (baseQty <= 0)
                    throw new StkPost.StockTransferException("INV-TRF-001", $"Enter a valid positive quantity for '{line.MaterialCode}'.");
                lines.Add(new StkPost.StockTransferLine(materialId, line.MaterialCode, baseQty));
            }
        }
        if (lines.Count == 0)
            throw new StkPost.StockTransferException("INV-TRF-001", "Add at least one material quantity to transfer.");

        return StkPost.PostTransfer(sourceId, targetId, doc.DocDate, reason, lines, operationKey);
    }

    private static void PostCountForDocument(InvDoc doc, string operationKey)
    {
        if (doc.WarehouseId is not int warehouseId)
            throw new InvalidOperationException("A warehouse is required for an inventory count document.");
        var reason = string.IsNullOrWhiteSpace(doc.Notes) ? doc.DocNo : doc.Notes.Trim();

        var cs = DatabaseRuntime.GetConnectionString()
            ?? throw new InvalidOperationException("No database connection.");
        var lines = new List<StkPost.StockCountLine>();
        using (var connection = Open(cs))
        using (var tx = connection.BeginTransaction())
        {
            foreach (var line in doc.Lines)
            {
                // A counted quantity of zero is a real count (the shelf
                // is empty) — it posts the negative delta through
                // PostCount like any other line; only lines with no
                // material identity at all are unusable.
                var materialId = ResolveLineMaterialId(connection, tx, line);
                var (baseQty, baseUnitCost) = StkPost.ToBaseUnits(connection, tx, materialId, line.Unit, line.Qty, line.UnitPrice);
                // Batch capture (054) restated per base unit like the
                // counted quantity itself.
                IReadOnlyList<ShouTech.Infrastructure.BatchAllocationRequest>? countBatches = null;
                if (line.BatchAllocations.Count > 0)
                {
                    var converted = new List<ShouTech.Infrastructure.BatchAllocationRequest>();
                    foreach (var b in line.BatchAllocations)
                    {
                        var (bBase, _) = StkPost.ToBaseUnits(connection, tx, materialId, line.Unit, b.Qty, 0m);
                        converted.Add(new ShouTech.Infrastructure.BatchAllocationRequest(
                            b.BatchNo, bBase, b.ExpiryDate, b.MfgDate, b.SupplierBatchNo));
                    }
                    countBatches = converted;
                }
                lines.Add(new StkPost.StockCountLine(materialId, line.MaterialCode, baseQty, baseUnitCost,
                    countBatches,
                    line.Serials.Count > 0 ? line.Serials : null));
            }
        }
        if (lines.Count == 0)
            throw new InvalidOperationException("The document has no lines to post.");

        StkPost.PostCount(warehouseId, doc.DocDate, reason, lines, operationKey);
    }

    /// <summary>The material id for a document line: the stored id when
    /// present, else resolved from the typed code in the current company
    /// (the same resolution StkPost.ApplyDocument performs). An
    /// unresolvable line fails the post honestly instead of moving the
    /// wrong stock.</summary>
    private static long ResolveLineMaterialId(DbConnection connection, DbTransaction tx, InvLine line)
    {
        if (line.MaterialId is long id && id > 0) return id;
        if (string.IsNullOrWhiteSpace(line.MaterialCode))
            throw new InvalidOperationException("A document line does not identify a material.");
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = DatabaseRuntime.IsPostgreSql()
            ? "SELECT material_id FROM materials WHERE company_id=$c AND code=$code AND is_deleted=FALSE LIMIT 1;"
            : "SELECT material_id FROM materials WHERE company_id=$c AND code=$code AND is_deleted=0 LIMIT 1;";
        Add(cmd, "$c", Company);
        Add(cmd, "$code", line.MaterialCode.Trim());
        var value = cmd.ExecuteScalar();
        if (value is null or DBNull)
            throw new InvalidOperationException($"Material '{line.MaterialCode}' does not exist in the current company.");
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static bool HasDedicatedPostClaim(long docId, InvDocKind kind, string operationKey)
    {
        var headerTable = kind.IsSales() ? "sales_documents" : "purchase_documents";
        var cs = DatabaseRuntime.GetConnectionString()
            ?? throw new InvalidOperationException("No database connection.");
        using var connection = Open(cs);
        var guidKey = ResolveCompanyGuidOrNull(connection, null)?.ToString("D") ?? Company;
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT posting_operation_key
            FROM {headerTable}
            WHERE doc_id=$id AND (company_id=$company OR company_id=$guid) AND status='Posting';
            """;
        Add(command, "$id", docId);
        Add(command, "$company", Company);
        Add(command, "$guid", guidKey);
        var value = command.ExecuteScalar();
        return value is not null and not DBNull
            && string.Equals(Convert.ToString(value, CultureInfo.InvariantCulture),
                operationKey, StringComparison.Ordinal);
    }

    private static void ClaimDedicatedPost(long docId, InvDocKind kind, string userId, string operationKey)
        => SetDedicatedStatus(docId, kind, from: "Draft", to: "Posting", userId: null,
            operationKey: operationKey,
            failureMessage: "The document is not a Draft or another posting operation has already claimed it.");

    private static void RevertDedicatedClaim(long docId, InvDocKind kind)
        => SetDedicatedStatus(docId, kind, from: "Posting", to: "Draft", userId: null,
            operationKey: null,
            failureMessage: "The document posting claim could not be reverted to Draft.");

    private static void CompleteDedicatedPost(long docId, InvDocKind kind, string userId)
        => SetDedicatedStatus(docId, kind, from: "Posting", to: "Posted", userId: userId,
            operationKey: null,
            failureMessage: "Stock effects were committed, but the document could not be finalized as Posted. Reconcile before retrying.");

    private static void SetDedicatedStatus(long docId, InvDocKind kind, string from, string to,
        string? userId, string? operationKey, string failureMessage)
    {
        var headerTable = kind.IsSales() ? "sales_documents" : "purchase_documents";
        var cs = DatabaseRuntime.GetConnectionString()
            ?? throw new InvalidOperationException("No database connection.");
        using var connection = Open(cs);
        using var cmd = connection.CreateCommand();
        var guidKey = ResolveCompanyGuidOrNull(connection, null)?.ToString("D") ?? Company;
        cmd.CommandText = DatabaseRuntime.IsPostgreSql()
            ? $"""
               UPDATE {headerTable}
               SET status=$to,
                   posting_operation_key=CASE WHEN $to='Posting' THEN $operationKey WHEN $to='Draft' THEN NULL ELSE posting_operation_key END,
                   posted_by=CASE WHEN $to='Posted' THEN $user ELSE posted_by END,
                   posted_at=CASE WHEN $to='Posted' THEN NOW() ELSE posted_at END
               WHERE doc_id=$id AND (company_id=$company OR company_id=$guid) AND status=$from;
               """
            : $"""
               UPDATE {headerTable}
               SET status=$to,
                   posting_operation_key=CASE WHEN $to='Posting' THEN $operationKey WHEN $to='Draft' THEN NULL ELSE posting_operation_key END,
                   posted_by=CASE WHEN $to='Posted' THEN $user ELSE posted_by END,
                   posted_at=CASE WHEN $to='Posted' THEN datetime('now') ELSE posted_at END
               WHERE doc_id=$id AND (company_id=$company OR company_id=$guid) AND status=$from;
               """;
        Add(cmd, "$to", to);
        Add(cmd, "$operationKey", operationKey);
        Add(cmd, "$user", to == "Posted" ? userId : null);
        Add(cmd, "$id", docId);
        Add(cmd, "$company", Company);
        Add(cmd, "$guid", guidKey);
        Add(cmd, "$from", from);
        if (cmd.ExecuteNonQuery() == 0)
            throw new InvalidOperationException(failureMessage);
    }

    /// <summary>Records the transfer engine's own document number on the
    /// posted transfer document (its journal_doc_no slot — the trace
    /// from the document to its TRANSFER_OUT / TRANSFER_IN movements).
    /// The stock already committed inside the engine's transaction, so
    /// a failure here is reported centrally but does not un-post the
    /// transfer.</summary>
    private static void LinkEngineDocumentNo(long docId, InvDocKind kind, string engineDocNo)
    {
        try
        {
            var headerTable = kind.IsSales() ? "sales_documents" : "purchase_documents";
            var cs = DatabaseRuntime.GetConnectionString()
                ?? throw new InvalidOperationException("No database connection.");
            using var connection = Open(cs);
            using var cmd = connection.CreateCommand();
            var guidKey = ResolveCompanyGuidOrNull(connection, null)?.ToString("D") ?? Company;
            cmd.CommandText = $"""
                UPDATE {headerTable}
                SET journal_doc_no=$jrn
                WHERE doc_id=$id AND (company_id=$company OR company_id=$guid) AND status='Posted';
                """;
            Add(cmd, "$jrn", engineDocNo);
            Add(cmd, "$id", docId);
            Add(cmd, "$company", Company);
            Add(cmd, "$guid", guidKey);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.InventoryOperationFailed, ex, "InvRepo.LinkEngineDocumentNo");
        }
    }

    /// <summary>
    /// Step 9: the current company's Guid key for the document tables,
    /// or null when it cannot be resolved. The tables carry two company
    /// keys — InvRepo's own company CODE and the order/chain family's
    /// Guid (steps 4–8) — and a document read must accept either of the
    /// CURRENT company's keys, or DocSearch-opened documents (converted
    /// invoices, chain quotations: Guid-keyed) load as "not found" and
    /// the window falls back to a blank new document. Resolution
    /// failure is reported centrally and degrades to the code key only.
    /// </summary>
    private static Guid? ResolveCompanyGuidOrNull(DbConnection connection, DbTransaction? tx)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            if (tx is not null) cmd.Transaction = tx;
            cmd.CommandText = "SELECT company_id FROM companies WHERE company_code=$company;";
            Add(cmd, "$company", Company);
            var value = cmd.ExecuteScalar();
            if (value is null or DBNull) return null;
            return Guid.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var guid) && guid != Guid.Empty
                ? guid
                : null;
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.InventoryOperationFailed, ex, "InvRepo.ResolveCompanyGuidOrNull");
            return null;
        }
    }

    public static long? FindAdjacentDocumentId(long currentDocId, InvDocKind kind, int offset)
    {
        if (offset is not (-10 or -1 or 1 or 10))
            throw new ArgumentOutOfRangeException(nameof(offset), "Navigation offset must be -10, -1, 1, or 10.");

        var connectionString = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("No database connection.");

        var table = kind.IsSales() ? "sales_documents" : "purchase_documents";
        var order = offset < 0 ? "DESC" : "ASC";
        var comparison = offset < 0 ? "<" : ">";
        var skip = Math.Abs(offset) - 1;
        using var connection = Open(connectionString);
        var companyGuid = ResolveCompanyGuidOrNull(connection, null)?.ToString("D") ?? Company;
        using var cmd = connection.CreateCommand();
        cmd.CommandText = currentDocId > 0
            ? $"""
                SELECT doc_id FROM {table}
                WHERE (company_id=$company OR company_id=$guid)
                  AND doc_kind=$kind AND is_deleted=0 AND doc_id {comparison} $current
                ORDER BY doc_id {order} LIMIT 1 OFFSET $skip;
                """
            : $"""
                SELECT doc_id FROM {table}
                WHERE (company_id=$company OR company_id=$guid)
                  AND doc_kind=$kind AND is_deleted=0
                ORDER BY doc_id {order} LIMIT 1;
                """;
        Add(cmd, "$company", Company);
        Add(cmd, "$guid", companyGuid);
        Add(cmd, "$kind", new InvDoc { Kind = kind }.KindCode);
        if (currentDocId > 0)
        {
            Add(cmd, "$current", currentDocId);
            Add(cmd, "$skip", skip);
        }
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    public static InvDoc? LoadByNumber(string docNo, InvDocKind kind)
    {
        if (string.IsNullOrWhiteSpace(docNo)) return null;
        DocSchema.Ensure();
        var table = kind.IsSales() ? "sales_documents" : "purchase_documents";
        var connectionString = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString)) return null;

        long? docId = null;
        using (var connection = Open(connectionString))
        {
            var guidKey = ResolveCompanyGuidOrNull(connection, null)?.ToString("D") ?? Company;
            using var command = connection.CreateCommand();
            var notDeleted = DatabaseRuntime.IsPostgreSql() ? "is_deleted = FALSE" : "is_deleted = 0";
            command.CommandText = $"""
                SELECT doc_id
                FROM {table}
                WHERE (company_id = $company OR company_id = $guid)
                  AND doc_kind = $kind AND doc_no = $docNo
                  AND status = 'Posted' AND {notDeleted}
                ORDER BY doc_id DESC
                LIMIT 1
                """;
            Add(command, "$company", Company);
            Add(command, "$guid", guidKey);
            Add(command, "$kind", new InvDoc { Kind = kind }.KindCode);
            Add(command, "$docNo", docNo.Trim());
            var value = command.ExecuteScalar();
            if (value is not null and not DBNull)
                docId = Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }

        return docId is long id ? Load(id, kind) : null;
    }

    public static IReadOnlyDictionary<int, decimal> GetAlreadyReturnedQuantities(
        long originalDocId, InvDocKind originalKind)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(originalDocId);
        if (originalKind is not (InvDocKind.SalesInvoice or InvDocKind.PosSale))
            throw new ArgumentOutOfRangeException(nameof(originalKind));

        var connectionString = DatabaseRuntime.GetConnectionString()
            ?? throw new InvalidOperationException("No database connection.");
        using var connection = Open(connectionString);
        var guidKey = ResolveCompanyGuidOrNull(connection, null)?.ToString("D") ?? Company;
        var notDeleted = DatabaseRuntime.IsPostgreSql() ? "header.is_deleted = FALSE" : "header.is_deleted = 0";
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT line.line_no, COALESCE(SUM(line.qty), 0)
            FROM sales_documents header
            INNER JOIN sales_document_lines line ON line.doc_id = header.doc_id
            WHERE (header.company_id = $company OR header.company_id = $guid)
              AND header.doc_kind = 'SRT'
              AND header.original_doc_id = $originalId
              AND header.original_doc_kind = $originalKind
              AND header.status = 'Posted'
              AND {notDeleted}
            GROUP BY line.line_no
            """;
        Add(command, "$company", Company);
        Add(command, "$guid", guidKey);
        Add(command, "$originalId", originalDocId);
        Add(command, "$originalKind", originalKind.ToString());

        var result = new Dictionary<int, decimal>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result[Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture)] =
                Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture);
        return result;
    }

    public static IReadOnlyDictionary<int, decimal> GetAlreadyReturnedPurchaseQuantities(long originalDocId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(originalDocId);
        var connectionString = DatabaseRuntime.GetConnectionString()
            ?? throw new InvalidOperationException("No database connection.");
        using var connection = Open(connectionString);
        var guidKey = ResolveCompanyGuidOrNull(connection, null)?.ToString("D") ?? Company;
        var notDeleted = DatabaseRuntime.IsPostgreSql() ? "returned.is_deleted = FALSE" : "returned.is_deleted = 0";
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT source.line_no, COALESCE(SUM(allocation.returned_qty), 0)
            FROM purchase_return_line_allocations allocation
            INNER JOIN purchase_document_lines source
                ON source.line_id = allocation.original_line_id
               AND source.doc_id = allocation.original_doc_id
            INNER JOIN purchase_documents returned
                ON returned.doc_id = allocation.return_doc_id
               AND CAST(returned.company_id AS TEXT) = allocation.company_id
            WHERE allocation.company_id IN ($company, $guid)
              AND allocation.original_doc_id = $originalId
              AND returned.doc_kind = 'PRT'
              AND returned.status = 'Posted'
              AND {notDeleted}
            GROUP BY source.line_no
            """;
        Add(command, "$company", Company);
        Add(command, "$guid", guidKey);
        Add(command, "$originalId", originalDocId);
        var result = new Dictionary<int, decimal>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result[Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture)] =
                Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture);
        return result;
    }

    public static void RestoreSalesReturnLineNumbers(long returnDocId, IReadOnlyList<int> originalLineNumbers)
        => RestoreReturnLineNumbers(returnDocId, InvDocKind.SalesReturn, originalLineNumbers);

    /// <summary>
    /// Restores source line identities on a saved return document. The
    /// document side determines the fixed table pair; no identifier comes
    /// from user input. This supports both sales and purchase reversals.
    /// </summary>
    public static void RestoreReturnLineNumbers(
        long returnDocId,
        InvDocKind returnKind,
        IReadOnlyList<int> originalLineNumbers)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(returnDocId);
        ArgumentNullException.ThrowIfNull(originalLineNumbers);
        if (returnKind is not (InvDocKind.SalesReturn or InvDocKind.PurchaseReturn))
            throw new ArgumentException("Only sales and purchase return documents can restore source line numbers.", nameof(returnKind));

        var headerTable = returnKind.IsSales() ? "sales_documents" : "purchase_documents";
        var lineTable = returnKind.IsSales() ? "sales_document_lines" : "purchase_document_lines";
        var notDeleted = DatabaseRuntime.IsPostgreSql() ? "is_deleted = FALSE" : "is_deleted = 0";
        var connectionString = DatabaseRuntime.GetConnectionString()
            ?? throw new InvalidOperationException("No database connection.");
        using var connection = Open(connectionString);
        using var transaction = connection.BeginTransaction();

        using (var verify = connection.CreateCommand())
        {
            verify.Transaction = transaction;
            verify.CommandText = $"SELECT COUNT(*) FROM {headerTable} WHERE doc_id = $doc AND doc_kind = $kind AND {notDeleted};";
            Add(verify, "$doc", returnDocId);
            Add(verify, "$kind", returnKind == InvDocKind.SalesReturn ? "SRT" : "PRT");
            if (Convert.ToInt64(verify.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
                throw new InvalidOperationException("The saved return document could not be verified.");
        }

        var lineIds = new List<long>();
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = $"SELECT line_id FROM {lineTable} WHERE doc_id = $doc ORDER BY line_id;";
            Add(read, "$doc", returnDocId);
            using var reader = read.ExecuteReader();
            while (reader.Read())
                lineIds.Add(Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture));
        }
        if (lineIds.Count != originalLineNumbers.Count)
            throw new InvalidOperationException("The saved return lines no longer match the original invoice lines.");
        if (originalLineNumbers.Distinct().Count() != originalLineNumbers.Count || originalLineNumbers.Any(n => n <= 0))
            throw new InvalidOperationException("Original invoice line numbers must be unique positive values.");

        using (var temporary = connection.CreateCommand())
        {
            temporary.Transaction = transaction;
            temporary.CommandText = $"UPDATE {lineTable} SET line_no = line_no + (SELECT COALESCE(MAX(line_no), 0) + 1000000 FROM {lineTable} WHERE doc_id = $doc) WHERE doc_id = $doc;";
            Add(temporary, "$doc", returnDocId);
            temporary.ExecuteNonQuery();
        }
        for (var index = 0; index < lineIds.Count; index++)
        {
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = $"UPDATE {lineTable} SET line_no = $lineNo WHERE doc_id = $doc AND line_id = $lineId;";
            Add(update, "$lineNo", originalLineNumbers[index]);
            Add(update, "$doc", returnDocId);
            Add(update, "$lineId", lineIds[index]);
            if (update.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("A saved return line could not be linked to its original invoice line.");
        }
        transaction.Commit();
    }

    public static InvDoc? Load(long docId, InvDocKind kind)
    {
        DocSchema.Ensure();
        var isSales = kind.IsSales();
        var headerTable = isSales ? "sales_documents" : "purchase_documents";
        var lineTable = isSales ? "sales_document_lines" : "purchase_document_lines";
        var cs = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(cs)) return null;

        using var connection = Open(cs);
        // Step 9: accept either of the current company's keys (code or
        // Guid) — see ResolveCompanyGuidOrNull. Without this, a
        // Guid-keyed document (order-converted invoice, chain
        // quotation) opened from DocSearch loaded as "not found" and
        // InvoiceWin silently presented a blank new document instead.
        var guidKey = ResolveCompanyGuidOrNull(connection, null)?.ToString("D") ?? Company;
        // W6 (migration 100): workflow columns load when present.
        var loadProvider = DatabaseRuntime.IsPostgreSql() ? "PostgreSQL" : "SQLite";
        var hasW6Header = PartyLedger.ColumnExists(connection, null, loadProvider, headerTable, "return_reason_code");
        var hasW6Lines = PartyLedger.ColumnExists(connection, null, loadProvider, lineTable, "return_condition");
        // W5 (migration 102): export header fields, sales table only.
        var hasExportHeader = isSales && PartyLedger.ColumnExists(connection, null, loadProvider, headerTable, "is_export");
        InvDoc doc;
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT doc_id, doc_kind, doc_no, doc_date, party_id, party_code, party_name, warehouse_id,
                       warehouse_name, currency_code, exchange_rate, status, tax_code, tax_rate, notes,
                       subtotal, tax_amount, grand_total,
                       discount_amount, extra_amount, paid_amount, payment_method, receipt_no, reference_no,
                       branch_name, sales_rep_name, cost_center, party_account_name, goods_account_name,
                       original_doc_id, original_doc_kind, original_doc_no, journal_doc_no,
                       target_warehouse_id, target_warehouse_name, valid_till{(isSales ? "" : ", due_date, supplier_credit_note_ref")}
                       {(hasW6Header ? ", return_reason_code, return_source_kind, no_invoice, inspection_status, approval_status" : string.Empty)}
                       {(hasExportHeader ? ", is_export, incoterm, port_of_loading, port_of_discharge, country_of_origin, customs_ref, vessel_flight, gross_weight, net_weight" : string.Empty)}
                FROM {headerTable}
                WHERE doc_id=$id AND (company_id=$company OR company_id=$guid) AND is_deleted=0;
                """;
            Add(cmd, "$id", docId);
            Add(cmd, "$company", Company);
            Add(cmd, "$guid", guidKey);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            var storedKind = reader.GetString(reader.GetOrdinal("doc_kind"));
            var expectedKind = new InvDoc { Kind = kind }.KindCode;
            if (!string.Equals(storedKind, expectedKind, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Document kind mismatch. The stored document is '{storedKind}', not '{expectedKind}'.");
            doc = new InvDoc
            {
                DocId = docId,
                Kind = kind,
                DocNo = reader.GetString(reader.GetOrdinal("doc_no")),
                DocDate = DateTime.Parse(reader.GetString(reader.GetOrdinal("doc_date")), CultureInfo.InvariantCulture),
                PartyId = reader.IsDBNull(reader.GetOrdinal("party_id")) ? null : Convert.ToInt64(reader.GetValue(reader.GetOrdinal("party_id")), CultureInfo.InvariantCulture),
                PartyCode = ReadString(reader, "party_code"),
                PartyName = reader.IsDBNull(reader.GetOrdinal("party_name")) ? "" : reader.GetString(reader.GetOrdinal("party_name")),
                WarehouseId = reader.IsDBNull(reader.GetOrdinal("warehouse_id")) ? null : Convert.ToInt32(reader.GetValue(reader.GetOrdinal("warehouse_id")), CultureInfo.InvariantCulture),
                WarehouseName = ReadString(reader, "warehouse_name"),
                CurrencyCode = reader.GetString(reader.GetOrdinal("currency_code")),
                ExchangeRate = Convert.ToDecimal(reader.GetValue(reader.GetOrdinal("exchange_rate")), CultureInfo.InvariantCulture),
                Status = reader.GetString(reader.GetOrdinal("status")).ToUpperInvariant() switch
                {
                    "POSTED" => InvDocStatus.Posted,
                    "POSTING" => InvDocStatus.Posting,
                    "CANCELLED" => InvDocStatus.Cancelled,
                    _ => InvDocStatus.Draft
                },
                TaxCode = reader.IsDBNull(reader.GetOrdinal("tax_code")) ? null : reader.GetString(reader.GetOrdinal("tax_code")),
                DefaultTaxRate = Convert.ToDecimal(reader.GetValue(reader.GetOrdinal("tax_rate")), CultureInfo.InvariantCulture),
                Notes = reader.IsDBNull(reader.GetOrdinal("notes")) ? null : reader.GetString(reader.GetOrdinal("notes")),
                LinesDiscountAmount = ReadDecimal(reader, "discount_amount"),
                LinesExtraAmount = ReadDecimal(reader, "extra_amount"),
                StoredSubtotal = ReadDecimal(reader, "subtotal"),
                StoredTaxAmount = ReadDecimal(reader, "tax_amount"),
                StoredGrandTotal = ReadDecimal(reader, "grand_total"),
                PaidAmount = ReadDecimal(reader, "paid_amount"),
                PaymentMethod = ParsePaymentMethod(ReadString(reader, "payment_method")),
                ReceiptNo = ReadString(reader, "receipt_no"),
                Reference = ReadString(reader, "reference_no"),
                SupplierCreditNoteRef = isSales ? string.Empty : ReadString(reader, "supplier_credit_note_ref"),
                BranchName = ReadString(reader, "branch_name"),
                SalesRepName = ReadString(reader, "sales_rep_name"),
                CostCenter = ReadString(reader, "cost_center"),
                PartyAccountName = ReadString(reader, "party_account_name"),
                GoodsAccountName = ReadString(reader, "goods_account_name"),
                OriginalDocId = ReadLongNullable(reader, "original_doc_id"),
                OriginalDocKind = ParseDocKind(ReadString(reader, "original_doc_kind")),
                OriginalDocNo = ReadString(reader, "original_doc_no"),
                JournalDocNo = ReadString(reader, "journal_doc_no"),
                TargetWarehouseId = (int?)ReadLongNullable(reader, "target_warehouse_id"),
                TargetWarehouseName = ReadString(reader, "target_warehouse_name"),
                ValidTill = ParseValidTill(reader),
                DueDate = isSales ? null : ReadDateNullable(reader, "due_date"),
            };
            if (hasW6Header)
            {
                doc.ReturnReasonCode = ReadString(reader, "return_reason_code");
                doc.ReturnSourceKind = ReadString(reader, "return_source_kind");
                var noInvoiceValue = reader.GetValue(reader.GetOrdinal("no_invoice"));
                doc.NoInvoice = noInvoiceValue is bool nb ? nb : Convert.ToInt32(noInvoiceValue, CultureInfo.InvariantCulture) != 0;
                doc.InspectionStatus = string.IsNullOrWhiteSpace(ReadString(reader, "inspection_status")) ? "NotRequired" : ReadString(reader, "inspection_status");
                doc.ApprovalStatus = string.IsNullOrWhiteSpace(ReadString(reader, "approval_status")) ? "NotRequired" : ReadString(reader, "approval_status");
            }
            if (hasExportHeader)
            {
                var isExportValue = reader.GetValue(reader.GetOrdinal("is_export"));
                doc.IsExport = isExportValue is bool eb ? eb : Convert.ToInt32(isExportValue, CultureInfo.InvariantCulture) != 0;
                doc.Incoterm = ReadString(reader, "incoterm");
                doc.PortOfLoading = ReadString(reader, "port_of_loading");
                doc.PortOfDischarge = ReadString(reader, "port_of_discharge");
                doc.CountryOfOrigin = ReadString(reader, "country_of_origin");
                doc.CustomsRef = ReadString(reader, "customs_ref");
                doc.VesselFlight = ReadString(reader, "vessel_flight");
                doc.GrossWeight = ReadDecimalNullable(reader, "gross_weight");
                doc.NetWeight = ReadDecimalNullable(reader, "net_weight");
            }
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT line_no, material_id, material_code, description, unit, qty, unit_price,
                       discount_pct, tax_code, tax_rate, tax_calculation_mode, warehouse_id, warehouse_name
                       {(hasW6Lines ? ", return_condition, return_reason_code" : string.Empty)}
                FROM {lineTable} WHERE doc_id=$id ORDER BY line_no;
                """;
            Add(cmd, "$id", docId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                doc.Lines.Add(new InvLine
                {
                    LineNo = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture),
                    MaterialId = reader.IsDBNull(1) ? null : Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                    MaterialCode = reader.IsDBNull(2) ? "" : Convert.ToString(reader.GetValue(2), CultureInfo.InvariantCulture) ?? "",
                    Description = Convert.ToString(reader.GetValue(3), CultureInfo.InvariantCulture) ?? "",
                    Unit = reader.IsDBNull(4) ? "" : Convert.ToString(reader.GetValue(4), CultureInfo.InvariantCulture) ?? "",
                    Qty = Convert.ToDecimal(reader.GetValue(5), CultureInfo.InvariantCulture),
                    UnitPrice = Convert.ToDecimal(reader.GetValue(6), CultureInfo.InvariantCulture),
                    DiscountPct = Convert.ToDecimal(reader.GetValue(7), CultureInfo.InvariantCulture),
                    TaxCode = reader.IsDBNull(8) ? string.Empty : Convert.ToString(reader.GetValue(8), CultureInfo.InvariantCulture) ?? string.Empty,
                    TaxRate = Convert.ToDecimal(reader.GetValue(9), CultureInfo.InvariantCulture),
                    TaxCalculationMode = reader.IsDBNull(10) ? "Exclusive" : Convert.ToString(reader.GetValue(10), CultureInfo.InvariantCulture) ?? "Exclusive",
                    WarehouseId = reader.IsDBNull(11) ? null : Convert.ToInt32(reader.GetValue(11), CultureInfo.InvariantCulture),
                    WarehouseName = reader.FieldCount > 12 && !reader.IsDBNull(12)
                        ? Convert.ToString(reader.GetValue(12), CultureInfo.InvariantCulture) ?? ""
                        : "",
                    ReturnCondition = hasW6Lines && reader.FieldCount > 13 && !reader.IsDBNull(13)
                        ? Convert.ToString(reader.GetValue(13), CultureInfo.InvariantCulture) ?? "Good"
                        : "Good",
                    ReturnReasonCode = hasW6Lines && reader.FieldCount > 14 && !reader.IsDBNull(14)
                        ? Convert.ToString(reader.GetValue(14), CultureInfo.InvariantCulture)
                        : null,
                });
            }
        }
        return doc;
    }


    /// <summary>Creates a linked credit/return document for a posted invoice without deleting the source.</summary>
    public static long ReversePostedInvoice(long originalDocId, InvDocKind originalKind, string reason, string userId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(originalDocId);
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A reversal reason is required.", nameof(reason));
        reason = reason.Trim();
        if (reason.Length > 1000) throw new ArgumentException("The reversal reason cannot exceed 1000 characters.", nameof(reason));

        var reversalKind = originalKind switch
        {
            InvDocKind.SalesInvoice or InvDocKind.PosSale => InvDocKind.SalesReturn,
            InvDocKind.PurchaseInvoice => InvDocKind.PurchaseReturn,
            _ => throw new InvalidOperationException("Only posted sales and purchase invoices can be reversed through this workflow.")
        };

        DocSchema.Ensure();
        EnsureReversalSchema();
        var company = Company;
        if (string.IsNullOrWhiteSpace(company)) throw new InvalidOperationException("No current company is selected.");
        var original = Load(originalDocId, originalKind)
            ?? throw new InvalidOperationException("The original invoice was not found in the current company.");
        if (original.Status != InvDocStatus.Posted) throw new InvalidOperationException("Only posted invoices can be reversed.");
        if (original.Lines.Count == 0) throw new InvalidOperationException("The invoice has no lines to reverse.");
        if (HasCompletedReversal(originalDocId, originalKind))
            throw new InvalidOperationException("This invoice has already been reversed.");

        var cs = DatabaseRuntime.GetConnectionString()
            ?? throw new InvalidOperationException("No database connection.");

        // Reuse a failed reservation only when no reversal draft was ever created.
        // A reservation linked to a draft is intentionally locked for reconciliation.
        using (var connection = Open(cs))
        using (var tx = connection.BeginTransaction())
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            Add(cmd, "$company", company);
            Add(cmd, "$originalKind", originalKind.ToString());
            Add(cmd, "$originalId", originalDocId);
            Add(cmd, "$reversalKind", reversalKind.ToString());
            Add(cmd, "$reason", reason);
            Add(cmd, "$actor", string.IsNullOrWhiteSpace(userId) ? Environment.UserName : userId.Trim());
            Add(cmd, "$createdAt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));

            cmd.CommandText = """
                UPDATE document_reversals
                SET status='Pending', reversal_doc_kind=$reversalKind, reason=$reason,
                    actor=$actor, created_at=$createdAt
                WHERE company_id=$company AND original_doc_kind=$originalKind
                  AND original_doc_id=$originalId AND status='Failed' AND reversal_doc_id IS NULL;
                """;
            var recovered = cmd.ExecuteNonQuery();
            if (recovered == 0)
            {
                cmd.Parameters.Clear();
                cmd.CommandText = """
                    INSERT INTO document_reversals
                    (reversal_id, company_id, original_doc_kind, original_doc_id, reversal_doc_kind,
                     reversal_doc_id, reason, actor, created_at, status)
                    VALUES ($rid, $company, $originalKind, $originalId, $reversalKind,
                            NULL, $reason, $actor, $createdAt, 'Pending');
                    """;
                Add(cmd, "$rid", Guid.NewGuid().ToString("N"));
                Add(cmd, "$company", company);
                Add(cmd, "$originalKind", originalKind.ToString());
                Add(cmd, "$originalId", originalDocId);
                Add(cmd, "$reversalKind", reversalKind.ToString());
                Add(cmd, "$reason", reason);
                Add(cmd, "$actor", string.IsNullOrWhiteSpace(userId) ? Environment.UserName : userId.Trim());
                Add(cmd, "$createdAt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                try { cmd.ExecuteNonQuery(); }
                catch (DbException ex)
                {
                    throw new InvalidOperationException("A reversal is already in progress or has already been recorded for this invoice. Check reversal history before retrying.", ex);
                }
            }
            tx.Commit();
        }

        long reversalId = 0;
        long reversalDraftId = 0;
        try
        {
            var reversal = new InvDoc
            {
                Kind = reversalKind,
                DocNo = NextDocNo(reversalKind),
                DocDate = DateTime.Today,
                PartyId = original.PartyId,
                PartyCode = original.PartyCode,
                PartyName = original.PartyName,
                WarehouseId = original.WarehouseId,
                WarehouseName = original.WarehouseName,
                CurrencyCode = original.CurrencyCode,
                ExchangeRate = original.ExchangeRate,
                TaxCode = original.TaxCode,
                DefaultTaxRate = original.DefaultTaxRate,
                // Step 5 (sweep §2 reversal BLOCKER): the reversal copies
                // the original's header MONEY and context fields in full.
                // Before, LinesDiscountAmount / LinesExtraAmount /
                // PaidAmount / PaymentMethod and the context fields were
                // left at defaults, so the reversal's stored grand_total
                // excluded the discount/extra the original included and
                // stored totals netted to the discount/extra amount
                // instead of zero — and its GL carried no settlement leg
                // to mirror the original's collected-at-posting cash.
                LinesDiscountAmount = original.LinesDiscountAmount,
                LinesExtraAmount = original.LinesExtraAmount,
                PaidAmount = original.PaidAmount,
                PaymentMethod = original.PaymentMethod,
                ReceiptNo = original.ReceiptNo,
                Reference = original.Reference,
                BranchName = original.BranchName,
                SalesRepName = original.SalesRepName,
                CostCenter = original.CostCenter,
                PartyAccountName = original.PartyAccountName,
                GoodsAccountName = original.GoodsAccountName,
                // Phenix "original": return documents point at the original invoice.
                OriginalDocId = original.DocId,
                OriginalDocKind = original.Kind,
                OriginalDocNo = original.DocNo,
                // W6: a generated reversal is a system return; its
                // mandatory reversal reason (above) is the reason of
                // record, and the master reason is stamped OTHER.
                ReturnReasonCode = "OTHER",
                ReturnSourceKind = "INV",
                Notes = $"Automatic reversal of {original.DocNo} (ID {original.DocId}). Reason: {reason}",
                Lines = original.Lines.Select(line => new InvLine
                {
                    MaterialId = line.MaterialId,
                    MaterialCode = line.MaterialCode,
                    Description = line.Description,
                    Unit = line.Unit,
                    Qty = line.Qty,
                    UnitPrice = line.UnitPrice,
                    DiscountPct = line.DiscountPct,
                    TaxCode = line.TaxCode,
                    TaxRate = line.TaxRate,
                    TaxCalculationMode = line.TaxCalculationMode,
                    WarehouseId = line.WarehouseId,
                    WarehouseName = line.WarehouseName
                }).ToList()
            };

            SaveDraft(reversal);
            reversalDraftId = reversal.DocId;
            RestoreReturnLineNumbers(
                reversalDraftId, reversalKind, original.Lines.Select(line => line.LineNo).ToArray());

            // Persist the draft ID before posting so a failure cannot leave an orphan
            // draft while deleting the only record that prevents a duplicate reversal.
            using (var linkConnection = Open(cs))
            using (var link = linkConnection.CreateCommand())
            {
                link.CommandText = """
                    UPDATE document_reversals
                    SET reversal_doc_id=$draftId
                    WHERE company_id=$company AND original_doc_kind=$originalKind
                      AND original_doc_id=$originalId AND status='Pending'
                      AND reversal_doc_id IS NULL;
                    """;
                Add(link, "$draftId", reversalDraftId);
                Add(link, "$company", company);
                Add(link, "$originalKind", originalKind.ToString());
                Add(link, "$originalId", originalDocId);
                if (link.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("The reversal draft was saved but could not be linked to its reservation. Reconcile before retrying.");
            }

            Post(reversalDraftId, reversal.Kind, userId);
            reversalId = reversalDraftId;

            using var connection = Open(cs);
            using var tx = connection.BeginTransaction();
            using var update = connection.CreateCommand();
            update.Transaction = tx;
            update.CommandText = """
                UPDATE document_reversals
                SET reversal_doc_id=$reversalId, status='Completed'
                WHERE company_id=$company AND original_doc_kind=$originalKind
                  AND original_doc_id=$originalId AND status='Pending';
                """;
            Add(update, "$reversalId", reversalId);
            Add(update, "$company", company);
            Add(update, "$originalKind", originalKind.ToString());
            Add(update, "$originalId", originalDocId);
            if (update.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("The reversal posted, but its audit link could not be finalized. Do not retry; ask an administrator to reconcile the pending reversal.");

            // Step 5 settlement symmetry (Step 4 owner flag 3): the
            // return document's own party_ledger entry mirrors the
            // original invoice entry, but the SETTLEMENT written at the
            // original's posting (and any later allocations against it)
            // would otherwise dangle — the party would keep a credit for
            // money collected on an invoice that no longer stands, and
            // the original would still read as paid. Un-settle inside
            // this same finalise transaction.
            UnsettleOriginalOnReversal(connection, tx, original, reversal, userId);
            tx.Commit();
            return reversalId;
        }
        catch (Exception ex)
        {
            if (reversalId == 0)
            {
                try
                {
                    using var connection = Open(cs);
                    using var cleanup = connection.CreateCommand();
                    cleanup.CommandText = """
                        UPDATE document_reversals
                        SET status='Failed',
                            reversal_doc_id=CASE
                                WHEN reversal_doc_id IS NULL THEN $draftId
                                ELSE reversal_doc_id
                            END
                        WHERE company_id=$company AND original_doc_kind=$kind
                          AND original_doc_id=$id AND status='Pending';
                        """;
                    object draftIdValue = reversalDraftId > 0 ? reversalDraftId : DBNull.Value;
                    Add(cleanup, "$draftId", draftIdValue);
                    Add(cleanup, "$company", company);
                    Add(cleanup, "$kind", originalKind.ToString());
                    Add(cleanup, "$id", originalDocId);
                    cleanup.ExecuteNonQuery();
                }
                catch (Exception exL626)
                {
                    AppHost.Errors.Report(ErrorCodes.InventoryOperationFailed, exL626, "InvRepo.ReversePostedInvoice");
                }
            }
            throw new InvalidOperationException("Invoice reversal did not complete successfully. Verify the invoice and reversal history before retrying.", ex);
        }
    }

    /// <summary>
    /// Step 5: makes a full reversal of a posted invoice symmetric with
    /// its settlement state, inside the caller's transaction:
    /// 1. Every settlement entry written AT the original's posting
    ///    (party_ledger rows with doc_kind RCV/PAY and doc_id = the
    ///    original — the shape only InvRepo.Post writes; cash-voucher
    ///    settlements carry doc_id NULL and installment collections use
    ///    INS, so neither can be mistaken for one) is MIRRORED with
    ///    debit/credit swapped, pointing at the reversal document. The
    ///    ledger stays append-only; the settlement's net effect becomes
    ///    zero, exactly as the GL mirror does for journal settlements.
    /// 2. Every payment_allocations row against the original is
    ///    deleted. Allocations of EXTERNAL payments (receipt/payment
    ///    vouchers) simply free that payment again — the voucher's own
    ///    ledger row stands, because the money really was received; the
    ///    party keeps the resulting credit balance, which is the
    ///    correct post-reversal position (a refund is owed / credit on
    ///    account). This is a FULL reversal (all lines, full quantity),
    ///    so no allocation against the original may survive it.
    /// 3. The original's paid_amount returns to 0 — its settled state is
    ///    exactly as if it had never been posted or paid.
    /// </summary>
    private static void UnsettleOriginalOnReversal(
        DbConnection connection, DbTransaction tx, InvDoc original, InvDoc reversal, string userId)
    {
        var provider = DatabaseRuntime.IsPostgreSql() ? "PostgreSQL" : "SQLite";
        var headerTable = original.IsSalesSide ? "sales_documents" : "purchase_documents";
        // userId is part of the call contract (the reversal row itself
        // records the actor; allocation rows carry no per-row actor on
        // delete), kept explicit so future audit columns have it.

        if (original.PartyId is long partyId
            && PartyLedger.TableExists(connection, tx, provider, "party_ledger"))
        {
            var partyKind = original.IsSalesSide ? "C" : "S";
            var hasBase = PartyLedger.ColumnExists(connection, tx, provider, "party_ledger", "base_debit");
            var settlements = new List<(decimal Debit, decimal Credit, string Currency, decimal? BaseDebit, decimal? BaseCredit, decimal Rate)>();
            using (var find = connection.CreateCommand())
            {
                find.Transaction = tx;
                find.CommandText = $"""
                    SELECT debit, credit, currency_code{(hasBase ? ", base_debit, base_credit, exchange_rate" : string.Empty)}
                    FROM party_ledger
                    WHERE company_id=$company AND party_id=$pid AND doc_id=$orig
                      AND doc_kind IN ('RCV', 'PAY');
                    """;
                Add(find, "$company", Company);
                Add(find, "$pid", partyId);
                Add(find, "$orig", original.DocId);
                using var reader = find.ExecuteReader();
                while (reader.Read())
                {
                    settlements.Add((
                        Convert.ToDecimal(reader.GetValue(0), CultureInfo.InvariantCulture),
                        Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture),
                        reader.IsDBNull(2) ? original.CurrencyCode : reader.GetString(2),
                        hasBase && !reader.IsDBNull(3) ? Convert.ToDecimal(reader.GetValue(3), CultureInfo.InvariantCulture) : null,
                        hasBase && !reader.IsDBNull(4) ? Convert.ToDecimal(reader.GetValue(4), CultureInfo.InvariantCulture) : null,
                        hasBase && !reader.IsDBNull(5) ? Convert.ToDecimal(reader.GetValue(5), CultureInfo.InvariantCulture) : 0m));
                }
            }

            foreach (var (debit, credit, currency, baseDebit, baseCredit, rate) in settlements)
            {
                // Step 7: mirror swaps the STORED base amounts, so the
                // settlement nets to zero in base exactly (rate moves
                // since the original posting do not leak in).
                PartyLedger.WriteEntry(
                    connection, tx, provider,
                    Company, partyKind, partyId,
                    reversal.KindCode, reversal.DocId, reversal.DocNo,
                    DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    credit, debit, currency,
                    $"Reversal of settlement collected at posting for {original.DocNo}",
                    exchangeRate: rate,
                    baseDebit: baseCredit,
                    baseCredit: baseDebit);
            }
        }

        if (PartyLedger.TableExists(connection, tx, provider, "payment_allocations"))
        {
            var documentTable = original.IsSalesSide ? "SALES" : "PURCHASE";
            var hasDocumentCompany = PartyLedger.ColumnExists(
                connection, tx, provider, "payment_allocations", "document_company_id");
            var documentCompanyExpression = hasDocumentCompany
                ? "COALESCE(document_company_id, company_id)"
                : "company_id";
            var allocations = new List<(string Id, long PaymentLedgerId, string DocumentCompany,
                decimal Amount, string? AllocatedBy, string? Note)>();
            using (var load = connection.CreateCommand())
            {
                load.Transaction = tx;
                load.CommandText = $"""
                    SELECT allocation_id, payment_ledger_id, {documentCompanyExpression},
                           amount, allocated_by, note
                    FROM payment_allocations
                    WHERE company_id=$company AND doc_table=$table AND doc_id=$orig
                    ORDER BY allocation_id;
                    """;
                Add(load, "$company", Company);
                Add(load, "$table", documentTable);
                Add(load, "$orig", original.DocId);
                using var reader = load.ExecuteReader();
                while (reader.Read())
                {
                    allocations.Add((
                        Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture) ?? string.Empty,
                        Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                        Convert.ToString(reader.GetValue(2), CultureInfo.InvariantCulture) ?? Company,
                        Convert.ToDecimal(reader.GetValue(3), CultureInfo.InvariantCulture),
                        reader.IsDBNull(4) ? null : Convert.ToString(reader.GetValue(4), CultureInfo.InvariantCulture),
                        reader.IsDBNull(5) ? null : Convert.ToString(reader.GetValue(5), CultureInfo.InvariantCulture)));
                }
            }

            if (allocations.Count > 0)
            {
                if (!PartyLedger.TableExists(connection, tx, provider, "payment_allocation_reversal_audit"))
                    throw new InvalidOperationException(
                        "Payment-allocation reversal audit schema is missing. Apply the provider migrations before reversing this invoice.");

                // The audit must identify the actual posted journal of the
                // return document, not merely its document number or ID.
                var companyGuid = ResolveCompanyGuid(connection, tx);
                Guid reversalJournalId;
                using (var journal = connection.CreateCommand())
                {
                    journal.Transaction = tx;
                    journal.CommandText = """
                        SELECT journal_entry_id
                        FROM journal_entries
                        WHERE company_id=$companyGuid AND source_doc_type=$sourceType
                          AND source_doc_id=$sourceId AND status=1
                        ORDER BY posted_at DESC
                        LIMIT 1;
                        """;
                    Add(journal, "$companyGuid", DatabaseRuntime.IsPostgreSql()
                        ? companyGuid
                        : companyGuid.ToString("D", CultureInfo.InvariantCulture));
                    Add(journal, "$sourceType", reversal.KindCode);
                    Add(journal, "$sourceId", reversal.DocId.ToString(CultureInfo.InvariantCulture));
                    var journalValue = journal.ExecuteScalar();
                    if (journalValue is null or DBNull
                        || !Guid.TryParse(Convert.ToString(journalValue, CultureInfo.InvariantCulture), out reversalJournalId))
                        throw new InvalidOperationException(
                            "The posted return journal could not be found; payment allocations were not released.");
                }

                foreach (var allocation in allocations)
                {
                    using var audit = connection.CreateCommand();
                    audit.Transaction = tx;
                    audit.CommandText = """
                        INSERT INTO payment_allocation_reversal_audit
                            (audit_id, company_id, allocation_id, payment_ledger_id, doc_table, doc_id,
                             document_company_id, amount, allocated_by, note, released_by, reversal_journal_id)
                        VALUES
                            ($audit, $company, $allocation, $payment, $table, $doc,
                             $documentCompany, $amount, $allocatedBy, $note, $releasedBy, $reversalJournal);
                        """;
                    var auditId = Guid.NewGuid();
                    Add(audit, "$audit", DatabaseRuntime.IsPostgreSql() ? auditId : auditId.ToString("D"));
                    Add(audit, "$company", Company);
                    Add(audit, "$allocation", DatabaseRuntime.IsPostgreSql()
                        ? Guid.Parse(allocation.Id)
                        : allocation.Id);
                    Add(audit, "$payment", allocation.PaymentLedgerId);
                    Add(audit, "$table", documentTable);
                    Add(audit, "$doc", original.DocId);
                    Add(audit, "$documentCompany", allocation.DocumentCompany);
                    Add(audit, "$amount", allocation.Amount);
                    Add(audit, "$allocatedBy", (object?)allocation.AllocatedBy ?? DBNull.Value);
                    Add(audit, "$note", (object?)allocation.Note ?? DBNull.Value);
                    Add(audit, "$releasedBy", userId);
                    Add(audit, "$reversalJournal", DatabaseRuntime.IsPostgreSql()
                        ? reversalJournalId
                        : reversalJournalId.ToString("D"));
                    audit.ExecuteNonQuery();
                }

                using var del = connection.CreateCommand();
                del.Transaction = tx;
                del.CommandText = """
                    DELETE FROM payment_allocations
                    WHERE company_id=$company AND doc_table=$table AND doc_id=$orig;
                    """;
                Add(del, "$company", Company);
                Add(del, "$table", documentTable);
                Add(del, "$orig", original.DocId);
                del.ExecuteNonQuery();
            }
        }

        using (var upd = connection.CreateCommand())
        {
            upd.Transaction = tx;
            upd.CommandText = $"""
                UPDATE {headerTable} SET paid_amount=0
                WHERE doc_id=$id AND company_id=$company AND is_deleted=0;
                """;
            if (DatabaseRuntime.IsPostgreSql())
                upd.CommandText = $"""
                    UPDATE {headerTable} SET paid_amount=0
                    WHERE doc_id=$id AND company_id=$company AND is_deleted=FALSE;
                    """;
            Add(upd, "$id", original.DocId);
            Add(upd, "$company", Company);
            upd.ExecuteNonQuery();
        }
    }

    public static bool HasCompletedReversal(long originalDocId, InvDocKind originalKind)
    {
        EnsureReversalSchema();
        var cs = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(cs)) return false;
        using var connection = Open(cs);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(1) FROM document_reversals
            WHERE company_id=$company AND original_doc_kind=$kind
              AND original_doc_id=$id AND status='Completed';
            """;
        Add(cmd, "$company", Company);
        Add(cmd, "$kind", originalKind.ToString());
        Add(cmd, "$id", originalDocId);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture) > 0;
    }

    private static void EnsureReversalSchema()
    {
        var cs = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException("No database connection.");
        using var connection = Open(cs);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = DatabaseRuntime.IsPostgreSql()
            ? "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = current_schema() AND table_name = 'document_reversals';"
            : "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'document_reversals';";
        if (Convert.ToInt32(cmd.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture) != 1)
            throw new InvalidOperationException("Reversal schema is missing. Apply the provider migrations under dba/mig.");
    }

    // ------------------------------------------------------------------
    // Step 3 GL mapping (J-2). Account codes are the canonical chart
    // codes of the seeded templates (GlAccounts); a material's acc_*
    // columns override the revenue / purchases / COGS / inventory legs
    // when they hold a code. Tax is split to the VAT accounts ONLY when
    // the company configured them postable (the seeded templates carry
    // 1205/2205 as non-postable groups); otherwise tax stays inside the
    // revenue / purchase leg so the entry still balances on the totals
    // the document layer produced. Full table: docs/FIX_PHASE_STEP3.md.
    // ------------------------------------------------------------------

    private sealed record MaterialGlInfo(
        string? AccSales, string? AccSalesReturn, string? AccCogs,
        string? AccPurchase, string? AccPurchaseReturn, string? AccInventory,
        string? TaxAccount, bool Stockable, decimal AvgCost, long? MaterialId = null);

    private static Guid ResolveCompanyGuid(DbConnection connection, DbTransaction tx)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT company_id FROM companies WHERE company_code=$company;";
        Add(cmd, "$company", Company);
        var value = cmd.ExecuteScalar();
        if (value is null or DBNull || !Guid.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var guid) || guid == Guid.Empty)
            throw new InvalidOperationException($"Company '{Company}' could not be resolved for GL posting.");
        return guid;
    }

    private static MaterialGlInfo LoadMaterialGl(DbConnection connection, DbTransaction tx, InvLine line, int? docWarehouseId)
    {
        var materialId = line.MaterialId;
        if ((materialId is null or 0) && !string.IsNullOrWhiteSpace(line.MaterialCode))
        {
            using var find = connection.CreateCommand();
            find.Transaction = tx;
            find.CommandText = "SELECT material_id FROM materials WHERE company_id=$c AND code=$code AND is_deleted=0 LIMIT 1;";
            if (DatabaseRuntime.IsPostgreSql())
                find.CommandText = "SELECT material_id FROM materials WHERE company_id=$c AND code=$code AND is_deleted=FALSE LIMIT 1;";
            Add(find, "$c", Company);
            Add(find, "$code", line.MaterialCode.Trim());
            var found = find.ExecuteScalar();
            materialId = found is null or DBNull ? null : Convert.ToInt64(found, CultureInfo.InvariantCulture);
        }
        if (materialId is null or 0)
            return new MaterialGlInfo(null, null, null, null, null, null, null, false, 0m, null);

        string? accSales = null, accSalesReturn = null, accCogs = null, accPurchase = null,
            accPurchaseReturn = null, accInventory = null, taxAccount = null;
        var stockable = false;
        decimal avgCost = 0m;
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT acc_sales, acc_sales_return, acc_cogs, acc_purchase, acc_purchase_return,
                       acc_inventory, tax_account, is_stocked, is_service, item_type, avg_cost, cost
                FROM materials WHERE company_id=$c AND material_id=$m AND is_deleted=0;
                """;
            if (DatabaseRuntime.IsPostgreSql())
                cmd.CommandText = """
                    SELECT acc_sales, acc_sales_return, acc_cogs, acc_purchase, acc_purchase_return,
                           acc_inventory, tax_account, is_stocked, is_service, item_type, avg_cost, cost
                    FROM materials WHERE company_id=$c AND material_id=$m AND is_deleted=FALSE;
                    """;
            Add(cmd, "$c", Company);
            Add(cmd, "$m", materialId.Value);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                string? Text(int i) => reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
                accSales = Text(0); accSalesReturn = Text(1); accCogs = Text(2);
                accPurchase = Text(3); accPurchaseReturn = Text(4); accInventory = Text(5);
                taxAccount = Text(6);
                var itemType = Text(9);
                stockable = Convert.ToBoolean(reader.GetValue(7), CultureInfo.InvariantCulture)
                    && !Convert.ToBoolean(reader.GetValue(8), CultureInfo.InvariantCulture)
                    && !string.Equals(itemType, "Service", StringComparison.OrdinalIgnoreCase);
                avgCost = reader.IsDBNull(10) ? 0m : Convert.ToDecimal(reader.GetValue(10), CultureInfo.InvariantCulture);
                if (avgCost == 0 && !reader.IsDBNull(11))
                    avgCost = Convert.ToDecimal(reader.GetValue(11), CultureInfo.InvariantCulture);
            }
        }

        // Prefer the live balance average for the line's warehouse (the
        // stock layer's current cost figure — Step 6 owns costing method
        // unification); fall back to the material card average above.
        var warehouseId = line.WarehouseId ?? docWarehouseId;
        if (warehouseId is not null && materialId is not null)
        {
            using var bal = connection.CreateCommand();
            bal.Transaction = tx;
            bal.CommandText = "SELECT average_cost FROM material_stock_balances WHERE company_id=$c AND warehouse_id=$w AND material_id=$m AND is_deleted=0 LIMIT 1;";
            if (DatabaseRuntime.IsPostgreSql())
                bal.CommandText = "SELECT average_cost FROM material_stock_balances WHERE company_id=$c AND warehouse_id=$w AND material_id=$m AND is_deleted=FALSE LIMIT 1;";
            Add(bal, "$c", Company);
            Add(bal, "$w", warehouseId.Value);
            Add(bal, "$m", materialId.Value);
            var value = bal.ExecuteScalar();
            if (value is not null and not DBNull)
            {
                var balanceAvg = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                if (balanceAvg > 0) avgCost = balanceAvg;
            }
        }
        return new MaterialGlInfo(accSales, accSalesReturn, accCogs, accPurchase, accPurchaseReturn, accInventory, taxAccount, stockable, avgCost, materialId);
    }

    private static string CodeOr(string? configured, string fallback)
        => string.IsNullOrWhiteSpace(configured) ? fallback : configured.Trim();

    private static bool IsGeneratedFullReversal(DbConnection connection, DbTransaction tx, InvDoc document)
    {
        if (document.DocId <= 0 || document.OriginalDocId is not > 0)
            return false;

        var provider = DatabaseRuntime.IsPostgreSql() ? "PostgreSQL" : "SQLite";
        if (!PartyLedger.TableExists(connection, tx, provider, "document_reversals"))
            return false;

        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT COUNT(*)
            FROM document_reversals
            WHERE company_id = $company
              AND reversal_doc_kind = $kind
              AND reversal_doc_id = $document
            """;
        Add(command, "$company", Company);
        Add(command, "$kind", document.Kind.ToString());
        Add(command, "$document", document.DocId);
        return Convert.ToInt32(command.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture) == 1;
    }

    private static List<ShouTech.Domain.Entities.GlLineRequest> BuildGlLines(DbConnection connection, DbTransaction tx, InvDoc doc, decimal effectiveRate = 1m,
        ShouTech.Infrastructure.WithholdingTax.Outcome? withholding = null,
        (decimal Amount, string? Account, string? Code, decimal Rate, decimal Base) reversalWithholding = default,
        (decimal ReceiptBase, decimal InvoiceBase) unwoundChain = default)
    {
        var lines = new List<ShouTech.Domain.Entities.GlLineRequest>();
        if (doc.Kind is InvDocKind.Quotation or InvDocKind.StockTransfer or InvDocKind.OpeningStock or InvDocKind.InventoryCount)
            return lines; // no GL for these kinds (see Step 3 doc)

        var provider = DatabaseRuntime.IsPostgreSql() ? "PostgreSQL" : "SQLite";
        var cc = string.IsNullOrWhiteSpace(doc.CostCenter) ? null : doc.CostCenter.Trim();
        // Step 5: every GL amount derives from the STORED totals, exactly
        // like party_ledger and the credit guard — one figure everywhere.
        var grand = doc.PostingTotal;
        var tax = doc.PostingTaxAmount;

        var infos = doc.Lines.Select(l => LoadMaterialGl(connection, tx, l, doc.WarehouseId)).ToList();

        // Tax is split by legal treatment. Output tax is always a liability;
        // recoverable input tax is an asset; non-recoverable input tax remains
        // part of the purchase/stock cost instead of being posted as a receivable.
        var isSalesDocument = doc.Kind.IsSales() || doc.Kind == InvDocKind.PosSale;
        var taxAccountCode = isSalesDocument ? GlAccounts.OutputVat : GlAccounts.InputVat;
        var materialTaxAccount = infos.Select(i => i.TaxAccount).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
        if (!string.IsNullOrWhiteSpace(materialTaxAccount)) taxAccountCode = materialTaxAccount.Trim();
        var companyGuid = ResolveCompanyGuid(connection, tx);
        var taxAccountGroups = new List<(string Account, decimal Amount)>();
        var nonRecoverableTaxByLine = new decimal[doc.Lines.Count];
        var canSplitTax = tax > 0m;
        var taxCountry = string.Empty;
        decimal taxAmountRequiringAccount = 0m;
        if (canSplitTax)
        {
            using var taxSettings = connection.CreateCommand();
            taxSettings.Transaction = tx;
            taxSettings.CommandText = "SELECT country_code FROM company_tax_settings WHERE company_code=$company AND tax_enabled = " +
                (DatabaseRuntime.IsPostgreSql() ? "TRUE" : "1") + " LIMIT 1;";
            InvRepo.Add(taxSettings, "$company", Company);
            taxCountry = Convert.ToString(taxSettings.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;

            var hasTaxLineGroups = doc.Lines.Any(line => line.LineTax != 0m);
            foreach (var group in doc.Lines.Where(line => line.LineTax != 0m)
                         .GroupBy(line => line.TaxCode ?? string.Empty, StringComparer.OrdinalIgnoreCase))
            {
                var account = taxAccountCode;
                var recoverable = true;
                if (!string.IsNullOrWhiteSpace(group.Key) && !string.IsNullOrWhiteSpace(taxCountry))
                {
                    using var rule = connection.CreateCommand();
                    rule.Transaction = tx;
                    var activeValue = DatabaseRuntime.IsPostgreSql() ? "TRUE" : "1";
                    var dateExpression = DatabaseRuntime.IsPostgreSql()
                        ? "CAST($documentDate AS date)" : "$documentDate";
                    var accountColumn = isSalesDocument ? "output_account_code" : "input_account_code";
                    rule.CommandText = $"""
                        SELECT COALESCE({accountColumn}, ''), is_recoverable
                        FROM company_tax_rules
                        WHERE company_code=$company AND country_code=$country
                          AND tax_code=$taxCode AND is_active={activeValue}
                          AND effective_from <= {dateExpression}
                          AND (effective_to IS NULL OR effective_to >= {dateExpression})
                        ORDER BY effective_from DESC
                        LIMIT 1;
                        """;
                    InvRepo.Add(rule, "$company", Company);
                    InvRepo.Add(rule, "$country", taxCountry);
                    InvRepo.Add(rule, "$taxCode", group.Key);
                    InvRepo.Add(rule, "$documentDate", doc.DocDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    using var ruleReader = rule.ExecuteReader();
                    if (ruleReader.Read())
                    {
                        var configuredAccount = Convert.ToString(ruleReader.GetValue(0), CultureInfo.InvariantCulture);
                        recoverable = isSalesDocument || (ruleReader.GetValue(1) is bool flag
                            ? flag
                            : Convert.ToInt32(ruleReader.GetValue(1), CultureInfo.InvariantCulture) == 1);
                        if (!string.IsNullOrWhiteSpace(configuredAccount))
                            account = configuredAccount.Trim();
                    }
                }

                var groupAmount = group.Sum(line => line.LineTax);
                if (!isSalesDocument && !recoverable)
                {
                    foreach (var line in group)
                    {
                        var lineIndex = doc.Lines.IndexOf(line);
                        if (lineIndex >= 0)
                            nonRecoverableTaxByLine[lineIndex] += line.LineTax;
                    }
                    continue;
                }

                taxAmountRequiringAccount += groupAmount;
                if (GlPosting.TryResolveAccount(connection, tx, provider, companyGuid, account) is null)
                {
                    canSplitTax = false;
                    taxAccountGroups.Clear();
                    break;
                }
                taxAccountGroups.Add((account, groupAmount));
            }

            if (!hasTaxLineGroups)
            {
                taxAmountRequiringAccount = tax;
                canSplitTax = GlPosting.TryResolveAccount(connection, tx, provider, companyGuid, taxAccountCode) is not null;
                if (canSplitTax) taxAccountGroups.Add((taxAccountCode, tax));
            }
            else if (canSplitTax)
            {
                var allocatedTax = taxAccountGroups.Sum(group => group.Amount) + nonRecoverableTaxByLine.Sum();
                var residual = tax - allocatedTax;
                if (residual != 0m)
                {
                    if (taxAccountGroups.Count > 0)
                    {
                        var last = taxAccountGroups[^1];
                        taxAccountGroups[^1] = (last.Account, last.Amount + residual);
                    }
                    else if (nonRecoverableTaxByLine.Length > 0)
                    {
                        nonRecoverableTaxByLine[^1] += residual;
                    }
                }
            }
        }
        var splitTax = canSplitTax && taxAccountGroups.Count > 0;
        if (taxAmountRequiringAccount > 0m && !splitTax)
            throw new InvalidOperationException(
                "The enabled tax system has no valid posting account for recoverable/output tax. Configure the tax account in the chart of accounts before posting.");
        var taxLeg = splitTax ? taxAccountGroups.Sum(group => group.Amount) : 0m;

        // Per-line revenue / purchase / returns leg amounts, adjusted so
        // their sum is EXACTLY (stored grand − tax leg). Before Step 5
        // the legs were the raw line subtotals (+ tax when unsplit), so
        // any header discount/extra made the entry unbalanced against
        // the control leg (which carries the grand total) and GlPosting
        // rejected it with ACC-5001. The header adjustment
        // (− discount + extra, plus any stored-vs-lines residue) is a
        // revenue/purchase adjustment in accounting terms: it is
        // allocated across the lines in proportion to |line subtotal|,
        // with the rounding residue on the largest line, so per-material
        // account overrides (acc_sales / acc_purchase / …) still carry
        // their share and the aggregated entry balances on the stored
        // total to the last 0.0001.
        var leg = new decimal[doc.Lines.Count];
        {
            var bases = doc.Lines
                .Select((line, index) => decimal.Round(
                    line.LineSubtotal + (isSalesDocument ? 0m :
                        (splitTax ? nonRecoverableTaxByLine[index] : line.LineTax)),
                    4, MidpointRounding.AwayFromZero))
                .ToArray();
            var adjustment = (grand - taxLeg) - bases.Sum();
            if (adjustment != 0m && bases.Length > 0)
            {
                var weights = doc.Lines.Select(l => Math.Abs(l.LineSubtotal)).ToArray();
                var weightSum = weights.Sum();
                decimal allocated = 0m;
                var largest = 0;
                for (var i = 0; i < bases.Length; i++)
                {
                    if (weights[i] > weights[largest]) largest = i;
                    var share = weightSum == 0m
                        ? (i == 0 ? adjustment : 0m)
                        : decimal.Round(adjustment * weights[i] / weightSum, 4, MidpointRounding.AwayFromZero);
                    leg[i] = share;
                    allocated += share;
                }
                leg[largest] += adjustment - allocated;
            }
            for (var i = 0; i < bases.Length; i++)
                leg[i] += bases[i];
        }

        void Add(string code, decimal debit, decimal credit, string? costCenter = null)
        {
            if (debit == 0 && credit == 0) return;
            lines.Add(new ShouTech.Domain.Entities.GlLineRequest(code, debit, credit, costCenter));
        }

        // COGS from the valuation engine (Step 6): StkPost applied this
        // document's stock effects earlier in THIS transaction, so the
        // movements' cost_amount IS the COGS — valued per material
        // cost_method, in base units, at the moment of posting. (Before
        // Step 6 this read the balance average_cost before posting and
        // multiplied the raw line quantity, understating cost for
        // non-base units and disagreeing with FIFO stock entirely.)
        // The pre-posting average remains only as a defensive fallback
        // for a stockable line the engine wrote no movement for.
        void AddCogs(bool reverse)
        {
            var moveType = doc.Kind switch
            {
                InvDocKind.SalesReturn => "SRT",
                InvDocKind.PosSale => "POS",
                _ => "SAL"
            };
            var byKey = new Dictionary<(long Material, long Warehouse), decimal>();
            var byMaterial = new Dictionary<long, decimal>();
            using (var mc = connection.CreateCommand())
            {
                mc.Transaction = tx;
                mc.CommandText = DatabaseRuntime.IsPostgreSql()
                    ? "SELECT material_id, warehouse_id, COALESCE(SUM(cost_amount), 0) FROM material_stock_movements WHERE company_id=$c AND document_no=$doc AND movement_type=$t AND is_deleted=FALSE GROUP BY material_id, warehouse_id;"
                    : "SELECT material_id, warehouse_id, COALESCE(SUM(cost_amount), 0) FROM material_stock_movements WHERE company_id=$c AND document_no=$doc AND movement_type=$t AND is_deleted=0 GROUP BY material_id, warehouse_id;";
                // NB: the static Add(DbCommand, ...) helper is shadowed
                // by BuildGlLines' local Add(string, ...) GL helper.
                foreach (var (name, value) in new (string, object)[]
                         { ("$c", Company), ("$doc", doc.DocNo ?? string.Empty), ("$t", moveType) })
                {
                    var p = mc.CreateParameter();
                    p.ParameterName = name;
                    p.Value = value;
                    mc.Parameters.Add(p);
                }
                using var mr = mc.ExecuteReader();
                while (mr.Read())
                {
                    var mat = Convert.ToInt64(mr.GetValue(0), CultureInfo.InvariantCulture);
                    var wh = Convert.ToInt64(mr.GetValue(1), CultureInfo.InvariantCulture);
                    var amount = Convert.ToDecimal(mr.GetValue(2), CultureInfo.InvariantCulture);
                    byKey[(mat, wh)] = amount;
                    byMaterial[mat] = byMaterial.GetValueOrDefault(mat) + amount;
                }
            }
            var linesPerMaterial = doc.Lines
                .Where(l => l.MaterialId is not null)
                .GroupBy(l => l.MaterialId!.Value)
                .ToDictionary(g => g.Key, g => g.Count());

            // Step 7: the engine's cost_amount is denominated in
            // BASE currency (stock values are base-currency, Step 6),
            // while GlLineRequest amounts are TRANSACTION currency —
            // GlPosting converts every line back to base at the
            // document's effective rate. Dividing the base cost by
            // the rate here makes the COGS leg's base amount land
            // EXACTLY on the engine's cost (rate 1 documents are
            // unchanged), instead of inflating COGS by the rate.
            void EmitCost(MaterialGlInfo info, decimal cost)
            {
                if (cost <= 0) return;
                if (effectiveRate != 1m)
                {
                    // Retain precision in this bridge conversion. GlPosting
                    // converts the line back to base currency at 4dp; a
                    // 4dp intermediate can create a one-unit rounding loss
                    // (for example 80 / 3.75 * 3.75 = 79.9999).
                    cost = decimal.Round(cost / effectiveRate, 8, MidpointRounding.AwayFromZero);
                    if (cost <= 0) return;
                }
                var cogs = CodeOr(info.AccCogs, GlAccounts.Cogs);
                var inventory = CodeOr(info.AccInventory, GlAccounts.Inventory);
                if (reverse) { Add(inventory, cost, 0); Add(cogs, 0, cost); }
                else { Add(cogs, cost, 0); Add(inventory, 0, cost); }
            }

            if (reverse)
            {
                // W6 condition routing (migration 100) for sales
                // returns. DAMAGED lines post NO cost leg at all: no
                // stock movement exists for them, and COGS is NOT
                // credited back — the original sale's COGS stands as
                // the write-off loss of the worthless goods, so the
                // journal balances and Inventory GL == stock valuation
                // (neither moves). QUARANTINE lines moved into the
                // quarantine warehouse, so their movement is keyed by
                // that warehouse, not the line's. Every movement must
                // be consumed exactly once across the document's lines
                // (the pre-W6 single-key lookup double-counted when a
                // material appeared on two lines / two warehouses):
                // exact-warehouse matches first, then the material's
                // remaining movement cost is shared across its
                // remaining lines by quantity, residue on the last.
                var quarantineWarehouseId = QualityGate.FindQuarantineWarehouse(connection, tx, provider, Company) ?? 0;
                var remainingByMaterial = new Dictionary<long, decimal>(byMaterial);
                var assigned = new decimal?[doc.Lines.Count];
                for (var i = 0; i < doc.Lines.Count; i++)
                {
                    var line = doc.Lines[i];
                    var info = infos[i];
                    if (!info.Stockable || line.Qty == 0) continue;
                    if (line.ReturnCondition == "Damaged") continue;
                    if (info.MaterialId is not long mid) continue;
                    var warehouse = line.ReturnCondition == "Quarantine" && quarantineWarehouseId > 0
                        ? quarantineWarehouseId
                        : (long)(line.WarehouseId ?? doc.WarehouseId ?? 0);
                    if (byKey.TryGetValue((mid, warehouse), out var moved))
                    {
                        assigned[i] = moved;
                        remainingByMaterial[mid] = remainingByMaterial.GetValueOrDefault(mid) - moved;
                        byKey.Remove((mid, warehouse));
                    }
                }
                var unassignedByMaterial = doc.Lines
                    .Select((line, index) => (line, index))
                    .Where(x => assigned[x.index] is null && infos[x.index].Stockable && x.line.Qty != 0
                        && x.line.ReturnCondition != "Damaged" && infos[x.index].MaterialId is not null)
                    .GroupBy(x => infos[x.index].MaterialId!.Value);
                foreach (var group in unassignedByMaterial)
                {
                    var remaining = remainingByMaterial.GetValueOrDefault(group.Key);
                    var members = group.ToList();
                    if (remaining <= 0m)
                    {
                        // No movement for this material at all (legacy
                        // rows): the pre-posting average fallback.
                        foreach (var (line, index) in members)
                            if (infos[index].AvgCost > 0)
                                EmitCost(infos[index], decimal.Round(line.Qty * infos[index].AvgCost, 4, MidpointRounding.AwayFromZero));
                        continue;
                    }
                    var totalQty = members.Sum(x => x.line.Qty);
                    var allocated = 0m;
                    for (var k = 0; k < members.Count; k++)
                    {
                        var share = k == members.Count - 1
                            ? remaining - allocated
                            : decimal.Round(remaining * members[k].line.Qty / totalQty, 4, MidpointRounding.AwayFromZero);
                        allocated += share;
                        EmitCost(infos[members[k].index], share);
                    }
                }
                for (var i = 0; i < doc.Lines.Count; i++)
                    if (assigned[i] is decimal exact)
                        EmitCost(infos[i], exact);
                return;
            }

            for (var i = 0; i < doc.Lines.Count; i++)
            {
                var line = doc.Lines[i];
                var info = infos[i];
                if (!info.Stockable || line.Qty == 0) continue;
                decimal cost;
                var warehouse = (long)(line.WarehouseId ?? doc.WarehouseId ?? 0);
                if (info.MaterialId is long mid
                    && byKey.TryGetValue((mid, warehouse), out var moved))
                {
                    cost = -moved;
                }
                else if (info.MaterialId is long midOnly
                         && linesPerMaterial.GetValueOrDefault(midOnly) == 1
                         && byMaterial.TryGetValue(midOnly, out var movedTotal))
                {
                    cost = -movedTotal;
                }
                else if (info.AvgCost > 0)
                {
                    cost = decimal.Round(line.Qty * info.AvgCost, 4, MidpointRounding.AwayFromZero);
                }
                else continue;
                EmitCost(info, cost);
            }
        }

        // Settlement account for any amount paid at the document: cash
        // box for cash, bank for checks/card; a credit document is never
        // paid at posting, so its paid portion is 0 and this is unused.
        var settleAccount = doc.PaymentMethod switch
        {
            InvPaymentMethod.Checks or InvPaymentMethod.Card => GlAccounts.Bank,
            _ => GlAccounts.Cash
        };

        switch (doc.Kind)
        {
            case InvDocKind.SalesInvoice:
            case InvDocKind.PosSale:
            {
                var paid = Math.Min(Math.Max(doc.PaidAmount, 0m), grand);
                if (doc.Kind == InvDocKind.PosSale
                    && doc.PosCashApplied is decimal splitCash && doc.PosCardApplied is decimal splitCard)
                {
                    // Split tender (Build POS 066): the collected part
                    // settles through Cash 131 for the cash tender and
                    // Bank 132 for the card tender; only the on-account
                    // remainder rides on Receivable — the same total the
                    // single-tender leg below carries, split honestly.
                    var cashLeg = Math.Min(Math.Max(splitCash, 0m), grand);
                    var cardLeg = Math.Min(Math.Max(splitCard, 0m), grand - cashLeg);
                    Add(GlAccounts.Cash, cashLeg, 0);
                    Add(GlAccounts.Bank, cardLeg, 0);
                    Add(GlAccounts.Receivable, grand - cashLeg - cardLeg, 0);
                }
                else if (doc.Kind == InvDocKind.PosSale || paid > 0)
                {
                    Add(settleAccount, paid, 0);
                    Add(GlAccounts.Receivable, grand - paid, 0);
                }
                else Add(GlAccounts.Receivable, grand, 0);
                for (var i = 0; i < doc.Lines.Count; i++)
                    Add(CodeOr(infos[i].AccSales, GlAccounts.SalesRevenue), 0, leg[i], cc);
                if (splitTax) foreach (var taxGroup in taxAccountGroups) Add(taxGroup.Account, 0, taxGroup.Amount);
                AddCogs(reverse: false);
                break;
            }
            case InvDocKind.SalesReturn:
            {
                if (string.Equals(doc.ReturnSourceKind, "DLV", StringComparison.OrdinalIgnoreCase))
                {
                    // W6 delivery-sourced return: the goods came back
                    // from a posted delivery that was never invoiced,
                    // so there is NO revenue / tax / receivable effect
                    // to reverse — the only GL is the relief of Stock
                    // Delivered But Not Billed: Dr Inventory / Cr 1215
                    // at the ORIGINAL DELIVERY cost, read from this
                    // return's own stock movements (StkPost re-entered
                    // the goods at the delivery's issue rate, so the
                    // movement cost IS that cost; damaged lines wrote
                    // no movement and relieve nothing — their 1215
                    // share stays parked until disposition, exactly
                    // like their COGS treatment). Charts without a
                    // postable 1215 fall back to Inventory (1203), the
                    // B4 delivery pattern; when the fallback makes both
                    // legs the same account the entry is a wash that
                    // keeps the audit trail uniform. Per-material
                    // inventory overrides (acc_inventory) still win for
                    // the debit leg, aggregated by account.
                    var deliveredAccount = GlPosting.TryResolveAccount(connection, tx, provider, companyGuid,
                        GlAccounts.StockDeliveredNotBilled) is not null
                            ? GlAccounts.StockDeliveredNotBilled
                            : GlAccounts.Inventory;
                    var reliefByAccount = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
                    using (var mc = connection.CreateCommand())
                    {
                        mc.Transaction = tx;
                        mc.CommandText = DatabaseRuntime.IsPostgreSql()
                            ? "SELECT material_id, COALESCE(SUM(cost_amount), 0) FROM material_stock_movements WHERE company_id=$c AND document_no=$doc AND movement_type='SRT' AND is_deleted=FALSE GROUP BY material_id;"
                            : "SELECT material_id, COALESCE(SUM(cost_amount), 0) FROM material_stock_movements WHERE company_id=$c AND document_no=$doc AND movement_type='SRT' AND is_deleted=0 GROUP BY material_id;";
                        foreach (var (name, value) in new (string, object)[]
                                 { ("$c", Company), ("$doc", doc.DocNo ?? string.Empty) })
                        {
                            var p = mc.CreateParameter();
                            p.ParameterName = name;
                            p.Value = value;
                            mc.Parameters.Add(p);
                        }
                        using var mr = mc.ExecuteReader();
                        while (mr.Read())
                        {
                            var mat = Convert.ToInt64(mr.GetValue(0), CultureInfo.InvariantCulture);
                            var amount = Convert.ToDecimal(mr.GetValue(1), CultureInfo.InvariantCulture);
                            if (amount <= 0) continue;
                            var info = infos.FirstOrDefault(x => x.MaterialId == mat);
                            var inventoryAccount = CodeOr(info?.AccInventory, GlAccounts.Inventory);
                            reliefByAccount[inventoryAccount] = reliefByAccount.GetValueOrDefault(inventoryAccount) + amount;
                        }
                    }
                    var reliefTotal = 0m;
                    foreach (var (account, amount) in reliefByAccount)
                    {
                        var amountTxn = effectiveRate != 1m
                            ? decimal.Round(amount / effectiveRate, 8, MidpointRounding.AwayFromZero)
                            : amount;
                        Add(account, amountTxn, 0);
                        reliefTotal += amountTxn;
                    }
                    Add(deliveredAccount, 0, reliefTotal);
                    break;
                }
                // A generated full reversal must mirror the original revenue
                // account exactly. A customer-initiated commercial return
                // remains classified in the contra-revenue account.
                var mirrorsOriginalSales = IsGeneratedFullReversal(connection, tx, doc)
                    && doc.OriginalDocKind is InvDocKind.SalesInvoice or InvDocKind.PosSale;
                for (var i = 0; i < doc.Lines.Count; i++)
                {
                    var revenueAccount = mirrorsOriginalSales
                        ? CodeOr(infos[i].AccSales, GlAccounts.SalesRevenue)
                        : CodeOr(infos[i].AccSalesReturn, GlAccounts.SalesReturns);
                    Add(revenueAccount, leg[i], 0, cc);
                }
                if (splitTax) foreach (var taxGroup in taxAccountGroups) Add(taxGroup.Account, taxGroup.Amount, 0);
                // Step 5 reversal symmetry: a return of a document that was
                // collected at posting mirrors its settlement leg too —
                // credit the settlement account for the paid part and only
                // the unpaid remainder to Receivable — so invoice + return
                // net to zero on EVERY account, cash/bank included.
                var paid = Math.Min(Math.Max(doc.PaidAmount, 0m), grand);
                if (doc.PosCashApplied is decimal refundCash && doc.PosCardApplied is decimal refundCard)
                {
                    // POS return split refund (Build POS 066): money
                    // goes back out through the same accounts it came
                    // in — cash refunds credit Cash, card refunds
                    // credit Bank, the on-account part off Receivable.
                    var cashLeg = Math.Min(Math.Max(refundCash, 0m), grand);
                    var cardLeg = Math.Min(Math.Max(refundCard, 0m), grand - cashLeg);
                    Add(GlAccounts.Cash, 0, cashLeg);
                    Add(GlAccounts.Bank, 0, cardLeg);
                    Add(GlAccounts.Receivable, 0, grand - cashLeg - cardLeg);
                }
                else
                {
                    Add(settleAccount, 0, paid);
                    Add(GlAccounts.Receivable, 0, grand - paid);
                }
                AddCogs(reverse: true);
                break;
            }
            case InvDocKind.PurchaseInvoice:
            {
                for (var i = 0; i < doc.Lines.Count; i++)
                    Add(CodeOr(infos[i].AccPurchase, GlAccounts.Purchases), leg[i], 0, cc);
                if (splitTax) foreach (var taxGroup in taxAccountGroups) Add(taxGroup.Account, taxGroup.Amount, 0);
                var paid = Math.Min(Math.Max(doc.PaidAmount, 0m), grand);
                Add(settleAccount, 0, paid);
                Add(GlAccounts.Payable, 0, grand - paid);
                // Withholding (W1): AP was credited GROSS above; the
                // deduction leg moves the withheld part out of AP into
                // the withholding payable account, so the AP balance is
                // NET while the gross credit stays visible in the entry.
                if (withholding is { Amount: > 0m } wht && wht.AccountCode is not null)
                {
                    Add(GlAccounts.Payable, wht.Amount, 0);
                    Add(wht.AccountCode, 0, wht.Amount);
                }
                break;
            }
            case InvDocKind.PurchaseReturn:
            {
                // Mirror of the purchase settlement leg (Step 5, as
                // SalesReturn above): the paid part comes back through
                // the settlement account, the unpaid part off Payable.
                var paid = Math.Min(Math.Max(doc.PaidAmount, 0m), grand);
                Add(GlAccounts.Payable, grand - paid, 0);
                Add(settleAccount, paid, 0);
                // A generated full reversal mirrors the original purchase
                // account. A normal supplier return remains classified in
                // the purchase-return contra account.
                var mirrorsOriginalPurchase = IsGeneratedFullReversal(connection, tx, doc)
                    && doc.OriginalDocKind == InvDocKind.PurchaseInvoice;
                for (var i = 0; i < doc.Lines.Count; i++)
                {
                    var purchaseAccount = mirrorsOriginalPurchase
                        ? CodeOr(infos[i].AccPurchase, GlAccounts.Purchases)
                        : CodeOr(infos[i].AccPurchaseReturn, GlAccounts.PurchaseReturns);
                    Add(purchaseAccount, 0, leg[i], cc);
                }
                // I4 GRNI mirror: when this return unwound mig-078
                // matches, the original chain invoice had cleared the
                // GRNI accrual at receipt value with the difference in
                // PPV. Mirror that clearing for the unwound portion —
                // reclass it out of the purchase credits above into
                // Cr GRNI (receipt base) and Cr/Dr PPV (invoice base −
                // receipt base) — so the accrual revives by exactly the
                // unwound receipt base: the period-end GRNI
                // reconciliation (match-ledger received-not-invoiced vs
                // GL 2203) stays exact across the return, and a later
                // re-invoice clears the accrual instead of
                // double-clearing it. Match base amounts are
                // base-currency; the /rate bridge matches AddCogs.
                if (unwoundChain.ReceiptBase != 0m || unwoundChain.InvoiceBase != 0m)
                {
                    var invoiceTxn = effectiveRate == 1m ? unwoundChain.InvoiceBase
                        : decimal.Round(unwoundChain.InvoiceBase / effectiveRate, 8, MidpointRounding.AwayFromZero);
                    var receiptTxn = effectiveRate == 1m ? unwoundChain.ReceiptBase
                        : decimal.Round(unwoundChain.ReceiptBase / effectiveRate, 8, MidpointRounding.AwayFromZero);
                    var legTotal = leg.Sum();
                    if (legTotal != 0m && invoiceTxn != 0m)
                    {
                        decimal allocatedBack = 0m;
                        for (var i = 0; i < doc.Lines.Count; i++)
                        {
                            var purchaseAccount = mirrorsOriginalPurchase
                                ? CodeOr(infos[i].AccPurchase, GlAccounts.Purchases)
                                : CodeOr(infos[i].AccPurchaseReturn, GlAccounts.PurchaseReturns);
                            var share = i == doc.Lines.Count - 1
                                ? invoiceTxn - allocatedBack
                                : decimal.Round(invoiceTxn * leg[i] / legTotal, 4, MidpointRounding.AwayFromZero);
                            allocatedBack += share;
                            Add(purchaseAccount, share, 0, cc);
                        }
                    }
                    Add(GlAccounts.GoodsReceivedNotInvoiced, 0, receiptTxn, cc);
                    var varianceTxn = invoiceTxn - receiptTxn;
                    if (varianceTxn > 0m) Add(GlAccounts.PurchasePriceVariance, 0, varianceTxn, cc);
                    else if (varianceTxn < 0m) Add(GlAccounts.PurchasePriceVariance, -varianceTxn, 0, cc);
                }
                if (splitTax) foreach (var taxGroup in taxAccountGroups) Add(taxGroup.Account, 0, taxGroup.Amount);
                // Withholding mirror (W1): a return reversing a withheld
                // invoice debits the withholding payable back and credits
                // AP for the withheld part, exactly mirroring the
                // original deduction leg (the Dr AP grand above already
                // carries the gross, so AP nets to zero with the
                // original's net balance).
                if (reversalWithholding.Amount > 0m && reversalWithholding.Account is not null)
                {
                    Add(reversalWithholding.Account, reversalWithholding.Amount, 0);
                    Add(GlAccounts.Payable, 0, reversalWithholding.Amount);
                }
                break;
            }
            case InvDocKind.StockIn:
                Add(GlAccounts.Inventory, grand, 0);
                Add(GlAccounts.Purchases, 0, grand);
                break;
            case InvDocKind.StockOut:
                Add(GlAccounts.Cogs, grand, 0);
                Add(GlAccounts.Inventory, 0, grand);
                break;
        }
        return lines;
    }


    /// <summary>
    /// Writes an immutable, line-level tax snapshot for a posted document.
    /// The snapshot is transactionally coupled to the GL posting and records
    /// the exact effective rule used at the document date.
    /// </summary>
    private static void WriteTaxTransactions(
        DbConnection connection, DbTransaction tx, InvDoc doc,
        string journalNo, string userId, decimal exchangeRate)
    {
        var enabledValue = DatabaseRuntime.IsPostgreSql() ? "TRUE" : "1";
        string countryCode;
        using (var settings = connection.CreateCommand())
        {
            settings.Transaction = tx;
            settings.CommandText = $"""
                SELECT country_code
                FROM company_tax_settings
                WHERE company_code = $company AND tax_enabled = {enabledValue}
                LIMIT 1;
                """;
            Add(settings, "$company", Company);
            countryCode = Convert.ToString(settings.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(countryCode))
        {
            if (doc.PostingTaxAmount != 0m || doc.Lines.Any(line => line.TaxRate != 0m))
                throw new InvalidOperationException(
                    "Tax is present on this document, but the company's tax system is not enabled. Enable the correct tax system or remove tax before posting.");
            return;
        }

        var isSalesTax = doc.Kind.IsSales() || doc.Kind == InvDocKind.PosSale;
        var direction = isSalesTax ? "OUTPUT" : "INPUT";
        var isReturn = doc.Kind is InvDocKind.SalesReturn or InvDocKind.PurchaseReturn;
        decimal registeredTax = 0m;

        foreach (var line in doc.Lines)
        {
            if (string.IsNullOrWhiteSpace(line.TaxCode))
            {
                if (line.TaxRate != 0m || line.LineTax != 0m)
                    throw new InvalidOperationException(
                        $"Invoice line {line.LineNo} has a tax rate but no tax code. Select a valid tax system code before posting.");
                continue;
            }

            string taxType;
            string treatment;
            string accountCode;
            bool recoverable;
            decimal approvedRate;
            string dataStatus;
            using (var rule = connection.CreateCommand())
            {
                rule.Transaction = tx;
                var documentDateExpression = DatabaseRuntime.IsPostgreSql()
                    ? "CAST($documentDate AS date)" : "$documentDate";
                rule.CommandText = $"""
                    SELECT tax_type, tax_treatment, rate, is_recoverable,
                           CASE WHEN $direction = 'OUTPUT' THEN COALESCE(output_account_code, '')
                                ELSE COALESCE(input_account_code, '') END,
                           COALESCE(data_status, 'USER_CONFIGURED')
                    FROM company_tax_rules
                    WHERE company_code = $company
                      AND country_code = $country
                      AND tax_code = $code
                      AND is_active = {enabledValue}
                      AND effective_from <= {documentDateExpression}
                      AND (effective_to IS NULL OR effective_to >= {documentDateExpression})
                    ORDER BY effective_from DESC
                    LIMIT 1;
                    """;
                Add(rule, "$company", Company);
                Add(rule, "$country", countryCode);
                Add(rule, "$code", line.TaxCode.Trim());
                Add(rule, "$direction", direction);
                Add(rule, "$documentDate", doc.DocDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                using var reader = rule.ExecuteReader();
                if (!reader.Read())
                    throw new InvalidOperationException(
                        $"Tax code '{line.TaxCode}' on invoice line {line.LineNo} has no active rule for {countryCode} on {doc.DocDate:yyyy-MM-dd}.");
                taxType = Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture) ?? string.Empty;
                treatment = Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) ?? "Standard";
                approvedRate = Convert.ToDecimal(reader.GetValue(2), CultureInfo.InvariantCulture);
                recoverable = reader.GetValue(3) is bool flag
                    ? flag
                    : Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture) == 1;
                accountCode = Convert.ToString(reader.GetValue(4), CultureInfo.InvariantCulture) ?? string.Empty;
                dataStatus = Convert.ToString(reader.GetValue(5), CultureInfo.InvariantCulture) ?? "USER_CONFIGURED";
            }

            if (string.Equals(dataStatus, "BASELINE_REVIEW_REQUIRED", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Tax rule '{line.TaxCode}' is a starter rule and has not been reviewed. Open Tax Configuration, review the effective rate and account mapping, then save the rule before posting.");

            if (string.IsNullOrWhiteSpace(accountCode))
                accountCode = direction == "OUTPUT" ? GlAccounts.OutputVat : GlAccounts.InputVat;

            if (Math.Abs(line.TaxRate - approvedRate) > 0.000001m)
                throw new InvalidOperationException(
                    $"Tax rate on invoice line {line.LineNo} does not match the approved effective rate for '{line.TaxCode}'. Reload the tax code and recalculate the invoice.");

            var amount = isReturn ? -line.LineTax : line.LineTax;
            registeredTax += amount;
            using var insert = connection.CreateCommand();
            insert.Transaction = tx;
            var insertDocumentDateExpression = DatabaseRuntime.IsPostgreSql()
                ? "CAST($docDate AS date)" : "$docDate";
            insert.CommandText = $"""
                INSERT INTO tax_transactions
                    (company_code, country_code, source_doc_kind, source_doc_id, source_doc_no,
                     line_no, tax_code, tax_type, tax_treatment, tax_rate, taxable_amount,
                     tax_amount, is_recoverable, tax_direction, tax_account_code, currency_code,
                     exchange_rate, base_tax_amount, journal_doc_no, created_by, document_date)
                VALUES
                    ($company, $country, $kind, $docId, $docNo,
                     $lineNo, $taxCode, $taxType, $treatment, $rate, $taxable,
                     $tax, $recoverable, $direction, $account, $currency,
                     $exchangeRate, $baseTax, $journal, $user, {insertDocumentDateExpression})
                ON CONFLICT (company_code, source_doc_kind, source_doc_id, line_no, tax_code) DO NOTHING;
                """;
            Add(insert, "$docDate", doc.DocDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            Add(insert, "$company", Company);
            Add(insert, "$country", countryCode);
            Add(insert, "$kind", doc.KindCode);
            Add(insert, "$docId", doc.DocId);
            Add(insert, "$docNo", doc.DocNo);
            Add(insert, "$lineNo", line.LineNo);
            Add(insert, "$taxCode", line.TaxCode.Trim());
            Add(insert, "$taxType", taxType);
            Add(insert, "$treatment", treatment);
            Add(insert, "$rate", approvedRate);
            Add(insert, "$taxable", isReturn ? -line.LineSubtotal : line.LineSubtotal);
            Add(insert, "$tax", amount);
            Add(insert, "$recoverable", recoverable);
            Add(insert, "$direction", direction);
            Add(insert, "$account", accountCode);
            Add(insert, "$currency", doc.CurrencyCode);
            Add(insert, "$exchangeRate", exchangeRate);
            Add(insert, "$baseTax", decimal.Round(amount * exchangeRate, 6, MidpointRounding.AwayFromZero));
            Add(insert, "$journal", journalNo);
            Add(insert, "$user", userId);
            insert.ExecuteNonQuery();
        }

        if (Math.Abs(registeredTax - (isReturn ? -doc.PostingTaxAmount : doc.PostingTaxAmount)) > 0.0001m)
            throw new InvalidOperationException(
                "The invoice tax lines do not reconcile with the stored document tax total. Recalculate the invoice before posting.");
    }

    /// <summary>
    /// I4 (migration 098): decrements the mig-078 receipt↔invoice
    /// matched quantities for every line this purchase return allocates
    /// against its original invoice, recording the exact decrement per
    /// match row in purchase_return_match_unwinds so a later reversal
    /// re-winds precisely. Runs inside the posting transaction, after
    /// ValidateAndAllocatePurchaseReturnAgainstOriginal has written the
    /// mig-080 allocations under the original invoice's lock — the same
    /// lock discipline, so concurrent returns cannot unwind the same
    /// matched quantity twice. Fully-consumed match rows are deleted
    /// (matched_qty has a &gt; 0 CHECK); the re-wind re-creates them
    /// from the receipt/invoice documents. Returns the base amounts
    /// that left the matches (receipt base / invoice base) so
    /// BuildGlLines can mirror the original invoice's GRNI clearing
    /// for exactly the unwound portion.
    /// </summary>
    private static (decimal ReceiptBase, decimal InvoiceBase) UnwindPurchaseReturnMatches(
        DbConnection connection, DbTransaction transaction, string guidKey, InvDoc returnDoc, string userId)
    {
        if (returnDoc.OriginalDocId is not long originalDocId || originalDocId <= 0)
            return (0m, 0m);
        var provider = DatabaseRuntime.IsPostgreSql() ? "PostgreSQL" : "SQLite";
        if (!PartyLedger.TableExists(connection, transaction, provider, "purchase_receipt_invoice_matches") ||
            !PartyLedger.TableExists(connection, transaction, provider, "purchase_return_match_unwinds"))
            return (0m, 0m);

        var allocations = new List<(long OriginalLineId, decimal ReturnedQty)>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = """
                SELECT original_line_id, returned_qty
                FROM purchase_return_line_allocations
                WHERE return_doc_id = $ret AND company_id = $company
                ORDER BY original_line_id;
                """;
            Add(cmd, "$ret", returnDoc.DocId);
            Add(cmd, "$company", Company);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                allocations.Add((Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                    Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture)));
        }

        var affectedOrders = new HashSet<long>();
        decimal unwoundReceiptBase = 0m, unwoundInvoiceBase = 0m;
        foreach (var (originalLineId, returnedQty) in allocations)
        {
            var remaining = returnedQty;
            var matches = new List<(string MatchId, string CompanyId, long PoId, long ReceiptDocId, long ReceiptLineId, decimal MatchedQty, decimal ReceiptBaseAmount, decimal InvoiceBaseAmount)>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = """
                    SELECT match_id, company_id, purchase_order_id, receipt_doc_id, receipt_line_id, matched_qty,
                           receipt_base_amount, invoice_base_amount
                    FROM purchase_receipt_invoice_matches
                    WHERE invoice_doc_id = $inv AND invoice_line_id = $line
                    ORDER BY receipt_doc_id, receipt_line_id;
                    """;
                Add(cmd, "$inv", originalDocId);
                Add(cmd, "$line", originalLineId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    matches.Add((
                        Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture) ?? string.Empty,
                        Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) ?? string.Empty,
                        Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture),
                        Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture),
                        Convert.ToInt64(reader.GetValue(4), CultureInfo.InvariantCulture),
                        Convert.ToDecimal(reader.GetValue(5), CultureInfo.InvariantCulture),
                        Convert.ToDecimal(reader.GetValue(6), CultureInfo.InvariantCulture),
                        Convert.ToDecimal(reader.GetValue(7), CultureInfo.InvariantCulture)));
            }

            foreach (var match in matches)
            {
                if (remaining <= 0m) break;
                var take = Math.Min(remaining, match.MatchedQty);
                if (take <= 0m) continue;
                remaining -= take;
                affectedOrders.Add(match.PoId);

                // The base amounts leaving the match with this take —
                // the complement of the scaling the UPDATE below applies,
                // so unwound + remaining == the original stored amounts.
                if (take >= match.MatchedQty)
                {
                    unwoundReceiptBase += match.ReceiptBaseAmount;
                    unwoundInvoiceBase += match.InvoiceBaseAmount;
                }
                else
                {
                    var keepFactor = (match.MatchedQty - take) / match.MatchedQty;
                    unwoundReceiptBase += match.ReceiptBaseAmount - match.ReceiptBaseAmount * keepFactor;
                    unwoundInvoiceBase += match.InvoiceBaseAmount - match.InvoiceBaseAmount * keepFactor;
                }

                using (var upd = connection.CreateCommand())
                {
                    upd.Transaction = transaction;
                    if (take >= match.MatchedQty)
                    {
                        upd.CommandText = "DELETE FROM purchase_receipt_invoice_matches WHERE match_id = $match;";
                    }
                    else
                    {
                        var factor = (match.MatchedQty - take) / match.MatchedQty;
                        upd.CommandText = """
                            UPDATE purchase_receipt_invoice_matches
                            SET matched_qty = matched_qty - $take,
                                receipt_base_amount = receipt_base_amount * $factor,
                                invoice_base_amount = invoice_base_amount * $factor,
                                variance_base_amount = (invoice_base_amount * $factor) - (receipt_base_amount * $factor)
                            WHERE match_id = $match;
                            """;
                        Add(upd, "$factor", factor);
                    }
                    Add(upd, "$take", take);
                    Add(upd, "$match", match.MatchId);
                    upd.ExecuteNonQuery();
                }

                using var ins = connection.CreateCommand();
                ins.Transaction = transaction;
                ins.CommandText = """
                    INSERT INTO purchase_return_match_unwinds
                        (company_id, return_doc_id, invoice_doc_id, match_id, receipt_doc_id,
                         receipt_line_id, invoice_line_id, unwound_qty, created_by)
                    VALUES ($company, $ret, $inv, $match, $receiptDoc, $receiptLine, $invLine, $qty, $actor);
                    """;
                Add(ins, "$company", match.CompanyId);
                Add(ins, "$ret", returnDoc.DocId);
                Add(ins, "$inv", originalDocId);
                Add(ins, "$match", match.MatchId);
                Add(ins, "$receiptDoc", match.ReceiptDocId);
                Add(ins, "$receiptLine", match.ReceiptLineId);
                Add(ins, "$invLine", originalLineId);
                Add(ins, "$qty", take);
                Add(ins, "$actor", userId);
                ins.ExecuteNonQuery();
            }
        }

        foreach (var poId in affectedOrders)
            RefreshPoStoredPerBilled(connection, transaction, guidKey, poId, inFlightReturnDocId: returnDoc.DocId);
        return (unwoundReceiptBase, unwoundInvoiceBase);
    }

    /// <summary>
    /// I4 re-wind: replays the stored unwind rows of a purchase return
    /// exactly (the L3 stored-split rule) — matched quantities and base
    /// amounts are restored, fully-unwound match rows are re-created
    /// from the receipt/invoice documents, the unwind rows are consumed,
    /// and the PO's stored per_billed is refreshed. Called when a posted
    /// PRT is reversed; the PRT document itself is never edited here.
    /// </summary>
    public static void RewindPurchaseReturnMatches(long returnDocId, string userId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(returnDocId);
        DocSchema.Ensure();
        var cs = DatabaseRuntime.GetConnectionString()
            ?? throw new InvalidOperationException("No database connection.");
        using var connection = Open(cs);
        using var tx = connection.BeginTransaction();
        var provider = DatabaseRuntime.IsPostgreSql() ? "PostgreSQL" : "SQLite";
        if (!PartyLedger.TableExists(connection, tx, provider, "purchase_return_match_unwinds"))
        {
            tx.Commit();
            return;
        }
        var guidKey = ResolveCompanyGuidOrNull(connection, tx)?.ToString("D") ?? Company;

        var unwinds = new List<(long Id, long InvoiceDocId, string MatchId, long ReceiptDocId, long ReceiptLineId, long InvoiceLineId, decimal Qty)>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT unwind_id, invoice_doc_id, match_id, receipt_doc_id, receipt_line_id, invoice_line_id, unwound_qty
                FROM purchase_return_match_unwinds WHERE return_doc_id = $ret ORDER BY unwind_id;
                """;
            Add(cmd, "$ret", returnDocId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                unwinds.Add((
                    Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                    Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                    Convert.ToString(reader.GetValue(2), CultureInfo.InvariantCulture) ?? string.Empty,
                    Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture),
                    Convert.ToInt64(reader.GetValue(4), CultureInfo.InvariantCulture),
                    Convert.ToInt64(reader.GetValue(5), CultureInfo.InvariantCulture),
                    Convert.ToDecimal(reader.GetValue(6), CultureInfo.InvariantCulture)));
        }

        var affectedOrders = new HashSet<long>();
        foreach (var unwind in unwinds)
        {
            decimal existingQty = 0m;
            long poId = 0;
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT matched_qty, purchase_order_id FROM purchase_receipt_invoice_matches WHERE match_id = $match;";
                Add(cmd, "$match", unwind.MatchId);
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    existingQty = Convert.ToDecimal(reader.GetValue(0), CultureInfo.InvariantCulture);
                    poId = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
                }
            }

            if (existingQty > 0m)
            {
                var factor = (existingQty + unwind.Qty) / existingQty;
                using var upd = connection.CreateCommand();
                upd.Transaction = tx;
                upd.CommandText = """
                    UPDATE purchase_receipt_invoice_matches
                    SET matched_qty = matched_qty + $qty,
                        receipt_base_amount = receipt_base_amount * $factor,
                        invoice_base_amount = invoice_base_amount * $factor,
                        variance_base_amount = (invoice_base_amount * $factor) - (receipt_base_amount * $factor)
                    WHERE match_id = $match;
                    """;
                Add(upd, "$qty", unwind.Qty);
                Add(upd, "$factor", factor);
                Add(upd, "$match", unwind.MatchId);
                upd.ExecuteNonQuery();
            }
            else
            {
                // Re-create the fully-unwound row from the documents:
                // receipt line cost/rate and invoice line cost/rate are
                // the same snapshots MatchPurchaseInvoiceToReceipts
                // originally stored.
                long materialId = 0;
                decimal receiptCost = 0m, receiptRate = 1m, invoiceCost = 0m, invoiceRate = 1m;
                using (var cmd = connection.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = """
                        SELECT rl.material_id, rl.unit_price * (1 - rl.discount_pct / 100), rd.exchange_rate,
                               il.unit_price * (1 - il.discount_pct / 100), idoc.exchange_rate, dl.source_doc_id
                        FROM purchase_document_lines rl
                        JOIN purchase_documents rd ON rd.doc_id = rl.doc_id
                        JOIN purchase_document_lines il ON il.line_id = $invLine
                        JOIN purchase_documents idoc ON idoc.doc_id = il.doc_id
                        LEFT JOIN document_links dl
                          ON dl.target_doc_kind = 'PUR' AND dl.target_doc_id = idoc.doc_id AND dl.source_doc_kind = 'PO'
                        WHERE rl.line_id = $receiptLine;
                        """;
                    Add(cmd, "$invLine", unwind.InvoiceLineId);
                    Add(cmd, "$receiptLine", unwind.ReceiptLineId);
                    using var reader = cmd.ExecuteReader();
                    if (!reader.Read())
                        throw new InvalidOperationException("The original receipt/invoice lines for a match re-wind were not found.");
                    materialId = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
                    receiptCost = Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture);
                    receiptRate = Convert.ToDecimal(reader.GetValue(2), CultureInfo.InvariantCulture);
                    invoiceCost = Convert.ToDecimal(reader.GetValue(3), CultureInfo.InvariantCulture);
                    invoiceRate = Convert.ToDecimal(reader.GetValue(4), CultureInfo.InvariantCulture);
                    poId = reader.IsDBNull(5) ? 0 : Convert.ToInt64(reader.GetValue(5), CultureInfo.InvariantCulture);
                }
                var receiptBase = decimal.Round(unwind.Qty * receiptCost * receiptRate, 6, MidpointRounding.AwayFromZero);
                var invoiceBase = decimal.Round(unwind.Qty * invoiceCost * invoiceRate, 6, MidpointRounding.AwayFromZero);
                using var ins = connection.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT INTO purchase_receipt_invoice_matches
                        (match_id, company_id, purchase_order_id, receipt_doc_id, receipt_line_id,
                         invoice_doc_id, invoice_line_id, material_id, matched_qty, receipt_unit_cost,
                         receipt_exchange_rate, invoice_unit_cost, invoice_exchange_rate,
                         receipt_base_amount, invoice_base_amount, variance_base_amount, created_by)
                    VALUES ($match, $company, $po, $receiptDoc, $receiptLine, $inv, $invLine, $material, $qty,
                            $receiptCost, $receiptRate, $invoiceCost, $invoiceRate,
                            $receiptBase, $invoiceBase, $variance, $actor);
                    """;
                Add(ins, "$match", unwind.MatchId);
                Add(ins, "$company", guidKey);
                Add(ins, "$po", poId);
                Add(ins, "$receiptDoc", unwind.ReceiptDocId);
                Add(ins, "$receiptLine", unwind.ReceiptLineId);
                Add(ins, "$inv", unwind.InvoiceDocId);
                Add(ins, "$invLine", unwind.InvoiceLineId);
                Add(ins, "$material", materialId);
                Add(ins, "$qty", unwind.Qty);
                Add(ins, "$receiptCost", receiptCost);
                Add(ins, "$receiptRate", receiptRate <= 0m ? 1m : receiptRate);
                Add(ins, "$invoiceCost", invoiceCost);
                Add(ins, "$invoiceRate", invoiceRate <= 0m ? 1m : invoiceRate);
                Add(ins, "$receiptBase", receiptBase);
                Add(ins, "$invoiceBase", invoiceBase);
                Add(ins, "$variance", invoiceBase - receiptBase);
                Add(ins, "$actor", userId);
                ins.ExecuteNonQuery();
            }
            if (poId > 0) affectedOrders.Add(poId);

            using var del = connection.CreateCommand();
            del.Transaction = tx;
            del.CommandText = "DELETE FROM purchase_return_match_unwinds WHERE unwind_id = $id;";
            Add(del, "$id", unwind.Id);
            del.ExecuteNonQuery();
        }

        foreach (var poId in affectedOrders)
            RefreshPoStoredPerBilled(connection, tx, guidKey, poId, excludeReturnDocId: returnDocId);
        tx.Commit();
    }

    /// <summary>
    /// I4/P5: recomputes one purchase order's STORED per_billed inside
    /// the caller's transaction, from posted chain invoices net of
    /// posted purchase-return allocations — the same semantics as
    /// OrderDocumentRepository's stored-progress maintenance.
    /// <paramref name="inFlightReturnDocId"/> names the return whose
    /// posting is running this refresh: its header is still 'Draft'
    /// (the status flip happens later in Post), so without it the
    /// refresh would miss the very return that triggered it and store
    /// the pre-return value. <paramref name="excludeReturnDocId"/>
    /// names the return being rewound: its allocations must stop
    /// netting even before its reversal flips the header status.
    /// </summary>
    private static void RefreshPoStoredPerBilled(DbConnection connection, DbTransaction transaction, string guidKey, long purchaseOrderId,
        long inFlightReturnDocId = 0, long excludeReturnDocId = 0)
    {
        var lines = new List<(int LineNo, decimal Qty)>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = "SELECT line_no, qty FROM purchase_document_lines WHERE doc_id = $po ORDER BY line_no;";
            Add(cmd, "$po", purchaseOrderId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                lines.Add((Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture),
                    Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture)));
        }
        if (lines.Count == 0) return;

        decimal totalOrdered = 0m, totalBilled = 0m;
        foreach (var (lineNo, qty) in lines)
        {
            totalOrdered += qty;
            decimal billed = 0m;
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = """
                    SELECT COALESCE(SUM(il.qty), 0)
                    FROM document_links dl
                    JOIN purchase_documents inv
                      ON inv.doc_id = dl.target_doc_id AND inv.doc_kind = 'PUR' AND inv.status = 'Posted'
                     AND CAST(inv.is_deleted AS TEXT) IN ('0', 'false')
                    JOIN purchase_document_lines il ON il.doc_id = inv.doc_id AND il.line_no = $lineNo
                    WHERE dl.source_doc_kind = 'PO' AND dl.source_doc_id = $po AND dl.target_doc_kind = 'PUR';
                    """;
                Add(cmd, "$lineNo", lineNo);
                Add(cmd, "$po", purchaseOrderId);
                billed = Convert.ToDecimal(cmd.ExecuteScalar() ?? 0m, CultureInfo.InvariantCulture);
            }
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = """
                    SELECT COALESCE(SUM(a.returned_qty), 0)
                    FROM purchase_return_line_allocations a
                    JOIN purchase_documents prt
                      ON prt.doc_id = a.return_doc_id AND prt.doc_kind = 'PRT'
                     AND (prt.status = 'Posted' OR prt.doc_id = $inFlight)
                     AND prt.doc_id <> $exclude
                     AND CAST(prt.is_deleted AS TEXT) IN ('0', 'false')
                    JOIN purchase_document_lines il ON il.line_id = a.original_line_id
                    JOIN document_links dl
                      ON dl.target_doc_kind = 'PUR' AND dl.target_doc_id = a.original_doc_id
                     AND dl.source_doc_kind = 'PO' AND dl.source_doc_id = $po
                    WHERE il.line_no = $lineNo;
                    """;
                Add(cmd, "$lineNo", lineNo);
                Add(cmd, "$po", purchaseOrderId);
                Add(cmd, "$inFlight", inFlightReturnDocId);
                Add(cmd, "$exclude", excludeReturnDocId);
                billed -= Convert.ToDecimal(cmd.ExecuteScalar() ?? 0m, CultureInfo.InvariantCulture);
            }
            totalBilled += Math.Min(qty, Math.Max(0m, billed));
        }
        var perBilled = totalOrdered <= 0m ? 0m : decimal.Round(100m * totalBilled / totalOrdered, 2);
        using var upd = connection.CreateCommand();
        upd.Transaction = transaction;
        upd.CommandText = "UPDATE purchase_documents SET per_billed = $per WHERE doc_id = $po AND (company_id = $company OR company_id = $guid);";
        Add(upd, "$per", perBilled);
        Add(upd, "$po", purchaseOrderId);
        Add(upd, "$company", Company);
        Add(upd, "$guid", guidKey);
        upd.ExecuteNonQuery();
    }

    private static void EnsureUniqueSupplierInvoiceReference(DbConnection connection, DbTransaction transaction, InvDoc doc)
    {
        if (doc.Kind != InvDocKind.PurchaseInvoice ||
            doc.PartyId is not long supplierId || supplierId <= 0 ||
            string.IsNullOrWhiteSpace(doc.Reference))
            return;

        var guidKey = ResolveCompanyGuidOrNull(connection, transaction)?.ToString("D") ?? Company;
        var normalizedReference = doc.Reference.Trim().ToUpperInvariant();

        // The partial unique index protects a single stored company key. Older
        // documents may carry the company code while newer order-chain rows use
        // the company GUID, so serialize application writes on the canonical
        // company/supplier/reference tuple before checking both aliases.
        if (DatabaseRuntime.IsPostgreSql())
        {
            using var lockCommand = connection.CreateCommand();
            lockCommand.Transaction = transaction;
            lockCommand.CommandText = """
                SELECT pg_advisory_xact_lock(
                    hashtextextended($lock_key, 0));
                """;
            Add(lockCommand, "$lock_key",
                $"{guidKey}|{supplierId.ToString(CultureInfo.InvariantCulture)}|{normalizedReference}");
            lockCommand.ExecuteNonQuery();
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT doc_no
            FROM purchase_documents
            WHERE (company_id = $company OR company_id = $guid)
              AND doc_kind = 'PUR'
              AND party_id = $supplier
              AND lower(trim(reference_no)) = lower(trim($reference))
              AND is_deleted = 0
              AND ($current IS NULL OR doc_id <> $current)
            LIMIT 1;
            """;
        Add(command, "$company", Company);
        Add(command, "$guid", guidKey);
        Add(command, "$supplier", supplierId);
        Add(command, "$reference", normalizedReference);
        Add(command, "$current", doc.DocId > 0 ? doc.DocId : null);

        var existingDocNo = command.ExecuteScalar();
        if (existingDocNo is not null and not DBNull)
            // I1: the SAME catalogued error the chain path raises
            // (PUR-1011) — one code for one rule on both paths.
            throw new AppError(ErrorCodes.SupplierBillReferenceDuplicate,
                $"Supplier bill '{doc.Reference.Trim()}' already exists for this supplier.",
                doc.Reference.Trim(),
                Convert.ToString(existingDocNo, CultureInfo.InvariantCulture) ?? string.Empty);
    }

    /// <summary>
    /// Y3 (migration 094): stamp purchase_documents.due_date for a purchase
    /// invoice inside the SaveDraft transaction. An explicit due date on the
    /// document wins; otherwise the supplier's payment terms
    /// (parties.credit_days, resolved by the canonical ledger_id the header
    /// carries) are added to the document date; with no resolvable terms the
    /// due date is the document date. The effective value is written back to
    /// the in-memory document so the window shows what was persisted.
    /// </summary>
    private static void StampPurchaseDueDate(DbConnection connection, DbTransaction tx, long docId, InvDoc doc)
    {
        var dueDate = doc.DueDate?.Date ?? doc.DocDate.Date;
        if (doc.DueDate is null && doc.PartyId is long supplierLedgerId)
        {
            using var lookup = connection.CreateCommand();
            lookup.Transaction = tx;
            lookup.CommandText = "SELECT credit_days FROM parties WHERE ledger_id = $party AND kind = 'S' AND is_deleted = "
                + (DatabaseRuntime.IsPostgreSql() ? "FALSE" : "0");
            Add(lookup, "$party", supplierLedgerId);
            var terms = lookup.ExecuteScalar();
            if (terms is not null and not DBNull)
            {
                var creditDays = Convert.ToInt32(terms, CultureInfo.InvariantCulture);
                if (creditDays > 0)
                    dueDate = doc.DocDate.Date.AddDays(creditDays);
            }
        }

        doc.DueDate = dueDate;
        using var update = connection.CreateCommand();
        update.Transaction = tx;
        update.CommandText = "UPDATE purchase_documents SET due_date = $due WHERE doc_id = $id;";
        Add(update, "$due", dueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add(update, "$id", docId);
        update.ExecuteNonQuery();
    }

    private static void BindHeader(DbCommand cmd, InvDoc doc)
    {
        Add(cmd, "$company", Company);
        Add(cmd, "$kind", doc.KindCode);
        Add(cmd, "$no", doc.DocNo);
        Add(cmd, "$date", doc.DocDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add(cmd, "$party", doc.PartyId);
        Add(cmd, "$pcode", doc.PartyCode);
        Add(cmd, "$pname", doc.PartyName);
        Add(cmd, "$wh", doc.WarehouseId);
        Add(cmd, "$whname", doc.WarehouseName);
        Add(cmd, "$cur", doc.CurrencyCode);
        Add(cmd, "$fx", doc.ExchangeRate);
        Add(cmd, "$sub", doc.Subtotal);
        Add(cmd, "$disc", doc.LinesDiscountAmount);
        Add(cmd, "$extra", doc.LinesExtraAmount);
        Add(cmd, "$tax", doc.TaxAmount);
        Add(cmd, "$grand", doc.InvoiceValue);
        Add(cmd, "$paid", doc.PaidAmount);
        Add(cmd, "$tcode", doc.TaxCode);
        Add(cmd, "$trate", doc.DefaultTaxRate);
        Add(cmd, "$notes", doc.Notes);
        Add(cmd, "$pay", doc.PaymentMethod.ToString());
        Add(cmd, "$receipt", doc.ReceiptNo);
        Add(cmd, "$ref", doc.Reference);
        Add(cmd, "$cnref", string.IsNullOrWhiteSpace(doc.SupplierCreditNoteRef) ? null : doc.SupplierCreditNoteRef.Trim());
        Add(cmd, "$branch", doc.BranchName);
        Add(cmd, "$rep", doc.SalesRepName);
        Add(cmd, "$cc", doc.CostCenter);
        Add(cmd, "$pacc", doc.PartyAccountName);
        Add(cmd, "$gacc", doc.GoodsAccountName);
        Add(cmd, "$origId", doc.OriginalDocId);
        Add(cmd, "$origKind", doc.OriginalDocKind?.ToString());
        Add(cmd, "$origNo", doc.OriginalDocNo);
        Add(cmd, "$jrn", doc.JournalDocNo);
        Add(cmd, "$twh", doc.TargetWarehouseId);
        Add(cmd, "$twhname", doc.TargetWarehouseName);
        Add(cmd, "$validTill", doc.ValidTill?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add(cmd, "$user", AppState.CurrentUser?.Username ?? Environment.UserName);
    }

    /// <summary>W6 (migration 100) header bindings, kept apart from
    /// BindHeader because the columns exist only once migration 100
    /// has been applied; SaveDraft binds them under the same
    /// ColumnExists condition that extends its SQL.</summary>
    private static void BindW6Header(DbCommand cmd, InvDoc doc)
    {
        Add(cmd, "$rreason", string.IsNullOrWhiteSpace(doc.ReturnReasonCode) ? null : doc.ReturnReasonCode.Trim());
        Add(cmd, "$rsource", string.IsNullOrWhiteSpace(doc.ReturnSourceKind) ? null : doc.ReturnSourceKind.Trim());
        Add(cmd, "$noinv", DatabaseRuntime.IsPostgreSql() ? (object)doc.NoInvoice : (doc.NoInvoice ? 1 : 0));
        Add(cmd, "$insp", string.IsNullOrWhiteSpace(doc.InspectionStatus) ? "NotRequired" : doc.InspectionStatus);
        Add(cmd, "$appr", string.IsNullOrWhiteSpace(doc.ApprovalStatus) ? "NotRequired" : doc.ApprovalStatus);
    }

    /// <summary>W5 (migration 102) export header bindings, kept apart
    /// from BindHeader because the columns exist only on the sales
    /// header table once migration 102 has been applied; SaveDraft
    /// binds them under the same ColumnExists condition that extends
    /// its SQL. Empty text fields persist as NULL, matching the chain
    /// repository's export columns.</summary>
    private static void BindExportHeader(DbCommand cmd, InvDoc doc)
    {
        static string? Clean(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        Add(cmd, "$isexp", DatabaseRuntime.IsPostgreSql() ? (object)doc.IsExport : (doc.IsExport ? 1 : 0));
        Add(cmd, "$incoterm", Clean(doc.Incoterm));
        Add(cmd, "$pload", Clean(doc.PortOfLoading));
        Add(cmd, "$pdisch", Clean(doc.PortOfDischarge));
        Add(cmd, "$origin", Clean(doc.CountryOfOrigin));
        Add(cmd, "$customs", Clean(doc.CustomsRef));
        Add(cmd, "$vessel", Clean(doc.VesselFlight));
        Add(cmd, "$grossw", doc.GrossWeight);
        Add(cmd, "$netw", doc.NetWeight);
    }

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

    private static string ReadString(DbDataReader reader, string column)
    {
        try
        {
            var ord = reader.GetOrdinal(column);
            return reader.IsDBNull(ord) ? "" : reader.GetString(ord);
        }
        catch (Exception exL725)
        {
            AppHost.Errors.Report(ErrorCodes.InventoryOperationFailed, exL725, "InvRepo.ReadString");
            return "";
        }
    }

    private static decimal ReadDecimal(DbDataReader reader, string column)
    {
        try
        {
            var ord = reader.GetOrdinal(column);
            return reader.IsDBNull(ord) ? 0m : Convert.ToDecimal(reader.GetValue(ord), CultureInfo.InvariantCulture);
        }
        catch (Exception exL738)
        {
            AppHost.Errors.Report(ErrorCodes.InventoryOperationFailed, exL738, "InvRepo.ReadDecimal");
            return 0m;
        }
    }

    private static InvPaymentMethod ParsePaymentMethod(string? value)
    {
        if (string.Equals(value, "Credit", StringComparison.OrdinalIgnoreCase))
            return InvPaymentMethod.Credit;
        if (string.Equals(value, "Checks", StringComparison.OrdinalIgnoreCase))
            return InvPaymentMethod.Checks;
        if (string.Equals(value, "Card", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "K-Net", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "Visa", StringComparison.OrdinalIgnoreCase))
            return InvPaymentMethod.Card;
        return InvPaymentMethod.Cash;
    }

    private static long? ReadLongNullable(DbDataReader reader, string column)
    {
        try
        {
            var ord = reader.GetOrdinal(column);
            return reader.IsDBNull(ord) ? null : Convert.ToInt64(reader.GetValue(ord), CultureInfo.InvariantCulture);
        }
        catch (Exception exL760)
        {
            AppHost.Errors.Report(ErrorCodes.InventoryOperationFailed, exL760, "InvRepo.ReadLongNullable");
            return null;
        }
    }

    /// <summary>W5: nullable decimal reader (export weights), following
    /// the ReadLongNullable fail-soft pattern.</summary>
    private static decimal? ReadDecimalNullable(DbDataReader reader, string column)
    {
        try
        {
            var ord = reader.GetOrdinal(column);
            return reader.IsDBNull(ord) ? null : Convert.ToDecimal(reader.GetValue(ord), CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.InventoryOperationFailed, ex, "InvRepo.ReadDecimalNullable");
            return null;
        }
    }

    private static DateTime? ReadDateNullable(DbDataReader reader, string column)
    {
        try
        {
            var ord = reader.GetOrdinal(column);
            if (reader.IsDBNull(ord)) return null;
            var raw = Convert.ToString(reader.GetValue(ord), CultureInfo.InvariantCulture);
            return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                ? parsed.Date
                : null;
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.InventoryOperationFailed, ex, "InvRepo.ReadDateNullable");
            return null;
        }
    }

    private static void ValidateAndAllocatePurchaseReturnAgainstOriginal(
        DbConnection connection,
        DbTransaction transaction,
        string guidKey,
        InvDoc returnDoc,
        string userId)
    {
        if (returnDoc.OriginalDocId is not long originalDocId || originalDocId <= 0 ||
            returnDoc.OriginalDocKind != InvDocKind.PurchaseInvoice)
            throw new InvalidOperationException("A purchase debit note must reference a posted supplier invoice.");

        var provider = DatabaseRuntime.IsPostgreSql() ? "PostgreSQL" : "SQLite";
        if (!PartyLedger.TableExists(connection, transaction, provider, "purchase_return_line_allocations"))
            throw new InvalidOperationException("Purchase-return lineage schema is missing. Apply database migration 080 before posting a purchase return.");

        var notDeleted = provider == "PostgreSQL" ? "is_deleted = FALSE" : "is_deleted = 0";
        string sourceCompany;
        string originalDocNo;
        long? originalPartyId;
        string originalPartyCode;
        string originalCurrency;
        decimal originalExchangeRate;
        decimal originalDiscountAmount;
        decimal originalExtraAmount;

        // Serialize concurrent returns against the same supplier invoice.
        // PostgreSQL locks the source row in the SELECT below; SQLite takes
        // its write reservation with this no-op update before reading prior
        // allocations, so two partial returns cannot both consume the same
        // remaining quantity.
        if (provider == "SQLite")
        {
            using var sourceLock = connection.CreateCommand();
            sourceLock.Transaction = transaction;
            sourceLock.CommandText = """
                UPDATE purchase_documents
                SET doc_no = doc_no
                WHERE doc_id = $originalId
                  AND (company_id = $company OR company_id = $guid)
                  AND is_deleted = 0
                """;
            Add(sourceLock, "$originalId", originalDocId);
            Add(sourceLock, "$company", Company);
            Add(sourceLock, "$guid", guidKey);
            if (sourceLock.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("The original supplier invoice could not be locked for return processing.");
        }

        using (var original = connection.CreateCommand())
        {
            original.Transaction = transaction;
            original.CommandText = $"""
                SELECT CAST(company_id AS TEXT), doc_no, doc_kind, status, party_id, party_code,
                       currency_code, exchange_rate, discount_amount, extra_amount
                FROM purchase_documents
                WHERE doc_id = $originalId
                  AND (company_id = $company OR company_id = $guid)
                  AND {notDeleted}
                {(provider == "PostgreSQL" ? "FOR UPDATE" : string.Empty)}
                """;
            Add(original, "$originalId", originalDocId);
            Add(original, "$company", Company);
            Add(original, "$guid", guidKey);
            using var reader = original.ExecuteReader();
            if (!reader.Read())
                throw new InvalidOperationException("The original supplier invoice was not found in the current company.");
            sourceCompany = Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture) ?? Company;
            originalDocNo = Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) ?? string.Empty;
            if (!string.Equals(Convert.ToString(reader.GetValue(2), CultureInfo.InvariantCulture), "PUR", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Convert.ToString(reader.GetValue(3), CultureInfo.InvariantCulture), "Posted", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Only a posted supplier invoice can be returned.");
            originalPartyId = reader.IsDBNull(4) ? null : Convert.ToInt64(reader.GetValue(4), CultureInfo.InvariantCulture);
            originalPartyCode = reader.IsDBNull(5) ? string.Empty : Convert.ToString(reader.GetValue(5), CultureInfo.InvariantCulture) ?? string.Empty;
            originalCurrency = reader.IsDBNull(6) ? "SAR" : Convert.ToString(reader.GetValue(6), CultureInfo.InvariantCulture) ?? "SAR";
            originalExchangeRate = reader.IsDBNull(7) ? 1m : Convert.ToDecimal(reader.GetValue(7), CultureInfo.InvariantCulture);
            originalDiscountAmount = reader.IsDBNull(8) ? 0m : Convert.ToDecimal(reader.GetValue(8), CultureInfo.InvariantCulture);
            originalExtraAmount = reader.IsDBNull(9) ? 0m : Convert.ToDecimal(reader.GetValue(9), CultureInfo.InvariantCulture);
        }

        if (string.IsNullOrWhiteSpace(sourceCompany))
            throw new InvalidOperationException("The original supplier invoice has no company key.");
        if (returnDoc.PartyId != originalPartyId ||
            !string.Equals(returnDoc.PartyCode ?? string.Empty, originalPartyCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(returnDoc.CurrencyCode, originalCurrency, StringComparison.OrdinalIgnoreCase) ||
            returnDoc.ExchangeRate != originalExchangeRate)
            throw new InvalidOperationException("The purchase return must use the original supplier and invoice currency.");
        if (!string.IsNullOrWhiteSpace(returnDoc.OriginalDocNo) &&
            !string.Equals(returnDoc.OriginalDocNo.Trim(), originalDocNo, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The original supplier invoice number does not match its document id.");
        if (returnDoc.PaidAmount < 0m || returnDoc.PaidAmount > returnDoc.GrandTotal)
            throw new InvalidOperationException("The supplier refund cannot exceed the purchase debit note total.");

        var sourceLines = new Dictionary<int, (long LineId, long? MaterialId, string Unit, decimal Qty,
            decimal UnitPrice, decimal DiscountPct, string TaxCode, decimal TaxRate, string TaxMode, int? WarehouseId)>();
        using (var lines = connection.CreateCommand())
        {
            lines.Transaction = transaction;
            lines.CommandText = """
                SELECT line_id, line_no, material_id, unit, qty, unit_price, discount_pct,
                       tax_code, tax_rate, tax_calculation_mode, warehouse_id
                FROM purchase_document_lines
                WHERE doc_id = $originalId
                ORDER BY line_no
                """;
            Add(lines, "$originalId", originalDocId);
            using var reader = lines.ExecuteReader();
            while (reader.Read())
            {
                var lineNo = Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);
                sourceLines[lineNo] = (
                    Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                    reader.IsDBNull(2) ? null : Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture),
                    reader.IsDBNull(3) ? string.Empty : Convert.ToString(reader.GetValue(3), CultureInfo.InvariantCulture) ?? string.Empty,
                    Convert.ToDecimal(reader.GetValue(4), CultureInfo.InvariantCulture),
                    Convert.ToDecimal(reader.GetValue(5), CultureInfo.InvariantCulture),
                    Convert.ToDecimal(reader.GetValue(6), CultureInfo.InvariantCulture),
                    reader.IsDBNull(7) ? string.Empty : Convert.ToString(reader.GetValue(7), CultureInfo.InvariantCulture) ?? string.Empty,
                    Convert.ToDecimal(reader.GetValue(8), CultureInfo.InvariantCulture),
                    reader.IsDBNull(9) ? "Exclusive" : Convert.ToString(reader.GetValue(9), CultureInfo.InvariantCulture) ?? "Exclusive",
                    reader.IsDBNull(10) ? null : Convert.ToInt32(reader.GetValue(10), CultureInfo.InvariantCulture));
            }
        }
        if (sourceLines.Count == 0)
            throw new InvalidOperationException("The original supplier invoice has no returnable lines.");
        if (returnDoc.Lines.Count == 0)
            throw new InvalidOperationException("A purchase return requires at least one original invoice line.");
        if (returnDoc.Lines.GroupBy(line => line.LineNo).Any(group => group.Count() > 1))
            throw new InvalidOperationException("A purchase return can contain each original invoice line only once.");

        var previouslyReturned = new Dictionary<long, decimal>();
        using (var returned = connection.CreateCommand())
        {
            returned.Transaction = transaction;
            returned.CommandText = """
                SELECT original_line_id, COALESCE(SUM(returned_qty), 0)
                FROM purchase_return_line_allocations
                WHERE company_id = $allocationCompany AND original_doc_id = $originalId
                GROUP BY original_line_id
                """;
            Add(returned, "$allocationCompany", Company);
            Add(returned, "$originalId", originalDocId);
            using var reader = returned.ExecuteReader();
            while (reader.Read())
                previouslyReturned[Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture)] =
                    Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture);
        }

        var originalLinesSubtotal = sourceLines.Values.Sum(source =>
            CalculateSalesReturnLineSubtotal(source.Qty, source.UnitPrice, source.DiscountPct, source.TaxRate, source.TaxMode));
        var returnLinesSubtotal = returnDoc.Lines.Sum(line =>
            CalculateSalesReturnLineSubtotal(line.Qty, line.UnitPrice, line.DiscountPct, line.TaxRate, line.TaxCalculationMode));
        var expectedDiscount = originalLinesSubtotal == 0m ? 0m :
            decimal.Round(originalDiscountAmount * returnLinesSubtotal / originalLinesSubtotal, 4, MidpointRounding.AwayFromZero);
        var expectedExtra = originalLinesSubtotal == 0m ? 0m :
            decimal.Round(originalExtraAmount * returnLinesSubtotal / originalLinesSubtotal, 4, MidpointRounding.AwayFromZero);
        if (returnDoc.LinesDiscountAmount != expectedDiscount || returnDoc.LinesExtraAmount != expectedExtra)
            throw new InvalidOperationException("The purchase return must allocate the original invoice discount and extra charges proportionally.");

        var allocations = new List<(long ReturnLineId, long OriginalLineId, long MaterialId, decimal Quantity)>();
        foreach (var line in returnDoc.Lines)
        {
            if (!sourceLines.TryGetValue(line.LineNo, out var source))
                throw new InvalidOperationException($"Return line {line.LineNo} does not exist on original supplier invoice '{originalDocNo}'.");
            if (source.MaterialId is not long materialId)
                throw new InvalidOperationException("Purchase return lineage requires a material or service card on every returned invoice line.");
            if (line.MaterialId != source.MaterialId ||
                !string.Equals(line.Unit ?? string.Empty, source.Unit, StringComparison.OrdinalIgnoreCase) ||
                line.UnitPrice != source.UnitPrice ||
                line.DiscountPct != source.DiscountPct ||
                !string.Equals(line.TaxCode ?? string.Empty, source.TaxCode, StringComparison.OrdinalIgnoreCase) ||
                line.TaxRate != source.TaxRate ||
                !string.Equals(line.TaxCalculationMode ?? "Exclusive", source.TaxMode, StringComparison.OrdinalIgnoreCase) ||
                line.WarehouseId != source.WarehouseId)
                throw new InvalidOperationException($"Return line {line.LineNo} must retain the original material, unit, price, discount and tax settings.");
            if (line.Qty <= 0m ||
                previouslyReturned.GetValueOrDefault(source.LineId) + line.Qty > source.Qty)
                throw new InvalidOperationException(
                    $"Return quantity for original line {line.LineNo} exceeds the remaining returnable quantity on supplier invoice '{originalDocNo}'.");

            using var returnLine = connection.CreateCommand();
            returnLine.Transaction = transaction;
            returnLine.CommandText = "SELECT line_id FROM purchase_document_lines WHERE doc_id = $returnId AND line_no = $lineNo";
            Add(returnLine, "$returnId", returnDoc.DocId);
            Add(returnLine, "$lineNo", line.LineNo);
            var returnLineValue = returnLine.ExecuteScalar();
            if (returnLineValue is null or DBNull)
                throw new InvalidOperationException($"Saved purchase return line {line.LineNo} could not be found for lineage allocation.");
            allocations.Add((Convert.ToInt64(returnLineValue, CultureInfo.InvariantCulture),
                source.LineId, materialId, line.Qty));
        }

        foreach (var allocation in allocations)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO purchase_return_line_allocations
                    (allocation_id, company_id, return_doc_id, return_line_id, original_doc_id,
                     original_line_id, material_id, returned_qty, created_by, created_at)
                VALUES
                    ($id, $company, $returnDoc, $returnLine, $originalDoc,
                     $originalLine, $material, $quantity, $actor, $createdAt)
                """;
            Add(insert, "$id", Guid.NewGuid().ToString("D"));
            Add(insert, "$company", Company);
            Add(insert, "$returnDoc", returnDoc.DocId);
            Add(insert, "$returnLine", allocation.ReturnLineId);
            Add(insert, "$originalDoc", originalDocId);
            Add(insert, "$originalLine", allocation.OriginalLineId);
            Add(insert, "$material", allocation.MaterialId);
            Add(insert, "$quantity", allocation.Quantity);
            Add(insert, "$actor", string.IsNullOrWhiteSpace(userId) ? Environment.UserName : userId.Trim());
            Add(insert, "$createdAt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            insert.ExecuteNonQuery();
        }
    }

    private static void ValidateSalesReturnAgainstOriginal(
        DbConnection connection,
        DbTransaction transaction,
        string guidKey,
        InvDoc returnDoc)
    {
        if (returnDoc.OriginalDocId is not long originalDocId || originalDocId <= 0 ||
            returnDoc.OriginalDocKind is not (InvDocKind.SalesInvoice or InvDocKind.PosSale))
            throw new InvalidOperationException("A sales return must reference a posted sales invoice or POS sale.");

        var originalKind = returnDoc.OriginalDocKind.Value;
        var originalKindCode = originalKind switch
        {
            InvDocKind.SalesInvoice => "SAL",
            InvDocKind.PosSale => "POS",
            _ => throw new InvalidOperationException("Unsupported sales-return source document kind.")
        };
        var notDeleted = DatabaseRuntime.IsPostgreSql() ? "is_deleted = FALSE" : "is_deleted = 0";

        string originalDocNo;
        long? originalPartyId;
        string originalPartyCode;
        string originalCurrency;
        decimal originalExchangeRate;
        decimal originalDiscountAmount;
        decimal originalExtraAmount;
        decimal originalPaidAmount;
        using (var original = connection.CreateCommand())
        {
            original.Transaction = transaction;
            original.CommandText = $"""
                SELECT doc_no, doc_kind, status, party_id, party_code, currency_code, exchange_rate, discount_amount, extra_amount, paid_amount
                FROM sales_documents
                WHERE doc_id = $originalId
                  AND (company_id = $company OR company_id = $guid)
                  AND {notDeleted}
                {(DatabaseRuntime.IsPostgreSql() ? "FOR UPDATE" : string.Empty)}
                """;
            Add(original, "$originalId", originalDocId);
            Add(original, "$company", Company);
            Add(original, "$guid", guidKey);
            using var reader = original.ExecuteReader();
            if (!reader.Read())
                throw new InvalidOperationException("The original sales invoice was not found in the current company.");
            originalDocNo = reader.GetString(0);
            if (!string.Equals(reader.GetString(1), originalKindCode, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(reader.GetString(2), "Posted", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Only a posted original sales invoice can be returned.");
            originalPartyId = reader.IsDBNull(3) ? null : Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture);
            originalPartyCode = reader.IsDBNull(4) ? string.Empty : Convert.ToString(reader.GetValue(4), CultureInfo.InvariantCulture) ?? string.Empty;
            originalCurrency = reader.IsDBNull(5) ? "SAR" : Convert.ToString(reader.GetValue(5), CultureInfo.InvariantCulture) ?? "SAR";
            originalExchangeRate = reader.IsDBNull(6) ? 1m : Convert.ToDecimal(reader.GetValue(6), CultureInfo.InvariantCulture);
            originalDiscountAmount = reader.IsDBNull(7) ? 0m : Convert.ToDecimal(reader.GetValue(7), CultureInfo.InvariantCulture);
            originalExtraAmount = reader.IsDBNull(8) ? 0m : Convert.ToDecimal(reader.GetValue(8), CultureInfo.InvariantCulture);
            originalPaidAmount = reader.IsDBNull(9) ? 0m : Convert.ToDecimal(reader.GetValue(9), CultureInfo.InvariantCulture);
        }

        if (returnDoc.PartyId != originalPartyId ||
            !string.Equals(returnDoc.PartyCode ?? string.Empty, originalPartyCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(returnDoc.CurrencyCode, originalCurrency, StringComparison.OrdinalIgnoreCase) ||
            returnDoc.ExchangeRate != originalExchangeRate)
            throw new InvalidOperationException("The sales return must use the original invoice customer and currency.");

        if (!string.IsNullOrWhiteSpace(returnDoc.OriginalDocNo) &&
            !string.Equals(returnDoc.OriginalDocNo.Trim(), originalDocNo, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The original sales invoice number does not match its document id.");

        var sourceLines = new Dictionary<int, (long? MaterialId, string Unit, decimal Qty,
            decimal UnitPrice, decimal DiscountPct, string TaxCode, decimal TaxRate, string TaxMode, int? WarehouseId)>();
        using (var lines = connection.CreateCommand())
        {
            lines.Transaction = transaction;
            lines.CommandText = """
                SELECT line_no, material_id, unit, qty, unit_price, discount_pct,
                       tax_code, tax_rate, tax_calculation_mode, warehouse_id
                FROM sales_document_lines
                WHERE doc_id = $originalId
                ORDER BY line_no
                """;
            Add(lines, "$originalId", originalDocId);
            using var reader = lines.ExecuteReader();
            while (reader.Read())
            {
                var lineNo = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
                sourceLines[lineNo] = (
                    reader.IsDBNull(1) ? null : Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                    reader.IsDBNull(2) ? string.Empty : Convert.ToString(reader.GetValue(2), CultureInfo.InvariantCulture) ?? string.Empty,
                    Convert.ToDecimal(reader.GetValue(3), CultureInfo.InvariantCulture),
                    Convert.ToDecimal(reader.GetValue(4), CultureInfo.InvariantCulture),
                    Convert.ToDecimal(reader.GetValue(5), CultureInfo.InvariantCulture),
                    reader.IsDBNull(6) ? string.Empty : Convert.ToString(reader.GetValue(6), CultureInfo.InvariantCulture) ?? string.Empty,
                    Convert.ToDecimal(reader.GetValue(7), CultureInfo.InvariantCulture),
                    reader.IsDBNull(8) ? "Exclusive" : Convert.ToString(reader.GetValue(8), CultureInfo.InvariantCulture) ?? "Exclusive",
                    reader.IsDBNull(9) ? null : Convert.ToInt32(reader.GetValue(9), CultureInfo.InvariantCulture));
            }
        }

        if (sourceLines.Count == 0)
            throw new InvalidOperationException("The original sales invoice has no returnable lines.");

        var previouslyReturned = new Dictionary<int, decimal>();
        using (var returned = connection.CreateCommand())
        {
            returned.Transaction = transaction;
            returned.CommandText = $"""
                SELECT line.line_no, COALESCE(SUM(line.qty), 0)
                FROM sales_documents header
                INNER JOIN sales_document_lines line ON line.doc_id = header.doc_id
                WHERE (header.company_id = $company OR header.company_id = $guid)
                  AND header.doc_kind = 'SRT'
                  AND header.original_doc_id = $originalId
                  AND header.original_doc_kind = $originalKind
                  AND header.status = 'Posted'
                  AND {notDeleted.Replace("is_deleted", "header.is_deleted", StringComparison.Ordinal)}
                  AND header.doc_id <> $returnId
                GROUP BY line.line_no
                """;
            Add(returned, "$company", Company);
            Add(returned, "$guid", guidKey);
            Add(returned, "$originalId", originalDocId);
            Add(returned, "$originalKind", originalKind.ToString());
            Add(returned, "$returnId", returnDoc.DocId);
            using var reader = returned.ExecuteReader();
            while (reader.Read())
                previouslyReturned[Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture)] =
                    Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture);
        }

        if (returnDoc.Lines.Count == 0)
            throw new InvalidOperationException("A sales return requires at least one original invoice line.");
        if (returnDoc.Lines.GroupBy(line => line.LineNo).Any(group => group.Count() > 1))
            throw new InvalidOperationException("A sales return can contain each original invoice line only once.");

        var originalLinesSubtotal = sourceLines.Values.Sum(source =>
            CalculateSalesReturnLineSubtotal(
                source.Qty, source.UnitPrice, source.DiscountPct, source.TaxRate, source.TaxMode));
        var returnLinesSubtotal = returnDoc.Lines.Sum(line =>
            CalculateSalesReturnLineSubtotal(
                line.Qty, line.UnitPrice, line.DiscountPct, line.TaxRate, line.TaxCalculationMode));
        var expectedDiscount = originalLinesSubtotal == 0m ? 0m :
            decimal.Round(originalDiscountAmount * returnLinesSubtotal / originalLinesSubtotal, 4, MidpointRounding.AwayFromZero);
        var expectedExtra = originalLinesSubtotal == 0m ? 0m :
            decimal.Round(originalExtraAmount * returnLinesSubtotal / originalLinesSubtotal, 4, MidpointRounding.AwayFromZero);
        if (returnDoc.LinesDiscountAmount != expectedDiscount ||
            returnDoc.LinesExtraAmount != expectedExtra)
            throw new InvalidOperationException("The sales return must allocate the original invoice discount and extra charges proportionally.");
        if (returnDoc.PaidAmount < 0m || returnDoc.PaidAmount > returnDoc.GrandTotal)
            throw new InvalidOperationException("The sales return refund cannot exceed its credit note total.");

        // G18 (SAL-7010): a cash refund can only hand back money the
        // customer actually PAID toward the original invoice — real-money
        // allocations (RCV/PAY/INS sources) against it, less refunds
        // already paid out by earlier posted returns. Credit-note
        // allocations (SRT sources) are not cash and never raise the
        // refundable amount. When the invoice carries no allocations at
        // all (legacy rows), its stored paid_amount is the evidence.
        if (returnDoc.PaidAmount > 0m)
        {
            var refundableBase = originalPaidAmount;
            var provider = DatabaseRuntime.IsPostgreSql() ? "PostgreSQL" : "SQLite";
            if (PartyLedger.TableExists(connection, transaction, provider, "payment_allocations")
                && PartyLedger.TableExists(connection, transaction, provider, "party_ledger"))
            {
                using var paidCmd = connection.CreateCommand();
                paidCmd.Transaction = transaction;
                paidCmd.CommandText = """
                    SELECT COUNT(*), COALESCE(SUM(a.amount), 0)
                    FROM payment_allocations a
                    INNER JOIN party_ledger l ON l.ledger_id = a.payment_ledger_id
                    WHERE a.company_id IN ($company, $guid) AND a.doc_table = 'SALES'
                      AND a.doc_id = $originalId AND l.doc_kind IN ('RCV', 'PAY', 'INS')
                    """;
                Add(paidCmd, "$company", Company);
                Add(paidCmd, "$guid", guidKey);
                Add(paidCmd, "$originalId", originalDocId);
                using var paidReader = paidCmd.ExecuteReader();
                if (paidReader.Read() && Convert.ToInt64(paidReader.GetValue(0), CultureInfo.InvariantCulture) > 0)
                    refundableBase = Convert.ToDecimal(paidReader.GetValue(1), CultureInfo.InvariantCulture);
            }

            decimal priorRefunds;
            using (var refunds = connection.CreateCommand())
            {
                refunds.Transaction = transaction;
                refunds.CommandText = $"""
                    SELECT COALESCE(SUM(paid_amount), 0)
                    FROM sales_documents
                    WHERE (company_id = $company OR company_id = $guid)
                      AND doc_kind = 'SRT' AND original_doc_id = $originalId
                      AND status = 'Posted' AND {notDeleted}
                      AND doc_id <> $returnId
                    """;
                Add(refunds, "$company", Company);
                Add(refunds, "$guid", guidKey);
                Add(refunds, "$originalId", originalDocId);
                Add(refunds, "$returnId", returnDoc.DocId);
                priorRefunds = Convert.ToDecimal(refunds.ExecuteScalar(), CultureInfo.InvariantCulture);
            }

            var refundable = Math.Max(0m, refundableBase - priorRefunds);
            var refundCap = Math.Min(returnDoc.PostingTotal, refundable);
            if (returnDoc.PaidAmount > refundCap + 0.005m)
                throw new AppError(
                    ErrorCodes.AllocationExceedsOpenAmount,
                    $"The sales return refund {returnDoc.PaidAmount} exceeds the amount the customer paid toward invoice '{originalDocNo}' that has not already been refunded ({refundable}).",
                    returnDoc.PaidAmount.ToString(CultureInfo.InvariantCulture),
                    refundable.ToString(CultureInfo.InvariantCulture));
        }

        foreach (var line in returnDoc.Lines)
        {
            if (!sourceLines.TryGetValue(line.LineNo, out var source))
                throw new InvalidOperationException($"Return line {line.LineNo} does not exist on original invoice '{originalDocNo}'.");
            if (line.MaterialId != source.MaterialId ||
                !string.Equals(line.Unit ?? string.Empty, source.Unit, StringComparison.OrdinalIgnoreCase) ||
                line.UnitPrice != source.UnitPrice ||
                line.DiscountPct != source.DiscountPct ||
                !string.Equals(line.TaxCode ?? string.Empty, source.TaxCode, StringComparison.OrdinalIgnoreCase) ||
                line.TaxRate != source.TaxRate ||
                !string.Equals(line.TaxCalculationMode ?? "Exclusive", source.TaxMode, StringComparison.OrdinalIgnoreCase) ||
                line.WarehouseId != source.WarehouseId)
                throw new InvalidOperationException($"Return line {line.LineNo} must retain the original material, unit, price, discount and tax settings.");
            if (line.Qty <= 0m)
                throw new InvalidOperationException(
                    $"Return quantity for original line {line.LineNo} must be positive.");
            if (line.ReturnCondition is not ("Good" or "Quarantine" or "Damaged"))
                throw new InvalidOperationException($"Return line {line.LineNo} carries an unknown condition '{line.ReturnCondition}'.");
            // W6 (item 7): the cumulative cap — this return plus every
            // previously posted return against the same source line —
            // raises the cataloged SAL-7021, not a plain message.
            if (previouslyReturned.GetValueOrDefault(line.LineNo) + line.Qty > source.Qty)
                throw new AppError(
                    ErrorCodes.ReturnQuantityExceeded,
                    "The returned quantity exceeds the remaining returnable quantity on the source document line.",
                    line.LineNo.ToString(CultureInfo.InvariantCulture), originalDocNo);
        }
    }

    /// <summary>
    /// W6: shape validation for an authorized no-invoice (walk-in)
    /// return. There is no source document to validate against: the
    /// return must name the party it credits, carry at least one
    /// positive line, and be a pure credit note (no cash refund —
    /// there is no collected payment to hand back, and no invoice for
    /// the Part I settlement machinery to settle). Stock re-enters at
    /// the CURRENT valuation rate (StkPost finds no original document,
    /// so the engine's unlinked-receipt rule applies) and the GL takes
    /// the standard returns path (Dr Sales Returns / Cr Receivable).
    /// The SALES.RETURN.APPROVE authorization itself is enforced at
    /// creation (SrtRepo.CreateNoInvoiceReturn).
    /// </summary>
    private static void ValidateNoInvoiceSalesReturn(InvDoc returnDoc)
    {
        if (returnDoc.OriginalDocId is not null || returnDoc.OriginalDocKind is not null)
            throw new InvalidOperationException("A no-invoice return must not reference a source document; use the invoice return path instead.");
        if (returnDoc.PartyId is null or <= 0)
            throw new InvalidOperationException("A no-invoice return requires the customer it credits.");
        if (returnDoc.PaidAmount != 0m)
            throw new InvalidOperationException("A no-invoice return is a credit note only; it cannot carry a cash refund.");
        if (returnDoc.Lines.Count == 0)
            throw new InvalidOperationException("A no-invoice return requires at least one line.");
        foreach (var line in returnDoc.Lines)
        {
            if (line.Qty <= 0m)
                throw new InvalidOperationException($"Return line {line.LineNo} must have a positive quantity.");
            if (line.ReturnCondition is not ("Good" or "Quarantine" or "Damaged"))
                throw new InvalidOperationException($"Return line {line.LineNo} carries an unknown condition '{line.ReturnCondition}'.");
        }
    }

    /// <summary>
    /// W6: validation for a return sourced from a posted delivery note
    /// that has NOT been invoiced yet. The delivery is the source of
    /// truth for party / currency / lines; cumulative returned
    /// quantity per delivery line is capped exactly like the invoice
    /// path (SAL-7021). A delivery that has since been invoiced is
    /// refused here — its returns belong to the invoice (returning
    /// against the delivery as well would double-relieve 1215).
    /// Delivery returns carry no tax (nothing was billed) and no
    /// header discount/extra allocation.
    /// </summary>
    private static void ValidateDeliverySalesReturn(
        DbConnection connection,
        DbTransaction transaction,
        string guidKey,
        InvDoc returnDoc)
    {
        if (returnDoc.OriginalDocId is not long deliveryId || deliveryId <= 0)
            throw new InvalidOperationException("A delivery-sourced return must reference its source delivery.");
        var notDeleted = DatabaseRuntime.IsPostgreSql() ? "is_deleted = FALSE" : "is_deleted = 0";

        string deliveryDocNo;
        long? deliveryPartyId;
        string deliveryCurrency;
        decimal deliveryExchangeRate;
        using (var delivery = connection.CreateCommand())
        {
            delivery.Transaction = transaction;
            delivery.CommandText = $"""
                SELECT doc_no, status, party_id, currency_code, exchange_rate
                FROM sales_documents
                WHERE doc_id = $deliveryId AND doc_kind = 'DLV'
                  AND (company_id = $company OR company_id = $guid)
                  AND {notDeleted}
                {(DatabaseRuntime.IsPostgreSql() ? "FOR UPDATE" : string.Empty)}
                """;
            Add(delivery, "$deliveryId", deliveryId);
            Add(delivery, "$company", Company);
            Add(delivery, "$guid", guidKey);
            using var reader = delivery.ExecuteReader();
            if (!reader.Read())
                throw new InvalidOperationException("The source delivery was not found in the current company.");
            deliveryDocNo = reader.GetString(0);
            if (!string.Equals(reader.GetString(1), "Posted", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Only a posted delivery can be returned.");
            deliveryPartyId = reader.IsDBNull(2) ? null : Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture);
            deliveryCurrency = reader.IsDBNull(3) ? "SAR" : Convert.ToString(reader.GetValue(3), CultureInfo.InvariantCulture) ?? "SAR";
            deliveryExchangeRate = reader.IsDBNull(4) ? 1m : Convert.ToDecimal(reader.GetValue(4), CultureInfo.InvariantCulture);
        }

        // Invoiced since delivery? A posted SAL whose document_links
        // name this delivery as source means the goods are billed —
        // returns must go against that invoice instead.
        using (var billed = connection.CreateCommand())
        {
            billed.Transaction = transaction;
            billed.CommandText = """
                SELECT COUNT(*)
                FROM document_links link
                INNER JOIN sales_documents invoice
                    ON invoice.doc_id = link.target_doc_id AND invoice.doc_kind = 'SAL'
                WHERE link.source_doc_id = $deliveryId AND link.source_doc_kind = 'DLV'
                  AND link.target_doc_kind = 'SAL' AND invoice.status = 'Posted'
                """;
            Add(billed, "$deliveryId", deliveryId);
            if (Convert.ToInt64(billed.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture) > 0)
                throw new InvalidOperationException(
                    $"Delivery '{deliveryDocNo}' has already been invoiced; return the goods against the invoice instead.");
        }

        if (returnDoc.PartyId != deliveryPartyId ||
            !string.Equals(returnDoc.CurrencyCode, deliveryCurrency, StringComparison.OrdinalIgnoreCase) ||
            returnDoc.ExchangeRate != deliveryExchangeRate)
            throw new InvalidOperationException("The delivery return must use the delivery's customer and currency.");
        if (returnDoc.PaidAmount != 0m)
            throw new InvalidOperationException("A delivery return cannot carry a cash refund; nothing was collected against the delivery.");
        if (returnDoc.PostingTaxAmount != 0m || returnDoc.Lines.Any(line => line.TaxRate != 0m || line.LineTax != 0m))
            throw new InvalidOperationException("A delivery return carries no tax; the delivery was never billed.");
        if (returnDoc.LinesDiscountAmount != 0m || returnDoc.LinesExtraAmount != 0m)
            throw new InvalidOperationException("A delivery return cannot carry header discount or extra charges.");

        var sourceLines = new Dictionary<int, (long? MaterialId, string Unit, decimal Qty, int? WarehouseId)>();
        using (var lines = connection.CreateCommand())
        {
            lines.Transaction = transaction;
            lines.CommandText = """
                SELECT line_no, material_id, unit, qty, warehouse_id
                FROM sales_document_lines
                WHERE doc_id = $deliveryId
                ORDER BY line_no
                """;
            Add(lines, "$deliveryId", deliveryId);
            using var reader = lines.ExecuteReader();
            while (reader.Read())
            {
                sourceLines[Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture)] = (
                    reader.IsDBNull(1) ? null : Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                    reader.IsDBNull(2) ? string.Empty : Convert.ToString(reader.GetValue(2), CultureInfo.InvariantCulture) ?? string.Empty,
                    Convert.ToDecimal(reader.GetValue(3), CultureInfo.InvariantCulture),
                    reader.IsDBNull(4) ? null : Convert.ToInt32(reader.GetValue(4), CultureInfo.InvariantCulture));
            }
        }
        if (sourceLines.Count == 0)
            throw new InvalidOperationException("The source delivery has no returnable lines.");

        var previouslyReturned = new Dictionary<int, decimal>();
        using (var returned = connection.CreateCommand())
        {
            returned.Transaction = transaction;
            returned.CommandText = $"""
                SELECT line.line_no, COALESCE(SUM(line.qty), 0)
                FROM sales_documents header
                INNER JOIN sales_document_lines line ON line.doc_id = header.doc_id
                WHERE (header.company_id = $company OR header.company_id = $guid)
                  AND header.doc_kind = 'SRT'
                  AND header.return_source_kind = 'DLV'
                  AND header.original_doc_id = $deliveryId
                  AND header.status = 'Posted'
                  AND {notDeleted.Replace("is_deleted", "header.is_deleted", StringComparison.Ordinal)}
                  AND header.doc_id <> $returnId
                GROUP BY line.line_no
                """;
            Add(returned, "$company", Company);
            Add(returned, "$guid", guidKey);
            Add(returned, "$deliveryId", deliveryId);
            Add(returned, "$returnId", returnDoc.DocId);
            using var reader = returned.ExecuteReader();
            while (reader.Read())
                previouslyReturned[Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture)] =
                    Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture);
        }

        if (returnDoc.Lines.Count == 0)
            throw new InvalidOperationException("A delivery return requires at least one delivery line.");
        if (returnDoc.Lines.GroupBy(line => line.LineNo).Any(group => group.Count() > 1))
            throw new InvalidOperationException("A delivery return can contain each delivery line only once.");
        foreach (var line in returnDoc.Lines)
        {
            if (!sourceLines.TryGetValue(line.LineNo, out var source))
                throw new InvalidOperationException($"Return line {line.LineNo} does not exist on delivery '{deliveryDocNo}'.");
            if (line.MaterialId != source.MaterialId ||
                !string.Equals(line.Unit ?? string.Empty, source.Unit, StringComparison.OrdinalIgnoreCase) ||
                line.WarehouseId != source.WarehouseId)
                throw new InvalidOperationException($"Return line {line.LineNo} must retain the original material, unit and warehouse of delivery '{deliveryDocNo}'.");
            if (line.Qty <= 0m)
                throw new InvalidOperationException($"Return quantity for delivery line {line.LineNo} must be positive.");
            if (line.ReturnCondition is not ("Good" or "Quarantine" or "Damaged"))
                throw new InvalidOperationException($"Return line {line.LineNo} carries an unknown condition '{line.ReturnCondition}'.");
            if (previouslyReturned.GetValueOrDefault(line.LineNo) + line.Qty > source.Qty)
                throw new AppError(
                    ErrorCodes.ReturnQuantityExceeded,
                    "The returned quantity exceeds the remaining returnable quantity on the source document line.",
                    line.LineNo.ToString(CultureInfo.InvariantCulture), deliveryDocNo);
        }
    }

    /// <summary>
    /// W6 posting gates for a sales return (skipped for POS-context
    /// returns and generated full reversals by the caller):
    /// inspection — a return with any Quarantine / Damaged line, or at
    /// / over the company amount threshold, must carry a recorded
    /// inspection decision (SAL-7022); approval — when the company
    /// flag (srt_return_settings.approval_required, shared-flag note:
    /// W23's parallel settings work must reuse this same flag rather
    /// than seed a second one) is ON, the return must be Approved by
    /// a SALES.RETURN.APPROVE holder before posting.
    /// </summary>
    private static void EnforceSalesReturnGates(DbConnection connection, DbTransaction tx, InvDoc doc)
    {
        var settings = SrtRepo.ReadSettings(connection, tx);
        var needsInspection = doc.Lines.Any(line => line.ReturnCondition is "Quarantine" or "Damaged")
            || (settings.InspectionAmountThreshold is decimal threshold && doc.PostingTotal >= threshold);
        if (needsInspection && !string.Equals(doc.InspectionStatus, "Inspected", StringComparison.OrdinalIgnoreCase))
            throw new AppError(
                ErrorCodes.ReturnInspectionRequired,
                "This return requires an inspection decision before it can be posted.",
                doc.DocNo);
        if (settings.ApprovalRequired && !string.Equals(doc.ApprovalStatus, "Approved", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Sales return '{doc.DocNo}' must be approved by a holder of {PermCat.Sales.ReturnApprove} before posting (current approval state: '{doc.ApprovalStatus}').");
    }

    private static decimal CalculateSalesReturnLineSubtotal(
        decimal quantity,
        decimal unitPrice,
        decimal discountPct,
        decimal taxRate,
        string taxCalculationMode)
    {
        var gross = quantity * unitPrice;
        var discounted = decimal.Round(
            gross - gross * (discountPct / 100m),
            4,
            MidpointRounding.AwayFromZero);
        if (taxRate <= 0m ||
            !string.Equals(taxCalculationMode, "Inclusive", StringComparison.OrdinalIgnoreCase))
            return discounted;

        return decimal.Round(
            discounted / (1m + taxRate / 100m),
            4,
            MidpointRounding.AwayFromZero);
    }

    public static bool IsDeliveryLinkedSalesInvoice(long invoiceDocId)
    {
        var connectionString = DatabaseRuntime.GetConnectionString()
            ?? throw new InvalidOperationException("No database connection.");
        using var connection = Open(connectionString);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(1)
            FROM document_links
            WHERE target_doc_id = $invoiceId
              AND target_doc_kind = 'SAL'
              AND source_doc_kind = 'DLV';
            """;
        Add(command, "$invoiceId", invoiceDocId);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    private static InvDocKind? ParseDocKind(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return Enum.TryParse<InvDocKind>(value, ignoreCase: true, out var kind) ? kind : null;
    }

    /// <summary>Reads the valid_till column (migration 032, TEXT date)
    /// selected by Load. A missing/unparseable value is reported through
    /// the error catalog like the other tolerant readers and loads as
    /// null rather than failing the whole document open.</summary>
    private static DateTime? ParseValidTill(DbDataReader reader)
    {
        try
        {
            var ord = reader.GetOrdinal("valid_till");
            if (reader.IsDBNull(ord)) return null;
            var value = reader.GetValue(ord);
            return value switch
            {
                DateTime dt => dt,
                DateOnly d => d.ToDateTime(TimeOnly.MinValue),
                _ => DateTime.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture),
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                    ? parsed
                    : null
            };
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.InventoryOperationFailed, ex, "InvRepo.ParseValidTill");
            return null;
        }
    }
}

internal static class InvDocKindExt
{
    public static bool IsSales(this InvDocKind kind)
        => kind is InvDocKind.SalesInvoice or InvDocKind.SalesReturn or InvDocKind.PosSale or InvDocKind.Quotation;
}

/// <summary>
/// The kind → effects policy for the invoice posting path (Fix Phase
/// Step 5, sweep I-2). ONE definition, enforced inside
/// <see cref="InvRepo.Post"/> at the repository layer, so no caller can
/// post a kind into effects it must not have. The full matrix is in
/// docs/FIX_PHASE_STEP5.md §3.
///
/// - SalesInvoice / PosSale: GL + party ledger (customer debit) + stock
///   out + settlement of money collected at posting + credit guard.
/// - SalesReturn: GL + party ledger (customer credit) + stock in. A
///   return is not a payment: no settlement, and it never blocks on the
///   credit guard (it reduces exposure).
/// - PurchaseInvoice: GL + party ledger (supplier credit) + stock in +
///   settlement + credit guard (supplier exposure, as before Step 5).
/// - PurchaseReturn: mirror of SalesReturn on the supplier side.
/// - StockIn / StockOut: GL (Step 3 mapping) + stock. NO party ledger —
///   a stock document is not a receivable/payable, whatever party text
///   was typed on it (before Step 5 these credited a supplier).
/// - OpeningStock: stock in only (valuation event, Step 6 owns its GL).
/// - Quotation: NO effects at all. Posting a quotation only advances
///   its status; it must never create a receivable, a GL sales entry,
///   a stock deduction or a settlement (sweep I-2). Quotations/orders
///   of the commercial chain (CommercialDocKinds QUO/SO/PO/…) do not
///   flow through InvRepo at all — CommercialDocumentRepository already
///   guards them the same way (QUO/SO post with no side effects).
/// - StockTransfer / InventoryCount: NO generic effects — Post routes
///   them to their dedicated engines BEFORE the generic flow (Step 9,
///   closing Step 5 flag F-1): StkPost.PostTransfer (two-sided,
///   value-conserving) and StkPost.PostCount (delta-based), the same
///   engines the warehouse windows use. Step 5 blocked them here with
///   ACC-5014 because the generic flow fabricated stock for both (a
///   transfer created qty-in from nothing; a count added the full
///   counted quantity instead of the delta, I-6/I-7); with the route
///   in place the block is gone and the kinds simply have no
///   GL / party-ledger / generic-stock effect of their own.
/// </summary>
/// <summary>
/// The POS context of one posting (Build POS 066): the session the
/// document belongs to (null = an explicitly enabled session-less
/// sale) and the tenders that settle it. Sales carry positive applied
/// amounts summing to the bill total; returns carry NEGATIVE refund
/// amounts summing to the return total. Supplied by PosPosting — the
/// till path — and enforced by InvRepo.Post inside its transaction.
/// </summary>
public sealed record PosPostingContext(
    Guid? SessionId,
    IReadOnlyList<ShouTech.Domain.Entities.PosTender> Tenders,
    // W9 exchange: the part of the sale's on-account tender that is a
    // consumed return credit (the exchange return's own credit leg is
    // its offset), not party credit — see PosTenderMath.
    decimal ExchangeCredit = 0m);

public static class InvPostingPolicy
{
    public sealed record Effects(
        bool Gl, bool PartyLedger, bool Stock, bool Settlement, bool CreditGuard, bool Blocked);

    private static readonly Effects None = new(false, false, false, false, false, false);
    private static readonly Effects BlockedHere = new(false, false, false, false, false, true);

    public static Effects For(InvDocKind kind) => kind switch
    {
        InvDocKind.SalesInvoice => new Effects(true, true, true, true, true, false),
        InvDocKind.PosSale => new Effects(true, true, true, true, true, false),
        InvDocKind.SalesReturn => new Effects(true, true, true, false, false, false),
        InvDocKind.PurchaseInvoice => new Effects(true, true, true, true, true, false),
        InvDocKind.PurchaseReturn => new Effects(true, true, true, false, false, false),
        InvDocKind.StockIn => new Effects(true, false, true, false, false, false),
        InvDocKind.StockOut => new Effects(true, false, true, false, false, false),
        InvDocKind.OpeningStock => new Effects(false, false, true, false, false, false),
        InvDocKind.Quotation => None,
        // Step 9: routed to the dedicated engines at the top of
        // InvRepo.Post — no generic effects, and NOT blocked.
        InvDocKind.StockTransfer => None,
        InvDocKind.InventoryCount => None,
        _ => BlockedHere
    };
}
