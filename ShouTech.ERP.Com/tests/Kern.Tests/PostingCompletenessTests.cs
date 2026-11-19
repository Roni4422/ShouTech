using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ShouTech.Application.Services;
using ShouTech.Domain;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;
using Xunit;

namespace Kern.Tests
{
    /// <summary>
    /// Phase A2 — posting completeness:
    /// 1) JournalService.UpdateDraftAsync edits drafts (header + lines,
    ///    audited) and refuses posted entries with ACC-5005.
    /// 2) Party credit limits block invoice posting with ACC-5006, both on
    ///    the sales-chain post path and on order convert-to-invoice.
    /// 3) Cost-center allocations are validated and applied at journal
    ///    posting: splits land in journal_line_cost_splits and sum exactly
    ///    to the line amounts; group centers (ACC-5008) and allocations
    ///    that do not total 100 (ACC-5007) block the post.
    /// </summary>
    public class PostingCompletenessTests
    {
        private const string Actor = "posting-test";
        private const string CompanyCode = "PC-TEST";

        // ---------------- Item 1: journal draft editing ----------------

        [Fact]
        public async Task UpdateDraft_ChangesHeaderAndLines_AndWritesAuditEvent()
        {
            var context = await CreateJournalContextAsync();
            try
            {
                var journalId = await context.Service.CreateDraftAsync(
                    context.CompanyId, new DateOnly(2026, 10, 16), "Original description", Actor,
                    new[]
                    {
                        new JournalLineRequest(context.DebitAccount, 25m, 0m),
                        new JournalLineRequest(context.CreditAccount, 0m, 25m)
                    },
                    documentNumber: "JV-PC-001");

                await context.Service.UpdateDraftAsync(
                    context.CompanyId, journalId, new DateOnly(2026, 10, 17), "Edited description", Actor,
                    new[]
                    {
                        new JournalLineRequest(context.DebitAccount, 40m, 0m),
                        new JournalLineRequest(context.CreditAccount, 0m, 40m)
                    },
                    referenceNumber: "REF-EDITED");

                var loaded = await context.Service.FindByDocumentNumberAsync(context.CompanyId, "JV-PC-001");
                Assert.NotNull(loaded);
                Assert.Equal(JournalEntryStatus.Draft, loaded!.Status);
                Assert.Equal("Edited description", loaded.Description);
                Assert.Equal("REF-EDITED", loaded.ReferenceNumber);
                Assert.Equal(new DateOnly(2026, 10, 17), loaded.EntryDate);
                Assert.Equal(2, loaded.Lines.Count);
                Assert.Equal(40m, loaded.Lines[0].Debit);
                Assert.Equal(40m, loaded.Lines[1].Credit);

                await using var connection = new SqliteConnection(context.ConnectionString);
                await connection.OpenAsync();
                Assert.Equal(1m, Scalar(connection,
                    $"SELECT COUNT(*) FROM journal_audit_events WHERE journal_entry_id = '{journalId:D}' AND event_type = 4"));
                Assert.Equal(2m, Scalar(connection,
                    $"SELECT COUNT(*) FROM journal_audit_events WHERE journal_entry_id = '{journalId:D}'"));
            }
            finally
            {
                context.Cleanup();
            }
        }

        [Fact]
        public async Task UpdateDraft_OnPostedEntry_ThrowsCodedNotEditableError()
        {
            var context = await CreateJournalContextAsync();
            try
            {
                var journalId = await context.Service.CreateDraftAsync(
                    context.CompanyId, new DateOnly(2026, 10, 16), "To be posted", Actor,
                    new[]
                    {
                        new JournalLineRequest(context.DebitAccount, 25m, 0m),
                        new JournalLineRequest(context.CreditAccount, 0m, 25m)
                    },
                    documentNumber: "JV-PC-002");
                await context.Service.PostAsync(journalId, Actor);

                var error = await Assert.ThrowsAsync<AppError>(() => context.Service.UpdateDraftAsync(
                    context.CompanyId, journalId, new DateOnly(2026, 10, 17), "Must not apply", Actor,
                    new[]
                    {
                        new JournalLineRequest(context.DebitAccount, 99m, 0m),
                        new JournalLineRequest(context.CreditAccount, 0m, 99m)
                    }));
                Assert.Equal(ErrorCodes.JournalEntryNotEditable, error.Code);

                var loaded = await context.Service.FindByDocumentNumberAsync(context.CompanyId, "JV-PC-002");
                Assert.NotNull(loaded);
                Assert.Equal(JournalEntryStatus.Posted, loaded!.Status);
                Assert.Equal("To be posted", loaded.Description);
                Assert.Equal(25m, loaded.Lines[0].Debit);
            }
            finally
            {
                context.Cleanup();
            }
        }

