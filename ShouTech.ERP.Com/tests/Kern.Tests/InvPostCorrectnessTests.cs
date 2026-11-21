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

    [Fact]
    public async Task DocSchema_ValidatesEachCompanyDatabaseIndependently()
    {
        using var validDb = await SeededAsync();
        DocSchema.Ensure();

        var root = Path.Combine(Path.GetTempPath(), "doc-schema-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var incompletePath = Path.Combine(root, "incomplete.db");
        try
        {
            using (var connection = new SqliteConnection($"Data Source={incompletePath};"))
                connection.Open();

            PointAppAt(incompletePath);
            var error = Assert.Throws<InvalidOperationException>(() => DocSchema.Ensure());
            Assert.Contains("schema is incomplete", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            PointAppAt(validDb.DbPath);
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SalesReturn_PostingRejectsCumulativeQuantityAboveOriginalInvoice()
    {
        using var db = await SeededAsync();
        var party = await SavePartyAsync(db, "C", "CUST-RETURN", 100000m);
        var original = BuildDoc(InvDocKind.SalesInvoice, party.LedgerId, party.Code,
            qty: 5m, price: 30m, taxRate: 0m, discount: 0m, extra: 0m, paid: 0m);
        InvRepo.SaveDraft(original);
        InvRepo.Post(original.DocId, original.Kind, Actor);
        Assert.Equal(995m, StockQty(db));
        Assert.Equal(original.DocId, InvRepo.LoadByNumber(original.DocNo, InvDocKind.SalesInvoice)!.DocId);

        InvDoc BuildReturn(decimal quantity) => new()
        {
            Kind = InvDocKind.SalesReturn,
            DocNo = InvRepo.NextDocNo(InvDocKind.SalesReturn),
            DocDate = new DateTime(2026, 11, 12),
            PartyId = party.LedgerId,
            PartyCode = party.Code,
            PartyName = party.NameEn,
            WarehouseId = 1,
            CurrencyCode = original.CurrencyCode,
            ExchangeRate = original.ExchangeRate,
            PaymentMethod = InvPaymentMethod.Credit,
            OriginalDocId = original.DocId,
            OriginalDocKind = InvDocKind.SalesInvoice,
            OriginalDocNo = original.DocNo,
            Notes = "Customer return",
            Lines =
            {
                new InvLine
                {
                    LineNo = 1,
                    MaterialId = original.Lines[0].MaterialId,
                    MaterialCode = original.Lines[0].MaterialCode,
                    Description = original.Lines[0].Description,
                    Unit = original.Lines[0].Unit,
                    Qty = quantity,
                    UnitPrice = original.Lines[0].UnitPrice,
                    DiscountPct = original.Lines[0].DiscountPct,
                    TaxCode = original.Lines[0].TaxCode,
                    TaxRate = original.Lines[0].TaxRate,
                    TaxCalculationMode = original.Lines[0].TaxCalculationMode,
                    WarehouseId = original.Lines[0].WarehouseId
                }
            }
        };

        var firstReturn = BuildReturn(3m);
        InvRepo.SaveDraft(firstReturn);
        InvRepo.Post(firstReturn.DocId, InvDocKind.SalesReturn, Actor);
        Assert.Equal(998m, StockQty(db));
        Assert.Equal(3m, InvRepo.GetAlreadyReturnedQuantities(
            original.DocId, InvDocKind.SalesInvoice)[1]);

        var secondReturn = BuildReturn(3m);
        InvRepo.SaveDraft(secondReturn);
        var error = Assert.Throws<InvalidOperationException>(
            () => InvRepo.Post(secondReturn.DocId, InvDocKind.SalesReturn, Actor));
        Assert.Contains("exceeds the remaining returnable quantity", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(998m, StockQty(db));
        Assert.Equal(1m, Scalar(db,
            "SELECT COUNT(*) FROM sales_documents WHERE doc_kind = 'SRT' AND status = 'Posted' AND original_doc_id = " + original.DocId.ToString(CultureInfo.InvariantCulture)));
    }

    [Fact]
    public async Task DeliveryLinkedInvoice_PostsThroughChainWithoutSecondStockIssue()
    {
        using var db = await SeededAsync();
        var party = await SavePartyAsync(db, "C", "CUST-DLV", 100000m);
        var orderRepository = new OrderDocumentRepository("SQLite", db.ConnectionString);
        var chainRepository = new CommercialDocumentRepository("SQLite", db.ConnectionString);
        var service = new OrderService(orderRepository, chainRepository);

        var orderId = await service.CreateOrderAsync(
            new CommercialDocumentDraft(
                db.CompanyId,
                CommercialDocKinds.SalesOrder,
                "SO-INV-DLV",
                new DateOnly(2026, 11, 10),
                party.LedgerId,
                party.NameEn,
                1,
                "SAR",
                1m,
                null,
                new[]
                {
                    new CommercialLineDraft(42, "MAT-42", "Item 42", "PCS", 2m, 25m, 0m, 0m, 1)
                },
                party.Code,
                new DateOnly(2026, 11, 30),
                null),
            Actor);
        await service.PostOrderAsync(CommercialDocSide.Sales, orderId, Actor);

        var (deliveryId, deliveryNo) = await service.CreateSalesDeliveryAsync(
            orderId, new DateOnly(2026, 11, 11), Actor,
            new Dictionary<long, decimal> { [42] = 2m }, 1);
        await service.PostSalesDeliveryAsync(deliveryId, Actor);
        var stockAfterDelivery = StockQty(db);
        Assert.Equal(998m, stockAfterDelivery);

        var (invoiceId, invoiceNo) = await service.CreateSalesInvoiceFromDeliveryAsync(
            deliveryId, new DateOnly(2026, 11, 12), Actor);
        // Exercise the same repository entry point used by InvoiceWin.
        InvRepo.Post(invoiceId, InvDocKind.SalesInvoice, Actor);

        Assert.Equal(998m, StockQty(db));
        Assert.Equal(-80m, Scalar(db,
            $"SELECT COALESCE(SUM(cost_amount), 0) FROM material_stock_movements WHERE company_id = '{CompanyCode}' AND document_no = '{deliveryNo}' AND material_id = 42;"));
        Assert.Equal(0m, Scalar(db,
            $"SELECT COALESCE(SUM(cost_amount), 0) FROM material_stock_movements WHERE company_id = '{CompanyCode}' AND document_no = '{invoiceNo}' AND material_id = 42;"));

        var gl = SingleEntry(db, "SAL", invoiceId).Lines.ToDictionary(
            line => line.Code, line => (line.Debit, line.Credit), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(50m, gl["1201"].Debit);
        Assert.Equal(50m, gl["301"].Credit);
        Assert.Equal(80m, gl["401"].Debit);
        Assert.Equal(80m, gl["1203"].Credit);
    }

    [Fact]
    public async Task LoadAndPost_RejectMismatchedStoredDocumentKind()
    {
        using var db = await SeededAsync();
        var beforeStock = StockQty(db);
        var doc = BuildDoc(InvDocKind.SalesInvoice, null, string.Empty,
            qty: 2m, price: 10m, taxRate: 0m, discount: 0m, extra: 0m, paid: 0m);
        InvRepo.SaveDraft(doc);

        Assert.Throws<InvalidOperationException>(
            () => InvRepo.Load(doc.DocId, InvDocKind.SalesReturn));
        Assert.Throws<InvalidOperationException>(
            () => InvRepo.Post(doc.DocId, InvDocKind.SalesReturn, Actor));

        var original = InvRepo.Load(doc.DocId, InvDocKind.SalesInvoice);
        Assert.NotNull(original);
        Assert.Equal(InvDocStatus.Draft, original!.Status);
        Assert.Equal(beforeStock, StockQty(db));
        Assert.Equal(0m, Scalar(db, "SELECT COUNT(*) FROM journal_entries;"));
        Assert.Equal(0m, Scalar(db, $"SELECT COUNT(*) FROM party_ledger WHERE doc_id = {doc.DocId};"));
    }

    [Fact]
    public async Task Load_RestoresLineWarehouseNameFromItsStoredColumn()
    {
        using var db = await SeededAsync();
        var doc = BuildDoc(InvDocKind.SalesInvoice, null, string.Empty,
            qty: 1m, price: 10m, taxRate: 0m, discount: 0m, extra: 0m, paid: 0m);
        doc.Lines[0].WarehouseName = "Line Warehouse";
        InvRepo.SaveDraft(doc);

        var loaded = InvRepo.Load(doc.DocId, InvDocKind.SalesInvoice);

        Assert.NotNull(loaded);
        Assert.Equal("Line Warehouse", loaded!.Lines.Single().WarehouseName);
    }

    [Fact]
    public async Task NextDocNo_UsesHighestExistingSequenceInsteadOfRowCount()
    {
        using var db = await SeededAsync();
        var year = DateTime.Today.Year.ToString(CultureInfo.InvariantCulture);
        using (var connection = new SqliteConnection(db.ConnectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO sales_documents
                    (company_id, doc_kind, doc_no, doc_date, status, is_deleted)
                VALUES
                    ($company, 'SAL', $number, $date, 'Draft', 0);
                """;
            command.Parameters.AddWithValue("$company", CompanyCode);
            command.Parameters.AddWithValue("$number", $"SAL-{year}-0007");
            command.Parameters.AddWithValue("$date", DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }

        Assert.Equal($"SAL-{year}-0008", InvRepo.NextDocNo(InvDocKind.SalesInvoice));
    }

    [Fact]
    public async Task NextDocNo_AllocatesUniqueNumbersForConcurrentCallers()
    {
        using var db = await SeededAsync();
        const int callers = 12;

        var allocated = await Task.WhenAll(
            Enumerable.Range(0, callers)
                .Select(_ => Task.Run(() => InvRepo.NextDocNo(InvDocKind.SalesInvoice))));

        var expected = Enumerable.Range(1, callers)
            .Select(value => $"SAL-{DateTime.Today.Year}-{value:D4}")
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(callers, allocated.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(expected, allocated.OrderBy(value => value, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task SaveDraft_AllocatesMissingNumberWithinItsDatabaseTransaction()
    {
        using var db = await SeededAsync();
        var doc = BuildDoc(InvDocKind.SalesInvoice, null, string.Empty,
            qty: 1m, price: 25m, taxRate: 0m, discount: 0m, extra: 0m, paid: 0m);
        doc.DocNo = string.Empty;

        InvRepo.SaveDraft(doc);

        Assert.Equal($"SAL-{DateTime.Today.Year}-0002", doc.DocNo);
        Assert.True(doc.DocId > 0);
    }

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
        Assert.Equal(900m, entry.Lines.Where(l => l.Code == "301").Sum(l => l.Credit));
        Assert.Equal(150m, entry.Lines.Where(l => l.Code == "2205").Sum(l => l.Credit));
        Assert.Equal(entry.Lines.Sum(l => l.Debit), entry.Lines.Sum(l => l.Credit));
    }

    [Fact]
    public async Task PostedInvoice_RejectsRetryWithoutDuplicatingAnyPostingEffects()
    {
        using var db = await SeededAsync();
        var party = await SavePartyAsync(db, "C", "CUST-RETRY", creditLimit: 0m);
        var stockBefore = StockQty(db);
        var doc = BuildDoc(InvDocKind.SalesInvoice, party.LedgerId, "CUST-RETRY",
            qty: 2m, price: 25m, taxRate: 0m, discount: 0m, extra: 0m, paid: 0m);
        InvRepo.SaveDraft(doc);

        InvRepo.Post(doc.DocId, doc.Kind, Actor);

        var postedStock = StockQty(db);
        var journalCount = Scalar(db,
            $"SELECT COUNT(*) FROM journal_entries WHERE source_doc_type = 'SAL' AND source_doc_id = '{doc.DocId}';");
        var ledgerCount = Scalar(db,
            $"SELECT COUNT(*) FROM party_ledger WHERE doc_id = {doc.DocId};");
        var movementCount = Scalar(db,
            $"SELECT COUNT(*) FROM material_stock_movements WHERE company_id = '{CompanyCode}' AND document_no = '{doc.DocNo}';");

        Assert.Throws<InvalidOperationException>(() => InvRepo.Post(doc.DocId, doc.Kind, Actor));

        Assert.Equal(InvDocStatus.Posted, InvRepo.Load(doc.DocId, doc.Kind)!.Status);
        Assert.Equal(postedStock, StockQty(db));
        Assert.Equal(stockBefore - 2m, postedStock);
        Assert.Equal(journalCount, Scalar(db,
            $"SELECT COUNT(*) FROM journal_entries WHERE source_doc_type = 'SAL' AND source_doc_id = '{doc.DocId}';"));
        Assert.Equal(ledgerCount, Scalar(db,
            $"SELECT COUNT(*) FROM party_ledger WHERE doc_id = {doc.DocId};"));
        Assert.Equal(movementCount, Scalar(db,
            $"SELECT COUNT(*) FROM material_stock_movements WHERE company_id = '{CompanyCode}' AND document_no = '{doc.DocNo}';"));
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
        Assert.Equal(900m, entry.Lines.Where(l => l.Code == "402").Sum(l => l.Debit));
        Assert.Equal(150m, entry.Lines.Where(l => l.Code == "1205").Sum(l => l.Debit));
        Assert.Equal(1050m, entry.Lines.Where(l => l.Code == "2201").Sum(l => l.Credit));
    }

    [Fact]
    public async Task PurchaseInvoiceReturn_AuditsReleasedSupplierPaymentAllocation()
    {
        using var db = await SeededAsync();
        var supplier = await SavePartyAsync(db, "S", "SUP-RET-1", creditLimit: 0m);
        var stockBefore = StockQty(db);

        var invoice = BuildDoc(InvDocKind.PurchaseInvoice, supplier.LedgerId, "SUP-RET-1",
            qty: 1m, price: 100m, taxRate: 0m, discount: 0m, extra: 0m, paid: 0m);
        InvRepo.SaveDraft(invoice);
        InvRepo.Post(invoice.DocId, invoice.Kind, Actor);

        long paymentLedgerId;
        using (var connection = new SqliteConnection(db.ConnectionString))
        {
            connection.Open();
            using var payment = connection.CreateCommand();
            payment.CommandText = """
                INSERT INTO party_ledger
                    (company_id, party_kind, party_id, doc_kind, doc_no, entry_date,
                     debit, credit, currency_code, note)
                VALUES
                    ($company, 'S', $party, 'PAY', 'PAY-RET-1', '2026-11-10',
                     100, 0, 'SAR', 'Supplier payment used for invoice settlement')
                RETURNING ledger_id;
                """;
            payment.Parameters.AddWithValue("$company", CompanyCode);
            payment.Parameters.AddWithValue("$party", supplier.LedgerId);
            paymentLedgerId = Convert.ToInt64(payment.ExecuteScalar(), CultureInfo.InvariantCulture);

            using var allocation = connection.CreateCommand();
            allocation.CommandText = """
                INSERT INTO payment_allocations
                    (allocation_id, company_id, document_company_id, party_kind, party_id,
                     payment_ledger_id, doc_table, doc_id, doc_no, amount, allocated_by, note)
                VALUES
                    ($allocation, $company, $documentCompany, 'S', $party,
                     $payment, 'PURCHASE', $doc, $docNo, 100, $actor, 'Full supplier invoice settlement');
                """;
            allocation.Parameters.AddWithValue("$allocation", Guid.NewGuid().ToString("D"));
            allocation.Parameters.AddWithValue("$company", CompanyCode);
            allocation.Parameters.AddWithValue("$documentCompany", CompanyCode);
            allocation.Parameters.AddWithValue("$party", supplier.LedgerId);
            allocation.Parameters.AddWithValue("$payment", paymentLedgerId);
            allocation.Parameters.AddWithValue("$doc", invoice.DocId);
            allocation.Parameters.AddWithValue("$docNo", invoice.DocNo);
            allocation.Parameters.AddWithValue("$actor", Actor);
            allocation.ExecuteNonQuery();

            using var paid = connection.CreateCommand();
            paid.CommandText = "UPDATE purchase_documents SET paid_amount=100 WHERE doc_id=$doc AND company_id=$company;";
            paid.Parameters.AddWithValue("$doc", invoice.DocId);
            paid.Parameters.AddWithValue("$company", CompanyCode);
            Assert.Equal(1, paid.ExecuteNonQuery());
        }

        var reversalId = InvRepo.ReversePostedInvoice(
            invoice.DocId, InvDocKind.PurchaseInvoice, "Supplier returned the full delivery", Actor);

        Assert.Equal(stockBefore, StockQty(db));
        Assert.Equal(0m, Scalar(db,
            $"SELECT paid_amount FROM purchase_documents WHERE doc_id={invoice.DocId};"));
        Assert.Equal(0m, Scalar(db,
            $"SELECT COUNT(*) FROM payment_allocations WHERE company_id='{CompanyCode}' AND doc_table='PURCHASE' AND doc_id={invoice.DocId};"));
        Assert.Equal(1m, Scalar(db,
            $"SELECT COUNT(*) FROM payment_allocation_reversal_audit WHERE company_id='{CompanyCode}' AND doc_table='PURCHASE' AND doc_id={invoice.DocId};"));
        Assert.Equal(100m, Scalar(db,
            $"SELECT amount FROM payment_allocation_reversal_audit WHERE company_id='{CompanyCode}' AND doc_table='PURCHASE' AND doc_id={invoice.DocId};"));
        Assert.Equal(1m, Scalar(db,
            $"SELECT COUNT(*) FROM payment_allocation_reversal_audit WHERE company_id='{CompanyCode}' AND doc_table='PURCHASE' AND doc_id={invoice.DocId} AND released_by='{Actor}';"));

        var returnDoc = InvRepo.Load(reversalId, InvDocKind.PurchaseReturn);
        Assert.NotNull(returnDoc);
        var persistedInvoice = InvRepo.Load(invoice.DocId, InvDocKind.PurchaseInvoice);
        Assert.NotNull(persistedInvoice);
        Assert.Equal(persistedInvoice!.StoredGrandTotal, returnDoc!.StoredGrandTotal);

        var journalId = ScalarText(db,
            $"SELECT journal_entry_id FROM journal_entries WHERE source_doc_type='PRT' AND source_doc_id='{reversalId}' AND status=1;");
        Assert.Equal(journalId, ScalarText(db,
            $"SELECT reversal_journal_id FROM payment_allocation_reversal_audit WHERE company_id='{CompanyCode}' AND doc_id={invoice.DocId};"));

        var reconciliation = new ReconciliationRepository("SQLite", db.ConnectionString);
        var availablePayments = await reconciliation.GetPaymentsAsync(CompanyCode, "S");
        var reopenedPayment = Assert.Single(availablePayments, row => row.LedgerId == paymentLedgerId);
        Assert.Equal(100m, reopenedPayment.Available);
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
    public async Task StockTransfer_RetryDoesNotDuplicateStockMovements()
    {
        using var db = await SeededAsync();
        using (var connection = new SqliteConnection(db.ConnectionString))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"INSERT INTO warehouses (warehouse_id, company_id, warehouse_code, warehouse_name_ar, warehouse_name_en, is_deleted) VALUES (2, '{CompanyCode}', 2, 'Second', 'Second', 0);";
            cmd.ExecuteNonQuery();
        }

        var doc = BuildDoc(InvDocKind.StockTransfer, null, "", qty: 3m, price: 10m,
            taxRate: 0m, discount: 0m, extra: 0m, paid: 0m);
        doc.TargetWarehouseId = 2;
        doc.TargetWarehouseName = "Second";
        InvRepo.SaveDraft(doc);
        InvRepo.Post(doc.DocId, doc.Kind, Actor);

        var sourceAfterPost = StockQty(db);
        var destinationAfterPost = Scalar(db, "SELECT COALESCE(available_qty, 0) FROM material_stock_balances WHERE company_id = '" + CompanyCode + "' AND warehouse_id = 2 AND material_id = 42;");
        var movementCountAfterPost = Scalar(db, $"SELECT COUNT(*) FROM material_stock_movements WHERE company_id = '{CompanyCode}' AND movement_type IN ('TRANSFER_OUT', 'TRANSFER_IN');");

        Assert.Throws<InvalidOperationException>(() => InvRepo.Post(doc.DocId, doc.Kind, Actor));

        Assert.Equal(sourceAfterPost, StockQty(db));
        Assert.Equal(destinationAfterPost, Scalar(db, "SELECT COALESCE(available_qty, 0) FROM material_stock_balances WHERE company_id = '" + CompanyCode + "' AND warehouse_id = 2 AND material_id = 42;"));
        Assert.Equal(movementCountAfterPost, Scalar(db, $"SELECT COUNT(*) FROM material_stock_movements WHERE company_id = '{CompanyCode}' AND movement_type IN ('TRANSFER_OUT', 'TRANSFER_IN');"));
        Assert.Equal(2m, movementCountAfterPost);
        Assert.Equal(InvDocStatus.Posted, InvRepo.Load(doc.DocId, InvDocKind.StockTransfer)!.Status);
    }

    [Fact]
    public async Task StockTransfer_FailedEngineRevertsPostingClaimWithoutStockEffects()
    {
        using var db = await SeededAsync();
        using (var connection = new SqliteConnection(db.ConnectionString))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"INSERT INTO warehouses (warehouse_id, company_id, warehouse_code, warehouse_name_ar, warehouse_name_en, is_deleted) VALUES (2, '{CompanyCode}', 2, 'Second', 'Second', 0);";
            cmd.ExecuteNonQuery();
        }

        var doc = BuildDoc(InvDocKind.StockTransfer, null, "", qty: 1001m, price: 10m,
            taxRate: 0m, discount: 0m, extra: 0m, paid: 0m);
        doc.TargetWarehouseId = 2;
        doc.TargetWarehouseName = "Second";
        InvRepo.SaveDraft(doc);

        Assert.Throws<StkPost.StockTransferException>(() => InvRepo.Post(doc.DocId, doc.Kind, Actor));

        Assert.Equal(InvDocStatus.Draft, InvRepo.Load(doc.DocId, doc.Kind)!.Status);
        Assert.Equal(1000m, StockQty(db));
        Assert.Equal(0m, Scalar(db, $"SELECT COUNT(*) FROM material_stock_movements WHERE company_id = '{CompanyCode}' AND movement_type IN ('TRANSFER_OUT', 'TRANSFER_IN');"));
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
    public async Task InventoryCount_RetryDoesNotDuplicateStockMovements()
    {
        using var db = await SeededAsync();
        var doc = BuildDoc(InvDocKind.InventoryCount, null, "", qty: 1003m, price: 10m,
            taxRate: 0m, discount: 0m, extra: 0m, paid: 0m);
        InvRepo.SaveDraft(doc);
        InvRepo.Post(doc.DocId, doc.Kind, Actor);

        var stockAfterPost = StockQty(db);
        var movementCountAfterPost = Scalar(db, $"SELECT COUNT(*) FROM material_stock_movements WHERE company_id = '{CompanyCode}' AND movement_type = 'COUNT_CLOSE';");

        Assert.Throws<InvalidOperationException>(() => InvRepo.Post(doc.DocId, doc.Kind, Actor));

        Assert.Equal(stockAfterPost, StockQty(db));
        Assert.Equal(movementCountAfterPost, Scalar(db, $"SELECT COUNT(*) FROM material_stock_movements WHERE company_id = '{CompanyCode}' AND movement_type = 'COUNT_CLOSE';"));
        Assert.Equal(1m, movementCountAfterPost);
        Assert.Equal(InvDocStatus.Posted, InvRepo.Load(doc.DocId, InvDocKind.InventoryCount)!.Status);
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
            Lines = { new InvLine { LineNo = 1, MaterialId = 42, MaterialCode = "MAT-42", Qty = qty, UnitPrice = price, TaxCode = taxRate > 0m ? "IP5-VAT15" : string.Empty, TaxRate = taxRate, WarehouseId = 1 } }
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

    private static string ScalarText(TestDb db, string sql)
    {
        using var connection = new SqliteConnection(db.ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
    }

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
        AppState.SetCompany(CompanyCode, "InvPost Test Co", "InvPost Test Co");

        using (var connection = new SqliteConnection(connectionString))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "INSERT INTO companies (company_id, company_code, company_name_ar, company_name_en) VALUES ($id, $code, 'InvPost Test Co', 'InvPost Test Co');";
            cmd.Parameters.AddWithValue("$id", companyId.ToString("D"));
            cmd.Parameters.AddWithValue("$code", CompanyCode);
            cmd.ExecuteNonQuery();

            using var taxSetup = connection.CreateCommand();
            taxSetup.CommandText = """
                INSERT INTO company_tax_settings(company_code, country_code, tax_enabled, tax_scheme)
                VALUES ('IP5', 'SA', 1, 'VAT');
                INSERT INTO company_tax_rules
                    (company_code, country_code, tax_code, tax_name, tax_type, tax_treatment,
                     rate, calculation_mode, is_recoverable, output_account_code, input_account_code,
                     effective_from, is_default, is_active)
                VALUES ('IP5', 'SA', 'IP5-VAT15', 'Standard VAT', 'VAT', 'Standard',
                        15, 'Exclusive', 1, '2205', '1205', '2026-01-01', 1, 1);
                """;
            taxSetup.ExecuteNonQuery();
        }

        var structure = new AccountingStructureRepository("SQLite", connectionString);
        await structure.CreateFiscalYearAsync(companyId, 2026, Actor);

        foreach (var (code, name) in new[]
        {
            ("131", "Cash"), ("132", "Bank"), ("1201", "Receivable"), ("1203", "Inventory"),
            ("1205", "Input VAT"), ("2201", "Payable"), ("2205", "Output VAT"), ("301", "Sales"), ("302", "Sales Returns"), ("401", "COGS"),
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
