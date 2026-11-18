using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ShouTech.App;
using ShouTech.App.Configuration;
using ShouTech.App.Reporting.Invoices;
using ShouTech.App.Services;
using ShouTech.Domain;
using ShouTech.Domain.Entities;
using ShouTech.Infrastructure;
using Xunit;

namespace Kern.Tests;

/// <summary>
/// Fix Phase Step 5 — InvRepo.Post correctness (sweep I-1 / I-2 and the
/// §2 reversal defects, plus Step 4 owner flag 3):
///   * (a) the sweep's exact case — lines 1,000 + tax 150 − header
///     discount 100 stores 1,050 and posts EXACTLY 1,050 to the GL and
///     to party_ledger (the old code debited 1,150);
///   * (b) extra charges are honored the same way;
///   * (c) a Quotation posts with NO receivable, NO party_ledger row,
///     NO GL entry and NO stock movement;
///   * (d) a paid invoice reversed through ReversePostedInvoice nets
///     the GL to zero on every account, restores the party balance,
///     deletes its allocations, mirrors its at-posting settlement and
///     returns the original's paid_amount to 0;
///   * (e) the credit guard evaluates the STORED total (a 1,050 doc
///     passes a 1,100 limit that the old 1,150 figure would have hit);
///   * stock kinds carry no party ledger; StockTransfer /
///     InventoryCount were blocked in this path with ACC-5014 in
///     Step 5 — Step 9 rewired them to their dedicated engines
///     (PostTransfer / PostCount), proven by the last two facts here
///     and in OrderChainStep9Tests.
/// Execution requires Windows (net8.0-windows testhost); the same
/// scenarios are verified headlessly by
/// ~/workspace/research/fix-step5/invpost_harness.py
/// (see docs/FIX_PHASE_STEP5.md).
/// </summary>
public sealed class InvPostCorrectnessTests
{
    private const string Actor = "invpost-test";
    private const string CompanyCode = "IP5";

    // ------------------------------------------------------------ (a)

    [Fact]
    public async Task HeaderDiscount_Stored1050_Posts1050_ToGlAndPartyLedger()
    {
        using var db = await SeededAsync();
        var party = await SavePartyAsync(db, "C", "CUST-1", creditLimit: 0m);

        var doc = BuildDoc(InvDocKind.SalesInvoice, party.LedgerId, "CUST-1",
            qty: 10m, price: 100m, taxRate: 15m, discount: 100m, extra: 0m, paid: 0m);
        InvRepo.SaveDraft(doc);
        Assert.Equal(1050m, StoredGrand(db, "sales_documents", doc.DocId));

        InvRepo.Post(doc.DocId, doc.Kind, Actor);

        Assert.Equal(1050m, LedgerSum(db, party.LedgerId, "SAL", debit: true));
        Assert.Equal(0m, LedgerSum(db, party.LedgerId, "SAL", debit: false));
        var entry = SingleEntry(db, "SAL", doc.DocId);
        Assert.Equal(1050m, entry.Lines.Where(l => l.Code == "1201").Sum(l => l.Debit));
        Assert.Equal(1050m, entry.Lines.Where(l => l.Code == "301").Sum(l => l.Credit));
        Assert.Equal(entry.Lines.Sum(l => l.Debit), entry.Lines.Sum(l => l.Credit));
    }

    [Fact]
    public async Task PurchaseDiscount_Stored1050_Posts1050_ToPayableAndGl()
    {
        using var db = await SeededAsync();
        var party = await SavePartyAsync(db, "S", "SUP-1", creditLimit: 0m);

        var doc = BuildDoc(InvDocKind.PurchaseInvoice, party.LedgerId, "SUP-1",
            qty: 10m, price: 100m, taxRate: 15m, discount: 100m, extra: 0m, paid: 0m);
        InvRepo.SaveDraft(doc);
        Assert.Equal(1050m, StoredGrand(db, "purchase_documents", doc.DocId));

        InvRepo.Post(doc.DocId, doc.Kind, Actor);

        Assert.Equal(1050m, LedgerSum(db, party.LedgerId, "PUR", debit: false));
        var entry = SingleEntry(db, "PUR", doc.DocId);
        Assert.Equal(1050m, entry.Lines.Where(l => l.Code == "402").Sum(l => l.Debit));
        Assert.Equal(1050m, entry.Lines.Where(l => l.Code == "2201").Sum(l => l.Credit));
    }

