using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ShouTech.Application.Services;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;
using ShouTech.Infrastructure;
using Xunit;

namespace Kern.Tests;

/// <summary>
/// Chunk B (master cards) coverage: service-layer validation with a fake
/// repository (following Phase3ServiceTests), plus real SQLite repository
/// tests against a temp-file database whose schema comes from migration 031
/// and the core journal/party-ledger migrations' table shapes.
/// </summary>
public class MasterCardServiceTests
{
    private const string Company = "MC-TEST";
    private const string User = "tester";

    // ------------------------------------------------------------ Cost centers

    [Fact]
    public async Task CostCenter_RoundTrip_TreeLevels_AndDuplicateGuard()
    {
        await using var db = await TestDb.CreateAsync();
        var service = db.Service;

        var root = new CostCenter { Code = "CC-100", NameEn = "Operations", IsGroup = true };
        await service.SaveCostCenterAsync(Company, root, User);
        Assert.Equal(1, root.Level);

        var child = new CostCenter { Code = "CC-110", NameEn = "Factory", ParentId = root.Id };
        await service.SaveCostCenterAsync(Company, child, User);
        Assert.Equal(2, child.Level);

        var loaded = await service.GetCostCenterByCodeAsync(Company, "CC-110");
        Assert.NotNull(loaded);
        Assert.Equal(root.Id, loaded!.ParentId);

        // A non-group center cannot be a parent.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveCostCenterAsync(Company, new CostCenter { Code = "CC-111", NameEn = "Sub", ParentId = child.Id }, User));
        // Duplicate code in the same company is refused.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveCostCenterAsync(Company, new CostCenter { Code = "CC-110", NameEn = "Copy" }, User));
        // The same code in another company is fine.
        await service.SaveCostCenterAsync("OTHER", new CostCenter { Code = "CC-110", NameEn = "Elsewhere" }, User);

        var all = await service.GetCostCentersAsync(Company);
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task CostCenter_Balance_CountsPostedOnly_AndDeleteGuardBlocksReferenced()
    {
        await using var db = await TestDb.CreateAsync();
        var service = db.Service;
        await service.SaveCostCenterAsync(Company, new CostCenter { Code = "CC-200", NameEn = "Sales" }, User);

        await db.InsertJournalAsync(status: 1, costCenter: "CC-200", debit: 500m, credit: 0m);   // posted
        await db.InsertJournalAsync(status: 1, costCenter: "CC-200", debit: 0m, credit: 125m);   // posted
        await db.InsertJournalAsync(status: 0, costCenter: "CC-200", debit: 999m, credit: 0m);   // draft: ignored

        var balance = await service.GetCostCenterBalanceAsync(Company, "CC-200");
        Assert.Equal(500m, balance.Debit);
        Assert.Equal(125m, balance.Credit);
        Assert.Equal(375m, balance.Balance);

        var center = await service.GetCostCenterByCodeAsync(Company, "CC-200");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DeleteCostCenterAsync(Company, center!.Id, User));
    }

    [Fact]
    public async Task CostCenter_Delete_Unreferenced_SoftDeletes()
    {
        await using var db = await TestDb.CreateAsync();
        var service = db.Service;
        var center = new CostCenter { Code = "CC-300", NameEn = "Temp" };
        await service.SaveCostCenterAsync(Company, center, User);
        await service.DeleteCostCenterAsync(Company, center.Id, User);
        Assert.Null(await service.GetCostCenterByCodeAsync(Company, "CC-300"));
    }

