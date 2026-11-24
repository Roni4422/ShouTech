using System;
using System.Collections.Generic;
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
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;
using ShouTech.Infrastructure;
using Xunit;

namespace Kern.Tests;

/// <summary>
/// Purchase-cycle completion, workstream A2 (Y2 GRNI exceptions + Y3
/// payable aging by due date), against real migrated SQLite databases:
///  * the GRNI detail query returns an aged open receipt with its
///    remaining qty/value and reports ZERO remaining for a receipt that
///    was fully invoiced through purchase_receipt_invoice_matches (078);
///  * InvoicedWithoutReceiptAsync detects a manual-path (InvRepo)
///    purchase invoice with no 078 match rows and excludes the chain
///    invoice that has matches;
///  * PayableDueDateAgingAsync places open invoices in the right
///    due-date bucket as of a given date (incl. the doc-date fallback
///    for invoices with no due_date, and exclusion of fully paid ones);
///  * due_date capture: InvRepo.SaveDraft stamps doc date + supplier
///    credit_days when no explicit due date is given, persists an
///    explicit one verbatim, and Load reads it back; the order-chain
///    invoice conversion stamps the same default (migration 109);
///  * period-end reconciliation (owner report §5/§11.4/§12): chain
///    receipts + invoices across two periods tie GRNI to the real
///    GL 2203 balance as of period-1 end, exclude period-2 activity
///    from both sides, and the aged exception crosses the threshold
///    only at the later as-of date.
/// Execution requires Windows (net8.0-windows testhost), like the rest
/// of Kern.Tests; compilation is verified headlessly.
/// </summary>
public sealed class GrniPayableAgingTests
{
    private const string Actor = "a2-test";
    private const string CompanyCode = "P2P-A2";
    private static readonly DateTime AsOf = new(2026, 3, 15);

    // ------------------------------------------------------------ Y2 (a)

