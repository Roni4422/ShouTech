using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace ShouTech.App.Services;

/// <summary>
/// Validates the commercial-document schema provisioned by DBA migrations.
/// Schema definitions belong exclusively in dba/mig; this service performs no DDL.
/// </summary>
public static class DocSchema
{
    private static readonly object Gate = new();
    private static readonly HashSet<string> ValidatedDatabases = new(StringComparer.Ordinal);

    public static void Ensure()
    {
        var cs = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException("No database connection is configured.");

        var isPostgreSql = DatabaseRuntime.IsPostgreSql();
        var normalizedConnectionString = isPostgreSql
            ? cs
            : DatabaseRuntime.NormalizeSqliteConnectionString(cs);
        var provider = isPostgreSql ? "postgresql" : "sqlite";
        var cacheKey = provider + ":" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalizedConnectionString)));

        lock (Gate)
        {
            if (ValidatedDatabases.Contains(cacheKey)) return;

            using var connection = Open(cs);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = isPostgreSql
                ? """
                  SELECT COUNT(*)
                  FROM information_schema.tables
                  WHERE table_schema = current_schema()
                    AND table_name IN ('sales_documents', 'sales_document_lines',
                      'purchase_documents', 'purchase_document_lines', 'party_ledger', 'stock_movements',
                      'document_number_sequences')
                  """
                : """
                  SELECT COUNT(*)
                  FROM sqlite_master
                  WHERE type = 'table'
                    AND name IN ('sales_documents', 'sales_document_lines',
                      'purchase_documents', 'purchase_document_lines', 'party_ledger', 'stock_movements',
                      'document_number_sequences')
                  """;
            var count = Convert.ToInt32(cmd.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
            if (count != 7)
                throw new InvalidOperationException(
                    "Commercial-document schema is incomplete. Apply the provider migrations under dba/mig before using invoices.");

            ValidatedDatabases.Add(cacheKey);
        }
    }

    private static DbConnection Open(string connectionString)
    {
        DbConnection connection = DatabaseRuntime.IsPostgreSql()
            ? new NpgsqlConnection(connectionString)
            : new SqliteConnection(DatabaseRuntime.NormalizeSqliteConnectionString(connectionString));
        connection.Open();
        return connection;
    }
}
