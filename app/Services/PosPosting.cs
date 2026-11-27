using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.App.Reporting.Invoices;
using ShouTech.Domain;
using ShouTech.Domain.Entities;
using ShouTech.Infrastructure;

namespace ShouTech.App.Services;

/// <summary>
/// The POS posting orchestrator (Build POS 066, docs/BUILD_POS.md).
/// A POS sale and a POS return post through the SAME verified engine
/// as every other commercial document — InvRepo.Post (real GL step 3,
/// party settlement step 4, stored totals step 5, engine valuation
/// steps 6/11, batch/serial 054) — extended, never forked:
///   * CompleteSale stamps the document's paid_amount from the
///     collected tenders (cash + card), so step 4's settlement writes
///     exactly the collected part and the on-account remainder stays
///     on the party ledger; the tenders and the session link are
///     written by InvRepo inside the posting transaction.
///   * CreatePosReturn builds a SalesReturn linked to the original
///     POS bill (original_doc_id), with header discount/extra and the
///     refund allocated PRO-RATA (the step-5 symmetric shape), posts
///     it through the same path, then partially un-settles the
///     original in one transaction: the at-posting settlement is
///     mirrored for the refunded part, the allocation is reduced,
///     and the original's paid_amount falls by the refund — the
///     partial analogue of ReversePostedInvoice's full un-settle.
/// </summary>
public static class PosPosting
{
    private static string Company => AppState.CurrentCompanyCode ?? string.Empty;