    [Fact]
    public async Task GrniDetail_AgedOpenReceiptReturned_FullyInvoicedReceiptHasZeroRemaining()
    {
        using var db = await SeededAsync();
        var rows = await RptQuery.GoodsReceivedNotInvoicedAsync(DateTime.MinValue, AsOf, CancellationToken.None);

        var aged = Assert.Single(rows, r => string.Equals(r.GetValueOrDefault("ReceiptNo")?.ToString(), "RCV-AGED", StringComparison.Ordinal));
        Assert.Equal(10m, Dec(aged.GetValueOrDefault("RemainingQty")));
        Assert.Equal(200m, Dec(aged.GetValueOrDefault("RemainingBaseAmount")));
        // Receipt dated 2025-12-01: 104 days old at the as-of date, so it
        // clears the window's default 90-day exception threshold.
        var receiptDate = DateTime.Parse(Convert.ToString(aged.GetValueOrDefault("ReceiptDate"), CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture);
        Assert.True((AsOf - receiptDate.Date).Days >= 90);

        var invoiced = Assert.Single(rows, r => string.Equals(r.GetValueOrDefault("ReceiptNo")?.ToString(), "RCV-DONE", StringComparison.Ordinal));
        Assert.Equal(5m, Dec(invoiced.GetValueOrDefault("InvoicedQty")));
        Assert.Equal(0m, Dec(invoiced.GetValueOrDefault("RemainingQty")));
    }

    // ------------------------------------------------------------ Y2 (b)

    [Fact]
    public async Task InvoicedWithoutReceipt_ManualInvoiceDetected_ChainInvoiceWithMatchesExcluded()
    {
        using var db = await SeededAsync();
        var rows = await RptQuery.InvoicedWithoutReceiptAsync(AsOf, CancellationToken.None);
        var numbers = rows.Select(r => r.GetValueOrDefault("InvoiceNo")?.ToString()).ToList();

        Assert.Contains("PUR-M-AGED", numbers);
        Assert.DoesNotContain("PUR-CHAIN", numbers);
        var manual = Assert.Single(rows, r => string.Equals(r.GetValueOrDefault("InvoiceNo")?.ToString(), "PUR-M-AGED", StringComparison.Ordinal));
        Assert.Equal(500m, Dec(manual.GetValueOrDefault("InvoiceBaseAmount")));
        Assert.Equal("StepA2 Supplier", Convert.ToString(manual.GetValueOrDefault("SupplierName"), CultureInfo.InvariantCulture));
    }

    // ------------------------------------------------------------ Y3 aging

    [Fact]
    public async Task PayableDueDateAging_PlacesOpenInvoicesInRightBucketsAsOfDate()
    {
        using var db = await SeededAsync();
        var rows = await RptQuery.PayableDueDateAgingAsync(AsOf, CancellationToken.None);
        var byNo = rows.ToDictionary(r => r.GetValueOrDefault("InvoiceNo")?.ToString() ?? "", StringComparer.Ordinal);

        // Due 2026-01-31, as of 2026-03-15: 43 days overdue -> 31-60.
        var aged = byNo["PUR-M-AGED"];
        Assert.Equal(43L, Convert.ToInt64(aged.GetValueOrDefault("DaysOverdue"), CultureInfo.InvariantCulture));
        Assert.Equal("Bucket31_60", Convert.ToString(aged.GetValueOrDefault("Bucket"), CultureInfo.InvariantCulture));
        Assert.Equal(500m, Dec(aged.GetValueOrDefault("OpenBaseAmount")));

        // Due 2026-04-15: not yet due -> negative overdue, first bucket.
        var current = byNo["PUR-M-CURRENT"];
        Assert.True(Convert.ToInt64(current.GetValueOrDefault("DaysOverdue"), CultureInfo.InvariantCulture) < 0);
        Assert.Equal("Bucket0_30", Convert.ToString(current.GetValueOrDefault("Bucket"), CultureInfo.InvariantCulture));

        // No due_date stored (pre-094 invoice): falls back to doc date
        // 2025-10-01 -> 165 days overdue -> 90+ bucket.
        var legacy = byNo["PUR-M-LEGACY"];
        Assert.Equal(165L, Convert.ToInt64(legacy.GetValueOrDefault("DaysOverdue"), CultureInfo.InvariantCulture));
        Assert.Equal("Bucket90Plus", Convert.ToString(legacy.GetValueOrDefault("Bucket"), CultureInfo.InvariantCulture));

        // Fully paid invoices are not open and never age.
        Assert.DoesNotContain("PUR-M-PAID", byNo.Keys);
    }

    // ------------------------------------------------- Y3 capture: manual

    [Fact]
    public async Task ManualPurchaseInvoice_SaveDraft_StampsDueDateFromSupplierTerms_AndLoadReadsItBack()
    {
        using var db = await SeededAsync();

        var derived = new InvDoc
        {
            Kind = InvDocKind.PurchaseInvoice,
            DocNo = "PUR-T-DERIVED",
            DocDate = new DateTime(2026, 1, 1),
            PartyId = 9,
            PartyCode = "SUP-1",
            PartyName = "StepA2 Supplier",
            WarehouseId = 1,
            CurrencyCode = "SAR",
            ExchangeRate = 1m,
            Lines = { new InvLine { LineNo = 1, MaterialId = 42, MaterialCode = "MAT-42", Description = "Item 42", Qty = 2m, UnitPrice = 50m, WarehouseId = 1 } }
        };
        InvRepo.SaveDraft(derived);
        // Supplier credit_days = 45 -> 2026-01-01 + 45 days.
        Assert.Equal(new DateTime(2026, 2, 15), derived.DueDate);
        Assert.Equal("2026-02-15", ScalarText(db.ConnectionString,
            $"SELECT due_date FROM purchase_documents WHERE doc_id = {derived.DocId};"));
        var loaded = InvRepo.Load(derived.DocId, InvDocKind.PurchaseInvoice);
        Assert.NotNull(loaded);
        Assert.Equal(new DateTime(2026, 2, 15), loaded!.DueDate);

        var explicitDoc = new InvDoc
        {
            Kind = InvDocKind.PurchaseInvoice,
            DocNo = "PUR-T-EXPLICIT",
            DocDate = new DateTime(2026, 1, 1),
            DueDate = new DateTime(2026, 5, 1),
            PartyId = 9,
            PartyCode = "SUP-1",
            PartyName = "StepA2 Supplier",
            WarehouseId = 1,
            CurrencyCode = "SAR",
            ExchangeRate = 1m,
            Lines = { new InvLine { LineNo = 1, MaterialId = 42, MaterialCode = "MAT-42", Description = "Item 42", Qty = 1m, UnitPrice = 10m, WarehouseId = 1 } }
        };
        InvRepo.SaveDraft(explicitDoc);
        Assert.Equal("2026-05-01", ScalarText(db.ConnectionString,
            $"SELECT due_date FROM purchase_documents WHERE doc_id = {explicitDoc.DocId};"));
    }

    // -------------------------------------------------- Y3 capture: chain

    [Fact]
    public async Task ChainPurchaseInvoice_Conversion_StampsDueDateFromSupplierTerms()
    {
        using var db = await SeededAsync();
        SeedChainPrerequisites(db.ConnectionString, db.CompanyId);
        var orders = new OrderDocumentRepository("SQLite", db.ConnectionString);
        var chain = new CommercialDocumentRepository("SQLite", db.ConnectionString);
        var service = new OrderService(orders, chain);

        var orderId = await service.CreateOrderAsync(
            new CommercialDocumentDraft(
                db.CompanyId, CommercialDocKinds.PurchaseOrder, "PO-A2-1",
                new DateOnly(2026, 11, 2), 9, "StepA2 Supplier", 1, "SAR", 1m, null,
                new[] { new CommercialLineDraft(42, "MAT-42", "Item 42", "PCS", 5m, 12m) },
                "SUP-1", new DateOnly(2026, 11, 20), null),
            Actor);
        await service.PostOrderAsync(CommercialDocSide.Purchase, orderId, Actor);
        var (receiptId, _) = await service.CreatePurchaseReceiptAsync(
            db.CompanyId, orderId, new DateOnly(2026, 11, 3),
            new[] { new CommercialLineDraft(42, "MAT-42", "Item 42", "PCS", 5m, 12m) }, Actor);
        Assert.True(receiptId > 0);
        var (invoiceId, _) = await service.CreatePurchaseInvoiceAsync(
            db.CompanyId, orderId, new DateOnly(2026, 11, 4),
            new[] { new CommercialLineDraft(42, "MAT-42", "Item 42", "PCS", 5m, 12m) }, Actor);

        // Invoice date 2026-11-04 + supplier credit_days 45 = 2026-12-19.
        Assert.Equal("2026-12-19", ScalarText(db.ConnectionString,
            $"SELECT due_date FROM purchase_documents WHERE doc_id = {invoiceId};"));
    }

    // ------------------------------------- period-end reconciliation (§5)

    /// <summary>
    /// Owner report §5 / §11 item 4 / §12: receipts and invoices posted
    /// across TWO periods through the real chain (so GL 2203 carries real
    /// journal entries). As of period-1 end (2026-11-30): the GRNI total
    /// equals the GL 2203 balance as of that date, period-2 activity is
    /// excluded from BOTH sides, and the aged exception for the still-open
    /// receipt appears only once the age threshold is crossed.
    /// </summary>
    [Fact]
    public async Task GrniPeriodEnd_AsOfPeriod1End_TiesToGl2203_ExcludesPeriod2_AgedExceptionOnlyAfterThreshold()
    {
        // Clean database (no report fixtures): the tie-out aggregates the
        // whole company, so every open receipt here must have its GL side.
        using var db = await SeededAsync(withReportFixtures: false);
        SeedChainPrerequisites(db.ConnectionString, db.CompanyId);
        var orders = new OrderDocumentRepository("SQLite", db.ConnectionString);
        var chain = new CommercialDocumentRepository("SQLite", db.ConnectionString);
        var service = new OrderService(orders, chain);

        var orderId = await service.CreateOrderAsync(
            new CommercialDocumentDraft(
                db.CompanyId, CommercialDocKinds.PurchaseOrder, "PO-A2-PE",
                new DateOnly(2026, 11, 2), 9, "StepA2 Supplier", 1, "SAR", 1m, null,
                new[] { new CommercialLineDraft(42, "MAT-42", "Item 42", "PCS", 10m, 12m) },
                "SUP-1", new DateOnly(2026, 11, 20), null),
            Actor);
        await service.PostOrderAsync(CommercialDocSide.Purchase, orderId, Actor);

        // Period 1 (Nov 2026): receive 5 x 12 = 60 (Cr GRNI 60), then bill
        // 2 x 12 (Dr GRNI 24) -> open receipt base 36, GL 2203 balance 36.
        var (receiptAId, receiptANo) = await service.CreatePurchaseReceiptAsync(
            db.CompanyId, orderId, new DateOnly(2026, 11, 3),
            new[] { new CommercialLineDraft(42, "MAT-42", "Item 42", "PCS", 5m, 12m) }, Actor);
        Assert.True(receiptAId > 0);
        var (invoiceAId, _) = await service.CreatePurchaseInvoiceAsync(
            db.CompanyId, orderId, new DateOnly(2026, 11, 4),
            new[] { new CommercialLineDraft(42, "MAT-42", "Item 42", "PCS", 2m, 12m) }, Actor);
        Assert.True(invoiceAId > 0);

        // Period 2 (Dec 2026): receive the remaining 5 x 12 = 60.
        var (receiptBId, receiptBNo) = await service.CreatePurchaseReceiptAsync(
            db.CompanyId, orderId, new DateOnly(2026, 12, 2),
            new[] { new CommercialLineDraft(42, "MAT-42", "Item 42", "PCS", 5m, 12m) }, Actor);
        Assert.True(receiptBId > 0);

        var period1End = new DateTime(2026, 11, 30);

        // Detail as of period-1 end: ONLY receipt A, remaining 3 / 36.
        // Receipt B (period 2) must not leak into the detail side.
        var detail = await RptQuery.GoodsReceivedNotInvoicedAsync(DateTime.MinValue, period1End, CancellationToken.None);
        var detailA = Assert.Single(detail,
            r => string.Equals(r.GetValueOrDefault("ReceiptNo")?.ToString(), receiptANo, StringComparison.Ordinal));
        Assert.Equal(3m, Dec(detailA.GetValueOrDefault("RemainingQty")));
        Assert.Equal(36m, Dec(detailA.GetValueOrDefault("RemainingBaseAmount")));
        Assert.DoesNotContain(detail,
            r => string.Equals(r.GetValueOrDefault("ReceiptNo")?.ToString(), receiptBNo, StringComparison.Ordinal));

        // Reconciliation as of the SAME boundary: GRNI total == GL 2203
        // balance as of that date == 36, difference zero — period-2's
        // receipt (and its GL credit) excluded from both sides.
        var reconciliation = await RptQuery.GoodsReceivedNotInvoicedReconciliationAsync(null, period1End, CancellationToken.None);
        var tie = Assert.Single(reconciliation);
        Assert.Equal(36m, Dec(tie.GetValueOrDefault("OpenReceiptBase")));
        Assert.Equal(36m, Dec(tie.GetValueOrDefault("GlBalanceBase")));
        Assert.Equal(0m, Dec(tie.GetValueOrDefault("DifferenceBase")));

        // Sanity: as of period-2 the picture moves to 96 on both sides,
        // proving the period-1 result was the boundary, not missing data.
        var reconciliationP2 = await RptQuery.GoodsReceivedNotInvoicedReconciliationAsync(null, new DateTime(2026, 12, 31), CancellationToken.None);
        var tieP2 = Assert.Single(reconciliationP2);
        Assert.Equal(96m, Dec(tieP2.GetValueOrDefault("OpenReceiptBase")));
        Assert.Equal(96m, Dec(tieP2.GetValueOrDefault("GlBalanceBase")));
        Assert.Equal(0m, Dec(tieP2.GetValueOrDefault("DifferenceBase")));

        // Aged exception: receipt A is 27 days old at period-1 end (below
        // the 90-day threshold — no exception yet) and 104 days old on
        // 2027-02-15 with the same 36 still open (exception applies).
        var ageAtP1End = (period1End - new DateTime(2026, 11, 3)).Days;
        Assert.Equal(27, ageAtP1End);
        Assert.True(ageAtP1End < 90);
        var later = new DateTime(2027, 2, 15);
        var detailLater = await RptQuery.GoodsReceivedNotInvoicedAsync(DateTime.MinValue, later, CancellationToken.None);
        var agedRow = Assert.Single(detailLater,
            r => string.Equals(r.GetValueOrDefault("ReceiptNo")?.ToString(), receiptANo, StringComparison.Ordinal));
        Assert.Equal(36m, Dec(agedRow.GetValueOrDefault("RemainingBaseAmount")));
        var receiptDate = DateTime.Parse(Convert.ToString(agedRow.GetValueOrDefault("ReceiptDate"), CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture);
        Assert.Equal(104, (later - receiptDate.Date).Days);
        Assert.True((later - receiptDate.Date).Days >= 90);
    }

    // ------------------------------------------------------------ harness

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

    private static async Task<TestDb> SeededAsync(bool withReportFixtures = true)
    {
        var root = Path.Combine(Path.GetTempPath(), "a2-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var dbPath = Path.Combine(root, "a2.db");
        var companyId = Guid.NewGuid();
        var connectionString = $"Data Source={dbPath};";

        Assert.True((await SchemaMigrator.ApplyAllAsync("SQLite", dbPath)).Success);
        PointAppAt(dbPath);
        AppState.SetCompany(CompanyCode, "A2 Test Co", "A2 Test Co");

        var guid = companyId.ToString("D");
        void Exec(string sql)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        Exec($"INSERT INTO companies (company_id, company_code, company_name_ar, company_name_en, currency_code) VALUES ('{guid}', '{CompanyCode}', 'A2 Test Co', 'A2 Test Co', 'SAR');");
        Exec($"INSERT INTO parties (id, company_id, kind, code, name_en, name_ar, credit_days, is_active, is_deleted, ledger_id) VALUES ('{Guid.NewGuid():D}', '{CompanyCode}', 'S', 'SUP-1', 'StepA2 Supplier', 'StepA2 Supplier', 45, 1, 0, 9);");

        if (!withReportFixtures)
            return new TestDb { Root = root, DbPath = dbPath, CompanyId = companyId };

        // Chain documents (Guid company key): PO -> two receipts; one
        // receipt fully invoiced through a 078 match, one left open.
        Exec($"INSERT INTO purchase_documents (doc_id, company_id, doc_kind, doc_no, doc_date, party_id, party_name, currency_code, exchange_rate, grand_total, status, is_deleted) VALUES (100, '{guid}', 'PO', 'PO-A2', '2025-11-20', 9, 'StepA2 Supplier', 'SAR', 1, 300, 'Posted', 0);");
        Exec($"INSERT INTO purchase_documents (doc_id, company_id, doc_kind, doc_no, doc_date, party_id, party_name, currency_code, exchange_rate, grand_total, status, is_deleted) VALUES (101, '{guid}', 'RCV', 'RCV-AGED', '2025-12-01', 9, 'StepA2 Supplier', 'SAR', 1, 200, 'Posted', 0);");
        Exec($"INSERT INTO purchase_document_lines (line_id, doc_id, line_no, material_id, material_code, description, qty, unit_price, line_subtotal, line_tax, line_total) VALUES (1001, 101, 1, 42, 'MAT-42', 'Item 42', 10, 20, 200, 0, 200);");
        Exec($"INSERT INTO purchase_documents (doc_id, company_id, doc_kind, doc_no, doc_date, party_id, party_name, currency_code, exchange_rate, grand_total, status, is_deleted) VALUES (102, '{guid}', 'RCV', 'RCV-DONE', '2025-12-05', 9, 'StepA2 Supplier', 'SAR', 1, 100, 'Posted', 0);");
        Exec($"INSERT INTO purchase_document_lines (line_id, doc_id, line_no, material_id, material_code, description, qty, unit_price, line_subtotal, line_tax, line_total) VALUES (1002, 102, 1, 42, 'MAT-42', 'Item 42', 5, 20, 100, 0, 100);");
        Exec($"INSERT INTO document_links (company_id, source_doc_kind, source_doc_id, target_doc_kind, target_doc_id, created_by) VALUES ('{guid}', 'PO', 100, 'RCV', 101, '{Actor}');");
        Exec($"INSERT INTO document_links (company_id, source_doc_kind, source_doc_id, target_doc_kind, target_doc_id, created_by) VALUES ('{guid}', 'PO', 100, 'RCV', 102, '{Actor}');");
        Exec($"INSERT INTO purchase_documents (doc_id, company_id, doc_kind, doc_no, doc_date, party_id, party_name, currency_code, exchange_rate, grand_total, paid_amount, status, is_deleted) VALUES (103, '{guid}', 'PUR', 'PUR-CHAIN', '2025-12-06', 9, 'StepA2 Supplier', 'SAR', 1, 100, 0, 'Posted', 0);");
        Exec($"INSERT INTO purchase_document_lines (line_id, doc_id, line_no, material_id, material_code, description, qty, unit_price, line_subtotal, line_tax, line_total) VALUES (1003, 103, 1, 42, 'MAT-42', 'Item 42', 5, 20, 100, 0, 100);");
        Exec($"INSERT INTO purchase_receipt_invoice_matches (match_id, company_id, purchase_order_id, receipt_doc_id, receipt_line_id, invoice_doc_id, invoice_line_id, material_id, matched_qty, receipt_unit_cost, receipt_exchange_rate, invoice_unit_cost, invoice_exchange_rate, receipt_base_amount, invoice_base_amount, variance_base_amount, created_by) VALUES ('{Guid.NewGuid():D}', '{guid}', 100, 102, 1002, 103, 1003, 42, 5, 20, 1, 20, 1, 100, 100, 0, '{Actor}');");

        // Manual-path documents (code company key), as InvRepo writes them.
        Exec($"INSERT INTO purchase_documents (doc_id, company_id, doc_kind, doc_no, doc_date, party_id, party_name, currency_code, exchange_rate, grand_total, paid_amount, status, is_deleted, due_date) VALUES (104, '{CompanyCode}', 'PUR', 'PUR-M-AGED', '2026-01-01', 9, 'StepA2 Supplier', 'SAR', 1, 500, 0, 'Posted', 0, '2026-01-31');");
        Exec($"INSERT INTO purchase_documents (doc_id, company_id, doc_kind, doc_no, doc_date, party_id, party_name, currency_code, exchange_rate, grand_total, paid_amount, status, is_deleted, due_date) VALUES (105, '{CompanyCode}', 'PUR', 'PUR-M-CURRENT', '2026-03-01', 9, 'StepA2 Supplier', 'SAR', 1, 200, 0, 'Posted', 0, '2026-04-15');");
        Exec($"INSERT INTO purchase_documents (doc_id, company_id, doc_kind, doc_no, doc_date, party_id, party_name, currency_code, exchange_rate, grand_total, paid_amount, status, is_deleted, due_date) VALUES (106, '{CompanyCode}', 'PUR', 'PUR-M-PAID', '2026-01-01', 9, 'StepA2 Supplier', 'SAR', 1, 300, 300, 'Posted', 0, '2026-01-31');");
        Exec($"INSERT INTO purchase_documents (doc_id, company_id, doc_kind, doc_no, doc_date, party_id, party_name, currency_code, exchange_rate, grand_total, paid_amount, status, is_deleted, due_date) VALUES (107, '{CompanyCode}', 'PUR', 'PUR-M-LEGACY', '2025-10-01', 9, 'StepA2 Supplier', 'SAR', 1, 50, 0, 'Posted', 0, NULL);");

        return new TestDb { Root = root, DbPath = dbPath, CompanyId = companyId };
    }

    /// <summary>GL/stock/fiscal seeds the order-chain engines require
    /// (mirrors OrderChainStep9Tests.CreateAsync's seed set).</summary>
    private static void SeedChainPrerequisites(string connectionString, Guid companyId)
    {
        var guid = companyId.ToString("D");
        void Exec(string sql)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        Exec($"INSERT INTO fiscal_years (company_id, fiscal_year, database_name, start_date, end_date, is_current, is_closed) VALUES ('{guid}', 2026, '', '2026-01-01', '2026-12-31', 1, 0);");
        long yearId;
        using (var connection = new SqliteConnection(connectionString))
        {
            connection.Open();
            using var q = connection.CreateCommand();
            q.CommandText = "SELECT last_insert_rowid();";
            yearId = (long)q.ExecuteScalar()!;
        }
        for (var m = 1; m <= 12; m++)
        {
            Exec($"INSERT INTO fiscal_periods (fiscal_period_id, company_id, fiscal_year_id, period_number, period_name, start_date, end_date, status) VALUES ('{Guid.NewGuid():D}', '{guid}', {yearId}, {m}, '2026-{m:D2}', '2026-{m:D2}-01', '{new DateTime(2026, m, 1).AddMonths(1).AddDays(-1):yyyy-MM-dd}', 'OPEN');");
        }
        foreach (var (code, name, type) in new[]
        {
            ("1201", "Trade Accounts Receivable", "ASSET"), ("1203", "Merchandise Inventory", "ASSET"),
            ("2201", "Trade Accounts Payable", "LIABILITY"), ("2203", "Accrued Liabilities / GRNI", "LIABILITY"),
            ("301", "Goods Sales Revenue", "REVENUE"), ("401", "Cost of Goods Sold", "EXPENSE"),
            ("402", "Merchandise Purchases", "EXPENSE"), ("403", "Purchase Returns", "EXPENSE"),
            ("131", "Cash on Hand", "ASSET")
        })
        {
            Exec($"INSERT INTO chart_of_accounts (account_id, company_id, account_code, account_name, account_type, is_postable, is_active, is_deleted) VALUES ('{Guid.NewGuid():D}', '{guid}', '{code}', '{name}', '{type}', 1, 1, 0);");
        }
        Exec($"INSERT INTO warehouses (warehouse_id, company_id, warehouse_code, warehouse_name_ar, warehouse_name_en, is_active, is_deleted) VALUES (1, '{CompanyCode}', 'WH-01', 'Main', 'Main', 1, 0);");
        Exec($"INSERT INTO materials (material_id, company_id, code, name_ar, name_en, is_stocked, is_service, cost_method, allow_negative, is_deleted) VALUES (42, '{CompanyCode}', 'MAT-42', 'Item 42', 'Item 42', 1, 0, 'WeightedAvg', 0, 0);");
        Exec($"INSERT INTO material_stock_balances (company_id, warehouse_id, material_id, available_qty, reserved_qty, average_cost, last_cost, stock_value, is_deleted) VALUES ('{CompanyCode}', 1, 42, 100, 0, 10, 10, 1000, 0);");
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

    private static decimal Dec(object? v) => v is null ? 0m : Convert.ToDecimal(v, CultureInfo.InvariantCulture);

    private static string? ScalarText(string connectionString, string sql)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }
}
