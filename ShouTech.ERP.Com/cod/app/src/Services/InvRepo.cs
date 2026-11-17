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
                     created_by, is_deleted)
                    VALUES
                    ($company, $kind, $no, $date, $party, $pcode, $pname, $wh,
                     $whname, $cur, $fx, $sub, $disc, $extra,
                     $tax, $grand, $paid, 'Draft', $tcode, $trate, $notes,
                     $pay, $receipt, $ref, $branch, $rep, $cc,
                     $pacc, $gacc,
                     $origId, $origKind, $origNo, $jrn,
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
                         created_by, is_deleted)
                        VALUES
                        ($company, $kind, $no, $date, $party, $pcode, $pname, $wh,
                         $whname, $cur, $fx, $sub, $disc, $extra,
                         $tax, $grand, $paid, 'Draft', $tcode, $trate, $notes,
                         $pay, $receipt, $ref, $branch, $rep, $cc,
                         $pacc, $gacc,
                         $origId, $origKind, $origNo, $jrn,
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
                      original_doc_id=$origId, original_doc_kind=$origKind, original_doc_no=$origNo, journal_doc_no=$jrn
                    WHERE doc_id=$id AND company_id=$company AND status='Draft' AND is_deleted=0;
                    """;
                BindHeader(cmd, doc);
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

    public static void Post(long docId, InvDocKind kind, string userId)
    {
        DocSchema.Ensure();
        MatRepo.EnsureSchema();
        var isSales = kind.IsSales();
        var headerTable = isSales ? "sales_documents" : "purchase_documents";
        var cs = DatabaseRuntime.GetConnectionString()
            ?? throw new InvalidOperationException("No database connection.");
        using var connection = Open(cs);
        using var tx = connection.BeginTransaction();

        InvDoc? doc;
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"""
                SELECT doc_id, doc_kind, doc_no, doc_date, party_id, party_name, warehouse_id,
                       currency_code, exchange_rate, subtotal, tax_amount, grand_total, status, tax_rate, party_code,
                       paid_amount, payment_method, cost_center
                FROM {headerTable}
                WHERE doc_id=$id AND company_id=$company AND is_deleted=0;
                """;
            Add(cmd, "$id", docId);
            Add(cmd, "$company", Company);
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
            };
        }

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

        // Real GL posting (Step 3, J-2): build the document's journal lines
        // from the totals the document layer hands us today (Step 5 owns
        // the stored-total / header-discount correctness of those totals)
        // and post them INSIDE this transaction. The journal number that
        // used to be fabricated as "JV-" + DocNo is now the REAL entry's
        // document number, returned by the posting engine.
        var glLines = BuildGlLines(connection, tx, doc);
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
                WHERE doc_id=$id AND company_id=$company AND status='Draft';
                """;
            if (DatabaseRuntime.IsPostgreSql())
                upd.CommandText = $"""
                    UPDATE {headerTable}
                    SET status='Posted', posted_by=$user, posted_at=NOW(), journal_doc_no=$jrn
                    WHERE doc_id=$id AND company_id=$company AND status='Draft';
                    """;
            Add(upd, "$user", userId);
            Add(upd, "$id", docId);
            Add(upd, "$company", Company);
            Add(upd, "$jrn", journalNo);
            if (upd.ExecuteNonQuery() == 0)
                throw new InvalidOperationException("Post failed — document may already be posted.");
        }

        // Party ledger: sales increase receivable (debit customer), purchases increase payable.
        if (doc.PartyId is long partyId)
        {
            var partyKind = isSales ? "C" : "S";
            decimal debit = 0, credit = 0;
            if (isSales)
            {
                if (kind == InvDocKind.SalesReturn) credit = doc.GrandTotal;
                else debit = doc.GrandTotal;
            }
            else
            {
                if (kind == InvDocKind.PurchaseReturn) debit = doc.GrandTotal;
                else credit = doc.GrandTotal;
            }

            // Credit-limit gate (ACC-5006): only a movement that increases
            // the party's exposure is checked (customer debit / supplier
            // credit). Returns reduce the balance and never block. A
            // violation aborts the whole posting transaction.
            var exposureIncrease = partyKind == "C" ? debit : credit;
            PartyCreditGuard.EnsureWithinLimit(
                connection, tx,
                DatabaseRuntime.IsPostgreSql() ? "PostgreSQL" : "SQLite",
                Company, partyKind, partyId,
                string.IsNullOrWhiteSpace(doc.PartyCode) ? null : doc.PartyCode,
                exposureIncrease, "$");

            using var led = connection.CreateCommand();
            led.Transaction = tx;
            led.CommandText = """
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
            if (doc.PaidAmount > 0m && kind is InvDocKind.SalesInvoice or InvDocKind.PurchaseInvoice or InvDocKind.PosSale)
            {
                var settled = Math.Min(doc.PaidAmount, doc.GrandTotal);
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
                        "Collected at document posting");

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

        // Inventory effects (same transaction as ledger + status).
        StkPost.ApplyDocument(connection, tx, doc);

        tx.Commit();
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
        InvDoc doc;
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT doc_id, doc_kind, doc_no, doc_date, party_id, party_code, party_name, warehouse_id,
                       warehouse_name, currency_code, exchange_rate, status, tax_code, tax_rate, notes,
                       discount_amount, extra_amount, paid_amount, payment_method, receipt_no, reference_no,
                       branch_name, sales_rep_name, cost_center, party_account_name, goods_account_name,
                       original_doc_id, original_doc_kind, original_doc_no, journal_doc_no
                FROM {headerTable}
                WHERE doc_id=$id AND company_id=$company AND is_deleted=0;
                """;
            Add(cmd, "$id", docId);
            Add(cmd, "$company", Company);
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
                CurrencyCode = original.CurrencyCode,
                ExchangeRate = original.ExchangeRate,
                TaxCode = original.TaxCode,
                DefaultTaxRate = original.DefaultTaxRate,
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
                    WarehouseId = line.WarehouseId
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
        string? TaxAccount, bool Stockable, decimal AvgCost);

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
            return new MaterialGlInfo(null, null, null, null, null, null, null, false, 0m);

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
        return new MaterialGlInfo(accSales, accSalesReturn, accCogs, accPurchase, accPurchaseReturn, accInventory, taxAccount, stockable, avgCost);
    }

    private static string CodeOr(string? configured, string fallback)
        => string.IsNullOrWhiteSpace(configured) ? fallback : configured.Trim();

    private static List<ShouTech.Domain.Entities.GlLineRequest> BuildGlLines(DbConnection connection, DbTransaction tx, InvDoc doc)
    {
        var lines = new List<ShouTech.Domain.Entities.GlLineRequest>();
        if (doc.Kind is InvDocKind.Quotation or InvDocKind.StockTransfer or InvDocKind.OpeningStock or InvDocKind.InventoryCount)
            return lines; // no GL for these kinds (see Step 3 doc)

        var provider = DatabaseRuntime.IsPostgreSql() ? "PostgreSQL" : "SQLite";
        var cc = string.IsNullOrWhiteSpace(doc.CostCenter) ? null : doc.CostCenter.Trim();
        var grand = doc.GrandTotal;
        var subtotal = doc.Subtotal;
        var tax = doc.TaxAmount;

        var infos = doc.Lines.Select(l => LoadMaterialGl(connection, tx, l, doc.WarehouseId)).ToList();

        // Optional tax leg: only when a postable VAT account resolves.
        var taxAccountCode = doc.Kind.IsSales() || doc.Kind == InvDocKind.PosSale
            ? GlAccounts.OutputVat : GlAccounts.InputVat;
        var materialTaxAccount = infos.Select(i => i.TaxAccount).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
        if (!string.IsNullOrWhiteSpace(materialTaxAccount)) taxAccountCode = materialTaxAccount.Trim();
        var companyGuid = ResolveCompanyGuid(connection, tx);
        var splitTax = tax > 0 && GlPosting.TryResolveAccount(connection, tx, provider, companyGuid, taxAccountCode) is not null;
        var taxLeg = splitTax ? tax : 0m;
        var netLeg = subtotal + (splitTax ? 0m : tax); // tax-inclusive when unsplit

        void Add(string code, decimal debit, decimal credit, string? costCenter = null)
        {
            if (debit == 0 && credit == 0) return;
            lines.Add(new ShouTech.Domain.Entities.GlLineRequest(code, debit, credit, costCenter));
        }

        // COGS at the stock layer's current cost figures (Step 6 scope).
        void AddCogs(bool reverse)
        {
            for (var i = 0; i < doc.Lines.Count; i++)
            {
                var line = doc.Lines[i];
                var info = infos[i];
                if (!info.Stockable || line.Qty == 0 || info.AvgCost <= 0) continue;
                var cost = decimal.Round(line.Qty * info.AvgCost, 4, MidpointRounding.AwayFromZero);
                if (cost <= 0) continue;
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
                if (doc.Kind == InvDocKind.PosSale || paid > 0)
                {
                    Add(settleAccount, paid, 0);
                    Add(GlAccounts.Receivable, grand - paid, 0);
                }
                else Add(GlAccounts.Receivable, grand, 0);
                for (var i = 0; i < doc.Lines.Count; i++)
                    Add(CodeOr(infos[i].AccSales, GlAccounts.SalesRevenue), 0,
                        decimal.Round(doc.Lines[i].LineSubtotal + (splitTax ? 0m : doc.Lines[i].LineTax), 4, MidpointRounding.AwayFromZero), cc);
                if (splitTax) Add(taxAccountCode, 0, taxLeg);
                AddCogs(reverse: false);
                break;
            }
            case InvDocKind.SalesReturn:
            {
                for (var i = 0; i < doc.Lines.Count; i++)
                    Add(CodeOr(infos[i].AccSalesReturn, GlAccounts.SalesReturns),
                        decimal.Round(doc.Lines[i].LineSubtotal + (splitTax ? 0m : doc.Lines[i].LineTax), 4, MidpointRounding.AwayFromZero), 0, cc);
                if (splitTax) Add(taxAccountCode, taxLeg, 0);
                Add(GlAccounts.Receivable, 0, grand);
                AddCogs(reverse: true);
                break;
            }
            case InvDocKind.PurchaseInvoice:
            {
                for (var i = 0; i < doc.Lines.Count; i++)
                    Add(CodeOr(infos[i].AccPurchase, GlAccounts.Purchases),
                        decimal.Round(doc.Lines[i].LineSubtotal + (splitTax ? 0m : doc.Lines[i].LineTax), 4, MidpointRounding.AwayFromZero), 0, cc);
                if (splitTax) Add(taxAccountCode, taxLeg, 0);
                var paid = Math.Min(Math.Max(doc.PaidAmount, 0m), grand);
                Add(doc.PaymentMethod == InvPaymentMethod.Cash ? GlAccounts.Cash : GlAccounts.Bank, 0, paid);
                Add(GlAccounts.Payable, 0, grand - paid);
                break;
            }
            case InvDocKind.PurchaseReturn:
            {
                Add(GlAccounts.Payable, grand, 0);
                for (var i = 0; i < doc.Lines.Count; i++)
                    Add(CodeOr(infos[i].AccPurchaseReturn, GlAccounts.PurchaseReturns), 0,
                        decimal.Round(doc.Lines[i].LineSubtotal + (splitTax ? 0m : doc.Lines[i].LineTax), 4, MidpointRounding.AwayFromZero), cc);
                if (splitTax) Add(taxAccountCode, 0, taxLeg);
                break;
            }
            case InvDocKind.StockIn:
                Add(GlAccounts.Inventory, netLeg + taxLeg, 0);
                Add(GlAccounts.Purchases, 0, netLeg + taxLeg);
                break;
            case InvDocKind.StockOut:
                Add(GlAccounts.Cogs, netLeg + taxLeg, 0);
                Add(GlAccounts.Inventory, 0, netLeg + taxLeg);
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