    // ------------------------------------------------------------ (b)

    [Fact]
    public async Task ExtraCharges_StoredTotal_PostsIncludingCharges()
    {
        using var db = await SeededAsync();
        var party = await SavePartyAsync(db, "C", "CUST-1", creditLimit: 0m);

        var doc = BuildDoc(InvDocKind.SalesInvoice, party.LedgerId, "CUST-1",
            qty: 10m, price: 100m, taxRate: 0m, discount: 0m, extra: 75m, paid: 0m);
        InvRepo.SaveDraft(doc);
        Assert.Equal(1075m, StoredGrand(db, "sales_documents", doc.DocId));

        InvRepo.Post(doc.DocId, doc.Kind, Actor);

        Assert.Equal(1075m, LedgerSum(db, party.LedgerId, "SAL", debit: true));
        var entry = SingleEntry(db, "SAL", doc.DocId);
        Assert.Equal(1075m, entry.Lines.Where(l => l.Code == "301").Sum(l => l.Credit));
        Assert.Equal(entry.Lines.Sum(l => l.Debit), entry.Lines.Sum(l => l.Credit));
    }

    // ------------------------------------------------------------ (c)

    [Fact]
    public async Task Quotation_Post_CreatesNoReceivableNoGlNoStockMovement()
    {
        using var db = await SeededAsync();
        var party = await SavePartyAsync(db, "C", "CUST-1", creditLimit: 0m);
        var stockBefore = StockQty(db);

        var doc = BuildDoc(InvDocKind.Quotation, party.LedgerId, "CUST-1",
            qty: 5m, price: 100m, taxRate: 0m, discount: 0m, extra: 0m, paid: 0m);
        InvRepo.SaveDraft(doc);
        InvRepo.Post(doc.DocId, doc.Kind, Actor);

        var loaded = InvRepo.Load(doc.DocId, InvDocKind.Quotation);
        Assert.Equal(InvDocStatus.Posted, loaded!.Status);
        Assert.Equal(string.Empty, loaded.JournalDocNo);
        Assert.Equal(0m, Scalar(db, $"SELECT COUNT(*) FROM party_ledger WHERE doc_id = {doc.DocId};"));
        Assert.Equal(0m, Scalar(db, $"SELECT COUNT(*) FROM journal_entries WHERE source_doc_type = 'QUO' AND source_doc_id = '{doc.DocId}';"));
        Assert.Equal(0m, Scalar(db, $"SELECT COUNT(*) FROM material_stock_movements WHERE document_no = '{doc.DocNo}';"));
        Assert.Equal(stockBefore, StockQty(db));
    }

    // ------------------------------------------------------------ (d)

