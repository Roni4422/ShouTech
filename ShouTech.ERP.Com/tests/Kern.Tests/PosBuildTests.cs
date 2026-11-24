using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ShouTech.App;
using ShouTech.App.Configuration;
using ShouTech.App.Reporting.Invoices;
using ShouTech.App.Services;
using ShouTech.Application.Services;
using ShouTech.Domain;
using ShouTech.Domain.Entities;
using ShouTech.Infrastructure;
using Xunit;

namespace Kern.Tests;

/// <summary>
/// BUILD POS (migration 066) — pins, against real migrated SQLite
/// databases:
///   * the pure tender math: a 60 cash / 40 card split settles a 100
///     bill with no change; cash tendered 70 applied 60 gives 10
///     change; a mismatch is POS-1004; on-account for a walk-in is
///     POS-1005; expected cash = opening + cash sales − cash refunds;
///   * the session gate: no open session refuses with POS-1001,
///     session-less without the bypass refuses with POS-1010, and a
///     posting naming a closed session refuses inside InvRepo.Post
///     leaving the document Draft;
///   * a split-tender sale posts GL Dr Cash 60 / Dr Bank 40 (never a
///     single-method leg), records both tenders in the posting
///     transaction, and links the document to its session;
///   * an on-account sale for a named customer settles exactly the
///     collected part through party_ledger (step 4 extended, not
///     forked): debit 100, settlement credit 70, balance 30; a walk-in
///     writes no party_ledger rows at all;
///   * a partial POS return of 1 of 2 restores stock by 1, mirrors
///     the settlement pro-rata, drops the original's paid_amount by
///     the refund, and a further return of 2 is POS-1006;
///   * a held bill persists and recalls with identical lines/totals,
///     and cannot be recalled twice (POS-1009);
///   * the Z close: expected = opening + net cash tenders exactly,
///     variance = counted − expected.
/// Execution requires Windows (net8.0-windows testhost); the
/// repository/service scenarios are verified headlessly by
/// ~/workspace/research/build-pos/pos_harness.py (see
/// docs/BUILD_POS.md).
/// </summary>
public class PosBuildTests
{
    private const string Actor = "pos-test";
    private const string CompanyCode = "POS1";
    private static readonly Func<InvDoc, IReadOnlyDictionary<int, LineBatchSerialCapture>?> NoCapture =
        _ => new Dictionary<int, LineBatchSerialCapture>();

    // ------------------------------------------------------------------
    // Pure tender math
    // ------------------------------------------------------------------

    [Fact]
    public void TenderMath_Split60Cash40Card_Settles100_NoChange()
    {
        var tenders = new List<PosTender>
        {
            new() { TenderType = PosTenderTypes.Cash, Amount = 60m, TenderedAmount = 60m },
            new() { TenderType = PosTenderTypes.Card, Amount = 40m }
        };
        Assert.Equal(0m, PosTenderMath.ValidateSaleTenders(100m, tenders, hasParty: false));
        Assert.Equal(100m, PosTenderMath.CollectedAmount(tenders));
        Assert.Equal(60m, PosTenderMath.CashApplied(tenders));
        Assert.Equal(40m, PosTenderMath.CardApplied(tenders));
    }

    [Fact]
    public void TenderMath_OverTenderedCash_ChangeIsExcessOnly()
    {
        var tenders = new List<PosTender>
        {
            new() { TenderType = PosTenderTypes.Cash, Amount = 60m, TenderedAmount = 70m },
            new() { TenderType = PosTenderTypes.Card, Amount = 40m }
        };
        Assert.Equal(10m, PosTenderMath.ValidateSaleTenders(100m, tenders, hasParty: false));
    }

    [Fact]
    public void TenderMath_Mismatch_ThrowsPos1004_AndWalkInOnAccount_ThrowsPos1005()
    {
        var short_ = new List<PosTender> { new() { TenderType = PosTenderTypes.Cash, Amount = 90m, TenderedAmount = 90m } };
        var error = Assert.Throws<AppError>(() => PosTenderMath.ValidateSaleTenders(100m, short_, hasParty: false));
        Assert.Equal(ErrorCodes.PosTenderMismatch, error.Code);

        var onAccount = new List<PosTender> { new() { TenderType = PosTenderTypes.OnAccount, Amount = 100m } };
        var error2 = Assert.Throws<AppError>(() => PosTenderMath.ValidateSaleTenders(100m, onAccount, hasParty: false));
        Assert.Equal(ErrorCodes.PosTenderInvalid, error2.Code);
    }

    [Fact]
    public void TenderMath_ExpectedCash_AndProRata()
    {
        var tenders = new List<PosTender>
        {
            new() { TenderType = PosTenderTypes.Cash, Amount = 60m },
            new() { TenderType = PosTenderTypes.Card, Amount = 40m },
            new() { TenderType = PosTenderTypes.Cash, Amount = -20m, DocKind = "SRT" }
        };
        Assert.Equal(240m, PosTenderMath.ExpectedCash(200m, tenders));
        Assert.Equal(25m, PosTenderMath.ProRata(100m, 50m, 200m));
        Assert.Equal(0m, PosTenderMath.ProRata(100m, 50m, 0m));
    }

    [Fact]
    public void ReturnMath_ExceedingReturnable_ThrowsPos1006()
    {
        var original = new PosOriginalBill
        {
            DocId = 5, DocNo = "POS-2026-0001", GrandTotal = 100m, PaidAmount = 100m,
            Lines = { new PosReturnableLine(1, 42, "MAT-42", "Item", "pcs", 2m, 50m, 0m, 0m, 1, 1m) }
        };
        var error = Assert.Throws<AppError>(() =>
            PosTenderMath.ValidateReturnQuantities(original, new Dictionary<int, decimal> { [1] = 2m }));
        Assert.Equal(ErrorCodes.PosReturnExceedsSold, error.Code);
        PosTenderMath.ValidateReturnQuantities(original, new Dictionary<int, decimal> { [1] = 1m });
    }

