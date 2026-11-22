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
    /// Order engine tests (ribbon completion, RS-5/RS-6): order drafts persist
    /// with scheduling fields, posting is a status flip with no side effects,
    /// convert-to-invoice copies only the remaining unbilled quantities and
    /// writes the party ledger + document link atomically (Step 9: plus the
    /// stock movement through the valuation engine and the COGS GL legs —
    /// proven in OrderChainStep9Tests), progress is
    /// computed from document_links, and expired quotations cannot convert.
    /// </summary>
    public class OrderServiceTests
    {
        [Fact]
        public async Task SalesOrder_SaveReloadPost_PersistsSchedulingFields()
        {
            var (service, orders, companyId, _, cleanup) = await CreateAsync();
            try
            {
                var docId = await service.CreateOrderAsync(
                    Order(companyId, CommercialDocKinds.SalesOrder, "SO-9001", 10m, 25m,
                        delivery: new DateOnly(2026, 11, 1)), "order-test");

                var saved = await orders.FindByIdAsync(CommercialDocSide.Sales, docId);
                Assert.NotNull(saved);
                Assert.Equal(CommercialDocumentStatus.Draft, saved!.Status);
                Assert.Equal(new DateOnly(2026, 11, 1), saved.DeliveryDate);
                Assert.Equal(250m, saved.GrandTotal);
                Assert.Equal("CUST-1", saved.PartyCode);

                await service.PostOrderAsync(CommercialDocSide.Sales, docId, "order-test");
                var posted = await orders.FindByIdAsync(CommercialDocSide.Sales, docId);
                Assert.Equal(CommercialDocumentStatus.Posted, posted!.Status);
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public async Task UpdateDraft_RejectsPostedOrder()
        {
            var (service, _, companyId, _, cleanup) = await CreateAsync();
            try
            {
                var docId = await service.CreateOrderAsync(
                    Order(companyId, CommercialDocKinds.SalesOrder, "SO-9002", 5m, 10m), "order-test");
                await service.PostOrderAsync(CommercialDocSide.Sales, docId, "order-test");

                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    service.UpdateOrderAsync(docId,
                        Order(companyId, CommercialDocKinds.SalesOrder, "SO-9002", 6m, 10m), "order-test"));
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public async Task ConvertToInvoice_BillsRemainingOnce_AndWritesLedgerAndLink()
        {
            var (service, orders, companyId, connectionString, cleanup) = await CreateAsync();
            try
            {
                var docId = await service.CreateOrderAsync(
                    Order(companyId, CommercialDocKinds.SalesOrder, "SO-9003", 10m, 25m), "order-test");
                await service.PostOrderAsync(CommercialDocSide.Sales, docId, "order-test");

                var (invoiceId, invoiceDocNo) = await service.ConvertToInvoiceAsync(
                    CommercialDocSide.Sales, docId, new DateOnly(2026, 11, 2), "order-test");
                Assert.StartsWith("SAL-", invoiceDocNo);

                var invoice = await orders.FindByIdAsync(CommercialDocSide.Sales, invoiceId);
                Assert.NotNull(invoice);
                Assert.Equal(CommercialDocKinds.SalesInvoice, invoice!.DocKind);
                Assert.Equal(CommercialDocumentStatus.Posted, invoice.Status);
                Assert.Equal(10m, invoice.Lines[0].Qty);
                Assert.Equal(250m, invoice.GrandTotal);

                var progress = await service.GetProgressAsync(CommercialDocSide.Sales, docId);
                Assert.Equal(100m, progress.PerBilled);
                Assert.Equal(0m, progress.PerFulfilled);

                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    service.ConvertToInvoiceAsync(CommercialDocSide.Sales, docId, new DateOnly(2026, 11, 3), "order-test"));

                await using var connection = new SqliteConnection(connectionString);
                await connection.OpenAsync();
                await using var ledger = connection.CreateCommand();
                ledger.CommandText = "SELECT COUNT(*), COALESCE(SUM(debit),0), COALESCE(SUM(credit),0) FROM party_ledger WHERE doc_id = $doc AND party_kind = 'C'";
                ledger.Parameters.AddWithValue("$doc", invoiceId);
                await using var reader = await ledger.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(1L, reader.GetInt64(0));
                Assert.Equal(250m, reader.GetDecimal(1));
                Assert.Equal(0m, reader.GetDecimal(2));
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public async Task PurchaseOrder_ConvertToInvoice_CreditsSupplierLedger()
        {
            var (service, orders, companyId, connectionString, cleanup) = await CreateAsync();
            try
            {
                var docId = await service.CreateOrderAsync(
                    Order(companyId, CommercialDocKinds.PurchaseOrder, "PO-9001", 4m, 50m, partyId: 9, partyCode: "SUP-1"), "order-test");
                await service.PostOrderAsync(CommercialDocSide.Purchase, docId, "order-test");
                await service.CreatePurchaseReceiptAsync(
                    companyId, docId, new DateOnly(2026, 11, 1),
                    new[] { new CommercialLineDraft(42, "MAT-42", "Order test item", "PCS", 4m, 50m) },
                    "order-test");

                var (invoiceId, _) = await service.CreatePurchaseInvoiceAsync(
                    companyId, docId, new DateOnly(2026, 11, 2),
                    new[] { new CommercialLineDraft(42, "MAT-42", "Order test item", "PCS", 4m, 50m) },
                    "order-test");
                var invoice = await orders.FindByIdAsync(CommercialDocSide.Purchase, invoiceId);
                Assert.Equal(CommercialDocKinds.PurchaseInvoice, invoice!.DocKind);
                Assert.Equal(200m, invoice.GrandTotal);

                await using var connection = new SqliteConnection(connectionString);
                await connection.OpenAsync();
                await using var ledger = connection.CreateCommand();
                ledger.CommandText = "SELECT COALESCE(SUM(credit),0) FROM party_ledger WHERE doc_id = $doc AND party_kind = 'S'";
                ledger.Parameters.AddWithValue("$doc", invoiceId);
                Assert.Equal(200m, Convert.ToDecimal(await ledger.ExecuteScalarAsync()));
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public async Task PostedInvoice_CannotBeCancelledDirectly()
        {
            var (service, orders, companyId, _, cleanup) = await CreateAsync();
            try
            {
                var orderId = await service.CreateOrderAsync(
                    Order(companyId, CommercialDocKinds.SalesOrder, "SO-9010", 2m, 25m), "order-test");
                await service.PostOrderAsync(CommercialDocSide.Sales, orderId, "order-test");
                var (invoiceId, _) = await service.ConvertToInvoiceAsync(
                    CommercialDocSide.Sales, orderId, new DateOnly(2026, 11, 2), "order-test");

                var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    orders.SetStatusAsync(CommercialDocSide.Sales, invoiceId,
                        CommercialDocumentStatus.Cancelled, "order-test"));
                Assert.Contains("return or reverse", error.Message.ToLowerInvariant());

                var invoice = await orders.FindByIdAsync(CommercialDocSide.Sales, invoiceId);
                Assert.Equal(CommercialDocumentStatus.Posted, invoice!.Status);
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public async Task CloseAndCancel_EnforceTransitions()
        {
            var (service, orders, companyId, _, cleanup) = await CreateAsync();
            try
            {
                var docId = await service.CreateOrderAsync(
                    Order(companyId, CommercialDocKinds.SalesOrder, "SO-9004", 2m, 10m), "order-test");
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    service.CloseOrderAsync(CommercialDocSide.Sales, docId, "order-test"));

                await service.PostOrderAsync(CommercialDocSide.Sales, docId, "order-test");
                await service.CloseOrderAsync(CommercialDocSide.Sales, docId, "order-test");
                var closed = await orders.FindByIdAsync(CommercialDocSide.Sales, docId);
                Assert.Equal(CommercialDocumentStatus.Closed, closed!.Status);

                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    service.ConvertToInvoiceAsync(CommercialDocSide.Sales, docId, new DateOnly(2026, 11, 2), "order-test"));
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public async Task NextDocNo_IncrementsPerKind()
        {
            var (service, _, companyId, _, cleanup) = await CreateAsync();
            try
            {
                var first = await service.NextDocNoAsync(CommercialDocSide.Sales, companyId, CommercialDocKinds.SalesOrder);
                Assert.Equal("SO-1001", first);
                await service.CreateOrderAsync(Order(companyId, CommercialDocKinds.SalesOrder, first, 1m, 1m), "order-test");
                var second = await service.NextDocNoAsync(CommercialDocSide.Sales, companyId, CommercialDocKinds.SalesOrder);
                Assert.Equal("SO-1002", second);
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public async Task ExpiredQuotation_ConversionBlocked()
        {
            var (service, _, companyId, _, cleanup) = await CreateAsync();
            try
            {
                // Create + post a quotation whose validity already lapsed.
                var repo = new CommercialDocumentRepository("SQLite", LastConnectionString);
                var quotationId = await repo.CreateSalesDraftAsync(
                    Order(companyId, CommercialDocKinds.SalesQuotation, "QUO-9001", 3m, 10m,
                        validTill: DateOnly.FromDateTime(DateTime.Today).AddDays(-1)), "order-test");
                await repo.PostSalesAsync(quotationId, "order-test");

                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    service.ConvertQuotationToOrderAsync(quotationId, DateOnly.FromDateTime(DateTime.Today), "order-test"));
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public async Task DeliveryBeforeOrderDate_Rejected()
        {
            var (service, _, companyId, _, cleanup) = await CreateAsync();
            try
            {
                var draft = Order(companyId, CommercialDocKinds.SalesOrder, "SO-9005", 1m, 1m,
                    delivery: new DateOnly(2026, 1, 1));
                await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateOrderAsync(draft, "order-test"));
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public async Task Rfq_SupplierQuotationComparison_AndPurchaseOrderConversion_PreserveDocumentLinks()
        {
            var (service, orders, companyId, _, cleanup) = await CreateAsync();
            try
            {
                var rfq = new CommercialDocumentDraft(
                    companyId,
                    CommercialDocKinds.RequestForQuotation,
                    "RFQ-9001",
                    new DateOnly(2026, 10, 20),
                    null,
                    null,
                    null,
                    "SAR",
                    1m,
                    "Request for supplier pricing",
                    new[]
                    {
                        new CommercialLineDraft(42, "MAT-42", "Order test item", "PCS", 5m, 0m)
                    });
                var rfqId = await service.CreateOrderAsync(rfq, "order-test");
                await service.PostOrderAsync(CommercialDocSide.Purchase, rfqId, "order-test");

                var (quotationId, quotationNo) = await service.CreateSupplierQuotationFromRfqAsync(
                    rfqId,
                    supplierId: 91,
                    supplierCode: "SUP-91",
                    supplierName: "Supplier 91",
                    supplierQuotationNumber: "SUP-Q-91",
                    quotationDate: new DateOnly(2026, 10, 21),
                    validTill: new DateOnly(2026, 11, 20),
                    currencyCode: "SAR",
                    exchangeRate: 1m,
                    quotedLines: new[]
                    {
                        new CommercialLineDraft(
                            42, "MAT-42", "Order test item", "PCS", 3m, 11m,
                            SourceLineNo: 1)
                    },
                    userId: "order-test");

                Assert.StartsWith("SQ-", quotationNo);

                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    service.CreateSupplierQuotationFromRfqAsync(
                        rfqId,
                        supplierId: 92,
                        supplierCode: "SUP-92",
                        supplierName: "Supplier 92",
                        supplierQuotationNumber: "SUP-Q-92",
                        quotationDate: new DateOnly(2026, 10, 21),
                        validTill: new DateOnly(2026, 11, 20),
                        currencyCode: "SAR",
                        exchangeRate: 1m,
                        quotedLines: new[]
                        {
                            new CommercialLineDraft(
                                42, "MAT-42", "Order test item", "PCS", 6m, 10m,
                                SourceLineNo: 1)
                        },
                        userId: "order-test"));
                await service.PostOrderAsync(CommercialDocSide.Purchase, quotationId, "order-test");

                var offers = await service.CompareSupplierQuotationsAsync(rfqId);
                var offer = Assert.Single(offers);
                Assert.Equal(quotationId, offer.DocId);
                Assert.Equal("Supplier 91", offer.SupplierName);
                Assert.Equal(33m, offer.GrandTotal);

                var (purchaseOrderId, purchaseOrderNo) =
                    await service.CreatePurchaseOrderFromSupplierQuotationAsync(
                        quotationId, new DateOnly(2026, 10, 22), "order-test");
                Assert.StartsWith("PO-", purchaseOrderNo);

                var purchaseOrder = await orders.FindByIdAsync(
                    CommercialDocSide.Purchase, purchaseOrderId);
                Assert.NotNull(purchaseOrder);
                Assert.Equal(CommercialDocKinds.PurchaseOrder, purchaseOrder!.DocKind);
                Assert.Equal(CommercialDocumentStatus.Draft, purchaseOrder.Status);
                Assert.Equal(3m, Assert.Single(purchaseOrder.Lines).Qty);
                Assert.Equal(33m, purchaseOrder.GrandTotal);

                var rfqLinks = await orders.GetLinksFromAsync(companyId, rfqId);
                var rfqResponse = Assert.Single(rfqLinks);
                Assert.Equal(CommercialDocKinds.SupplierQuotation, rfqResponse.TargetDocKind);
                Assert.Equal(quotationId, rfqResponse.TargetDocId);

                var quotationLinks = await orders.GetLinksFromAsync(companyId, quotationId);
                var poLink = Assert.Single(quotationLinks);
                Assert.Equal(CommercialDocKinds.PurchaseOrder, poLink.TargetDocKind);
                Assert.Equal(purchaseOrderId, poLink.TargetDocId);

                // The repository must enforce the one-active-PO invariant
                // inside its transaction, even when callers bypass the
                // service's earlier duplicate check.
                var duplicateDraft = new CommercialDocumentDraft(
                    companyId,
                    CommercialDocKinds.PurchaseOrder,
                    "PO-DUPLICATE",
                    new DateOnly(2026, 10, 22),
                    91,
                    "Supplier 91",
                    null,
                    "SAR",
                    1m,
                    "Duplicate conversion must be rejected",
                    new[]
                    {
                        new CommercialLineDraft(42, "MAT-42", "Order test item", "PCS", 5m, 11m)
                    },
                    "SUP-91");
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    orders.CreateLinkedDraftAsync(
                        CommercialDocSide.Purchase,
                        duplicateDraft,
                        CommercialDocKinds.SupplierQuotation,
                        quotationId,
                        "order-test"));
            }
            finally
            {
                cleanup();
            }
        }

        // ------------------------------------------------------------ harness

        private static string LastConnectionString = string.Empty;

        private static CommercialDocumentDraft Order(
            Guid companyId, string kind, string docNo, decimal qty, decimal price,
            long partyId = 7, string partyCode = "CUST-1", DateOnly? delivery = null, DateOnly? validTill = null)
            => new(
                companyId,
                kind,
                docNo,
                new DateOnly(2026, 10, 20),
                partyId,
                "Test Party",
                null,
                "SAR",
                1m,
                null,
                new[] { new CommercialLineDraft(42, "MAT-42", "Order test item", "PCS", qty, price) },
                partyCode,
                delivery,
                validTill);

        private static Task<(OrderService Service, OrderDocumentRepository Orders, Guid CompanyId, string ConnectionString, Action Cleanup)> CreateAsync()
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"shoutech-orders-{Guid.NewGuid():N}.sqlite3");
            var connectionString = $"Data Source={databasePath};";
            LastConnectionString = connectionString;
            var companyId = Guid.NewGuid();

            ApplySqliteMigrations(databasePath);
            SeedGlCompany(connectionString, companyId, "ORDER-GL");
            SeedStock(connectionString, "ORDER-GL");

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


        /// <summary>Step 9: order conversion now moves stock through the
        /// valuation engine (company-CODE keyed, like StkPost), so the
        /// fixture carries the order tests' material (42) as a stocked
        /// card, one active warehouse, and an opening balance of
        /// 1,000 @ 10.00 in that warehouse.</summary>
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
            Exec("INSERT INTO materials (material_id, company_id, code, name_ar, name_en, is_stocked, is_service, cost_method, allow_negative, is_deleted) VALUES (42, $code, 'MAT-42', 'Order test item', 'Order test item', 1, 0, 'WeightedAvg', 0, 0);",
                ("$code", companyCode));
            Exec("INSERT INTO material_stock_balances (company_id, warehouse_id, material_id, available_qty, reserved_qty, average_cost, last_cost, stock_value, is_deleted) VALUES ($code, 1, 42, 1000, 0, 10, 10, 10000, 0);",
                ("$code", companyCode));
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
