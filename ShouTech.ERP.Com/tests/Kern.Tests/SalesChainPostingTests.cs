using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ShouTech.Application.Services;
using ShouTech.Domain;
using ShouTech.Domain.Entities;
using ShouTech.Infrastructure;
using Xunit;

namespace Kern.Tests
{
    /// <summary>
    /// Posting side-effect tests (issue #7): DLV posts stock movements and balances,
    /// SAL posts the customer party ledger, QUO/SO posts have no side effects.
    /// </summary>
    public class SalesChainPostingTests
    {
        [Fact]
        public async Task PostingDeliveryNote_WritesStockMovementAndDecrementsBalance()
        {
            var (service, _, companyId, connectionString, cleanup) = await ChainTests.CreateAsync();
            try
            {
                var quotationId = await service.CreateQuotationAsync(Draft(companyId, "QT-7001"), "posting-test");
                var orderId = await service.CreateFromSourceAsync(quotationId, "SO-7001", new DateOnly(2026, 10, 20), "posting-test");
                await service.PostAsync(orderId, "posting-test");
                var deliveryId = await service.CreateFromSourceAsync(orderId, "DLV-7001", new DateOnly(2026, 10, 21), "posting-test");

                await service.PostAsync(deliveryId, "posting-test");

                await using var connection = new SqliteConnection(connectionString);
                await connection.OpenAsync();

                Assert.Equal(10m, Scalar(connection,
                    "SELECT qty_out FROM material_stock_movements WHERE document_no = 'DLV-7001' AND material_id = 42"));
                Assert.Equal(990m, Scalar(connection,
                    "SELECT available_qty FROM material_stock_balances WHERE material_id = 42 AND warehouse_id = 1"));
                Assert.Equal(0, (int)Scalar(connection,
                    "SELECT COUNT(*) FROM party_ledger"));
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public async Task PostingDeliveryNote_InSecondUnit_DeductsQuantityTimesEquivalent()
        {
            var (service, _, companyId, connectionString, cleanup) = await ChainTests.CreateAsync();
            try
            {
                // Material 42 is stocked in BOX (base unit); the draft line's
                // unit PCS is its second unit with equivalent 0.25.
                await using (var seed = new SqliteConnection(connectionString))
                {
                    await seed.OpenAsync();
                    await using var update = seed.CreateCommand();
                    update.CommandText = """
                        UPDATE materials
                        SET unit1 = 'BOX', unit1_factor = 1, unit2 = 'PCS',
                            unit2_factor = 0.25,
                            units_json = '[{"UnitName":"BOX","Factor":1},{"UnitName":"PCS","Factor":0.25}]'
                        WHERE company_id = $company AND material_id = 42;
                        """;
                    update.Parameters.AddWithValue("$company", companyId.ToString("D"));
                    Assert.Equal(1, await update.ExecuteNonQueryAsync());
                }

                var quotationId = await service.CreateQuotationAsync(Draft(companyId, "QT-7004"), "posting-test");
                var orderId = await service.CreateFromSourceAsync(quotationId, "SO-7004", new DateOnly(2026, 10, 20), "posting-test");
                await service.PostAsync(orderId, "posting-test");
                var deliveryId = await service.CreateFromSourceAsync(orderId, "DLV-7004", new DateOnly(2026, 10, 21), "posting-test");

                await service.PostAsync(deliveryId, "posting-test");

                await using var connection = new SqliteConnection(connectionString);
                await connection.OpenAsync();

                Assert.Equal(2.5m, Scalar(connection,
                    "SELECT qty_out FROM material_stock_movements WHERE document_no = 'DLV-7004' AND material_id = 42"));
                Assert.Equal(997.5m, Scalar(connection,
                    "SELECT available_qty FROM material_stock_balances WHERE material_id = 42 AND warehouse_id = 1"));
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public async Task PostingDeliveryNote_ForServiceMaterial_CreatesNoMovementOrBalance()
        {
            var (service, _, companyId, connectionString, cleanup) = await ChainTests.CreateAsync();
            try
            {
                // A service material must not create stock movements.
                await using (var seed = new SqliteConnection(connectionString))
                {
                    await seed.OpenAsync();
                    await using var update = seed.CreateCommand();
                    update.CommandText = """
                        UPDATE materials
                        SET is_stocked = 0, is_service = 1, item_type = 'Service'
                        WHERE company_id = $company AND material_id = 42;
                        """;
                    update.Parameters.AddWithValue("$company", companyId.ToString("D"));
                    Assert.Equal(1, await update.ExecuteNonQueryAsync());
                }

                var quotationId = await service.CreateQuotationAsync(Draft(companyId, "QT-7005"), "posting-test");
                var orderId = await service.CreateFromSourceAsync(quotationId, "SO-7005", new DateOnly(2026, 10, 20), "posting-test");
                await service.PostAsync(orderId, "posting-test");
                var deliveryId = await service.CreateFromSourceAsync(orderId, "DLV-7005", new DateOnly(2026, 10, 21), "posting-test");

                await service.PostAsync(deliveryId, "posting-test");

                await using var connection = new SqliteConnection(connectionString);
                await connection.OpenAsync();

                Assert.Equal(0, (int)Scalar(connection,
                    "SELECT COUNT(*) FROM material_stock_movements WHERE material_id = 42"));
                Assert.Equal(1000m, Scalar(connection,
                    "SELECT available_qty FROM material_stock_balances WHERE material_id = 42 AND warehouse_id = 1"));
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public async Task PostingDeliveryNote_RejectsDraftsThatOverDeliverTheSameSalesOrder()
        {
            var (service, repository, companyId, connectionString, cleanup) = await ChainTests.CreateAsync();
            try
            {
                var quotationId = await service.CreateQuotationAsync(Draft(companyId, "QT-OVER-DLV"), "posting-test");
                var orderId = await service.CreateFromSourceAsync(
                    quotationId, "SO-OVER-DLV", new DateOnly(2026, 10, 20), "posting-test");
                await service.PostAsync(orderId, "posting-test");

                // Both drafts are created before either is posted. A draft must
                // not consume quantity, but posting the second must re-check the
                // source order against the first committed delivery.
                var firstDeliveryId = await service.CreateFromSourceAsync(
                    orderId, "DLV-OVER-1", new DateOnly(2026, 10, 21), "posting-test",
                    quantityBySourceLine: new System.Collections.Generic.Dictionary<int, decimal> { [1] = 7m });
                var secondDeliveryId = await service.CreateFromSourceAsync(
                    orderId, "DLV-OVER-2", new DateOnly(2026, 10, 22), "posting-test",
                    quantityBySourceLine: new System.Collections.Generic.Dictionary<int, decimal> { [1] = 7m });

                await service.PostAsync(firstDeliveryId, "posting-test");
                var error = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => service.PostAsync(secondDeliveryId, "posting-test"));
                Assert.Contains("exceeds the remaining quantity", error.Message, StringComparison.OrdinalIgnoreCase);

                var secondDelivery = await repository.FindSalesByIdAsync(secondDeliveryId);
                Assert.NotNull(secondDelivery);
                Assert.Equal(CommercialDocumentStatus.Draft, secondDelivery!.Status);
                await using var verify = new SqliteConnection(connectionString);
                await verify.OpenAsync();
                Assert.Equal(1, (int)Scalar(verify,
                    "SELECT COUNT(*) FROM material_stock_movements WHERE document_no IN ('DLV-OVER-1', 'DLV-OVER-2')"));
                Assert.Equal(993m, Scalar(verify,
                    "SELECT available_qty FROM material_stock_balances WHERE material_id = 42 AND warehouse_id = 1"));
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public async Task PostingSalesInvoice_WritesCustomerPartyLedgerDebit()
        {
            var (service, _, companyId, connectionString, cleanup) = await ChainTests.CreateAsync();
            try
            {
                var quotationId = await service.CreateQuotationAsync(Draft(companyId, "QT-7002"), "posting-test");
                var orderId = await service.CreateFromSourceAsync(quotationId, "SO-7002", new DateOnly(2026, 10, 20), "posting-test");
                await service.PostAsync(orderId, "posting-test");
                var deliveryId = await service.CreateFromSourceAsync(orderId, "DLV-7002", new DateOnly(2026, 10, 21), "posting-test");
                await service.PostAsync(deliveryId, "posting-test");
                var invoiceId = await service.CreateFromSourceAsync(deliveryId, "SAL-7002", new DateOnly(2026, 10, 22), "posting-test");

                await service.PostAsync(invoiceId, "posting-test");

                await using var connection = new SqliteConnection(connectionString);
                await connection.OpenAsync();

                Assert.Equal(250m, Scalar(connection,
                    "SELECT debit FROM party_ledger WHERE doc_no = 'SAL-7002' AND party_kind = 'C' AND party_id = 7"));
                // Stock moved once, on the delivery note; the invoice must not move it again.
                Assert.Equal(1, (int)Scalar(connection,
                    "SELECT COUNT(*) FROM material_stock_movements"));
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public async Task PostingQuotationAndOrder_HaveNoSideEffects()
        {
            var (service, _, companyId, connectionString, cleanup) = await ChainTests.CreateAsync();
            try
            {
                var quotationId = await service.CreateQuotationAsync(Draft(companyId, "QT-7003"), "posting-test");
                var orderId = await service.CreateFromSourceAsync(quotationId, "SO-7003", new DateOnly(2026, 10, 20), "posting-test");
                await service.PostAsync(orderId, "posting-test");

                await using var connection = new SqliteConnection(connectionString);
                await connection.OpenAsync();
                Assert.Equal(0, (int)Scalar(connection, "SELECT COUNT(*) FROM material_stock_movements"));
                Assert.Equal(0, (int)Scalar(connection, "SELECT COUNT(*) FROM party_ledger"));
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public async Task PostingTwice_ThrowsAndDoesNotDuplicateSideEffects()
        {
            var (service, _, companyId, connectionString, cleanup) = await ChainTests.CreateAsync();
            try
            {
                var quotationId = await service.CreateQuotationAsync(Draft(companyId, "QT-7004"), "posting-test");
                var orderId = await service.CreateFromSourceAsync(quotationId, "SO-7004", new DateOnly(2026, 10, 20), "posting-test");
                await service.PostAsync(orderId, "posting-test");
                var deliveryId = await service.CreateFromSourceAsync(orderId, "DLV-7004", new DateOnly(2026, 10, 21), "posting-test");
                await service.PostAsync(deliveryId, "posting-test");

                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    service.PostAsync(deliveryId, "posting-test"));

                await using var connection = new SqliteConnection(connectionString);
                await connection.OpenAsync();
                Assert.Equal(1, (int)Scalar(connection,
                    "SELECT COUNT(*) FROM material_stock_movements WHERE document_no = 'DLV-7004'"));
            }
            finally
            {
                cleanup();
            }
        }

        private static decimal Scalar(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToDecimal(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        private static CommercialDocumentDraft Draft(Guid companyId, string docNo) => new(
            companyId,
            CommercialDocKinds.SalesQuotation,
            docNo,
            new DateOnly(2026, 10, 16),
            PartyId: 7,
            PartyName: "Posting Test Customer",
            WarehouseId: 1,
            CurrencyCode: "SAR",
            ExchangeRate: 1m,
            Notes: null,
            new[]
            {
                new CommercialLineDraft(42, "MAT-42", "Posting test item", "PCS", 10m, 25m)
            });
    }

    /// <summary>
    /// Sales cycle B4 (gaps G12/G13/G14): delivery-note GL
    /// (Dr 1215 Stock Delivered But Not Billed / Cr 1203 Inventory at
    /// movement cost), partial invoicing of one delivery with pro-rata
    /// delivery-cost allocation (final invoice takes the rounding plug),
    /// and chain reversal of posted DLV / SAL documents with the
    /// SAL-7005 / SAL-7006 dependency guards.
    /// Fixture: material 42, 1000 units on hand at average cost 40
    /// (seeded by ChainTests); the standard chain draft is 10 units.
    /// </summary>
    public class SalesChainDeliveryGlTests
    {
        [Fact]
        public async Task PostingDeliveryNote_PostsDeliveredNotBilledGlAtMovementCost()
        {
            var (service, _, companyId, connectionString, cleanup) = await ChainTests.CreateAsync();
            try
            {
                var deliveryId = await PostedDeliveryAsync(service, companyId, "B4-GL");

                await using var connection = new SqliteConnection(connectionString);
                await connection.OpenAsync();

                // Movement cost: 10 units x average cost 40 = 400.
                Assert.Equal(400m, GlSum(connection, companyId, "DLV", deliveryId, "1215", debit: true));
                Assert.Equal(400m, GlSum(connection, companyId, "DLV", deliveryId, "1203", debit: false));
                // Delivered-but-unbilled position is visible in the 1215 balance.
                Assert.Equal(400m, AccountNetDebit(connection, companyId, "1215"));
                Assert.Equal(-400m, AccountNetDebit(connection, companyId, "1203"));
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public async Task PartialInvoices_SplitDeliveryCostProRata_AndSumExactlyToDeliveryCost()
        {
            var (service, _, companyId, connectionString, cleanup) = await ChainTests.CreateAsync();
            try
            {
                // Revalue the fixture stock to 33.33 so the delivery cost
                // (333.30 for 10 units) does not divide evenly: the first
                // invoice (3 units) releases 99.99 pro-rata and the
                // completing invoice (7 units) takes the 233.31 plug.
                await using (var seed = new SqliteConnection(connectionString))
                {
                    await seed.OpenAsync();
                    await using var update = seed.CreateCommand();
                    update.CommandText = "UPDATE material_stock_balances SET average_cost = 33.33, last_cost = 33.33 WHERE material_id = 42 AND warehouse_id = 1";
                    Assert.Equal(1, await update.ExecuteNonQueryAsync());
                }

                var deliveryId = await PostedDeliveryAsync(service, companyId, "B4-PART");

                await using var connection = new SqliteConnection(connectionString);
                await connection.OpenAsync();
                Assert.Equal(333.30m, AccountNetDebit(connection, companyId, "1215"));

                var firstInvoiceId = await service.CreateFromSourceAsync(
                    deliveryId, "SAL-B4-PART-1", new DateOnly(2026, 10, 22), "posting-test",
                    quantityByMaterialId: new Dictionary<long, decimal> { [42] = 3m });
                await service.PostAsync(firstInvoiceId, "posting-test");

                Assert.Equal(99.99m, GlSum(connection, companyId, "SAL", firstInvoiceId, "401", debit: true));
                Assert.Equal(99.99m, GlSum(connection, companyId, "SAL", firstInvoiceId, "1215", debit: false));
                Assert.Equal(233.31m, AccountNetDebit(connection, companyId, "1215"));

                var secondInvoiceId = await service.CreateFromSourceAsync(
                    deliveryId, "SAL-B4-PART-2", new DateOnly(2026, 10, 23), "posting-test",
                    quantityByMaterialId: new Dictionary<long, decimal> { [42] = 7m });
                await service.PostAsync(secondInvoiceId, "posting-test");

                Assert.Equal(233.31m, GlSum(connection, companyId, "SAL", secondInvoiceId, "401", debit: true));
                Assert.Equal(233.31m, GlSum(connection, companyId, "SAL", secondInvoiceId, "1215", debit: false));
                // Both invoices together released the delivery cost to the cent.
                Assert.Equal(0m, AccountNetDebit(connection, companyId, "1215"));
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public async Task DeliveryReversal_NetsStockAndGlToZero()
        {
            var (service, repository, companyId, connectionString, cleanup) = await ChainTests.CreateAsync();
            try
            {
                var deliveryId = await PostedDeliveryAsync(service, companyId, "B4-REV");

                await repository.ReverseChainDocumentAsync(
                    companyId, deliveryId, "Customer refused the goods", "reversal-test");

                await using var connection = new SqliteConnection(connectionString);
                await connection.OpenAsync();

                Assert.Equal(1000m, Scalar(connection,
                    "SELECT available_qty FROM material_stock_balances WHERE material_id = 42 AND warehouse_id = 1"));
                // Issue and reversal receipt net to zero quantity and cost.
                Assert.Equal(0m, Scalar(connection,
                    "SELECT COALESCE(SUM(qty_in - qty_out), 0) FROM material_stock_movements WHERE document_no IN ('DLV-B4-REV', 'DLV-B4-REV-REV')"));
                Assert.Equal(0m, Scalar(connection,
                    "SELECT COALESCE(SUM(cost_amount), 0) FROM material_stock_movements WHERE document_no IN ('DLV-B4-REV', 'DLV-B4-REV-REV')"));
                Assert.Equal(0m, AccountNetDebit(connection, companyId, "1215"));
                Assert.Equal(0m, AccountNetDebit(connection, companyId, "1203"));
                Assert.Equal("Cancelled", Text(connection,
                    $"SELECT status FROM sales_documents WHERE doc_id = {deliveryId}"));
                Assert.Equal(1, (int)Scalar(connection,
                    $"SELECT COUNT(*) FROM document_reversals WHERE original_doc_kind = 'DLV' AND original_doc_id = {deliveryId} AND reason = 'Customer refused the goods' AND actor = 'reversal-test' AND status = 'Completed'"));
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public async Task DeliveryReversal_WithPostedInvoice_ThrowsSal7005()
        {
            var (service, repository, companyId, _, cleanup) = await ChainTests.CreateAsync();
            try
            {
                var deliveryId = await PostedDeliveryAsync(service, companyId, "B4-GRD");
                var invoiceId = await service.CreateFromSourceAsync(
                    deliveryId, "SAL-B4-GRD", new DateOnly(2026, 10, 22), "posting-test");
                await service.PostAsync(invoiceId, "posting-test");

                var error = await Assert.ThrowsAsync<AppError>(() =>
                    repository.ReverseChainDocumentAsync(companyId, deliveryId, "Too late", "reversal-test"));
                Assert.Equal(ErrorCodes.ChainReversalBlockedDownstream, error.Code);
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public async Task InvoiceReversal_WithPaidAmount_ThrowsSal7006()
        {
            var (service, repository, companyId, connectionString, cleanup) = await ChainTests.CreateAsync();
            try
            {
                var deliveryId = await PostedDeliveryAsync(service, companyId, "B4-PAID");
                var invoiceId = await service.CreateFromSourceAsync(
                    deliveryId, "SAL-B4-PAID", new DateOnly(2026, 10, 22), "posting-test");
                await service.PostAsync(invoiceId, "posting-test");

                await using (var seed = new SqliteConnection(connectionString))
                {
                    await seed.OpenAsync();
                    await using var update = seed.CreateCommand();
                    update.CommandText = $"UPDATE sales_documents SET paid_amount = 25 WHERE doc_id = {invoiceId}";
                    Assert.Equal(1, await update.ExecuteNonQueryAsync());
                }

                var error = await Assert.ThrowsAsync<AppError>(() =>
                    repository.ReverseChainDocumentAsync(companyId, invoiceId, "Paid already", "reversal-test"));
                Assert.Equal(ErrorCodes.ChainReversalBlockedPayments, error.Code);
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public async Task InvoiceReversal_WithPaymentAllocation_ThrowsSal7006()
        {
            var (service, repository, companyId, connectionString, cleanup) = await ChainTests.CreateAsync();
            try
            {
                var deliveryId = await PostedDeliveryAsync(service, companyId, "B4-ALLOC");
                var invoiceId = await service.CreateFromSourceAsync(
                    deliveryId, "SAL-B4-ALLOC", new DateOnly(2026, 10, 22), "posting-test");
                await service.PostAsync(invoiceId, "posting-test");

                await using (var seed = new SqliteConnection(connectionString))
                {
                    await seed.OpenAsync();
                    await using var insert = seed.CreateCommand();
                    insert.CommandText = """
                        INSERT INTO payment_allocations
                            (allocation_id, company_id, document_company_id, party_kind, party_id,
                             payment_ledger_id, doc_table, doc_id, doc_no, amount, allocated_by)
                        VALUES ('alloc-b4-1', 'CHAIN-GL', $company, 'C', 7, 1, 'SALES', $doc, 'SAL-B4-ALLOC', 25, 'posting-test')
                        """;
                    insert.Parameters.AddWithValue("$company", companyId.ToString("D"));
                    insert.Parameters.AddWithValue("$doc", invoiceId);
                    Assert.Equal(1, await insert.ExecuteNonQueryAsync());
                }

                var error = await Assert.ThrowsAsync<AppError>(() =>
                    repository.ReverseChainDocumentAsync(companyId, invoiceId, "Allocated already", "reversal-test"));
                Assert.Equal(ErrorCodes.ChainReversalBlockedPayments, error.Code);
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public async Task InvoiceReversal_MirrorsGlAndPartyLedger()
        {
            var (service, repository, companyId, connectionString, cleanup) = await ChainTests.CreateAsync();
            try
            {
                var deliveryId = await PostedDeliveryAsync(service, companyId, "B4-SREV");
                var invoiceId = await service.CreateFromSourceAsync(
                    deliveryId, "SAL-B4-SREV", new DateOnly(2026, 10, 22), "posting-test");
                await service.PostAsync(invoiceId, "posting-test");

                await repository.ReverseChainDocumentAsync(
                    companyId, invoiceId, "Invoice issued in error", "reversal-test");

                await using var connection = new SqliteConnection(connectionString);
                await connection.OpenAsync();

                // Party ledger debit (250) mirrored by an equal credit.
                Assert.Equal(0m, Scalar(connection,
                    "SELECT COALESCE(SUM(debit - credit), 0) FROM party_ledger WHERE doc_no = 'SAL-B4-SREV'"));
                // Receivable and revenue net to zero across post + reversal.
                Assert.Equal(0m, AccountNetDebit(connection, companyId, "1201"));
                Assert.Equal(0m, AccountNetDebit(connection, companyId, "301"));
                // COGS reversal returns the cost to delivered-not-billed:
                // the delivery still stands, so 1215 carries the 400 again.
                Assert.Equal(400m, AccountNetDebit(connection, companyId, "1215"));
                Assert.Equal("Cancelled", Text(connection,
                    $"SELECT status FROM sales_documents WHERE doc_id = {invoiceId}"));
                Assert.Equal(1, (int)Scalar(connection,
                    $"SELECT COUNT(*) FROM document_reversals WHERE original_doc_kind = 'SAL' AND original_doc_id = {invoiceId} AND status = 'Completed'"));
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public async Task CreateInvoiceFromDelivery_BlocksDuplicateDraft_ButAllowsNextAfterPartialPosted()
        {
            var (_, repository, companyId, connectionString, cleanup) = await ChainTests.CreateAsync();
            try
            {
                var chain = new SalesChainService(repository);
                var orders = new OrderService(
                    new OrderDocumentRepository("SQLite", connectionString), repository);
                var deliveryId = await PostedDeliveryAsync(chain, companyId, "B4-ORD");

                // A partial invoice (4 of 10) is created and posted through
                // the chain service.
                var partialId = await chain.CreateFromSourceAsync(
                    deliveryId, "SAL-B4-ORD-1", new DateOnly(2026, 10, 22), "posting-test",
                    quantityByMaterialId: new Dictionary<long, decimal> { [42] = 4m });
                await chain.PostAsync(partialId, "posting-test");

                // The pre-B4 service refused ANY further invoice once one
                // active invoice existed; now the next invoice for the
                // remaining 6 units is created normally (gap G14).
                var next = await orders.CreateSalesInvoiceFromDeliveryAsync(
                    deliveryId, new DateOnly(2026, 10, 23), "posting-test");
                Assert.True(next.InvoiceId > 0);
                Assert.NotEqual(partialId, next.InvoiceId);

                // But a second open DRAFT for the same delivery is a
                // duplicate and is blocked.
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    orders.CreateSalesInvoiceFromDeliveryAsync(
                        deliveryId, new DateOnly(2026, 10, 23), "posting-test"));

                // The created draft bills exactly the remaining quantity.
                var snapshot = await repository.FindSalesByIdAsync(next.InvoiceId);
                Assert.NotNull(snapshot);
                Assert.Single(snapshot!.Lines);
                Assert.Equal(6m, snapshot.Lines[0].Qty);
            }
            finally
            {
                cleanup();
            }
        }

        /// <summary>Builds QT -> SO -> DLV for the standard 10-unit draft and posts the delivery.</summary>
        private static async Task<long> PostedDeliveryAsync(SalesChainService service, Guid companyId, string suffix)
        {
            var quotationId = await service.CreateQuotationAsync(Draft(companyId, $"QT-{suffix}"), "posting-test");
            var orderId = await service.CreateFromSourceAsync(quotationId, $"SO-{suffix}", new DateOnly(2026, 10, 20), "posting-test");
            await service.PostAsync(orderId, "posting-test");
            var deliveryId = await service.CreateFromSourceAsync(orderId, $"DLV-{suffix}", new DateOnly(2026, 10, 21), "posting-test");
            await service.PostAsync(deliveryId, "posting-test");
            return deliveryId;
        }

        private static CommercialDocumentDraft Draft(Guid companyId, string docNo) => new(
            companyId,
            CommercialDocKinds.SalesQuotation,
            docNo,
            new DateOnly(2026, 10, 16),
            PartyId: 7,
            PartyName: "Posting Test Customer",
            WarehouseId: 1,
            CurrencyCode: "SAR",
            ExchangeRate: 1m,
            Notes: null,
            new[]
            {
                new CommercialLineDraft(42, "MAT-42", "Posting test item", "PCS", 10m, 25m)
            });

        /// <summary>Sum of one side (debit or credit) of one account on one document's live GL entry.</summary>
        private static decimal GlSum(SqliteConnection connection, Guid companyId, string sourceKind, long sourceDocId, string accountCode, bool debit)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT COALESCE(SUM(l.{(debit ? "debit" : "credit")}), 0)
                FROM journal_entries e
                JOIN journal_entry_lines l ON l.journal_entry_id = e.journal_entry_id
                JOIN chart_of_accounts a ON a.account_id = l.account_id
                WHERE e.company_id = $company AND e.source_doc_type = $kind
                  AND e.source_doc_id = $source AND e.reversal_of_entry_id IS NULL
                  AND a.company_id = $company AND a.account_code = $account
                """;
            command.Parameters.AddWithValue("$company", companyId.ToString("D"));
            command.Parameters.AddWithValue("$kind", sourceKind);
            command.Parameters.AddWithValue("$source", sourceDocId.ToString(CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$account", accountCode);
            return Convert.ToDecimal(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        /// <summary>Net debit (debits - credits) of one account across all live entries of the company.</summary>
        private static decimal AccountNetDebit(SqliteConnection connection, Guid companyId, string accountCode)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COALESCE(SUM(l.debit - l.credit), 0)
                FROM journal_entry_lines l
                JOIN journal_entries e ON e.journal_entry_id = l.journal_entry_id
                JOIN chart_of_accounts a ON a.account_id = l.account_id
                WHERE e.company_id = $company AND a.company_id = $company AND a.account_code = $account
                """;
            command.Parameters.AddWithValue("$company", companyId.ToString("D"));
            command.Parameters.AddWithValue("$account", accountCode);
            return Convert.ToDecimal(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        private static decimal Scalar(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToDecimal(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        private static string Text(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
        }
    }

    internal sealed class ChainTests
    {
        public static Task<(SalesChainService Service, ShouTech.Infrastructure.CommercialDocumentRepository Repository, Guid CompanyId, string ConnectionString, Action Cleanup)> CreateAsync()
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"shoutech-sales-posting-{Guid.NewGuid():N}.sqlite3");
            var connectionString = $"Data Source={databasePath};";
            var companyId = Guid.NewGuid();

            ApplySqliteMigrations(databasePath);
            SeedGlCompany(connectionString, companyId, "CHAIN-GL");
            SeedInventory(connectionString, companyId);

            var repository = new ShouTech.Infrastructure.CommercialDocumentRepository("SQLite", connectionString);
            var service = new SalesChainService(repository);
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
            return Task.FromResult<(SalesChainService Service, ShouTech.Infrastructure.CommercialDocumentRepository Repository, Guid CompanyId, string ConnectionString, Action Cleanup)>((service, repository, companyId, connectionString, cleanup));
        }


        private static void SeedInventory(string connectionString, Guid companyId)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO warehouses
                    (warehouse_id, company_id, warehouse_code, warehouse_name_ar, warehouse_name_en, is_active, is_deleted)
                VALUES (1, $companyId, 1, 'Main', 'Main', 1, 0);

                INSERT INTO materials
                    (material_id, company_id, code, name_ar, name_en, is_stocked, is_deleted, allow_negative, unit1, unit1_factor)
                VALUES (42, $companyId, 'MAT-42', 'Posting test item', 'Posting test item', 1, 0, 0, 'PCS', 1);

                INSERT INTO material_stock_balances
                    (company_id, warehouse_id, material_id, available_qty, average_cost, last_cost)
                VALUES ('CHAIN-GL', 1, 42, 1000, 40, 40);
                """;
            command.Parameters.AddWithValue("$companyId", companyId.ToString("D"));
            command.ExecuteNonQuery();
        }

        /// <summary>Step 3: document posting now creates real GL entries,
        /// so the fixture company must exist in companies with an open
        /// fiscal year/periods and the canonical posting accounts.</summary>
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
            Exec("INSERT INTO companies (company_id, company_code, company_name_ar, company_name_en) VALUES ($id, $code, 'GL Test Co', 'GL Test Co');",
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
            foreach (var (code, name, type) in new[]
            {
                ("1201", "Trade Accounts Receivable", "ASSET"), ("1203", "Merchandise Inventory", "ASSET"),
                ("1215", "Stock Delivered But Not Billed", "ASSET"),
                ("2201", "Trade Accounts Payable", "LIABILITY"), ("301", "Goods Sales Revenue", "REVENUE"),
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
