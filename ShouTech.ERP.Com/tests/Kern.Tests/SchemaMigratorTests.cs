using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ShouTech.App.Services;
using Xunit;

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