    // ------------------------------------------------------------------
    // Repository / service integration
    // ------------------------------------------------------------------

    [Fact]
    public async Task SessionGate_NoSession_Pos1001_SessionlessWithoutBypass_Pos1010_WithBypass_Ok()
    {
        using var db = await SeededAsync();
        var service = new PosService(new PosRepository("SQLite", db.ConnectionString));

        var error = await Assert.ThrowsAsync<AppError>(() =>
            service.RequireSessionForSaleAsync(CompanyCode, "T1", callerMayBypassSession: true));
        Assert.Equal(ErrorCodes.PosNoOpenSession, error.Code);

        await service.SaveSettingsAsync(CompanyCode, new PosSettings { AllowSessionless = true, DefaultTerminalCode = "T1" }, Actor);
        var error2 = await Assert.ThrowsAsync<AppError>(() =>
            service.RequireSessionForSaleAsync(CompanyCode, "T1", callerMayBypassSession: false));
        Assert.Equal(ErrorCodes.PosSessionlessNotAllowed, error2.Code);
        Assert.Null(await service.RequireSessionForSaleAsync(CompanyCode, "T1", callerMayBypassSession: true));

        var missingWarehouse = await Assert.ThrowsAsync<AppError>(() =>
            service.OpenSessionAsync(CompanyCode, "T1", "Cashier", null, "SAR", 200m, Actor));
        Assert.Equal(ErrorCodes.PosSessionStateInvalid, missingWarehouse.Code);

        var session = await service.OpenSessionAsync(CompanyCode, "T1", "Cashier", 1, "SAR", 200m, Actor);
        var error3 = await Assert.ThrowsAsync<AppError>(() =>
            service.OpenSessionAsync(CompanyCode, "T1", "Other", 1, "SAR", 0m, Actor));
        Assert.Equal(ErrorCodes.PosSessionAlreadyOpen, error3.Code);
        Assert.Equal(session.Id, (await service.RequireSessionForSaleAsync(CompanyCode, "T1", callerMayBypassSession: false))!.Id);
    }

    [Fact]
    public async Task TerminalWarehouse_ResolvesCompanyScopedActiveTerminalDefinition()
    {
        using var db = await SeededAsync();
        await using (var connection = new SqliteConnection(db.ConnectionString))
        {
            await connection.OpenAsync();
            await using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO pos_terminals (id, company_id, code, name_en, warehouse_code, is_active, is_deleted) VALUES ('terminal-1', $company, 'T1', 'Main terminal', '1', 1, 0);";
            insert.Parameters.AddWithValue("$company", CompanyCode);
            Assert.Equal(1, await insert.ExecuteNonQueryAsync());
        }

        var service = new PosService(new PosRepository("SQLite", db.ConnectionString));
        Assert.Equal(1, await service.GetTerminalWarehouseIdAsync(CompanyCode, "T1"));
        Assert.Null(await service.GetTerminalWarehouseIdAsync(CompanyCode, "T2"));
        Assert.Null(await service.GetTerminalWarehouseIdAsync("OTHER", "T1"));
    }

    [Fact]
    public async Task ProductSearch_ReturnsLiveStockForSelectedWarehouse_AndCompanyOnly()
    {
        using var db = await SeededAsync();
        var repo = new PosRepository("SQLite", db.ConnectionString);
        await using (var connection = new SqliteConnection(db.ConnectionString))
        {
            await connection.OpenAsync();
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE material_stock_balances SET reserved_qty = 125 WHERE company_id = $company AND warehouse_id = 1 AND material_id = 42;";
            update.Parameters.AddWithValue("$company", CompanyCode);
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }

        var products = await repo.SearchProductsAsync(CompanyCode, "MAT-42", warehouseId: 1, limit: 10);
        var product = Assert.Single(products);
        Assert.Equal(1000m, product.OnHandQty);
        Assert.Equal(125m, product.ReservedQty);
        Assert.Equal(875m, product.SellableQty);

        var otherWarehouse = await repo.SearchProductsAsync(CompanyCode, "MAT-42", warehouseId: 2, limit: 10);
        Assert.Equal(0m, Assert.Single(otherWarehouse).SellableQty);

        var noWarehouse = await repo.SearchProductsAsync(CompanyCode, "MAT-42", warehouseId: null, limit: 10);
        Assert.Equal(875m, Assert.Single(noWarehouse).SellableQty);

        var otherCompany = await repo.SearchProductsAsync("OTHER", "MAT-42", warehouseId: 1, limit: 10);
        Assert.Empty(otherCompany);
    }

    [Fact]
    public async Task HeldBill_Persists_RecallsIdentically_ThenPos1009()
    {
        using var db = await SeededAsync();
        var repo = new PosRepository("SQLite", db.ConnectionString);
        var bill = new PosHeldBill
        {
            Cashier = "Cashier", CurrencyCode = "SAR", BillDiscount = 5m,
            Lines =
            {
                new PosHeldLine { MaterialId = 42, MaterialCode = "MAT-42", Description = "Item", Unit = "pcs", Qty = 2m, UnitPrice = 50m, TaxRate = 0m },
                new PosHeldLine { MaterialId = 43, MaterialCode = "MAT-43", Description = "Other", Unit = "pcs", Qty = 1m, UnitPrice = 10m, DiscountPct = 10m, TaxRate = 0m }
            }
        };
        var held = await repo.HoldBillAsync(CompanyCode, bill, Actor);
        Assert.StartsWith("HLD-", held.HeldNo);
        Assert.Equal(104m, held.GrandTotal); // (100 + 9) - 5

        var recalled = await repo.RecallHeldBillAsync(CompanyCode, held.Id, Actor);
        Assert.Equal(held.GrandTotal, recalled.GrandTotal);
        Assert.Equal(2, recalled.Lines.Count);
        Assert.Equal(50m, recalled.Lines[0].UnitPrice);
        Assert.Equal(10m, recalled.Lines[1].DiscountPct);

        var error = await Assert.ThrowsAsync<AppError>(() => repo.RecallHeldBillAsync(CompanyCode, held.Id, Actor));
        Assert.Equal(ErrorCodes.PosHeldBillStateInvalid, error.Code);
        Assert.Empty(await repo.GetHeldBillsAsync(CompanyCode));
    }

