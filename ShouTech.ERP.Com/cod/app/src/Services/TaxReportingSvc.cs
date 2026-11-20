using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.App.Views;

namespace ShouTech.App.Services;

/// <summary>
/// Read-only tax-period aggregation over immutable posted tax snapshots.
/// Amounts remain grouped by currency and direction; no cross-currency
/// totals are produced without an explicit reporting-currency conversion.
/// </summary>
public static class TaxReportingSvc
{
    public sealed record TaxPeriodRow(
        string TaxCode,
        string TaxType,
        string Treatment,
        decimal Rate,
        string Direction,
        bool IsRecoverable,
        string CurrencyCode,
        int TransactionCount,
        int DocumentCount,
        decimal TaxableAmount,
        decimal TaxAmount,
        decimal BaseTaxAmount);

    public sealed record TaxRegisterRow(
        DateTime DocumentDate,
        string DocumentKind,
        string DocumentNumber,
        int LineNumber,
        string TaxCode,
        string TaxType,
        string Treatment,
        decimal Rate,
        decimal TaxableAmount,
        decimal TaxAmount,
        bool IsRecoverable,
        string Direction,
        string TaxAccountCode,
        string CurrencyCode,
        decimal ExchangeRate,
        decimal BaseTaxAmount,
        string JournalNumber);

