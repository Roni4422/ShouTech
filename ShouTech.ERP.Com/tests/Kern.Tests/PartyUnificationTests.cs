using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
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
/// Fix Phase Step 4 (party unification + settlement chain, migration 061):
/// - a customer created in the card's store (parties) resolves in the
///   operational invoice lookup (TaxSvc) — the scenario that failed while
///   two party masters existed (sweep C-1);
/// - balance resolution is party-exact and company-scoped: the sweep's
///   proven failing case (code "1" inheriting a stranger's 777 debit) and
///   the credit guard's cross-company fallback (G-1) stay dead;
/// - a cash receipt voucher settles party_ledger inside the journal
///   posting transaction, PayRecWin's repository sees it as a real
///   payment, allocation settles the invoice's paid_amount, and journal
///   reversal mirrors the settlement symmetrically (B-03);
/// - an invoice collected at posting (incl. a POS cash sale) writes its
///   settlement entry + allocation in the posting transaction, so the
///   party balance falls by exactly the collected amount;
/// - migration 061 leaves exactly one party master.
/// Execution is owed on Windows (net8.0-windows testhost).
/// </summary>
public sealed class PartyUnificationTests
{
    private const string CompanyCode = "PU1";
    private const string Actor = "party-tests";

    // ------------------------------------------------------------ (a) card -> invoiceable

    [Fact]
    public async Task CardParty_ResolvesInOperationalLookup()
    {
        using var db = await SeededAsync();
        var party = await SavePartyAsync(db, "1", "One Trading", creditLimit: 0m);

        var resolved = TaxSvc.ResolveParty("1", customer: true);

        Assert.NotNull(resolved);
        Assert.Equal(party.LedgerId, resolved!.Value.Id);
        Assert.Equal("One Trading", resolved.Value.Name);
        // A code that was never a party does not resolve to anything —
        // and the numeric code "1" resolves to THIS party's ledger id,
        // not to the number 1.
        Assert.Null(TaxSvc.ResolveParty("9999", customer: true));
    }

    // ------------------------------------------------------------ (b) balance exactness

    [Fact]
    public async Task Balance_IsPartyExact_AndCompanyScoped()
    {
        using var db = await SeededAsync();
        var party = await SavePartyAsync(db, "1", "One Trading", creditLimit: 700m);
        await SavePartyAsync(db, "CUST-2", "Two Trading", creditLimit: 0m);

        // The sweep's proven failing case: a stranger's 777 debit sitting
        // in the ledger under a different party id, plus this party's own
        // rows in ANOTHER company — neither may leak into the balance.
        Exec(db, $"INSERT INTO party_ledger (company_id, party_kind, party_id, doc_kind, entry_date, debit, credit) VALUES ('{CompanyCode}', 'C', {party.LedgerId + 500}, 'SAL', '2026-01-01', 777, 0);");
        Exec(db, $"INSERT INTO party_ledger (company_id, party_kind, party_id, doc_kind, entry_date, debit, credit) VALUES ('OTHER', 'C', {party.LedgerId}, 'SAL', '2026-01-01', 555, 0);");
        Exec(db, $"INSERT INTO party_ledger (company_id, party_kind, party_id, doc_kind, entry_date, debit, credit) VALUES ('{CompanyCode}', 'C', {party.LedgerId}, 'SAL', '2026-01-01', 100, 0);");

        var repo = new MasterCardRepository("SQLite", db.ConnectionString);
        var balance = await repo.GetPartyBalanceAsync(CompanyCode, "C", "1");
        Assert.Equal(100m, balance.Debit);
        Assert.Equal(0m, balance.Credit);
        Assert.Equal(100m, balance.Balance);

        // Credit guard: PU1 balance 100 + 100 new = 200 <= 700 passes;
        // had the OTHER-company 555 leaked in, it would block. A 650
        // document then exceeds the real limit and blocks (ACC-5006).
        using var connection = new SqliteConnection(db.ConnectionString);
        connection.Open();
        using var tx = connection.BeginTransaction();
        PartyCreditGuard.EnsureWithinLimit(connection, tx, "SQLite", CompanyCode, "C", party.LedgerId, "1", 100m, "$");
        var error = Assert.Throws<AppError>(() =>
            PartyCreditGuard.EnsureWithinLimit(connection, tx, "SQLite", CompanyCode, "C", party.LedgerId, "1", 650m, "$"));
        Assert.Equal(ErrorCodes.CreditLimitExceeded, error.Code);
        tx.Rollback();
    }

    // ------------------------------------------------------------ (c)+(d) settlement chain