    // ------------------------------------------------------------------
    // Posting integration (InvRepo + PosPosting)
    // ------------------------------------------------------------------

    [Fact]
    public async Task SplitSale_PostsSplitGl_TendersAndSessionLink_InsideOneTransaction()
    {
        using var db = await SeededAsync();
        var service = new PosService(new PosRepository("SQLite", db.ConnectionString));
        var session = await service.OpenSessionAsync(CompanyCode, "T1", "Cashier", 1, "SAR", 200m, Actor);

        var doc = BuildSale(qty: 1m, price: 100m, partyId: null);
        var tenders = new List<PosTender>
        {
            new() { TenderType = PosTenderTypes.Cash, Amount = 60m, TenderedAmount = 70m, ChangeAmount = 10m },
            new() { TenderType = PosTenderTypes.Card, Amount = 40m, CardRef = "AUTH-1" }
        };
        var posted = PosPosting.CompleteSale(doc, session.Id, tenders, Actor, NoCapture);
        Assert.Equal(InvDocStatus.Posted, posted.Status);
        Assert.Equal(100m, posted.PaidAmount);

        // GL: cash leg 60 to 131, card leg 40 to 132, revenue 100.
        var entry = SingleEntry(db, "POS", posted.DocId);
        AssertBalanced(entry);
        Assert.Equal(60m, Line(entry, "131").Debit);
        Assert.Equal(40m, Line(entry, "132").Debit);
        Assert.Equal(100m, Line(entry, "301").Credit);

        // Tenders recorded with the change on the cash row; session link set.
        var recorded = await new PosRepository("SQLite", db.ConnectionString)
            .GetTendersForDocAsync(CompanyCode, posted.DocId, "POS");
        Assert.Equal(2, recorded.Count);
        Assert.Equal(10m, recorded.Single(t => t.TenderType == PosTenderTypes.Cash).ChangeAmount);
        Assert.Equal(session.Id, recorded[0].SessionId);
        Assert.Equal(session.Id.ToString("D"), ScalarString(db,
            $"SELECT pos_session_id FROM sales_documents WHERE doc_id = {posted.DocId}"));
        // Walk-in: no party ledger rows at all.
        Assert.Equal(0m, ScalarDecimal(db, "SELECT COUNT(*) FROM party_ledger"));
    }

    [Fact]
    public async Task OnAccountSale_NamedCustomer_LedgerSettlesCollectedOnly()
    {
        using var db = await SeededAsync();
        var service = new PosService(new PosRepository("SQLite", db.ConnectionString));
        var session = await service.OpenSessionAsync(CompanyCode, "T1", "Cashier", 1, "SAR", 0m, Actor);

        var doc = BuildSale(qty: 1m, price: 100m, partyId: 7);
        var tenders = new List<PosTender>
        {
            new() { TenderType = PosTenderTypes.Cash, Amount = 70m, TenderedAmount = 70m },
            new() { TenderType = PosTenderTypes.OnAccount, Amount = 30m }
        };
        var posted = PosPosting.CompleteSale(doc, session.Id, tenders, Actor, NoCapture);
        Assert.Equal(70m, posted.PaidAmount);
        // Debit 100 (the sale), settlement credit 70 (collected) -> balance 30 owed.
        Assert.Equal(100m, ScalarDecimal(db, "SELECT COALESCE(SUM(debit),0) FROM party_ledger WHERE party_id = 7"));
        Assert.Equal(70m, ScalarDecimal(db, "SELECT COALESCE(SUM(credit),0) FROM party_ledger WHERE party_id = 7"));
        var entry = SingleEntry(db, "POS", posted.DocId);
        Assert.Equal(70m, Line(entry, "131").Debit);
        Assert.Equal(30m, Line(entry, "1201").Debit);
    }

    [Fact]
    public async Task SaleNamingClosedSession_RefusedPos1001_DocStaysDraft()
    {
        using var db = await SeededAsync();
        var service = new PosService(new PosRepository("SQLite", db.ConnectionString));
        var session = await service.OpenSessionAsync(CompanyCode, "T1", "Cashier", 1, "SAR", 0m, Actor);
        await service.CloseSessionAsync(CompanyCode, session.Id, 0m, Actor);

        var doc = BuildSale(qty: 1m, price: 100m, partyId: null);
        InvRepo.SaveDraft(doc);
        var tenders = new List<PosTender> { new() { TenderType = PosTenderTypes.Cash, Amount = 100m, TenderedAmount = 100m } };
        var error = Assert.Throws<AppError>(() =>
            InvRepo.Post(doc.DocId, InvDocKind.PosSale, Actor, null, new PosPostingContext(session.Id, tenders)));
        Assert.Equal(ErrorCodes.PosNoOpenSession, error.Code);
        Assert.Equal("Draft", ScalarString(db, $"SELECT status FROM sales_documents WHERE doc_id = {doc.DocId}"));
    }

