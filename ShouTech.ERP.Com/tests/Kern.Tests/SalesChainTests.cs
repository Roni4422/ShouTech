using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ShouTech.Application.Services;
using ShouTech.Domain.Entities;
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
                    quantityByMaterialId: new Dictionary<long, decimal> { [42] = 4m });
                await service.PostAsync(firstDelivery, "chain-test");

                var fulfilled = await repository.GetFulfilledQuantitiesAsync(companyId, orderId, CommercialDocKinds.DeliveryNote);
                Assert.Equal(4m, fulfilled[42]);

                var secondDelivery = await service.CreateFromSourceAsync(
                    orderId, "DLV-3002", new DateOnly(2026, 10, 22), "chain-test");
                var delivery = await repository.FindSalesByIdAsync(secondDelivery);
                Assert.NotNull(delivery);
                Assert.Equal(6m, delivery!.Lines[0].Qty);

                await service.PostAsync(secondDelivery, "chain-test");
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    service.CreateFromSourceAsync(orderId, "DLV-3003", new DateOnly(2026, 10, 23), "chain-test"));
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
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    service.CreateFromSourceAsync(draftId, "SO-9999", new DateOnly(2026, 10, 20), "chain-test"));
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
                    quantityByMaterialId: new Dictionary<long, decimal> { [42] = 3m });
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
        public async Task DeliveryFromOrder_AllocatesRepeatedMaterialQuantitiesAcrossSourceLines()
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

                var firstDeliveryId = await service.CreateFromSourceAsync(
                    orderId, "DLV-DUP-MAT-1", new DateOnly(2026, 10, 21), "chain-test",
                    quantityByMaterialId: new Dictionary<long, decimal> { [42] = 5m });
                var firstDelivery = await repository.FindSalesByIdAsync(firstDeliveryId);
                Assert.NotNull(firstDelivery);
                Assert.Equal(new[] { 4m, 1m }, firstDelivery!.Lines.Select(line => line.Qty));

                await service.PostAsync(firstDeliveryId, "chain-test");
                var secondDeliveryId = await service.CreateFromSourceAsync(
                    orderId, "DLV-DUP-MAT-2", new DateOnly(2026, 10, 22), "chain-test");
                var secondDelivery = await repository.FindSalesByIdAsync(secondDeliveryId);
                Assert.NotNull(secondDelivery);
                var remainingLine = Assert.Single(secondDelivery!.Lines);
                Assert.Equal(5m, remainingLine.Qty);
                Assert.Equal("Second line", remainingLine.Description);
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

                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    service.CreateFromSourceAsync(orderId, "DLV-3005", new DateOnly(2026, 10, 21), "chain-test",
                        quantityByMaterialId: new Dictionary<long, decimal> { [42] = 11m }));
            }
            finally
            {
                cleanup();
            }
        }

        private static string? DbPath;

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
                    VALUES ($companyId, 1, 42, 1000, 40, 40);
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