    [Fact]
    public async Task PaidInvoice_Reversal_Unsettles_AndNetsEverythingToZero()
    {
        using var db = await SeededAsync();
        var party = await SavePartyAsync(db, "C", "CUST-1", creditLimit: 0m);
        var stockBefore = StockQty(db);

        var doc = BuildDoc(InvDocKind.SalesInvoice, party.LedgerId, "CUST-1",
            qty: 1m, price: 100m, taxRate: 0m, discount: 0m, extra: 0m, paid: 100m,
            method: InvPaymentMethod.Cash);
        InvRepo.SaveDraft(doc);
        InvRepo.Post(doc.DocId, doc.Kind, Actor);

        // Settled at posting: balance 0, one allocation, paid_amount 100.
        var repo = new MasterCardRepository("SQLite", db.ConnectionString);
        Assert.Equal(0m, (await repo.GetPartyBalanceAsync(CompanyCode, "C", "CUST-1")).Balance);
        Assert.Equal(1m, Scalar(db, $"SELECT COUNT(*) FROM payment_allocations WHERE doc_id = {doc.DocId};"));
        Assert.Equal(100m, Scalar(db, $"SELECT paid_amount FROM sales_documents WHERE doc_id = {doc.DocId};"));

        var reversalId = InvRepo.ReversePostedInvoice(doc.DocId, InvDocKind.SalesInvoice, "step5 test", Actor);

        // Party balance restored to exactly zero net; allocations gone;
        // the original is back to unpaid; stock restored.
        var balance = await repo.GetPartyBalanceAsync(CompanyCode, "C", "CUST-1");
        Assert.Equal(balance.Debit, balance.Credit);
        Assert.Equal(0m, Scalar(db, $"SELECT COUNT(*) FROM payment_allocations WHERE doc_id = {doc.DocId};"));
        Assert.Equal(0m, Scalar(db, $"SELECT paid_amount FROM sales_documents WHERE doc_id = {doc.DocId};"));
        Assert.Equal(stockBefore, StockQty(db));

        // The reversal document stores the SAME grand total as the
        // original (header money fields copied), so stored totals cancel.
        var reversal = InvRepo.Load(reversalId, InvDocKind.SalesReturn);
        Assert.Equal(100m, reversal!.StoredGrandTotal);

        // GL nets to zero: cash, receivable, inventory and COGS accounts
        // individually, and revenue net of its contra (sales 301 vs
        // sales returns 302 are distinct accounts by design).
        decimal GlNet(string code) => Scalar(db, $"""
            SELECT COALESCE(SUM(l.debit),0) - COALESCE(SUM(l.credit),0)
            FROM journal_entry_lines l
            JOIN journal_entries e ON e.journal_entry_id = l.journal_entry_id
            JOIN chart_of_accounts a ON a.account_id = l.account_id
            WHERE e.status = 1 AND a.account_code = '{code}';
            """);
        Assert.Equal(0m, GlNet("131"));  // cash collected, then mirrored out
        Assert.Equal(0m, GlNet("1201")); // receivable
        Assert.Equal(0m, GlNet("1203")); // inventory
        Assert.Equal(0m, GlNet("401"));  // COGS
        Assert.Equal(0m, GlNet("301") + GlNet("302")); // revenue vs returns contra
    }

    // ------------------------------------------------------------ (e)

    [Fact]
    public async Task CreditGuard_EvaluatesTheStoredTotal()
    {
        using var db = await SeededAsync();
        // Limit 1,100: the stored 1,050 passes; the pre-Step-5 recomputed
        // figure (1,150, header discount dropped) would have blocked.
        var party = await SavePartyAsync(db, "C", "CUST-1", creditLimit: 1100m);

        var doc = BuildDoc(InvDocKind.SalesInvoice, party.LedgerId, "CUST-1",
            qty: 10m, price: 100m, taxRate: 15m, discount: 100m, extra: 0m, paid: 0m);
        InvRepo.SaveDraft(doc);
        InvRepo.Post(doc.DocId, doc.Kind, Actor); // must NOT throw

        // A further 100 on top of the posted 1,050 exceeds 1,100 and the
        // guard blocks with ACC-5006 — evaluated on stored figures.
        var second = BuildDoc(InvDocKind.SalesInvoice, party.LedgerId, "CUST-1",
            qty: 1m, price: 100m, taxRate: 0m, discount: 0m, extra: 0m, paid: 0m);
        InvRepo.SaveDraft(second);
        var error = Assert.Throws<AppError>(() => InvRepo.Post(second.DocId, second.Kind, Actor));
        Assert.Equal(ErrorCodes.CreditLimitExceeded, error.Code);
    }

    // ------------------------------------------------------------ kind guards

