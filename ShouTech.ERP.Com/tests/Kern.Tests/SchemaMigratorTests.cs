using System;
using System.IO;
using System.Threading.Tasks;
using ShouTech.App.Services;
using Xunit;

namespace Kern.Tests;

public sealed class SchemaMigratorTests
{
    [Fact]
    public async Task DesktopMigrator_UsesDbaHistoryAndIsIdempotent()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"shoutech-schema-migrator-{Guid.NewGuid():N}.db");

        try
        {
            var connectionString = $"Data Source={databasePath}";

            var first = await SchemaMigrator.ApplyAllAsync("sqlite", connectionString);
            Assert.True(first.Success, first.Message);
            Assert.True(first.AppliedCount > 0);

            var second = await SchemaMigrator.ApplyAllAsync("sqlite", connectionString);
            Assert.True(second.Success, second.Message);
            Assert.Equal(0, second.AppliedCount);
            Assert.True(second.SkippedCount > 0);
        }
        finally
        {
            if (File.Exists(databasePath))
                File.Delete(databasePath);
        }
    }
}