    public static IReadOnlyList<TaxPeriodRow> LoadPeriod(DateTime fromDate, DateTime toDate)
    {
        if (fromDate.Date > toDate.Date)
            throw new ArgumentException("Tax period start date cannot be after its end date.");

        if (!AppHost.Permissions.Has(PermCat.Accounting.TaxReportView))
            throw new UnauthorizedAccessException("The current user is not authorized to view tax reports.");

        var company = AppState.CurrentCompanyCode;
        var cs = DatabaseRuntime.GetConnectionString();
        var settings = TaxSvc.LoadSettings();
        if (string.IsNullOrWhiteSpace(company) || string.IsNullOrWhiteSpace(cs) ||
            settings is null || !settings.Enabled)
            return Array.Empty<TaxPeriodRow>();

        DbConnection connection = DatabaseRuntime.IsPostgreSql()
            ? new NpgsqlConnection(cs)
            : new SqliteConnection(DatabaseRuntime.NormalizeSqliteConnectionString(cs));
        using (connection)
        {
            connection.Open();
            using var command = connection.CreateCommand();
            var dateFrom = DatabaseRuntime.IsPostgreSql() ? "CAST($fromDate AS date)" : "$fromDate";
            var dateTo = DatabaseRuntime.IsPostgreSql() ? "CAST($toDate AS date)" : "$toDate";
            command.CommandText = $"""
                SELECT tax_code, tax_type, tax_treatment, tax_rate, tax_direction,
                       is_recoverable, currency_code, COUNT(*),
                       COUNT(DISTINCT source_doc_kind || ':' || CAST(source_doc_id AS TEXT)),
                       COALESCE(SUM(taxable_amount), 0),
                       COALESCE(SUM(tax_amount), 0), COALESCE(SUM(base_tax_amount), 0)
                FROM tax_transactions
                WHERE company_code=$company
                  AND country_code=$country
                  AND document_date >= {dateFrom}
                  AND document_date <= {dateTo}
                GROUP BY tax_code, tax_type, tax_treatment, tax_rate, tax_direction,
                         is_recoverable, currency_code
                ORDER BY tax_direction, tax_code, currency_code;
                """;
            Add(command, "$company", company);
            Add(command, "$country", settings.CountryCode);
            Add(command, "$fromDate", fromDate.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            Add(command, "$toDate", toDate.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

            var rows = new List<TaxPeriodRow>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new TaxPeriodRow(
                    Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture) ?? string.Empty,
                    Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) ?? string.Empty,
                    Convert.ToString(reader.GetValue(2), CultureInfo.InvariantCulture) ?? string.Empty,
                    Convert.ToDecimal(reader.GetValue(3), CultureInfo.InvariantCulture),
                    Convert.ToString(reader.GetValue(4), CultureInfo.InvariantCulture) ?? string.Empty,
                    ToBoolean(reader.GetValue(5)),
                    Convert.ToString(reader.GetValue(6), CultureInfo.InvariantCulture) ?? string.Empty,
                    Convert.ToInt32(reader.GetValue(7), CultureInfo.InvariantCulture),
                    Convert.ToInt32(reader.GetValue(8), CultureInfo.InvariantCulture),
                    Convert.ToDecimal(reader.GetValue(9), CultureInfo.InvariantCulture),
                    Convert.ToDecimal(reader.GetValue(10), CultureInfo.InvariantCulture),
                    Convert.ToDecimal(reader.GetValue(11), CultureInfo.InvariantCulture)));
            }
            return rows.AsReadOnly();
        }
    }

    /// <summary>
    /// Returns the immutable source-line tax register for the selected
    /// company and jurisdiction. The document date, not posting timestamp,
    /// determines the reporting period.
    /// </summary>
    public static IReadOnlyList<TaxRegisterRow> LoadRegister(DateTime fromDate, DateTime toDate)
    {
        if (fromDate.Date > toDate.Date)
            throw new ArgumentException("Tax period start date cannot be after its end date.");

        if (!AppHost.Permissions.Has(PermCat.Accounting.TaxReportView))
            throw new UnauthorizedAccessException("The current user is not authorized to view tax reports.");

        var company = AppState.CurrentCompanyCode;
        var cs = DatabaseRuntime.GetConnectionString();
        var settings = TaxSvc.LoadSettings();
        if (string.IsNullOrWhiteSpace(company) || string.IsNullOrWhiteSpace(cs) ||
            settings is null || !settings.Enabled)
            return Array.Empty<TaxRegisterRow>();

        DbConnection connection = DatabaseRuntime.IsPostgreSql()
            ? new NpgsqlConnection(cs)
            : new SqliteConnection(DatabaseRuntime.NormalizeSqliteConnectionString(cs));
        using (connection)
        {
            connection.Open();
            using var command = connection.CreateCommand();
            var dateFrom = DatabaseRuntime.IsPostgreSql() ? "CAST($fromDate AS date)" : "$fromDate";
            var dateTo = DatabaseRuntime.IsPostgreSql() ? "CAST($toDate AS date)" : "$toDate";
            command.CommandText = $"""
                SELECT document_date, source_doc_kind, source_doc_no, line_no,
                       tax_code, tax_type, tax_treatment, tax_rate, taxable_amount,
                       tax_amount, is_recoverable, tax_direction, tax_account_code,
                       currency_code, exchange_rate, base_tax_amount, journal_doc_no
                FROM tax_transactions
                WHERE company_code=$company
                  AND country_code=$country
                  AND document_date >= {dateFrom}
                  AND document_date <= {dateTo}
                ORDER BY document_date, source_doc_kind, source_doc_no, line_no, tax_code;
                """;
            Add(command, "$company", company);
            Add(command, "$country", settings.CountryCode);
            Add(command, "$fromDate", fromDate.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            Add(command, "$toDate", toDate.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

            var rows = new List<TaxRegisterRow>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new TaxRegisterRow(
                    ReadDate(reader.GetValue(0)),
                    Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) ?? string.Empty,
                    Convert.ToString(reader.GetValue(2), CultureInfo.InvariantCulture) ?? string.Empty,
                    Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture),
                    Convert.ToString(reader.GetValue(4), CultureInfo.InvariantCulture) ?? string.Empty,
                    Convert.ToString(reader.GetValue(5), CultureInfo.InvariantCulture) ?? string.Empty,
                    Convert.ToString(reader.GetValue(6), CultureInfo.InvariantCulture) ?? string.Empty,
                    Convert.ToDecimal(reader.GetValue(7), CultureInfo.InvariantCulture),
                    Convert.ToDecimal(reader.GetValue(8), CultureInfo.InvariantCulture),
                    Convert.ToDecimal(reader.GetValue(9), CultureInfo.InvariantCulture),
                    ToBoolean(reader.GetValue(10)),
                    Convert.ToString(reader.GetValue(11), CultureInfo.InvariantCulture) ?? string.Empty,
                    Convert.ToString(reader.GetValue(12), CultureInfo.InvariantCulture) ?? string.Empty,
                    Convert.ToString(reader.GetValue(13), CultureInfo.InvariantCulture) ?? string.Empty,
                    Convert.ToDecimal(reader.GetValue(14), CultureInfo.InvariantCulture),
                    Convert.ToDecimal(reader.GetValue(15), CultureInfo.InvariantCulture),
                    Convert.ToString(reader.GetValue(16), CultureInfo.InvariantCulture) ?? string.Empty));
            }
            return rows.AsReadOnly();
        }
    }

    private static DateTime ReadDate(object value)
        => value switch
        {
            DateOnly date => date.ToDateTime(TimeOnly.MinValue),
            DateTime dateTime => dateTime,
            _ => Convert.ToDateTime(value, CultureInfo.InvariantCulture)
        };

    private static bool ToBoolean(object value)
        => value is bool flag
            ? flag
            : value is not null and not DBNull &&
              Convert.ToInt32(value, CultureInfo.InvariantCulture) == 1;

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