    [Fact]
    public async Task PartialReturn_RestoresStock_MirrorsSettlement_ThenExcessRefused()
    {
        using var db = await SeededAsync();
        var repo = new PosRepository("SQLite", db.ConnectionString);
        var service = new PosService(repo);
        var session = await service.OpenSessionAsync(CompanyCode, "T1", "Cashier", 1, "SAR", 200m, Actor);

        var doc = BuildSale(qty: 2m, price: 50m, partyId: 7);
        var tenders = new List<PosTender> { new() { TenderType = PosTenderTypes.Cash, Amount = 100m, TenderedAmount = 100m } };
        var posted = PosPosting.CompleteSale(doc, session.Id, tenders, Actor, NoCapture);
        Assert.Equal(998m, StockQty(db));

        var original = await repo.GetOriginalBillAsync(CompanyCode, posted.DocNo);
        Assert.NotNull(original);

        // A later receipt voucher may also be allocated to this invoice.
        // A POS refund must reduce only the original at-posting allocation,
        // never consume or delete this external receipt allocation.
        long externalPaymentLedgerId;
        using (var connection = new SqliteConnection(db.ConnectionString))
        {
            connection.Open();
            using var tx = connection.BeginTransaction();
            externalPaymentLedgerId = PartyLedger.WriteEntry(
                connection, tx, "SQLite", CompanyCode, "C", 7,
                "RCV", null, "RCV-EXT-1", "2026-03-10",
                0m, 25m, "SAR", "External receipt allocated after sale",
                exchangeRate: 1m);
            using var allocation = connection.CreateCommand();
            allocation.Transaction = tx;
            allocation.CommandText = """
                INSERT INTO payment_allocations
                    (allocation_id, company_id, party_kind, party_id, payment_ledger_id,
                     doc_table, doc_id, doc_no, amount, allocated_by, note)
                VALUES ($id, $company, 'C', 7, $payment,
                        'SALES', $doc, $docNo, 25, $actor, 'External receipt allocation');
                """;
            allocation.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
            allocation.Parameters.AddWithValue("$company", CompanyCode);
            allocation.Parameters.AddWithValue("$payment", externalPaymentLedgerId);
            allocation.Parameters.AddWithValue("$doc", posted.DocId);
            allocation.Parameters.AddWithValue("$docNo", posted.DocNo);
            allocation.Parameters.AddWithValue("$actor", Actor);
            allocation.ExecuteNonQuery();
            tx.Commit();
        }

        var returned = PosPosting.CreatePosReturn(original!, new Dictionary<int, decimal> { [1] = 1m },
            cashRefund: 50m, cardRefund: 0m, session.Id, "customer changed mind", Actor, NoCapture);
        Assert.Equal(InvDocStatus.Posted, returned.Status);
        Assert.Equal(999m, StockQty(db)); // one unit back in stock

        // The sale/return pair nets to zero for the refunded amount.
        // The separate 25 receipt credit and its allocation remain intact.
        Assert.Equal(150m, ScalarDecimal(db, "SELECT COALESCE(SUM(debit),0) FROM party_ledger WHERE party_id = 7"));
        Assert.Equal(175m, ScalarDecimal(db, "SELECT COALESCE(SUM(credit),0) FROM party_ledger WHERE party_id = 7"));
        Assert.Equal(50m, ScalarDecimal(db, $"SELECT paid_amount FROM sales_documents WHERE doc_id = {posted.DocId}"));
        Assert.Equal(50m, ScalarDecimal(db, $"""
            SELECT COALESCE(SUM(pa.amount), 0)
            FROM payment_allocations pa
            JOIN party_ledger pl ON pl.ledger_id = pa.payment_ledger_id
            WHERE pa.company_id = '{CompanyCode}' AND pa.doc_id = {posted.DocId}
              AND pl.doc_id = {posted.DocId} AND pl.doc_kind = 'RCV';
            """));
        Assert.Equal(25m, ScalarDecimal(db,
            $"SELECT amount FROM payment_allocations WHERE payment_ledger_id = {externalPaymentLedgerId}"));

        // Refund tender recorded negative on the return, in the session.
        var refundTenders = await repo.GetTendersForDocAsync(CompanyCode, returned.DocId, "SRT");
        Assert.Equal(-50m, refundTenders.Single().Amount);

        // Only 1 of 2 remains returnable: returning 2 more is POS-1006.
        var original2 = await repo.GetOriginalBillAsync(CompanyCode, posted.DocNo);
        Assert.Equal(1m, original2!.Lines[0].QtyAlreadyReturned);
        var error = Assert.Throws<AppError>(() => PosPosting.CreatePosReturn(original2,
            new Dictionary<int, decimal> { [1] = 2m }, 100m, 0m, session.Id, "too many", Actor, NoCapture));
        Assert.Equal(ErrorCodes.PosReturnExceedsSold, error.Code);

        // Z close: expected = 200 opening + 100 cash sale - 50 cash refund = 250.
        var z = await service.CloseSessionAsync(CompanyCode, session.Id, countedCash: 240m, Actor);
        Assert.Equal(250m, z.ExpectedCash);
        Assert.Equal(-10m, z.Variance);
        Assert.Equal(1, z.SalesCount);
        Assert.Equal(1, z.ReturnsCount);
        Assert.Equal(50m, z.CashRefunded);
    }

    [Fact]
    public async Task PartialReturn_MissingOriginalSettlementAllocation_RollsBackEntirePosting()
    {
        using var db = await SeededAsync();
        var repo = new PosRepository("SQLite", db.ConnectionString);
        var service = new PosService(repo);
        var session = await service.OpenSessionAsync(CompanyCode, "T1", "Cashier", 1, "SAR", 200m, Actor);

        var sale = BuildSale(qty: 2m, price: 50m, partyId: 7);
        var tenders = new List<PosTender>
        {
            new() { TenderType = PosTenderTypes.Cash, Amount = 100m, TenderedAmount = 100m }
        };
        var posted = PosPosting.CompleteSale(sale, session.Id, tenders, Actor, NoCapture);
        Assert.Equal(998m, StockQty(db));

        // Simulate inconsistent legacy data: the at-posting party-ledger
        // settlement exists, but its allocation was removed. The return
        // must fail closed and roll back stock, GL, party ledger and tenders.
        using (var connection = new SqliteConnection(db.ConnectionString))
        {
            connection.Open();
            using var delete = connection.CreateCommand();
            delete.CommandText = """
                DELETE FROM payment_allocations
                WHERE company_id=$company AND doc_table='SALES' AND doc_id=$doc
                  AND payment_ledger_id IN (
                      SELECT ledger_id FROM party_ledger
                      WHERE company_id=$company AND doc_id=$doc AND doc_kind='RCV');
                """;
            delete.Parameters.AddWithValue("$company", CompanyCode);
            delete.Parameters.AddWithValue("$doc", posted.DocId);
            Assert.Equal(1, delete.ExecuteNonQuery());
        }

        var original = await repo.GetOriginalBillAsync(CompanyCode, posted.DocNo);
        Assert.NotNull(original);
        var error = Assert.Throws<InvalidOperationException>(() =>
            PosPosting.CreatePosReturn(original!, new Dictionary<int, decimal> { [1] = 1m },
                cashRefund: 50m, cardRefund: 0m, session.Id, "missing allocation", Actor, NoCapture));
        Assert.Contains("allocation", error.Message.ToLowerInvariant());

        Assert.Equal(998m, StockQty(db));
        Assert.Equal(100m, ScalarDecimal(db,
            $"SELECT paid_amount FROM sales_documents WHERE doc_id = {posted.DocId}"));
        Assert.Equal(0m, ScalarDecimal(db, """
            SELECT COUNT(*) FROM journal_entries
            WHERE source_doc_type='SRT' AND status=1;
            """));
        Assert.Equal(0m, ScalarDecimal(db, """
            SELECT COUNT(*) FROM pos_sale_tenders WHERE doc_kind='SRT';
            """));
        Assert.Equal(1m, ScalarDecimal(db, """
            SELECT COUNT(*) FROM sales_documents
            WHERE doc_kind='SRT' AND status='Draft';
            """));
    }