    [Fact]
    public async Task CashReceipt_SettlesLedger_Allocates_AndReversalRestores()
    {
        using var db = await SeededAsync();
        var party = await SavePartyAsync(db, "CUST-1", "Settlement Customer", creditLimit: 0m);

        // Posted sales invoice 1000 + its ledger debit (the InvRepo shape).
        var docId = (long)ExecScalar(db,
            $"INSERT INTO sales_documents (company_id, doc_kind, doc_no, doc_date, party_id, party_name, currency_code, grand_total, paid_amount, status) VALUES ('{CompanyCode}', 'SAL', 'SAL-1', '2026-03-01', {party.LedgerId}, 'Settlement Customer', 'SAR', 1000, 0, 'Posted'); SELECT last_insert_rowid();");
        Exec(db, $"INSERT INTO party_ledger (company_id, party_kind, party_id, doc_kind, doc_id, doc_no, entry_date, debit, credit, currency_code) VALUES ('{CompanyCode}', 'C', {party.LedgerId}, 'SAL', {docId}, 'SAL-1', '2026-03-01', 1000, 0, 'SAR');");

        var journalRepo = new JournalRepository("SQLite", db.ConnectionString);
        var journal = new JournalService(journalRepo);
        var cashId = await journalRepo.ResolveAccountIdAsync(db.CompanyId, "131");
        var receivableId = await journalRepo.ResolveAccountIdAsync(db.CompanyId, "1201");

        async Task<Guid> ReceiptAsync(string docNo, decimal amount)
        {
            var id = await journal.CreateDraftAsync(db.CompanyId, new DateOnly(2026, 3, 5),
                $"Receipt {docNo}", Actor,
                new[]
                {
                    new JournalLineRequest(cashId, amount, 0m),
                    new JournalLineRequest(receivableId, 0m, amount)
                },
                documentNumber: docNo);
            Exec(db, $"INSERT INTO voucher_party_links (journal_entry_id, company_id, party_kind, party_id, party_code, doc_kind, doc_no, amount, currency_code, created_by) VALUES ('{id:D}', '{CompanyCode}', 'C', {party.LedgerId}, 'CUST-1', 'RCV', '{docNo}', {amount.ToString(CultureInfo.InvariantCulture)}, 'SAR', '{Actor}');");
            await journal.PostAsync(id, Actor);
            return id;
        }

        var masterRepo = new MasterCardRepository("SQLite", db.ConnectionString);
        var recon = new ReconciliationRepository("SQLite", db.ConnectionString);

        // Receipt 1 (400): posts -> ledger settlement exists, balance falls.
        await ReceiptAsync("RCV-1", 400m);
        var afterFirst = await masterRepo.GetPartyBalanceAsync(CompanyCode, "C", "CUST-1");
        Assert.Equal(600m, afterFirst.Balance);
        Assert.Equal(1m, ExecScalar(db,
            $"SELECT COUNT(*) FROM party_ledger WHERE company_id = '{CompanyCode}' AND party_kind = 'C' AND party_id = {party.LedgerId} AND doc_kind = 'RCV' AND credit = 400 AND journal_entry_id IS NOT NULL;"));

        // PayRecWin sees the real payment; allocation settles the invoice.
        var payments = await recon.GetPaymentsAsync(CompanyCode, "C");
        var payment = Assert.Single(payments, p => p.DocNo == "RCV-1");
        Assert.Equal("Settlement Customer", payment.PartyName);
        Assert.Equal(400m, payment.Available);
        var open = await recon.GetOpenDocumentsAsync(CompanyCode, "C");
        var openDoc = Assert.Single(open, d => d.DocNo == "SAL-1");
        Assert.Equal(1000m, openDoc.OpenAmount);

        await recon.AllocateAsync(CompanyCode, "C", payment.LedgerId, "SALES", docId, 400m, Actor);
        Assert.Equal(400m, ExecScalar(db, $"SELECT paid_amount FROM sales_documents WHERE doc_id = {docId};"));
        var afterAlloc = await recon.GetOpenDocumentsAsync(CompanyCode, "C");
        Assert.Equal(600m, Assert.Single(afterAlloc, d => d.DocNo == "SAL-1").OpenAmount);

        // Receipt 2 (200), then reversed: the ledger mirror restores the
        // balance exactly, while receipt 1's allocation stays settled.
        var second = await ReceiptAsync("RCV-2", 200m);
        Assert.Equal(400m, (await masterRepo.GetPartyBalanceAsync(CompanyCode, "C", "CUST-1")).Balance);
        await journal.ReverseAsync(second, Actor);
        Assert.Equal(600m, (await masterRepo.GetPartyBalanceAsync(CompanyCode, "C", "CUST-1")).Balance);
        Assert.Equal(400m, ExecScalar(db, $"SELECT paid_amount FROM sales_documents WHERE doc_id = {docId};"));
    }

    [Fact]
    public async Task SupplierPayment_IsAllocatedToPostedPurchaseInvoice()
    {
        using var db = await SeededAsync();
        var supplierRepo = new MasterCardRepository("SQLite", db.ConnectionString);
        var supplier = new Party { Kind = "S", Code = "SUP-1", NameEn = "Settlement Supplier", CreditLimit = 0m };
        await supplierRepo.SavePartyAsync(CompanyCode, supplier, Actor);
        Assert.True(supplier.LedgerId > 0);

        var purchaseId = (long)ExecScalar(db,
            $"INSERT INTO purchase_documents (company_id, doc_kind, doc_no, doc_date, party_id, party_name, currency_code, grand_total, paid_amount, status) VALUES ('{CompanyCode}', 'PUR', 'PUR-SUP-1', '2026-03-01', {supplier.LedgerId}, 'Settlement Supplier', 'SAR', 1000, 0, 'Posted'); SELECT last_insert_rowid();");
        Exec(db,
            $"INSERT INTO party_ledger (company_id, party_kind, party_id, doc_kind, doc_id, doc_no, entry_date, debit, credit, currency_code) VALUES ('{CompanyCode}', 'S', {supplier.LedgerId}, 'PUR', {purchaseId}, 'PUR-SUP-1', '2026-03-01', 0, 1000, 'SAR');");

        var journalRepo = new JournalRepository("SQLite", db.ConnectionString);
        var journal = new JournalService(journalRepo);
        var cashId = await journalRepo.ResolveAccountIdAsync(db.CompanyId, "131");
        var payableId = await journalRepo.ResolveAccountIdAsync(db.CompanyId, "2201");
        var wrongExpenseId = await journalRepo.ResolveAccountIdAsync(db.CompanyId, "401");
        var badPaymentId = await journal.CreateDraftAsync(db.CompanyId, new DateOnly(2026, 3, 4),
            "Invalid supplier payment", Actor,
            new[]
            {
                new JournalLineRequest(wrongExpenseId, 600m, 0m),
                new JournalLineRequest(cashId, 0m, 600m)
            },
            documentNumber: "PAY-SUP-BAD",
            partyLink: new VoucherPartyLinkRequest(CompanyCode, "S", supplier.LedgerId, "SUP-1", "PAY", "PAY-SUP-BAD", 600m, "SAR"));
        Assert.NotEqual(payableId, wrongExpenseId);
        Assert.Equal(1m, ExecScalar(db,
            $"SELECT COUNT(*) FROM voucher_party_links WHERE journal_entry_id = '{badPaymentId:D}'"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.PostAsync(badPaymentId, Actor));
        Assert.Equal(0m, ExecScalar(db,
            $"SELECT COUNT(*) FROM party_ledger WHERE company_id = '{CompanyCode}' AND journal_entry_id = '{badPaymentId:D}';"));

        var paymentId = await journal.CreateDraftAsync(db.CompanyId, new DateOnly(2026, 3, 5),
            "Supplier payment PAY-SUP-1", Actor,
            new[]
            {
                new JournalLineRequest(payableId, 600m, 0m),
                new JournalLineRequest(cashId, 0m, 600m)
            },
            documentNumber: "PAY-SUP-1",
            partyLink: new VoucherPartyLinkRequest(CompanyCode, "S", supplier.LedgerId, "SUP-1", "PAY", "PAY-SUP-1", 600m, "SAR"));
        await journal.PostAsync(paymentId, Actor);

        var recon = new ReconciliationRepository("SQLite", db.ConnectionString);
        var open = Assert.Single(await recon.GetOpenDocumentsAsync(CompanyCode, "S"));
        Assert.Equal("PUR-SUP-1", open.DocNo);
        Assert.Equal(1000m, open.OpenAmount);
        var payment = Assert.Single(await recon.GetPaymentsAsync(CompanyCode, "S"));
        Assert.Equal("PAY-SUP-1", payment.DocNo);
        Assert.Equal(600m, payment.Available);

        Exec(db, $"UPDATE purchase_documents SET currency_code = 'EUR' WHERE doc_id = {purchaseId};");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            recon.AllocateAsync(CompanyCode, "S", payment.LedgerId, "PURCHASE", purchaseId, 450m, Actor));
        Assert.Equal(0m, ExecScalar(db, $"SELECT paid_amount FROM purchase_documents WHERE doc_id = {purchaseId};"));
        Exec(db, $"UPDATE purchase_documents SET currency_code = 'SAR' WHERE doc_id = {purchaseId};");

        await recon.AllocateAsync(CompanyCode, "S", payment.LedgerId, "PURCHASE", purchaseId, 450m, Actor, "Partial supplier settlement");

        Assert.Equal(450m, ExecScalar(db, $"SELECT paid_amount FROM purchase_documents WHERE doc_id = {purchaseId};"));
        Assert.Equal(550m, Assert.Single(await recon.GetOpenDocumentsAsync(CompanyCode, "S")).OpenAmount);
        Assert.Equal(150m, Assert.Single(await recon.GetPaymentsAsync(CompanyCode, "S")).Available);
        Assert.Equal(1m, ExecScalar(db,
            $"SELECT COUNT(*) FROM payment_allocations WHERE company_id = '{CompanyCode}' AND payment_ledger_id = (SELECT ledger_id FROM party_ledger WHERE company_id = '{CompanyCode}' AND doc_kind = 'PAY' AND doc_no = 'PAY-SUP-1') AND doc_table = 'PURCHASE' AND doc_id = {purchaseId} AND amount = 450;"));

        // Reversing the payment must release its allocation and restore the
        // supplier invoice to fully open in the same reversal transaction.
        await journal.ReverseAsync(paymentId, Actor);
        Assert.Equal(0m, ExecScalar(db, $"SELECT paid_amount FROM purchase_documents WHERE doc_id = {purchaseId};"));
        Assert.Equal(0m, ExecScalar(db,
            $"SELECT COUNT(*) FROM payment_allocations WHERE company_id = '{CompanyCode}' AND payment_ledger_id = (SELECT ledger_id FROM party_ledger WHERE company_id = '{CompanyCode}' AND doc_kind = 'PAY' AND doc_no = 'PAY-SUP-1');"));
        Assert.Equal(1m, ExecScalar(db,
            $"SELECT COUNT(*) FROM payment_allocation_reversal_audit WHERE company_id = '{CompanyCode}' AND doc_table = 'PURCHASE' AND doc_id = {purchaseId} AND amount = 450 AND released_by = '{Actor}';"));
        Assert.Equal(1000m, Assert.Single(await recon.GetOpenDocumentsAsync(CompanyCode, "S")).OpenAmount);
        Assert.Empty(await recon.GetPaymentsAsync(CompanyCode, "S"));
    }

