using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ShouTech.Application.Services;
using ShouTech.Domain.Entities;
using ShouTech.Infrastructure;
using Xunit;

namespace Kern.Tests
{
    /// <summary>
    /// Buying-chain tests (VIBE modern spec, milestone M1): RFQ -> PO ->
    /// Purchase Receipt -> Purchase Invoice, partial fulfillment,
    /// transition guards, chain-link recording, and the receipt's
    /// stock-in side effect (quantity AND value through the ONE
    /// StockValuationEngine).
    /// </summary>
    public class BuyingChainTests
    {
        private const string CompanyCode = "B77";

        [Fact]
        public async Task OrderFromRfq_CopiesTotalsAndRecordsLink()
        {
            var (service, repository, companyId, _, cleanup) = await CreateAsync();
            try
            {
                var rfqId = await service.CreateRequestForQuotationAsync(
                    Request(companyId, CompanyCode, "RFQ-1001", 10m, 8m), "chain-test");

                var orderId = await service.CreateFromSourceAsync(
                    rfqId, "PO-2001", new DateOnly(2026, 10, 20), "chain-test");
                await service.PostAsync(orderId, "chain-test");

                var order = await repository.FindPurchaseByIdAsync(orderId);
                Assert.NotNull(order);
                Assert.Equal(CommercialDocKinds.PurchaseOrder, order!.DocKind);
                Assert.Equal("PO-2001", order.DocNo);
                Assert.Equal(CommercialDocumentStatus.Posted, order.Status);
                Assert.Equal(80m, order.Subtotal);
                Assert.Equal(80m, order.GrandTotal);

                var links = await repository.GetPurchaseLinksFromAsync(companyId, rfqId);
                var link = Assert.Single(links);
                Assert.Equal(CommercialDocKinds.RequestForQuotation, link.SourceDocKind);
                Assert.Equal(CommercialDocKinds.PurchaseOrder, link.TargetDocKind);
                Assert.Equal(orderId, link.TargetDocId);
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public async Task ReceiptFromOrder_SupportsPartialQuantityThenRemaining_AndReceivesStock()
        {
            var (service, repository, companyId, connectionString, cleanup) = await CreateAsync();
            try
            {
                var rfqId = await service.CreateRequestForQuotationAsync(
                    Request(companyId, CompanyCode, "RFQ-1002", 10m, 8m), "chain-test");
                var orderId = await service.CreateFromSourceAsync(
                    rfqId, "PO-2002", new DateOnly(2026, 10, 20), "chain-test");
                await service.PostAsync(orderId, "chain-test");

                var firstReceipt = await service.CreateFromSourceAsync(
                    orderId, "RCV-3001", new DateOnly(2026, 10, 21), "chain-test",
                    quantityByMaterialId: new Dictionary<long, decimal> { [42] = 4m });
                await service.PostAsync(firstReceipt, "chain-test");

                var fulfilled = await repository.GetPurchaseFulfilledQuantitiesAsync(companyId, orderId, CommercialDocKinds.PurchaseReceipt);
                Assert.Equal(4m, fulfilled[42]);

                // Stock-in side effect (engine): 4 base units on hand.
                Assert.Equal(4m, StockQty(connectionString, 42));

                var secondReceiptId = await service.CreateFromSourceAsync(
                    orderId, "RCV-3002", new DateOnly(2026, 10, 22), "chain-test");
                var second = await repository.FindPurchaseByIdAsync(secondReceiptId);
                Assert.NotNull(second);
                Assert.Equal(6m, second!.Lines[0].Qty);

                await service.PostAsync(secondReceiptId, "chain-test");
                Assert.Equal(10m, StockQty(connectionString, 42));
                Assert.Equal(80m, StockValue(connectionString, 42));

                await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
                    service.CreateFromSourceAsync(orderId, "RCV-3003", new DateOnly(2026, 10, 23), "chain-test"));
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
                var rfqId = await service.CreateRequestForQuotationAsync(
                    Request(companyId, CompanyCode, "RFQ-1003", 5m, 10m), "chain-test");

                // An RFQ can only create a purchase order, never a receipt or invoice directly.
                var orderId = await service.CreateFromSourceAsync(
                    rfqId, "PO-1003", new DateOnly(2026, 10, 20), "chain-test");
                await service.PostAsync(orderId, "chain-test");

                var order = await repository.FindPurchaseByIdAsync(orderId);
                Assert.NotNull(order);
                Assert.Equal(CommercialDocKinds.PurchaseOrder, order!.DocKind);

                // Draft sources cannot continue the chain.
                var draftId = await repository.CreatePurchaseDraftAsync(
                    Request(companyId, CompanyCode, "RFQ-1004", 5m, 10m), "chain-test");
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    service.CreateFromSourceAsync(draftId, "PO-9999", new DateOnly(2026, 10, 20), "chain-test"));
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
                var rfqId = await service.CreateRequestForQuotationAsync(
                    Request(companyId, CompanyCode, "RFQ-1005", 10m, 8m), "chain-test");
                var orderId = await service.CreateFromSourceAsync(
                    rfqId, "PO-2005", new DateOnly(2026, 10, 20), "chain-test");
                await service.PostAsync(orderId, "chain-test");

                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    await service.CreateFromSourceAsync(orderId, "RCV-3005", new DateOnly(2026, 10, 21), "chain-test",
                        quantityByMaterialId: new Dictionary<long, decimal> { [42] = 11m }));
            }
            finally
            {
                cleanup();
            }
        }

