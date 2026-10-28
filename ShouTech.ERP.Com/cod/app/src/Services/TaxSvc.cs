using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.App.Views;

namespace ShouTech.App.Services;

/// <summary>
/// Resolves default VAT / tax rates for the active company using tax_codes
/// and regional defaults (GCC, North Africa, Levant, EU-style).
/// </summary>
public static class TaxSvc
{
    private static readonly Dictionary<string, decimal> CountryDefaults = new(StringComparer.OrdinalIgnoreCase)
    {
        // GCC
        ["SA"] = 15m, ["AE"] = 5m, ["BH"] = 10m, ["OM"] = 5m, ["QA"] = 0m, ["KW"] = 0m,
        // Levant
        ["SY"] = 0m, ["JO"] = 16m, ["LB"] = 11m, ["PS"] = 16m, ["IQ"] = 0m,
        // North Africa
        ["EG"] = 14m, ["MA"] = 20m, ["TN"] = 19m, ["DZ"] = 19m, ["LY"] = 0m,
        // Europe (common)
        ["TR"] = 20m, ["DE"] = 19m, ["FR"] = 20m, ["GB"] = 20m, ["NL"] = 21m,
    };

    public sealed record TaxCodeRec(string Code, string Name, decimal Rate, string Country);

    public static decimal GetDefaultRate()
    {
        try
        {
            DocSchema.Ensure();
            var fromDb = TryLoadActiveRate();
            if (fromDb.HasValue) return fromDb.Value;

            var country = GetCompanyCountry();
            if (!string.IsNullOrWhiteSpace(country) &&
                CountryDefaults.TryGetValue(country.Trim(), out var rate))
                return rate;
        }
        catch
        {
            // Fall through to conservative default.
        }
        return 15m;
    }

    public static IReadOnlyList<TaxCodeRec> LoadCodes()
    {
        var list = new List<TaxCodeRec>();
        var cs = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(cs)) return list;

        try
        {
            using var connection = Open(cs);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT tax_code, COALESCE(tax_name, tax_code), COALESCE(rate, 0), COALESCE(country_code, '')
                FROM tax_codes
                WHERE COALESCE(is_active, 1) = 1
                ORDER BY tax_code;
                """;
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
        catch
        {
            // Optional table.
        }
        return list;
    }

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
            if (customer)
            {
                cmd.CommandText = """
                    SELECT customer_id, customer_name_ar FROM customers
                    WHERE company_id=$c AND customer_code=$code AND is_deleted=0 LIMIT 1;
                    """;
            }
            else
            {
                cmd.CommandText = """
                    SELECT supplier_id, supplier_name_ar FROM suppliers
                    WHERE company_id=$c AND supplier_code=$code AND is_deleted=0 LIMIT 1;
                    """;
            }
            Add(cmd, "$c", company);
            Add(cmd, "$code", code.Trim());
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            return (Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                    Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) ?? "");
        }
        catch
        {
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
                Convert.ToDecimal(reader.GetValue(4), CultureInfo.InvariantCulture));
        }
        catch
        {
            return null;
        }
    }

    private static decimal? TryLoadActiveRate()
    {
        var cs = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(cs)) return null;
        using var connection = Open(cs);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT rate FROM tax_codes
            WHERE COALESCE(is_active,1)=1 AND COALESCE(is_default,0)=1
            LIMIT 1;
            """;
        try
        {
            var v = cmd.ExecuteScalar();
            if (v is null or DBNull) return null;
            return Convert.ToDecimal(v, CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    private static string GetCompanyCountry()
    {
        var cs = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(cs)) return "SA";
        try
        {
            using var connection = Open(cs);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT COALESCE(country_code, country, 'SA') FROM companies
                WHERE company_id=$id LIMIT 1;
                """;
            Add(cmd, "$id", AppState.CurrentCompanyCode ?? "");
            var v = cmd.ExecuteScalar();
            return Convert.ToString(v, CultureInfo.InvariantCulture) ?? "SA";
        }
        catch
        {
            return "SA";
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

    private static void Add(DbCommand command, string name, object? value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        command.Parameters.Add(p);
    }
}
