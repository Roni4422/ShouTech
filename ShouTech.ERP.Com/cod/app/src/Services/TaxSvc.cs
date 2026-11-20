using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.App.Views;

using ShouTech.Domain;
namespace ShouTech.App.Services;

/// <summary>
/// Resolves tax settings and effective-dated rules for the active company.

/// </summary>
public static class TaxSvc
{
    // Tax is opt-in per company. Country selection alone never enables taxation.
    // Country rates are stored as effective-dated company rules, not hard-coded here.

    public sealed record TaxCodeRec(string Code, string Name, decimal Rate, string Country);

    public static bool IsEnabled
    {
        get
        {
            try { return ReadTaxEnabled(); }
            catch (Exception ex)
            {
                AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, ex, "TaxSvc.IsEnabled");
                return false;
            }
        }
    }

    public static decimal GetDefaultRate()
    {
        if (!IsEnabled) return 0m;

        try
        {
            var fromDb = TryLoadActiveRate();
            return fromDb ?? 0m;
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, ex, "TaxSvc.GetDefaultRate");
            return 0m;
        }
    }

    /// <summary>
    /// Persists the explicit company decision. A false value disables tax
    /// calculations without deleting historical tax rules or posted tax data.
    /// </summary>
    public static void SetTaxEnabled(string countryCode, bool enabled, string taxScheme = "", string registrationNumber = "")
    {
        var code = (countryCode ?? string.Empty).Trim().ToUpperInvariant();
        if (!TaxCountryPackCatalog.TryGet(code, out _))
            throw new ArgumentException("A supported country or jurisdiction code is required.", nameof(countryCode));

        var companyCode = AppState.CurrentCompanyCode;
        if (string.IsNullOrWhiteSpace(companyCode))
            throw new InvalidOperationException("An active company is required to configure taxation.");

        var cs = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException("The active company database is unavailable.");

        using var connection = Open(cs);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO company_tax_settings
                (company_code, country_code, tax_enabled, tax_scheme, registration_number, updated_at)
            VALUES ($company, $country, $enabled, $scheme, $registration, CURRENT_TIMESTAMP)
            ON CONFLICT(company_code) DO UPDATE SET
                country_code = excluded.country_code,
                tax_enabled = excluded.tax_enabled,
                tax_scheme = excluded.tax_scheme,
                registration_number = excluded.registration_number,
                updated_at = CURRENT_TIMESTAMP;
            """;
        Add(command, "$company", companyCode);
        Add(command, "$country", code);
        Add(command, "$enabled", enabled);
        Add(command, "$scheme", taxScheme ?? string.Empty);
        Add(command, "$registration", registrationNumber ?? string.Empty);
        command.ExecuteNonQuery();
    }

    private static bool ReadTaxEnabled()
    {
        var cs = DatabaseRuntime.GetConnectionString();
        var companyCode = AppState.CurrentCompanyCode;
        if (string.IsNullOrWhiteSpace(cs) || string.IsNullOrWhiteSpace(companyCode)) return false;

        using var connection = Open(cs);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT tax_enabled FROM company_tax_settings WHERE company_code = $company LIMIT 1;";
        Add(command, "$company", companyCode);
        var value = command.ExecuteScalar();
        return value is not null and not DBNull &&
            (value is bool flag ? flag : Convert.ToInt32(value, CultureInfo.InvariantCulture) == 1);
    }

    public static IReadOnlyList<TaxCodeRec> LoadCodes()
    {
        var list = new List<TaxCodeRec>();
        var cs = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(cs)) return list;

        try
        {
            if (!IsEnabled) return list;
            using var connection = Open(cs);
            using var cmd = connection.CreateCommand();
            var activeValue = DatabaseRuntime.IsPostgreSql() ? "TRUE" : "1";
            cmd.CommandText = $"""
                SELECT r.tax_code, COALESCE(r.tax_name, r.tax_code), COALESCE(r.rate, 0), COALESCE(r.country_code, '')
                FROM company_tax_rules r
                INNER JOIN company_tax_settings s ON s.company_code = r.company_code
                WHERE r.company_code = $company
                  AND r.country_code = s.country_code
                  AND r.is_active = {activeValue}
                  AND r.effective_from <= CURRENT_DATE
                  AND (r.effective_to IS NULL OR r.effective_to >= CURRENT_DATE)
                ORDER BY tax_code;
                """;
            Add(cmd, "$company", AppState.CurrentCompanyCode ?? "");
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new TaxCodeRec(
                    Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture) ?? "",
                    Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) ?? "",
                    Convert.ToDecimal(reader.GetValue(2), CultureInfo.InvariantCulture),
                    Convert.ToString(reader.GetValue(3), CultureInfo.InvariantCulture) ?? ""));
            }
        }
        catch (Exception exL77)
        {
            AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, exL77, "TaxSvc.LoadCodes");
            // Optional table.
        }
        return list;
    }

    /// <summary>
    /// Resolves a party code against the ONE canonical party master
    /// (parties, migration 061) in the current company. The returned Id is
    /// the party's ledger_id — the identity party_ledger and the document
    /// party_id columns carry. Inactive parties do not resolve (sweep
    /// L-8: they must not keep accruing postings), and the display name
    /// follows the card's English name with the Arabic name as fallback
    /// (sweep RP-5). A code is matched exactly; it is never parsed as a
    /// number (sweep C-2).
    /// </summary>
    public static (long? Id, string Name)? ResolveParty(string code, bool customer)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var cs = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(cs)) return null;
        var company = AppState.CurrentCompanyCode ?? "";

        try
        {
            using var connection = Open(cs);
            using var cmd = connection.CreateCommand();
            var deletedPredicate = DatabaseRuntime.IsPostgreSql() ? "is_deleted = FALSE" : "is_deleted = 0";
            var activePredicate = DatabaseRuntime.IsPostgreSql() ? "is_active = TRUE" : "is_active = 1";
            cmd.CommandText = $"""
                SELECT ledger_id, COALESCE(NULLIF(name_en, ''), name_ar, '')
                FROM parties
                WHERE company_id=$c AND kind=$kind AND code=$code AND {deletedPredicate} AND {activePredicate}
                LIMIT 1;
                """;
            Add(cmd, "$c", company);
            Add(cmd, "$kind", customer ? "C" : "S");
            Add(cmd, "$code", code.Trim());
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            if (reader.IsDBNull(0)) return null;
            return (Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                    Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) ?? "");
        }
        catch (Exception exL116)
        {
            AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, exL116, "TaxSvc.static");
            return null;
        }
    }

    public static (long? Id, string Name, decimal Price, string Unit, decimal TaxRate)? ResolveMaterial(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var cs = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(cs)) return null;
        var company = AppState.CurrentCompanyCode ?? "";

        try
        {
            using var connection = Open(cs);
            using var cmd = connection.CreateCommand();
            var activeMaterialPredicate = DatabaseRuntime.IsPostgreSql() ? "is_deleted = FALSE" : "COALESCE(is_deleted,0)=0";
            cmd.CommandText = $"""
                SELECT material_id, COALESCE(NULLIF(pos_name,''), NULLIF(name_ar,''), NULLIF(name_en,''), code),
                       COALESCE(price_retail, price_list, 0),
                       COALESCE(default_unit, unit1, ''),
                       COALESCE(tax_rate, 0)
                FROM materials
                WHERE company_id=$c
                  AND {activeMaterialPredicate}
                  AND (
                    code=$code OR alt_code=$code OR old_code=$code OR gtin=$code OR sku_external=$code
                    OR unit1_barcode=$code OR unit2_barcode=$code OR unit3_barcode=$code
                    OR unit4_barcode=$code OR unit5_barcode=$code
                    OR name_ar=$code OR name_en=$code OR pos_name=$code OR short_name=$code
                    OR search_name=$code OR name_ar LIKE $like OR name_en LIKE $like
                    OR pos_name LIKE $like OR short_name LIKE $like OR search_name LIKE $like
                  )
                ORDER BY CASE WHEN code=$code OR alt_code=$code OR old_code=$code OR gtin=$code
                    OR unit1_barcode=$code OR unit2_barcode=$code OR unit3_barcode=$code
                    OR unit4_barcode=$code OR unit5_barcode=$code THEN 0 ELSE 1 END,
                    name_ar
                LIMIT 1;
                """;
            Add(cmd, "$c", company);
            Add(cmd, "$code", code.Trim());
            Add(cmd, "$like", "%" + code.Trim() + "%");
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            return (
                Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) ?? "",
                Convert.ToDecimal(reader.GetValue(2), CultureInfo.InvariantCulture),
                Convert.ToString(reader.GetValue(3), CultureInfo.InvariantCulture) ?? "",
                IsEnabled ? Convert.ToDecimal(reader.GetValue(4), CultureInfo.InvariantCulture) : 0m);
        }
        catch (Exception exL168)
        {
            AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, exL168, "TaxSvc.static");
            return null;
        }
    }

    private static decimal? TryLoadActiveRate()
    {
        var cs = DatabaseRuntime.GetConnectionString();
        var companyCode = AppState.CurrentCompanyCode;
        if (string.IsNullOrWhiteSpace(cs) || string.IsNullOrWhiteSpace(companyCode)) return null;
        using var connection = Open(cs);
        using var command = connection.CreateCommand();
        var activeValue = DatabaseRuntime.IsPostgreSql() ? "TRUE" : "1";
        command.CommandText = $"""
            SELECT r.rate
            FROM company_tax_rules r
            INNER JOIN company_tax_settings s ON s.company_code = r.company_code
            WHERE r.company_code = $company
              AND r.is_active = {activeValue}
              AND r.is_default = {activeValue}
              AND s.tax_enabled = {activeValue}
              AND r.country_code = s.country_code
              AND r.effective_from <= CURRENT_DATE
              AND (r.effective_to IS NULL OR r.effective_to >= CURRENT_DATE)
            ORDER BY r.effective_from DESC
            LIMIT 1;
            """;
        Add(command, "$company", companyCode);
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToDecimal(value, CultureInfo.InvariantCulture);
    }

    private static DbConnection Open(string connectionString)
    {
        DbConnection connection = DatabaseRuntime.IsPostgreSql()
            ? new NpgsqlConnection(connectionString)
            : new SqliteConnection(DatabaseRuntime.NormalizeSqliteConnectionString(connectionString));
        connection.Open();
        return connection;
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        command.Parameters.Add(p);
    }
}
