using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ShouTech.Application.Services;
using ShouTech.Infrastructure;
using Xunit;

namespace Kern.Tests;

public sealed class MaintenanceReportTests : IDisposable
{
    private readonly string _path;
    private readonly OperationsService _service;

    public MaintenanceReportTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"shoutech-maint-{Guid.NewGuid():N}.db");
        _service = new OperationsService(new OperationsRepository("SQLite", $"Data Source={_path}"));
        using var connection = new SqliteConnection($"Data Source={_path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE maintenance_reports (
                report_no INTEGER PRIMARY KEY AUTOINCREMENT,
                company_code TEXT NOT NULL,
                run_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                created_by TEXT NOT NULL,
                provider TEXT NOT NULL,
                status TEXT NOT NULL,
                check_count INTEGER NOT NULL,
                passed_count INTEGER NOT NULL,
                warning_count INTEGER NOT NULL,
                failed_count INTEGER NOT NULL,
                report_json TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    [Fact]
    public async Task Reports_AreSequentialPersistentAndCompanyScoped()
    {
        var first = await _service.SaveMaintenanceReportAsync("C1", "admin", "SQLite", "Completed", 3, 2, 0, 1, "{\"checks\":[1]}");
        var second = await _service.SaveMaintenanceReportAsync("C1", "admin", "SQLite", "Completed", 1, 1, 0, 0, "{\"checks\":[2]}");

        Assert.Equal(first + 1, second);
        var history = await _service.GetMaintenanceReportsAsync("C1");
        Assert.Equal(2, history.Count);
        Assert.Equal(second, history[0].ReportNo);
        Assert.Null(await _service.GetMaintenanceReportAsync("OTHER", second));
        var detail = await _service.GetMaintenanceReportAsync("C1", first);
        Assert.NotNull(detail);
        Assert.Equal("{\"checks\":[1]}", detail!.ReportJson);
    }

    [Fact]
    public async Task Reports_RejectInvalidCounts()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _service.SaveMaintenanceReportAsync("C1", "admin", "SQLite", "Completed", 1, 2, 0, 0, "{}"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_path)) File.Delete(_path);
    }
}
