using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ShouTech.Application.Services;
using ShouTech.Domain;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;
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
                    quantityBySourceLine: new Dictionary<int, decimal> { [1] = 3m });
                await service.PostAsync(firstInvoiceId, "posting-test");

                Assert.Equal(99.99m, GlSum(connection, companyId, "SAL", firstInvoiceId, "401", debit: true));
                Assert.Equal(99.99m, GlSum(connection, companyId, "SAL", firstInvoiceId, "1215", debit: false));
                Assert.Equal(233.31m, AccountNetDebit(connection, companyId, "1215"));

                var secondInvoiceId = await service.CreateFromSourceAsync(
                    deliveryId, "SAL-B4-PART-2", new DateOnly(2026, 10, 23), "posting-test",
                    quantityBySourceLine: new Dictionary<int, decimal> { [1] = 7m });
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
                    quantityBySourceLine: new Dictionary<int, decimal> { [1] = 4m });
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
    /// <summary>
    /// Phase 09 commissions (migration 097, CommService / CommRepository)
    /// folded into this file because it already owns the migrated chain
    /// fixture (ChainTests: GL company 'CHAIN-GL', material 42, stock).
    /// Pins: policy resolution specificity + tier slab selection (pure
    /// resolver); a sales-basis entry generated from a REAL posted chain
    /// invoice; a proportional NEGATIVE return adjustment; a collection
    /// period-run entry from a payment allocation; and a settlement that
    /// sums Approved entries and pays through a BALANCED journal voucher
    /// (Dr commission expense / Cr cash), flipping entries to Settled.
    /// Execution requires Windows (net8.0-windows testhost).
    /// </summary>
    public class SalesCommissionTests
    {
        private const string Actor = "comm-test";
        private const string CO = "CHAIN-GL";

        private static CommService Service(string connectionString)
            => new(new CommRepository("SQLite", connectionString), new JournalRepository("SQLite", connectionString));

        private static void Exec(string connectionString, string sql)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        private static decimal Scalar(string connectionString, string sql)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToDecimal(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        private static void SeedRep(string connectionString)
            => Exec(connectionString,
                $"INSERT INTO sales_reps (id, company_id, code, name_en) VALUES ('{Guid.NewGuid():D}', '{CO}', 'REP-1', 'Rania Saleh');");

        [Fact]
        public void PolicyResolution_MostSpecificScopeWins_AndTierSlabOverridesFlatRate()
        {
            var fallback = new CommPolicy { Name = "default", Basis = CommBases.Sales, RatePct = 2m };
            var repOnly = new CommPolicy { Name = "rep", Basis = CommBases.Sales, RatePct = 3m, RepScope = CommRepScopes.SpecificRep, RepCode = "REP-1" };
            var repParty = new CommPolicy { Name = "rep+party", Basis = CommBases.Sales, RatePct = 4m, RepScope = CommRepScopes.SpecificRep, RepCode = "REP-1", CustomerScope = CommCustomerScopes.SpecificParty, PartyId = 7 };
            var repGroup = new CommPolicy { Name = "rep+group", Basis = CommBases.Sales, RatePct = 5m, RepScope = CommRepScopes.SpecificRep, RepCode = "REP-1", ProductScope = CommProductScopes.Group, MaterialGroupCode = "GRP-1" };
            var repMaterial = new CommPolicy { Name = "rep+material", Basis = CommBases.Sales, RatePct = 6m, RepScope = CommRepScopes.SpecificRep, RepCode = "REP-1", ProductScope = CommProductScopes.Material, MaterialId = 42 };
            var policies = new List<CommPolicy> { fallback, repOnly, repParty, repGroup, repMaterial };

            Assert.Same(repMaterial, CommPolicyResolver.Resolve(policies, CommBases.Sales, "REP-1", 42, "GRP-1", 7, null, "2026-10-22"));
            Assert.Same(repGroup, CommPolicyResolver.Resolve(policies, CommBases.Sales, "REP-1", 99, "GRP-1", 7, null, "2026-10-22"));
            Assert.Same(repParty, CommPolicyResolver.Resolve(policies, CommBases.Sales, "REP-1", 99, "OTHER", 7, null, "2026-10-22"));
            Assert.Same(repOnly, CommPolicyResolver.Resolve(policies, CommBases.Sales, "REP-1", 99, "OTHER", 8, null, "2026-10-22"));
            Assert.Same(fallback, CommPolicyResolver.Resolve(policies, CommBases.Sales, "REP-9", 99, "OTHER", 8, null, "2026-10-22"));
            Assert.Null(CommPolicyResolver.Resolve(policies, CommBases.Profit, "REP-1", 42, "GRP-1", 7, null, "2026-10-22"));

            var tiered = new CommPolicy
            {
                Name = "tiered", Basis = CommBases.Sales, RatePct = 2m,
                Tiers = new List<CommPolicyTier>
                {
                    new() { FromAmount = 100m, RatePct = 5m },
                    new() { FromAmount = 1000m, RatePct = 8m }
                }
            };
            Assert.Equal(2m, CommPolicyResolver.ResolveRate(tiered, 50m));
            Assert.Equal(5m, CommPolicyResolver.ResolveRate(tiered, 999m));
            Assert.Equal(8m, CommPolicyResolver.ResolveRate(tiered, 1000m));
        }

        [Fact]
        public async Task PostedChainInvoice_GeneratesSalesEntry_AndReturnClawsBackProportionally()
        {
            var (chain, _, companyId, connectionString, cleanup) = await ChainTests.CreateAsync();
            try
            {
                SeedRep(connectionString);
                var service = Service(connectionString);
                await service.SavePolicyAsync(CO, new CommPolicy
                {
                    Name = "Rep flat 10%", Basis = CommBases.Sales, RatePct = 10m,
                    RepScope = CommRepScopes.SpecificRep, RepCode = "REP-1"
                }, Actor);

                // QT -> SO -> DLV -> SAL through the real chain; the rep is
                // stamped on the invoice draft exactly as InvoiceWin would
                // store the free-text sales_rep_name.
                var quotationId = await chain.CreateQuotationAsync(ChainDraft(companyId, "QT-COMM"), Actor);
                var orderId = await chain.CreateFromSourceAsync(quotationId, "SO-COMM", new DateOnly(2026, 10, 20), Actor);
                await chain.PostAsync(orderId, Actor);
                var deliveryId = await chain.CreateFromSourceAsync(orderId, "DLV-COMM", new DateOnly(2026, 10, 21), Actor);
                await chain.PostAsync(deliveryId, Actor);
                var invoiceId = await chain.CreateFromSourceAsync(deliveryId, "SAL-COMM", new DateOnly(2026, 10, 22), Actor);
                Exec(connectionString, $"UPDATE sales_documents SET sales_rep_name = 'REP-1' WHERE doc_id = {invoiceId}");
                await chain.PostAsync(invoiceId, Actor);

                // 10 units x 25 = 250 grand total; 10% = 25.
                Assert.Equal(1, await service.GenerateForInvoiceAsync(companyId.ToString("D"), invoiceId, Actor));
                // Idempotent: a second hook fire creates nothing.
                Assert.Equal(0, await service.GenerateForInvoiceAsync(companyId.ToString("D"), invoiceId, Actor));
                var entries = await service.GetEntriesAsync(CO, "REP-1");
                var entry = Assert.Single(entries);
                Assert.Equal(CommBases.Sales, entry.Basis);
                Assert.Equal(250m, entry.BasisAmount);
                Assert.Equal(25m, entry.CommissionAmount);
                Assert.Equal(CommEntryStatuses.Pending, entry.Status);
                Assert.Equal("2026-10-22", entry.PeriodDate);

                // A posted return of half the invoice (125 of 250) claws
                // back exactly half the commission as a negative entry.
                Exec(connectionString, $"""
                    INSERT INTO sales_documents
                        (company_id, doc_kind, doc_no, doc_date, party_id, party_name, currency_code,
                         subtotal, grand_total, status, sales_rep_name, original_doc_id, original_doc_kind)
                    VALUES ('{companyId:D}', 'SRT', 'SRT-COMM', '2026-10-25', 7, 'Posting Test Customer', 'SAR',
                            125, 125, 'Posted', 'REP-1', {invoiceId}, 'SAL');
                    """);
                var returnId = (long)Scalar(connectionString, "SELECT doc_id FROM sales_documents WHERE doc_no = 'SRT-COMM'");
                Assert.Equal(1, await service.GenerateForReturnAsync(companyId.ToString("D"), returnId, Actor));
                var all = await service.GetEntriesAsync(CO, "REP-1");
                Assert.Equal(2, all.Count);
                var adjustment = Assert.Single(all, e => e.SourceDocKind == CommSourceKinds.SalesReturn);
                Assert.Equal(-125m, adjustment.BasisAmount);
                Assert.Equal(-12.5m, adjustment.CommissionAmount);
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public async Task CollectionRun_Settlement_PaysThroughBalancedJournal()
        {
            var (chain, _, companyId, connectionString, cleanup) = await ChainTests.CreateAsync();
            try
            {
                SeedRep(connectionString);
                // Commission expense account for the payment voucher.
                Exec(connectionString, $"""
                    INSERT INTO chart_of_accounts (account_id, company_id, account_code, account_name, account_type, is_postable, is_active, is_deleted)
                    VALUES ('{Guid.NewGuid():D}', '{companyId:D}', '511', 'Sales Commission Expense', 'EXPENSE', 1, 1, 0);
                    """);
                var service = Service(connectionString);
                await service.SavePolicyAsync(CO, new CommPolicy
                {
                    Name = "Rep flat 10%", Basis = CommBases.Sales, RatePct = 10m,
                    RepScope = CommRepScopes.SpecificRep, RepCode = "REP-1"
                }, Actor);
                await service.SavePolicyAsync(CO, new CommPolicy
                {
                    Name = "Collections 5%", Basis = CommBases.Collection, RatePct = 5m,
                    RepScope = CommRepScopes.SpecificRep, RepCode = "REP-1"
                }, Actor);

                var quotationId = await chain.CreateQuotationAsync(ChainDraft(companyId, "QT-PAY"), Actor);
                var orderId = await chain.CreateFromSourceAsync(quotationId, "SO-PAY", new DateOnly(2026, 10, 20), Actor);
                await chain.PostAsync(orderId, Actor);
                var deliveryId = await chain.CreateFromSourceAsync(orderId, "DLV-PAY", new DateOnly(2026, 10, 21), Actor);
                await chain.PostAsync(deliveryId, Actor);
                var invoiceId = await chain.CreateFromSourceAsync(deliveryId, "SAL-PAY", new DateOnly(2026, 10, 22), Actor);
                Exec(connectionString, $"UPDATE sales_documents SET sales_rep_name = 'REP-1' WHERE doc_id = {invoiceId}");
                await chain.PostAsync(invoiceId, Actor);
                Assert.Equal(1, await service.GenerateForInvoiceAsync(companyId.ToString("D"), invoiceId, Actor));

                // Collection basis: 200 collected against the invoice in
                // the period run -> 5% = 10, keyed by the allocation id.
                Exec(connectionString, $"""
                    INSERT INTO payment_allocations
                        (allocation_id, company_id, document_company_id, party_kind, party_id, payment_ledger_id,
                         doc_table, doc_id, doc_no, amount, allocated_by, allocated_at)
                    VALUES ('{Guid.NewGuid():D}', '{CO}', '{companyId:D}', 'C', 7, 1, 'SALES', {invoiceId}, 'SAL-PAY', 200, '{Actor}', '2026-10-24');
                    """);
                Assert.Equal(1, await service.GenerateCollectionForPeriodAsync(CO, "2026-10-01", "2026-10-31", Actor));
                Assert.Equal(0, await service.GenerateCollectionForPeriodAsync(CO, "2026-10-01", "2026-10-31", Actor));

                var entries = await service.GetEntriesAsync(CO, "REP-1", null, "2026-10-01", "2026-10-31");
                Assert.Equal(2, entries.Count);
                Assert.Equal(10m, Assert.Single(entries, e => e.Basis == CommBases.Collection).CommissionAmount);

                // Approve both, settle the period: 25 + 10 = 35.
                await service.ApproveEntriesAsync(CO, entries.Select(e => e.Id).ToList(), Actor);
                var settlement = await service.CreateSettlementAsync(CO, "REP-1", "2026-10-01", "2026-10-31", Actor);
                Assert.Equal(35m, settlement.TotalAmount);
                Assert.Equal(2, settlement.EntryCount);
                Assert.Equal(CommSettlementStatuses.Draft, settlement.Status);

                // Paying before approval is SAL-7014.
                var notApproved = await Assert.ThrowsAsync<AppError>(() => service.PaySettlementAsync(CO, settlement.Id, Actor));
                Assert.Equal(ErrorCodes.CommissionSettlementInvalid, notApproved.Code);

                await service.ApproveSettlementAsync(CO, settlement.Id, Actor);
                var journalRef = await service.PaySettlementAsync(CO, settlement.Id, Actor);
                Assert.StartsWith("SCO-", journalRef);

                var paid = await service.GetSettlementsAsync(CO, "REP-1");
                var paidSettlement = Assert.Single(paid);
                Assert.Equal(CommSettlementStatuses.Paid, paidSettlement.Status);
                Assert.Equal(journalRef, paidSettlement.JournalRef);
                var settled = await service.GetEntriesAsync(CO, "REP-1", CommEntryStatuses.Settled, "2026-10-01", "2026-10-31");
                Assert.Equal(2, settled.Count);

                // The voucher is balanced: Dr 511 commission expense 35,
                // Cr 131 cash 35, nothing else.
                Assert.Equal(35m, Scalar(connectionString, $"""
                    SELECT COALESCE(SUM(l.debit), 0) FROM journal_entry_lines l
                    JOIN journal_entries e ON e.journal_entry_id = l.journal_entry_id
                    JOIN chart_of_accounts a ON a.account_id = l.account_id
                    WHERE e.document_number = '{journalRef}' AND a.account_code = '511'
                    """));
                Assert.Equal(35m, Scalar(connectionString, $"""
                    SELECT COALESCE(SUM(l.credit), 0) FROM journal_entry_lines l
                    JOIN journal_entries e ON e.journal_entry_id = l.journal_entry_id
                    JOIN chart_of_accounts a ON a.account_id = l.account_id
                    WHERE e.document_number = '{journalRef}' AND a.account_code = '131'
                    """));
                Assert.Equal(0m, Scalar(connectionString, $"""
                    SELECT COALESCE(SUM(l.debit - l.credit), 0) FROM journal_entry_lines l
                    JOIN journal_entries e ON e.journal_entry_id = l.journal_entry_id
                    WHERE e.document_number = '{journalRef}'
                    """));
            }
            finally
            {
                cleanup();
            }
        }

        private static CommercialDocumentDraft ChainDraft(Guid companyId, string docNo) => new(
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
    /// Sales contracts (migration 101, W7c) folded into this file
    /// because it owns the migrated chain fixture (ChainTests: GL
    /// company 'CHAIN-GL', material 42, party 7). Pins: the contract
    /// price source as pricing resolution level (a); order draw-down
    /// validation SAL-7023 (inactive) and SAL-7015 (cumulative quantity
    /// exceeded) at save; scheduled generation idempotent per period;
    /// fulfillment quantities + revenue/COGS rolled up from the posted
    /// chain documents; renewal creating a successor.
    /// </summary>
    public class SalesContractTests
    {
        private const string CompanyCode = "CHAIN-GL";

        private static SalesContract NewContract(decimal qty = 10m, decimal price = 19m, bool withSchedule = false) => new()
        {
            PartyId = 7,
            PartyCode = "CUST-7",
            PartyName = "Posting Test Customer",
            Title = "Annual supply",
            StartDate = "2026-01-01",
            EndDate = "2026-12-31",
            CurrencyCode = "SAR",
            Lines = new List<SalesContractLine>
            {
                new()
                {
                    MaterialId = 42,
                    MaterialCode = "MAT-42",
                    ContractedQty = qty,
                    UnitPrice = price,
                    NextDeliveryDate = withSchedule ? "2026-10-20" : null,
                    IntervalDays = withSchedule ? 7 : null,
                    ScheduledQty = withSchedule ? 4m : 0m
                }
            }
        };

        private static CommercialDocumentDraft OrderDraft(Guid companyId, string docNo, Guid contractId, decimal qty) => new(
            companyId,
            CommercialDocKinds.SalesOrder,
            docNo,
            new DateOnly(2026, 10, 20),
            PartyId: 7,
            PartyName: "Posting Test Customer",
            WarehouseId: 1,
            CurrencyCode: "SAR",
            ExchangeRate: 1m,
            Notes: null,
            new[] { new CommercialLineDraft(42, "MAT-42", "Posting test item", "PCS", qty, 25m) },
            PartyCode: "CUST-7",
            ContractId: contractId.ToString("D"));

        [Fact]
        public async Task ContractPriceSource_PricesActiveContractLine_AsResolutionLevelA()
        {
            var (_, _, companyId, connectionString, cleanup) = await ChainTests.CreateAsync();
            try
            {
                var service = new ContractService(new ContractRepository("SQLite", connectionString));
                var contract = NewContract();
                await service.SaveContractAsync(CompanyCode, contract, "contract-test");
                await service.SetStatusAsync(CompanyCode, contract.Id, ContractStatuses.Active, "contract-test");

                await using var connection = new SqliteConnection(connectionString);
                await connection.OpenAsync();
                await using var transaction = connection.BeginTransaction();
                var resolution = SalesPriceResolver.Default.Resolve(
                    connection, transaction, "SQLite", companyId.ToString("D"),
                    "CUST-7", 42, "MAT-42", 1m, "PCS", "SAR", "2026-10-20");
                Assert.Equal(19m, resolution.Price);
                Assert.Equal(PriceSources.Contract, resolution.Source);
            }
            finally { cleanup(); }
        }

        [Fact]
        public async Task OrderAgainstInactiveContract_ThrowsSAL7023_AndExceeding_ThrowsSAL7015()
        {
            var (_, _, companyId, connectionString, cleanup) = await ChainTests.CreateAsync();
            try
            {
                var contracts = new ContractService(new ContractRepository("SQLite", connectionString));
                var contract = NewContract(qty: 10m);
                await contracts.SaveContractAsync(CompanyCode, contract, "contract-test");
                var orders = new OrderService(
                    new OrderDocumentRepository("SQLite", connectionString),
                    new CommercialDocumentRepository("SQLite", connectionString));

                var inactive = await Assert.ThrowsAsync<AppError>(() =>
                    orders.CreateOrderAsync(OrderDraft(companyId, "SO-C1", contract.Id, 5m), "contract-test"));
                Assert.Equal(ErrorCodes.ContractInactive, inactive.Code);

                await contracts.SetStatusAsync(CompanyCode, contract.Id, ContractStatuses.Active, "contract-test");
                await orders.CreateOrderAsync(OrderDraft(companyId, "SO-C2", contract.Id, 10m), "contract-test");

                var exceeded = await Assert.ThrowsAsync<AppError>(() =>
                    orders.CreateOrderAsync(OrderDraft(companyId, "SO-C3", contract.Id, 1m), "contract-test"));
                Assert.Equal(ErrorCodes.ContractQuantityExceeded, exceeded.Code);
            }
            finally { cleanup(); }
        }

        [Fact]
        public async Task ScheduledGeneration_CreatesOneDraftOrderPerPeriod_Idempotently()
        {
            var (_, _, companyId, connectionString, cleanup) = await ChainTests.CreateAsync();
            try
            {
                var repository = new ContractRepository("SQLite", connectionString);
                var service = new ContractService(repository);
                var contract = NewContract(qty: 10m, withSchedule: true);
                await service.SaveContractAsync(CompanyCode, contract, "contract-test");
                await service.SetStatusAsync(CompanyCode, contract.Id, ContractStatuses.Active, "contract-test");

                var created = await repository.GenerateDueDeliveriesAsync(companyId.ToString("D"), "2026-10-21", "contract-test");
                Assert.Equal(1, created);
                // Cadence advanced to 2026-10-27: a re-run for the same
                // as-of date generates nothing new.
                var again = await repository.GenerateDueDeliveriesAsync(companyId.ToString("D"), "2026-10-21", "contract-test");
                Assert.Equal(0, again);

                await using var connection = new SqliteConnection(connectionString);
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM sales_documents WHERE contract_id = $c AND doc_kind = 'SO' AND status = 'Draft'";
                command.Parameters.AddWithValue("$c", contract.Id.ToString("D"));
                Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
            }
            finally { cleanup(); }
        }

        [Fact]
        public async Task Fulfillment_RollsUpDeliveredInvoicedRevenueAndCogs_FromChain()
        {
            var (service, repository, companyId, connectionString, cleanup) = await ChainTests.CreateAsync();
            try
            {
                var contracts = new ContractService(new ContractRepository("SQLite", connectionString));
                var contract = NewContract(qty: 10m);
                await contracts.SaveContractAsync(CompanyCode, contract, "contract-test");
                await contracts.SetStatusAsync(CompanyCode, contract.Id, ContractStatuses.Active, "contract-test");

                var orderId = await repository.CreateSalesDraftAsync(
                    OrderDraft(companyId, "SO-C9", contract.Id, 10m), "contract-test");
                await service.PostAsync(orderId, "contract-test");
                var deliveryId = await service.CreateFromSourceAsync(orderId, "DLV-C9", new DateOnly(2026, 10, 21), "contract-test");
                await service.PostAsync(deliveryId, "contract-test");
                var invoiceId = await service.CreateFromSourceAsync(deliveryId, "SAL-C9", new DateOnly(2026, 10, 22), "contract-test");
                await service.PostAsync(invoiceId, "contract-test");

                var fulfillment = await contracts.GetFulfillmentAsync(CompanyCode, contract.Id);
                Assert.NotNull(fulfillment);
                Assert.Equal(10m, fulfillment!.DeliveredQty);
                Assert.Equal(10m, fulfillment.InvoicedQty);
                Assert.Equal(0m, fulfillment.RemainingQty);
                Assert.Equal(250m, fulfillment.Revenue);
                Assert.Equal(400m, fulfillment.Cogs);
                Assert.Equal(-150m, fulfillment.Margin);
            }
            finally { cleanup(); }
        }

        [Fact]
        public async Task Renewal_CreatesSuccessorDraft_AndMarksSourceRenewed()
        {
            var (_, _, _, connectionString, cleanup) = await ChainTests.CreateAsync();
            try
            {
                var service = new ContractService(new ContractRepository("SQLite", connectionString));
                var contract = NewContract();
                await service.SaveContractAsync(CompanyCode, contract, "contract-test");
                await service.SetStatusAsync(CompanyCode, contract.Id, ContractStatuses.Active, "contract-test");

                var successor = await service.RenewAsync(CompanyCode, contract.Id, "contract-test");
                Assert.Equal(ContractStatuses.Draft, successor.Status);
                Assert.Equal(contract.Id, successor.RenewalOfId);
                Assert.Equal("2027-01-01", successor.StartDate);
                Assert.Single(successor.Lines);

                var source = await service.GetContractAsync(CompanyCode, contract.Id);
                Assert.Equal(ContractStatuses.Renewed, source!.Status);
            }
            finally { cleanup(); }
        }
    }


    /// <summary>
    /// W5 consolidated invoices + export fields (migration 102): ONE
    /// sales invoice drawing lines from MULTIPLE posted deliveries.
    /// Fixture (ChainTests): material 42, 1000 units on hand at average
    /// cost 40, customer party 7; the standard line is priced 25.
    /// Pins: per-line source attribution on the created invoice; the
    /// two deliveries' 1215 positions relieved by the single invoice
    /// exactly to zero; per-order per_billed exact at 100 for BOTH
    /// orders of a cross-order consolidation (the attribution guards
    /// against the shared invoice being counted twice); partial
    /// consolidation then completion; the SAL-7001/7002/7003/7004
    /// guards; the order-deliveries billing list behind the picker;
    /// and the export header + line attribution persistence round-trip.
    /// </summary>
    public class SalesConsolidatedInvoiceTests
    {
        [Fact]
        public async Task ConsolidatedInvoice_TwoDeliveriesOneOrder_Relieves1215ToZero()
        {
            var (service, repository, companyId, connectionString, cleanup) = await ChainTests.CreateAsync();
            try
            {
                var orderId = await PostedOrderAsync(service, companyId, "C1", 10m);
                var firstDeliveryId = await PostedDeliveryAsync(service, orderId, "DLV-C1-1", 4m);
                var secondDeliveryId = await PostedDeliveryAsync(service, orderId, "DLV-C1-2", 6m);

                await using var connection = new SqliteConnection(connectionString);
                await connection.OpenAsync();
                // Both deliveries parked their cost in 1215: 10 x 40.
                Assert.Equal(400m, AccountNetDebit(connection, companyId, "1215"));

                var invoiceId = await service.CreateConsolidatedInvoiceAsync(
                    new[]
                    {
                        new ConsolidatedDeliverySelection(firstDeliveryId),
                        new ConsolidatedDeliverySelection(secondDeliveryId)
                    },
                    "SAL-C1", new DateOnly(2026, 10, 22), "cons-test");

                var invoice = await repository.FindSalesByIdAsync(invoiceId);
                Assert.NotNull(invoice);
                Assert.Equal(CommercialDocKinds.SalesInvoice, invoice!.DocKind);
                Assert.Equal(250m, invoice.GrandTotal);
                Assert.Equal(2, invoice.Lines.Count);
                // Each line keeps its OWN source delivery + source line.
                Assert.Contains(invoice.Lines, line =>
                    line.SourceDocId == firstDeliveryId && line.SourceLineNo == 1 && line.Qty == 4m);
                Assert.Contains(invoice.Lines, line =>
                    line.SourceDocId == secondDeliveryId && line.SourceLineNo == 1 && line.Qty == 6m);
                // One link per delivery, one link to the shared order.
                var linksTo = await repository.GetSalesLinksToAsync(companyId, invoiceId);
                Assert.Equal(2, linksTo.Count(link => link.SourceDocKind == CommercialDocKinds.DeliveryNote));
                Assert.Single(linksTo, link => link.SourceDocKind == CommercialDocKinds.SalesOrder);

                await service.PostAsync(invoiceId, "cons-test");

                // The single invoice relieved BOTH deliveries' cost.
                Assert.Equal(400m, GlSum(connection, companyId, "SAL", invoiceId, "401", debit: true));
                Assert.Equal(400m, GlSum(connection, companyId, "SAL", invoiceId, "1215", debit: false));
                Assert.Equal(0m, AccountNetDebit(connection, companyId, "1215"));
                Assert.Equal(250m, Scalar(connection,
                    "SELECT debit FROM party_ledger WHERE doc_no = 'SAL-C1' AND party_kind = 'C' AND party_id = 7"));

                var order = await repository.FindSalesByIdAsync(orderId);
                Assert.NotNull(order);
                Assert.Equal(100m, order!.PerDelivered);
                Assert.Equal(100m, order.PerBilled);
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public async Task ConsolidatedInvoice_AcrossTwoOrders_BillsEachOrderExactlyOnce()
        {
            var (service, repository, companyId, connectionString, cleanup) = await ChainTests.CreateAsync();
            try
            {
                var firstOrderId = await PostedOrderAsync(service, companyId, "C2A", 10m);
                var firstDeliveryId = await PostedDeliveryAsync(service, firstOrderId, "DLV-C2A", 10m);
                var secondOrderId = await PostedOrderAsync(service, companyId, "C2B", 10m);
                var secondDeliveryId = await PostedDeliveryAsync(service, secondOrderId, "DLV-C2B", 10m);

                var invoiceId = await service.CreateConsolidatedInvoiceAsync(
                    new[]
                    {
                        new ConsolidatedDeliverySelection(firstDeliveryId),
                        new ConsolidatedDeliverySelection(secondDeliveryId)
                    },
                    "SAL-C2", new DateOnly(2026, 10, 22), "cons-test");
                await service.PostAsync(invoiceId, "cons-test");

                await using var connection = new SqliteConnection(connectionString);
                await connection.OpenAsync();
                Assert.Equal(0m, AccountNetDebit(connection, companyId, "1215"));
                Assert.Equal(500m, Scalar(connection,
                    "SELECT debit FROM party_ledger WHERE doc_no = 'SAL-C2' AND party_kind = 'C' AND party_id = 7"));

                // Stored progress: neither order may count the other
                // order's lines of the shared invoice.
                var firstOrder = await repository.FindSalesByIdAsync(firstOrderId);
                var secondOrder = await repository.FindSalesByIdAsync(secondOrderId);
                Assert.Equal(100m, firstOrder!.PerBilled);
                Assert.Equal(100m, secondOrder!.PerBilled);

                // The recompute helper (OrderDocumentRepository) agrees.
                var orders = new OrderDocumentRepository("SQLite", connectionString);
                var firstProgress = await orders.GetProgressAsync(CommercialDocSide.Sales, firstOrderId);
                var secondProgress = await orders.GetProgressAsync(CommercialDocSide.Sales, secondOrderId);
                Assert.Equal(100m, firstProgress.PerBilled);
                Assert.Equal(100m, secondProgress.PerBilled);
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public async Task ConsolidatedInvoice_PartialThenCompletion_1215EndsAtZero()
        {
            var (service, repository, companyId, connectionString, cleanup) = await ChainTests.CreateAsync();
            try
            {
                var orderId = await PostedOrderAsync(service, companyId, "C3", 10m);
                var firstDeliveryId = await PostedDeliveryAsync(service, orderId, "DLV-C3-1", 5m);
                var secondDeliveryId = await PostedDeliveryAsync(service, orderId, "DLV-C3-2", 5m);

                // First consolidation: delivery 1 in full (5 x 40 = 200
                // of cost) and 2 of delivery 2's 5 units (80 of cost).
                var firstInvoiceId = await service.CreateConsolidatedInvoiceAsync(
                    new[]
                    {
                        new ConsolidatedDeliverySelection(firstDeliveryId),
                        new ConsolidatedDeliverySelection(secondDeliveryId,
                            new Dictionary<int, decimal> { [1] = 2m })
                    },
                    "SAL-C3-1", new DateOnly(2026, 10, 22), "cons-test");
                await service.PostAsync(firstInvoiceId, "cons-test");

                await using var connection = new SqliteConnection(connectionString);
                await connection.OpenAsync();
                Assert.Equal(280m, GlSum(connection, companyId, "SAL", firstInvoiceId, "401", debit: true));
                Assert.Equal(120m, AccountNetDebit(connection, companyId, "1215"));

                // The picker data behind SalesDeliveryWin agrees:
                // delivery 1 fully billed, delivery 2 has 3 remaining.
                var orderService = new OrderService(
                    new OrderDocumentRepository("SQLite", connectionString), repository);
                var deliveries = await orderService.GetOrderDeliveriesAsync(orderId);
                Assert.Equal(2, deliveries.Count);
                Assert.Equal(0m, deliveries.Single(d => d.DeliveryDocId == firstDeliveryId).RemainingQty);
                Assert.Equal(3m, deliveries.Single(d => d.DeliveryDocId == secondDeliveryId).RemainingQty);

                var secondInvoiceId = await service.CreateConsolidatedInvoiceAsync(
                    new[] { new ConsolidatedDeliverySelection(secondDeliveryId) },
                    "SAL-C3-2", new DateOnly(2026, 10, 23), "cons-test");
                await service.PostAsync(secondInvoiceId, "cons-test");

                Assert.Equal(0m, AccountNetDebit(connection, companyId, "1215"));
                var order = await repository.FindSalesByIdAsync(orderId);
                Assert.Equal(100m, order!.PerBilled);
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public async Task ConsolidatedInvoice_Guards_RejectInvalidSelections()
        {
            var (service, repository, companyId, _, cleanup) = await ChainTests.CreateAsync();
            try
            {
                var orderId = await PostedOrderAsync(service, companyId, "C4", 10m);
                var deliveryId = await PostedDeliveryAsync(service, orderId, "DLV-C4", 10m);

                // Quantity beyond the delivery line's remaining (SAL-7002).
                var exceeds = await Assert.ThrowsAsync<AppError>(() =>
                    service.CreateConsolidatedInvoiceAsync(
                        new[]
                        {
                            new ConsolidatedDeliverySelection(deliveryId,
                                new Dictionary<int, decimal> { [1] = 11m })
                        },
                        "SAL-C4-X", new DateOnly(2026, 10, 22), "cons-test"));
                Assert.Equal(ErrorCodes.ChainQuantityExceedsRemaining, exceeds.Code);

                // A non-delivery source cannot be consolidated (SAL-7004).
                var wrongKind = await Assert.ThrowsAsync<AppError>(() =>
                    service.CreateConsolidatedInvoiceAsync(
                        new[] { new ConsolidatedDeliverySelection(orderId) },
                        "SAL-C4-Y", new DateOnly(2026, 10, 22), "cons-test"));
                Assert.Equal(ErrorCodes.ChainTransitionNotAllowed, wrongKind.Code);

                // A different party's delivery cannot join (SAL-7004).
                var otherOrderId = await PostedOrderAsync(service, companyId, "C4B", 10m, partyId: 8);
                var otherDeliveryId = await PostedDeliveryAsync(service, otherOrderId, "DLV-C4B", 10m);
                var mismatch = await Assert.ThrowsAsync<AppError>(() =>
                    service.CreateConsolidatedInvoiceAsync(
                        new[]
                        {
                            new ConsolidatedDeliverySelection(deliveryId),
                            new ConsolidatedDeliverySelection(otherDeliveryId)
                        },
                        "SAL-C4-Z", new DateOnly(2026, 10, 22), "cons-test"));
                Assert.Equal(ErrorCodes.ChainTransitionNotAllowed, mismatch.Code);

                // Bill the delivery in full, then nothing remains (SAL-7003).
                var invoiceId = await service.CreateConsolidatedInvoiceAsync(
                    new[] { new ConsolidatedDeliverySelection(deliveryId) },
                    "SAL-C4", new DateOnly(2026, 10, 22), "cons-test");
                await service.PostAsync(invoiceId, "cons-test");
                var fulfilled = await Assert.ThrowsAsync<AppError>(() =>
                    service.CreateConsolidatedInvoiceAsync(
                        new[] { new ConsolidatedDeliverySelection(deliveryId) },
                        "SAL-C4-W", new DateOnly(2026, 10, 23), "cons-test"));
                Assert.Equal(ErrorCodes.ChainSourceFullyFulfilled, fulfilled.Code);
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public async Task ConsolidatedInvoice_DraftDelivery_ThrowsSal7001()
        {
            var (service, _, companyId, _, cleanup) = await ChainTests.CreateAsync();
            try
            {
                var orderId = await PostedOrderAsync(service, companyId, "C5", 10m);
                var draftDeliveryId = await service.CreateFromSourceAsync(
                    orderId, "DLV-C5", new DateOnly(2026, 10, 21), "cons-test");

                var error = await Assert.ThrowsAsync<AppError>(() =>
                    service.CreateConsolidatedInvoiceAsync(
                        new[] { new ConsolidatedDeliverySelection(draftDeliveryId) },
                        "SAL-C5", new DateOnly(2026, 10, 22), "cons-test"));
                Assert.Equal(ErrorCodes.ChainSourceNotPosted, error.Code);
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public async Task SalesDraft_ExportFieldsAndLineAttribution_RoundTrip()
        {
            var (_, repository, companyId, _, cleanup) = await ChainTests.CreateAsync();
            try
            {
                var draft = new CommercialDocumentDraft(
                    companyId,
                    CommercialDocKinds.SalesInvoice,
                    "SAL-EXP-1",
                    new DateOnly(2026, 10, 22),
                    PartyId: 7,
                    PartyName: "Export Customer",
                    WarehouseId: 1,
                    CurrencyCode: "USD",
                    ExchangeRate: 3.75m,
                    Notes: null,
                    new[]
                    {
                        new CommercialLineDraft(42, "MAT-42", "Export item", "PCS", 2m, 25m,
                            SourceDocId: 555, SourceRefLineNo: 3)
                    },
                    IsExport: true,
                    Incoterm: "FOB",
                    PortOfLoading: "Jeddah",
                    PortOfDischarge: "Rotterdam",
                    CountryOfOrigin: "SA",
                    CustomsRef: "CUS-991",
                    VesselFlight: "MV Aurora",
                    GrossWeight: 12.5m,
                    NetWeight: 10.25m);
                var docId = await repository.CreateSalesDraftAsync(draft, "cons-test");

                var saved = await repository.FindSalesByIdAsync(docId);
                Assert.NotNull(saved);
                Assert.True(saved!.IsExport);
                Assert.Equal("FOB", saved.Incoterm);
                Assert.Equal("Jeddah", saved.PortOfLoading);
                Assert.Equal("Rotterdam", saved.PortOfDischarge);
                Assert.Equal("SA", saved.CountryOfOrigin);
                Assert.Equal("CUS-991", saved.CustomsRef);
                Assert.Equal("MV Aurora", saved.VesselFlight);
                Assert.Equal(12.5m, saved.GrossWeight);
                Assert.Equal(10.25m, saved.NetWeight);
                var line = Assert.Single(saved.Lines);
                Assert.Equal(555, line.SourceDocId);
                Assert.Equal(3, line.SourceLineNo);
            }
            finally
            {
                cleanup();
            }
        }

        private static async Task<long> PostedOrderAsync(
            SalesChainService service, Guid companyId, string suffix, decimal qty, long partyId = 7)
        {
            var quotationId = await service.CreateQuotationAsync(
                Quotation(companyId, $"QT-{suffix}", qty, partyId), "cons-test");
            var orderId = await service.CreateFromSourceAsync(
                quotationId, $"SO-{suffix}", new DateOnly(2026, 10, 20), "cons-test");
            await service.PostAsync(orderId, "cons-test");
            return orderId;
        }

        private static async Task<long> PostedDeliveryAsync(
            SalesChainService service, long orderId, string docNo, decimal qty)
        {
            var deliveryId = await service.CreateFromSourceAsync(
                orderId, docNo, new DateOnly(2026, 10, 21), "cons-test",
                quantityBySourceLine: new Dictionary<int, decimal> { [1] = qty });
            await service.PostAsync(deliveryId, "cons-test");
            return deliveryId;
        }

        private static CommercialDocumentDraft Quotation(Guid companyId, string docNo, decimal qty, long partyId) => new(
            companyId,
            CommercialDocKinds.SalesQuotation,
            docNo,
            new DateOnly(2026, 10, 16),
            PartyId: partyId,
            PartyName: "Consolidated Test Customer",
            WarehouseId: 1,
            CurrencyCode: "SAR",
            ExchangeRate: 1m,
            Notes: null,
            new[]
            {
                new CommercialLineDraft(42, "MAT-42", "Consolidated test item", "PCS", qty, 25m)
            });

        private static decimal Scalar(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToDecimal(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

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
    }


}