    [Fact]
    public async Task StockIn_WithParty_CreatesNoPartyLedgerRow()
    {
        using var db = await SeededAsync();
        var party = await SavePartyAsync(db, "S", "SUP-1", creditLimit: 0m);
        var stockBefore = StockQty(db);

        var doc = BuildDoc(InvDocKind.StockIn, party.LedgerId, "SUP-1",
            qty: 5m, price: 10m, taxRate: 0m, discount: 0m, extra: 0m, paid: 0m);
        InvRepo.SaveDraft(doc);
        InvRepo.Post(doc.DocId, doc.Kind, Actor);

        Assert.Equal(0m, Scalar(db, $"SELECT COUNT(*) FROM party_ledger WHERE doc_id = {doc.DocId};"));
        Assert.Equal(stockBefore + 5m, StockQty(db));
    }

    // Step 9 (F-1 closure): TRN/CNT are no longer blocked with
    // ACC-5014 — they post through their dedicated engines.
    [Fact]
    public async Task StockTransfer_PostsTwoSided_AndConservesValue()
    {
        using var db = await SeededAsync();
        using (var connection = new SqliteConnection(db.ConnectionString))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"INSERT INTO warehouses (warehouse_id, company_id, warehouse_code, warehouse_name_ar, warehouse_name_en, is_deleted) VALUES (2, '{CompanyCode}', 2, 'Second', 'Second', 0);";
            cmd.ExecuteNonQuery();
        }
        var doc = BuildDoc(InvDocKind.StockTransfer, null, "", qty: 3m, price: 10m, taxRate: 0m, discount: 0m, extra: 0m, paid: 0m);
        doc.TargetWarehouseId = 2;
        doc.TargetWarehouseName = "Second";
        InvRepo.SaveDraft(doc);

        // The destination persists with the document (migration 064).
        var saved = InvRepo.Load(doc.DocId, InvDocKind.StockTransfer);
        Assert.Equal(2, saved!.TargetWarehouseId);

        InvRepo.Post(doc.DocId, doc.Kind, Actor);