    // ------------------------------------------------------------------
    // W9: paid-outs, exchanges, discount limits, performance
    // ------------------------------------------------------------------

    [Fact]
    public async Task PaidOut_ReducesExpectedCash_AndCloseVariance_ClosedSessionRefused()
    {
        using var db = await SeededAsync();
        var repo = new PosRepository("SQLite", db.ConnectionString);
        var service = new PosService(repo);
        var session = await service.OpenSessionAsync(CompanyCode, "T1", "Cashier", 1, "SAR", 200m, Actor);

        var doc = BuildSale(qty: 1m, price: 100m, partyId: null);
        var tenders = new List<PosTender> { new() { TenderType = PosTenderTypes.Cash, Amount = 100m, TenderedAmount = 100m } };
        PosPosting.CompleteSale(doc, session.Id, tenders, Actor, NoCapture);

        var expense = await service.RecordSessionExpenseAsync(CompanyCode, session.Id, 30m, "Cleaning supplies", "RCPT-9", Actor);
        Assert.StartsWith("EXP-", expense.ExpenseNo);
        Assert.Equal(30m, Assert.Single(await repo.GetSessionExpensesAsync(CompanyCode, session.Id)).Amount);

        // Expected cash: 200 opening + 100 cash sale - 30 paid-out = 270.
        var z = await service.GetZReportAsync(CompanyCode, session.Id);
        Assert.Equal(1, z.PaidOutsCount);
        Assert.Equal(30m, z.PaidOutsTotal);
        Assert.Equal(270m, z.ExpectedCash);

        var zero = await Assert.ThrowsAsync<AppError>(() =>
            service.RecordSessionExpenseAsync(CompanyCode, session.Id, 0m, "Free", null, Actor));
        Assert.Equal(ErrorCodes.PosSessionStateInvalid, zero.Code);
        var noReason = await Assert.ThrowsAsync<AppError>(() =>
            service.RecordSessionExpenseAsync(CompanyCode, session.Id, 5m, " ", null, Actor));
        Assert.Equal(ErrorCodes.PosSessionStateInvalid, noReason.Code);

        var closed = await service.CloseSessionAsync(CompanyCode, session.Id, countedCash: 270m, Actor);
        Assert.Equal(270m, closed.ExpectedCash);
        Assert.Equal(0m, closed.Variance);

        var afterClose = await Assert.ThrowsAsync<AppError>(() =>
            service.RecordSessionExpenseAsync(CompanyCode, session.Id, 5m, "Late", null, Actor));
        Assert.Equal(ErrorCodes.PosSessionStateInvalid, afterClose.Code);
    }

    [Fact]
    public async Task Exchange_PositiveNet_ReturnCreditCoversPart_CashSettlesDifference()
    {
        using var db = await SeededAsync();
        var repo = new PosRepository("SQLite", db.ConnectionString);
        var service = new PosService(repo);
        var session = await service.OpenSessionAsync(CompanyCode, "T1", "Cashier", 1, "SAR", 200m, Actor);

        var doc = BuildSale(qty: 2m, price: 50m, partyId: null);
        var tenders = new List<PosTender> { new() { TenderType = PosTenderTypes.Cash, Amount = 100m, TenderedAmount = 100m } };
        var posted = PosPosting.CompleteSale(doc, session.Id, tenders, Actor, NoCapture);
        Assert.Equal(998m, StockQty(db));

        var original = await repo.GetOriginalBillAsync(CompanyCode, posted.DocNo);
        Assert.NotNull(original);
        var newSale = BuildSale(qty: 1m, price: 80m, partyId: null);
        var netTenders = new List<PosTender> { new() { TenderType = PosTenderTypes.Cash, Amount = 30m, TenderedAmount = 30m } };
        var result = PosPosting.CompleteExchange(original!, new Dictionary<int, decimal> { [1] = 1m },
            newSale, netTenders, cashRefund: 0m, cardRefund: 0m, session.Id, "wrong size", Actor, NoCapture);

        Assert.Equal(InvDocStatus.Posted, result.ReturnDoc.Status);
        Assert.Equal(InvDocStatus.Posted, result.SaleDoc.Status);
        Assert.Equal(50m, result.ReturnDoc.GrandTotal);
        Assert.Equal(80m, result.SaleDoc.GrandTotal);
        Assert.Equal(30m, result.Net);
        Assert.False(string.IsNullOrWhiteSpace(result.ExchangeRef));
        // Both documents carry the same exchange link.
        Assert.Equal(result.ExchangeRef, ScalarString(db, $"SELECT exchange_ref FROM sales_documents WHERE doc_id = {result.ReturnDoc.DocId}"));
        Assert.Equal(result.ExchangeRef, ScalarString(db, $"SELECT exchange_ref FROM sales_documents WHERE doc_id = {result.SaleDoc.DocId}"));
        // Stock: -2 sold +1 returned -1 exchanged-in = 998.
        Assert.Equal(998m, StockQty(db));
        // The return's credit leg and the sale's receivable leg cancel:
        // account 1201 nets to zero across every posted entry.
        Assert.Equal(0m, ScalarDecimal(db, """
            SELECT COALESCE(SUM(l.debit),0) - COALESCE(SUM(l.credit),0)
            FROM journal_entry_lines l
            JOIN chart_of_accounts a ON a.account_id = l.account_id
            JOIN journal_entries e ON e.journal_entry_id = l.journal_entry_id
            WHERE a.account_code = '1201' AND e.status = 1;
            """));
        // The sale was tendered: exchange credit 50 on-account + 30 cash.
        var saleTenders = await repo.GetTendersForDocAsync(CompanyCode, result.SaleDoc.DocId, "POS");
        Assert.Equal(50m, saleTenders.Single(t => t.TenderType == PosTenderTypes.OnAccount).Amount);
        Assert.Equal(30m, saleTenders.Single(t => t.TenderType == PosTenderTypes.Cash).Amount);
        // Drawer: 200 opening + 100 original cash + 30 net cash = 330.
        var z = await service.GetZReportAsync(CompanyCode, session.Id);
        Assert.Equal(330m, z.ExpectedCash);
    }