    [Fact]
    public async Task Allocation_RequiresTotal100_AndPersistsLines()
    {
        await using var db = await TestDb.CreateAsync();
        var service = db.Service;
        var main = new CostCenter { Code = "CC-900", NameEn = "Shared", IsGroup = true };
        var a = new CostCenter { Code = "CC-910", NameEn = "A" };
        var b = new CostCenter { Code = "CC-920", NameEn = "B" };
        await service.SaveCostCenterAsync(Company, main, User);
        await service.SaveCostCenterAsync(Company, a, User);
        await service.SaveCostCenterAsync(Company, b, User);

        var bad = new CostCenterAllocation
        {
            MainCostCenterId = main.Id,
            ValidFrom = new DateOnly(2026, 1, 1),
            Lines = new List<CostCenterAllocationLine>
            {
                new() { CostCenterId = a.Id, Percentage = 60m },
                new() { CostCenterId = b.Id, Percentage = 30m }
            }
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAllocationAsync(Company, bad, User));

        bad.Lines[1].Percentage = 40m;
        await service.SaveAllocationAsync(Company, bad, User);
        var saved = Assert.Single(await service.GetAllocationsAsync(Company));
        Assert.Equal(2, saved.Lines.Count);
        Assert.Equal(100m, saved.Lines.Sum(l => l.Percentage));
    }

    // -------------------------------------------------------------- Currencies

