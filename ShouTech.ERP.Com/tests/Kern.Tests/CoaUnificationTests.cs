using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ShouTech.App;
using ShouTech.App.Configuration;
using ShouTech.App.Services;
using ShouTech.Application.Services;
using ShouTech.Domain.Entities;
using Xunit;

namespace Kern.Tests;

/// <summary>
/// Fix Phase Step 2 — chart-of-accounts unification regression coverage
/// (sweep journal-coa.md J-1/C-1, C-2, C-3, R-1, R-2, R-3). Pins:
///   * the chart the UI maintains (AccRepo over chart_of_accounts) is the
///     chart the journal resolves against — a journal draft using a
///     CoaWin-visible code saves AND posts on a fresh company;
///   * Trial Balance / Balance Sheet / P&L / Account Statement compute
///     from POSTED journal_entry_lines (never stored balances), with the
///     Trial Balance honoring its date range;
///   * AccRec balances are computed live from posted lines (C-2);
///   * the lifecycle guard refuses delete/disable for an account with
///     posted movements and allows it for an unused account (C-3);
///   * migration 059 leaves exactly one chart store (legacy accounts
///     table gone, extension columns present) and the migrator is
///     idempotent on a double run.
/// All facts run the REAL services against real migrated SQLite
/// databases. Execution requires Windows (net8.0-windows testhost); the
/// same scenarios — plus the pre-unification fixture upgrade — are
/// verified headlessly by ~/workspace/research/fix-step2/coa_harness.py
/// (27/27 PASS; see docs/FIX_PHASE_STEP2.md).
/// </summary>
public class CoaUnificationTests
{
    private const string Actor = "coa-test";
    private const string CompanyCode = "CT1";

    [Fact]
    public async Task FreshCompany_UiChart_JournalSavesAndPosts()
    {
        using var db = await SeededCompanyAsync();
        var journalStore = new ShouTech.Infrastructure.JournalRepository("SQLite", db.ConnectionString);
        var journal = new JournalService(journalStore);

        var companyId = await journalStore.ResolveCompanyIdAsync(CompanyCode);
        Assert.Equal(db.CompanyId, companyId);

        // The exact pre-fix failure: code 131 is visible in CoaWin (seeded
        // through AccRepo below) but ResolveAccountIdAsync threw
        // "Account code '131' is missing..." because it read the other table.
        var cashId = await journalStore.ResolveAccountIdAsync(companyId, "131");
        var salesId = await journalStore.ResolveAccountIdAsync(companyId, "51");

        var entryId = await journal.CreateDraftAsync(
            companyId, new DateOnly(2026, 3, 10), "Unification sale", Actor,
            new[]
            {
                new JournalLineRequest(cashId, 1000m, 0m),
                new JournalLineRequest(salesId, 0m, 1000m)
            },
            documentNumber: "JV-CT-1");
        await journal.PostAsync(entryId, Actor);

        var loaded = await journal.FindByDocumentNumberAsync(companyId, "JV-CT-1");
        Assert.NotNull(loaded);
        Assert.Equal(JournalEntryStatus.Posted, loaded!.Status);
        Assert.Equal("131", loaded.Lines[0].AccountCode);
        Assert.Equal(1000m, loaded.Lines[0].Debit);
    }

    [Fact]
    public async Task Reports_ComputeFromPostedLines()
    {
        using var db = await SeededCompanyAsync();
        await PostTwoEntriesAsync(db);

        var tb = await RptQuery.TrialBalanceAsync(new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), CancellationToken.None);
        var cash = tb.Single(r => (string)r["Code"]! == "131");
        Assert.Equal(1000m, Dec(cash["Debit"]));
        Assert.Equal(250m, Dec(cash["Credit"]));
        Assert.Equal(750m, Dec(cash["Balance"]));
        Assert.Equal(tb.Sum(r => Dec(r["Debit"])), tb.Sum(r => Dec(r["Credit"])));

        // The pre-fix Trial Balance ignored its dates; a June-only window
        // must now be all zeros.
        var june = await RptQuery.TrialBalanceAsync(new DateTime(2026, 6, 1), new DateTime(2026, 6, 30), CancellationToken.None);
        Assert.All(june, r => Assert.Equal(0m, Dec(r["Debit"])));

