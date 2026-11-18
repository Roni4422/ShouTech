using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.App.Reporting.Invoices;
using ShouTech.App.Views;

using ShouTech.Domain;
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
        var year = DateTime.Today.Year.ToString(CultureInfo.InvariantCulture);
        var cs = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(cs)) return $"{prefix}-{year}-0001";

        try
        {
            using var connection = Open(cs);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"""
                SELECT COUNT(1) FROM {table}
                WHERE company_id = $company AND doc_kind = $kind
                  AND doc_no LIKE $pattern;
                """;
            Add(cmd, "$company", Company);
            Add(cmd, "$kind", prefix);
            Add(cmd, "$pattern", $"{prefix}-{year}-%");
            var count = Convert.ToInt32(cmd.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
            return $"{prefix}-{year}-{(count + 1).ToString("D4", CultureInfo.InvariantCulture)}";
        }
        catch (Exception exL58)
        {
            AppHost.Errors.Report(ErrorCodes.InventoryOperationFailed, exL58, "InvRepo.NextDocNo");
            return $"{prefix}-{year}-0001";
        }
    }

    public static long SaveDraft(InvDoc doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (doc.Status == InvDocStatus.Posted)
            throw new InvalidOperationException("Posted documents cannot be overwritten. Use a return document.");

        DocSchema.Ensure();
        doc.RenumberLines();
        doc.RecalcLineTaxesFromDefault();

        var cs = DatabaseRuntime.GetConnectionString()
            ?? throw new InvalidOperationException("No database connection.");
        using var connection = Open(cs);
        using var tx = connection.BeginTransaction();

        var isSales = doc.IsSalesSide;
        var headerTable = isSales ? "sales_documents" : "purchase_documents";
        var lineTable = isSales ? "sales_document_lines" : "purchase_document_lines";

        long docId = doc.DocId;
        if (docId <= 0)
        {
            if (string.IsNullOrWhiteSpace(doc.DocNo))
                doc.DocNo = NextDocNo(doc.Kind);

            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = $"""
                    INSERT INTO {headerTable}
                    (company_id, doc_kind, doc_no, doc_date, party_id, party_code, party_name, warehouse_id,
                     warehouse_name, currency_code, exchange_rate, subtotal, discount_amount, extra_amount,
                     tax_amount, grand_total, paid_amount, status, tax_code, tax_rate, notes,
                     payment_method, receipt_no, reference_no, branch_name, sales_rep_name, cost_center,
                     party_account_name, goods_account_name,
                     original_doc_id, original_doc_kind, original_doc_no, journal_doc_no,
                     target_warehouse_id, target_warehouse_name,
                     created_by, is_deleted)
                    VALUES
                    ($company, $kind, $no, $date, $party, $pcode, $pname, $wh,
                     $whname, $cur, $fx, $sub, $disc, $extra,
                     $tax, $grand, $paid, 'Draft', $tcode, $trate, $notes,
                     $pay, $receipt, $ref, $branch, $rep, $cc,
                     $pacc, $gacc,
                     $origId, $origKind, $origNo, $jrn,
                     $twh, $twhname,
                     $user, 0);
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
                         target_warehouse_id, target_warehouse_name,
                         created_by, is_deleted)
                        VALUES
                        ($company, $kind, $no, $date, $party, $pcode, $pname, $wh,
                         $whname, $cur, $fx, $sub, $disc, $extra,
                         $tax, $grand, $paid, 'Draft', $tcode, $trate, $notes,
                         $pay, $receipt, $ref, $branch, $rep, $cc,
                         $pacc, $gacc,
                         $origId, $origKind, $origNo, $jrn,
                         $twh, $twhname,
                         $user, 0)
                        RETURNING doc_id;
                        """;
                }
                BindHeader(cmd, doc);
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
                      payment_method=$pay, receipt_no=$receipt, reference_no=$ref, branch_name=$branch,
                      sales_rep_name=$rep, cost_center=$cc, party_account_name=$pacc, goods_account_name=$gacc,
                      original_doc_id=$origId, original_doc_kind=$origKind, original_doc_no=$origNo, journal_doc_no=$jrn,
                      target_warehouse_id=$twh, target_warehouse_name=$twhname
                    WHERE doc_id=$id AND (company_id=$company OR company_id=$guid) AND status='Draft' AND is_deleted=0;
                    """;
                BindHeader(cmd, doc);
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

        foreach (var line in doc.Lines)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"""
                INSERT INTO {lineTable}
                (doc_id, line_no, material_id, material_code, description, unit, qty, unit_price,
                 discount_pct, tax_rate, line_subtotal, line_tax, line_total, warehouse_id, warehouse_name)
                VALUES
                ($doc, $no, $mid, $mcode, $desc, $unit, $qty, $price,
                 $disc, $trate, $sub, $ltax, $ltot, $wh, $whname);
                """;
            Add(cmd, "$doc", docId);
            Add(cmd, "$no", line.LineNo);
            Add(cmd, "$mid", line.MaterialId);
            Add(cmd, "$mcode", line.MaterialCode);
            Add(cmd, "$desc", line.Description);
            Add(cmd, "$unit", line.Unit);
            Add(cmd, "$qty", line.Qty);
            Add(cmd, "$price", line.UnitPrice);
            Add(cmd, "$disc", line.DiscountPct);
            Add(cmd, "$trate", line.TaxRate);
            Add(cmd, "$sub", line.LineSubtotal);
            Add(cmd, "$ltax", line.LineTax);
            Add(cmd, "$ltot", line.LineTotal);
            Add(cmd, "$wh", line.WarehouseId ?? doc.WarehouseId);
            Add(cmd, "$whname", string.IsNullOrWhiteSpace(line.WarehouseName) ? doc.WarehouseName : line.WarehouseName);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
        doc.DocId = docId;
        doc.Status = InvDocStatus.Draft;
        return docId;
    }

    public static void Post(long docId, InvDocKind kind, string userId,
        IReadOnlyDictionary<int, LineBatchSerialCapture>? batchSerialByLine = null,
        PosPostingContext? pos = null)
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
        using var connection = Open(cs);
        using var tx = connection.BeginTransaction();
        // Step 9: dual company key (code or Guid), as in Load.
        var guidKey = ResolveCompanyGuidOrNull(connection, tx)?.ToString("D") ?? Company;

        InvDoc? doc;
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"""
                SELECT doc_id, doc_kind, doc_no, doc_date, party_id, party_name, warehouse_id,
                       currency_code, exchange_rate, subtotal, tax_amount, grand_total, status, tax_rate, party_code,
                       discount_amount, extra_amount,
                       paid_amount, payment_method, cost_center
                FROM {headerTable}
                WHERE doc_id=$id AND (company_id=$company OR company_id=$guid) AND is_deleted=0;
                """;
            Add(cmd, "$id", docId);
            Add(cmd, "$company", Company);
            Add(cmd, "$guid", guidKey);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                throw new InvalidOperationException("Document not found.");
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
                       discount_pct, tax_rate, warehouse_id
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
                    TaxRate = Convert.ToDecimal(lr.GetValue(8), CultureInfo.InvariantCulture),
                    WarehouseId = lr.IsDBNull(9) ? null : Convert.ToInt32(lr.GetValue(9), CultureInfo.InvariantCulture),
                });
            }
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
        var glLines = effects.Gl ? BuildGlLines(connection, tx, doc, fx.Rate) : new List<ShouTech.Domain.Entities.GlLineRequest>();
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
                    doc.ExchangeRate <= 0 ? 1m : doc.ExchangeRate,
                    glLines),
                userId);
            journalNo = glResult.DocumentNumber;
        }
        doc.JournalDocNo = journalNo;

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

        // Party ledger: sales increase receivable (debit customer),
        // purchases increase payable — ONLY for kinds whose policy carries
        // a party-ledger effect (Step 5). Before this gate the block ran
        // for ANY kind with a PartyId: a QUOTATION created a real
        // receivable and stock documents credited a supplier (sweep I-2).
        // The amount is the STORED grand total (doc.PostingTotal) — the
        // same figure the GL above and the credit guard below consume.
        if (effects.PartyLedger && doc.PartyId is long partyId)
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
            PartyCreditGuard.EnsureWithinLimit(
                connection, tx,
                DatabaseRuntime.IsPostgreSql() ? "PostgreSQL" : "SQLite",
                Company, partyKind, partyId,
                string.IsNullOrWhiteSpace(doc.PartyCode) ? null : doc.PartyCode,
                FxRates.ToBase(exposureIncrease, fx.Rate), "$");

            // Step 7: the ledger row carries its base amounts alongside
            // the transaction amounts (migration 062), at the same
            // effective rate the GL used.
            var ledgerHasBase = PartyLedger.ColumnExists(connection, tx, fxProvider, "party_ledger", "base_debit");
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
            Add(led, "$debit", debit);
            Add(led, "$credit", credit);
            Add(led, "$cur", doc.CurrencyCode);
            Add(led, "$note", "Auto post");
            if (ledgerHasBase)
            {
                Add(led, "$baseDebit", FxRates.ToBase(debit, fx.Rate));
                Add(led, "$baseCredit", FxRates.ToBase(credit, fx.Rate));
                Add(led, "$rate", fx.Rate);
            }
            led.ExecuteNonQuery();

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
                var settled = Math.Min(doc.PaidAmount, doc.PostingTotal);
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

        // (Inventory effects were applied before the GL block above —
        // Step 6 ordering, so COGS reads the engine's movement costs.)

        tx.Commit();
    }

    // ---------------------------------------------------------------
    // Build POS 066: session / tender gate + tender writes
    // ---------------------------------------------------------------

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
            ShouTech.Domain.Entities.PosTenderMath.ValidateSaleTenders(doc.PostingTotal, pos.Tenders, doc.PartyId is not null);
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

        ClaimDedicatedPost(docId, kind, userId);
        try
        {
            string engineDocNo;
            if (kind == InvDocKind.StockTransfer)
            {
                engineDocNo = PostTransferForDocument(doc);
            }
            else
            {
                PostCountForDocument(doc);
                engineDocNo = string.Empty;
            }
            if (!string.IsNullOrEmpty(engineDocNo))
                LinkEngineDocumentNo(docId, kind, engineDocNo);
        }
        catch (Exception engineFailure)
        {
            try
            {
                RevertDedicatedClaim(docId, kind);
            }
            catch (Exception revertFailure)
            {
                throw new InvalidOperationException(
                    $"Posting document '{doc.DocNo}' failed ({engineFailure.Message}) and its status could not be reverted to Draft; the document may read Posted without stock effects. Reconcile it manually before retrying.",
                    revertFailure);
            }
            throw;
        }
    }

    private static string PostTransferForDocument(InvDoc doc)
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

        return StkPost.PostTransfer(sourceId, targetId, doc.DocDate, reason, lines);
    }

    private static void PostCountForDocument(InvDoc doc)
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

        StkPost.PostCount(warehouseId, doc.DocDate, reason, lines);
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

    private static void ClaimDedicatedPost(long docId, InvDocKind kind, string userId)
        => SetDedicatedStatus(docId, kind, from: "Draft", to: "Posted", userId,
            "Post failed — document may already be posted.");

    private static void RevertDedicatedClaim(long docId, InvDocKind kind)
        => SetDedicatedStatus(docId, kind, from: "Posted", to: "Draft", userId: null,
            "The document status could not be reverted to Draft.");

    private static void SetDedicatedStatus(long docId, InvDocKind kind, string from, string to, string? userId, string failureMessage)
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
               SET status=$to, posted_by=$user, posted_at=NOW()
               WHERE doc_id=$id AND (company_id=$company OR company_id=$guid) AND status=$from;
               """
            : $"""
               UPDATE {headerTable}
               SET status=$to, posted_by=$user, posted_at=datetime('now')
               WHERE doc_id=$id AND (company_id=$company OR company_id=$guid) AND status=$from;
               """;
        Add(cmd, "$to", to);
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
                       target_warehouse_id, target_warehouse_name
                FROM {headerTable}
                WHERE doc_id=$id AND (company_id=$company OR company_id=$guid) AND is_deleted=0;
                """;
            Add(cmd, "$id", docId);
            Add(cmd, "$company", Company);
            Add(cmd, "$guid", guidKey);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
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
                Status = string.Equals(reader.GetString(reader.GetOrdinal("status")), "Posted", StringComparison.OrdinalIgnoreCase)
                    ? InvDocStatus.Posted : InvDocStatus.Draft,
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
            };
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT line_no, material_id, material_code, description, unit, qty, unit_price,
                       discount_pct, tax_rate, warehouse_id, warehouse_name
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
                    TaxRate = Convert.ToDecimal(reader.GetValue(8), CultureInfo.InvariantCulture),
                    WarehouseId = reader.IsDBNull(9) ? null : Convert.ToInt32(reader.GetValue(9), CultureInfo.InvariantCulture),
                    WarehouseName = reader.FieldCount > 10 && !reader.IsDBNull(10)
                        ? Convert.ToString(reader.GetValue(10), CultureInfo.InvariantCulture) ?? ""
                        : "",
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
                    TaxRate = line.TaxRate,
                    WarehouseId = line.WarehouseId,
                    WarehouseName = line.WarehouseName
                }).ToList()
            };

            SaveDraft(reversal);
            reversalDraftId = reversal.DocId;

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
                find.CommandText = """
                    SELECT debit, credit, currency_code
                    """ + (hasBase ? ", base_debit, base_credit, exchange_rate" : "") + """
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
            using var del = connection.CreateCommand();
            del.Transaction = tx;
            del.CommandText = """
                DELETE FROM payment_allocations
                WHERE company_id=$company AND doc_id=$orig AND doc_table IN ('SALES', 'PURCHASE');
                """;
            Add(del, "$company", Company);
            Add(del, "$orig", original.DocId);
            del.ExecuteNonQuery();
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

    private static List<ShouTech.Domain.Entities.GlLineRequest> BuildGlLines(DbConnection connection, DbTransaction tx, InvDoc doc, decimal effectiveRate = 1m)
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

        // Optional tax leg: only when a postable VAT account resolves.
        var taxAccountCode = doc.Kind.IsSales() || doc.Kind == InvDocKind.PosSale
            ? GlAccounts.OutputVat : GlAccounts.InputVat;
        var materialTaxAccount = infos.Select(i => i.TaxAccount).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
        if (!string.IsNullOrWhiteSpace(materialTaxAccount)) taxAccountCode = materialTaxAccount.Trim();
        var companyGuid = ResolveCompanyGuid(connection, tx);
        var splitTax = tax > 0 && GlPosting.TryResolveAccount(connection, tx, provider, companyGuid, taxAccountCode) is not null;
        var taxLeg = splitTax ? tax : 0m;

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
                .Select(l => decimal.Round(l.LineSubtotal + (splitTax ? 0m : l.LineTax), 4, MidpointRounding.AwayFromZero))
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
                    cost = reverse ? moved : -moved;
                }
                else if (info.MaterialId is long midOnly
                         && linesPerMaterial.GetValueOrDefault(midOnly) == 1
                         && byMaterial.TryGetValue(midOnly, out var movedTotal))
                {
                    cost = reverse ? movedTotal : -movedTotal;
                }
                else if (info.AvgCost > 0)
                {
                    cost = decimal.Round(line.Qty * info.AvgCost, 4, MidpointRounding.AwayFromZero);
                }
                else continue;
                if (cost <= 0) continue;
                // Step 7: the engine's cost_amount is denominated in
                // BASE currency (stock values are base-currency, Step 6),
                // while GlLineRequest amounts are TRANSACTION currency —
                // GlPosting converts every line back to base at the
                // document's effective rate. Dividing the base cost by
                // the rate here makes the COGS leg's base amount land
                // EXACTLY on the engine's cost (rate 1 documents are
                // unchanged), instead of inflating COGS by the rate.
                if (effectiveRate != 1m)
                {
                    cost = decimal.Round(cost / effectiveRate, 4, MidpointRounding.AwayFromZero);
                    if (cost <= 0) continue;
                }
                var cogs = CodeOr(info.AccCogs, GlAccounts.Cogs);
                var inventory = CodeOr(info.AccInventory, GlAccounts.Inventory);
                if (reverse) { Add(inventory, cost, 0); Add(cogs, 0, cost); }
                else { Add(cogs, cost, 0); Add(inventory, 0, cost); }
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
                if (splitTax) Add(taxAccountCode, 0, taxLeg);
                AddCogs(reverse: false);
                break;
            }
            case InvDocKind.SalesReturn:
            {
                for (var i = 0; i < doc.Lines.Count; i++)
                    Add(CodeOr(infos[i].AccSalesReturn, GlAccounts.SalesReturns), leg[i], 0, cc);
                if (splitTax) Add(taxAccountCode, taxLeg, 0);
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
                if (splitTax) Add(taxAccountCode, taxLeg, 0);
                var paid = Math.Min(Math.Max(doc.PaidAmount, 0m), grand);
                Add(settleAccount, 0, paid);
                Add(GlAccounts.Payable, 0, grand - paid);
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
                for (var i = 0; i < doc.Lines.Count; i++)
                    Add(CodeOr(infos[i].AccPurchaseReturn, GlAccounts.PurchaseReturns), 0, leg[i], cc);
                if (splitTax) Add(taxAccountCode, 0, taxLeg);
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
        Add(cmd, "$user", AppState.CurrentUser?.Username ?? Environment.UserName);
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

    private static InvDocKind? ParseDocKind(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return Enum.TryParse<InvDocKind>(value, ignoreCase: true, out var kind) ? kind : null;
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
    IReadOnlyList<ShouTech.Domain.Entities.PosTender> Tenders);

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