    [Fact]
    public async Task Currency_Rates_AddListDesc_UpsertSameDate_AndBaseRule()
    {
        await using var db = await TestDb.CreateAsync();
        var service = db.Service;
        await service.SaveCurrencyAsync(Company, new Currency { Code = "usd", NameEn = "US Dollar", Symbol = "$" }, User);

        await service.AddRateAsync(Company, new CurrencyRate { CurrencyCode = "USD", RateDate = new DateOnly(2026, 1, 1), Rate = 3.75m }, User, "SAR");
        await service.AddRateAsync(Company, new CurrencyRate { CurrencyCode = "USD", RateDate = new DateOnly(2026, 2, 1), Rate = 3.80m, MinRate = 3.70m, MaxRate = 3.90m }, User, "SAR");
        // Same date re-save replaces the row instead of duplicating it.
        await service.AddRateAsync(Company, new CurrencyRate { CurrencyCode = "USD", RateDate = new DateOnly(2026, 2, 1), Rate = 3.82m, MinRate = 3.70m, MaxRate = 3.90m }, User, "SAR");

        var rates = await service.GetRatesAsync(Company, "USD");
        Assert.Equal(2, rates.Count);
        Assert.Equal(new DateOnly(2026, 2, 1), rates[0].RateDate);
        Assert.Equal(3.82m, rates[0].Rate);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AddRateAsync(Company, new CurrencyRate { CurrencyCode = "USD", RateDate = new DateOnly(2026, 3, 1), Rate = 0m }, User, "SAR"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AddRateAsync(Company, new CurrencyRate { CurrencyCode = "USD", RateDate = new DateOnly(2026, 3, 1), Rate = 4.50m, MinRate = 3.70m, MaxRate = 3.90m }, User, "SAR"));
        // Base currency must be exactly 1.
        await service.SaveCurrencyAsync(Company, new Currency { Code = "SAR", NameEn = "Saudi Riyal" }, User);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AddRateAsync(Company, new CurrencyRate { CurrencyCode = "SAR", RateDate = new DateOnly(2026, 1, 1), Rate = 1.01m }, User, "SAR"));
        await service.AddRateAsync(Company, new CurrencyRate { CurrencyCode = "SAR", RateDate = new DateOnly(2026, 1, 1), Rate = 1m }, User, "SAR");
        // Rate for a currency that does not exist is refused by the repository.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AddRateAsync(Company, new CurrencyRate { CurrencyCode = "EUR", RateDate = new DateOnly(2026, 1, 1), Rate = 4m }, User, "SAR"));
        // Invalid currency codes are refused by the service.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveCurrencyAsync(Company, new Currency { Code = "US", NameEn = "Bad" }, User));
    }

    // ------------------------------------------------------------------ Banks

    [Fact]
    public async Task Bank_Save_Load_AndBalanceFromPostedGlLines()
    {
        await using var db = await TestDb.CreateAsync();
        var service = db.Service;
        var bank = new Bank { Code = "BNK-01", NameEn = "Main Bank", CurrencyCode = "SAR", GlAccountCode = "1120", Iban = "SA0380000000608010167519" };
        await service.SaveBankAsync(Company, bank, User);

        await db.InsertAccountAsync("1120");
        await db.InsertJournalAsync(status: 1, accountCode: "1120", debit: 1000m, credit: 0m);
        await db.InsertJournalAsync(status: 1, accountCode: "1120", debit: 0m, credit: 250m);
        await db.InsertJournalAsync(status: 0, accountCode: "1120", debit: 5000m, credit: 0m); // draft ignored

        var loaded = await service.GetBankByCodeAsync(Company, "BNK-01");
        Assert.NotNull(loaded);
        Assert.Equal("SA0380000000608010167519", loaded!.Iban);

        var balance = await service.GetBankBalanceAsync(Company, "1120");
        Assert.Equal(750m, balance.Balance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveBankAsync(Company, new Bank { Code = "BNK-02", NameEn = "Bad IBAN", Iban = "XX" }, User));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveBankAsync(Company, new Bank { Code = "BNK-01", NameEn = "Duplicate" }, User));
    }

    // ---------------------------------------------------------------- Parties

    [Fact]
    public async Task Party_Save_BalanceFromLedger_AndDeleteGuard()
    {
        await using var db = await TestDb.CreateAsync();
        var service = db.Service;

        var group = new PartyGroup { Kind = "C", Code = "GRP-1", NameEn = "Retail" };
        await service.SavePartyGroupAsync(Company, group, User);
        var party = new Party
        {
            Kind = "C", Code = "CUS-1", NameEn = "Acme Trading", PartyGroupId = group.Id,
            CreditLimit = 10000m, CreditDays = 45, PriceTier = "Wholesale"
        };
        await service.SavePartyAsync(Company, party, User);

        // Step 4: the balance resolves through the party's OWN ledger_id
        // (assigned by the repository on save) — ledger rows of a stranger
        // who happens to share the numeric space must not leak in.
        var saved = await service.GetPartyByCodeAsync(Company, "C", "CUS-1");
        Assert.NotNull(saved);
        Assert.True(saved!.LedgerId > 0);
        await db.InsertLedgerAsync("C", saved.LedgerId, debit: 700m, credit: 0m);
        await db.InsertLedgerAsync("C", saved.LedgerId, debit: 0m, credit: 200m);
        await db.InsertLedgerAsync("S", saved.LedgerId, debit: 0m, credit: 999m); // other kind ignored
        await db.InsertLedgerAsync("C", saved.LedgerId + 1000, debit: 777m, credit: 0m); // stranger ignored

        var balance = await service.GetPartyBalanceAsync(Company, "C", "CUS-1");
        Assert.Equal(700m, balance.Debit);
        Assert.Equal(200m, balance.Credit);
        Assert.Equal(500m, balance.Balance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DeletePartyAsync(Company, party.Id, "C", "CUS-1", User));

        // Search finds the party by name fragment and by phone after update.
        party.Phone = "0551234567";
        await service.SavePartyAsync(Company, party, User);
        var byName = await service.SearchPartiesAsync(Company, "C", "acme");
        Assert.Single(byName);
        var byPhone = await service.SearchPartiesAsync(Company, null, "0551");
        Assert.Single(byPhone);
        var suppliersOnly = await service.SearchPartiesAsync(Company, "S", "acme");
        Assert.Empty(suppliersOnly);

        // Validation: bad kind, negative credit, bad email.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.SavePartyAsync(Company, new Party { Kind = "X", Code = "P-9", NameEn = "Bad" }, User));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SavePartyAsync(Company, new Party { Kind = "S", Code = "SUP-1", NameEn = "Bad", CreditLimit = -1m }, User));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SavePartyAsync(Company, new Party { Kind = "S", Code = "SUP-1", NameEn = "Bad", Email = "not-an-email" }, User));
    }

    [Fact]
    public async Task Party_Delete_Unreferenced_Succeeds()
    {
        await using var db = await TestDb.CreateAsync();
        var service = db.Service;
        var party = new Party { Kind = "S", Code = "SUP-9", NameEn = "Supplier Nine" };
        await service.SavePartyAsync(Company, party, User);
        await service.DeletePartyAsync(Company, party.Id, "S", "SUP-9", User);
        Assert.Null(await service.GetPartyByCodeAsync(Company, "S", "SUP-9"));
    }

    // -------------------------------------------------------------- Sales reps

    [Fact]
    public async Task SalesRep_Save_Load_Validation()
    {
        await using var db = await TestDb.CreateAsync();
        var service = db.Service;
        var group = new SalesRepGroup { Code = "RG-1", NameEn = "Field Team" };
        await service.SaveSalesRepGroupAsync(Company, group, User);

        var rep = new SalesRep { Code = "REP-01", NameEn = "Omar", Phone = "0500", CommissionPct = 2.5m, TargetAmount = 100000m, GroupId = group.Id };
        await service.SaveSalesRepAsync(Company, rep, User);

        var loaded = await service.GetSalesRepByCodeAsync(Company, "REP-01");
        Assert.NotNull(loaded);
        Assert.Equal(2.5m, loaded!.CommissionPct);
        Assert.Equal(group.Id, loaded.GroupId);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveSalesRepAsync(Company, new SalesRep { Code = "REP-02", NameEn = "Bad", CommissionPct = 101m }, User));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveSalesRepAsync(Company, new SalesRep { Code = "REP-02", NameEn = "Bad", GroupId = Guid.NewGuid() }, User));

        await service.DeleteSalesRepAsync(Company, rep.Id, User);
        Assert.Null(await service.GetSalesRepByCodeAsync(Company, "REP-01"));
    }

    // ------------------------------------------------------- Service-only fakes

    [Fact]
    public async Task Service_RejectsInvalidCostCenterBeforeRepository()
    {
        var repo = new FakeMasterCardRepository();
        var service = new MasterCardService(repo);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveCostCenterAsync(Company, new CostCenter { Code = " ", NameEn = "X" }, User));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveCostCenterAsync(Company, new CostCenter { Code = "X", NameEn = " " }, User));
        Assert.Equal(0, repo.SaveCostCenterCalls);
    }

    // ------------------------------------------------------------------ TestDb

    // ------------------------------------------------------------ W1 (mig 095)

    [Fact]
    public async Task Party_HeaderAdditions_RoundTrip()
    {
        await using var db = await TestDb.CreateAsync();
        var service = db.Service;
        var party = new Party
        {
            Kind = PartyKinds.Customer, Code = "C-900", NameEn = "Risky Customer",
            DefaultCurrency = "USD", RiskClass = PartyRiskClasses.High,
            IsBlacklisted = true, BlacklistReason = "Unpaid judgments",
            BlacklistedAt = "2026-01-02 03:04:05", BlacklistedBy = User
        };
        await service.SavePartyAsync(Company, party, User);
        var loaded = await service.GetPartyByCodeAsync(Company, "C", "C-900");
        Assert.NotNull(loaded);
        Assert.Equal("USD", loaded!.DefaultCurrency);
        Assert.Equal(PartyRiskClasses.High, loaded.RiskClass);
        Assert.True(loaded.IsBlacklisted);
        Assert.Equal("Unpaid judgments", loaded.BlacklistReason);
        Assert.Equal(User, loaded.BlacklistedBy);
        Assert.Null(loaded.OpeningBalanceAmount);
    }

    [Fact]
    public async Task Party_BlacklistWithoutReason_IsRejected()
    {
        await using var db = await TestDb.CreateAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            db.Service.SavePartyAsync(Company, new Party
            {
                Kind = PartyKinds.Customer, Code = "C-901", NameEn = "No Reason",
                IsBlacklisted = true
            }, User));
    }

    [Fact]
    public async Task PartyContactsAddressesBranches_RoundTrip_AndPrimaryMirror()
    {
        await using var db = await TestDb.CreateAsync();
        var service = db.Service;
        var party = new Party { Kind = PartyKinds.Customer, Code = "C-910", NameEn = "Structured Customer" };
        await service.SavePartyAsync(Company, party, User);

        var contact = new PartyContact
        {
            PartyId = party.Id, Name = "Sara Buyer", Role = "Purchasing",
            Phone = "111", Mobile = "222", Email = "sara@example.com", IsPrimary = true
        };
        await service.SavePartyContactAsync(Company, contact, User);
        var second = new PartyContact { PartyId = party.Id, Name = "Omar Ops", Phone = "333" };
        await service.SavePartyContactAsync(Company, second, User);

        var address = new PartyAddress
        {
            PartyId = party.Id, Kind = PartyAddressKinds.Delivery,
            City = "Riyadh", Country = "SA", PostalCode = "12345",
            Address = "King Fahd Rd 10", IsDefault = true
        };
        await service.SavePartyAddressAsync(Company, address, User);

        var branch = new PartyBranch { PartyId = party.Id, Name = "Jeddah Branch", Address = "Corniche", Phone = "444" };
        await service.SavePartyBranchAsync(Company, branch, User);

        Assert.Equal(2, (await service.GetPartyContactsAsync(Company, party.Id)).Count);
        Assert.Single(await service.GetPartyAddressesAsync(Company, party.Id));
        Assert.Single(await service.GetPartyBranchesAsync(Company, party.Id));

        // Primary contact / default address mirror onto the party summary.
        var loaded = await service.GetPartyByCodeAsync(Company, "C", "C-910");
        Assert.NotNull(loaded);
        Assert.Equal("111", loaded!.Phone);
        Assert.Equal("222", loaded.Mobile);
        Assert.Equal("sara@example.com", loaded.Email);
        Assert.Equal("King Fahd Rd 10", loaded.Address);

        // A second primary replaces the first as THE primary.
        second.IsPrimary = true;
        await service.SavePartyContactAsync(Company, second, User);
        var contacts = await service.GetPartyContactsAsync(Company, party.Id);
        Assert.Single(contacts, c => c.IsPrimary);
        Assert.Equal("Omar Ops", contacts.Single(c => c.IsPrimary).Name);

        // Soft delete removes the row from reads.
        await service.DeletePartyBranchAsync(Company, branch.Id, User);
        Assert.Empty(await service.GetPartyBranchesAsync(Company, party.Id));
    }

    private sealed class TestDb : IAsyncDisposable
    {
        private readonly string _path;
        public string ConnectionString { get; }
        public MasterCardService Service { get; }

        private TestDb(string path)
        {
            _path = path;
            ConnectionString = $"Data Source={path};";
            Service = new MasterCardService(new MasterCardRepository("SQLite", ConnectionString));
        }

        public static async Task<TestDb> CreateAsync()
        {
            var path = Path.Combine(Path.GetTempPath(), $"shoutech-mastercards-{Guid.NewGuid():N}.sqlite3");
            var db = new TestDb(path);
            await db.ExecuteAsync(ReadMigration031());
            // Migration 061 adds the canonical party ledger identity.
            await db.ExecuteAsync("ALTER TABLE parties ADD COLUMN ledger_id INTEGER;");
            // W1 (migration 095): stubs for the tables 095 touches that
            // the light fixture does not otherwise build.
            await db.ExecuteAsync("""
                CREATE TABLE permissions (
                    permission_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    permission_code TEXT UNIQUE, permission_name TEXT, group_code TEXT,
                    feature_code TEXT, operation_code TEXT, name_ar TEXT, name_en TEXT,
                    description TEXT, display_order INTEGER, is_sensitive INTEGER, is_active INTEGER DEFAULT 1
                );
                CREATE TABLE roles (role_id INTEGER PRIMARY KEY AUTOINCREMENT, role_code TEXT, is_active INTEGER DEFAULT 1);
                CREATE TABLE role_permissions (role_id INTEGER, permission_id INTEGER);
                CREATE TABLE sales_documents (doc_id INTEGER PRIMARY KEY AUTOINCREMENT, company_id TEXT);
                """);
            await db.ExecuteAsync(ReadMigration095());
            // Core ledger shapes (from migrations 001/009/010) needed by the
            // balance and delete-guard queries.
            await db.ExecuteAsync("""
                CREATE TABLE journal_entries (
                    journal_entry_id TEXT NOT NULL PRIMARY KEY,
                    company_id TEXT NOT NULL,
                    entry_date TEXT NOT NULL,
                    description TEXT NOT NULL,
                    status INTEGER NOT NULL DEFAULT 0
                );
                CREATE TABLE journal_entry_lines (
                    journal_entry_line_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    journal_entry_id TEXT NOT NULL,
                    line_number INTEGER NOT NULL,
                    account_id TEXT NOT NULL,
                    debit NUMERIC NOT NULL DEFAULT 0,
                    credit NUMERIC NOT NULL DEFAULT 0,
                    cost_center TEXT,
                    note TEXT
                );
                CREATE TABLE chart_of_accounts (
                    account_id TEXT NOT NULL PRIMARY KEY,
                    company_id TEXT NOT NULL,
                    account_code TEXT NOT NULL,
                    account_name TEXT NOT NULL
                );
                CREATE TABLE party_ledger (
                    ledger_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    company_id TEXT NOT NULL,
                    party_kind TEXT NOT NULL CHECK (party_kind IN ('C','S')),
                    party_id INTEGER NOT NULL,
                    doc_kind TEXT,
                    doc_id INTEGER,
                    doc_no TEXT,
                    entry_date TEXT NOT NULL,
                    debit NUMERIC NOT NULL DEFAULT 0,
                    credit NUMERIC NOT NULL DEFAULT 0,
                    currency_code TEXT NOT NULL DEFAULT 'SAR',
                    note TEXT,
                    created_at TEXT NOT NULL DEFAULT (datetime('now'))
                );
                CREATE TABLE customers (
                    customer_id INTEGER PRIMARY KEY,
                    company_id TEXT NOT NULL,
                    customer_code TEXT NOT NULL,
                    is_deleted INTEGER NOT NULL DEFAULT 0
                );
                CREATE TABLE suppliers (
                    supplier_id INTEGER PRIMARY KEY,
                    company_id TEXT NOT NULL,
                    supplier_code TEXT NOT NULL,
                    is_deleted INTEGER NOT NULL DEFAULT 0
                );
                """);
            return db;
        }

        private static string ReadMigration095()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "dba", "mig")))
                root = root.Parent;
            Assert.NotNull(root);
            var path = Path.Combine(root!.FullName, "dba", "mig", "sqlite", "095_party_structure.sql");
            Assert.True(File.Exists(path), $"Migration 095 not found: {path}");
            return File.ReadAllText(path);
        }

        private static string ReadMigration031()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "dba", "mig")))
                root = root.Parent;
            Assert.NotNull(root);
            var path = Path.Combine(root!.FullName, "dba", "mig", "sqlite", "031_master_cards.sql");
            Assert.True(File.Exists(path), $"Migration 031 not found: {path}");
            return File.ReadAllText(path);
        }

        public async Task InsertJournalAsync(int status, string? costCenter = null, string? accountCode = null, decimal debit = 0m, decimal credit = 0m)
        {
            var entryId = Guid.NewGuid().ToString("D");
            var accountId = accountCode is null ? Guid.NewGuid().ToString("D") : await ResolveAccountIdAsync(accountCode);
            await ExecuteAsync(
                "INSERT INTO journal_entries (journal_entry_id, company_id, entry_date, description, status) VALUES ($id, $company, '2026-01-01', 'test', $status);",
                ("$id", entryId), ("$company", Company), ("$status", status));
            await ExecuteAsync(
                "INSERT INTO journal_entry_lines (journal_entry_id, line_number, account_id, debit, credit, cost_center) VALUES ($id, 1, $account, $debit, $credit, $cc);",
                ("$id", entryId), ("$account", accountId), ("$debit", debit), ("$credit", credit), ("$cc", (object?)costCenter ?? DBNull.Value));
        }

        public async Task InsertAccountAsync(string accountCode)
        {
            await ExecuteAsync(
                "INSERT INTO chart_of_accounts (account_id, company_id, account_code, account_name) VALUES ($id, $company, $code, $name);",
                ("$id", Guid.NewGuid().ToString("D")), ("$company", Company), ("$code", accountCode), ("$name", "Account " + accountCode));
        }

        private async Task<string> ResolveAccountIdAsync(string accountCode)
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT account_id FROM chart_of_accounts WHERE company_id = $company AND account_code = $code;";
            command.Parameters.AddWithValue("$company", Company);
            command.Parameters.AddWithValue("$code", accountCode);
            var value = await command.ExecuteScalarAsync();
            Assert.NotNull(value);
            return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!;
        }

        public async Task InsertLedgerAsync(string kind, long partyId, decimal debit, decimal credit)
        {
            await ExecuteAsync(
                "INSERT INTO party_ledger (company_id, party_kind, party_id, entry_date, debit, credit) VALUES ($company, $kind, $party, '2026-01-01', $debit, $credit);",
                ("$company", Company), ("$kind", kind), ("$party", partyId), ("$debit", debit), ("$credit", credit));
        }

        private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
                command.Parameters.AddWithValue(name, value);
            await command.ExecuteNonQueryAsync();
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(_path))
                File.Delete(_path);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeMasterCardRepository : IMasterCardRepository
    {
        public int SaveCostCenterCalls { get; private set; }
        public Task<IReadOnlyList<CostCenter>> GetCostCentersAsync(string companyId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CostCenter>>(Array.Empty<CostCenter>());
        public Task<CostCenter?> GetCostCenterByCodeAsync(string companyId, string code, CancellationToken cancellationToken = default) => Task.FromResult<CostCenter?>(null);
        public Task<CostCenter?> GetCostCenterByIdAsync(string companyId, Guid id, CancellationToken cancellationToken = default) => Task.FromResult<CostCenter?>(null);
        public Task SaveCostCenterAsync(string companyId, CostCenter costCenter, string userId, CancellationToken cancellationToken = default) { SaveCostCenterCalls++; return Task.CompletedTask; }
        public Task SoftDeleteCostCenterAsync(string companyId, Guid id, string userId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<int> CountCostCenterJournalRefsAsync(string companyId, string code, CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<LedgerBalance> GetCostCenterBalanceAsync(string companyId, string code, CancellationToken cancellationToken = default) => Task.FromResult(new LedgerBalance());
        public Task<IReadOnlyDictionary<string, LedgerBalance>> GetCostCenterBalancesAsync(string companyId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyDictionary<string, LedgerBalance>>(new Dictionary<string, LedgerBalance>());
        public Task<IReadOnlyList<CostCenterAllocation>> GetAllocationsAsync(string companyId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CostCenterAllocation>>(Array.Empty<CostCenterAllocation>());
        public Task SaveAllocationAsync(string companyId, CostCenterAllocation allocation, string userId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SoftDeleteAllocationAsync(string companyId, Guid id, string userId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<Bank>> GetBanksAsync(string companyId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Bank>>(Array.Empty<Bank>());
        public Task<Bank?> GetBankByCodeAsync(string companyId, string code, CancellationToken cancellationToken = default) => Task.FromResult<Bank?>(null);
        public Task SaveBankAsync(string companyId, Bank bank, string userId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SoftDeleteBankAsync(string companyId, Guid id, string userId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<LedgerBalance> GetBankBalanceAsync(string companyId, string glAccountCode, CancellationToken cancellationToken = default) => Task.FromResult(new LedgerBalance());
        public Task<IReadOnlyList<Currency>> GetCurrenciesAsync(string companyId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Currency>>(Array.Empty<Currency>());
        public Task<Currency?> GetCurrencyByCodeAsync(string companyId, string code, CancellationToken cancellationToken = default) => Task.FromResult<Currency?>(null);
        public Task SaveCurrencyAsync(string companyId, Currency currency, string userId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SoftDeleteCurrencyAsync(string companyId, Guid id, string userId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<CurrencyRate>> GetRatesAsync(string companyId, string currencyCode, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CurrencyRate>>(Array.Empty<CurrencyRate>());
        public Task SaveRateAsync(string companyId, CurrencyRate rate, string userId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<Party>> GetPartiesAsync(string companyId, string? kind = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Party>>(Array.Empty<Party>());
        public Task<Party?> GetPartyByCodeAsync(string companyId, string kind, string code, CancellationToken cancellationToken = default) => Task.FromResult<Party?>(null);
        public Task<IReadOnlyList<Party>> SearchPartiesAsync(string companyId, string? kind, string query, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Party>>(Array.Empty<Party>());
        public Task SavePartyAsync(string companyId, Party party, string userId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SoftDeletePartyAsync(string companyId, Guid id, string userId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<int> CountPartyLedgerRefsAsync(string companyId, string kind, string code, CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<LedgerBalance> GetPartyBalanceAsync(string companyId, string kind, string code, CancellationToken cancellationToken = default) => Task.FromResult(new LedgerBalance());
        public Task<IReadOnlyList<PartyContact>> GetPartyContactsAsync(string companyId, Guid partyId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<PartyContact>>(Array.Empty<PartyContact>());
        public Task SavePartyContactAsync(string companyId, PartyContact contact, string userId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SoftDeletePartyContactAsync(string companyId, Guid id, string userId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<PartyAddress>> GetPartyAddressesAsync(string companyId, Guid partyId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<PartyAddress>>(Array.Empty<PartyAddress>());
        public Task SavePartyAddressAsync(string companyId, PartyAddress address, string userId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SoftDeletePartyAddressAsync(string companyId, Guid id, string userId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<PartyBranch>> GetPartyBranchesAsync(string companyId, Guid partyId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<PartyBranch>>(Array.Empty<PartyBranch>());
        public Task SavePartyBranchAsync(string companyId, PartyBranch branch, string userId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SoftDeletePartyBranchAsync(string companyId, Guid id, string userId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string> PostPartyOpeningBalanceAsync(string companyId, string kind, string code, decimal signedAmount, DateOnly openingDate, string currencyCode, string userId, CancellationToken cancellationToken = default) => Task.FromResult(Guid.NewGuid().ToString("D"));
        public Task<IReadOnlyList<PartyGroup>> GetPartyGroupsAsync(string companyId, string? kind = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<PartyGroup>>(Array.Empty<PartyGroup>());
        public Task<PartyGroup?> GetPartyGroupByCodeAsync(string companyId, string kind, string code, CancellationToken cancellationToken = default) => Task.FromResult<PartyGroup?>(null);
        public Task SavePartyGroupAsync(string companyId, PartyGroup group, string userId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SoftDeletePartyGroupAsync(string companyId, Guid id, string userId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<SalesRep>> GetSalesRepsAsync(string companyId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SalesRep>>(Array.Empty<SalesRep>());
        public Task<SalesRep?> GetSalesRepByCodeAsync(string companyId, string code, CancellationToken cancellationToken = default) => Task.FromResult<SalesRep?>(null);
        public Task SaveSalesRepAsync(string companyId, SalesRep rep, string userId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SoftDeleteSalesRepAsync(string companyId, Guid id, string userId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<SalesRepGroup>> GetSalesRepGroupsAsync(string companyId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SalesRepGroup>>(Array.Empty<SalesRepGroup>());
        public Task<SalesRepGroup?> GetSalesRepGroupByCodeAsync(string companyId, string code, CancellationToken cancellationToken = default) => Task.FromResult<SalesRepGroup?>(null);
        public Task SaveSalesRepGroupAsync(string companyId, SalesRepGroup group, string userId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SoftDeleteSalesRepGroupAsync(string companyId, Guid id, string userId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
