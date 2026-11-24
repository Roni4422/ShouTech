using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
    /// Selling-chain tests (issue #7): Quotation -> Sales Order -> Delivery Note -> Invoice,
    /// partial fulfillment, transition guards, and chain-link recording.
    /// </summary>
    public class SalesChainTests
    {
        [Fact]
        public async Task OrderFromQuotation_CopiesTotalsAndRecordsLink()
        {
            var (service, repository, companyId, _, cleanup) = await CreateAsync();
            try
            {
                var quotationId = await service.CreateQuotationAsync(
                    Quotation(companyId, "QT-1001", 10m, 25m), "chain-test");

                var orderId = await service.CreateFromSourceAsync(
                    quotationId, "SO-2001", new DateOnly(2026, 10, 20), "chain-test");
                await service.PostAsync(orderId, "chain-test");

                var order = await repository.FindSalesByIdAsync(orderId);
                Assert.NotNull(order);
                Assert.Equal(CommercialDocKinds.SalesOrder, order!.DocKind);
                Assert.Equal("SO-2001", order.DocNo);
                Assert.Equal(CommercialDocumentStatus.Posted, order.Status);
                Assert.Equal(250m, order.Subtotal);
                Assert.Equal(250m, order.GrandTotal);

                var links = await repository.GetSalesLinksFromAsync(companyId, quotationId);
                var link = Assert.Single(links);
                Assert.Equal(CommercialDocKinds.SalesQuotation, link.SourceDocKind);
                Assert.Equal(CommercialDocKinds.SalesOrder, link.TargetDocKind);
                Assert.Equal(orderId, link.TargetDocId);
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public async Task DeliveryFromOrder_SupportsPartialQuantityThenRemaining()
        {
            var (service, repository, companyId, _, cleanup) = await CreateAsync();
            try
            {
                var quotationId = await service.CreateQuotationAsync(
                    Quotation(companyId, "QT-1002", 10m, 25m), "chain-test");
                var orderId = await service.CreateFromSourceAsync(
                    quotationId, "SO-2002", new DateOnly(2026, 10, 20), "chain-test");
                await service.PostAsync(orderId, "chain-test");

                var firstDelivery = await service.CreateFromSourceAsync(
                    orderId, "DLV-3001", new DateOnly(2026, 10, 21), "chain-test",
                    quantityBySourceLine: new Dictionary<int, decimal> { [1] = 4m });
                await service.PostAsync(firstDelivery, "chain-test");

                var fulfilled = await repository.GetFulfilledQuantitiesAsync(companyId, orderId, CommercialDocKinds.DeliveryNote);
                Assert.Equal(4m, fulfilled[42]);

                var secondDelivery = await service.CreateFromSourceAsync(
                    orderId, "DLV-3002", new DateOnly(2026, 10, 22), "chain-test");
                var delivery = await repository.FindSalesByIdAsync(secondDelivery);
                Assert.NotNull(delivery);
                Assert.Equal(6m, delivery!.Lines[0].Qty);

                await service.PostAsync(secondDelivery, "chain-test");
                var fulfilledError = await Assert.ThrowsAsync<AppError>(() =>
                    service.CreateFromSourceAsync(orderId, "DLV-3003", new DateOnly(2026, 10, 23), "chain-test"));
                Assert.Equal(ErrorCodes.ChainSourceFullyFulfilled, fulfilledError.Code);
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public async Task Chain_RejectsInvalidTransitionsAndDraftSources()
        {
            var (service, repository, companyId, _, cleanup) = await CreateAsync();
            try
            {
                var quotationId = await service.CreateQuotationAsync(
                    Quotation(companyId, "QT-1003", 5m, 10m), "chain-test");

                // A quotation can only create a sales order, never an invoice directly.
                var orderId = await service.CreateFromSourceAsync(
                    quotationId, "SO-1003", new DateOnly(2026, 10, 20), "chain-test");
                var order = await repository.FindSalesByIdAsync(orderId);
                Assert.NotNull(order);
                Assert.Equal(CommercialDocKinds.SalesOrder, order!.DocKind);

                // Draft sources cannot continue the chain.
                var draftId = await repository.CreateSalesDraftAsync(
                    Quotation(companyId, "QT-1004", 5m, 10m), "chain-test");
                var notPostedError = await Assert.ThrowsAsync<AppError>(() =>
                    service.CreateFromSourceAsync(draftId, "SO-9999", new DateOnly(2026, 10, 20), "chain-test"));
                Assert.Equal(ErrorCodes.ChainSourceNotPosted, notPostedError.Code);
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public async Task PostedOrder_CannotBeCancelledWhilePostedDeliveryIsLinked()
        {
            var (service, _, companyId, connectionString, cleanup) = await CreateAsync();
            try
            {
                var quotationId = await service.CreateQuotationAsync(
                    Quotation(companyId, "QT-1010", 10m, 25m), "chain-test");
                var orderId = await service.CreateFromSourceAsync(
                    quotationId, "SO-2010", new DateOnly(2026, 10, 20), "chain-test");
                await service.PostAsync(orderId, "chain-test");

                var deliveryId = await service.CreateFromSourceAsync(
                    orderId, "DLV-3010", new DateOnly(2026, 10, 21), "chain-test",
                    quantityBySourceLine: new Dictionary<int, decimal> { [1] = 3m });
                await service.PostAsync(deliveryId, "chain-test");

                var orders = new ShouTech.Infrastructure.OrderDocumentRepository("SQLite", connectionString);
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    orders.SetStatusAsync(global::ShouTech.Domain.Interfaces.CommercialDocSide.Sales, orderId,
                        CommercialDocumentStatus.Cancelled, "chain-test"));
                Assert.Contains("downstream", error.Message.ToLowerInvariant());

                var order = await orders.GetProgressAsync(global::ShouTech.Domain.Interfaces.CommercialDocSide.Sales, orderId);
                Assert.Equal(30m, order.PerFulfilled);
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public async Task CreateFromSource_RollsBackDraftWhenLinkInsertFails()
        {
            var (service, _, companyId, connectionString, cleanup) = await CreateAsync();
            try
            {
                var quotationId = await service.CreateQuotationAsync(
                    Quotation(companyId, "QT-ATOMIC-1", 2m, 15m), "chain-test");

                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText = """
                        CREATE TRIGGER reject_document_link
                        BEFORE INSERT ON document_links
                        BEGIN
                            SELECT RAISE(ABORT, 'forced link failure');
                        END;
                        """;
                    command.ExecuteNonQuery();
                }

                await Assert.ThrowsAsync<SqliteException>(() => service.CreateFromSourceAsync(
                    quotationId, "SO-ATOMIC-1", new DateOnly(2026, 10, 20), "chain-test"));

                using var verify = new SqliteConnection(connectionString);
                verify.Open();
                using var query = verify.CreateCommand();
                query.CommandText = "SELECT COUNT(*) FROM sales_documents WHERE company_id = $company AND doc_no = 'SO-ATOMIC-1';";
                query.Parameters.AddWithValue("$company", companyId.ToString("D"));
                Assert.Equal(0L, Convert.ToInt64(query.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public async Task DeliveryFromOrder_DuplicateMaterialLinesDeliverIndependentlyBySourceLine()
        {
            var (service, repository, companyId, _, cleanup) = await CreateAsync();
            try
            {
                var quotationId = await service.CreateQuotationAsync(new CommercialDocumentDraft(
                    companyId,
                    CommercialDocKinds.SalesQuotation,
                    "QT-DUP-MAT",
                    new DateOnly(2026, 10, 16),
                    7,
                    "Chain Test Customer",
                    1,
                    "SAR",
                    1m,
                    null,
                    new[]
                    {
                        new CommercialLineDraft(42, "MAT-42", "First line", "PCS", 4m, 25m),
                        new CommercialLineDraft(42, "MAT-42", "Second line", "PCS", 6m, 25m)
                    }), "chain-test");

                var orderId = await service.CreateFromSourceAsync(
                    quotationId, "SO-DUP-MAT", new DateOnly(2026, 10, 20), "chain-test");
                await service.PostAsync(orderId, "chain-test");

                // Deliver ONLY the second line (same material as the first):
                // per-source-line quantities must not touch line 1, and the
                // delivery line keeps the source line number as its identity.
                var firstDeliveryId = await service.CreateFromSourceAsync(
                    orderId, "DLV-DUP-MAT-1", new DateOnly(2026, 10, 21), "chain-test",
                    quantityBySourceLine: new Dictionary<int, decimal> { [2] = 5m });
                var firstDelivery = await repository.FindSalesByIdAsync(firstDeliveryId);
                Assert.NotNull(firstDelivery);
                var deliveredLine = Assert.Single(firstDelivery!.Lines);
                Assert.Equal(2, deliveredLine.LineNo);
                Assert.Equal(5m, deliveredLine.Qty);
                Assert.Equal("Second line", deliveredLine.Description);

                await service.PostAsync(firstDeliveryId, "chain-test");
                var fulfilledByLine = await repository.GetFulfilledQuantitiesBySourceLineAsync(
                    companyId, orderId, CommercialDocKinds.DeliveryNote);
                Assert.Equal(5m, fulfilledByLine[2]);
                Assert.False(fulfilledByLine.ContainsKey(1));

                // The remaining conversion carries line 1 in full and the
                // one unit left on line 2, each under its own line number.
                var secondDeliveryId = await service.CreateFromSourceAsync(
                    orderId, "DLV-DUP-MAT-2", new DateOnly(2026, 10, 22), "chain-test");
                var secondDelivery = await repository.FindSalesByIdAsync(secondDeliveryId);
                Assert.NotNull(secondDelivery);
                Assert.Equal(new[] { 1, 2 }, secondDelivery!.Lines.Select(line => line.LineNo).ToArray());
                Assert.Equal(new[] { 4m, 1m }, secondDelivery.Lines.Select(line => line.Qty).ToArray());
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public async Task CreateFromSource_RejectsQuantityOverRemaining()
        {
            var (service, _, companyId, _, cleanup) = await CreateAsync();
            try
            {
                var quotationId = await service.CreateQuotationAsync(
                    Quotation(companyId, "QT-1005", 10m, 25m), "chain-test");
                var orderId = await service.CreateFromSourceAsync(
                    quotationId, "SO-2005", new DateOnly(2026, 10, 20), "chain-test");
                await service.PostAsync(orderId, "chain-test");

                var exceedsError = await Assert.ThrowsAsync<AppError>(() =>
                    service.CreateFromSourceAsync(orderId, "DLV-3005", new DateOnly(2026, 10, 21), "chain-test",
                        quantityBySourceLine: new Dictionary<int, decimal> { [1] = 11m }));
                Assert.Equal(ErrorCodes.ChainQuantityExceedsRemaining, exceedsError.Code);
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public async Task Conversion_CarriesFullHeaderAndSourceLineNumbers()
        {
            var (service, repository, companyId, _, cleanup) = await CreateAsync();
            try
            {
                var quotationId = await service.CreateQuotationAsync(new CommercialDocumentDraft(
                    companyId,
                    CommercialDocKinds.SalesQuotation,
                    "QT-HDR-1",
                    new DateOnly(2026, 10, 16),
                    7,
                    "Chain Test Customer",
                    1,
                    "SAR",
                    1m,
                    "Header notes carry over",
                    new[]
                    {
                        new CommercialLineDraft(42, "MAT-42", "First line", "PCS", 4m, 25m),
                        new CommercialLineDraft(42, "MAT-42", "Second line", "PCS", 6m, 25m)
                    },
                    PartyCode: "CUS-7",
                    DeliveryDate: new DateOnly(2026, 11, 1),
                    ValidTill: new DateOnly(2026, 12, 31),
                    CostCenter: "CC-1",
                    BranchName: "Main Branch",
                    SalesRepName: "Rep One",
                    WarehouseName: "Main"), "chain-test");

                var orderId = await service.CreateFromSourceAsync(
                    quotationId, "SO-HDR-1", new DateOnly(2026, 10, 20), "chain-test");
                var order = await repository.FindSalesByIdAsync(orderId);
                Assert.NotNull(order);
                Assert.Equal("CUS-7", order!.PartyCode);
                Assert.Equal(new DateOnly(2026, 11, 1), order.DeliveryDate);
                Assert.Equal(new DateOnly(2026, 12, 31), order.ValidTill);
                Assert.Equal("CC-1", order.CostCenter);
                Assert.Equal("Main Branch", order.BranchName);
                Assert.Equal("Rep One", order.SalesRepName);
                Assert.Equal("Main", order.WarehouseName);
                Assert.Equal("Header notes carry over", order.Notes);
                Assert.Equal(new[] { 1, 2 }, order.Lines.Select(line => line.LineNo).ToArray());
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public async Task ChainDraft_CarriesHeaderDiscountAndExtraProRata()
        {
            var (service, repository, companyId, _, cleanup) = await CreateAsync();
            try
            {
                var quotationId = await service.CreateQuotationAsync(new CommercialDocumentDraft(
                    companyId,
                    CommercialDocKinds.SalesQuotation,
                    "QT-MONEY-1",
                    new DateOnly(2026, 10, 16),
                    7,
                    "Chain Test Customer",
                    1,
                    "SAR",
                    1m,
                    null,
                    new[] { new CommercialLineDraft(42, "MAT-42", "Chain test item", "PCS", 10m, 25m) },
                    DiscountAmount: 20m,
                    ExtraAmount: 10m), "chain-test");

                var quotation = await repository.FindSalesByIdAsync(quotationId);
                Assert.NotNull(quotation);
                Assert.Equal(240m, quotation!.GrandTotal);

                var orderId = await service.CreateFromSourceAsync(
                    quotationId, "SO-MONEY-1", new DateOnly(2026, 10, 20), "chain-test");
                await service.PostAsync(orderId, "chain-test");
                var order = await repository.FindSalesByIdAsync(orderId);
                Assert.NotNull(order);
                Assert.Equal(20m, order!.DiscountAmount);
                Assert.Equal(10m, order.ExtraAmount);
                Assert.Equal(240m, order.GrandTotal);

                // Half the quantity converts: header money follows pro-rata
                // and the stored grand total stays coherent
                // (125 subtotal - 10 discount + 0 tax + 5 extra = 120).
                var deliveryId = await service.CreateFromSourceAsync(
                    orderId, "DLV-MONEY-1", new DateOnly(2026, 10, 21), "chain-test",
                    quantityBySourceLine: new Dictionary<int, decimal> { [1] = 5m });
                var delivery = await repository.FindSalesByIdAsync(deliveryId);
                Assert.NotNull(delivery);
                Assert.Equal(10m, delivery!.DiscountAmount);
                Assert.Equal(5m, delivery.ExtraAmount);
                Assert.Equal(120m, delivery.GrandTotal);
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public async Task StoredProgress_AndDerivedStatus_FollowPartialDeliveryAndInvoice()
        {
            var (service, repository, companyId, connectionString, cleanup) = await CreateAsync();
            try
            {
                var orders = new ShouTech.Infrastructure.OrderDocumentRepository("SQLite", connectionString);
                var orderService = new OrderService(orders, repository);

                var quotationId = await service.CreateQuotationAsync(
                    Quotation(companyId, "QT-PROG-1", 10m, 25m), "chain-test");
                var orderId = await service.CreateFromSourceAsync(
                    quotationId, "SO-PROG-1", new DateOnly(2026, 10, 20), "chain-test");
                await service.PostAsync(orderId, "chain-test");

                var (deliveryId, _) = await orderService.CreateSalesDeliveryAsync(
                    orderId, new DateOnly(2026, 10, 21), "chain-test",
                    new Dictionary<int, decimal> { [1] = 5m });
                await orderService.PostSalesDeliveryAsync(deliveryId, "chain-test");

                var afterDelivery = await repository.FindSalesByIdAsync(orderId);
                Assert.NotNull(afterDelivery);
                Assert.Equal(50m, afterDelivery!.PerDelivered);
                Assert.Equal(0m, afterDelivery.PerBilled);
                Assert.Equal(SalesChainDerivedStatus.ToDeliverAndBill, afterDelivery.DerivedStatus);

                var (invoiceId, _) = await orderService.CreateSalesInvoiceFromDeliveryAsync(
                    deliveryId, new DateOnly(2026, 10, 22), "chain-test");
                await orderService.PostSalesChainDocumentAsync(invoiceId, "chain-test");

                var afterInvoice = await repository.FindSalesByIdAsync(orderId);
                Assert.NotNull(afterInvoice);
                Assert.Equal(50m, afterInvoice!.PerDelivered);
                Assert.Equal(50m, afterInvoice.PerBilled);
                Assert.Equal(SalesChainDerivedStatus.ToDeliverAndBill, afterInvoice.DerivedStatus);

                var progress = await orders.GetProgressAsync(
                    global::ShouTech.Domain.Interfaces.CommercialDocSide.Sales, orderId);
                Assert.Equal(afterInvoice.PerDelivered, progress.PerFulfilled);
                Assert.Equal(afterInvoice.PerBilled, progress.PerBilled);
                Assert.Equal(SalesChainDerivedStatus.ToDeliverAndBill, progress.DerivedStatus);

                var (secondDeliveryId, _) = await orderService.CreateSalesDeliveryAsync(
                    orderId, new DateOnly(2026, 10, 23), "chain-test");
                await orderService.PostSalesDeliveryAsync(secondDeliveryId, "chain-test");

                // Fully delivered, half billed: the derived status flips
                // to To Bill, driven by the stored columns.
                var afterSecondDelivery = await repository.FindSalesByIdAsync(orderId);
                Assert.NotNull(afterSecondDelivery);
                Assert.Equal(100m, afterSecondDelivery!.PerDelivered);
                Assert.Equal(50m, afterSecondDelivery.PerBilled);
                Assert.Equal(SalesChainDerivedStatus.ToBill, afterSecondDelivery.DerivedStatus);

                var (secondInvoiceId, _) = await orderService.CreateSalesInvoiceFromDeliveryAsync(
                    secondDeliveryId, new DateOnly(2026, 10, 24), "chain-test");
                await orderService.PostSalesChainDocumentAsync(secondInvoiceId, "chain-test");

                var completed = await repository.FindSalesByIdAsync(orderId);
                Assert.NotNull(completed);
                Assert.Equal(100m, completed!.PerDelivered);
                Assert.Equal(100m, completed.PerBilled);
                Assert.Equal(SalesChainDerivedStatus.Completed, completed.DerivedStatus);
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public async Task QuotationExpiry_SweepsPostedQuotationsPastValidTill()
        {
            var (service, repository, companyId, connectionString, cleanup) = await CreateAsync();
            try
            {
                var orders = new ShouTech.Infrastructure.OrderDocumentRepository("SQLite", connectionString);
                var orderService = new OrderService(orders, repository);
                var today = DateOnly.FromDateTime(DateTime.Today);

                var expiredId = await repository.CreateSalesDraftAsync(new CommercialDocumentDraft(
                    companyId, CommercialDocKinds.SalesQuotation, "QT-EXP-1",
                    today.AddDays(-10), 7, "Chain Test Customer", 1, "SAR", 1m, null,
                    new[] { new CommercialLineDraft(42, "MAT-42", "Chain test item", "PCS", 2m, 10m) },
                    ValidTill: today.AddDays(-1)), "chain-test");
                await repository.PostSalesAsync(expiredId, "chain-test");

                var liveId = await repository.CreateSalesDraftAsync(new CommercialDocumentDraft(
                    companyId, CommercialDocKinds.SalesQuotation, "QT-EXP-2",
                    today.AddDays(-10), 7, "Chain Test Customer", 1, "SAR", 1m, null,
                    new[] { new CommercialLineDraft(42, "MAT-42", "Chain test item", "PCS", 2m, 10m) },
                    ValidTill: today.AddDays(5)), "chain-test");
                await repository.PostSalesAsync(liveId, "chain-test");

                var expiredCount = await orderService.ExpirePostedQuotationsAsync(companyId, today);
                Assert.Equal(1, expiredCount);

                var expiredDoc = await repository.FindSalesByIdAsync(expiredId);
                Assert.Equal(CommercialDocumentStatus.Expired, expiredDoc!.Status);
                var liveDoc = await repository.FindSalesByIdAsync(liveId);
                Assert.Equal(CommercialDocumentStatus.Posted, liveDoc!.Status);

                // Converting the expired quotation is blocked with SAL-7008.
                var expiredError = await Assert.ThrowsAsync<AppError>(() =>
                    orderService.ConvertQuotationToOrderAsync(expiredId, today, "chain-test"));
                Assert.Equal(ErrorCodes.QuotationExpired, expiredError.Code);

                // Expiry is a quotation-only transition.
                var orderId = await service.CreateFromSourceAsync(
                    liveId, "SO-EXP-1", today, "chain-test");
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    orders.SetStatusAsync(global::ShouTech.Domain.Interfaces.CommercialDocSide.Sales,
                        orderId, CommercialDocumentStatus.Expired, "chain-test"));
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public async Task DirectConversion_StoresBilledProgressAndToDeliverStatus()
        {
            var (service, repository, companyId, connectionString, cleanup) = await CreateAsync();
            try
            {
                var orders = new ShouTech.Infrastructure.OrderDocumentRepository("SQLite", connectionString);
                var orderService = new OrderService(orders, repository);

                var quotationId = await service.CreateQuotationAsync(
                    Quotation(companyId, "QT-PROG-2", 4m, 25m), "chain-test");
                var orderId = await service.CreateFromSourceAsync(
                    quotationId, "SO-PROG-2", new DateOnly(2026, 10, 20), "chain-test");
                await service.PostAsync(orderId, "chain-test");

                await orderService.ConvertToInvoiceAsync(
                    global::ShouTech.Domain.Interfaces.CommercialDocSide.Sales,
                    orderId, new DateOnly(2026, 10, 25), "chain-test");

                var order = await repository.FindSalesByIdAsync(orderId);
                Assert.NotNull(order);
                Assert.Equal(0m, order!.PerDelivered);
                Assert.Equal(100m, order.PerBilled);
                Assert.Equal(SalesChainDerivedStatus.ToDeliver, order.DerivedStatus);
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public async Task Cancellation_RequiresAndPersistsDocumentedReason()
        {
            var (service, repository, companyId, connectionString, cleanup) = await CreateAsync();
            try
            {
                var orders = new ShouTech.Infrastructure.OrderDocumentRepository("SQLite", connectionString);
                var orderService = new OrderService(orders, repository);

                var quotationId = await service.CreateQuotationAsync(
                    Quotation(companyId, "QT-CXL-1", 2m, 10m), "chain-test");
                var orderId = await service.CreateFromSourceAsync(
                    quotationId, "SO-CXL-1", new DateOnly(2026, 10, 20), "chain-test");

                // No reason, no cancellation (spec sections 3/14).
                await Assert.ThrowsAsync<ArgumentException>(() =>
                    orderService.CancelOrderAsync(
                        global::ShouTech.Domain.Interfaces.CommercialDocSide.Sales, orderId, "chain-test"));
                await Assert.ThrowsAsync<ArgumentException>(() =>
                    orderService.CancelOrderAsync(
                        global::ShouTech.Domain.Interfaces.CommercialDocSide.Sales, orderId, "chain-test", reason: "  "));

                await orderService.CancelOrderAsync(
                    global::ShouTech.Domain.Interfaces.CommercialDocSide.Sales, orderId, "chain-test",
                    reason: "Customer withdrew the request");

                var cancelled = await repository.FindSalesByIdAsync(orderId);
                Assert.NotNull(cancelled);
                Assert.Equal(CommercialDocumentStatus.Cancelled, cancelled!.Status);
                Assert.Equal("Customer withdrew the request", cancelled.CancelReason);
                Assert.Equal("chain-test", cancelled.CancelledBy);
                Assert.False(string.IsNullOrWhiteSpace(cancelled.CancelledAt));
            }
            finally
            {
                cleanup();
            }
        }

        // ------------------------------------------- lifecycle (W23, mig 098)

        [Fact]
        public async Task Amendment_PreservesPriorRevisionAndIncrementsRevisionNo()
        {
            var (service, repository, companyId, _, cleanup) = await CreateAsync();
            try
            {
                // A posted quotation with no downstream yet: amendable.
                var quotationId = await service.CreateQuotationAsync(
                    Quotation(companyId, "QT-REV-1", 10m, 25m), "rev-test");
                var before = await repository.FindSalesByIdAsync(quotationId);
                Assert.Equal(1, before!.RevisionNo);

                var newRevision = await service.AmendAsync(quotationId,
                    Quotation(companyId, "QT-REV-1", 4m, 30m), expectedRevision: 1,
                    "rev-test", "Customer asked for fewer units at a better price");
                Assert.Equal(2, newRevision);

                var amended = await repository.FindSalesByIdAsync(quotationId);
                Assert.NotNull(amended);
                Assert.Equal(2, amended!.RevisionNo);
                Assert.Equal(CommercialDocumentStatus.Draft, amended.Status);
                Assert.Equal(120m, amended.GrandTotal);
                Assert.Equal(4m, amended.Lines[0].Qty);

                // The prior version is retrievable read-only, in full.
                var revisions = await service.GetRevisionsAsync(quotationId);
                var prior = Assert.Single(revisions);
                Assert.Equal(1, prior.RevisionNo);
                Assert.Contains("\"QT-REV-1\"", prior.SnapshotJson);
                Assert.Contains("25", prior.SnapshotJson);
                Assert.Equal("Customer asked for fewer units at a better price", prior.ChangeReason);
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public async Task Amendment_WithStaleRevision_ThrowsSal7017()
        {
            var (service, repository, companyId, _, cleanup) = await CreateAsync();
            try
            {
                var quotationId = await service.CreateQuotationAsync(
                    Quotation(companyId, "QT-REV-2", 10m, 25m), "rev-test");
                await service.AmendAsync(quotationId,
                    Quotation(companyId, "QT-REV-2", 5m, 25m), expectedRevision: 1, "rev-test");

                var error = await Assert.ThrowsAsync<AppError>(() => service.AmendAsync(quotationId,
                    Quotation(companyId, "QT-REV-2", 6m, 25m), expectedRevision: 1, "rev-test"));
                Assert.Equal(ErrorCodes.DocumentRevisionConflict, error.Code);
                Assert.Equal("SAL-7017", error.Code);

                var current = await repository.FindSalesByIdAsync(quotationId);
                Assert.Equal(2, current!.RevisionNo);
                Assert.Equal(5m, current.Lines[0].Qty);
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public async Task PostedOrder_WithPostedDownstream_CannotBeAmended()
        {
            var (service, repository, companyId, _, cleanup) = await CreateAsync();
            try
            {
                var quotationId = await service.CreateQuotationAsync(
                    Quotation(companyId, "QT-REV-3", 10m, 25m), "rev-test");
                var orderId = await service.CreateFromSourceAsync(
                    quotationId, "SO-REV-3", new DateOnly(2026, 10, 20), "rev-test");
                await service.PostAsync(orderId, "rev-test");

                // No posted downstream yet: the posted order amends cleanly
                // back to a draft revision.
                var orderDraft = new CommercialDocumentDraft(
                    companyId, CommercialDocKinds.SalesOrder, "SO-REV-3",
                    new DateOnly(2026, 10, 20), PartyId: 7, PartyName: "Chain Test Customer",
                    WarehouseId: 1, CurrencyCode: "SAR", ExchangeRate: 1m, Notes: null,
                    new[] { new CommercialLineDraft(42, "MAT-42", "Chain test item", "PCS", 8m, 25m) });
                var newRevision = await service.AmendAsync(orderId, orderDraft, expectedRevision: 1, "rev-test");
                Assert.Equal(2, newRevision);
                var amended = await repository.FindSalesByIdAsync(orderId);
                Assert.Equal(CommercialDocumentStatus.Draft, amended!.Status);

                // Post it again and convert: now a posted downstream exists.
                await service.PostAsync(orderId, "rev-test");
                var deliveryId = await service.CreateFromSourceAsync(
                    orderId, "DLV-REV-3", new DateOnly(2026, 10, 21), "rev-test");
                await service.PostAsync(deliveryId, "rev-test");

                var error = await Assert.ThrowsAsync<AppError>(() => service.AmendAsync(
                    orderId, orderDraft, expectedRevision: 2, "rev-test"));
                Assert.Equal(ErrorCodes.ApprovalStateInvalid, error.Code);
                Assert.Equal("SAL-7016", error.Code);
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public async Task ApprovalWorkflow_BlocksPostingUntilApproved_WhenCompanyRequiresIt()
        {
            var (service, repository, companyId, _, cleanup) = await CreateAsync();
            try
            {
                await service.SetApprovalRequiredAsync(companyId, true, "appr-test");
                Assert.True(await service.IsApprovalRequiredAsync(companyId));

                // Draft created directly (CreateQuotationAsync would post).
                var draftId = await repository.CreateSalesDraftAsync(
                    Quotation(companyId, "QT-APPR-1", 10m, 25m), "appr-test");

                var blocked = await Assert.ThrowsAsync<AppError>(() =>
                    service.PostAsync(draftId, "appr-test"));
                Assert.Equal(ErrorCodes.ApprovalStateInvalid, blocked.Code);

                // Approve before submit is an invalid transition.
                var early = await Assert.ThrowsAsync<AppError>(() =>
                    service.ApproveAsync(draftId, "boss", _ => true));
                Assert.Equal(ErrorCodes.ApprovalStateInvalid, early.Code);

                await service.SubmitForApprovalAsync(draftId, "appr-test");
                var submitted = await repository.FindSalesByIdAsync(draftId);
                Assert.Equal(SalesApprovalState.Pending, submitted!.ApprovalState);

                // Approval is a separated duty: without the permission the
                // service refuses before touching state.
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                    service.ApproveAsync(draftId, "boss", _ => false));

                await service.ApproveAsync(draftId, "boss", _ => true);
                var approved = await repository.FindSalesByIdAsync(draftId);
                Assert.Equal(SalesApprovalState.Approved, approved!.ApprovalState);

                await service.PostAsync(draftId, "appr-test");
                var posted = await repository.FindSalesByIdAsync(draftId);
                Assert.Equal(CommercialDocumentStatus.Posted, posted!.Status);

                var history = await service.GetApprovalHistoryAsync(draftId);
                Assert.Equal(new[] { SalesApprovalAction.Submitted, SalesApprovalAction.Approved },
                    history.Select(h => h.Action).ToArray());
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public async Task ApprovalRejection_RequiresReasonAndBlocksPosting()
        {
            var (service, repository, companyId, _, cleanup) = await CreateAsync();
            try
            {
                await service.SetApprovalRequiredAsync(companyId, true, "appr-test");
                var draftId = await repository.CreateSalesDraftAsync(
                    Quotation(companyId, "QT-APPR-2", 10m, 25m), "appr-test");
                await service.SubmitForApprovalAsync(draftId, "appr-test");

                await Assert.ThrowsAsync<ArgumentException>(() =>
                    service.RejectAsync(draftId, "boss", "  ", _ => true));

                await service.RejectAsync(draftId, "boss", "Price is above the approved margin.", _ => true);
                var rejected = await repository.FindSalesByIdAsync(draftId);
                Assert.Equal(SalesApprovalState.Rejected, rejected!.ApprovalState);

                var blocked = await Assert.ThrowsAsync<AppError>(() =>
                    service.PostAsync(draftId, "appr-test"));
                Assert.Equal(ErrorCodes.ApprovalStateInvalid, blocked.Code);

                var history = await service.GetApprovalHistoryAsync(draftId);
                var rejection = Assert.Single(history, h => h.Action == SalesApprovalAction.Rejected);
                Assert.Equal("Price is above the approved margin.", rejection.Reason);
                Assert.Equal("boss", rejection.Actor);
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public async Task ApprovalWorkflow_DefaultOff_PostingUnaffected()
        {
            var (service, repository, companyId, _, cleanup) = await CreateAsync();
            try
            {
                Assert.False(await service.IsApprovalRequiredAsync(companyId));
                var quotationId = await service.CreateQuotationAsync(
                    Quotation(companyId, "QT-APPR-3", 10m, 25m), "appr-test");
                var posted = await repository.FindSalesByIdAsync(quotationId);
                Assert.Equal(CommercialDocumentStatus.Posted, posted!.Status);
                Assert.Equal(SalesApprovalState.NotRequired, posted.ApprovalState);
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public async Task DirectQuotationToInvoice_RequiresPermissionAndLinksChain()
        {
            var (service, repository, companyId, _, cleanup) = await CreateAsync();
            try
            {
                var quotationId = await service.CreateQuotationAsync(
                    Quotation(companyId, "QT-DIR-1", 10m, 25m), "direct-test");

                // Without SALES.QUOTATION.DIRECTINVOICE the shortcut refuses.
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                    service.CreateInvoiceFromQuotationAsync(
                        quotationId, "SAL-DIR-1", new DateOnly(2026, 10, 22), "direct-test", _ => false));

                var invoiceId = await service.CreateInvoiceFromQuotationAsync(
                    quotationId, "SAL-DIR-1", new DateOnly(2026, 10, 22), "direct-test",
                    code => code == SalesChainService.QuotationDirectInvoicePermission);
                var invoice = await repository.FindSalesByIdAsync(invoiceId);
                Assert.NotNull(invoice);
                Assert.Equal(CommercialDocKinds.SalesInvoice, invoice!.DocKind);
                Assert.Equal(CommercialDocumentStatus.Draft, invoice.Status);
                Assert.Equal(250m, invoice.GrandTotal);

                var links = await repository.GetSalesLinksFromAsync(companyId, quotationId);
                var link = Assert.Single(links);
                Assert.Equal(CommercialDocKinds.SalesInvoice, link.TargetDocKind);
                Assert.Equal(invoiceId, link.TargetDocId);
            }
            finally
            {
                cleanup();
            }
        }

        private static string? DbPath;

        // ---------------------------------------------------------------
        // W7a pricing & promotions: resolver levels, discount rules,
        // BXGY, and the SAL-7018/7019/7020 guard with audited overrides.
        // ---------------------------------------------------------------

        private static void SeedPricing(string connectionString, string extraSql)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE materials SET price_retail = 100, price_wholesale = 80, price_list = 90
                WHERE material_id = 42;
                INSERT INTO parties (id, company_id, kind, code, name_en, price_tier, is_deleted)
                VALUES ('party-w7a', 'SC-TEST', 'C', 'CUST-1', 'Pricing Customer', 'Wholesale', 0);
                """ + extraSql;
            command.ExecuteNonQuery();
        }

        private static PriceResolution ResolveWith(string connectionString, Guid companyId, string partyCode, decimal qty)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var tx = connection.BeginTransaction();
            // The Guid company key mirrors the chain repositories; the
            // resolver's dual-key list also covers the 'SC-TEST' code
            // that pricing masters (customer_prices, discount_rules)
            // are keyed by.
            return SalesPriceResolver.Default.Resolve(
                connection, tx, "SQLite", companyId.ToString("D"), partyCode, 42, "MAT-42", qty, "PCS", "SAR", "2026-10-20");
        }

        [Fact]
        public async Task Pricing_CustomerPriceWinsOverTierAndCard()
        {
            var (_, _, companyId, connectionString, cleanup) = await CreateAsync();
            try
            {
                SeedPricing(connectionString, """
                    INSERT INTO customer_prices (id, company_id, party_code, material_code, price, is_active, is_deleted)
                    VALUES ('cp-1', 'SC-TEST', 'CUST-1', 'MAT-42', 55, 1, 0);
                    """);
                var resolution = ResolveWith(connectionString, companyId, "CUST-1", 1m);
                Assert.Equal(55m, resolution.Price);
                Assert.Equal(PriceSources.CustomerPrice, resolution.Source);
            }
            finally { cleanup(); }
        }

        [Fact]
        public async Task Pricing_PriceListTierBeatsTierColumn_AndExpiredRowsAreIgnored()
        {
            var (_, _, companyId, connectionString, cleanup) = await CreateAsync();
            try
            {
                SeedPricing(connectionString, """
                    INSERT INTO material_price_list (company_id, material_id, price_level, price, currency, unit_name, valid_from, valid_to)
                    VALUES ('SC-TEST', 42, 'Wholesale', 1, '', NULL, '2020-01-01', '2020-12-31');
                    INSERT INTO material_price_list (company_id, material_id, price_level, price, currency, unit_name, valid_from, valid_to)
                    VALUES ('SC-TEST', 42, 'Wholesale', 70, '', NULL, '2026-01-01', NULL);
                    """);
                var resolution = ResolveWith(connectionString, companyId, "CUST-1", 1m);
                Assert.Equal(70m, resolution.Price);
                Assert.Equal(PriceSources.PriceListTier, resolution.Source);
            }
            finally { cleanup(); }
        }

        [Fact]
        public async Task Pricing_TierColumnThenMaterialCardFallback()
        {
            var (_, _, companyId, connectionString, cleanup) = await CreateAsync();
            try
            {
                SeedPricing(connectionString, "");
                var tier = ResolveWith(connectionString, companyId, "CUST-1", 1m);
                Assert.Equal(80m, tier.Price);
                Assert.Equal(PriceSources.TierColumn, tier.Source);

                var card = ResolveWith(connectionString, companyId, "", 1m);
                Assert.Equal(90m, card.Price);
                Assert.Equal(PriceSources.MaterialCard, card.Source);
            }
            finally { cleanup(); }
        }

        [Fact]
        public async Task Pricing_DiscountRulesTakeHighestQualifying_AndBxgyEarnsFreeQty()
        {
            var (_, _, companyId, connectionString, cleanup) = await CreateAsync();
            try
            {
                SeedPricing(connectionString, """
                    UPDATE materials SET bonus_buy_qty = 5, bonus_qty = 1 WHERE material_id = 42;
                    INSERT INTO discount_rules (id, company_id, scope, target_code, discount_pct, min_qty, is_active, is_deleted)
                    VALUES ('dr-1', 'SC-TEST', 'CUSTOMER', 'CUST-1', 5, 10, 1, 0);
                    INSERT INTO discount_rules (id, company_id, scope, target_code, discount_pct, min_qty, is_active, is_deleted)
                    VALUES ('dr-2', 'SC-TEST', 'MATERIAL', 'MAT-42', 3, 0, 1, 0);
                    """);
                var resolution = ResolveWith(connectionString, companyId, "CUST-1", 12m);
                Assert.Equal(5m, resolution.DiscountPct);
                Assert.Equal(2m, resolution.BonusFreeQty);

                var belowBreak = ResolveWith(connectionString, companyId, "CUST-1", 4m);
                Assert.Equal(3m, belowBreak.DiscountPct);
                Assert.Equal(0m, belowBreak.BonusFreeQty);
            }
            finally { cleanup(); }
        }

        private static void GuardWith(string connectionString, Guid companyId, IReadOnlyList<PriceGuardLine> lines, PriceOverrideContext? overrides)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var tx = connection.BeginTransaction();
            SalesPriceGuard.Validate(connection, tx, "SQLite", companyId.ToString("D"), "SAL", "SAL-1", 1, lines, overrides);
            tx.Commit();
        }

        [Fact]
        public async Task PricingGuard_BlocksBelowMinimum_AndOverrideIsAudited()
        {
            var (_, _, companyId, connectionString, cleanup) = await CreateAsync();
            try
            {
                SeedPricing(connectionString, "UPDATE materials SET price_min = 50 WHERE material_id = 42;");
                var lines = new[] { new PriceGuardLine(1, 42, "MAT-42", 1m, 40m, 0m) };
                var error = Assert.Throws<AppError>(() => GuardWith(connectionString, companyId, lines, null));
                Assert.Equal(ErrorCodes.PriceBelowMinimum, error.Code);

                GuardWith(connectionString, companyId, lines,
                    new PriceOverrideContext("pricing-admin", PriceOverride: true, DiscountOverride: false));
                using var verify = new SqliteConnection(connectionString);
                verify.Open();
                using var command = verify.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM spr_price_overrides WHERE rule_code = 'SAL-7018' AND actor = 'pricing-admin'";
                Assert.Equal(1L, Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
            }
            finally { cleanup(); }
        }

        [Fact]
        public async Task PricingGuard_MarginAndDiscountRules()
        {
            var (_, _, companyId, connectionString, cleanup) = await CreateAsync();
            try
            {
                // Balance average cost is 40 (fixture); price 45 nets a
                // margin of ~11% — below the 20% target.
                SeedPricing(connectionString, "UPDATE materials SET margin_target_pct = 20, discount_max_pct = 10 WHERE material_id = 42;");
                var marginError = Assert.Throws<AppError>(() => GuardWith(connectionString, companyId,
                    new[] { new PriceGuardLine(1, 42, "MAT-42", 1m, 45m, 0m) }, null));
                Assert.Equal(ErrorCodes.MarginBelowMinimum, marginError.Code);

                var discountError = Assert.Throws<AppError>(() => GuardWith(connectionString, companyId,
                    new[] { new PriceGuardLine(1, 42, "MAT-42", 1m, 100m, 15m) }, null));
                Assert.Equal(ErrorCodes.DiscountExceedsLimit, discountError.Code);

                // A discount-only override does not authorize a price breach.
                var stillBlocked = Assert.Throws<AppError>(() => GuardWith(connectionString, companyId,
                    new[] { new PriceGuardLine(1, 42, "MAT-42", 1m, 45m, 0m) },
                    new PriceOverrideContext("discounter", PriceOverride: false, DiscountOverride: true)));
                Assert.Equal(ErrorCodes.MarginBelowMinimum, stillBlocked.Code);
            }
            finally { cleanup(); }
        }

        [Fact]
        public async Task PricingGuard_UnsetLimitsNeverBlock()
        {
            var (_, _, companyId, connectionString, cleanup) = await CreateAsync();
            try
            {
                SeedPricing(connectionString, "");
                GuardWith(connectionString, companyId,
                    new[] { new PriceGuardLine(1, 42, "MAT-42", 1m, 1m, 90m) }, null);
            }
            finally { cleanup(); }
        }

        [Fact]
        public async Task Pricing_BxgyFreeLineIsAddedAtOrderSave_AndMovesStockAtDelivery()
        {
            var (service, _, companyId, connectionString, cleanup) = await CreateAsync();
            try
            {
                SeedPricing(connectionString, "UPDATE materials SET bonus_buy_qty = 2, bonus_qty = 1 WHERE material_id = 42;");
                var orders = new OrderDocumentRepository("SQLite", connectionString);
                var draft = new CommercialDocumentDraft(
                    companyId, CommercialDocKinds.SalesOrder, "SO-W7A-B", new DateOnly(2026, 10, 20),
                    null, null, 1, "SAR", 1m, null,
                    new[] { new CommercialLineDraft(42, "MAT-42", "Chain test item", "PCS", 4m, 10m, 0m, 0m, 1) },
                    PartyCode: "CUST-1");
                var orderId = await orders.CreateDraftAsync(CommercialDocSide.Sales, draft, "pricing-test");
                var saved = await orders.FindByIdAsync(CommercialDocSide.Sales, orderId);
                Assert.NotNull(saved);
                Assert.Equal(2, saved!.Lines.Count);
                var free = Assert.Single(saved.Lines, l => l.UnitPrice == 0m);
                Assert.Equal(2m, free.Qty);

                await orders.PostOrderAsync(CommercialDocSide.Sales, orderId, "pricing-test");
                var deliveryId = await service.CreateFromSourceAsync(
                    orderId, "DLV-W7A", new DateOnly(2026, 10, 21), "pricing-test");
                await service.PostAsync(deliveryId, "pricing-test");

                using var verify = new SqliteConnection(connectionString);
                verify.Open();
                using var command = verify.CreateCommand();
                command.CommandText = "SELECT available_qty FROM material_stock_balances WHERE company_id = 'SC-TEST' AND material_id = 42";
                // Paid 4 + free 2 leave the warehouse; the free part earns
                // zero revenue but consumes stock and COGS like any line.
                Assert.Equal(994m, Convert.ToDecimal(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
            }
            finally { cleanup(); }
        }

        [Fact]
        public async Task PricingGuard_BlocksSalesOrderSaveBelowMinimum()
        {
            var (_, _, companyId, connectionString, cleanup) = await CreateAsync();
            try
            {
                SeedPricing(connectionString, "UPDATE materials SET price_min = 50 WHERE material_id = 42;");
                var orders = new OrderDocumentRepository("SQLite", connectionString);
                var draft = new CommercialDocumentDraft(
                    companyId, CommercialDocKinds.SalesOrder, "SO-W7A", new DateOnly(2026, 10, 20),
                    null, null, 1, "SAR", 1m, null,
                    new[] { new CommercialLineDraft(42, "MAT-42", "Chain test item", "PCS", 1m, 40m, 0m, 0m, 1) },
                    PartyCode: "CUST-1");
                var error = await Assert.ThrowsAsync<AppError>(() =>
                    orders.CreateDraftAsync(CommercialDocSide.Sales, draft, "pricing-test"));
                Assert.Equal(ErrorCodes.PriceBelowMinimum, error.Code);
            }
            finally { cleanup(); }
        }

        private static CommercialDocumentDraft Quotation(Guid companyId, string docNo, decimal qty, decimal price) => new(
            companyId,
            CommercialDocKinds.SalesQuotation,
            docNo,
            new DateOnly(2026, 10, 16),
            PartyId: 7,
            PartyName: "Chain Test Customer",
            WarehouseId: 1,
            CurrencyCode: "SAR",
            ExchangeRate: 1m,
            Notes: null,
            new[]
            {
                new CommercialLineDraft(42, "MAT-42", "Chain test item", "PCS", qty, price)
            });

        private static Task<(SalesChainService Service, ShouTech.Infrastructure.CommercialDocumentRepository Repository, Guid CompanyId, string ConnectionString, Action Cleanup)> CreateAsync()
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"shoutech-sales-chain-{Guid.NewGuid():N}.sqlite3");
            DbPath = databasePath;
            var connectionString = $"Data Source={databasePath};";
            var companyId = Guid.NewGuid();

            ApplySqliteMigrations(databasePath);

            // The delivery-note posting path uses the real stock valuation
            // engine, so this fixture must contain the same company-scoped
            // material, warehouse, and opening balance as a usable company.
            using (var seed = new SqliteConnection(connectionString))
            {
                seed.Open();
                using var command = seed.CreateCommand();
                command.CommandText = """
                    INSERT INTO companies
                        (company_id, company_code, company_name_ar, company_name_en)
                    VALUES ($companyId, 'SC-TEST', 'Sales Chain Test', 'Sales Chain Test');

                    INSERT INTO warehouses
                        (warehouse_id, company_id, warehouse_code, warehouse_name_ar, warehouse_name_en, is_active, is_deleted)
                    VALUES (1, $companyId, 1, 'Main', 'Main', 1, 0);

                    INSERT INTO materials
                        (material_id, company_id, code, name_ar, name_en, is_stocked, is_deleted)
                    VALUES (42, $companyId, 'MAT-42', 'Chain test item', 'Chain test item', 1, 0);

                    INSERT INTO material_stock_balances
                        (company_id, warehouse_id, material_id, available_qty, average_cost, last_cost)
                    VALUES ('SC-TEST', 1, 42, 1000, 40, 40);
                    """;
                command.Parameters.AddWithValue("$companyId", companyId.ToString("D"));
                command.ExecuteNonQuery();
            }

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