    [Fact]
    public async Task Exchange_NegativeNet_RefundsOnlyTheExcess()
    {
        using var db = await SeededAsync();
        var repo = new PosRepository("SQLite", db.ConnectionString);
        var service = new PosService(repo);
        var session = await service.OpenSessionAsync(CompanyCode, "T1", "Cashier", 1, "SAR", 0m, Actor);

        var doc = BuildSale(qty: 1m, price: 100m, partyId: null);
        var tenders = new List<PosTender> { new() { TenderType = PosTenderTypes.Cash, Amount = 100m, TenderedAmount = 100m } };
        var posted = PosPosting.CompleteSale(doc, session.Id, tenders, Actor, NoCapture);

        var original = await repo.GetOriginalBillAsync(CompanyCode, posted.DocNo);
        Assert.NotNull(original);
        var newSale = BuildSale(qty: 1m, price: 60m, partyId: null);
        var result = PosPosting.CompleteExchange(original!, new Dictionary<int, decimal> { [1] = 1m },
            newSale, new List<PosTender>(), cashRefund: 40m, cardRefund: 0m, session.Id, "downgrade", Actor, NoCapture);

        Assert.Equal(-40m, result.Net);
        Assert.Equal(InvDocStatus.Posted, result.SaleDoc.Status);
        // Return tenders: 40 cash back out, 60 consumed as exchange credit.
        var returnTenders = await repo.GetTendersForDocAsync(CompanyCode, result.ReturnDoc.DocId, "SRT");
        Assert.Equal(-40m, returnTenders.Single(t => t.TenderType == PosTenderTypes.Cash).Amount);
        Assert.Equal(-60m, returnTenders.Single(t => t.TenderType == PosTenderTypes.OnAccount).Amount);
        // Drawer: 0 opening + 100 in - 40 refunded = 60.
        var z = await service.GetZReportAsync(CompanyCode, session.Id);
        Assert.Equal(60m, z.ExpectedCash);
        Assert.Equal(999m, StockQty(db));
    }

    [Fact]
    public void DiscountMath_Measure_Ceiling_AndOverrideGate()
    {
        var measurement = PosDiscountMath.Measure(
            new[] { (2m, 100m, 10m), (1m, 50m, 0m) }, billDiscountAmount: 10m);
        Assert.Equal(250m, measurement.GrossSubtotal);
        Assert.Equal(30m, measurement.DiscountAmount); // 20 line + 10 bill
        Assert.Equal(12m, measurement.EffectivePct);
        Assert.Equal(10m, measurement.MaxLinePct);
        Assert.Equal(12m, measurement.BindingPct);

        Assert.Equal(0m, PosDiscountMath.Ceiling(hasOverridePermission: false, numericLimitPct: 50m));
        Assert.Equal(100m, PosDiscountMath.Ceiling(hasOverridePermission: true, numericLimitPct: null));
        Assert.Equal(10m, PosDiscountMath.Ceiling(hasOverridePermission: true, numericLimitPct: 10m));
        Assert.True(PosDiscountMath.RequiresOverride(measurement, 10m));
        Assert.False(PosDiscountMath.RequiresOverride(measurement, 12m));
        Assert.False(PosDiscountMath.RequiresOverride(
            PosDiscountMath.Measure(new[] { (1m, 100m, 0m) }, 0m), 0m));

        Assert.Equal(30m, PosTenderMath.ExchangeNet(80m, 50m));
        Assert.Equal(-40m, PosTenderMath.ExchangeNet(60m, 100m));
        Assert.Equal(270m, PosTenderMath.ExpectedCash(200m,
            new[] { new PosTender { TenderType = PosTenderTypes.Cash, Amount = 100m } }, paidOutsTotal: 30m));
    }

