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

    [Fact]
    public async Task DesktopMigrator_RefusesToReplayMigrationsOnExistingSchemaWithoutHistory()
    {
        var databaseDirectory = Path.Combine(
            Path.GetTempPath(),
            $"shoutech-schema-migrator-legacy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(databaseDirectory);
        var databasePath = Path.Combine(databaseDirectory, "legacy.db");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            Pooling = false
        }.ToString();

        try
        {
            await using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE legacy_company_data (id INTEGER PRIMARY KEY, value TEXT NOT NULL);";
                await command.ExecuteNonQueryAsync();
            }

            var result = await SchemaMigrator.ApplyAllAsync("sqlite", connectionString);

            Assert.False(result.Success);
            Assert.Contains("no migration history", result.Message, StringComparison.OrdinalIgnoreCase);

            await using var verify = new SqliteConnection(connectionString);
            await verify.OpenAsync();
            await using var check = verify.CreateCommand();
            check.CommandText = "SELECT COUNT(*) FROM schema_migrations;";
            Assert.Equal(0L, (long)(await check.ExecuteScalarAsync() ?? -1L));
            check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='legacy_company_data';";
            Assert.Equal(1L, (long)(await check.ExecuteScalarAsync() ?? -1L));
        }
        finally
        {
            if (Directory.Exists(databaseDirectory))
                Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    [Fact]
    public void MigrationBundleReader_PreservesLegacyIdsAndRejectsChangedSegments()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"shoutech-migration-bundle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "sys.sql");
        const string sql = "CREATE TABLE bundle_probe (id INTEGER PRIMARY KEY);\n";
        const string windowsHash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        const string linuxHash = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
        const string rawHash = "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC";
        var segmentHash = MigrationBundleReader.ComputeHash(sql);

        try
        {
            File.WriteAllText(path,
                $"-- @shoutech-migration 001_probe.sql {windowsHash} {linuxHash} {rawHash} {segmentHash}\n{sql}");

            var migration = Assert.Single(MigrationBundleReader.Load(directory));
            Assert.Equal("001_probe.sql", migration.Id);
            Assert.Equal(windowsHash, migration.Hash);
            Assert.True(migration.MatchesHash(linuxHash));
            Assert.True(migration.MatchesHash(rawHash));
            Assert.Equal(sql, migration.Sql);

            File.WriteAllText(path,
                $"-- @shoutech-migration 001_probe.sql {windowsHash} {linuxHash} {rawHash} {segmentHash}\n{sql}-- tampered");
            Assert.Throws<InvalidDataException>(() => MigrationBundleReader.Load(directory));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ProductCategorySchema_UsesOneLanguageNeutralName()
    {
        var databaseDirectory = Path.Combine(
            Path.GetTempPath(),
            $"shoutech-category-schema-{Guid.NewGuid():N}");
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
            var result = await SchemaMigrator.ApplyAllAsync("sqlite", connectionString);
            Assert.True(result.Success, result.Message);

            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA table_info(product_categories);";
            await using var reader = await command.ExecuteReaderAsync();
            var columns = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (await reader.ReadAsync())
                columns.Add(reader.GetString(1));

            Assert.Contains("name", columns);
            Assert.DoesNotContain("category_name_ar", columns);
            Assert.DoesNotContain("category_name_en", columns);
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
