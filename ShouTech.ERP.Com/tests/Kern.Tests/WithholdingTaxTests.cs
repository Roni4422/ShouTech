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
using ShouTech.Application.Services;
using ShouTech.Domain;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;
using ShouTech.Infrastructure;
using Xunit;

namespace Kern.Tests;

/// <summary>
/// W1 — Withholding tax / TDS (migration 099). The same scenarios are
/// EXECUTED headlessly on Linux by tools/W1Harness (36/36 PASS, see
/// docs/PURCHASE_CYCLE_COMPLETION.md §4 W1); these facts are the
/// Windows-suite mirror (execution owed on Windows, net8.0-windows
/// testhost, as with the rest of the suite).
/// </summary>
public sealed class WithholdingTaxTests
{
    private const string Actor = "w1-test";
    private const string CompanyCode = "WHT";
    private const string ChainCode = "WHTC";

    [Fact]
    public async Task ManualInvoice_Withholding_PostsGrossApNetSupplierAndRegister()
    {
        using var db = await SeededAsync();
        var supplier = await SavePartyAsync(db, "SUP-W1");
        var docId = PostManual(db, supplier, 10000m, 15m, CompanyCode + "-VAT15", "WHT5");

        // GL: Dr Purchases 10,000 / Dr Input VAT 1,500 / Cr AP GROSS
        // 11,500 / Dr AP 500 (deduction) / Cr Withholding payable 500.
        Assert.Equal(10000m, Gl(db, docId, "402").Debit);
        Assert.Equal(1500m, Gl(db, docId, "1205").Debit);
        var ap = Gl(db, docId, "2201");
        Assert.Equal(11500m, ap.Credit);
        Assert.Equal(500m, ap.Debit);
        Assert.Equal(500m, Gl(db, docId, "2215").Credit);
        // Supplier balance NET; header + register carry the outcome.
        Assert.Equal(11000m, Scalar(db, $"SELECT COALESCE(SUM(credit)-SUM(debit),0) FROM party_ledger WHERE company_id='{CompanyCode}' AND party_id={supplier.LedgerId};"));
        Assert.Equal(500m, Scalar(db, $"SELECT withholding_amount FROM purchase_documents WHERE doc_id={docId};"));
        Assert.Equal(10000m, Scalar(db, $"SELECT withholding_base FROM purchase_documents WHERE doc_id={docId};"));
        Assert.Equal(1m, Scalar(db, $"SELECT COUNT(*) FROM tax_transactions WHERE company_code='{CompanyCode}' AND source_doc_id={docId} AND tax_type='WITHHOLDING' AND tax_amount=500 AND line_no=0;"));
        Assert.Equal(1m, Scalar(db, $"SELECT COUNT(*) FROM tax_transactions WHERE company_code='{CompanyCode}' AND source_doc_id={docId} AND tax_type='VAT' AND tax_amount=1500;"));
    }