    [Fact]
    public async Task DiscountLimit_UserRowBeatsRoleRow_UnknownUserUncapped()
    {
        using var db = await SeededAsync();
        var repo = new PosRepository("SQLite", db.ConnectionString);
        using (var connection = new SqliteConnection(db.ConnectionString))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO users (user_id, company_id, username, password_hash, password_salt, full_name, full_name_ar, full_name_en)
                VALUES ('u-cashier', $guid, 'cashier1', 'x', 'y', 'Cashier One', 'Cashier One', 'Cashier One');
                INSERT INTO roles (role_id, company_id, role_code, role_name) VALUES (777, $guid, 'ROLE_CASHIER', 'Cashier');
                INSERT INTO user_roles (user_id, role_id) VALUES ('u-cashier', 777);
                """;
            cmd.Parameters.AddWithValue("$guid", db.CompanyId.ToString("D"));
            cmd.ExecuteNonQuery();
        }

        await repo.SaveDiscountLimitAsync(CompanyCode, PosDiscountLimitSubjects.Role, "ROLE_CASHIER", 10m, Actor);
        Assert.Equal(10m, await repo.GetDiscountLimitPctAsync(CompanyCode, "cashier1"));
        await repo.SaveDiscountLimitAsync(CompanyCode, PosDiscountLimitSubjects.User, "cashier1", 25m, Actor);
        Assert.Equal(25m, await repo.GetDiscountLimitPctAsync(CompanyCode, "cashier1"));
        Assert.Null(await repo.GetDiscountLimitPctAsync(CompanyCode, "ghost"));
        Assert.Null(await repo.GetDiscountLimitPctAsync("OTHER", "cashier1"));
    }

    [Fact]
    public async Task SupervisorGuard_VerifiesPasswordAndOverrideRight_ThenAudits()
    {
        using var db = await SeededAsync();
        var hash = PwdHash.Create("boss-pass");
        using (var connection = new SqliteConnection(db.ConnectionString))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO users (user_id, company_id, username, password_hash, password_salt, full_name, full_name_ar, full_name_en)
                VALUES ('u-boss', $guid, 'boss', $hash, $salt, 'Boss', 'Boss', 'Boss');
                INSERT INTO users (user_id, company_id, username, password_hash, password_salt, full_name, full_name_ar, full_name_en)
                VALUES ('u-clerk', $guid, 'clerk', $hash, $salt, 'Clerk', 'Clerk', 'Clerk');
                INSERT INTO user_permissions (user_id, permission_id, effect)
                SELECT 'u-boss', permission_id, 1 FROM permissions WHERE permission_code = 'POS.DISCOUNT.OVERRIDE';
                """;
            cmd.Parameters.AddWithValue("$guid", db.CompanyId.ToString("D"));
            cmd.Parameters.AddWithValue("$hash", hash.Hash);
            cmd.Parameters.AddWithValue("$salt", hash.Salt);
            cmd.ExecuteNonQuery();
        }

        Assert.True(PosDiscountGuard.VerifySupervisor("boss", "boss-pass"));
        Assert.False(PosDiscountGuard.VerifySupervisor("boss", "wrong-pass"));
        Assert.False(PosDiscountGuard.VerifySupervisor("clerk", "boss-pass")); // right password, no override right
        Assert.False(PosDiscountGuard.VerifySupervisor("ghost", "boss-pass"));