    // ------------------------------------------------------------ supplier advances (Y1, migration 110)

    private async Task<Party> SaveSupplierAsync(TestDb db, string code, string name)
    {
        var repo = new MasterCardRepository("SQLite", db.ConnectionString);
        var party = new Party { Kind = "S", Code = code, NameEn = name, CreditLimit = 0m };
        await repo.SavePartyAsync(CompanyCode, party, Actor);
        Assert.True(party.LedgerId > 0);
        return party;
    }

    private static long InsertPurchaseDoc(TestDb db, string kind, string docNo, long partyId, string partyName, decimal total)
        => (long)ExecScalar(db,
            $"INSERT INTO purchase_documents (company_id, doc_kind, doc_no, doc_date, party_id, party_name, currency_code, grand_total, paid_amount, status) VALUES ('{CompanyCode}', '{kind}', '{docNo}', '2026-03-01', {partyId}, '{partyName}', 'SAR', {total.ToString(CultureInfo.InvariantCulture)}, 0, 'Posted'); SELECT last_insert_rowid();");

    private void LinkPoToInvoice(TestDb db, long poId, long invoiceId)
        => Exec(db,
            $"INSERT INTO document_links (company_id, source_doc_kind, source_doc_id, target_doc_kind, target_doc_id, created_by) VALUES ('{db.CompanyId:D}', 'PO', {poId}, 'PUR', {invoiceId}, '{Actor}');");

    private static async Task<Guid> AdvanceVoucherAsync(TestDb db, JournalService journal, JournalRepository journalRepo, Party supplier, long poId, string docNo, decimal amount, string debitAccount = "1215")
    {
        var cashId = await journalRepo.ResolveAccountIdAsync(db.CompanyId, "131");
        var debitId = await journalRepo.ResolveAccountIdAsync(db.CompanyId, debitAccount);
        var id = await journal.CreateDraftAsync(db.CompanyId, new DateOnly(2026, 3, 5),
            $"Supplier advance {docNo}", Actor,
            new[]
            {
                new JournalLineRequest(debitId, amount, 0m),
                new JournalLineRequest(cashId, 0m, amount)
            },
            documentNumber: docNo);
        Exec(db,
            $"INSERT INTO voucher_party_links (journal_entry_id, company_id, party_kind, party_id, party_code, doc_kind, doc_no, amount, currency_code, created_by, po_doc_id, is_advance) VALUES ('{id:D}', '{CompanyCode}', 'S', {supplier.LedgerId}, '{supplier.Code}', 'PAY', '{docNo}', {amount.ToString(CultureInfo.InvariantCulture)}, 'SAR', '{Actor}', {poId}, 1);");
        await journal.PostAsync(id, Actor);
        return id;
    }

    private static decimal GlAccountNetDebit(TestDb db, string accountCode)
        => ExecScalar(db,
            $"SELECT COALESCE(SUM(l.debit - l.credit), 0) FROM journal_entry_lines l JOIN chart_of_accounts a ON a.account_id = l.account_id JOIN journal_entries j ON j.journal_entry_id = l.journal_entry_id WHERE a.account_code = '{accountCode}' AND j.status IN (1, 2);");