        [Fact]
        public async Task UpdateDraft_UnbalancedLines_ThrowsBalancedError()
        {
            var context = await CreateJournalContextAsync();
            try
            {
                var journalId = await context.Service.CreateDraftAsync(
                    context.CompanyId, new DateOnly(2026, 10, 16), "Balanced", Actor,
                    new[]
                    {
                        new JournalLineRequest(context.DebitAccount, 25m, 0m),
                        new JournalLineRequest(context.CreditAccount, 0m, 25m)
                    },
                    documentNumber: "JV-PC-003");

                var error = await Assert.ThrowsAsync<AppError>(() => context.Service.UpdateDraftAsync(
                    context.CompanyId, journalId, new DateOnly(2026, 10, 16), "Unbalanced edit", Actor,
                    new[]
                    {
                        new JournalLineRequest(context.DebitAccount, 40m, 0m),
                        new JournalLineRequest(context.CreditAccount, 0m, 30m)
                    }));
                Assert.Equal(ErrorCodes.JournalNotBalanced, error.Code);
            }
            finally
            {
                context.Cleanup();
            }
        }

        // ---------------- Item 2: credit limits at posting ----------------

        [Fact]
        public async Task ChainSalesInvoicePost_OverCreditLimit_BlocksWithCodedError()
        {
            var (service, companyId, connectionString, cleanup) = await CreateChainContextAsync();
            try
            {
                // Canonical party card for ledger party 7: limit 100 < 250 total.
                await ExecAsync(connectionString,
                    $"INSERT INTO parties (id, company_id, kind, code, name_en, credit_limit, ledger_id) VALUES ('{Guid.NewGuid():D}', '{companyId:D}', 'C', 'CUST-7', 'Chain Customer', 100, 7)");

                var invoiceId = await BuildChainInvoiceAsync(service, companyId, "PC4");

                var error = await Assert.ThrowsAsync<AppError>(() => service.PostAsync(invoiceId, Actor));
                Assert.Equal(ErrorCodes.CreditLimitExceeded, error.Code);
                Assert.Equal(3, error.Args.Count);
                Assert.Equal(100m, Convert.ToDecimal(error.Args[0], CultureInfo.InvariantCulture));
                Assert.Equal(0m, Convert.ToDecimal(error.Args[1], CultureInfo.InvariantCulture));
                Assert.Equal(250m, Convert.ToDecimal(error.Args[2], CultureInfo.InvariantCulture));

                await using var connection = new SqliteConnection(connectionString);
                await connection.OpenAsync();
                Assert.Equal(0m, Scalar(connection, "SELECT COUNT(*) FROM party_ledger"));
                Assert.Equal("Draft", Text(connection,
                    $"SELECT status FROM sales_documents WHERE doc_id = {invoiceId}"));
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public async Task ChainSalesInvoicePost_WithinCreditLimit_Posts()
        {
            var (service, companyId, connectionString, cleanup) = await CreateChainContextAsync();
            try
            {
                await ExecAsync(connectionString,
                    $"INSERT INTO parties (id, company_id, kind, code, name_en, credit_limit, ledger_id) VALUES ('{Guid.NewGuid():D}', '{companyId:D}', 'C', 'CUST-7', 'Chain Customer', 1000, 7)");

                var invoiceId = await BuildChainInvoiceAsync(service, companyId, "PC5");
                await service.PostAsync(invoiceId, Actor);

                await using var connection = new SqliteConnection(connectionString);
                await connection.OpenAsync();
                Assert.Equal(250m, Scalar(connection,
                    "SELECT debit FROM party_ledger WHERE party_kind = 'C' AND party_id = 7"));
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public async Task ConvertToInvoice_OverCreditLimit_BlocksWithCodedError()
        {
            var (service, companyId, connectionString, cleanup) = await CreateOrderContextAsync();
            try
            {
                // 031 parties card for code CUST-1: limit 100 < 250 total.
                await ExecAsync(connectionString,
                    $"INSERT INTO parties (id, company_id, kind, code, name_en, credit_limit, ledger_id) VALUES ('{Guid.NewGuid():D}', '{companyId:D}', 'C', 'CUST-1', 'Test Party', 100, 7)");

                var orderId = await service.CreateOrderAsync(OrderDraft(companyId, "SO-PC6", 10m, 25m), Actor);
                await service.PostOrderAsync(CommercialDocSide.Sales, orderId, Actor);

                var error = await Assert.ThrowsAsync<AppError>(() =>
                    service.ConvertToInvoiceAsync(CommercialDocSide.Sales, orderId, new DateOnly(2026, 11, 2), Actor));
                Assert.Equal(ErrorCodes.CreditLimitExceeded, error.Code);

                await using var connection = new SqliteConnection(connectionString);
                await connection.OpenAsync();
                Assert.Equal(0m, Scalar(connection, "SELECT COUNT(*) FROM party_ledger"));
                Assert.Equal(0m, Scalar(connection,
                    $"SELECT COUNT(*) FROM sales_documents WHERE doc_kind = '{CommercialDocKinds.SalesInvoice}'"));
            }
            finally
            {
                cleanup();
            }
        }

        // ---------------- Item 3: cost centers at posting ----------------

        [Fact]
        public async Task Post_WithAllocation_WritesSplitsSummingToLineAmounts()
        {
            var context = await CreateJournalContextAsync();
            try
            {
                await SeedCostCentersAsync(context.ConnectionString, context.CompanyId);

                var allocatedId = await context.Service.CreateDraftAsync(
                    context.CompanyId, new DateOnly(2026, 10, 16), "Allocated entry", Actor,
                    new[]
                    {
                        new JournalLineRequest(context.DebitAccount, 100m, 0m, null, "CC-MAIN"),
                        new JournalLineRequest(context.CreditAccount, 0m, 100m)
                    },
                    documentNumber: "JV-PC-010");
                await context.Service.PostAsync(allocatedId, Actor);

                var directId = await context.Service.CreateDraftAsync(
                    context.CompanyId, new DateOnly(2026, 10, 16), "Direct leaf entry", Actor,
                    new[]
                    {
                        new JournalLineRequest(context.DebitAccount, 50m, 0m, null, "CC-A"),
                        new JournalLineRequest(context.CreditAccount, 0m, 50m)
                    },
                    documentNumber: "JV-PC-011");
                await context.Service.PostAsync(directId, Actor);

                await using var connection = new SqliteConnection(context.ConnectionString);
                await connection.OpenAsync();

                // Allocation 60/40 across CC-A / CC-B for the 100 debit line.
                Assert.Equal(2m, Scalar(connection,
                    $"SELECT COUNT(*) FROM journal_line_cost_splits WHERE journal_entry_id = '{allocatedId:D}'"));
                Assert.Equal(60m, Scalar(connection,
                    $"SELECT debit_amount FROM journal_line_cost_splits WHERE journal_entry_id = '{allocatedId:D}' AND cost_center_code = 'CC-A'"));
                Assert.Equal(40m, Scalar(connection,
                    $"SELECT debit_amount FROM journal_line_cost_splits WHERE journal_entry_id = '{allocatedId:D}' AND cost_center_code = 'CC-B'"));
                Assert.Equal(100m, Scalar(connection,
                    $"SELECT SUM(debit_amount) FROM journal_line_cost_splits WHERE journal_entry_id = '{allocatedId:D}'"));

                // No allocation on CC-A itself: one 100% self-split.
                Assert.Equal(1m, Scalar(connection,
                    $"SELECT COUNT(*) FROM journal_line_cost_splits WHERE journal_entry_id = '{directId:D}'"));
                Assert.Equal(50m, Scalar(connection,
                    $"SELECT debit_amount FROM journal_line_cost_splits WHERE journal_entry_id = '{directId:D}' AND cost_center_code = 'CC-A'"));
                Assert.Equal(100m, Scalar(connection,
                    $"SELECT percentage FROM journal_line_cost_splits WHERE journal_entry_id = '{directId:D}'"));
            }
            finally
            {
                context.Cleanup();
            }
        }

        [Fact]
        public async Task Post_WithGroupCostCenter_BlocksWithCodedError()
        {
            var context = await CreateJournalContextAsync();
            try
            {
                await SeedCostCentersAsync(context.ConnectionString, context.CompanyId);

                var journalId = await context.Service.CreateDraftAsync(
                    context.CompanyId, new DateOnly(2026, 10, 16), "Group center entry", Actor,
                    new[]
                    {
                        new JournalLineRequest(context.DebitAccount, 100m, 0m, null, "CC-GRP"),
                        new JournalLineRequest(context.CreditAccount, 0m, 100m)
                    },
                    documentNumber: "JV-PC-012");

                var error = await Assert.ThrowsAsync<AppError>(() => context.Service.PostAsync(journalId, Actor));
                Assert.Equal(ErrorCodes.CostCenterNotLeaf, error.Code);

                var loaded = await context.Service.FindByDocumentNumberAsync(context.CompanyId, "JV-PC-012");
                Assert.Equal(JournalEntryStatus.Draft, loaded!.Status);

                await using var connection = new SqliteConnection(context.ConnectionString);
                await connection.OpenAsync();
                Assert.Equal(0m, Scalar(connection,
                    $"SELECT COUNT(*) FROM journal_line_cost_splits WHERE journal_entry_id = '{journalId:D}'"));
            }
            finally
            {
                context.Cleanup();
            }
        }

        [Fact]
        public async Task Post_WithAllocationNotSummingTo100_BlocksWithCodedError()
        {
            var context = await CreateJournalContextAsync();
            try
            {
                await SeedCostCentersAsync(context.ConnectionString, context.CompanyId);

                var journalId = await context.Service.CreateDraftAsync(
                    context.CompanyId, new DateOnly(2026, 10, 16), "Bad allocation entry", Actor,
                    new[]
                    {
                        new JournalLineRequest(context.DebitAccount, 100m, 0m, null, "CC-BAD"),
                        new JournalLineRequest(context.CreditAccount, 0m, 100m)
                    },
                    documentNumber: "JV-PC-013");

                var error = await Assert.ThrowsAsync<AppError>(() => context.Service.PostAsync(journalId, Actor));
                Assert.Equal(ErrorCodes.CostCenterAllocationInvalid, error.Code);

                var loaded = await context.Service.FindByDocumentNumberAsync(context.CompanyId, "JV-PC-013");
                Assert.Equal(JournalEntryStatus.Draft, loaded!.Status);
            }
            finally
            {
                context.Cleanup();
            }
        }

        // ---------------- fixtures ----------------

        private sealed record JournalContext(
            JournalService Service,
            Guid CompanyId,
            Guid DebitAccount,
            Guid CreditAccount,
            string ConnectionString,
            Action Cleanup);

        private static async Task<JournalContext> CreateJournalContextAsync()
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"shoutech-posting-{Guid.NewGuid():N}.sqlite3");
            var connectionString = $"Data Source={databasePath};";
            var companyId = Guid.NewGuid();
            Action cleanup = () => CleanupFiles(databasePath);

            try
            {
                ApplySqliteMigrations(databasePath);

                await ExecAsync(connectionString,
                    $"INSERT INTO companies (company_id, company_code, company_name_ar, company_name_en) VALUES ('{companyId:D}', '{CompanyCode}', 'Posting Test Company', 'Posting Test Company')");

                var structure = new ShouTech.Infrastructure.AccountingStructureRepository("SQLite", connectionString);
                await structure.CreateFiscalYearAsync(companyId, 2026, Actor);
                var debitAccount = await structure.CreateAccountAsync(companyId, "1101", "Cash", "ASSET");
                var creditAccount = await structure.CreateAccountAsync(companyId, "4101", "Sales", "REVENUE");

                var repository = new ShouTech.Infrastructure.JournalRepository("SQLite", connectionString);
                return new JournalContext(new JournalService(repository), companyId, debitAccount, creditAccount, connectionString, cleanup);
            }
            catch
            {
                cleanup();
                throw;
            }
        }

        private static Task<(SalesChainService Service, Guid CompanyId, string ConnectionString, Action Cleanup)> CreateChainContextAsync()
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"shoutech-posting-chain-{Guid.NewGuid():N}.sqlite3");
            var connectionString = $"Data Source={databasePath};";
            var companyId = Guid.NewGuid();
            ApplySqliteMigrations(databasePath);
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using var seed = connection.CreateCommand();
                seed.CommandText = """
                    INSERT INTO companies (company_id, company_code, company_name_ar, company_name_en)
                    VALUES ($companyId, $companyCode, 'Posting test company', 'Posting test company');

                    INSERT INTO warehouses
                        (warehouse_id, company_id, warehouse_code, warehouse_name_ar, warehouse_name_en, is_active, is_deleted)
                    VALUES (1, $companyCode, 1, 'Main', 'Main', 1, 0);

                    INSERT INTO materials
                        (material_id, company_id, code, name_ar, name_en, is_stocked, is_deleted, allow_negative)
                    VALUES (42, $companyCode, 'MAT-42', 'Posting test item', 'Posting test item', 1, 0, 0);

                    INSERT INTO material_stock_balances
                        (company_id, warehouse_id, material_id, available_qty, average_cost, last_cost)
                    VALUES ($companyCode, 1, 42, 1000, 40, 40);

                    INSERT INTO chart_of_accounts
                        (account_id, company_id, account_code, account_name, account_type, is_active, is_deleted, is_postable)
                    VALUES ($accountId, $companyId, '1201', 'Accounts Receivable', 'ASSET', 1, 0, 1);

                    INSERT INTO chart_of_accounts
                        (account_id, company_id, account_code, account_name, account_type, is_active, is_deleted, is_postable)
                    VALUES
                        ($salesAccountId, $companyId, '301', 'Sales Revenue', 'REVENUE', 1, 0, 1),
                        ($inventoryAccountId, $companyId, '1203', 'Inventory', 'ASSET', 1, 0, 1),
                        ($cogsAccountId, $companyId, '401', 'Cost of Goods Sold', 'EXPENSE', 1, 0, 1);
                    """;
                seed.Parameters.AddWithValue("$accountId", Guid.NewGuid().ToString("D"));
                seed.Parameters.AddWithValue("$salesAccountId", Guid.NewGuid().ToString("D"));
                seed.Parameters.AddWithValue("$inventoryAccountId", Guid.NewGuid().ToString("D"));
                seed.Parameters.AddWithValue("$cogsAccountId", Guid.NewGuid().ToString("D"));
                seed.Parameters.AddWithValue("$companyId", companyId.ToString("D"));
                seed.Parameters.AddWithValue("$companyCode", companyId.ToString("D"));
                seed.ExecuteNonQuery();

                // The chain's invoice posting now writes a real GL entry.
                // Keep every test document date in an explicitly open period.
                using var fiscal = connection.CreateCommand();
                fiscal.CommandText = """
                    INSERT INTO fiscal_years
                        (company_id, fiscal_year, database_name, start_date, end_date, is_current, is_closed)
                    VALUES ($company, 2026, '', '2026-01-01', '2026-12-31', 1, 0);
                    SELECT last_insert_rowid();
                    """;
                fiscal.Parameters.AddWithValue("$company", companyId.ToString("D"));
                var yearId = Convert.ToInt64(fiscal.ExecuteScalar(), CultureInfo.InvariantCulture);

                for (var month = 1; month <= 12; month++)
                {
                    using var period = connection.CreateCommand();
                    period.CommandText = """
                        INSERT INTO fiscal_periods
                            (fiscal_period_id, company_id, fiscal_year_id, period_number,
                             period_name, start_date, end_date, status)
                        VALUES ($id, $company, $year, $number, $name, $start, $end, 'OPEN');
                        """;
                    var start = new DateTime(2026, month, 1);
                    period.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
                    period.Parameters.AddWithValue("$company", companyId.ToString("D"));
                    period.Parameters.AddWithValue("$year", yearId);
                    period.Parameters.AddWithValue("$number", month);
                    period.Parameters.AddWithValue("$name", $"2026-{month:D2}");
                    period.Parameters.AddWithValue("$start", start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    period.Parameters.AddWithValue("$end", start.AddMonths(1).AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    period.ExecuteNonQuery();
                }
            }
            var repository = new ShouTech.Infrastructure.CommercialDocumentRepository("SQLite", connectionString);
            return Task.FromResult<(SalesChainService Service, Guid CompanyId, string ConnectionString, Action Cleanup)>((new SalesChainService(repository), companyId, connectionString, () => CleanupFiles(databasePath)));
        }

        private static Task<(OrderService Service, Guid CompanyId, string ConnectionString, Action Cleanup)> CreateOrderContextAsync()
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"shoutech-posting-orders-{Guid.NewGuid():N}.sqlite3");
            var connectionString = $"Data Source={databasePath};";
            var companyId = Guid.NewGuid();
            ApplySqliteMigrations(databasePath);
            var orders = new ShouTech.Infrastructure.OrderDocumentRepository("SQLite", connectionString);
            var chain = new ShouTech.Infrastructure.CommercialDocumentRepository("SQLite", connectionString);
            return Task.FromResult<(OrderService Service, Guid CompanyId, string ConnectionString, Action Cleanup)>((new OrderService(orders, chain), companyId, connectionString, () => CleanupFiles(databasePath)));
        }