        var measurement = PosDiscountMath.Measure(new[] { (1m, 200m, 20m) }, 0m);
        PosDiscountGuard.RecordOverride("POS", "POS-TEST-1", 42,
            new PosDiscountApproval("cashier1", "boss", 10m, measurement));
        Assert.Equal(1m, ScalarDecimal(db, "SELECT COUNT(*) FROM pos_discount_overrides WHERE approved_by = 'boss' AND doc_no = 'POS-TEST-1'"));
        Assert.Equal(40m, ScalarDecimal(db, "SELECT discount_amount FROM pos_discount_overrides WHERE doc_no = 'POS-TEST-1'"));
    }

    [Fact]
    public async Task Performance_GroupsSessionsByTerminalAndCashier()
    {
        using var db = await SeededAsync();
        var repo = new PosRepository("SQLite", db.ConnectionString);
        var service = new PosService(repo);
        var session = await service.OpenSessionAsync(CompanyCode, "T1", "Cashier", 1, "SAR", 200m, Actor);
        var doc = BuildSale(qty: 1m, price: 100m, partyId: null);
        var tenders = new List<PosTender> { new() { TenderType = PosTenderTypes.Cash, Amount = 100m, TenderedAmount = 100m } };
        PosPosting.CompleteSale(doc, session.Id, tenders, Actor, NoCapture);
        await service.RecordSessionExpenseAsync(CompanyCode, session.Id, 10m, "Till supplies", null, Actor);

        var rows = await repo.GetPerformanceAsync(CompanyCode, new DateTime(2026, 1, 1), new DateTime(2027, 1, 1), null, null);
        var row = Assert.Single(rows);
        Assert.Equal("T1", row.TerminalCode);
        Assert.Equal("Cashier", row.Cashier);
        Assert.Equal(1, row.SalesCount);
        Assert.Equal(100m, row.GrossSales);
        Assert.Equal(0m, row.ReturnsTotal);
        Assert.Equal(10m, row.PaidOuts);
        Assert.Equal(100m, row.AverageBasket);

        Assert.Empty(await repo.GetPerformanceAsync(CompanyCode, new DateTime(2026, 1, 1), new DateTime(2027, 1, 1), "T2", null));
        Assert.Single(await repo.GetPerformanceAsync(CompanyCode, new DateTime(2026, 1, 1), new DateTime(2027, 1, 1), null, "Cashier"));
    }

    // ------------------------------------------------------------------
    // Helpers (GlPostingTests shape)
    // ------------------------------------------------------------------

    private static InvDoc BuildSale(decimal qty, decimal price, long? partyId) => new()
    {
        Kind = InvDocKind.PosSale,
        DocNo = InvRepo.NextDocNo(InvDocKind.PosSale),
        DocDate = new DateTime(2026, 3, 10),
        PartyId = partyId,
        PartyCode = partyId is null ? string.Empty : "CUST-1",
        PartyName = partyId is null ? string.Empty : "Customer One",
        WarehouseId = 1,
        CurrencyCode = "SAR",
        Lines = { new InvLine { LineNo = 1, MaterialId = 42, MaterialCode = "MAT-42", Description = "Item", Unit = "pcs", Qty = qty, UnitPrice = price, TaxRate = 0m, WarehouseId = 1 } }
    };

    private static decimal StockQty(TestDb db) => ScalarDecimal(db,
        "SELECT available_qty FROM material_stock_balances WHERE company_id = 'POS1' AND material_id = 42 AND warehouse_id = 1");

    private sealed record EntryRow(List<(string Code, decimal Debit, decimal Credit)> Lines);

    private static EntryRow SingleEntry(TestDb db, string sourceType, object sourceId)
    {
        using var connection = new SqliteConnection(db.ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT e.journal_entry_id FROM journal_entries e
            WHERE e.source_doc_type = $type AND e.source_doc_id = $source AND e.status = 1 AND e.reversal_of_entry_id IS NULL;
            """;
        cmd.Parameters.AddWithValue("$type", sourceType);
        cmd.Parameters.AddWithValue("$source", Convert.ToString(sourceId, CultureInfo.InvariantCulture));
        string id;
        using (var reader = cmd.ExecuteReader())
        {
            Assert.True(reader.Read(), $"No posted GL entry for {sourceType}/{sourceId}");
            id = reader.GetString(0);
        }
        var lines = new List<(string, decimal, decimal)>();
        using var linesCmd = connection.CreateCommand();
        linesCmd.CommandText = """
            SELECT a.account_code, l.debit, l.credit FROM journal_entry_lines l
            JOIN chart_of_accounts a ON a.account_id = l.account_id
            WHERE l.journal_entry_id = $id ORDER BY l.line_number;
            """;
        linesCmd.Parameters.AddWithValue("$id", id);
        using var lr = linesCmd.ExecuteReader();
        while (lr.Read()) lines.Add((lr.GetString(0), lr.GetDecimal(1), lr.GetDecimal(2)));
        return new EntryRow(lines);
    }

    private static (string Code, decimal Debit, decimal Credit) Line(EntryRow entry, string code)
        => entry.Lines.Single(l => l.Code == code);

    private static void AssertBalanced(EntryRow entry)
        => Assert.Equal(entry.Lines.Sum(l => l.Debit), entry.Lines.Sum(l => l.Credit));

    private static decimal ScalarDecimal(TestDb db, string sql)
    {
        using var connection = new SqliteConnection(db.ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? 0m : Convert.ToDecimal(value, CultureInfo.InvariantCulture);
    }

    private static string? ScalarString(TestDb db, string sql)
    {
        using var connection = new SqliteConnection(db.ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private sealed class TestDb : IDisposable
    {
        public required string Root { get; init; }
        public required string DbPath { get; init; }
        public required Guid CompanyId { get; init; }
        public string ConnectionString => $"Data Source={DbPath};";
        public void Dispose()
        {
            AppState.ClearCompany();
            SqliteConnection.ClearAllPools();
            Directory.Delete(Root, recursive: true);
        }
    }

    private static async Task<TestDb> SeededAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "pos-build-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var dbPath = Path.Combine(root, "pos.db");
        var companyId = Guid.NewGuid();
        var connectionString = $"Data Source={dbPath};";

        var migration = await SchemaMigrator.ApplyAllAsync("SQLite", dbPath);
        Assert.True(migration.Success, migration.Message);
        using (var schemaProbe = new SqliteConnection(connectionString))
        {
            schemaProbe.Open();
            using var columnsCommand = schemaProbe.CreateCommand();
            columnsCommand.CommandText = "PRAGMA table_info('party_ledger');";
            using var columnsReader = columnsCommand.ExecuteReader();
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (columnsReader.Read())
                columns.Add(columnsReader.GetString(1));
            Assert.Contains("debit", columns);
            Assert.Contains("credit", columns);
        }
        PointAppAt(dbPath);
        AppState.SetCompany(CompanyCode, "POS Test Co", "POS Test Co");

        using (var connection = new SqliteConnection(connectionString))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "INSERT INTO companies (company_id, company_code, company_name_ar, company_name_en) VALUES ($id, $code, 'POS Test Co', 'POS Test Co');";
            cmd.Parameters.AddWithValue("$id", companyId.ToString("D"));
            cmd.Parameters.AddWithValue("$code", CompanyCode);
            cmd.ExecuteNonQuery();
        }

        var structure = new AccountingStructureRepository("SQLite", connectionString);
        await structure.CreateFiscalYearAsync(companyId, 2026, Actor);

        foreach (var (code, name) in new[]
        {
            ("131", "Cash"), ("132", "Bank"), ("1201", "Receivable"), ("1203", "Inventory"),
            ("2201", "Payable"), ("301", "Sales"), ("302", "Sales Returns"), ("401", "COGS"),
            ("402", "Purchases"), ("403", "Purchase Returns")
        })
        {
            AccRepo.Save(null, new AccRepo.AccRec(
                string.Empty, code, null, name, name, "Postable", "Active", "Low", "Unspecified",
                false, false, false, false, false, false, 0m, string.Empty, 0m, 0m, 0m),
                renumberDescendants: false);
        }

        using (var connection = new SqliteConnection(connectionString))
        {
            connection.Open();
            void Exec(string sql)
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
            Exec($"INSERT INTO materials (material_id, company_id, code, name_ar, name_en, is_stocked, avg_cost, is_deleted) VALUES (42, '{CompanyCode}', 'MAT-42', 'Item', 'Item', 1, 40, 0);");
            Exec($"INSERT INTO warehouses (warehouse_id, company_id, warehouse_code, warehouse_name_ar, warehouse_name_en, is_deleted) VALUES (1, '{CompanyCode}', 1, 'Main', 'Main', 0);");
            Exec($"INSERT INTO material_stock_balances (company_id, warehouse_id, material_id, available_qty, average_cost, last_cost) VALUES ('{CompanyCode}', 1, 42, 1000, 40, 40);");
        }

        return new TestDb { Root = root, DbPath = dbPath, CompanyId = companyId };
    }

    private static void PointAppAt(string sqlitePath)
    {
        var settings = AppCfgSvc.Current;
        settings.ConnectionStrings["Default"] = new ConnectionStringSettings
        {
            Provider = "SQLite",
            ConnectionString = sqlitePath
        };
        settings.Database ??= new DatabaseSettings();
        settings.Database.DefaultProvider = "SQLite";
    }
}