    [Fact]
    public async Task Advance_PostsDebitAdvances_NotPayable_AndNetsSupplierBalance()
    {
        using var db = await SeededAsync();
        var supplier = await SaveSupplierAsync(db, "SUP-A", "Advance Supplier");
        var poId = InsertPurchaseDoc(db, "PO", "PO-A1", supplier.LedgerId, "Advance Supplier", 1000m);

        var journalRepo = new JournalRepository("SQLite", db.ConnectionString);
        var journal = new JournalService(journalRepo);
        await AdvanceVoucherAsync(db, journal, journalRepo, supplier, poId, "PAY-ADV-1", 400m);

        // GL: the advance debits 1215 and never touches AP 2201.
        Assert.Equal(400m, GlAccountNetDebit(db, "1215"));
        Assert.Equal(0m, GlAccountNetDebit(db, "2201"));
        // Party ledger: one ADV settlement entry (debit) for the supplier.
        Assert.Equal(1m, ExecScalar(db,
            $"SELECT COUNT(*) FROM party_ledger WHERE company_id = '{CompanyCode}' AND party_kind = 'S' AND party_id = {supplier.LedgerId} AND doc_kind = 'ADV' AND debit = 400 AND journal_entry_id IS NOT NULL;"));

        // The supplier statement nets the advance against the payable:
        // invoice credit 1000 + advance debit 400 -> net credit 600 owed.
        var invoiceId = InsertPurchaseDoc(db, "PUR", "PUR-A1", supplier.LedgerId, "Advance Supplier", 1000m);
        Exec(db, $"INSERT INTO party_ledger (company_id, party_kind, party_id, doc_kind, doc_id, doc_no, entry_date, debit, credit, currency_code) VALUES ('{CompanyCode}', 'S', {supplier.LedgerId}, 'PUR', {invoiceId}, 'PUR-A1', '2026-03-02', 0, 1000, 'SAR');");
        var balance = await new MasterCardRepository("SQLite", db.ConnectionString)
            .GetPartyBalanceAsync(CompanyCode, "S", "SUP-A");
        Assert.Equal(400m, balance.Debit);
        Assert.Equal(1000m, balance.Credit);
        Assert.Equal(-600m, balance.Balance);
    }

    [Fact]
    public async Task Advance_VoucherDebitingPayable_IsRejected()
    {
        using var db = await SeededAsync();
        var supplier = await SaveSupplierAsync(db, "SUP-A", "Advance Supplier");
        var poId = InsertPurchaseDoc(db, "PO", "PO-A1", supplier.LedgerId, "Advance Supplier", 1000m);

        var journalRepo = new JournalRepository("SQLite", db.ConnectionString);
        var journal = new JournalService(journalRepo);
        // An advance-flagged voucher that debits AP 2201 (the old shape)
        // must fail the in-transaction control enforcement for 1215.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AdvanceVoucherAsync(db, journal, journalRepo, supplier, poId, "PAY-ADV-BAD", 400m, debitAccount: "2201"));
        Assert.Equal(0m, ExecScalar(db,
            $"SELECT COUNT(*) FROM party_ledger WHERE company_id = '{CompanyCode}' AND doc_kind = 'ADV';"));
    }

    [Fact]
    public async Task Advance_CannotBeAllocatedToAnotherPurchaseOrdersInvoice()
    {
        using var db = await SeededAsync();
        var supplier = await SaveSupplierAsync(db, "SUP-A", "Advance Supplier");
        var poA = InsertPurchaseDoc(db, "PO", "PO-A1", supplier.LedgerId, "Advance Supplier", 1000m);
        var poB = InsertPurchaseDoc(db, "PO", "PO-B1", supplier.LedgerId, "Advance Supplier", 1000m);
        var invoiceB = InsertPurchaseDoc(db, "PUR", "PUR-B1", supplier.LedgerId, "Advance Supplier", 1000m);
        LinkPoToInvoice(db, poB, invoiceB);

        var journalRepo = new JournalRepository("SQLite", db.ConnectionString);
        var journal = new JournalService(journalRepo);
        await AdvanceVoucherAsync(db, journal, journalRepo, supplier, poA, "PAY-ADV-1", 400m);

        var recon = new ReconciliationRepository("SQLite", db.ConnectionString);
        var advance = Assert.Single(await recon.GetPaymentsAsync(CompanyCode, "S"));
        Assert.True(advance.IsAdvance);
        Assert.Equal("PO-A1", advance.PoDocNo);
        Assert.Equal(400m, advance.Available);

        var error = await Assert.ThrowsAsync<AppError>(() =>
            recon.AllocateAsync(CompanyCode, "S", advance.LedgerId, "PURCHASE", invoiceB, 100m, Actor));
        Assert.Equal(ErrorCodes.AdvancePurchaseOrderMismatch, error.Code);
        Assert.Equal(0m, ExecScalar(db, $"SELECT paid_amount FROM purchase_documents WHERE doc_id = {invoiceB};"));
        Assert.Equal(0m, ExecScalar(db, $"SELECT COUNT(*) FROM payment_allocations WHERE company_id = '{CompanyCode}';"));
        Assert.Equal(400m, GlAccountNetDebit(db, "1215"));
    }

    [Fact]
    public async Task Advance_AllocatedToOwnPoInvoice_ClearsAdvanceAndSettles()
    {
        using var db = await SeededAsync();
        var supplier = await SaveSupplierAsync(db, "SUP-A", "Advance Supplier");
        var poId = InsertPurchaseDoc(db, "PO", "PO-A1", supplier.LedgerId, "Advance Supplier", 1000m);
        var invoiceId = InsertPurchaseDoc(db, "PUR", "PUR-A1", supplier.LedgerId, "Advance Supplier", 1000m);
        LinkPoToInvoice(db, poId, invoiceId);

        var journalRepo = new JournalRepository("SQLite", db.ConnectionString);
        var journal = new JournalService(journalRepo);
        await AdvanceVoucherAsync(db, journal, journalRepo, supplier, poId, "PAY-ADV-1", 400m);

        var recon = new ReconciliationRepository("SQLite", db.ConnectionString);
        var advance = Assert.Single(await recon.GetPaymentsAsync(CompanyCode, "S"));
        var allocationId = await recon.AllocateAsync(CompanyCode, "S", advance.LedgerId, "PURCHASE", invoiceId, 250m, Actor, "Advance applied");

        Assert.Equal(250m, ExecScalar(db, $"SELECT paid_amount FROM purchase_documents WHERE doc_id = {invoiceId};"));
        Assert.Equal(1m, ExecScalar(db,
            $"SELECT COUNT(*) FROM payment_allocations WHERE company_id = '{CompanyCode}' AND allocation_id = '{allocationId:D}' AND source_kind = 'ADVANCE' AND advance_po_doc_id = {poId} AND clearing_journal_id IS NOT NULL AND amount = 250;"));
        // Clearing: Dr AP 2201 / Cr Advances 1215 in the allocation txn.
        Assert.Equal(250m, GlAccountNetDebit(db, "2201"));
        Assert.Equal(150m, GlAccountNetDebit(db, "1215"));
        // The advance remains offered with exactly its unconsumed part.
        var after = Assert.Single(await recon.GetPaymentsAsync(CompanyCode, "S"));
        Assert.Equal(150m, after.Available);
    }

