using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ShouTech.Application.Services;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;
using ShouTech.Infrastructure;
using Xunit;

namespace Kern.Tests
{
    /// <summary>
    /// Fix Phase Step 9 — order-chain wiring + DocSearch open:
    ///   (a) DocSearch's load path returns THE stored document (id +
    ///       totals) for order and invoice kinds, and search matches
    ///       both company keys the document tables carry (Guid for the
    ///       order/chain family, code for the invoice family);
    ///   (b) order → invoice conversion end-to-end: the invoice carries
    ///       the order's lines/totals, its GL entry is balanced and
    ///       carries COGS at the valuation engine's cost, stock is
    ///       deducted (sales) / received (purchase) at engine cost, and
    ///       billed progress updates truthfully — including a partial
    ///       conversion that bills only the remaining quantities;
    ///   (c) quotation → sales order through OrderService (the service
    ///       DocSearch's Convert button now calls).
    /// TRN/CNT engine posting is proven in InvPostCorrectnessTests.
    /// Execution requires Windows (net8.0-windows testhost); the same
    /// scenarios are verified headlessly by
    /// ~/workspace/research/fix-step9/step9_harness.py
    /// (see docs/FIX_PHASE_STEP9.md).
    /// </summary>
    public class OrderChainStep9Tests
    {
        private const string Actor = "step9-test";
        private const string CompanyCode = "ORDER-GL";

        // ------------------------------------------------------------ (b) sales

