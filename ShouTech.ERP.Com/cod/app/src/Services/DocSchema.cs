using System;
using System.Data.Common;
using System.Globalization;
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
    private static bool _validated;

    public static void Ensure()
    {
        if (_validated) return;
        lock (Gate)
        {
            if (_validated) return;
            var cs = DatabaseRuntime.GetConnectionString();
            if (string.IsNullOrWhiteSpace(cs))
                throw new InvalidOperationException("No database connection is configured.");

            using var connection = Open(cs);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = DatabaseRuntime.IsPostgreSql()
                ? """
                  SELECT COUNT(*)
                  FROM information_schema.tables
                  WHERE table_schema = current_schema()
                    AND table_name IN ('sales_documents', 'sales_document_lines',
                      'purchase_documents', 'purchase_document_lines', 'party_ledger', 'stock_movements')
                  """
                : """
                  SELECT COUNT(*)
                  FROM sqlite_master
                  WHERE type = 'table'
                    AND name IN ('sales_documents', 'sales_document_lines',
                      'purchase_documents', 'purchase_document_lines', 'party_ledger', 'stock_movements')
                  """;
            var count = Convert.ToInt32(cmd.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
            if (count != 6)
                throw new InvalidOperationException(
                    "Commercial-document schema is incomplete. Apply the provider migrations under dba/mig before using invoices.");

            _validated = true;
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