    [Fact]
    public async Task AdvanceAllocation_ReversalRestoresAdvance_AndVoucherReversalIsBlockedWhileConsumed()
    {
        using var db = await SeededAsync();
        var supplier = await SaveSupplierAsync(db, "SUP-A", "Advance Supplier");
        var poId = InsertPurchaseDoc(db, "PO", "PO-A1", supplier.LedgerId, "Advance Supplier", 1000m);
        var invoiceId = InsertPurchaseDoc(db, "PUR", "PUR-A1", supplier.LedgerId, "Advance Supplier", 1000m);
        LinkPoToInvoice(db, poId, invoiceId);

        var journalRepo = new JournalRepository("SQLite", db.ConnectionString);
        var journal = new JournalService(journalRepo);
        var voucherId = await AdvanceVoucherAsync(db, journal, journalRepo, supplier, poId, "PAY-ADV-1", 400m);

        var recon = new ReconciliationRepository("SQLite", db.ConnectionString);
        var advance = Assert.Single(await recon.GetPaymentsAsync(CompanyCode, "S"));
        var allocationId = await recon.AllocateAsync(CompanyCode, "S", advance.LedgerId, "PURCHASE", invoiceId, 250m, Actor);
        Assert.Equal(250m, ExecScalar(db, $"SELECT paid_amount FROM purchase_documents WHERE doc_id = {invoiceId};"));

        // While any of the advance is consumed, reversing the advance
        // voucher itself is blocked with the catalogued PUR-1013.
        var blocked = await Assert.ThrowsAsync<AppError>(() => journal.ReverseAsync(voucherId, Actor));
        Assert.Equal(ErrorCodes.AdvanceReversalBlockedWhileConsumed, blocked.Code);
        Assert.Equal(1m, ExecScalar(db, $"SELECT status FROM journal_entries WHERE journal_entry_id = '{voucherId:D}';"));
        Assert.Equal(250m, ExecScalar(db, $"SELECT paid_amount FROM purchase_documents WHERE doc_id = {invoiceId};"));

        // Reversing the allocation un-clears the advance exactly.
        await recon.UnallocateAsync(CompanyCode, allocationId, Actor);
        Assert.Equal(0m, ExecScalar(db, $"SELECT paid_amount FROM purchase_documents WHERE doc_id = {invoiceId};"));
        Assert.Equal(0m, ExecScalar(db, $"SELECT COUNT(*) FROM payment_allocations WHERE company_id = '{CompanyCode}';"));
        Assert.Equal(1m, ExecScalar(db,
            $"SELECT COUNT(*) FROM payment_allocation_reversal_audit WHERE company_id = '{CompanyCode}' AND allocation_id = '{allocationId:D}' AND amount = 250 AND released_by = '{Actor}';"));
        Assert.Equal(400m, GlAccountNetDebit(db, "1215"));
        Assert.Equal(0m, GlAccountNetDebit(db, "2201"));
        var restored = Assert.Single(await recon.GetPaymentsAsync(CompanyCode, "S"));
        Assert.Equal(400m, restored.Available);

        // After unallocation the voucher reversal is allowed and mirrors
        // the advance settlement: the supplier prepayment disappears.
        await journal.ReverseAsync(voucherId, Actor);
        Assert.Empty(await recon.GetPaymentsAsync(CompanyCode, "S"));
        Assert.Equal(0m, GlAccountNetDebit(db, "1215"));
        var balance = await new MasterCardRepository("SQLite", db.ConnectionString)
            .GetPartyBalanceAsync(CompanyCode, "S", "SUP-A");
        Assert.Equal(0m, balance.Balance);
    }

    // ------------------------------------------------------------ invoice / POS paid at posting

    [Fact]
    public async Task InvoiceCollectedAtPosting_SettlesPartyBalance()
    {
        using var db = await SeededAsync();
        var party = await SavePartyAsync(db, "CUST-1", "Posting Customer", creditLimit: 0m);

        var doc = BuildInvoice(InvDocKind.SalesInvoice, party.LedgerId, qty: 10m, price: 50m, paid: 250m);
        InvRepo.SaveDraft(doc);
        InvRepo.Post(doc.DocId, InvDocKind.SalesInvoice, Actor);

        // Debit 500 (the invoice), credit 250 (collected) -> balance 250,
        // and the collected part is pre-allocated to the document.
        var repo = new MasterCardRepository("SQLite", db.ConnectionString);
        Assert.Equal(250m, (await repo.GetPartyBalanceAsync(CompanyCode, "C", "CUST-1")).Balance);
        Assert.Equal(250m, ExecScalar(db,
            $"SELECT COALESCE(SUM(amount), 0) FROM payment_allocations WHERE company_id = '{CompanyCode}' AND doc_id = {doc.DocId};"));
        var recon = new ReconciliationRepository("SQLite", db.ConnectionString);
        Assert.Empty(await recon.GetPaymentsAsync(CompanyCode, "C")); // fully allocated: nothing unallocated
        Assert.Equal(250m, Assert.Single(await recon.GetOpenDocumentsAsync(CompanyCode, "C")).OpenAmount);
    }

    [Fact]
    public async Task PosCashSale_WithParty_NetsToZero()
    {
        using var db = await SeededAsync();
        var party = await SavePartyAsync(db, "CUST-1", "Pos Customer", creditLimit: 0m);

        var doc = BuildInvoice(InvDocKind.PosSale, party.LedgerId, qty: 2m, price: 50m, paid: 100m);
        InvRepo.SaveDraft(doc);
        InvRepo.Post(doc.DocId, InvDocKind.PosSale, Actor);

        var repo = new MasterCardRepository("SQLite", db.ConnectionString);
        Assert.Equal(0m, (await repo.GetPartyBalanceAsync(CompanyCode, "C", "CUST-1")).Balance);
    }

    // ------------------------------------------------------------ migration shape

