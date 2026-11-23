using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Linq;
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
    /// StockValuationEngine). Also pins the purchase cycle completion:
    /// ERPNext-style purchase returns (PRT) from a posted receipt or
    /// invoice (stock-out + supplier debit + mirrored GL) and the
    /// incoming quality gate on receipts (quarantine diversion + Draft
    /// inspection).
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

        [Fact]
        public async Task ReturnFromReceipt_ReducesStock_DebitsSupplier_PostsMirroredGl()
        {
            var (service, repository, companyId, connectionString, cleanup) = await CreateAsync();
            try
            {
                var rfqId = await service.CreateRequestForQuotationAsync(
                    Request(companyId, CompanyCode, "RFQ-2101", 10m, 8m), "chain-test");
                var orderId = await service.CreateFromSourceAsync(
                    rfqId, "PO-2101", new DateOnly(2026, 10, 20), "chain-test");
                await service.PostAsync(orderId, "chain-test");
                var receiptId = await service.CreateFromSourceAsync(
                    orderId, "RCV-2101", new DateOnly(2026, 10, 21), "chain-test");
                await service.PostAsync(receiptId, "chain-test");
                Assert.Equal(10m, StockQty(connectionString, 42));
                Assert.Equal(80m, StockValue(connectionString, 42));

                var returnId = await service.CreateReturnFromSourceAsync(
                    receiptId, "PRT-2101", new DateOnly(2026, 10, 23), "chain-test",
                    quantityByMaterialId: new Dictionary<long, decimal> { [42] = 4m });
                await service.PostAsync(returnId, "chain-test");

                var returned = await repository.FindPurchaseByIdAsync(returnId);
                Assert.NotNull(returned);
                Assert.Equal(CommercialDocKinds.PurchaseReturn, returned!.DocKind);
                Assert.Equal(CommercialDocumentStatus.Posted, returned.Status);
                Assert.Equal(32m, returned.Subtotal);
                Assert.Equal(32m, returned.GrandTotal);

                var links = await repository.GetPurchaseLinksFromAsync(companyId, receiptId, CommercialDocKinds.PurchaseReturn);
                var link = Assert.Single(links);
                Assert.Equal(returnId, link.TargetDocId);

                // Stock-out side effect: 4 base units leave at the current
                // rate (average 8), so 6 units / 48.00 remain.
                Assert.Equal(6m, StockQty(connectionString, 42));
                Assert.Equal(48m, StockValue(connectionString, 42));

                // Supplier ledger: one DEBIT entry for the returned value.
                Assert.Equal((32m, 0m), LedgerAmounts(connectionString, CommercialDocKinds.PurchaseReturn, returnId));

                // GL: Dr Payable / Cr Purchase Returns, balanced.
                var entry = GlLines(connectionString, CommercialDocKinds.PurchaseReturn, returnId);
                Assert.Equal((32m, 0m), entry[GlAccounts.Payable]);
                Assert.Equal((0m, 32m), entry[GlAccounts.PurchaseReturns]);

                // The receipt cannot be returned twice: only 6 remain.
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    service.CreateReturnFromSourceAsync(
                        receiptId, "PRT-2102", new DateOnly(2026, 10, 24), "chain-test",
                        quantityByMaterialId: new Dictionary<long, decimal> { [42] = 7m }));

                // The remaining 6 units are still returnable in full.
                var restId = await service.CreateReturnFromSourceAsync(
                    receiptId, "PRT-2103", new DateOnly(2026, 10, 24), "chain-test");
                await service.PostAsync(restId, "chain-test");
                Assert.Equal(0m, StockQty(connectionString, 42));
                Assert.Equal(0m, StockValue(connectionString, 42));
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public async Task ReturnFromInvoice_DebitsSupplier_SplitsVatLeg()
        {
            var (service, repository, companyId, connectionString, cleanup) = await CreateAsync();
            try
            {
                var draft = Request(companyId, CompanyCode, "RFQ-2201", 4m, 10m);
                var taxed = draft with
                {
                    Lines = draft.Lines.Select(l => l with { TaxRate = 15m }).ToList()
                };
                var rfqId = await service.CreateRequestForQuotationAsync(taxed, "chain-test");
                var orderId = await service.CreateFromSourceAsync(
                    rfqId, "PO-2201", new DateOnly(2026, 10, 20), "chain-test");
                await service.PostAsync(orderId, "chain-test");
                var receiptId = await service.CreateFromSourceAsync(
                    orderId, "RCV-2201", new DateOnly(2026, 10, 21), "chain-test");
                await service.PostAsync(receiptId, "chain-test");
                var invoiceId = await service.CreateFromSourceAsync(
                    receiptId, "PUR-2201", new DateOnly(2026, 10, 22), "chain-test");
                await service.PostAsync(invoiceId, "chain-test");

                // The invoice credited the supplier 46.00 (40 + 15% VAT).
                Assert.Equal((0m, 46m), LedgerAmounts(connectionString, CommercialDocKinds.PurchaseInvoice, invoiceId));
                var invoiceGl = GlLines(connectionString, CommercialDocKinds.PurchaseInvoice, invoiceId);
                Assert.Equal((0m, 46m), invoiceGl[GlAccounts.Payable]);
                Assert.Equal((40m, 0m), invoiceGl[GlAccounts.Purchases]);
                Assert.Equal((6m, 0m), invoiceGl[GlAccounts.InputVat]);

                var returnId = await service.CreateReturnFromSourceAsync(
                    invoiceId, "PRT-2201", new DateOnly(2026, 10, 24), "chain-test",
                    quantityByMaterialId: new Dictionary<long, decimal> { [42] = 2m });
                await service.PostAsync(returnId, "chain-test");

                // Mirror legs: Dr Payable / Cr Purchase Returns / Cr Input VAT.
                Assert.Equal((23m, 0m), LedgerAmounts(connectionString, CommercialDocKinds.PurchaseReturn, returnId));
                var entry = GlLines(connectionString, CommercialDocKinds.PurchaseReturn, returnId);
                Assert.Equal((23m, 0m), entry[GlAccounts.Payable]);
                Assert.Equal((0m, 20m), entry[GlAccounts.PurchaseReturns]);
                Assert.Equal((0m, 3m), entry[GlAccounts.InputVat]);

                // Only the unreturned invoiced quantity (2) remains.
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    service.CreateReturnFromSourceAsync(
                        invoiceId, "PRT-2202", new DateOnly(2026, 10, 25), "chain-test",
                        quantityByMaterialId: new Dictionary<long, decimal> { [42] = 3m }));

                // Sanity: 2 of the 4 received units stay in stock.
                Assert.Equal(2m, StockQty(connectionString, 42));
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public async Task ReceiptOfQualityMaterial_DivertsToQuarantineAndCreatesIncomingInspection()
        {
            var (service, _, companyId, connectionString, cleanup) = await CreateAsync();
            try
            {
                Exec(connectionString,
                    $"UPDATE materials SET quality_check_required = 1 WHERE company_id = '{CompanyCode}' AND material_id = 42;");

                var rfqId = await service.CreateRequestForQuotationAsync(
                    Request(companyId, CompanyCode, "RFQ-2301", 10m, 8m), "chain-test");
                var orderId = await service.CreateFromSourceAsync(
                    rfqId, "PO-2301", new DateOnly(2026, 10, 20), "chain-test");
                await service.PostAsync(orderId, "chain-test");
                var receiptId = await service.CreateFromSourceAsync(
                    orderId, "RCV-2301", new DateOnly(2026, 10, 21), "chain-test");
                await service.PostAsync(receiptId, "chain-test");

                // Decision Q2: the quantity does NOT land in the requested
                // warehouse; it waits in the company's quarantine warehouse.
                Assert.Equal(0m, StockQty(connectionString, 42));
                var quarantineWarehouseId = (long)Scalar(connectionString,
                    $"SELECT warehouse_id FROM warehouses WHERE company_id = '{CompanyCode}' AND is_quarantine = 1 AND is_deleted = 0;");
                Assert.True(quarantineWarehouseId > 0);
                Assert.Equal(10m, Scalar(connectionString,
                    $"SELECT available_qty FROM material_stock_balances WHERE company_id = '{CompanyCode}' AND warehouse_id = {quarantineWarehouseId} AND material_id = 42;"));
                Assert.Equal(80m, Scalar(connectionString,
                    $"SELECT stock_value FROM material_stock_balances WHERE company_id = '{CompanyCode}' AND warehouse_id = {quarantineWarehouseId} AND material_id = 42;"));

                // A Draft Incoming inspection is created against the receipt.
                using var connection = new SqliteConnection(connectionString);
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = $"""
                    SELECT COUNT(*) FROM quality_inspections
                    WHERE company_id = '{CompanyCode}' AND inspection_type = 'Incoming'
                      AND ref_doc_kind = 'RCV' AND ref_doc_no = 'RCV-2301'
                      AND material_id = 42 AND status = 'Draft'
                      AND warehouse_id = 1 AND quarantine_warehouse_id = {quarantineWarehouseId}
                      AND total_qty = 10
                    """;
                Assert.Equal(1L, Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture));

                // Referenced so the deconstruction stays exhaustive.
                _ = companyId;
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public async Task Chain_RejectsReturnFromNonPurchaseDocuments()
        {
            var (service, _, companyId, _, cleanup) = await CreateAsync();
            try
            {
                var rfqId = await service.CreateRequestForQuotationAsync(
                    Request(companyId, CompanyCode, "RFQ-2401", 5m, 10m), "chain-test");

                // An RFQ carries neither stock nor liability: no return path.
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    service.CreateReturnFromSourceAsync(
                        rfqId, "PRT-2401", new DateOnly(2026, 10, 20), "chain-test"));
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

        private static void Exec(string connectionString, string sql)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        /// <summary>(debit, credit) of the single party_ledger row the
        /// given posted purchase document wrote for its supplier.</summary>
        private static (decimal Debit, decimal Credit) LedgerAmounts(string connectionString, string docKind, long docId)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT debit, credit FROM party_ledger WHERE doc_kind = '{docKind}' AND doc_id = {docId} AND party_kind = 'S';";
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read(), $"No party_ledger row for {docKind} {docId}.");
            var row = (Convert.ToDecimal(reader.GetValue(0), CultureInfo.InvariantCulture),
                       Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture));
            Assert.False(reader.Read(), $"More than one party_ledger row for {docKind} {docId}.");
            return row;
        }

        /// <summary>Per-account (debit, credit) of the ONE live GL entry
        /// linked to the given document (migration 060 shape).</summary>
        private static Dictionary<string, (decimal Debit, decimal Credit)> GlLines(string connectionString, string sourceDocType, long docId)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"""
                SELECT a.account_code, l.debit, l.credit
                FROM journal_entries e
                JOIN journal_entry_lines l ON l.journal_entry_id = e.journal_entry_id
                JOIN chart_of_accounts a ON a.account_id = l.account_id
                WHERE e.source_doc_type = '{sourceDocType}' AND e.source_doc_id = '{docId}'
                  AND e.reversal_of_entry_id IS NULL
                ORDER BY l.line_number
                """;
            var lines = new Dictionary<string, (decimal Debit, decimal Credit)>();
            decimal debits = 0m, credits = 0m;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var code = reader.GetString(0);
                var debit = Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture);
                var credit = Convert.ToDecimal(reader.GetValue(2), CultureInfo.InvariantCulture);
                lines[code] = (debit, credit);
                debits += debit;
                credits += credit;
            }
            Assert.Equal(debits, credits);
            return lines;
        }

        private static async Task<(BuyingChainService Service, PurchaseDocumentRepository Repository, Guid CompanyId, string ConnectionString, Action Cleanup)> CreateAsync()
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

            // The return-path tests post purchase invoices and returns to
            // GL: an open fiscal year and the postable mirror accounts
            // (Payable / Purchases / Purchase Returns / Input VAT) must
            // exist.
            var structure = new AccountingStructureRepository("SQLite", connectionString);
            await structure.CreateFiscalYearAsync(companyId, 2026, "chain-test");
            Exec(connectionString, $"""
                INSERT INTO chart_of_accounts (account_id, company_id, account_code, account_name, account_type)
                VALUES
                    ('{Guid.NewGuid():D}', '{companyId:D}', '2201', 'Payable', 'LIABILITY'),
                    ('{Guid.NewGuid():D}', '{companyId:D}', '402', 'Purchases', 'EXPENSE'),
                    ('{Guid.NewGuid():D}', '{companyId:D}', '403', 'Purchase Returns', 'EXPENSE'),
                    ('{Guid.NewGuid():D}', '{companyId:D}', '1205', 'Input VAT', 'ASSET');
                """);

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