        // ---------------- Helpers ----------------

        private static CommercialDocumentDraft Request(Guid companyId, string code, string docNo, decimal qty, decimal price) => new(
            companyId,
            CommercialDocKinds.RequestForQuotation,
            docNo,
            new DateOnly(2026, 10, 16),
            PartyId: 9,
            PartyName: "Chain Test Supplier",
            WarehouseId: 1,
            CurrencyCode: "SAR",
            ExchangeRate: 1m,
            Notes: null,
            new[]
            {
                new CommercialLineDraft(42, "MAT-42", "Chain test item", "PCS", qty, price)
            });

        private static decimal Scalar(string connectionString, string sql)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToDecimal(cmd.ExecuteScalar() ?? 0m, CultureInfo.InvariantCulture);
        }

        private static decimal StockQty(string connectionString, long materialId)
            => Scalar(connectionString, $"SELECT available_qty FROM material_stock_balances WHERE company_id = '{CompanyCode}' AND warehouse_id = 1 AND material_id = {materialId};");

        private static decimal StockValue(string connectionString, long materialId)
            => Scalar(connectionString, $"SELECT stock_value FROM material_stock_balances WHERE company_id = '{CompanyCode}' AND warehouse_id = 1 AND material_id = {materialId};");

        private static Task<(BuyingChainService Service, PurchaseDocumentRepository Repository, Guid CompanyId, string ConnectionString, Action Cleanup)> CreateAsync()
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"shoutech-buying-chain-{Guid.NewGuid():N}.sqlite3");
            var connectionString = $"Data Source={databasePath};";
            var companyId = Guid.NewGuid();

            ApplySqliteMigrations(databasePath);

            // Master data for the stock side: the company (code-keyed stock
            // world), one warehouse, and the chain material card.
            using (var seed = new SqliteConnection(connectionString))
            {
                seed.Open();
                void Exec(string sql)
                {
                    using var cmd = seed.CreateCommand();
                    cmd.CommandText = sql;
                    cmd.ExecuteNonQuery();
                }
                Exec($"INSERT INTO companies (company_id, company_code, company_name_ar, company_name_en) VALUES ('{companyId:D}', '{CompanyCode}', 'Buying Chain Test Co', 'Buying Chain Test Co');");
                Exec($"INSERT INTO warehouses (warehouse_id, company_id, warehouse_code, warehouse_name_en, is_deleted) VALUES (1, '{CompanyCode}', 1, 'Main', 0);");
                Exec($"""
                    INSERT INTO materials (material_id, company_id, code, name_ar, name_en,
                        is_stocked, is_service, cost_method, allow_negative, is_deleted,
                        has_batch, has_serial, has_expiry, shelf_life_days, alert_before_expiry_days,
                        removal_strategy)
                    VALUES (42, '{CompanyCode}', 'MAT-42', 'Item', 'Item',
                        1, 0, 'WeightedAvg', 1, 0,
                        0, 0, 0, 0, 0, 'FIFO');
                    """);
            }

            var repository = new PurchaseDocumentRepository("SQLite", connectionString);
            var service = new BuyingChainService(repository);
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
            return Task.FromResult((service, repository, companyId, connectionString, cleanup));
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