    /// <summary>Completes a POS sale: validates the tenders against
    /// the cart total (pure math, POS-1004), saves the draft, captures
    /// batch/serial, and posts with the POS context. Returns the
    /// posted document (with its journal number).</summary>
    public static InvDoc CompleteSale(
        InvDoc doc, Guid? sessionId, IReadOnlyList<PosTender> tenders,
        string userId, Func<InvDoc, IReadOnlyDictionary<int, LineBatchSerialCapture>?> captureBatchSerial,
        decimal exchangeCredit = 0m)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(tenders);
        if (doc.Kind is not (InvDocKind.PosSale or InvDocKind.SalesInvoice))
            throw new InvalidOperationException("CompleteSale only posts POS sales (or sales invoices for terminals bound to SAL).");
        PosTenderMath.ValidateSaleTenders(doc.GrandTotal, tenders, doc.PartyId is not null, exchangeCredit);
        doc.PaidAmount = PosTenderMath.CollectedAmount(tenders);
        // The document's single PaymentMethod stays meaningful for the
        // legacy readers: Card when any card tender exists, Credit
        // when the whole bill is on account, else Cash. The GL split
        // itself comes from the tenders (InvRepo.ValidatePosContext).
        doc.PaymentMethod = PosTenderMath.CardApplied(tenders) > 0 ? InvPaymentMethod.Card
            : doc.PaidAmount == 0 ? InvPaymentMethod.Credit
            : InvPaymentMethod.Cash;
        InvRepo.SaveDraft(doc);
        var captures = captureBatchSerial(doc);
        if (captures is null) return doc; // capture cancelled by the cashier
        InvRepo.Post(doc.DocId, doc.Kind, userId, captures,
            new PosPostingContext(sessionId, tenders, exchangeCredit));
        var posted = InvRepo.Load(doc.DocId, doc.Kind) ?? doc;
        return posted;
    }

    /// <summary>The two posted documents of one POS exchange (W9).</summary>
    public sealed record PosExchangeResult(InvDoc ReturnDoc, InvDoc SaleDoc, string ExchangeRef, decimal Net);

    /// <summary>
    /// A POS exchange (sales cycle W9): return lines off a completed
    /// bill AND a new sale in one flow, settled on the NET.
    ///
    /// Document model (chosen): an exchange is TWO documents — the
    /// SRT return and the POS sale — each posted through the existing
    /// verified paths (CreatePosReturn / CompleteSale, each in its
    /// own posting transaction; the InvRepo architecture posts one
    /// document per transaction, so a single cross-document
    /// transaction does not exist to reuse). Both documents are
    /// stamped with the same exchange_ref (migration 104) so the
    /// pair is one auditable flow. If the sale leg fails after the
    /// return posted, the return stands as an ordinary POS return —
    /// a true, complete document, never a half-exchange.
    ///
    /// Netting: net = new sale total − return total.
    ///   * The return's refund is ONLY the negative-net excess
    ///     (cashRefund / cardRefund, magnitudes summing to −net when
    ///     net &lt; 0, both zero otherwise); the consumed part of the
    ///     return rides as the return's on-account remainder — the
    ///     existing partial-return shape.
    ///   * The new sale is tendered with an on-account "exchange
    ///     credit" tender equal to the consumed return value, plus
    ///     <paramref name="netTenders"/> settling the positive net.
    ///     The credit is not party credit (PosTenderMath carries it
    ///     as exchangeCredit): its GL receivable leg is offset by
    ///     the return's own credit leg, and for a named party the
    ///     return's ledger credit and the sale's ledger debit net on
    ///     the party ledger. Drawer math nets too: the session's
    ///     cash tenders sum to exactly the net cash movement.
    /// </summary>
    public static PosExchangeResult CompleteExchange(
        PosOriginalBill original, IReadOnlyDictionary<int, decimal> qtyByLineNo,
        InvDoc newSale, IReadOnlyList<PosTender> netTenders,
        decimal cashRefund, decimal cardRefund, Guid? sessionId,
        string reason, string userId,
        Func<InvDoc, IReadOnlyDictionary<int, LineBatchSerialCapture>?> captureBatchSerial)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(newSale);
        ArgumentNullException.ThrowIfNull(netTenders);
        if (newSale.Kind is not (InvDocKind.PosSale or InvDocKind.SalesInvoice))
            throw new InvalidOperationException("An exchange's new document must be a POS sale (or a sales invoice for terminals bound to SAL).");
        if (newSale.Lines.Count == 0)
            throw new AppError(ErrorCodes.PosTenderInvalid, "An exchange needs at least one new sale line; a plain return is not an exchange.");
        // The exchange is with the original bill's customer (or the
        // same walk-in): the sale inherits the original's party.
        newSale.PartyId = original.PartyId;
        newSale.PartyCode = original.PartyCode ?? string.Empty;
        newSale.PartyName = original.PartyName ?? string.Empty;

        // 1) The return leg. Its total decides the net, so it posts
        // first with only the excess refunded (validated below once
        // the real total is known — the refund split the cashier
        // entered is checked against the posted total, and a split
        // that exceeds the excess is refused BEFORE the sale leg).
        var returnDoc = CreatePosReturn(original, qtyByLineNo, cashRefund, cardRefund,
            sessionId, reason, userId, captureBatchSerial);
        if (returnDoc.Status != InvDocStatus.Posted) return new PosExchangeResult(returnDoc, newSale, string.Empty, 0m); // capture cancelled; draft stands

        var returnTotal = returnDoc.GrandTotal;
        var net = PosTenderMath.ExchangeNet(newSale.GrandTotal, returnTotal);
        var refundGiven = PosTenderMath.Round(cashRefund + cardRefund);
        var expectedRefund = net < 0 ? -net : 0m;
        if (refundGiven != expectedRefund)
        {
            // The return already posted as an ordinary return with
            // the cashier's split; refuse the sale leg rather than
            // pretend the pair netted.
            throw new AppError(ErrorCodes.PosTenderMismatch,
                $"The refunded {refundGiven} does not equal the exchange excess {expectedRefund} (return {returnTotal}, new sale {newSale.GrandTotal}); the return {returnDoc.DocNo} stands as a plain return and no exchange sale was posted.");
        }
        var consumedCredit = PosTenderMath.Round(returnTotal - refundGiven);
        if (consumedCredit > PosTenderMath.Round(newSale.GrandTotal))
            throw new AppError(ErrorCodes.PosTenderMismatch,
                "The consumed return credit exceeds the new sale total; the exchange cannot net.");

        // 2) The sale leg: exchange-credit tender + the net tenders.
        var saleTenders = new List<PosTender>();
        if (consumedCredit > 0)
            saleTenders.Add(new PosTender { TenderType = PosTenderTypes.OnAccount, Amount = consumedCredit });
        saleTenders.AddRange(netTenders);
        var postedSale = CompleteSale(newSale, sessionId, saleTenders, userId, captureBatchSerial, consumedCredit);
        if (postedSale.Status != InvDocStatus.Posted) return new PosExchangeResult(returnDoc, postedSale, string.Empty, net); // capture cancelled

        // 3) Link the pair.
        var exchangeRef = "EXC-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        StampExchangeRef(returnDoc.DocId, postedSale.DocId, exchangeRef);
        return new PosExchangeResult(returnDoc, postedSale, exchangeRef, net);
    }

    /// <summary>Stamps the shared exchange_ref on both documents of
    /// an exchange, in one transaction (the RestoreOriginalLineNumbers
    /// pattern).</summary>
    private static void StampExchangeRef(long returnDocId, long saleDocId, string exchangeRef)
    {
        var cs = DatabaseRuntime.GetConnectionString()
            ?? throw new InvalidOperationException("No database connection.");
        using var connection = Open(cs);
        using var tx = connection.BeginTransaction();
        foreach (var docId in new[] { returnDocId, saleDocId })
        {
            using var upd = connection.CreateCommand();
            upd.Transaction = tx;
            // sales_documents rows carry the company CODE on insert,
            // but legacy rows may carry the company GUID (the InvRepo
            // dual-key pattern) — match either store of the company.
            upd.CommandText = "UPDATE sales_documents SET exchange_ref=$ref WHERE doc_id=$id AND company_id IN ($company, (SELECT company_id FROM companies WHERE company_code=$company));";
            Add(upd, "$ref", exchangeRef);
            Add(upd, "$id", docId);
            Add(upd, "$company", Company);
            if (upd.ExecuteNonQuery() != 1)
                throw new InvalidOperationException($"Document {docId} could not be stamped with the exchange reference.");
        }
        tx.Commit();
    }

    /// <summary>The refund tenders for a return, built from the
    /// cashier's refund split (positive magnitudes in, negative
    /// stored amounts out): cash + card refund the collected part; any
    /// remainder of the return total is the on-account part.</summary>
    public static List<PosTender> BuildRefundTenders(decimal returnTotal, decimal cashRefund, decimal cardRefund)
    {
        if (cashRefund < 0 || cardRefund < 0)
            throw new AppError(ErrorCodes.PosTenderInvalid, "A refund amount cannot be negative.");
        if (PosTenderMath.Round(cashRefund + cardRefund) > PosTenderMath.Round(returnTotal))
            throw new AppError(ErrorCodes.PosTenderInvalid,
                "The refunded cash + card cannot exceed the return total; the rest is on-account.");
        var tenders = new List<PosTender>();
        if (cashRefund > 0) tenders.Add(new PosTender { TenderType = PosTenderTypes.Cash, Amount = -cashRefund });
        if (cardRefund > 0) tenders.Add(new PosTender { TenderType = PosTenderTypes.Card, Amount = -cardRefund });
        var onAccount = PosTenderMath.Round(returnTotal - cashRefund - cardRefund);
        if (onAccount > 0) tenders.Add(new PosTender { TenderType = PosTenderTypes.OnAccount, Amount = -onAccount });
        if (tenders.Count == 0 && returnTotal > 0)
            tenders.Add(new PosTender { TenderType = PosTenderTypes.OnAccount, Amount = -returnTotal });
        return tenders;
    }

    /// <summary>Creates and posts a POS return against an original
    /// bill. qtyByLineNo is keyed by the ORIGINAL line numbers (the
    /// return lines keep them, which is what makes the
    /// already-returned accounting in PosRepository exact).</summary>
    public static InvDoc CreatePosReturn(
        PosOriginalBill original, IReadOnlyDictionary<int, decimal> qtyByLineNo,
        decimal cashRefund, decimal cardRefund, Guid? sessionId,
        string reason, string userId,
        Func<InvDoc, IReadOnlyDictionary<int, LineBatchSerialCapture>?> captureBatchSerial)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(qtyByLineNo);
        PosTenderMath.ValidateReturnQuantities(original, qtyByLineNo);

        var lines = new List<InvLine>();
        foreach (var (lineNo, qty) in qtyByLineNo.OrderBy(kv => kv.Key))
        {
            if (qty <= 0) continue;
            var source = original.Lines.Single(l => l.LineNo == lineNo);
            lines.Add(new InvLine
            {
                LineNo = source.LineNo,
                MaterialId = source.MaterialId,
                MaterialCode = source.MaterialCode,
                Description = source.Description,
                Unit = source.Unit ?? string.Empty,
                Qty = qty,
                UnitPrice = source.UnitPrice,
                DiscountPct = source.DiscountPct,
                TaxRate = source.TaxRate,
                TaxCalculationMode = source.TaxCalculationMode,
                WarehouseId = source.WarehouseId
            });
        }
        if (lines.Count == 0)
            throw new AppError(ErrorCodes.PosReturnOriginalInvalid, "Select at least one line quantity to return.");

        var returnedSubtotal = lines.Sum(l => l.LineSubtotal);
        var returnDoc = new InvDoc
        {
            Kind = InvDocKind.SalesReturn,
            DocNo = InvRepo.NextDocNo(InvDocKind.SalesReturn),
            DocDate = DateTime.Today,
            PartyId = original.PartyId,
            PartyCode = original.PartyCode ?? string.Empty,
            PartyName = original.PartyName ?? string.Empty,
            CurrencyCode = original.CurrencyCode,
            ExchangeRate = original.ExchangeRate,
            LinesDiscountAmount = PosTenderMath.ProRata(original.DiscountAmount, returnedSubtotal, original.LinesSubtotal),
            LinesExtraAmount = PosTenderMath.ProRata(original.ExtraAmount, returnedSubtotal, original.LinesSubtotal),
            PaidAmount = cashRefund + cardRefund,
            PaymentMethod = cardRefund > 0 && cashRefund == 0 ? InvPaymentMethod.Card : InvPaymentMethod.Cash,
            OriginalDocId = original.DocId,
            OriginalDocKind = InvDocKind.PosSale,
            OriginalDocNo = original.DocNo,
            // W6: the till collects its reason as free text (Notes);
            // the master reason code is stamped OTHER.
            ReturnReasonCode = "OTHER",
            ReturnSourceKind = "INV",
            Notes = $"POS return of {original.DocNo}. Reason: {reason}".Trim(),
            Lines = lines
        };
        // SaveDraft renumbers lines 1..n in original-line ORDER — the
        // return's already-returned accounting needs the ORIGINAL line
        // numbers, so they are captured here and restored in storage
        // after the save (same order, so the mapping is positional).
        var originalLineNos = returnDoc.Lines.Select(l => l.LineNo).ToList();
        InvRepo.SaveDraft(returnDoc);
        RestoreOriginalLineNumbers(returnDoc.DocId, originalLineNos);
        for (var i = 0; i < returnDoc.Lines.Count; i++)
            returnDoc.Lines[i].LineNo = originalLineNos[i];

        var captures = captureBatchSerial(returnDoc);
        if (captures is null) return returnDoc; // capture cancelled
        var tenders = BuildRefundTenders(returnDoc.GrandTotal, cashRefund, cardRefund);
        InvRepo.Post(returnDoc.DocId, returnDoc.Kind, userId, captures,
            new PosPostingContext(sessionId, tenders));
        return InvRepo.Load(returnDoc.DocId, returnDoc.Kind) ?? returnDoc;
    }

    /// <summary>SaveDraft persists lines with LineNo 1..n in order;
    /// this rewrites the stored line_no values to the original bill's
    /// line numbers (same order — the return was built in original
    /// line order), inside one transaction.</summary>
    private static void RestoreOriginalLineNumbers(long docId, List<int> originalLineNos)
    {
        var cs = DatabaseRuntime.GetConnectionString()
            ?? throw new InvalidOperationException("No database connection.");
        using var connection = Open(cs);
        using var tx = connection.BeginTransaction();
        using (var read = connection.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = "SELECT line_id FROM sales_document_lines WHERE doc_id=$doc ORDER BY line_id;";
            Add(read, "$doc", docId);
            var ids = new List<long>();
            using var reader = read.ExecuteReader();
            while (reader.Read()) ids.Add(Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture));
            reader.Close();
            if (ids.Count != originalLineNos.Count)
                throw new InvalidOperationException("The return's stored lines do not match its in-memory lines.");
            for (var i = 0; i < ids.Count; i++)
            {
                using var upd = connection.CreateCommand();
                upd.Transaction = tx;
                upd.CommandText = "UPDATE sales_document_lines SET line_no=$no WHERE line_id=$id;";
                Add(upd, "$no", originalLineNos[i]);
                Add(upd, "$id", ids[i]);
                upd.ExecuteNonQuery();
            }
        }
        tx.Commit();
    }

    /// <summary>The partial analogue of InvRepo's full-reversal
    /// un-settle, in one transaction: (1) the original's at-posting
    /// settlement entries are mirrored — sides swapped, pointing at
    /// the return — for exactly the refunded collected amount;
    /// (2) the at-posting allocation against the original is reduced
    /// by the same amount (deleted at zero); (3) the original's
    /// paid_amount falls by the refund. External voucher allocations
    /// are NOT touched: that money really was received, and freeing
    /// it is the full-reversal rule, not a partial return's.</summary>
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
}