        var bs = await RptQuery.BalanceSheetAsync(new DateTime(2026, 12, 31), CancellationToken.None);
        Assert.Equal(750m, Dec(bs.Single(r => (string)r["Code"]! == "131")["Amount"]));

        var pl = await RptQuery.IncomeStatementAsync(new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), CancellationToken.None);
        Assert.Equal(1000m, Dec(pl.Single(r => (string)r["Code"]! == "51")["Amount"]));
        Assert.Equal(250m, Dec(pl.Single(r => (string)r["Code"]! == "61")["Amount"]));

        var stmt = await RptQuery.AccountStatementAsync("131", new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), CancellationToken.None);
        Assert.Equal(2, stmt.Count);
        Assert.Equal(new[] { "JV-CT-1", "JV-CT-2" }, stmt.Select(r => (string)r["DocNo"]!).ToArray());
    }

    [Fact]
    public async Task AccRecBalances_AreComputedFromPostedLines_NotStored()
    {
        using var db = await SeededCompanyAsync();
        await PostTwoEntriesAsync(db);

        var cash = AccRepo.LoadAll().Single(x => x.Code == "131");
        Assert.Equal(1000m, cash.Debit);
        Assert.Equal(250m, cash.Credit);
        Assert.Equal(750m, cash.Balance);

        var banks = AccRepo.LoadAll().Single(x => x.Code == "132");
        Assert.Equal(0m, banks.Balance);
    }

    [Fact]
    public async Task LifecycleGuard_BlocksPostedAccount_AllowsUnusedAccount()
    {
        using var db = await SeededCompanyAsync();
        await PostTwoEntriesAsync(db);

        var cash = AccRepo.LoadAll().Single(x => x.Code == "131");
        var guard = AccRepo.CheckLifecycle(cash);
        Assert.False(guard.CanDelete);
        Assert.False(guard.CanDisable);
        Assert.Throws<InvalidOperationException>(() => AccRepo.Delete(cash));

        var banks = AccRepo.LoadAll().Single(x => x.Code == "132");
        var free = AccRepo.CheckLifecycle(banks);
        Assert.True(free.CanDelete, string.Join(";", free.Reasons));
        AccRepo.Delete(banks);
        Assert.DoesNotContain(AccRepo.LoadAll(), x => x.Code == "132");
    }

    [Fact]
    public async Task AccRepo_RoundTrip_PreservesTypeParentAndFlags()
    {
        using var db = await SeededCompanyAsync();

        var rows = AccRepo.LoadAll();
        var group = rows.Single(x => x.Code == "13");
        Assert.Equal("Summary", group.PostingMode);
        var cash = rows.Single(x => x.Code == "131");
        Assert.Equal("13", cash.ParentCode);
        Assert.True(cash.Reconcile);

        // Renumbering the group renumbers descendants' codes while the
        // Guid parent link keeps them attached (no orphaned children).
        var renamed = group with { Code = "18" };
        AccRepo.Save(group, renamed, renumberDescendants: true);
        var after = AccRepo.LoadAll();
        Assert.Contains(after, x => x.Code == "18");
        Assert.Equal("18", after.Single(x => x.Code == "181").ParentCode);
        Assert.Equal("18", after.Single(x => x.Code == "182").ParentCode);
    }

    [Fact]
    public async Task Migration_LeavesOneChartStore_AndDoubleRunIsIdempotent()
    {
        var root = Path.Combine(Path.GetTempPath(), "coa-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var dbPath = Path.Combine(root, "coa.db");
        try
        {
            Assert.True((await SchemaMigrator.ApplyAllAsync("SQLite", dbPath)).Success);
            Assert.True((await SchemaMigrator.ApplyAllAsync("SQLite", dbPath)).Success);

            using var connection = new SqliteConnection($"Data Source={dbPath};");
            connection.Open();
            Assert.Equal(0L, ScalarLong(connection,
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='accounts'"));
            Assert.Equal(1L, ScalarLong(connection,
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='chart_of_accounts'"));
            var columns = new List<string>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "PRAGMA table_info(chart_of_accounts);";
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) columns.Add(reader.GetString(1));
            }
            Assert.Contains("name_ar", columns);
            Assert.Contains("tax_rate", columns);
            Assert.DoesNotContain("balance", columns);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    // ------------------------------------------------------------ seeding

    private sealed class TestDb : IDisposable
    {
        public required string Root { get; init; }
        public required string DbPath { get; init; }
        public required Guid CompanyId { get; init; }
        public string ConnectionString => $"Data Source={DbPath};";

        public void Dispose()
        {
            AppState.ClearCompany();
            SqliteConnection.ClearAllPools();
            Directory.Delete(Root, recursive: true);
        }
    }

    /// <summary>
    /// A fresh company the way the app creates one: migrated database,
    /// companies row, fiscal year WITH monthly periods
    /// (AccountingStructureRepository — the same engine Company creation's
    /// period seeding mirrors), and the chart seeded through AccRepo, the
    /// exact writer CoaWin/AccCard/company creation use after Step 2.
    /// </summary>
    private static async Task<TestDb> SeededCompanyAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "coa-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var dbPath = Path.Combine(root, "coa.db");
        var companyId = Guid.NewGuid();
        var connectionString = $"Data Source={dbPath};";

        Assert.True((await SchemaMigrator.ApplyAllAsync("SQLite", dbPath)).Success);
        PointAppAt(dbPath);
        AppState.SetCompany(CompanyCode, "Unification Test Co", "Unification Test Co");

        using (var connection = new SqliteConnection(connectionString))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                "INSERT INTO companies (company_id, company_code, company_name_ar, company_name_en) VALUES ($id, $code, 'Unification Test Co', 'Unification Test Co');";
            cmd.Parameters.AddWithValue("$id", companyId.ToString("D"));
            cmd.Parameters.AddWithValue("$code", CompanyCode);
            cmd.ExecuteNonQuery();
        }

        var structure = new ShouTech.Infrastructure.AccountingStructureRepository("SQLite", connectionString);
        await structure.CreateFiscalYearAsync(companyId, 2026, Actor);

        Seed("13", null, "Cash & Cash Equivalents", "Summary");
        Seed("131", "13", "Cash", "Postable", reconcile: true);
        Seed("132", "13", "Banks", "Postable");
        Seed("51", null, "Sales", "Postable");
        Seed("61", null, "Rent", "Postable");

        return new TestDb { Root = root, DbPath = dbPath, CompanyId = companyId };
    }

    private static void Seed(string code, string? parent, string name, string postingMode, bool reconcile = false)
    {
        AccRepo.Save(null, new AccRepo.AccRec(
            string.Empty, code, parent, name, name, postingMode, "Active", "Low", "Unspecified",
            reconcile, false, false, false, false, false, 0m, string.Empty, 0m, 0m, 0m),
            renumberDescendants: false);
    }

    private static async Task PostTwoEntriesAsync(TestDb db)
    {
        var store = new ShouTech.Infrastructure.JournalRepository("SQLite", db.ConnectionString);
        var journal = new JournalService(store);
        var cash = await store.ResolveAccountIdAsync(db.CompanyId, "131");
        var sales = await store.ResolveAccountIdAsync(db.CompanyId, "51");
        var rent = await store.ResolveAccountIdAsync(db.CompanyId, "61");

        var sale = await journal.CreateDraftAsync(
            db.CompanyId, new DateOnly(2026, 3, 10), "Sale", Actor,
            new[] { new JournalLineRequest(cash, 1000m, 0m), new JournalLineRequest(sales, 0m, 1000m) },
            documentNumber: "JV-CT-1");
        await journal.PostAsync(sale, Actor);

        var rentEntry = await journal.CreateDraftAsync(
            db.CompanyId, new DateOnly(2026, 3, 11), "Rent", Actor,
            new[] { new JournalLineRequest(rent, 250m, 0m), new JournalLineRequest(cash, 0m, 250m) },
            documentNumber: "JV-CT-2");
        await journal.PostAsync(rentEntry, Actor);
    }

    private static void PointAppAt(string sqlitePath)
    {
        var settings = AppCfgSvc.Current;
        settings.ConnectionStrings["Default"] = new ConnectionStringSettings
        {
            Provider = "SQLite",
            ConnectionString = sqlitePath
        };
        settings.Database ??= new DatabaseSettings();
        settings.Database.DefaultProvider = "SQLite";
    }

    private static decimal Dec(object? value)
        => value is null ? 0m : Convert.ToDecimal(value, CultureInfo.InvariantCulture);

    private static long ScalarLong(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}
