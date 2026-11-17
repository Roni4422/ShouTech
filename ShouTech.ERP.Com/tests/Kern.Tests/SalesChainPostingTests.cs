using System;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ShouTech.Application.Services;
using ShouTech.Domain.Entities;
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
                Assert.Equal(-10m, Scalar(connection,
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
                // unit PCS is its second unit with equivalent 0.25, so
                // delivering 10 PCS must deduct exactly 2.5 BOX.
                await using (var seed = new SqliteConnection(connectionString))
                {
                    await seed.OpenAsync();
                    await using var insert = seed.CreateCommand();
                    insert.CommandText = """
                        INSERT INTO materials
                          (material_id, company_id, code, name_ar, name_en,
                           unit1, unit1_factor, unit2, unit2_factor, units_json)
                        VALUES
                          (42, $company, 'MAT-42', 'Posting test item', 'Posting test item',
                           'BOX', 1, 'PCS', 0.25,
                           '[{"UnitName":"BOX","Factor":1},{"UnitName":"PCS","Factor":0.25}]');
                        """;
                    var companyParam = insert.CreateParameter();
                    companyParam.ParameterName = "$company";
                    companyParam.Value = companyId.ToString();
                    insert.Parameters.Add(companyParam);
                    await insert.ExecuteNonQueryAsync();
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
                Assert.Equal(-2.5m, Scalar(connection,
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
                // Material 42 is a SERVICE material (non-stock): delivering
                // it must not move stock, even though the line carries qty.
                await using (var seed = new SqliteConnection(connectionString))
                {
                    await seed.OpenAsync();
                    await using var insert = seed.CreateCommand();
                    insert.CommandText = """
                        INSERT INTO materials
                          (material_id, company_id, code, name_ar, name_en,
                           is_stocked, is_service, item_type, unit1, unit1_factor)
                        VALUES
                          (42, $company, 'MAT-42', 'Posting test item', 'Posting test item',
                           0, 1, 'Service', 'PCS', 1);
                        """;
                    var companyParam = insert.CreateParameter();
                    companyParam.ParameterName = "$company";
                    companyParam.Value = companyId.ToString();
                    insert.Parameters.Add(companyParam);
                    await insert.ExecuteNonQueryAsync();
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
                Assert.Equal(0, (int)Scalar(connection,
                    "SELECT COUNT(*) FROM material_stock_balances WHERE material_id = 42"));
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

    internal sealed class ChainTests
    {
        public static Task<(SalesChainService Service, ShouTech.Infrastructure.CommercialDocumentRepository Repository, Guid CompanyId, string ConnectionString, Action Cleanup)> CreateAsync()
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"shoutech-sales-posting-{Guid.NewGuid():N}.sqlite3");
            var connectionString = $"Data Source={databasePath};";
            var companyId = Guid.NewGuid();

            ApplySqliteMigrations(databasePath);
            SeedGlCompany(connectionString, companyId, "CHAIN-GL");

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
