using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ShouTech.App.Services;
using Xunit;
using System.Linq;
using ShouTech.Application.Services;
using ShouTech.Infrastructure;

namespace Kern.Tests;

public sealed class SchemaMigratorTests
{
    [Fact]
    public async Task DesktopMigrator_UsesDbaHistoryAndIsIdempotent()
    {
        var databaseDirectory = Path.Combine(
            Path.GetTempPath(),
            $"shoutech-schema-migrator-{Guid.NewGuid():N}");
        Directory.CreateDirectory(databaseDirectory);
        var databasePath = Path.Combine(databaseDirectory, "company.db");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            Pooling = false
        }.ToString();

        try
        {
            var first = await SchemaMigrator.ApplyAllAsync("sqlite", connectionString);
            Assert.True(first.Success, first.Message);
            Assert.True(first.AppliedCount > 0);
            Assert.True(File.Exists(databasePath));

            var second = await SchemaMigrator.ApplyAllAsync("sqlite", connectionString);
            Assert.True(second.Success, second.Message);
            Assert.Equal(0, second.AppliedCount);
            Assert.True(second.SkippedCount > 0);
        }
        finally
        {
            if (Directory.Exists(databaseDirectory))
                Directory.Delete(databaseDirectory, recursive: true);
        }
    }
}

// ---- merged from tests/Kern.Tests/ErpPlanTests.cs ----
public class ErpPlanTests
{
    [Fact]
    public void ErpPlan_ShouldPrioritizeCoreModulesFirst()
    {
        var plan = new ErpPlan();

        var feats = plan.Pri();

        Assert.Equal("ACC", feats.First().Code);
        Assert.Contains(feats, x => x.Code == "DASH");
        Assert.Contains(feats, x => x.Code == "AI");
    }

    [Fact]
    public void ErpPlan_ShouldExposeFastWorkflowChecklist()
    {
        var plan = new ErpPlan();

        var workflow = plan.FastFlow();

        Assert.Contains(workflow, x => x.Contains("invoice", System.StringComparison.OrdinalIgnoreCase));
        Assert.Contains(workflow, x => x.Contains("stock", System.StringComparison.OrdinalIgnoreCase));
    }
}

// ---- merged from tests/Kern.Tests/BudgetServicePermissionTests.cs ----
public sealed class BudgetServicePermissionTests
{
    private static BudgetService DeniedService()
        => new(new BudgetRepository("SQLite", "Data Source=:memory:"), _ => false);

    [Fact]
    public async Task ReadRequiresBudgetViewPermission()
    {
        var service = DeniedService();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetAllAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetVarianceAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetFiscalYearsAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task DeleteRequiresBudgetEditPermission()
    {
        var service = DeniedService();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.DeleteAsync(Guid.NewGuid(), Guid.NewGuid(), "user"));
    }

    [Fact]
    public async Task StatusChangeRequiresBudgetActivationPermission()
    {
        var service = DeniedService();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.SetStatusAsync(Guid.NewGuid(), Guid.NewGuid(), "Active", "user"));
    }
}
