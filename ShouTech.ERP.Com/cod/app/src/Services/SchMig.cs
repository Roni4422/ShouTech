#nullable enable
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace ShouTech.App.Services;

/// <summary>
/// Applies ordered SQL migrations from dba/mig/{sqlite|postgresql} to a company database.
/// Provider-specific: SQLite files are never executed against PostgreSQL and vice versa.
/// </summary>
public static class SchemaMigrator
{
    public sealed class MigrationResult
    {
        public bool Success { get; init; }
        public string Provider { get; init; } = "";
        public int AppliedCount { get; init; }
        public int SkippedCount { get; init; }
        public string Message { get; init; } = "";
        public string? FailedMigrationId { get; init; }
    }

    private sealed record MigrationFile(string Id, string Path, string Sql, string Hash);

    public static string NormalizeProvider(string? provider)
    {
        var p = (provider ?? "sqlite").Trim().ToLowerInvariant();
        if (p is "pg" or "postgres" or "postgresql" or "npgsql")
            return "postgresql";
        return "sqlite";
    }

    public static string? FindRepositoryRoot(string? startDirectory = null)
    {
        var dir = new DirectoryInfo(
            string.IsNullOrWhiteSpace(startDirectory)
                ? AppContext.BaseDirectory
                : startDirectory);

        // Prefer explicit layout next to the executable (published / bin output with linked migrations).
        var baseDir = AppContext.BaseDirectory;
        if (Directory.Exists(Path.Combine(baseDir, "dba", "mig")))
            return baseDir;

        while (dir != null)
        {
            var mig = Path.Combine(dir.FullName, "dba", "mig");
            if (Directory.Exists(mig))
                return dir.FullName;
            dir = dir.Parent;
        }

        return null;
    }

    public static string ResolveMigrationDirectory(string provider)
    {
        var root = FindRepositoryRoot()
            ?? FindRepositoryRoot(Directory.GetCurrentDirectory())
            ?? throw new InvalidOperationException(
                "Repository root with dba/mig was not found. Deploy dba/mig next to the application or run from the repository.");

        var normalized = NormalizeProvider(provider);
        var path = Path.Combine(root, "dba", "mig", normalized);
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException($"Migration directory not found: {path}");
        return path;
    }

    public static async Task<MigrationResult> ApplyAllAsync(
        string provider,
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeProvider(provider);
        if (string.IsNullOrWhiteSpace(connectionString))
            return new MigrationResult
            {
                Success = false,
                Provider = normalized,
                Message = "Connection string is required to apply schema migrations."
            };

        try
        {
            var directory = ResolveMigrationDirectory(normalized);
            var migrations = Directory.GetFiles(directory, "*.sql", SearchOption.TopDirectoryOnly)
                .Select(path =>
                {
                    var bytes = File.ReadAllBytes(path);
                    return new MigrationFile(
                        Path.GetFileName(path),
                        path,
                        File.ReadAllText(path, Encoding.UTF8),
                        Convert.ToHexString(SHA256.HashData(bytes)));
                })
                .OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (migrations.Count == 0)
            {
                return new MigrationResult
                {
                    Success = false,
                    Provider = normalized,
                    Message = $"No SQL migrations found in {directory}."
                };
            }

            await using var connection = CreateConnection(normalized, connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await EnsureHistoryTableAsync(connection, normalized, cancellationToken).ConfigureAwait(false);

            var applied = await ReadAppliedAsync(connection, cancellationToken).ConfigureAwait(false);
            var appliedCount = 0;
            var skippedCount = 0;

            foreach (var migration in migrations)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (applied.TryGetValue(migration.Id, out var existingHash))
                {
                    if (!string.Equals(existingHash, migration.Hash, StringComparison.OrdinalIgnoreCase))
                    {
                        return new MigrationResult
                        {
                            Success = false,
                            Provider = normalized,
                            AppliedCount = appliedCount,
                            SkippedCount = skippedCount,
                            FailedMigrationId = migration.Id,
                            Message =
                                $"Migration checksum mismatch: {migration.Id}. Applied migrations must be immutable; add a new migration instead."
                        };
                    }

                    skippedCount++;
                    continue;
                }

                await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (normalized == "sqlite")
                        await ExecuteSqliteMigrationAsync(connection, migration.Sql, transaction, cancellationToken)
                            .ConfigureAwait(false);
                    else
                    {
                        await using var command = connection.CreateCommand();
                        command.Transaction = transaction;
                        command.CommandTimeout = 600;
                        command.CommandText = migration.Sql;
                        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    }

                    await RecordMigrationAsync(connection, migration, transaction, cancellationToken)
                        .ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    appliedCount++;
                }
                catch (Exception ex)
                {
                    try { await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false); } catch { /* ignore */ }
                    return new MigrationResult
                    {
                        Success = false,
                        Provider = normalized,
                        AppliedCount = appliedCount,
                        SkippedCount = skippedCount,
                        FailedMigrationId = migration.Id,
                        Message = $"Failed applying migration {migration.Id}: {ex.Message}"
                    };
                }
            }

