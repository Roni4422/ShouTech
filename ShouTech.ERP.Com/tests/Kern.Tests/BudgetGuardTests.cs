using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ShouTech.App;
using ShouTech.App.Configuration;
using ShouTech.App.Services;
using ShouTech.Application.Services;
using ShouTech.Domain;
using ShouTech.Domain.Entities;
using ShouTech.Infrastructure;
using Xunit;

namespace Kern.Tests;

/// <summary>
/// Phase B 055 — Budgets (migration 055, BudgetGuard). Pins, against
/// real migrated SQLite databases:
///   * an Active Account budget blocks an over-budget journal posting
///     with ACC-5018 and writes nothing (entry stays Draft, a Blocked
///     event is recorded);
///   * Warn mode posts, returns the warning, and records a Warned
///     event; actuals then read over-budget in variance;
///   * the accumulated-monthly horizon blocks with ACC-5019 under an
///     equal split and under a custom distribution;
///   * a document posting through GlPosting (PostDocumentAsync) is
///     guarded identically;
///   * reversing a consuming posting restores headroom exactly;
///   * unbudgeted accounts and Draft budgets never block;
///   * cost-center budgets capture only center-attributed amounts
///     (a center-less line posts freely against a CC budget), and a
///     budget on a parent center covers its children;
///   * the chart budget flag is the opt-in: saving a budget sets it,
///     clearing it opts the account out.
/// Execution requires Windows (net8.0-windows testhost); the same
/// scenarios are verified headlessly by
/// ~/workspace/research/fix-step55/budget55_harness.py
/// (see docs/BUILD_PHASEB_055.md).
/// </summary>
public class BudgetGuardTests
{
    private const string Actor = "budget-test";
    private const string CompanyCode = "BUD1";
    private static readonly Guid CenterOne = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid CenterParent = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid CenterChild = Guid.Parse("33333333-3333-4333-8333-333333333333");

    [Fact]
    public async Task AnnualStop_BlocksOverBudgetPosting_AndRecordsBlockedEvent()
    {
        using var db = await SeededAsync();
        await CreateBudgetAsync(db, "Ops", BudgetAgainstKinds.Account, "510", null,
            1000m, BudgetActions.Stop, BudgetActions.Ignore, activate: true);

        await PostJournalAsync(db, 400m, "510", null, new DateOnly(2026, 2, 10), "JV-B-1");
        Assert.Equal(400m, await AnnualActualAsync(db, "510"));

        var blockedId = await CreateDraftAsync(db, 700m, "510", null, new DateOnly(2026, 3, 10), "JV-B-2");
        var error = await Assert.ThrowsAsync<AppError>(() => db.Journal.PostAsync(blockedId, Actor));
        Assert.Equal(ErrorCodes.BudgetExceededAnnual, error.Code);

        // Nothing was written: the entry is still Draft, actuals are
        // unchanged, and the block itself is auditable.
        Assert.Equal(0, EntryStatus(db, blockedId));
        Assert.Equal(400m, await AnnualActualAsync(db, "510"));
        Assert.Equal(1, EventCount(db, "Blocked"));
    }

    [Fact]
    public async Task WarnMode_Posts_ReturnsWarning_AndActualExceedsBudget()
    {
        using var db = await SeededAsync();
        await CreateBudgetAsync(db, "Ops", BudgetAgainstKinds.Account, "510", null,
            1000m, BudgetActions.Warn, BudgetActions.Ignore, activate: true);

        var first = await PostJournalAsync(db, 400m, "510", null, new DateOnly(2026, 2, 10), "JV-B-1");
        Assert.Empty(first);
        var second = await PostJournalAsync(db, 700m, "510", null, new DateOnly(2026, 3, 10), "JV-B-2");
        var warning = Assert.Single(second);
        Assert.Equal(100m, warning.Overage);
        Assert.Equal(1100m, await AnnualActualAsync(db, "510"));
        Assert.Equal(1, EventCount(db, "Warned"));
    }

    [Fact]
    public async Task MonthlyStop_EqualSplit_BlocksAccumulatedOverrun_WithAcc5019()
    {
        using var db = await SeededAsync();
        await CreateBudgetAsync(db, "Ops", BudgetAgainstKinds.Account, "510", null,
            12000m, BudgetActions.Ignore, BudgetActions.Stop, activate: true);

        // Equal split: 1000/month; through March the limit is 3000.
        await PostJournalAsync(db, 2500m, "510", null, new DateOnly(2026, 3, 5), "JV-B-1");
        var blockedId = await CreateDraftAsync(db, 1000m, "510", null, new DateOnly(2026, 3, 10), "JV-B-2");
        var error = await Assert.ThrowsAsync<AppError>(() => db.Journal.PostAsync(blockedId, Actor));
        Assert.Equal(ErrorCodes.BudgetExceededMonthly, error.Code);
    }