        private static async Task<long> BuildChainInvoiceAsync(SalesChainService service, Guid companyId, string tag)
        {
            var quotationId = await service.CreateQuotationAsync(ChainDraft(companyId, $"QT-{tag}"), Actor);
            var orderId = await service.CreateFromSourceAsync(quotationId, $"SO-{tag}", new DateOnly(2026, 10, 20), Actor);
            await service.PostAsync(orderId, Actor);
            var deliveryId = await service.CreateFromSourceAsync(orderId, $"DLV-{tag}", new DateOnly(2026, 10, 21), Actor);
            await service.PostAsync(deliveryId, Actor);
            return await service.CreateFromSourceAsync(deliveryId, $"SAL-{tag}", new DateOnly(2026, 10, 22), Actor);
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

        private static CommercialDocumentDraft OrderDraft(Guid companyId, string docNo, decimal qty, decimal price) => new(
            companyId,
            CommercialDocKinds.SalesOrder,
            docNo,
            new DateOnly(2026, 10, 20),
            7,
            "Test Party",
            null,
            "SAR",
            1m,
            null,
            new[] { new CommercialLineDraft(42, "MAT-42", "Order test item", "PCS", qty, price) },
            "CUST-1",
            null,
            null);

        private static async Task SeedCostCentersAsync(string connectionString, Guid companyId)
        {
            // Cost-center master tables key the company by CODE (031 design);
            // journal lines carry the center code as text.
            var mainId = Guid.NewGuid();
            var aId = Guid.NewGuid();
            var bId = Guid.NewGuid();
            var grpId = Guid.NewGuid();
            var badId = Guid.NewGuid();
            var allocationId = Guid.NewGuid();
            var badAllocationId = Guid.NewGuid();

            await ExecAsync(connectionString, $"""
                INSERT INTO cost_centers (id, company_id, code, name_en, is_group, is_active) VALUES
                    ('{mainId:D}', '{CompanyCode}', 'CC-MAIN', 'Main Center', 0, 1),
                    ('{aId:D}', '{CompanyCode}', 'CC-A', 'Center A', 0, 1),
                    ('{bId:D}', '{CompanyCode}', 'CC-B', 'Center B', 0, 1),
                    ('{grpId:D}', '{CompanyCode}', 'CC-GRP', 'Group Center', 1, 1),
                    ('{badId:D}', '{CompanyCode}', 'CC-BAD', 'Bad Allocation Center', 0, 1);
                INSERT INTO cost_center_allocations (id, company_id, main_cost_center_id, valid_from, is_active) VALUES
                    ('{allocationId:D}', '{CompanyCode}', '{mainId:D}', '2026-01-01', 1),
                    ('{badAllocationId:D}', '{CompanyCode}', '{badId:D}', '2026-01-01', 1);
                INSERT INTO cost_center_allocation_lines (id, allocation_id, cost_center_id, percentage) VALUES
                    ('{Guid.NewGuid():D}', '{allocationId:D}', '{aId:D}', 60),
                    ('{Guid.NewGuid():D}', '{allocationId:D}', '{bId:D}', 40),
                    ('{Guid.NewGuid():D}', '{badAllocationId:D}', '{aId:D}', 60),
                    ('{Guid.NewGuid():D}', '{badAllocationId:D}', '{bId:D}', 30);
                """);
        }

        private static async Task ExecAsync(string connectionString, string sql)
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
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
            return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture)!;
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

        private static void CleanupFiles(string databasePath)
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath))
                File.Delete(databasePath);
            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                var sidecar = databasePath + suffix;
                if (File.Exists(sidecar))
                    File.Delete(sidecar);
            }
        }
    }
}