    [Fact]
    public async Task Allocation_SettlesNet_AndRejectsAboveNet()
    {
        using var db = await SeededAsync();
        var supplier = await SavePartyAsync(db, "SUP-W1");
        var docId = PostManual(db, supplier, 10000m, 15m, CompanyCode + "-VAT15", "WHT5");
        var paymentId = WritePayment(db, supplier.LedgerId, 11500m);

        var recon = new ReconciliationRepository("SQLite", db.ConnectionString);
        var open = Assert.Single(await recon.GetOpenDocumentsAsync(CompanyCode, "S"), r => r.DocId == docId);
        Assert.Equal(11000m, open.OpenAmount);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            recon.AllocateAsync(CompanyCode, "S", paymentId, "PURCHASE", docId, 11500m, Actor));
        Assert.Equal(0m, Scalar(db, $"SELECT COUNT(*) FROM payment_allocations WHERE company_id='{CompanyCode}' AND doc_id={docId};"));
        await recon.AllocateAsync(CompanyCode, "S", paymentId, "PURCHASE", docId, 11000m, Actor);
        Assert.Equal(11000m, Scalar(db, $"SELECT paid_amount FROM purchase_documents WHERE doc_id={docId};"));
        Assert.DoesNotContain(await recon.GetOpenDocumentsAsync(CompanyCode, "S"), r => r.DocId == docId);
    }

    [Fact]
    public async Task Reversal_MirrorsWithholding_AndRegister()
    {
        using var db = await SeededAsync();
        var supplier = await SavePartyAsync(db, "SUP-W1");
        var docId = PostManual(db, supplier, 10000m, 15m, CompanyCode + "-VAT15", "WHT5");

        InvRepo.ReversePostedInvoice(docId, InvDocKind.PurchaseInvoice, "W1 test reversal", Actor);

        Assert.Equal(11000m, Scalar(db, $"SELECT debit FROM party_ledger WHERE company_id='{CompanyCode}' AND doc_kind='PRT' AND party_id={supplier.LedgerId};"));
        Assert.Equal(0m, Scalar(db, $"SELECT COALESCE(SUM(credit)-SUM(debit),0) FROM party_ledger WHERE company_id='{CompanyCode}' AND party_id={supplier.LedgerId};"));
        Assert.Equal(1m, Scalar(db, $"SELECT COUNT(*) FROM tax_transactions WHERE company_code='{CompanyCode}' AND tax_type='WITHHOLDING' AND tax_amount=-500 AND source_doc_kind='PRT';"));
        Assert.Equal(1m, Scalar(db, $"SELECT COUNT(*) FROM tax_transactions WHERE company_code='{CompanyCode}' AND source_doc_id={docId} AND tax_type='WITHHOLDING' AND tax_amount=500;"));
        Assert.Equal(0m, Scalar(db, """
            SELECT COALESCE(SUM(l.credit)-SUM(l.debit),0) FROM journal_entry_lines l
            JOIN chart_of_accounts a ON a.account_id=l.account_id
            JOIN journal_entries e ON e.journal_entry_id=l.journal_entry_id
            WHERE a.account_code='2215' AND e.status=1
            """));
    }

    [Fact]
    public async Task BelowThreshold_EvaluatedWithoutWithholding()
    {
        using var db = await SeededAsync();
        var supplier = await SavePartyAsync(db, "SUP-W1");
        var docId = PostManual(db, supplier, 10000m, 0m, string.Empty, "WHT-T");

        Assert.Equal(0m, Scalar(db, $"SELECT withholding_amount FROM purchase_documents WHERE doc_id={docId};"));
        Assert.Equal("WHT-T", ScalarText(db, $"SELECT withholding_tax_code FROM purchase_documents WHERE doc_id={docId};"));
        Assert.Equal(10000m, Scalar(db, $"SELECT credit FROM party_ledger WHERE company_id='{CompanyCode}' AND doc_id={docId} AND doc_kind='PUR';"));
        Assert.Equal(1m, Scalar(db, $"SELECT COUNT(*) FROM tax_transactions WHERE company_code='{CompanyCode}' AND source_doc_id={docId} AND tax_type='WITHHOLDING' AND tax_amount=0;"));
    }

    [Fact]
    public async Task CumulativeThreshold_CrossingWithholdsExcessOnly()
    {
        using var db = await SeededAsync();
        var supplier = await SavePartyAsync(db, "SUP-W2");
        var first = PostManual(db, supplier, 6000m, 0m, string.Empty, "WHT-C");
        var second = PostManual(db, supplier, 6000m, 0m, string.Empty, "WHT-C");

        Assert.Equal(0m, Scalar(db, $"SELECT withholding_amount FROM purchase_documents WHERE doc_id={first};"));
        Assert.Equal(100m, Scalar(db, $"SELECT withholding_amount FROM purchase_documents WHERE doc_id={second};"));
        Assert.Equal(11900m, Scalar(db, $"SELECT COALESCE(SUM(credit)-SUM(debit),0) FROM party_ledger WHERE company_id='{CompanyCode}' AND party_id={supplier.LedgerId};"));
    }

    [Fact]
    public async Task UnknownWithholdingCode_FailsWithCataloguedError()
    {
        using var db = await SeededAsync();
        var supplier = await SavePartyAsync(db, "SUP-W1");
        var doc = BuildDoc(supplier, 1000m, 0m, string.Empty);
        InvRepo.SaveDraft(doc);
        Exec(db, $"UPDATE purchase_documents SET withholding_tax_code='NOPE' WHERE doc_id={doc.DocId};");
        var error = Assert.Throws<AppError>(() => InvRepo.Post(doc.DocId, InvDocKind.PurchaseInvoice, Actor));
        Assert.Equal(ErrorCodes.WithholdingRuleNotFound, error.Code);
    }

    [Fact]
    public async Task ChainInvoice_WithholdsIdentically_ViaSupplierDefault()
    {
        using var db = await SeededAsync();
        SeedChainCompany(db);
        AppState.SetCompany(ChainCode, "W1 Chain Co", "W1 Chain Co");
        var orders = new OrderDocumentRepository("SQLite", db.ConnectionString);
        var service = new OrderService(orders, new CommercialDocumentRepository("SQLite", db.ConnectionString));

        var poId = await service.CreateOrderAsync(
            new CommercialDocumentDraft(db.ChainCompanyId, CommercialDocKinds.PurchaseOrder, "PO-W1-1",
                new DateOnly(2026, 3, 10), 9, "Chain Supplier", 2, "SAR", 1m, null,
                new[] { new CommercialLineDraft(142, "MAT-42", "Item 42", "PCS", 1m, 10000m, 0m, 15m, 2, null, TaxCode: ChainCode + "-VAT15") },
                "SUP-1"), Actor);
        await service.PostOrderAsync(CommercialDocSide.Purchase, poId, Actor);
        await service.CreatePurchaseReceiptAsync(db.ChainCompanyId, poId, new DateOnly(2026, 3, 10),
            new[] { new CommercialLineDraft(142, "MAT-42", "Item 42", "PCS", 1m, 10000m, 0m, 15m, 2, 1, TaxCode: ChainCode + "-VAT15") }, Actor);
        var (invoiceId, _) = await service.CreatePurchaseInvoiceAsync(db.ChainCompanyId, poId, new DateOnly(2026, 3, 10),
            new[] { new CommercialLineDraft(142, "MAT-42", "Item 42", "PCS", 1m, 10000m, 0m, 15m, 2, 1, TaxCode: ChainCode + "-VAT15") }, Actor, "BILL-W1-1", new DateOnly(2026, 3, 10));

        var ap = Gl(db, invoiceId, "2201", db.ChainCompanyId);
        Assert.Equal(11500m, ap.Credit);
        Assert.Equal(500m, ap.Debit);
        Assert.Equal(500m, Gl(db, invoiceId, "2215", db.ChainCompanyId).Credit);
        Assert.Equal(1500m, Gl(db, invoiceId, "1205", db.ChainCompanyId).Debit);
        Assert.Equal(11000m, Scalar(db, $"SELECT COALESCE(SUM(credit)-SUM(debit),0) FROM party_ledger WHERE company_id='{ChainCode}' AND party_id=9;"));
        Assert.Equal(500m, Scalar(db, $"SELECT withholding_amount FROM purchase_documents WHERE doc_id={invoiceId};"));
        Assert.Equal(1m, Scalar(db, $"SELECT COUNT(*) FROM tax_transactions WHERE company_code='{ChainCode}' AND source_doc_id={invoiceId} AND tax_type='WITHHOLDING' AND tax_amount=500;"));
        var recon = new ReconciliationRepository("SQLite", db.ConnectionString);
        var open = Assert.Single(await recon.GetOpenDocumentsAsync(ChainCode, "S"), r => r.DocId == invoiceId);
        Assert.Equal(11000m, open.OpenAmount);
    }

    [Fact]
    public async Task RegisterTotal_TiesToWithholdingPayableGlMovement()
    {
        using var db = await SeededAsync();
        var supplier = await SavePartyAsync(db, "SUP-W1");
        PostManual(db, supplier, 10000m, 15m, CompanyCode + "-VAT15", "WHT5");
        var register = Scalar(db, $"SELECT COALESCE(SUM(tax_amount),0) FROM tax_transactions WHERE company_code='{CompanyCode}' AND tax_type='WITHHOLDING';");
        var gl = Scalar(db, """
            SELECT COALESCE(SUM(l.credit)-SUM(l.debit),0) FROM journal_entry_lines l
            JOIN chart_of_accounts a ON a.account_id=l.account_id
            JOIN journal_entries e ON e.journal_entry_id=l.journal_entry_id
            WHERE a.account_code='2215' AND e.status=1
            """);
        Assert.Equal(500m, register);
        Assert.Equal(register, gl);
    }

    // ------------------------------------------------------------ fixture

    private static InvDoc BuildDoc(Party supplier, decimal price, decimal taxRate, string taxCode)
        => new()
        {
            Kind = InvDocKind.PurchaseInvoice,
            DocNo = InvRepo.NextDocNo(InvDocKind.PurchaseInvoice),
            DocDate = new DateTime(2026, 3, 10),
            PartyId = supplier.LedgerId,
            PartyCode = supplier.Code,
            PartyName = "W1 Party",
            WarehouseId = 1,
            CurrencyCode = "SAR",
            Lines = { new InvLine { LineNo = 1, MaterialId = 42, MaterialCode = "MAT-42", Qty = 1m, UnitPrice = price, TaxCode = taxCode, TaxRate = taxRate, WarehouseId = 1 } }
        };

    private static long PostManual(TestDb db, Party supplier, decimal price, decimal taxRate, string taxCode, string withholdingSelection)
    {
        var doc = BuildDoc(supplier, price, taxRate, taxCode);
        InvRepo.SaveDraft(doc);
        if (withholdingSelection.Length > 0)
            Exec(db, $"UPDATE purchase_documents SET withholding_tax_code='{withholdingSelection}' WHERE doc_id={doc.DocId};");
        InvRepo.Post(doc.DocId, InvDocKind.PurchaseInvoice, Actor);
        return doc.DocId;
    }

    private static long WritePayment(TestDb db, long partyId, decimal amount)
    {
        using var connection = new SqliteConnection(db.ConnectionString);
        connection.Open();
        using var tx = connection.BeginTransaction();
        var id = PartyLedger.WriteEntry(connection, tx, "SQLite", CompanyCode, "S", partyId,
            "PAY", null, "PAY-W1-T", "2026-03-11", amount, 0m, "SAR", "Supplier payment", exchangeRate: 1m);
        tx.Commit();
        return id;
    }

    private static (decimal Debit, decimal Credit) Gl(TestDb db, long docId, string account, Guid? company = null)
    {
        var companyId = company ?? db.CompanyId;
        return (Scalar(db, $"SELECT COALESCE(SUM(l.debit),0) FROM journal_entry_lines l JOIN chart_of_accounts a ON a.account_id=l.account_id JOIN journal_entries e ON e.journal_entry_id=l.journal_entry_id WHERE e.source_doc_type='PUR' AND e.source_doc_id='{docId}' AND e.company_id='{companyId:D}' AND a.account_code='{account}' AND e.status=1;"),
                Scalar(db, $"SELECT COALESCE(SUM(l.credit),0) FROM journal_entry_lines l JOIN chart_of_accounts a ON a.account_id=l.account_id JOIN journal_entries e ON e.journal_entry_id=l.journal_entry_id WHERE e.source_doc_type='PUR' AND e.source_doc_id='{docId}' AND e.company_id='{companyId:D}' AND a.account_code='{account}' AND e.status=1;"));
    }

    private static async Task<Party> SavePartyAsync(TestDb db, string code)
    {
        var repo = new MasterCardRepository("SQLite", db.ConnectionString);
        var party = new Party { Kind = "S", Code = code, NameEn = "W1 Party", CreditLimit = 1000000m };
        await repo.SavePartyAsync(CompanyCode, party, Actor);
        Assert.True(party.LedgerId > 0);
        return party;
    }

    private static void Exec(TestDb db, string sql)
    {
        using var connection = new SqliteConnection(db.ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static decimal Scalar(TestDb db, string sql)
    {
        using var connection = new SqliteConnection(db.ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToDecimal(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static string ScalarText(TestDb db, string sql)
    {
        using var connection = new SqliteConnection(db.ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private sealed class TestDb : IDisposable
    {
        public required string Root { get; init; }
        public required string DbPath { get; init; }
        public required Guid CompanyId { get; init; }
        public Guid ChainCompanyId { get; init; }
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
        var root = Path.Combine(Path.GetTempPath(), "w1-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var dbPath = Path.Combine(root, "w1.db");
        var companyId = Guid.NewGuid();
        var chainCompanyId = Guid.NewGuid();
        var connectionString = $"Data Source={dbPath};";

        var migration = await SchemaMigrator.ApplyAllAsync("SQLite", dbPath);
        Assert.True(migration.Success, migration.Message);

        var settings = AppCfgSvc.Current;
        settings.ConnectionStrings["Default"] = new ConnectionStringSettings { Provider = "SQLite", ConnectionString = dbPath };
        settings.Database ??= new DatabaseSettings();
        settings.Database.DefaultProvider = "SQLite";
        AppState.SetCompany(CompanyCode, "W1 Test Co", "W1 Test Co");

        var db = new TestDb { Root = root, DbPath = dbPath, CompanyId = companyId, ChainCompanyId = chainCompanyId };
        Exec(db, $"INSERT INTO companies (company_id, company_code, company_name_ar, company_name_en) VALUES ('{companyId:D}', '{CompanyCode}', 'W1 Co', 'W1 Co');");
        foreach (var code in new[] { CompanyCode })
        {
            Exec(db, $"""
                INSERT INTO company_tax_settings(company_code, country_code, tax_enabled, tax_scheme) VALUES ('{code}', 'SA', 1, 'VAT');
                INSERT INTO company_tax_rules (company_code, country_code, tax_code, tax_name, tax_type, tax_treatment, rate, calculation_mode, is_recoverable, output_account_code, input_account_code, effective_from, is_default, is_active)
                  VALUES ('{code}', 'SA', '{code}-VAT15', 'Standard VAT', 'VAT', 'Standard', 15, 'Exclusive', 1, '2205', '1205', '2026-01-01', 1, 1);
                INSERT INTO company_tax_rules (company_code, country_code, tax_code, tax_name, tax_type, tax_treatment, rate, calculation_mode, is_recoverable, output_account_code, input_account_code, effective_from, is_default, is_active, is_withholding, withholding_account_code, withholding_threshold_amount, withholding_threshold_mode)
                  VALUES ('{code}', 'SA', 'WHT5', 'Withholding 5%', 'WITHHOLDING', 'Withholding', 5, 'Exclusive', 0, '', '2215', '2026-01-01', 0, 1, 1, '2215', NULL, 'PerDocument');
                INSERT INTO company_tax_rules (company_code, country_code, tax_code, tax_name, tax_type, tax_treatment, rate, calculation_mode, is_recoverable, output_account_code, input_account_code, effective_from, is_default, is_active, is_withholding, withholding_account_code, withholding_threshold_amount, withholding_threshold_mode)
                  VALUES ('{code}', 'SA', 'WHT-T', 'Withholding threshold', 'WITHHOLDING', 'Withholding', 5, 'Exclusive', 0, '', '2215', '2026-01-01', 0, 1, 1, '2215', 20000, 'PerDocument');
                INSERT INTO company_tax_rules (company_code, country_code, tax_code, tax_name, tax_type, tax_treatment, rate, calculation_mode, is_recoverable, output_account_code, input_account_code, effective_from, is_default, is_active, is_withholding, withholding_account_code, withholding_threshold_amount, withholding_threshold_mode)
                  VALUES ('{code}', 'SA', 'WHT-C', 'Withholding cumulative', 'WITHHOLDING', 'Withholding', 5, 'Exclusive', 0, '', '2215', '2026-01-01', 0, 1, 1, '2215', 10000, 'Cumulative');
                """);
        }

        var structure = new AccountingStructureRepository("SQLite", connectionString);
        await structure.CreateFiscalYearAsync(companyId, 2026, Actor);

        foreach (var (code, name, type) in new[]
        {
            ("131", "Cash", "ASSET"), ("132", "Bank", "ASSET"), ("1201", "Receivable", "ASSET"), ("1203", "Inventory", "ASSET"),
            ("1205", "Input VAT", "ASSET"), ("2201", "Payable", "LIABILITY"), ("2203", "GRNI", "LIABILITY"), ("2205", "Output VAT", "LIABILITY"),
            ("2215", "Withholding Tax Payable", "LIABILITY"), ("301", "Sales", "REVENUE"), ("302", "Sales Returns", "REVENUE"),
            ("401", "COGS", "EXPENSE"), ("402", "Purchases", "EXPENSE"), ("403", "Purchase Returns", "EXPENSE"), ("415", "PPV", "EXPENSE")
        })
            Exec(db, $"INSERT INTO chart_of_accounts (account_id, company_id, account_code, account_name, account_type, is_postable, is_active, is_deleted) VALUES ('{Guid.NewGuid():D}', '{companyId:D}', '{code}', '{name}', '{type}', 1, 1, 0);");

        Exec(db, $"INSERT INTO materials (material_id, company_id, code, name_ar, name_en, is_stocked, avg_cost, is_deleted) VALUES (42, '{CompanyCode}', 'MAT-42', 'Item', 'Item', 1, 40, 0);");
        Exec(db, $"INSERT INTO warehouses (warehouse_id, company_id, warehouse_code, warehouse_name_ar, warehouse_name_en, is_deleted) VALUES (1, '{CompanyCode}', 1, 'Main', 'Main', 0);");
        Exec(db, $"INSERT INTO material_stock_balances (company_id, warehouse_id, material_id, available_qty, average_cost, last_cost) VALUES ('{CompanyCode}', 1, 42, 1000, 40, 40);");
        return db;
    }

    private static void SeedChainCompany(TestDb db)
    {
        var chain = db.ChainCompanyId;
        Exec(db, $"INSERT INTO companies (company_id, company_code, company_name_ar, company_name_en) VALUES ('{chain:D}', '{ChainCode}', 'W1 Chain Co', 'W1 Chain Co');");
        Exec(db, $"""
            INSERT INTO company_tax_settings(company_code, country_code, tax_enabled, tax_scheme) VALUES ('{ChainCode}', 'SA', 1, 'VAT');
            INSERT INTO company_tax_rules (company_code, country_code, tax_code, tax_name, tax_type, tax_treatment, rate, calculation_mode, is_recoverable, output_account_code, input_account_code, effective_from, is_default, is_active)
              VALUES ('{ChainCode}', 'SA', '{ChainCode}-VAT15', 'Standard VAT', 'VAT', 'Standard', 15, 'Exclusive', 1, '2205', '1205', '2026-01-01', 1, 1);
            INSERT INTO company_tax_rules (company_code, country_code, tax_code, tax_name, tax_type, tax_treatment, rate, calculation_mode, is_recoverable, output_account_code, input_account_code, effective_from, is_default, is_active, is_withholding, withholding_account_code)
              VALUES ('{ChainCode}', 'SA', 'WHT5', 'Withholding 5%', 'WITHHOLDING', 'Withholding', 5, 'Exclusive', 0, '', '2215', '2026-01-01', 0, 1, 1, '2215');
            """);
        Exec(db, $"INSERT INTO fiscal_years (company_id, fiscal_year, database_name, start_date, end_date, is_current, is_closed) VALUES ('{chain:D}', 2026, '', '2026-01-01', '2026-12-31', 1, 0);");
        var yearId = Scalar(db, "SELECT last_insert_rowid();");
        for (var m = 1; m <= 12; m++)
            Exec(db, $"INSERT INTO fiscal_periods (fiscal_period_id, company_id, fiscal_year_id, period_number, period_name, start_date, end_date, status) VALUES ('{Guid.NewGuid():D}', '{chain:D}', {(long)yearId}, {m}, '2026-{m:D2}', '2026-{m:D2}-01', '{new DateTime(2026, m, 1).AddMonths(1).AddDays(-1):yyyy-MM-dd}', 'OPEN');");
        foreach (var (code, name, type) in new[]
        {
            ("1203", "Inventory", "ASSET"), ("1205", "Input VAT", "ASSET"), ("2201", "Payable", "LIABILITY"),
            ("2203", "GRNI", "LIABILITY"), ("2215", "Withholding Tax Payable", "LIABILITY"),
            ("402", "Purchases", "EXPENSE"), ("415", "PPV", "EXPENSE"), ("131", "Cash", "ASSET"), ("1215", "Advances to Suppliers", "ASSET")
        })
            Exec(db, $"INSERT INTO chart_of_accounts (account_id, company_id, account_code, account_name, account_type, is_postable, is_active, is_deleted) VALUES ('{Guid.NewGuid():D}', '{chain:D}', '{code}', '{name}', '{type}', 1, 1, 0);");
        Exec(db, $"INSERT INTO parties (id, company_id, kind, code, name_en, credit_limit, is_active, is_deleted, created_by, ledger_id, withholding_tax_code) VALUES ('{Guid.NewGuid():D}', '{ChainCode}', 'S', 'SUP-1', 'Chain Supplier', 0, 1, 0, '{Actor}', 9, 'WHT5');");
        Exec(db, $"INSERT INTO materials (material_id, company_id, code, name_ar, name_en, is_stocked, is_service, cost_method, allow_negative, is_deleted) VALUES (142, '{ChainCode}', 'MAT-42', 'Item', 'Item', 1, 0, 'WeightedAvg', 0, 0);");
        Exec(db, $"INSERT INTO warehouses (warehouse_id, company_id, warehouse_code, warehouse_name_ar, warehouse_name_en, is_active, is_deleted) VALUES (2, '{ChainCode}', 'WH-01', 'Main', 'Main', 1, 0);");
        Exec(db, $"INSERT INTO material_stock_balances (company_id, warehouse_id, material_id, available_qty, reserved_qty, average_cost, last_cost, stock_value, is_deleted) VALUES ('{ChainCode}', 2, 142, 100, 0, 10, 10, 1000, 0);");
    }
}
