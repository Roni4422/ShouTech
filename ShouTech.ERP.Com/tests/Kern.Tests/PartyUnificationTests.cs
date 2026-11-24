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
            documentNumber: "PAY-SUP-BAD");
        Exec(db,
            $"INSERT INTO voucher_party_links (journal_entry_id, company_id, party_kind, party_id, party_code, doc_kind, doc_no, amount, currency_code, created_by) VALUES ('{badPaymentId:D}', '{CompanyCode}', 'S', {supplier.LedgerId}, 'SUP-1', 'PAY', 'PAY-SUP-BAD', 600, 'SAR', '{Actor}');");
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
            documentNumber: "PAY-SUP-1");
        Exec(db,
            $"INSERT INTO voucher_party_links (journal_entry_id, company_id, party_kind, party_id, party_code, doc_kind, doc_no, amount, currency_code, created_by) VALUES ('{paymentId:D}', '{CompanyCode}', 'S', {supplier.LedgerId}, 'SUP-1', 'PAY', 'PAY-SUP-1', 600, 'SAR', '{Actor}');");
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

        var ex = Assert.Throws<InvalidOperationException>(() => TaxSvc.ResolveParty("BAD-1", customer: true));
        Assert.Contains("blacklisted", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Fraud", ex.Message);
        Assert.Throws<InvalidOperationException>(() => PartyLedgerSvc.ResolveParty("BAD-1", "C"));

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
            cmd.CommandText = "INSERT INTO companies (company_id, company_code, company_name_ar, company_name_en) VALUES ($id, $code, 'Party Test Co', 'Party Test Co');";
            cmd.Parameters.AddWithValue("$id", companyId.ToString("D"));
            cmd.Parameters.AddWithValue("$code", CompanyCode);
            cmd.ExecuteNonQuery();
        }

        var structure = new AccountingStructureRepository("SQLite", connectionString);
        await structure.CreateFiscalYearAsync(companyId, 2026, Actor);

        foreach (var (code, name) in new[]
        {
            ("131", "Cash"), ("1201", "Receivable"), ("1203", "Inventory"),
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
