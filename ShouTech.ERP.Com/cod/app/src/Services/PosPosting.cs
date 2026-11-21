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
        string userId, Func<InvDoc, IReadOnlyDictionary<int, LineBatchSerialCapture>?> captureBatchSerial)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(tenders);
        if (doc.Kind != InvDocKind.PosSale)
            throw new InvalidOperationException("CompleteSale only posts POS sales.");
        PosTenderMath.ValidateSaleTenders(doc.GrandTotal, tenders, doc.PartyId is not null);
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
            new PosPostingContext(sessionId, tenders));
        var posted = InvRepo.Load(doc.DocId, doc.Kind) ?? doc;
        return posted;
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
            ExchangeRate = 1m,
            LinesDiscountAmount = PosTenderMath.ProRata(original.DiscountAmount, returnedSubtotal, original.LinesSubtotal),
            LinesExtraAmount = PosTenderMath.ProRata(original.ExtraAmount, returnedSubtotal, original.LinesSubtotal),
            PaidAmount = cashRefund + cardRefund,
            PaymentMethod = cardRefund > 0 && cashRefund == 0 ? InvPaymentMethod.Card : InvPaymentMethod.Cash,
            OriginalDocId = original.DocId,
            OriginalDocKind = InvDocKind.PosSale,
            OriginalDocNo = original.DocNo,
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