    [Fact]
    public async Task MonthlyStop_CustomDistribution_IsAuthoritative()
    {
        using var db = await SeededAsync();
        var distribution = new Dictionary<int, decimal> { [1] = 600m, [2] = 600m };
        for (var m = 3; m <= 12; m++) distribution[m] = 0m;
        await CreateBudgetAsync(db, "Ops", BudgetAgainstKinds.Account, "510", null,
            1200m, BudgetActions.Ignore, BudgetActions.Stop, activate: true, distribution: distribution);

        await PostJournalAsync(db, 500m, "510", null, new DateOnly(2026, 1, 15), "JV-B-1");
        var blockedId = await CreateDraftAsync(db, 200m, "510", null, new DateOnly(2026, 1, 20), "JV-B-2");
        var error = await Assert.ThrowsAsync<AppError>(() => db.Journal.PostAsync(blockedId, Actor));
        Assert.Equal(ErrorCodes.BudgetExceededMonthly, error.Code);
    }

    [Fact]
    public async Task DocumentPosting_ThroughGlPosting_IsGuardedIdentically()
    {
        using var db = await SeededAsync();
        await CreateBudgetAsync(db, "Ops", BudgetAgainstKinds.Account, "510", null,
            1000m, BudgetActions.Stop, BudgetActions.Ignore, activate: true);
        await PostJournalAsync(db, 400m, "510", null, new DateOnly(2026, 2, 10), "JV-B-1");

        var store = new JournalRepository("SQLite", db.ConnectionString);
        var request = new GlDocumentRequest(db.CompanyId, new DateOnly(2026, 3, 10),
            "Expense document", "EXP", "77", "EXP-77", "SAR", 1m,
            new GlLineRequest[] { new("510", 700m, 0m), new("131", 0m, 700m) });
        var error = await Assert.ThrowsAsync<AppError>(() => store.PostDocumentAsync(request, Actor));
        Assert.Equal(ErrorCodes.BudgetExceededAnnual, error.Code);
        Assert.Equal(400m, await AnnualActualAsync(db, "510"));
        Assert.Equal(1, EventCount(db, "Blocked"));
    }

    [Fact]
    public async Task Reversal_RestoresHeadroom_Exactly()
    {
        using var db = await SeededAsync();
        await CreateBudgetAsync(db, "Ops", BudgetAgainstKinds.Account, "510", null,
            1000m, BudgetActions.Stop, BudgetActions.Ignore, activate: true);

        var entryId = await CreateDraftAsync(db, 400m, "510", null, new DateOnly(2026, 2, 10), "JV-B-1");
        await db.Journal.PostAsync(entryId, Actor);
        Assert.Equal(400m, await AnnualActualAsync(db, "510"));

        await db.Journal.ReverseAsync(entryId, Actor);
        Assert.Equal(0m, await AnnualActualAsync(db, "510"));

        // The full budget is available again: exactly-at-limit posts.
        await PostJournalAsync(db, 1000m, "510", null, new DateOnly(2026, 3, 10), "JV-B-2");
        Assert.Equal(1000m, await AnnualActualAsync(db, "510"));
    }

    [Fact]
    public async Task UnbudgetedAccount_AndDraftBudget_NeverBlock()
    {
        using var db = await SeededAsync();
        await CreateBudgetAsync(db, "Ops", BudgetAgainstKinds.Account, "510", null,
            1000m, BudgetActions.Stop, BudgetActions.Stop, activate: true);
        await CreateBudgetAsync(db, "Drafty", BudgetAgainstKinds.Account, "520", null,
            10m, BudgetActions.Stop, BudgetActions.Stop, activate: false);

        await PostJournalAsync(db, 99999m, "520", null, new DateOnly(2026, 2, 10), "JV-B-1");
        Assert.Equal(99999m, await AnnualActualAsync(db, "520"));
    }

