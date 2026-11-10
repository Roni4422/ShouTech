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

                var third = await service.CreateFromSourceAsync(
                    orderId, "DLV-3003", new DateOnly(2026, 10, 23), "chain-test");
                await service.PostAsync(third, "chain-test");
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    service.CreateFromSourceAsync(orderId, "DLV-3004", new DateOnly(2026, 10, 24), "chain-test"));
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public async Task Chain_RejectsInvalidTransitionsAndDraftSources()
        {
            var (service, _, companyId, _, cleanup) = await CreateAsync();
            try
            {
                var quotationId = await service.CreateQuotationAsync(
                    Quotation(companyId, "QT-1003", 5m, 10m), "chain-test");

                // QUO -> SAL is not an allowed transition (must go through SO and DLV).
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    service.CreateFromSourceAsync(quotationId, "SAL-9999", new DateOnly(2026, 10, 20), "chain-test"));

                // Draft sources cannot continue the chain.
                var repository = new ShouTech.Infrastructure.CommercialDocumentRepository("SQLite", DbPath!);
                var draftId = await repository.CreateSalesDraftAsync(Quotation(companyId, "QT-1004", 5m, 10m), "chain-test");
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    service.CreateFromSourceAsync(draftId, "SO-9999", new DateOnly(2026, 10, 20), "chain-test"));
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

        private static async Task<(SalesChainService Service, ShouTech.Infrastructure.CommercialDocumentRepository Repository, Guid CompanyId, string ConnectionString, Action Cleanup)> CreateAsync()
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"shoutech-sales-chain-{Guid.NewGuid():N}.sqlite3");
            DbPath = databasePath;
            var connectionString = $"Data Source={databasePath};";
            var companyId = Guid.NewGuid();

            ApplySqliteMigrations(databasePath);

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
            return (service, repository, companyId, connectionString, cleanup);
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