    [Fact]
    public async Task Migration_LeavesOnePartyMaster_AndDoubleRunIsIdempotent()
    {
        using var db = await SeededAsync();

        Assert.Equal(0m, ExecScalar(db, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('customers', 'suppliers');"));
        Assert.Equal(1m, ExecScalar(db, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'parties';"));
        Assert.Equal(1m, ExecScalar(db, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'voucher_party_links';"));
        Assert.True((await SchemaMigrator.ApplyAllAsync("SQLite", db.DbPath)).Success);
    }

    // ------------------------------------------------------------ fixture

    private static InvDoc BuildInvoice(InvDocKind kind, long partyLedgerId, decimal qty, decimal price, decimal paid)
        => new()
        {
            Kind = kind,
            DocNo = InvRepo.NextDocNo(kind),
            DocDate = new DateTime(2026, 3, 10),
            PartyId = partyLedgerId,
            PartyCode = "CUST-1",
            PartyName = "Posting Customer",
            WarehouseId = 1,
            CurrencyCode = "SAR",
            PaidAmount = paid,
            PaymentMethod = kind == InvDocKind.PosSale ? InvPaymentMethod.Cash : InvPaymentMethod.Credit,
            Lines = { new InvLine { LineNo = 1, MaterialId = 42, MaterialCode = "MAT-42", Qty = qty, UnitPrice = price, TaxRate = 0m, WarehouseId = 1 } }
        };

    // ------------------------------------------------------------ W1 (mig 095)

    [Fact]
    public async Task BlacklistedParty_IsRejectedAtResolution()
    {
        using var db = await SeededAsync();
        var repo = new MasterCardRepository("SQLite", db.ConnectionString);
        var party = new Party
        {
            Kind = "C", Code = "BAD-1", NameEn = "Blacklisted Customer",
            IsBlacklisted = true, BlacklistReason = "Fraud", BlacklistedBy = Actor
        };
        await repo.SavePartyAsync(CompanyCode, party, Actor);

        var ex = Assert.Throws<AppError>(() => TaxSvc.ResolveParty("BAD-1", customer: true));
        Assert.Equal(ErrorCodes.PartyBlacklisted, ex.Code);
        Assert.Contains("blacklisted", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Fraud", ex.Message);
        var ledgerError = Assert.Throws<AppError>(() => PartyLedgerSvc.ResolveParty("BAD-1", "C"));
        Assert.Equal(ErrorCodes.PartyBlacklisted, ledgerError.Code);

        // The balance still reports (balances never vanish): the party
        // shows in CustomerBalances even while blacklisted + inactive.
        Exec(db, $"INSERT INTO party_ledger (company_id, party_kind, party_id, doc_kind, entry_date, debit, credit) VALUES ('{CompanyCode}', 'C', {party.LedgerId}, 'SAL', '2026-01-01', 250, 0);");
        Exec(db, $"UPDATE parties SET is_active = 0 WHERE code = 'BAD-1' AND company_id = '{CompanyCode}';");
        var balances = await RptQuery.CustomerBalancesAsync(CancellationToken.None);
        var row = balances.Single(r => Convert.ToString(r["Code"], CultureInfo.InvariantCulture) == "BAD-1");
        Assert.Equal(250m, Convert.ToDecimal(row["Balance"], CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task OpeningBalance_PostsOnce_AndBalanceReflectsIt()
    {
        using var db = await SeededAsync();
        var service = new MasterCardService(new MasterCardRepository("SQLite", db.ConnectionString));
        await SavePartyAsync(db, "OPN-1", "Opening Customer", creditLimit: 0m);

        var journalId = await service.PostPartyOpeningBalanceAsync(
            CompanyCode, "C", "OPN-1", 750m, new DateOnly(2026, 1, 1), null, Actor);
        Assert.False(string.IsNullOrWhiteSpace(journalId));

        var balance = await service.GetPartyBalanceAsync(CompanyCode, "C", "OPN-1");
        Assert.Equal(750m, balance.Balance);

        var loaded = await service.GetPartyByCodeAsync(CompanyCode, "C", "OPN-1");
        Assert.Equal(750m, loaded!.OpeningBalanceAmount);
        Assert.Equal(journalId, loaded.OpeningJournalId);

        // GL: receivable debited 750, retained earnings (32) credited 750.
        Assert.Equal(750m, ExecScalar(db,
            "SELECT COALESCE(SUM(l.debit - l.credit), 0) FROM journal_entry_lines l JOIN chart_of_accounts a ON a.account_id = l.account_id WHERE a.account_code = '1201';"));
        Assert.Equal(-750m, ExecScalar(db,
            "SELECT COALESCE(SUM(l.debit - l.credit), 0) FROM journal_entry_lines l JOIN chart_of_accounts a ON a.account_id = l.account_id WHERE a.account_code = '32';"));

        // A second opening is blocked.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PostPartyOpeningBalanceAsync(CompanyCode, "C", "OPN-1", 10m, new DateOnly(2026, 1, 2), null, Actor));
    }

    [Fact]
    public async Task CustomerStatement_RunningBalanceAndOpening()
    {
        using var db = await SeededAsync();
        var party = await SavePartyAsync(db, "STMT-1", "Statement Customer", creditLimit: 0m);
        // Before the range: +500 opening position. Inside: +300, -100.
        Exec(db, $"INSERT INTO party_ledger (company_id, party_kind, party_id, doc_kind, doc_no, entry_date, debit, credit, currency_code) VALUES ('{CompanyCode}', 'C', {party.LedgerId}, 'SAL', 'SAL-0', '2025-12-15', 500, 0, 'SAR');");
        Exec(db, $"INSERT INTO party_ledger (company_id, party_kind, party_id, doc_kind, doc_no, entry_date, debit, credit, currency_code) VALUES ('{CompanyCode}', 'C', {party.LedgerId}, 'SAL', 'SAL-1', '2026-02-01', 300, 0, 'SAR');");
        Exec(db, $"INSERT INTO party_ledger (company_id, party_kind, party_id, doc_kind, doc_no, entry_date, debit, credit, currency_code) VALUES ('{CompanyCode}', 'C', {party.LedgerId}, 'RCV', 'RCV-1', '2026-02-10', 0, 100, 'SAR');");

        var rows = await RptQuery.CustomerStatementAsync(
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), "STMT-1", CancellationToken.None);
        Assert.Equal(2, rows.Count);
        Assert.Equal(500m, Convert.ToDecimal(rows[0]["OpeningBalanceBase"], CultureInfo.InvariantCulture));
        Assert.Equal(800m, Convert.ToDecimal(rows[0]["RunningBalanceBase"], CultureInfo.InvariantCulture));
        Assert.Equal(700m, Convert.ToDecimal(rows[1]["RunningBalanceBase"], CultureInfo.InvariantCulture));
    }

    // ------------------------------------------------------------ W8 receipts completion

    /// <summary>Adds the W8 allocation accounts (305/306/509) the seed
    /// does not carry, through the same AccRepo path the seed uses.</summary>
    private static void EnsureW8Accounts()
    {
        foreach (var (code, name) in new[]
        {
            ("305", "Sales Discounts"), ("306", "Other Income"), ("509", "Foreign Exchange Losses")
        })
        {
            AccRepo.Save(null, new AccRepo.AccRec(
                string.Empty, code, null, name, name, "Postable", "Active", "Low", "Unspecified",
                false, false, false, false, false, false, 0m, string.Empty, 0m, 0m, 0m),
                renumberDescendants: false);
        }
    }

    private static void SetBaseSar(TestDb db)
        => Exec(db, $"UPDATE companies SET currency_code = 'SAR' WHERE company_code = '{CompanyCode}';");

    /// <summary>Net (debit - credit) posted on one account by ALLOC
    /// journals only — the allocation journal is denominated in base
    /// currency at rate 1, so transaction and base amounts agree.</summary>
    private static decimal AllocJournalNet(TestDb db, string accountCode)
        => ExecScalar(db, $"""
            SELECT COALESCE(SUM(l.debit - l.credit), 0)
            FROM journal_entry_lines l
            JOIN journal_entries j ON j.journal_entry_id = l.journal_entry_id
            JOIN chart_of_accounts a ON a.account_id = l.account_id
            WHERE a.account_code = '{accountCode}' AND j.source_doc_type = 'ALLOC' AND j.status = 1;
            """);

    private static long SeedPostedInvoice(TestDb db, long partyLedgerId, string docNo, string currency, decimal total, decimal storedRate)
    {
        var docId = (long)ExecScalar(db,
            $"INSERT INTO sales_documents (company_id, doc_kind, doc_no, doc_date, party_id, party_name, currency_code, exchange_rate, grand_total, paid_amount, status) VALUES ('{CompanyCode}', 'SAL', '{docNo}', '2026-03-01', {partyLedgerId}, 'Fx Customer', '{currency}', {storedRate.ToString(CultureInfo.InvariantCulture)}, {total.ToString(CultureInfo.InvariantCulture)}, 0, 'Posted'); SELECT last_insert_rowid();");
        var baseTotal = total * storedRate;
        Exec(db, $"INSERT INTO party_ledger (company_id, party_kind, party_id, doc_kind, doc_id, doc_no, entry_date, debit, credit, currency_code, base_debit, base_credit, exchange_rate) VALUES ('{CompanyCode}', 'C', {partyLedgerId}, 'SAL', {docId}, '{docNo}', '2026-03-01', {total.ToString(CultureInfo.InvariantCulture)}, 0, '{currency}', {baseTotal.ToString(CultureInfo.InvariantCulture)}, 0, {storedRate.ToString(CultureInfo.InvariantCulture)});");
        return docId;
    }

    private static long SeedReceipt(TestDb db, long partyLedgerId, string docNo, string currency, decimal amount, decimal rate)
    {
        var baseAmount = amount * rate;
        return (long)ExecScalar(db,
            $"INSERT INTO party_ledger (company_id, party_kind, party_id, doc_kind, doc_no, entry_date, debit, credit, currency_code, base_debit, base_credit, exchange_rate) VALUES ('{CompanyCode}', 'C', {partyLedgerId}, 'RCV', '{docNo}', '2026-03-05', 0, {amount.ToString(CultureInfo.InvariantCulture)}, '{currency}', 0, {baseAmount.ToString(CultureInfo.InvariantCulture)}, {rate.ToString(CultureInfo.InvariantCulture)}); SELECT last_insert_rowid();");
    }

    [Fact]
    public async Task SettlementDiscount_PostsBalancedJournal_AndSettlesDocument()
    {
        using var db = await SeededAsync();
        SetBaseSar(db);
        EnsureW8Accounts();
        var party = await SavePartyAsync(db, "W8-DISC", "Discount Customer", creditLimit: 0m);
        var docId = SeedPostedInvoice(db, party.LedgerId, "W8-INV-1", "SAR", 1000m, 1m);
        var receiptId = SeedReceipt(db, party.LedgerId, "W8-RCV-1", "SAR", 950m, 1m);

        var recon = new ReconciliationRepository("SQLite", db.ConnectionString);
        await recon.AllocateAsync(CompanyCode, "C", receiptId, "SALES", docId, 950m, Actor,
            discountAmount: 50m);

        // The document settles by payment + discount.
        Assert.Equal(1000m, ExecScalar(db, $"SELECT paid_amount FROM sales_documents WHERE doc_id = {docId};"));
        Assert.Equal(50m, ExecScalar(db, "SELECT discount_amount FROM payment_allocations WHERE doc_table = 'SALES' AND doc_id = " + docId + ";"));
        Assert.Equal(0m, ExecScalar(db, "SELECT fx_amount FROM payment_allocations WHERE doc_table = 'SALES' AND doc_id = " + docId + ";"));
        // Journal: Dr 305 Sales Discounts 50 / Cr 1201 Receivable 50, balanced.
        Assert.Equal(50m, AllocJournalNet(db, "305"));
        Assert.Equal(-50m, AllocJournalNet(db, "1201"));
        Assert.Equal(0m, ExecScalar(db, """
            SELECT COALESCE(SUM(l.debit - l.credit), 0) FROM journal_entry_lines l
            JOIN journal_entries j ON j.journal_entry_id = l.journal_entry_id
            WHERE j.source_doc_type = 'ALLOC' AND j.status = 1;
            """));
        // The allocation wrote its unified document event.
        Assert.Equal(1m, ExecScalar(db, $"SELECT COUNT(*) FROM sau_document_events WHERE doc_id = {docId} AND event_type = 'Allocated';"));
    }

    [Fact]
    public async Task SettlementDiscount_ServiceRequiresAuthorization()
    {
        using var db = await SeededAsync();
        SetBaseSar(db);
        EnsureW8Accounts();
        var party = await SavePartyAsync(db, "W8-AUTH", "Auth Customer", creditLimit: 0m);
        var docId = SeedPostedInvoice(db, party.LedgerId, "W8-INV-2", "SAR", 100m, 1m);
        var receiptId = SeedReceipt(db, party.LedgerId, "W8-RCV-2", "SAR", 90m, 1m);

        var service = new ReconciliationService(new ReconciliationRepository("SQLite", db.ConnectionString));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AllocateAsync(CompanyCode, "C", receiptId, "SALES", docId, 90m, Actor,
                discountAmount: 10m, settlementDiscountAuthorized: false));
        Assert.Equal(0m, ExecScalar(db, $"SELECT paid_amount FROM sales_documents WHERE doc_id = {docId};"));

        await service.AllocateAsync(CompanyCode, "C", receiptId, "SALES", docId, 90m, Actor,
            discountAmount: 10m, settlementDiscountAuthorized: true);
        Assert.Equal(100m, ExecScalar(db, $"SELECT paid_amount FROM sales_documents WHERE doc_id = {docId};"));
    }

    [Fact]
    public async Task CrossCurrency_FxLoss_PostsToFxLossAccount()
    {
        using var db = await SeededAsync();
        SetBaseSar(db);
        EnsureW8Accounts();
        var party = await SavePartyAsync(db, "W8-FXL", "Fx Loss Customer", creditLimit: 0m);
        // Invoice USD 100 booked at 3.75 (base 375). Receipt EUR 80
        // booked at 4.00 (base 320). Settling the full invoice with the
        // receipt realizes a 55 base loss: 375 - 320.
        var docId = SeedPostedInvoice(db, party.LedgerId, "W8-INV-3", "USD", 100m, 3.75m);
        var receiptId = SeedReceipt(db, party.LedgerId, "W8-RCV-3", "EUR", 80m, 4.00m);

        var recon = new ReconciliationRepository("SQLite", db.ConnectionString);
        await recon.AllocateAsync(CompanyCode, "C", receiptId, "SALES", docId, 80m, Actor,
            docAmount: 100m);

        Assert.Equal(100m, ExecScalar(db, $"SELECT paid_amount FROM sales_documents WHERE doc_id = {docId};"));
        Assert.Equal(100m, ExecScalar(db, "SELECT doc_amount FROM payment_allocations WHERE doc_table = 'SALES' AND doc_id = " + docId + ";"));
        Assert.Equal(55m, ExecScalar(db, "SELECT fx_amount FROM payment_allocations WHERE doc_table = 'SALES' AND doc_id = " + docId + ";"));
        // Dr 509 Foreign Exchange Losses 55 / Cr 1201 Receivable 55.
        Assert.Equal(55m, AllocJournalNet(db, "509"));
        Assert.Equal(-55m, AllocJournalNet(db, "1201"));
        Assert.Equal(0m, AllocJournalNet(db, "306"));
    }

    [Fact]
    public async Task CrossCurrency_FxGain_PostsToOtherIncome()
    {
        using var db = await SeededAsync();
        SetBaseSar(db);
        EnsureW8Accounts();
        var party = await SavePartyAsync(db, "W8-FXG", "Fx Gain Customer", creditLimit: 0m);
        // Invoice USD 100 booked at 3.75 (base 375). Receipt EUR 100
        // booked at 4.00 (base 400). Settling realizes a 25 base gain.
        var docId = SeedPostedInvoice(db, party.LedgerId, "W8-INV-4", "USD", 100m, 3.75m);
        var receiptId = SeedReceipt(db, party.LedgerId, "W8-RCV-4", "EUR", 100m, 4.00m);

        var recon = new ReconciliationRepository("SQLite", db.ConnectionString);
        await recon.AllocateAsync(CompanyCode, "C", receiptId, "SALES", docId, 100m, Actor,
            docAmount: 100m);

        Assert.Equal(100m, ExecScalar(db, $"SELECT paid_amount FROM sales_documents WHERE doc_id = {docId};"));
        Assert.Equal(-25m, ExecScalar(db, "SELECT fx_amount FROM payment_allocations WHERE doc_table = 'SALES' AND doc_id = " + docId + ";"));
        // Dr 1201 Receivable 25 / Cr 306 Other Income 25.
        Assert.Equal(25m, AllocJournalNet(db, "1201"));
        Assert.Equal(-25m, AllocJournalNet(db, "306"));
        Assert.Equal(0m, AllocJournalNet(db, "509"));
    }

    [Fact]
    public void SuggestFifo_OldestDueFirst_SameCurrencyOnly()
    {
        OpenDocumentRow Doc(long id, long party, string currency, decimal total, DateOnly docDate, DateOnly? due)
            => new(id, "SALES", $"INV-{id}", docDate, party, "P", currency, total, 0m, 0m, due);
        var docs = new[]
        {
            Doc(1, 7, "SAR", 100m, new DateOnly(2026, 1, 10), new DateOnly(2026, 2, 1)),
            Doc(2, 7, "SAR", 100m, new DateOnly(2026, 1, 5), new DateOnly(2026, 1, 1)),
            Doc(3, 7, "USD", 50m, new DateOnly(2025, 12, 1), new DateOnly(2025, 12, 1)),
            Doc(4, 9, "SAR", 500m, new DateOnly(2025, 1, 1), new DateOnly(2025, 1, 1)),
        };

        var plan = ReconciliationService.SuggestFifo(docs, partyId: 7, paymentCurrency: "SAR", paymentAvailable: 150m);

        Assert.Equal(2, plan.Count);
        Assert.Equal(2L, plan[0].Document.DocId);
        Assert.Equal(100m, plan[0].Amount);
        Assert.Equal(1L, plan[1].Document.DocId);
        Assert.Equal(50m, plan[1].Amount);
    }

    [Fact]
    public async Task DeniedDiscountCheck_LogsAccessDeniedEvent()
    {
        using var db = await SeededAsync();
        var perms = new PermServ();
        Assert.False(perms.Has(PermCat.Sales.SettlementDiscount));

        var allowed = perms.Require(PermCat.Sales.SettlementDiscount, "SALES", "SAL", 42, "SAL-42");

        Assert.False(allowed);
        Assert.Equal(1m, ExecScalar(db, "SELECT COUNT(*) FROM sau_document_events WHERE event_type = 'AccessDenied' AND doc_id = 42;"));
        Assert.Equal(1m, ExecScalar(db, "SELECT COUNT(*) FROM sau_document_events WHERE event_type = 'AccessDenied' AND details LIKE '%SALES.SETTLEMENT.DISCOUNT%';"));
    }

    private static async Task<Party> SavePartyAsync(TestDb db, string code, string name, decimal creditLimit)
    {
        var repo = new MasterCardRepository("SQLite", db.ConnectionString);
        var party = new Party { Kind = "C", Code = code, NameEn = name, CreditLimit = creditLimit };
        await repo.SavePartyAsync(CompanyCode, party, Actor);
        Assert.True(party.LedgerId > 0, "SaveParty must assign the canonical ledger_id.");
        return party;
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
        var root = Path.Combine(Path.GetTempPath(), "party-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var dbPath = Path.Combine(root, "party.db");
        var companyId = Guid.NewGuid();
        var connectionString = $"Data Source={dbPath};";

        Assert.True((await SchemaMigrator.ApplyAllAsync("SQLite", dbPath)).Success);
        PointAppAt(dbPath);
        AppState.SetCompany(CompanyCode, "Party Test Co", "Party Test Co");

        using (var connection = new SqliteConnection(connectionString))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "INSERT INTO companies (company_id, company_code, company_name_ar, company_name_en, currency_code) VALUES ($id, $code, 'Party Test Co', 'Party Test Co', 'SAR');";
            cmd.Parameters.AddWithValue("$id", companyId.ToString("D"));
            cmd.Parameters.AddWithValue("$code", CompanyCode);
            cmd.ExecuteNonQuery();
        }

        var structure = new AccountingStructureRepository("SQLite", connectionString);
        await structure.CreateFiscalYearAsync(companyId, 2026, Actor);

        foreach (var (code, name) in new[]
        {
            ("131", "Cash"), ("1201", "Receivable"), ("1203", "Inventory"),
            ("1215", "Advances to Suppliers"),
            ("2201", "Payable"), ("301", "Sales"), ("401", "COGS"), ("32", "Retained Earnings")
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

    private static void Exec(TestDb db, string sql)
    {
        using var connection = new SqliteConnection(db.ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static decimal ExecScalar(TestDb db, string sql)
    {
        using var connection = new SqliteConnection(db.ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToDecimal(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}