    [Fact]
    public async Task CostCenterBudget_CapturesOnlyAttributedAmounts_ParentCoversChildren()
    {
        using var db = await SeededAsync();
        // Budget on the PARENT center; postings land on its child.
        await CreateBudgetAsync(db, "Centers", BudgetAgainstKinds.CostCenter, "510",
            CenterParent.ToString("D"), 1000m, BudgetActions.Stop, BudgetActions.Ignore, activate: true);

        await PostJournalAsync(db, 400m, "510", "CC-CHILD", new DateOnly(2026, 2, 10), "JV-B-1");
        Assert.Equal(400m, await AnnualActualAsync(db, "510"));

        // A center-less line is invisible to the CC budget (documented
        // rule): it posts freely and does not consume the budget.
        await PostJournalAsync(db, 700m, "510", null, new DateOnly(2026, 2, 12), "JV-B-2");
        Assert.Equal(400m, await AnnualActualAsync(db, "510"));

        var blockedId = await CreateDraftAsync(db, 700m, "510", "CC-CHILD", new DateOnly(2026, 3, 10), "JV-B-3");
        var error = await Assert.ThrowsAsync<AppError>(() => db.Journal.PostAsync(blockedId, Actor));
        Assert.Equal(ErrorCodes.BudgetExceededAnnual, error.Code);
    }

    [Fact]
    public async Task BudgetFlag_IsTheOptIn_SavingSetsIt_ClearingOptsOut()
    {
        using var db = await SeededAsync();
        Assert.Equal(0, BudgetFlag(db, "510"));
        await CreateBudgetAsync(db, "Ops", BudgetAgainstKinds.Account, "510", null,
            1000m, BudgetActions.Stop, BudgetActions.Ignore, activate: true);
        Assert.Equal(1, BudgetFlag(db, "510"));

        Exec(db, "UPDATE chart_of_accounts SET budget = 0 WHERE account_code = '510';");
        await PostJournalAsync(db, 5000m, "510", null, new DateOnly(2026, 2, 10), "JV-B-1");
    }

    // ------------------------------------------------------------ helpers

    private static async Task CreateBudgetAsync(TestDb db, string name, string against,
        string accountCode, string? dimensionId, decimal annual, string actionAnnual,
        string actionMonthly, bool activate, Dictionary<int, decimal>? distribution = null)
    {
        var accountId = await db.Store.ResolveAccountIdAsync(db.CompanyId, accountCode);
        var budget = new Budget
        {
            FiscalYearId = db.FiscalYearId,
            Name = name,
            BudgetAgainst = against,
            Status = BudgetStatuses.Draft,
            ActionAnnual = actionAnnual,
            ActionMonthly = actionMonthly,
            Lines = new List<BudgetLine>
            {
                new()
                {
                    AccountId = accountId,
                    AccountCode = accountCode,
                    CostCenterId = against == BudgetAgainstKinds.CostCenter ? dimensionId : null,
                    ProjectId = against == BudgetAgainstKinds.Project ? dimensionId : null,
                    AnnualAmount = annual,
                    PeriodAmounts = distribution ?? new Dictionary<int, decimal>()
                }
            }
        };
        await db.Budgets.SaveAsync(db.CompanyId, budget, Actor);
        if (activate)
            await db.Budgets.SetStatusAsync(db.CompanyId, budget.Id, BudgetStatuses.Active, Actor);
    }

    private static async Task<Guid> CreateDraftAsync(TestDb db, decimal amount, string accountCode,
        string? costCenter, DateOnly date, string documentNumber)
    {
        var expense = await db.Store.ResolveAccountIdAsync(db.CompanyId, accountCode);
        var cash = await db.Store.ResolveAccountIdAsync(db.CompanyId, "131");
        return await db.Journal.CreateDraftAsync(db.CompanyId, date, "Budget test entry", Actor,
            new[]
            {
                new JournalLineRequest(expense, amount, 0m, null, costCenter),
                new JournalLineRequest(cash, 0m, amount)
            }, documentNumber: documentNumber);
    }

    private static async Task<IReadOnlyList<BudgetWarning>> PostJournalAsync(TestDb db, decimal amount,
        string accountCode, string? costCenter, DateOnly date, string documentNumber)
    {
        var entryId = await CreateDraftAsync(db, amount, accountCode, costCenter, date, documentNumber);
        return await db.Journal.PostAsync(entryId, Actor);
    }

    private static async Task<decimal> AnnualActualAsync(TestDb db, string accountCode)
    {
        var rows = await db.Budgets.GetVarianceAsync(db.CompanyId);
        return rows.Where(r => r.AccountCode == accountCode).Sum(r => r.AnnualActual);
    }