/// <summary>An approved supervisor override of the cashier discount
/// limit (migration 104): who approved, the ceiling that was
/// exceeded, and the measured discount it approved. Written to
/// pos_discount_overrides by <see cref="PosDiscountGuard"/> once the
/// sale it approved has posted.</summary>
public sealed record PosDiscountApproval(
    string Cashier, string ApprovedBy, decimal LimitPct,
    PosDiscountMath.Measurement Measurement);

/// <summary>
/// Supervisor-override verification + audit for the numeric cashier
/// discount limit (sales cycle W9, migration 104). A supervisor is a
/// user of the SAME company who (a) authenticates with their own
/// password (PwdHash.Verify — the Auth.cs primitive, no session is
/// touched and no login lockout state is mutated) and (b) holds
/// POS.DISCOUNT.OVERRIDE under the exact Permissions.cs resolution
/// (administrator, role grant, or per-user allow; a per-user deny
/// wins). Verification never reveals which half failed.
/// </summary>
public static class PosDiscountGuard
{
    private static string Company => AppState.CurrentCompanyCode ?? string.Empty;

    /// <summary>True when the named user authenticated AND holds
    /// POS.DISCOUNT.OVERRIDE in the current company.</summary>
    public static bool VerifySupervisor(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password)) return false;
        var cs = DatabaseRuntime.GetConnectionString()
            ?? throw new InvalidOperationException("No database connection.");
        var pg = DatabaseRuntime.IsPostgreSql();
        var notDeleted = pg ? "is_deleted = FALSE" : "is_deleted = 0";
        var active = pg ? "is_active = TRUE" : "is_active = 1";
        using var connection = OpenConnection(cs);
        object userId;
        string hash, salt;
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT u.user_id, u.password_hash, u.password_salt
                FROM users u JOIN companies c ON c.company_id = u.company_id
                WHERE c.company_code = $company AND u.username = $user
                  AND u.{active} AND u.{notDeleted} AND c.{active} AND c.{notDeleted}
                """;
            AddParam(cmd, "$company", Company);
            AddParam(cmd, "$user", username.Trim());
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return false;
            userId = reader.GetValue(0);
            hash = reader.IsDBNull(1) ? string.Empty : Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) ?? string.Empty;
            salt = reader.IsDBNull(2) ? string.Empty : Convert.ToString(reader.GetValue(2), CultureInfo.InvariantCulture) ?? string.Empty;
        }
        if (!PwdHash.Verify(password, hash, salt)) return false;

        var effectTrue = pg ? "TRUE" : "1";
        var effectFalse = pg ? "FALSE" : "0";
        long Count(string sql)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            AddParam(cmd, "$uid", userId);
            AddParam(cmd, "$company", Company);
            return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        var isAdmin = Count($"""
            SELECT COUNT(*) FROM user_roles ur JOIN roles r ON r.role_id = ur.role_id
            JOIN companies c ON c.company_id = r.company_id
            WHERE ur.user_id = $uid AND c.company_code = $company
              AND r.role_code = 'ROLE_ADMINISTRATOR' AND r.{active} AND r.{notDeleted}
            """) > 0;
        var granted = isAdmin || Count($"""
            SELECT COUNT(*) FROM role_permissions rp
            JOIN user_roles ur ON ur.role_id = rp.role_id
            JOIN permissions p ON p.permission_id = rp.permission_id
            WHERE ur.user_id = $uid AND p.permission_code = 'POS.DISCOUNT.OVERRIDE' AND p.{active}
            """) > 0 || Count($"""
            SELECT COUNT(*) FROM user_permissions up
            JOIN permissions p ON p.permission_id = up.permission_id
            WHERE up.user_id = $uid AND p.permission_code = 'POS.DISCOUNT.OVERRIDE' AND up.effect = {effectTrue}
            """) > 0;
        if (!granted) return false;
        var denied = Count($"""
            SELECT COUNT(*) FROM user_permissions up
            JOIN permissions p ON p.permission_id = up.permission_id
            WHERE up.user_id = $uid AND p.permission_code = 'POS.DISCOUNT.OVERRIDE' AND up.effect = {effectFalse}
            """) > 0;
        return !denied;
    }

    /// <summary>Writes the override audit row for a posted sale.
    /// Called AFTER the sale posts, so the row always names a real
    /// document; a cancelled checkout writes nothing.</summary>
    public static void RecordOverride(string docKind, string docNo, long docId, PosDiscountApproval approval)
    {
        ArgumentNullException.ThrowIfNull(approval);
        var cs = DatabaseRuntime.GetConnectionString()
            ?? throw new InvalidOperationException("No database connection.");
        using var connection = OpenConnection(cs);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO pos_discount_overrides
                (id, company_id, doc_kind, doc_no, doc_id, cashier, approved_by,
                 limit_pct, effective_pct, max_line_pct, discount_amount)
            VALUES ($id, $company, $kind, $no, $doc, $cashier, $approver,
                    $limit, $effective, $maxline, $amount);
            """;
        AddParam(cmd, "$id", Guid.NewGuid().ToString("D"));
        AddParam(cmd, "$company", Company);
        AddParam(cmd, "$kind", docKind);
        AddParam(cmd, "$no", docNo);
        AddParam(cmd, "$doc", docId);
        AddParam(cmd, "$cashier", approval.Cashier);
        AddParam(cmd, "$approver", approval.ApprovedBy);
        AddParam(cmd, "$limit", approval.LimitPct);
        AddParam(cmd, "$effective", approval.Measurement.EffectivePct);
        AddParam(cmd, "$maxline", approval.Measurement.MaxLinePct);
        AddParam(cmd, "$amount", approval.Measurement.DiscountAmount);
        cmd.ExecuteNonQuery();
    }

    private static DbConnection OpenConnection(string connectionString)
    {
        DbConnection connection = DatabaseRuntime.IsPostgreSql()
            ? new NpgsqlConnection(connectionString)
            : new SqliteConnection(DatabaseRuntime.NormalizeSqliteConnectionString(connectionString));
        connection.Open();
        return connection;
    }

    private static void AddParam(DbCommand command, string name, object? value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        command.Parameters.Add(p);
    }
}