        [Fact]
        public async Task SalesOrder_TwoLines_Convert_PostsStockAtCost_AndBalancedGlWithCogs()
        {
            var (service, orders, companyId, connectionString, cleanup) = await CreateAsync();
            try
            {
                var orderId = await service.CreateOrderAsync(
                    Order(companyId, CommercialDocKinds.SalesOrder, "SO-9101",
                        new CommercialLineDraft(42, "MAT-42", "Item 42", "PCS", 4m, 25m),
                        new CommercialLineDraft(43, "MAT-43", "Item 43", "PCS", 2m, 30m)), Actor);
                await service.PostOrderAsync(CommercialDocSide.Sales, orderId, Actor);

                var (invoiceId, invoiceDocNo) = await service.ConvertToInvoiceAsync(
                    CommercialDocSide.Sales, orderId, new DateOnly(2026, 11, 2), Actor);
                Assert.StartsWith("SAL-", invoiceDocNo);

                var invoice = await orders.FindByIdAsync(CommercialDocSide.Sales, invoiceId);
                Assert.NotNull(invoice);
                Assert.Equal(CommercialDocKinds.SalesInvoice, invoice!.DocKind);
                Assert.Equal(CommercialDocumentStatus.Posted, invoice.Status);
                Assert.Equal(2, invoice.Lines.Count);
                Assert.Equal(160m, invoice.GrandTotal); // 4x25 + 2x30

                // Stock deducted at ENGINE cost (10 and 20), never at
                // the selling prices (25 and 30).
                Assert.Equal(96m, BalanceQty(connectionString, 42));
                Assert.Equal(960m, BalanceValue(connectionString, 42));
                Assert.Equal(48m, BalanceQty(connectionString, 43));
                Assert.Equal(960m, BalanceValue(connectionString, 43));
                Assert.Equal(-40m, MovementCost(connectionString, invoiceDocNo, 42));
                Assert.Equal(-40m, MovementCost(connectionString, invoiceDocNo, 43));

                // GL: balanced; Dr Receivable 160 / Cr Revenue 160 and
                // Dr COGS 80 / Cr Inventory 80 (4x10 + 2x20).
                var gl = GlByAccount(connectionString, "SAL", invoiceId);
                Assert.Equal(160m, gl["1201"].Debit);
                Assert.Equal(160m, gl["301"].Credit);
                Assert.Equal(80m, gl["401"].Debit);
                Assert.Equal(80m, gl["1203"].Credit);
                decimal totalDebit = 0m, totalCredit = 0m;
                foreach (var v in gl.Values) { totalDebit += v.Debit; totalCredit += v.Credit; }
                Assert.Equal(totalDebit, totalCredit);

                // Billed progress is truthful; a second conversion is
                // refused because nothing remains unbilled.
                var progress = await service.GetProgressAsync(CommercialDocSide.Sales, orderId);
                Assert.Equal(100m, progress.PerBilled);
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    service.ConvertToInvoiceAsync(CommercialDocSide.Sales, orderId, new DateOnly(2026, 11, 3), Actor));
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public async Task PurchaseOrder_ReceiptThenInvoice_AccruesAndClearsGrniWithoutDoubleReceiving()
        {
            var (service, orders, companyId, connectionString, cleanup) = await CreateAsync();
            try
            {
                var orderId = await service.CreateOrderAsync(
                    Order(companyId, CommercialDocKinds.PurchaseOrder, "PO-9101",
                        new CommercialLineDraft(42, "MAT-42", "Item 42", "PCS", 5m, 12m),
                        partyId: 9, partyCode: "SUP-1"), Actor);
                await service.PostOrderAsync(CommercialDocSide.Purchase, orderId, Actor);

                // First receipt is partial. It increases inventory and credits GRNI,
                // but it must not create a supplier payable before the bill arrives.
                var (firstReceiptId, firstReceiptNo) = await service.CreatePurchaseReceiptAsync(
                    companyId, orderId, new DateOnly(2026, 11, 2),
                    new[] { new CommercialLineDraft(42, "MAT-42", "Item 42", "PCS", 3m, 12m) }, Actor);
                var firstReceipt = await orders.FindByIdAsync(CommercialDocSide.Purchase, firstReceiptId);
                Assert.Equal(CommercialDocKinds.PurchaseReceipt, firstReceipt!.DocKind);
                Assert.Equal(CommercialDocumentStatus.Posted, firstReceipt.Status);
                Assert.Equal(36m, firstReceipt.GrandTotal);
                Assert.Equal(103m, BalanceQty(connectionString, 42));
                Assert.Equal(1036m, BalanceValue(connectionString, 42));
                Assert.Equal(36m, MovementCost(connectionString, firstReceiptNo, 42));
                Assert.Equal(12m, Scalar(connectionString, "SELECT last_purchase_cost FROM materials WHERE material_id = 42;"));

                var receiptGl = GlByAccount(connectionString, "RCV", firstReceiptId);
                Assert.Equal(36m, receiptGl["1203"].Debit);
                Assert.Equal(36m, receiptGl["2203"].Credit);
                Assert.Equal(0m, Scalar(connectionString,
                    $"SELECT COUNT(*) FROM party_ledger WHERE doc_kind = 'RCV' AND doc_id = {firstReceiptId};"));

                var firstProgress = await service.GetProgressAsync(CommercialDocSide.Purchase, orderId);
                Assert.Equal(60m, firstProgress.PerFulfilled);
                Assert.Equal(0m, firstProgress.PerBilled);

                // The first supplier bill may consume only part of the first receipt.
                var (firstInvoiceId, firstInvoiceNo) = await service.CreatePurchaseInvoiceAsync(
                    companyId, orderId, new DateOnly(2026, 11, 2),
                    new[] { new CommercialLineDraft(42, "MAT-42", "Item 42", "PCS", 2m, 13m) }, Actor);
                var firstInvoice = await orders.FindByIdAsync(CommercialDocSide.Purchase, firstInvoiceId);
                Assert.Equal(2m, firstInvoice!.Lines[0].Qty);
                Assert.Equal(26m, firstInvoice.GrandTotal);
                Assert.Equal(103m, BalanceQty(connectionString, 42));
                Assert.Equal(1036m, BalanceValue(connectionString, 42));
                Assert.Equal(0m, MovementCost(connectionString, firstInvoiceNo, 42));
                var firstInvoiceGl = GlByAccount(connectionString, "PUR", firstInvoiceId);
                Assert.Equal(24m, firstInvoiceGl["2203"].Debit);
                Assert.Equal(2m, firstInvoiceGl["402"].Debit);
                Assert.Equal(26m, firstInvoiceGl["2201"].Credit);

                var partiallyBilled = await service.GetProgressAsync(CommercialDocSide.Purchase, orderId);
                Assert.Equal(60m, partiallyBilled.PerFulfilled);
                Assert.Equal(40m, partiallyBilled.PerBilled);

                // Receive the balance in a second receipt, then bill the remaining
                // one unit from receipt one plus both units from receipt two.
                var (secondReceiptId, secondReceiptNo) = await service.CreatePurchaseReceiptAsync(
                    companyId, orderId, new DateOnly(2026, 11, 3),
                    new[] { new CommercialLineDraft(42, "MAT-42", "Item 42", "PCS", 2m, 12m) }, Actor);
                Assert.Equal(105m, BalanceQty(connectionString, 42));
                Assert.Equal(1060m, BalanceValue(connectionString, 42));
                Assert.Equal(24m, MovementCost(connectionString, secondReceiptNo, 42));

                var (invoiceId, invoiceDocNo) = await service.CreatePurchaseInvoiceAsync(
                    companyId, orderId, new DateOnly(2026, 11, 4),
                    new[] { new CommercialLineDraft(42, "MAT-42", "Item 42", "PCS", 3m, 13m) }, Actor);
                var invoice = await orders.FindByIdAsync(CommercialDocSide.Purchase, invoiceId);
                Assert.Equal(CommercialDocKinds.PurchaseInvoice, invoice!.DocKind);
                Assert.Equal(3m, invoice.Lines[0].Qty);
                Assert.Equal(39m, invoice.GrandTotal);
                Assert.Equal(105m, BalanceQty(connectionString, 42));
                Assert.Equal(1060m, BalanceValue(connectionString, 42));
                Assert.Equal(0m, MovementCost(connectionString, invoiceDocNo, 42));

                var invoiceGl = GlByAccount(connectionString, "PUR", invoiceId);
                Assert.Equal(36m, invoiceGl["2203"].Debit);
                Assert.Equal(3m, invoiceGl["402"].Debit);
                Assert.Equal(39m, invoiceGl["2201"].Credit);
                Assert.Equal(2m, Scalar(connectionString,
                    $"SELECT COUNT(*) FROM purchase_receipt_invoice_matches WHERE invoice_doc_id = {invoiceId};"));
                Assert.Equal(3m, Scalar(connectionString,
                    $"SELECT COUNT(*) FROM purchase_receipt_invoice_matches WHERE purchase_order_id = {orderId};"));
                Assert.Equal(5m, Scalar(connectionString,
                    $"SELECT SUM(matched_qty) FROM purchase_receipt_invoice_matches WHERE purchase_order_id = {orderId};"));
                Assert.Equal(5m, Scalar(connectionString,
                    $"SELECT SUM(variance_base_amount) FROM purchase_receipt_invoice_matches WHERE purchase_order_id = {orderId};"));
                Assert.Equal(1m, Scalar(connectionString,
                    $"SELECT COUNT(*) FROM party_ledger WHERE doc_kind = 'PUR' AND doc_id = {firstInvoiceId} AND credit = 26;"));
                Assert.Equal(1m, Scalar(connectionString,
                    $"SELECT COUNT(*) FROM party_ledger WHERE doc_kind = 'PUR' AND doc_id = {invoiceId} AND credit = 39;"));

                var progress = await service.GetProgressAsync(CommercialDocSide.Purchase, orderId);
                Assert.Equal(100m, progress.PerFulfilled);
                Assert.Equal(100m, progress.PerBilled);

                // A third receipt is an over-receipt and must fail without changing stock.
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    service.CreatePurchaseReceiptAsync(companyId, orderId, new DateOnly(2026, 11, 5),
                        new[] { new CommercialLineDraft(42, "MAT-42", "Item 42", "PCS", 1m, 12m) }, Actor));
                Assert.Equal(105m, BalanceQty(connectionString, 42));
                Assert.NotEqual(firstReceiptId, secondReceiptId);
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public async Task SupplierPaymentVoucher_ReconcilesAgainstGuidScopedPurchaseInvoice()
        {
            var (service, orders, companyId, connectionString, cleanup) = await CreateAsync();
            try
            {
                var orderId = await service.CreateOrderAsync(
                    Order(companyId, CommercialDocKinds.PurchaseOrder, "PO-9103",
                        new CommercialLineDraft(42, "MAT-42", "Item 42", "PCS", 2m, 30m),
                        partyId: 9, partyCode: "SUP-1"), Actor);
                await service.PostOrderAsync(CommercialDocSide.Purchase, orderId, Actor);
                await service.CreatePurchaseReceiptAsync(companyId, orderId, new DateOnly(2026, 11, 6),
                    new[] { new CommercialLineDraft(42, "MAT-42", "Item 42", "PCS", 2m, 30m) }, Actor);
                var (invoiceId, invoiceNo) = await service.CreatePurchaseInvoiceAsync(
                    companyId, orderId, new DateOnly(2026, 11, 7),
                    new[] { new CommercialLineDraft(42, "MAT-42", "Item 42", "PCS", 2m, 30m) }, Actor);
                Assert.StartsWith("PUR-", invoiceNo);

                var journalRepository = new JournalRepository("SQLite", connectionString);
                var journal = new JournalService(journalRepository);
                var payableAccount = await journalRepository.ResolveAccountIdAsync(companyId, "2201");
                var cashAccount = await journalRepository.ResolveAccountIdAsync(companyId, "131");
                var paymentId = await journal.CreateDraftAsync(
                    companyId, new DateOnly(2026, 11, 8), "Supplier payment PAY-9103", Actor,
                    new[]
                    {
                        new JournalLineRequest(payableAccount, 60m, 0m, "Settle supplier invoice"),
                        new JournalLineRequest(cashAccount, 0m, 60m, "Cash payment")
                    },
                    currencyCode: "SAR", exchangeRate: 1m, documentNumber: "PAY-9103");

                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText = """
                        INSERT INTO voucher_party_links
                            (journal_entry_id, company_id, party_kind, party_id, party_code, doc_kind, doc_no, amount, currency_code, created_by)
                        VALUES
                            ($journal, $company, 'S', 9, 'SUP-1', 'PAY', 'PAY-9103', 60, 'SAR', $actor);
                        """;
                    command.Parameters.AddWithValue("$journal", paymentId.ToString("D"));
                    command.Parameters.AddWithValue("$company", CompanyCode);
                    command.Parameters.AddWithValue("$actor", Actor);
                    command.ExecuteNonQuery();
                }
                await journal.PostAsync(paymentId, Actor);

                var reconciliation = new ReconciliationRepository("SQLite", connectionString);
                var openBefore = await reconciliation.GetOpenDocumentsAsync(CompanyCode, "S");
                var invoiceRow = Assert.Single(openBefore, row => row.DocId == invoiceId);
                Assert.Equal(60m, invoiceRow.OpenAmount);

                var payment = Assert.Single(await reconciliation.GetPaymentsAsync(CompanyCode, "S"),
                    row => row.DocNo == "PAY-9103");
                Assert.Equal(60m, payment.Available);
                await reconciliation.AllocateAsync(
                    CompanyCode, "S", payment.LedgerId, "PURCHASE", invoiceId, 60m, Actor,
                    "Full settlement of supplier invoice");

                Assert.Equal(60m, Scalar(connectionString,
                    $"SELECT paid_amount FROM purchase_documents WHERE doc_id = {invoiceId};"));
                Assert.Equal(1m, Scalar(connectionString,
                    $"SELECT COUNT(*) FROM payment_allocations WHERE company_id = '{CompanyCode}' AND document_company_id = '{companyId:D}' AND doc_table = 'PURCHASE' AND doc_id = {invoiceId} AND amount = 60;"));
                Assert.Empty(await reconciliation.GetOpenDocumentsAsync(CompanyCode, "S"));
                Assert.Empty(await reconciliation.GetPaymentsAsync(CompanyCode, "S"));
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public async Task NonStockServiceReceipt_AccruesToExpenseWithoutCreatingInventory()
        {
            var (service, orders, companyId, connectionString, cleanup) = await CreateAsync();
            try
            {
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText = "UPDATE materials SET is_stocked = 0, is_service = 1 WHERE material_id = 43;";
                    command.ExecuteNonQuery();
                }

                var orderId = await service.CreateOrderAsync(
                    Order(companyId, CommercialDocKinds.PurchaseOrder, "PO-9104",
                        new CommercialLineDraft(43, "MAT-43", "Consulting service", "HOUR", 2m, 50m),
                        partyId: 9, partyCode: "SUP-1"), Actor);
                await service.PostOrderAsync(CommercialDocSide.Purchase, orderId, Actor);

                var (receiptId, receiptNo) = await service.CreatePurchaseReceiptAsync(
                    companyId, orderId, new DateOnly(2026, 11, 9),
                    new[] { new CommercialLineDraft(43, "MAT-43", "Consulting service", "HOUR", 2m, 50m) }, Actor);
                Assert.Equal(50m, BalanceQty(connectionString, 43));
                Assert.Equal(0m, MovementCost(connectionString, receiptNo, 43));
                var receiptGl = GlByAccount(connectionString, "RCV", receiptId);
                Assert.Equal(100m, receiptGl["402"].Debit);
                Assert.Equal(100m, receiptGl["2203"].Credit);
                Assert.False(receiptGl.ContainsKey("1203"));

                var (invoiceId, invoiceNo) = await service.CreatePurchaseInvoiceAsync(
                    companyId, orderId, new DateOnly(2026, 11, 10),
                    new[] { new CommercialLineDraft(43, "MAT-43", "Consulting service", "HOUR", 2m, 45m) }, Actor);
                Assert.Equal(0m, MovementCost(connectionString, invoiceNo, 43));
                var invoiceGl = GlByAccount(connectionString, "PUR", invoiceId);
                Assert.Equal(100m, invoiceGl["2203"].Debit);
                Assert.Equal(10m, invoiceGl["402"].Credit);
                Assert.Equal(90m, invoiceGl["2201"].Credit);
                Assert.Equal(50m, BalanceQty(connectionString, 43));
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public async Task DuplicateMaterialLines_DistributeReceiptAndInvoiceProgressBySourceLine()
        {
            var (service, orders, companyId, connectionString, cleanup) = await CreateAsync();
            try
            {
                var orderId = await service.CreateOrderAsync(
                    Order(companyId, CommercialDocKinds.PurchaseOrder, "PO-9105",
                        new CommercialLineDraft(42, "MAT-42", "Item 42 - first price", "PCS", 2m, 10m),
                        new CommercialLineDraft(42, "MAT-42", "Item 42 - second price", "PCS", 3m, 12m),
                        partyId: 9, partyCode: "SUP-1"), Actor);
                await service.PostOrderAsync(CommercialDocSide.Purchase, orderId, Actor);

                var (receiptId, receiptNo) = await service.CreatePurchaseReceiptAsync(
                    companyId, orderId, new DateOnly(2026, 11, 11),
                    new[] { new CommercialLineDraft(42, "MAT-42", "Item 42", "PCS", 3m, 11m) }, Actor);
                var receipt = await orders.FindByIdAsync(CommercialDocSide.Purchase, receiptId);
                Assert.Equal(2, receipt!.Lines.Count);
                Assert.Equal(2m, receipt.Lines[0].Qty);
                Assert.Equal(1m, receipt.Lines[1].Qty);
                Assert.Equal(10m, receipt.Lines[0].UnitPrice);
                Assert.Equal(12m, receipt.Lines[1].UnitPrice);
                Assert.Equal(103m, BalanceQty(connectionString, 42));
                Assert.Equal(1032m, BalanceValue(connectionString, 42));
                Assert.Equal(32m, MovementCost(connectionString, receiptNo, 42));

                var receivedProgress = await service.GetProgressAsync(CommercialDocSide.Purchase, orderId);
                Assert.Equal(2m, receivedProgress.Lines[0].FulfilledQty);
                Assert.Equal(1m, receivedProgress.Lines[1].FulfilledQty);
                Assert.Equal(60m, receivedProgress.PerFulfilled);

                var (invoiceId, invoiceNo) = await service.CreatePurchaseInvoiceAsync(
                    companyId, orderId, new DateOnly(2026, 11, 12),
                    new[] { new CommercialLineDraft(42, "MAT-42", "Item 42", "PCS", 3m, 11m) }, Actor);
                Assert.Equal(0m, MovementCost(connectionString, invoiceNo, 42));
                var invoiceGl = GlByAccount(connectionString, "PUR", invoiceId);
                Assert.Equal(32m, invoiceGl["2203"].Debit);
                Assert.Equal(1m, invoiceGl["402"].Debit);
                Assert.Equal(33m, invoiceGl["2201"].Credit);

                var billedProgress = await service.GetProgressAsync(CommercialDocSide.Purchase, orderId);
                Assert.Equal(2m, billedProgress.Lines[0].BilledQty);
                Assert.Equal(1m, billedProgress.Lines[1].BilledQty);
                Assert.Equal(100m, billedProgress.PerFulfilled);
                Assert.Equal(100m, billedProgress.PerBilled);
                Assert.Equal(2m, Scalar(connectionString,
                    $"SELECT COUNT(*) FROM purchase_receipt_invoice_matches WHERE invoice_doc_id = {invoiceId};"));
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public async Task PurchaseInvoice_CannotBeCreatedBeforeAnyGoodsReceipt()
        {
            var (service, orders, companyId, connectionString, cleanup) = await CreateAsync();
            try
            {
                var orderId = await service.CreateOrderAsync(
                    Order(companyId, CommercialDocKinds.PurchaseOrder, "PO-9102",
                        new CommercialLineDraft(42, "MAT-42", "Item 42", "PCS", 2m, 15m),
                        partyId: 9, partyCode: "SUP-1"), Actor);
                await service.PostOrderAsync(CommercialDocSide.Purchase, orderId, Actor);

                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    service.CreatePurchaseInvoiceAsync(companyId, orderId, new DateOnly(2026, 11, 4),
                        new[] { new CommercialLineDraft(42, "MAT-42", "Item 42", "PCS", 1m, 15m) }, Actor));

                var invoices = await service.SearchAsync(CommercialDocSide.Purchase, companyId,
                    CommercialDocKinds.PurchaseInvoice, null, null, null, null, 50);
                Assert.Empty(invoices);
                Assert.Equal(100m, BalanceQty(connectionString, 42));
                Assert.Equal(1000m, BalanceValue(connectionString, 42));
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public async Task PartialConversion_BillsOnlyTheRemainingQuantities()
        {
            var (service, orders, companyId, connectionString, cleanup) = await CreateAsync();
            try
            {
                var orderId = await service.CreateOrderAsync(
                    Order(companyId, CommercialDocKinds.SalesOrder, "SO-9102",
                        new CommercialLineDraft(42, "MAT-42", "Item 42", "PCS", 10m, 25m)), Actor);
                await service.PostOrderAsync(CommercialDocSide.Sales, orderId, Actor);

                // Pre-bill 4 of the 10 through the chain path (a posted
                // SAL linked to the order — the chain SAL itself moves
                // no stock, it only books the receivable).
                var chain = new CommercialDocumentRepository("SQLite", connectionString);
                var preId = await chain.CreateSalesDraftAsync(
                    Order(companyId, CommercialDocKinds.SalesInvoice, "SAL-PRE-9102",
                        new CommercialLineDraft(42, "MAT-42", "Item 42", "PCS", 4m, 25m)), Actor);
                await chain.PostSalesAsync(preId, Actor);
                await chain.AddLinkAsync(companyId, CommercialDocKinds.SalesOrder, orderId,
                    CommercialDocKinds.SalesInvoice, preId, Actor);

                var midProgress = await service.GetProgressAsync(CommercialDocSide.Sales, orderId);
                Assert.Equal(40m, midProgress.PerBilled);
                Assert.Equal(100m, BalanceQty(connectionString, 42)); // pre-bill moved no stock

                // Conversion bills ONLY the remaining 6 — and moves
                // stock for exactly those 6.
                var (invoiceId, _) = await service.ConvertToInvoiceAsync(
                    CommercialDocSide.Sales, orderId, new DateOnly(2026, 11, 2), Actor);
                var invoice = await orders.FindByIdAsync(CommercialDocSide.Sales, invoiceId);
                Assert.Equal(6m, invoice!.Lines[0].Qty);
                Assert.Equal(150m, invoice.GrandTotal);
                Assert.Equal(94m, BalanceQty(connectionString, 42));

                var progress = await service.GetProgressAsync(CommercialDocSide.Sales, orderId);
                Assert.Equal(100m, progress.PerBilled);
            }
            finally
            {
                cleanup();
            }
        }

        // ------------------------------------------------------------ (a) DocSearch load path

        [Fact]
        public async Task DocSearchLoadPath_ReturnsTheStoredDocument_ForOrderAndInvoice()
        {
            var (service, orders, companyId, connectionString, cleanup) = await CreateAsync();
            try
            {
                var orderId = await service.CreateOrderAsync(
                    Order(companyId, CommercialDocKinds.SalesOrder, "SO-9103",
                        new CommercialLineDraft(42, "MAT-42", "Item 42", "PCS", 3m, 25m)), Actor);
                await service.PostOrderAsync(CommercialDocSide.Sales, orderId, Actor);

                // The order row DocSearch opens: FindById returns THAT
                // document (id + totals), not a blank.
                var order = await orders.FindByIdAsync(CommercialDocSide.Sales, orderId);
                Assert.NotNull(order);
                Assert.Equal(orderId, order!.DocId);
                Assert.Equal(75m, order.GrandTotal);

                var (invoiceId, _) = await service.ConvertToInvoiceAsync(
                    CommercialDocSide.Sales, orderId, new DateOnly(2026, 11, 2), Actor);
                var invoice = await orders.FindByIdAsync(CommercialDocSide.Sales, invoiceId);
                Assert.NotNull(invoice);
                Assert.Equal(invoiceId, invoice!.DocId);
                Assert.Equal(75m, invoice.GrandTotal);
                Assert.Equal(CommercialDocumentStatus.Posted, invoice.Status);

                // Search sees both families: the Guid-keyed documents
                // above AND a native invoice-family row keyed by the
                // company CODE (the row shape InvRepo writes).
                await using (var connection = new SqliteConnection(connectionString))
                {
                    await connection.OpenAsync();
                    await using var insert = connection.CreateCommand();
                    insert.CommandText = """
                        INSERT INTO sales_documents
                            (company_id, doc_kind, doc_no, doc_date, party_name, currency_code,
                             exchange_rate, subtotal, discount_amount, tax_amount, grand_total,
                             paid_amount, status, is_deleted)
                        VALUES
                            ($code, 'SAL', 'SAL-NATIVE-9103', '2026-11-01', 'Native Party', 'SAR',
                             1, 77, 0, 0, 77, 0, 'Posted', 0);
                        """;
                    insert.Parameters.AddWithValue("$code", CompanyCode);
                    await insert.ExecuteNonQueryAsync();
                }

                var found = await service.SearchAsync(CommercialDocSide.Sales, companyId,
                    CommercialDocKinds.SalesInvoice, null, "NATIVE", null, null, 50);
                Assert.Single(found);
                Assert.Equal("SAL-NATIVE-9103", found[0].DocNo);
                Assert.Equal(77m, found[0].GrandTotal);
            }
            finally
            {
                cleanup();
            }
        }

        // ------------------------------------------------------------ (c) QUO -> SO

        [Fact]
        public async Task Quotation_ConvertToOrder_CreatesLinkedSalesOrderDraft()
        {
            var (service, orders, companyId, connectionString, cleanup) = await CreateAsync();
            try
            {
                var chain = new CommercialDocumentRepository("SQLite", connectionString);
                var quotationId = await chain.CreateSalesDraftAsync(
                    Order(companyId, CommercialDocKinds.SalesQuotation, "QUO-9101",
                        new CommercialLineDraft(42, "MAT-42", "Item 42", "PCS", 2m, 25m)), Actor);
                await chain.PostSalesAsync(quotationId, Actor);

                var orderDocId = await service.ConvertQuotationToOrderAsync(
                    quotationId, new DateOnly(2026, 11, 2), Actor);

                var created = await chain.FindSalesByIdAsync(orderDocId);
                Assert.NotNull(created);
                Assert.Equal(CommercialDocKinds.SalesOrder, created!.DocKind);
                Assert.Equal(CommercialDocumentStatus.Draft, created.Status);
                Assert.Equal(50m, created.GrandTotal);

                var links = await orders.GetLinksFromAsync(companyId, quotationId);
                Assert.Contains(links, l => l.TargetDocId == orderDocId
                    && l.TargetDocKind == CommercialDocKinds.SalesOrder);
            }
            finally
            {
                cleanup();
            }
        }

        // ------------------------------------------------------------ fixture

        private static CommercialDocumentDraft Order(
            Guid companyId, string kind, string docNo, CommercialLineDraft line,
            long partyId = 7, string partyCode = "CUST-1")
            => new(
                companyId, kind, docNo, new DateOnly(2026, 10, 20),
                partyId, "Test Party", null, "SAR", 1m, null,
                new[] { line }, partyCode, new DateOnly(2026, 11, 1), null);

        private static CommercialDocumentDraft Order(
            Guid companyId, string kind, string docNo, CommercialLineDraft line1, CommercialLineDraft line2,
            long partyId = 7, string partyCode = "CUST-1")
            => new(
                companyId, kind, docNo, new DateOnly(2026, 10, 20),
                partyId, "Test Party", null, "SAR", 1m, null,
                new[] { line1, line2 }, partyCode, new DateOnly(2026, 11, 1), null);

        private static decimal BalanceQty(string connectionString, long materialId)
            => Scalar(connectionString,
                $"SELECT COALESCE(available_qty, 0) FROM material_stock_balances WHERE company_id = '{CompanyCode}' AND warehouse_id = 1 AND material_id = {materialId};");

        private static decimal BalanceValue(string connectionString, long materialId)
            => Scalar(connectionString,
                $"SELECT COALESCE(stock_value, 0) FROM material_stock_balances WHERE company_id = '{CompanyCode}' AND warehouse_id = 1 AND material_id = {materialId};");

        private static decimal MovementCost(string connectionString, string docNo, long materialId)
            => Scalar(connectionString,
                $"SELECT COALESCE(SUM(cost_amount), 0) FROM material_stock_movements WHERE company_id = '{CompanyCode}' AND document_no = '{docNo}' AND material_id = {materialId};");

        private static System.Collections.Generic.Dictionary<string, (decimal Debit, decimal Credit)> GlByAccount(
            string connectionString, string sourceType, long sourceId)
        {
            var result = new System.Collections.Generic.Dictionary<string, (decimal, decimal)>();
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT a.account_code, COALESCE(SUM(l.debit), 0), COALESCE(SUM(l.credit), 0)
                FROM journal_entries e
                JOIN journal_entry_lines l ON l.journal_entry_id = e.journal_entry_id
                JOIN chart_of_accounts a ON a.account_id = l.account_id
                WHERE e.source_doc_type = $type AND e.source_doc_id = $source
                  AND e.status = 1 AND e.reversal_of_entry_id IS NULL
                GROUP BY a.account_code;
                """;
            cmd.Parameters.AddWithValue("$type", sourceType);
            cmd.Parameters.AddWithValue("$source", sourceId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                result[reader.GetString(0)] = (reader.GetDecimal(1), reader.GetDecimal(2));
            return result;
        }

        private static decimal Scalar(string connectionString, string sql)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToDecimal(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }

        private static Task<(OrderService Service, OrderDocumentRepository Orders, Guid CompanyId, string ConnectionString, Action Cleanup)> CreateAsync()
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"shoutech-step9-{Guid.NewGuid():N}.sqlite3");
            var connectionString = $"Data Source={databasePath};";
            var companyId = Guid.NewGuid();

            ApplySqliteMigrations(databasePath);
            SeedGlCompany(connectionString, companyId, CompanyCode);
            SeedStock(connectionString, CompanyCode);

            var orders = new OrderDocumentRepository("SQLite", connectionString);
            var chain = new CommercialDocumentRepository("SQLite", connectionString);
            var service = new OrderService(orders, chain);
            Action cleanup = () =>
            {
                SqliteConnection.ClearAllPools();
                if (File.Exists(databasePath)) File.Delete(databasePath);
                foreach (var suffix in new[] { "-wal", "-shm" })
                {
                    var sidecar = databasePath + suffix;
                    if (File.Exists(sidecar)) File.Delete(sidecar);
                }
            };
            return Task.FromResult<(OrderService Service, OrderDocumentRepository Orders, Guid CompanyId, string ConnectionString, Action Cleanup)>((service, orders, companyId, connectionString, cleanup));
        }

        private static void SeedStock(string connectionString, string companyCode)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            void Exec(string sql, params (string Name, object Value)[] ps)
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = sql;
                foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
                cmd.ExecuteNonQuery();
            }
            Exec("INSERT INTO warehouses (warehouse_id, company_id, warehouse_code, warehouse_name_ar, warehouse_name_en, is_active, is_deleted) VALUES (1, $code, 'WH-01', 'Main', 'Main', 1, 0);",
                ("$code", companyCode));
            Exec("INSERT INTO materials (material_id, company_id, code, name_ar, name_en, is_stocked, is_service, cost_method, allow_negative, is_deleted) VALUES (42, $code, 'MAT-42', 'Item 42', 'Item 42', 1, 0, 'WeightedAvg', 0, 0);",
                ("$code", companyCode));
            Exec("INSERT INTO materials (material_id, company_id, code, name_ar, name_en, is_stocked, is_service, cost_method, allow_negative, is_deleted) VALUES (43, $code, 'MAT-43', 'Item 43', 'Item 43', 1, 0, 'WeightedAvg', 0, 0);",
                ("$code", companyCode));
            Exec("INSERT INTO material_stock_balances (company_id, warehouse_id, material_id, available_qty, reserved_qty, average_cost, last_cost, stock_value, is_deleted) VALUES ($code, 1, 42, 100, 0, 10, 10, 1000, 0);",
                ("$code", companyCode));
            Exec("INSERT INTO material_stock_balances (company_id, warehouse_id, material_id, available_qty, reserved_qty, average_cost, last_cost, stock_value, is_deleted) VALUES ($code, 1, 43, 50, 0, 20, 20, 1000, 0);",
                ("$code", companyCode));
        }

        private static void SeedGlCompany(string connectionString, Guid companyId, string companyCode)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            void Exec(string sql, params (string Name, object Value)[] ps)
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = sql;
                foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
                cmd.ExecuteNonQuery();
            }
            Exec("INSERT INTO companies (company_id, company_code, company_name_ar, company_name_en) VALUES ($id, $code, 'Step9 Test Co', 'Step9 Test Co');",
                ("$id", companyId.ToString("D")), ("$code", companyCode));
            Exec("INSERT INTO fiscal_years (company_id, fiscal_year, database_name, start_date, end_date, is_current, is_closed) VALUES ($c, 2026, '', '2026-01-01', '2026-12-31', 1, 0);",
                ("$c", companyId.ToString("D")));
            long yearId;
            using (var q = connection.CreateCommand()) { q.CommandText = "SELECT last_insert_rowid();"; yearId = (long)q.ExecuteScalar()!; }
            for (var m = 1; m <= 12; m++)
            {
                Exec("INSERT INTO fiscal_periods (fiscal_period_id, company_id, fiscal_year_id, period_number, period_name, start_date, end_date, status) VALUES ($id, $c, $y, $n, $name, $s, $e, 'OPEN');",
                    ("$id", Guid.NewGuid().ToString("D")), ("$c", companyId.ToString("D")), ("$y", yearId), ("$n", m),
                    ("$name", $"2026-{m:D2}"), ("$s", $"2026-{m:D2}-01"),
                    ("$e", new DateTime(2026, m, 1).AddMonths(1).AddDays(-1).ToString("yyyy-MM-dd")));
            }
            Exec("INSERT INTO parties (id, company_id, kind, code, name_en, credit_limit, is_active, is_deleted, created_by, ledger_id) VALUES ($id, $company, 'S', 'SUP-1', 'Step9 Supplier', 0, 1, 0, $actor, 9);",
                ("$id", Guid.NewGuid().ToString("D")), ("$company", companyCode), ("$actor", Actor));
            foreach (var (code, name, type) in new[]
            {
                ("1201", "Trade Accounts Receivable", "ASSET"), ("1203", "Merchandise Inventory", "ASSET"),
                ("2201", "Trade Accounts Payable", "LIABILITY"), ("2203", "Accrued Liabilities / GRNI", "LIABILITY"), ("301", "Goods Sales Revenue", "REVENUE"),
                ("401", "Cost of Goods Sold", "EXPENSE"), ("402", "Merchandise Purchases", "EXPENSE"),
                ("131", "Cash on Hand", "ASSET")
            })
            {
                Exec("INSERT INTO chart_of_accounts (account_id, company_id, account_code, account_name, account_type, is_postable, is_active, is_deleted) VALUES ($id, $c, $code, $name, $type, 1, 1, 0);",
                    ("$id", Guid.NewGuid().ToString("D")), ("$c", companyId.ToString("D")), ("$code", code), ("$name", name), ("$type", type));
            }
        }

        private static void ApplySqliteMigrations(string databasePath)
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "dba", "mig")))
                root = root.Parent;

            Assert.NotNull(root);
            var migrator = Path.Combine(root!.FullName, "tools", "SqlMigrator", "bin", "Release", "net8.0", "SqlMigrator.dll");
            Assert.True(File.Exists(migrator), $"SQL migrator was not built: {migrator}");

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    WorkingDirectory = root.FullName,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.StartInfo.ArgumentList.Add(migrator);
            process.StartInfo.ArgumentList.Add("--provider");
            process.StartInfo.ArgumentList.Add("sqlite");
            process.StartInfo.ArgumentList.Add("--connection");
            process.StartInfo.ArgumentList.Add($"Data Source={databasePath};");

            Assert.True(process.Start(), "Could not start the DBA SQL migrator.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            Assert.True(process.WaitForExit(120_000), "DBA SQL migrations timed out.");
            var output = stdout.GetAwaiter().GetResult();
            var errors = stderr.GetAwaiter().GetResult();
            Assert.True(process.ExitCode == 0,
                $"Applying SQLite DBA migrations failed (exit {process.ExitCode}).\n{output}\n{errors}");
        }
    }
}
