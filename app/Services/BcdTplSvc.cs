using System;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.App;
using ShouTech.Domain;

namespace ShouTech.App.Services;

/// <summary>
/// Store for the composite barcode template (migration 126;
/// docs/OPTWIN_SECTIONS.md §3.4). One row per company; Load returns
/// the ACTIVE template only, so the scan consumers
/// (TaxSvc.ParseScan) see "no template" whenever parsing is off or
/// unconfigured. Same sync-ADO pattern as PrintSetSvc / PosTermRepo
/// over the app's central DatabaseRuntime.
/// </summary>
public static class BcdTplSvc
{
    /// <summary>The ACTIVE template, or null — the scan paths' view.</summary>
    public static BarcodeTemplate? Load()
    {
        var row = LoadRow();
        return row is { Active: true } ? row.Value.Template : null;
    }

    /// <summary>The stored row incl. its active flag — the Options
    /// editor's view (an inactive template must stay editable).</summary>
    public static (BarcodeTemplate Template, bool Active)? LoadRow()
    {
        var company = CompanyCode();
        if (company.Length == 0) return null;
        try
        {
            using var connection = Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT prefix, total_length, code_start, code_length,
                       wint_start, wint_length, wfrac_start, wfrac_length,
                       COALESCE(is_active, 1)
                FROM barcode_templates
                WHERE company_id = $company
                """;
            AddParam(cmd, "$company", company);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            var template = new BarcodeTemplate(
                Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture) ?? string.Empty,
                Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture),
                Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture),
                Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture),
                Convert.ToInt32(reader.GetValue(4), CultureInfo.InvariantCulture),
                Convert.ToInt32(reader.GetValue(5), CultureInfo.InvariantCulture),
                Convert.ToInt32(reader.GetValue(6), CultureInfo.InvariantCulture),
                Convert.ToInt32(reader.GetValue(7), CultureInfo.InvariantCulture));
            if (!template.IsValid(out _)) return null;
            var active = Convert.ToInt32(reader.GetValue(8), CultureInfo.InvariantCulture) != 0;
            return (template, active);
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, ex, "BcdTplSvc.LoadRow");
            return null;
        }
    }

    /// <summary>Saves (or, with null, removes) the company's template.
    /// Returns false when the template fails structural validation —
    /// the editor shows the reason; a bad template must never reach
    /// the scan paths.</summary>
    public static bool Save(BarcodeTemplate? template, bool active, string actor)
    {
        var company = CompanyCode();
        if (company.Length == 0) return false;
        try
        {
            using var connection = Open();
            if (template is null)
            {
                using var delete = connection.CreateCommand();
                delete.CommandText = "DELETE FROM barcode_templates WHERE company_id = $company";
                AddParam(delete, "$company", company);
                delete.ExecuteNonQuery();
                return true;
            }
            if (!template.IsValid(out _)) return false;
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO barcode_templates
                    (company_id, prefix, total_length, code_start, code_length,
                     wint_start, wint_length, wfrac_start, wfrac_length,
                     is_active, updated_by, updated_at)
                VALUES ($company, $prefix, $total, $cs, $cl, $ws, $wl, $fs, $fl, $active, $by, $at)
                ON CONFLICT (company_id) DO UPDATE SET
                    prefix = EXCLUDED.prefix, total_length = EXCLUDED.total_length,
                    code_start = EXCLUDED.code_start, code_length = EXCLUDED.code_length,
                    wint_start = EXCLUDED.wint_start, wint_length = EXCLUDED.wint_length,
                    wfrac_start = EXCLUDED.wfrac_start, wfrac_length = EXCLUDED.wfrac_length,
                    is_active = EXCLUDED.is_active, updated_by = EXCLUDED.updated_by,
                    updated_at = EXCLUDED.updated_at
                """;
            AddParam(cmd, "$company", company);
            AddParam(cmd, "$prefix", template.Prefix);
            AddParam(cmd, "$total", template.TotalLength);
            AddParam(cmd, "$cs", template.CodeStart);
            AddParam(cmd, "$cl", template.CodeLength);
            AddParam(cmd, "$ws", template.WeightIntStart);
            AddParam(cmd, "$wl", template.WeightIntLength);
            AddParam(cmd, "$fs", template.WeightFracStart);
            AddParam(cmd, "$fl", template.WeightFracLength);
            AddParam(cmd, "$active", active ? 1 : 0);
            AddParam(cmd, "$by", actor);
            AddParam(cmd, "$at", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            cmd.ExecuteNonQuery();
            return true;
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, ex, "BcdTplSvc.Save");
            return false;
        }
    }

    private static string CompanyCode() => (AppState.CurrentCompanyCode ?? string.Empty).Trim();

    private static System.Data.Common.DbConnection Open()
    {
        var connectionString = DatabaseRuntime.GetConnectionString();
        if (DatabaseRuntime.IsPostgreSql())
        {
            var pg = new NpgsqlConnection(connectionString);
            pg.Open();
            return pg;
        }
        var lite = new SqliteConnection(connectionString);
        lite.Open();
        return lite;
    }

    private static void AddParam(System.Data.Common.DbCommand cmd, string name, object value)
    {
        var param = cmd.CreateParameter();
        param.ParameterName = name;
        param.Value = value;
        cmd.Parameters.Add(param);
    }
}
