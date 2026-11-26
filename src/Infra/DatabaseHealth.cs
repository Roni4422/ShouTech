using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.Domain.Interfaces;

namespace ShouTech.Infrastructure;

/// <summary>Checks connectivity using the supported SQLite and PostgreSQL providers.</summary>
public sealed class DatabaseHealth : IDatabaseHealth
{
    private readonly string _connectionString;
    private readonly string _provider;
    private readonly TimeSpan _timeout;

    public DatabaseHealth(string provider, string connectionString, TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("A database connection string is required.", nameof(connectionString));
        _provider = Normalize(provider);
        _connectionString = connectionString;
        _timeout = timeout ?? TimeSpan.FromSeconds(5);
    }

    public async Task<DatabaseHealthResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await using var connection = _provider == "SQLite"
                ? (System.Data.Common.DbConnection)new SqliteConnection(_connectionString)
                : new NpgsqlConnection(_connectionString);
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(_timeout);
            await connection.OpenAsync(timeoutSource.Token).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            await command.ExecuteScalarAsync(timeoutSource.Token).ConfigureAwait(false);
            stopwatch.Stop();
            return new DatabaseHealthResult(true, _provider, "Database connection is healthy.", stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new DatabaseHealthResult(false, _provider, "Database connection timed out.", stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new DatabaseHealthResult(false, _provider, ex.Message, stopwatch.Elapsed);
        }
    }

    private static string Normalize(string provider) => provider.Trim().ToLowerInvariant() switch
    {
        "sqlite" or "sqlite3" => "SQLite",
        "postgres" or "postgresql" or "npgsql" => "PostgreSQL",
        _ => throw new NotSupportedException($"Unsupported database provider '{provider}'. Use SQLite or PostgreSQL.")
    };
}