            return new MigrationResult
            {
                Success = true,
                Provider = normalized,
                AppliedCount = appliedCount,
                SkippedCount = skippedCount,
                Message =
                    $"Schema migrations completed for {normalized}. Applied={appliedCount}, Skipped={skippedCount}, Total={migrations.Count}."
            };
        }
        catch (Exception ex)
        {
            return new MigrationResult
            {
                Success = false,
                Provider = normalized,
                Message = $"Schema migration failed: {ex.Message}"
            };
        }
    }

    private static DbConnection CreateConnection(string provider, string connectionString)
    {
        if (provider == "postgresql")
            return new NpgsqlConnection(connectionString);

        return new SqliteConnection(DatabaseRuntime.NormalizeSqliteConnectionString(connectionString));
    }

    private static async Task EnsureHistoryTableAsync(
        DbConnection connection,
        string provider,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = provider == "postgresql"
            ? """
              CREATE TABLE IF NOT EXISTS schema_migrations (
                  migration_id TEXT PRIMARY KEY,
                  checksum TEXT NOT NULL,
                  applied_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
              );
              """
            : """
              CREATE TABLE IF NOT EXISTS schema_migrations (
                  migration_id TEXT NOT NULL PRIMARY KEY,
                  checksum TEXT NOT NULL,
                  applied_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
              );
              """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Dictionary<string, string>> ReadAppliedAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT migration_id, checksum FROM schema_migrations";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result[reader.GetString(0)] = reader.GetString(1).Trim();
        return result;
    }

    private static async Task RecordMigrationAsync(
        DbConnection connection,
        MigrationFile migration,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO schema_migrations (migration_id, checksum) VALUES (@id, @checksum)";
        var p1 = command.CreateParameter();
        p1.ParameterName = "@id";
        p1.Value = migration.Id;
        command.Parameters.Add(p1);
        var p2 = command.CreateParameter();
        p2.ParameterName = "@checksum";
        p2.Value = migration.Hash;
        command.Parameters.Add(p2);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteSqliteMigrationAsync(
        DbConnection connection,
        string sql,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        var lines = sql.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var pending = new StringBuilder();

        async Task FlushPendingAsync()
        {
            var batch = pending.ToString();
            pending.Clear();
            if (string.IsNullOrWhiteSpace(batch))
                return;

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = 600;
            command.CommandText = batch;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            const string directive = "-- @if-column-missing ";
            if (!line.StartsWith(directive, StringComparison.OrdinalIgnoreCase))
            {
                pending.AppendLine(lines[i]);
                continue;
            }

            await FlushPendingAsync().ConfigureAwait(false);

            var target = line[directive.Length..].Trim();
            var parts = target.Split('.', StringSplitOptions.TrimEntries);
            if (parts.Length != 2
                || !Regex.IsMatch(parts[0], "^[A-Za-z_][A-Za-z0-9_]*$")
                || !Regex.IsMatch(parts[1], "^[A-Za-z_][A-Za-z0-9_]*$"))
                throw new InvalidOperationException(
                    $"Invalid SQLite conditional-column directive: {target}");

            var statement = new StringBuilder();
            var terminated = false;
            while (i + 1 < lines.Length &&
                   !lines[i + 1].TrimStart().StartsWith(directive, StringComparison.OrdinalIgnoreCase))
            {
                i++;
                statement.AppendLine(lines[i]);
                if (lines[i].Contains(';'))
                {
                    terminated = true;
                    break;
                }
            }

            if (!terminated)
                throw new InvalidOperationException(
                    $"Conditional SQLite migration statement for {target} is missing a semicolon.");

            if (!await SqliteColumnExistsAsync(
                    connection, transaction, parts[0], parts[1], cancellationToken).ConfigureAwait(false))
            {
                await using var alter = connection.CreateCommand();
                alter.Transaction = transaction;
                alter.CommandTimeout = 600;
                alter.CommandText = statement.ToString();
                await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await FlushPendingAsync().ConfigureAwait(false);
    }

    private static async Task<bool> SqliteColumnExistsAsync(
        DbConnection connection,
        DbTransaction transaction,
        string table,
        string column,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info({table})";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var name = reader.GetString(1);
            if (string.Equals(name, column, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