        Assert.Equal(997m, StockQty(db));
        Assert.Equal(3m, Scalar(db, "SELECT COALESCE(available_qty, 0) FROM material_stock_balances WHERE company_id = '" + CompanyCode + "' AND warehouse_id = 2 AND material_id = 42;"));
        // Value is conserved: 1,000 @ 40 = 40,000 before and after.
        Assert.Equal(40000m, Scalar(db, "SELECT COALESCE(SUM(stock_value), 0) FROM material_stock_balances WHERE company_id = '" + CompanyCode + "' AND material_id = 42;"));
        var posted = InvRepo.Load(doc.DocId, InvDocKind.StockTransfer);
        Assert.Equal(InvDocStatus.Posted, posted!.Status);
        Assert.StartsWith("TRF-", posted.JournalDocNo);
        // No party ledger, no GL: a transfer is not a sale or a payment.
        Assert.Equal(0m, Scalar(db, $"SELECT COUNT(*) FROM party_ledger WHERE doc_id = {doc.DocId};"));
        Assert.Equal(0m, Scalar(db, $"SELECT COUNT(*) FROM journal_entries WHERE source_doc_id = '{doc.DocId}';"));
    }

    [Fact]
    public async Task InventoryCount_PostsDeltaOnly_NotFullCountedQty()
    {
        using var db = await SeededAsync();
        Assert.Equal(1000m, StockQty(db)); // book quantity

        // Counted 1003 against book 1000: the movement must be +3,
        // not +1003 (the pre-Step-5 fabrication).
        var doc = BuildDoc(InvDocKind.InventoryCount, null, "", qty: 1003m, price: 10m, taxRate: 0m, discount: 0m, extra: 0m, paid: 0m);
        InvRepo.SaveDraft(doc);
        InvRepo.Post(doc.DocId, doc.Kind, Actor);

        Assert.Equal(1003m, StockQty(db));
        Assert.Equal(3m, Scalar(db, $"SELECT COALESCE(SUM(qty_in), 0) FROM material_stock_movements WHERE company_id = '{CompanyCode}' AND material_id = 42 AND movement_type = 'COUNT_CLOSE';"));
        var posted = InvRepo.Load(doc.DocId, InvDocKind.InventoryCount);
        Assert.Equal(InvDocStatus.Posted, posted!.Status);
    }

    [Fact]
    public void PostingPolicy_MatrixIsAsDesigned()
    {
        Assert.True(InvPostingPolicy.For(InvDocKind.SalesInvoice).PartyLedger);
        Assert.True(InvPostingPolicy.For(InvDocKind.PosSale).Settlement);
        Assert.False(InvPostingPolicy.For(InvDocKind.SalesReturn).Settlement);
        Assert.False(InvPostingPolicy.For(InvDocKind.Quotation).Gl);
        Assert.False(InvPostingPolicy.For(InvDocKind.Quotation).PartyLedger);
        Assert.False(InvPostingPolicy.For(InvDocKind.Quotation).Stock);
        Assert.False(InvPostingPolicy.For(InvDocKind.StockIn).PartyLedger);
        Assert.True(InvPostingPolicy.For(InvDocKind.StockIn).Stock);
        Assert.False(InvPostingPolicy.For(InvDocKind.OpeningStock).Gl);
        // Step 9: TRN/CNT are routed to their dedicated engines — no
        // generic effects, and no longer blocked.
        Assert.False(InvPostingPolicy.For(InvDocKind.StockTransfer).Blocked);
        Assert.False(InvPostingPolicy.For(InvDocKind.InventoryCount).Blocked);
        Assert.False(InvPostingPolicy.For(InvDocKind.StockTransfer).Gl);
        Assert.False(InvPostingPolicy.For(InvDocKind.StockTransfer).PartyLedger);
        Assert.False(InvPostingPolicy.For(InvDocKind.InventoryCount).Gl);
        Assert.False(InvPostingPolicy.For(InvDocKind.InventoryCount).PartyLedger);
    }

    // ------------------------------------------------------------ fixture

    // Step 9 (DS-02 / I-8): DocSearch opens the document it found.
    // Order-chain invoices are stored under the company GUID (the
    // order family's key) while InvRepo's native rows use the company
    // CODE; Load must find either key, or opening a converted invoice
    // from search read as "not found" and the window fell back to a
    // blank document.
    [Fact]
    public async Task Load_FindsGuidKeyedDocument_SoDocSearchOpensIt()
    {
        using var db = await SeededAsync();
        long docId;
        using (var connection = new SqliteConnection(db.ConnectionString))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO sales_documents
                    (company_id, doc_kind, doc_no, doc_date, party_name, currency_code,
                     exchange_rate, tax_rate, subtotal, discount_amount, tax_amount,
                     grand_total, paid_amount, status, is_deleted)
                VALUES ($guid, 'SAL', 'SAL-CHAIN-1', '2026-11-02', 'Chain Party', 'SAR',
                        1, 0, 250, 0, 0, 250, 0, 'Posted', 0);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$guid", db.CompanyId.ToString("D"));
            docId = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        var loaded = InvRepo.Load(docId, InvDocKind.SalesInvoice);
        Assert.NotNull(loaded);
        Assert.Equal(docId, loaded!.DocId);
        Assert.Equal("SAL-CHAIN-1", loaded.DocNo);
        // This fixture intentionally has no lines; verify the persisted
        // header amount rather than the line-derived display total.
        Assert.Equal(250m, loaded.StoredGrandTotal);
        Assert.Equal(InvDocStatus.Posted, loaded.Status);
    }

    private static InvDoc BuildDoc(InvDocKind kind, long? partyId, string partyCode,
        decimal qty, decimal price, decimal taxRate, decimal discount, decimal extra, decimal paid,
        InvPaymentMethod method = InvPaymentMethod.Credit)
        => new()
        {
            Kind = kind,
            DocNo = InvRepo.NextDocNo(kind),
            DocDate = new DateTime(2026, 3, 10),
            PartyId = partyId,
            PartyCode = partyCode,
            PartyName = "Posting Party",
            WarehouseId = 1,
            CurrencyCode = "SAR",
            LinesDiscountAmount = discount,
            LinesExtraAmount = extra,
            PaidAmount = paid,
            PaymentMethod = method,
            Lines = { new InvLine { LineNo = 1, MaterialId = 42, MaterialCode = "MAT-42", Qty = qty, UnitPrice = price, TaxRate = taxRate, WarehouseId = 1 } }
        };

    private static async Task<Party> SavePartyAsync(TestDb db, string kind, string code, decimal creditLimit)
    {
        var repo = new MasterCardRepository("SQLite", db.ConnectionString);
        var party = new Party { Kind = kind, Code = code, NameEn = "Posting Party", CreditLimit = creditLimit };
        await repo.SavePartyAsync(CompanyCode, party, Actor);
        Assert.True(party.LedgerId > 0);
        return party;
    }

    private sealed record EntryRow(System.Collections.Generic.List<(string Code, decimal Debit, decimal Credit)> Lines);

    private static EntryRow SingleEntry(TestDb db, string sourceType, long sourceId)
    {
        using var connection = new SqliteConnection(db.ConnectionString);
        connection.Open();
        string id;
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT journal_entry_id FROM journal_entries
                WHERE source_doc_type = $type AND source_doc_id = $source AND status = 1 AND reversal_of_entry_id IS NULL;
                """;
            cmd.Parameters.AddWithValue("$type", sourceType);
            cmd.Parameters.AddWithValue("$source", sourceId.ToString(CultureInfo.InvariantCulture));
            id = Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture)!;
            Assert.False(string.IsNullOrWhiteSpace(id), $"No posted GL entry for {sourceType}/{sourceId}");
        }
        var lines = new System.Collections.Generic.List<(string, decimal, decimal)>();
        using var linesCmd = connection.CreateCommand();
        linesCmd.CommandText = """
            SELECT a.account_code, l.debit, l.credit FROM journal_entry_lines l
            JOIN chart_of_accounts a ON a.account_id = l.account_id
            WHERE l.journal_entry_id = $id;
            """;
        linesCmd.Parameters.AddWithValue("$id", id);
        using var lr = linesCmd.ExecuteReader();
        while (lr.Read()) lines.Add((lr.GetString(0), lr.GetDecimal(1), lr.GetDecimal(2)));
        return new EntryRow(lines);
    }

    private static decimal StoredGrand(TestDb db, string table, long docId)
        => Scalar(db, $"SELECT grand_total FROM {table} WHERE doc_id = {docId};");

    private static decimal LedgerSum(TestDb db, long partyId, string docKind, bool debit)
        => Scalar(db, $"SELECT COALESCE(SUM({(debit ? "debit" : "credit")}), 0) FROM party_ledger WHERE company_id = '{CompanyCode}' AND party_id = {partyId} AND doc_kind = '{docKind}';");

    private static decimal StockQty(TestDb db)
        => Scalar(db, "SELECT COALESCE(available_qty, 0) FROM material_stock_balances WHERE company_id = '" + CompanyCode + "' AND warehouse_id = 1 AND material_id = 42;");

    private static decimal Scalar(TestDb db, string sql)
    {
        using var connection = new SqliteConnection(db.ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToDecimal(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
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
        var root = Path.Combine(Path.GetTempPath(), "invpost-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var dbPath = Path.Combine(root, "invpost.db");
        var companyId = Guid.NewGuid();
        var connectionString = $"Data Source={dbPath};";

        Assert.True((await SchemaMigrator.ApplyAllAsync("SQLite", dbPath)).Success);
        PointAppAt(dbPath);
        AppState.SetCompany(CompanyCode, "InvPost Test Co", "InvPost Test Co");

        using (var connection = new SqliteConnection(connectionString))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "INSERT INTO companies (company_id, company_code, company_name_ar, company_name_en) VALUES ($id, $code, 'InvPost Test Co', 'InvPost Test Co');";
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