    private static int EntryStatus(TestDb db, Guid entryId)
    {
        using var connection = new SqliteConnection(db.ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT status FROM journal_entries WHERE journal_entry_id = $id;";
        cmd.Parameters.AddWithValue("$id", entryId.ToString("D"));
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static int EventCount(TestDb db, string actionTaken)
    {
        using var connection = new SqliteConnection(db.ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM budget_control_events WHERE action_taken = $a;";
        cmd.Parameters.AddWithValue("$a", actionTaken);
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static int BudgetFlag(TestDb db, string accountCode)
    {
        using var connection = new SqliteConnection(db.ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT budget FROM chart_of_accounts WHERE account_code = $c;";
        cmd.Parameters.AddWithValue("$c", accountCode);
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void Exec(TestDb db, string sql)
    {
        using var connection = new SqliteConnection(db.ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private sealed class TestDb : IDisposable
    {
        public required string Root { get; init; }
        public required string DbPath { get; init; }
        public required Guid CompanyId { get; init; }
        public required long FiscalYearId { get; init; }
        public required JournalRepository Store { get; init; }
        public required JournalService Journal { get; init; }
        public required BudgetService Budgets { get; init; }
        public string ConnectionString => $"Data Source={DbPath};";
        public void Dispose()
        {
            AppState.ClearCompany();
            SqliteConnection.ClearAllPools();
            Directory.Delete(Root, recursive: true);
        }
    }

    private static async Task<TestDb> SeededAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "budget-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var dbPath = Path.Combine(root, "budget.db");
        var companyId = Guid.NewGuid();
        var connectionString = $"Data Source={dbPath};";

        Assert.True((await SchemaMigrator.ApplyAllAsync("SQLite", dbPath)).Success);
        PointAppAt(dbPath);
        AppState.SetCompany(CompanyCode, "Budget Test Co", "Budget Test Co");

        using (var connection = new SqliteConnection(connectionString))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "INSERT INTO companies (company_id, company_code, company_name_ar, company_name_en) VALUES ($id, $code, 'Budget Test Co', 'Budget Test Co');";
            cmd.Parameters.AddWithValue("$id", companyId.ToString("D"));
            cmd.Parameters.AddWithValue("$code", CompanyCode);
            cmd.ExecuteNonQuery();
        }

        var structure = new AccountingStructureRepository("SQLite", connectionString);
        var fiscalYearId = await structure.CreateFiscalYearAsync(companyId, 2026, Actor);

        foreach (var (code, name) in new[] { ("131", "Cash"), ("510", "Rent Expense"), ("520", "Travel Expense") })
        {
            AccRepo.Save(null, new AccRepo.AccRec(
                string.Empty, code, null, name, name, "Postable", "Active", "Low", "Unspecified",
                false, false, false, false, false, false, 0m, string.Empty, 0m, 0m, 0m),
                renumberDescendants: false);
        }

        // Cost centers (master world, keyed by company CODE): a leaf, a
        // group parent, and the parent's child.
        using (var connection = new SqliteConnection(connectionString))
        {
            connection.Open();
            void SeedCenter(Guid id, string code, string name, int isGroup, string? parentId)
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO cost_centers (id, company_id, code, name_en, parent_id, is_group, is_active, is_deleted)
                    VALUES ($id, $company, $code, $name, $parent, $group, 1, 0);
                    """;
                cmd.Parameters.AddWithValue("$id", id.ToString("D"));
                cmd.Parameters.AddWithValue("$company", CompanyCode);
                cmd.Parameters.AddWithValue("$code", code);
                cmd.Parameters.AddWithValue("$name", name);
                cmd.Parameters.AddWithValue("$parent", (object?)parentId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$group", isGroup);
                cmd.ExecuteNonQuery();
            }
            SeedCenter(CenterOne, "CC1", "Center One", 0, null);
            SeedCenter(CenterParent, "CC-PARENT", "Parent Center", 1, null);
            SeedCenter(CenterChild, "CC-CHILD", "Child Center", 0, CenterParent.ToString("D"));
        }

        var store = new JournalRepository("SQLite", connectionString);
        return new TestDb
        {
            Root = root,
            DbPath = dbPath,
            CompanyId = companyId,
            FiscalYearId = fiscalYearId,
            Store = store,
            Journal = new JournalService(store),
            Budgets = new BudgetService(new BudgetRepository("SQLite", connectionString))
        };
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
}
