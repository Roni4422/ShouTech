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

    public sealed record TaxCodeRec(string Code, string Name, decimal Rate, string Country, string CalculationMode);

    public sealed record TaxSettingsRec(string CountryCode, bool Enabled, string Scheme, string RegistrationNumber);

    public sealed record TaxRuleRec(
        string Code, string Name, string TaxType, string Treatment, decimal Rate,
        string CalculationMode, bool IsCompound, bool IsRecoverable,
        string OutputAccountCode, string InputAccountCode,
        DateTime EffectiveFrom, DateTime? EffectiveTo,
        bool IsDefault, bool IsActive, string DataStatus, string SourceUrl, string SourceReference);

    public sealed record TaxRuleDraft(
        string Code,
        string Name,
        string TaxType,
        string Treatment,
        decimal Rate,
        DateTime EffectiveFrom,
        DateTime? EffectiveTo,
        bool IsRecoverable,
        bool IsCompound,
        bool IsDefault,
        string OutputAccountCode,
        string InputAccountCode,
        string CalculationMode = "Exclusive",
        string SourceUrl = "",
        string SourceReference = "");

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
        if (!AppHost.Permissions.Has(PermCat.Accounting.TaxManage))
            throw new UnauthorizedAccessException("The current user is not authorized to change company tax settings.");

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
        using var transaction = connection.BeginTransaction();
        try
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
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
            Add(command, "$scheme", string.IsNullOrWhiteSpace(taxScheme) &&
                TaxCountryPackCatalog.TryGet(code, out var pack) ? pack.Regime.ToString() : taxScheme ?? string.Empty);
            Add(command, "$registration", registrationNumber ?? string.Empty);
            command.ExecuteNonQuery();

            if (enabled)
            {
                var dateExpression = DatabaseRuntime.IsPostgreSql() ? "CAST($effectiveFrom AS date)" : "$effectiveFrom";
                var verifiedExpression = DatabaseRuntime.IsPostgreSql() ? "CAST($verifiedOn AS date)" : "$verifiedOn";
                foreach (var rule in TaxCountryPackCatalog.GetInitialRules(code))
                {
                    using var insert = connection.CreateCommand();
                    insert.Transaction = transaction;
                    insert.CommandText = $"""
                        INSERT INTO company_tax_rules
                            (company_code, country_code, tax_code, tax_name, tax_type, tax_treatment,
                             rate, calculation_mode, is_compound, is_recoverable, output_account_code,
                             input_account_code, effective_from, is_default, is_active, created_by,
                             source_url, source_reference, verified_on, data_status)
                        VALUES
                            ($company, $country, $taxCode, $taxName, $taxType, $treatment,
                             $rate, 'Exclusive', {(DatabaseRuntime.IsPostgreSql() ? "FALSE" : "0")},
                             $recoverable, $outputAccount, $inputAccount, {dateExpression},
                             $isDefault, {(DatabaseRuntime.IsPostgreSql() ? "TRUE" : "1")},
                             $createdBy, $sourceUrl, $sourceReference, {verifiedExpression},
                             'BASELINE_REVIEW_REQUIRED')
                        ON CONFLICT(company_code, tax_code, effective_from) DO NOTHING;
                        """;
                    Add(insert, "$company", companyCode);
                    Add(insert, "$country", code);
                    Add(insert, "$taxCode", rule.Code);
                    Add(insert, "$taxName", rule.Name);
                    Add(insert, "$taxType", rule.TaxType);
                    Add(insert, "$treatment", rule.Treatment);
                    Add(insert, "$rate", rule.Rate);
                    Add(insert, "$recoverable", rule.IsRecoverable);
                    Add(insert, "$outputAccount", rule.OutputAccountCode);
                    Add(insert, "$inputAccount", rule.InputAccountCode);
                    Add(insert, "$effectiveFrom", rule.EffectiveFrom);
                    Add(insert, "$isDefault", rule.IsDefault);
                    Add(insert, "$createdBy", Environment.UserName);
                    Add(insert, "$sourceUrl", rule.SourceUrl);
                    Add(insert, "$sourceReference", rule.SourceReference);
                    Add(insert, "$verifiedOn", DBNull.Value);
                    insert.ExecuteNonQuery();
                }
            }

            transaction.Commit();
        }
        catch
        {
            try { transaction.Rollback(); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Saves a company-specific, effective-dated tax rule. This is the
    /// configuration path for jurisdictions that require state, local,
    /// schedule, or other non-uniform rates. Posted tax snapshots are never
    /// rewritten when a rule is changed.
    /// </summary>
    public static void SaveRule(TaxRuleDraft rule)
    {
        if (rule is null) throw new ArgumentNullException(nameof(rule));
        var code = (rule.Code ?? string.Empty).Trim().ToUpperInvariant();
        var name = (rule.Name ?? string.Empty).Trim();
        var taxType = (rule.TaxType ?? string.Empty).Trim().ToUpperInvariant();
        var treatment = (rule.Treatment ?? string.Empty).Trim();
        var outputAccount = (rule.OutputAccountCode ?? string.Empty).Trim();
        var inputAccount = (rule.InputAccountCode ?? string.Empty).Trim();
        var calculationMode = (rule.CalculationMode ?? string.Empty).Trim();

        if (code.Length is < 1 or > 50 || name.Length is < 1 or > 200 ||
            taxType.Length is < 1 or > 40 || treatment.Length is < 1 or > 40)
            throw new ArgumentException("Tax code, name, type, and treatment are required and must fit the schema.", nameof(rule));
        if (rule.Rate < 0m || rule.Rate > 100000m)
            throw new ArgumentOutOfRangeException(nameof(rule), "Tax rate must be between 0 and 100000 percent.");
        if (calculationMode is not ("Exclusive" or "Inclusive"))
            throw new ArgumentException("Calculation mode must be Exclusive or Inclusive.", nameof(rule));
        if (rule.EffectiveTo.HasValue && rule.EffectiveTo.Value.Date < rule.EffectiveFrom.Date)
            throw new ArgumentException("Tax rule end date cannot precede its effective date.", nameof(rule));
        if (outputAccount.Length > 50 || inputAccount.Length > 50)
            throw new ArgumentException("Tax account codes cannot exceed 50 characters.", nameof(rule));

        if (!AppHost.Permissions.Has(PermCat.Accounting.TaxManage))
            throw new UnauthorizedAccessException("The current user is not authorized to manage tax rules.");
        if (!IsEnabled)
            throw new InvalidOperationException("Enable the company's tax system before adding tax rules.");

        var companyCode = AppState.CurrentCompanyCode;
        var cs = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(companyCode) || string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException("An active company database is required to save tax rules.");

        using var connection = Open(cs);
        using var transaction = connection.BeginTransaction();
        try
        {
            string countryCode;
            using (var settings = connection.CreateCommand())
            {
                settings.Transaction = transaction;
                settings.CommandText = "SELECT country_code FROM company_tax_settings WHERE company_code=$company AND tax_enabled = " +
                    (DatabaseRuntime.IsPostgreSql() ? "TRUE" : "1") + " LIMIT 1;";
                Add(settings, "$company", companyCode);
                countryCode = Convert.ToString(settings.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
            }
            if (string.IsNullOrWhiteSpace(countryCode))
                throw new InvalidOperationException("The active company has no enabled tax jurisdiction.");

            var dateExpression = DatabaseRuntime.IsPostgreSql() ? "CAST($effectiveFrom AS date)" : "$effectiveFrom";
            var endExpression = rule.EffectiveTo.HasValue
                ? (DatabaseRuntime.IsPostgreSql() ? "CAST($effectiveTo AS date)" : "$effectiveTo")
                : "NULL";
            var activeValue = DatabaseRuntime.IsPostgreSql() ? "TRUE" : "1";
            var inactiveValue = DatabaseRuntime.IsPostgreSql() ? "FALSE" : "0";

            if (rule.IsDefault)
            {
                using var clearDefault = connection.CreateCommand();
                clearDefault.Transaction = transaction;
                clearDefault.CommandText = $"""
                    UPDATE company_tax_rules
                    SET is_default = {inactiveValue}
                    WHERE company_code=$company AND country_code=$country
                      AND effective_from = {dateExpression}
                      AND is_default = {activeValue}
                      AND tax_code <> $taxCode;
                    """;
                Add(clearDefault, "$company", companyCode);
                Add(clearDefault, "$country", countryCode);
                Add(clearDefault, "$effectiveFrom", rule.EffectiveFrom.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                Add(clearDefault, "$taxCode", code);
                clearDefault.ExecuteNonQuery();
            }

            using (var save = connection.CreateCommand())
            {
                save.Transaction = transaction;
                save.CommandText = $"""
                    INSERT INTO company_tax_rules
                        (company_code, country_code, tax_code, tax_name, tax_type, tax_treatment,
                         rate, calculation_mode, is_compound, is_recoverable, output_account_code,
                         input_account_code, effective_from, effective_to, is_default, is_active,
                         created_by, source_url, source_reference, verified_on, data_status)
                    VALUES
                        ($company, $country, $taxCode, $taxName, $taxType, $treatment,
                         $rate, $calculationMode, $isCompound, $isRecoverable, $outputAccount,
                         $inputAccount, {dateExpression}, {endExpression}, $isDefault, {activeValue},
                         $createdBy, $sourceUrl, $sourceReference, NULL, 'USER_CONFIGURED')
                    ON CONFLICT(company_code, tax_code, effective_from) DO UPDATE SET
                        country_code=excluded.country_code,
                        tax_name=excluded.tax_name,
                        tax_type=excluded.tax_type,
                        tax_treatment=excluded.tax_treatment,
                        rate=excluded.rate,
                        calculation_mode=excluded.calculation_mode,
                        is_compound=excluded.is_compound,
                        is_recoverable=excluded.is_recoverable,
                        output_account_code=excluded.output_account_code,
                        input_account_code=excluded.input_account_code,
                        effective_to=excluded.effective_to,
                        is_default=excluded.is_default,
                        is_active=excluded.is_active,
                        created_by=excluded.created_by,
                        source_url=CASE WHEN excluded.source_url='' THEN company_tax_rules.source_url ELSE excluded.source_url END,
                        source_reference=CASE WHEN excluded.source_reference='' THEN company_tax_rules.source_reference ELSE excluded.source_reference END,
                        data_status='USER_CONFIGURED';
                    """;
                Add(save, "$company", companyCode);
                Add(save, "$country", countryCode);
                Add(save, "$taxCode", code);
                Add(save, "$taxName", name);
                Add(save, "$taxType", taxType);
                Add(save, "$treatment", treatment);
                Add(save, "$rate", rule.Rate);
                Add(save, "$calculationMode", calculationMode);
                Add(save, "$isCompound", rule.IsCompound);
                Add(save, "$isRecoverable", rule.IsRecoverable);
                Add(save, "$outputAccount", outputAccount);
                Add(save, "$inputAccount", inputAccount);
                Add(save, "$effectiveFrom", rule.EffectiveFrom.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                if (rule.EffectiveTo.HasValue)
                    Add(save, "$effectiveTo", rule.EffectiveTo.Value.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                Add(save, "$isDefault", rule.IsDefault);
                Add(save, "$createdBy", Environment.UserName);
                Add(save, "$sourceUrl", rule.SourceUrl ?? string.Empty);
                Add(save, "$sourceReference", rule.SourceReference ?? string.Empty);
                save.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        catch
        {
            try { transaction.Rollback(); } catch { }
            throw;
        }
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

    public static TaxSettingsRec? LoadSettings()
    {
        var cs = DatabaseRuntime.GetConnectionString();
        var company = AppState.CurrentCompanyCode;
        if (string.IsNullOrWhiteSpace(cs) || string.IsNullOrWhiteSpace(company))
            return null;

        using var connection = Open(cs);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT country_code, tax_enabled, COALESCE(tax_scheme, ''), COALESCE(registration_number, '')
            FROM company_tax_settings
            WHERE company_code=$company
            LIMIT 1;
            """;
        Add(command, "$company", company);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new TaxSettingsRec(
            Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture) ?? string.Empty,
            ToBoolean(reader.GetValue(1)),
            Convert.ToString(reader.GetValue(2), CultureInfo.InvariantCulture) ?? string.Empty,
            Convert.ToString(reader.GetValue(3), CultureInfo.InvariantCulture) ?? string.Empty);
    }

    public static IReadOnlyList<TaxRuleRec> LoadRules()
    {
        var list = new List<TaxRuleRec>();
        var cs = DatabaseRuntime.GetConnectionString();
        var company = AppState.CurrentCompanyCode;
        if (string.IsNullOrWhiteSpace(cs) || string.IsNullOrWhiteSpace(company))
            return list;

        using var connection = Open(cs);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT tax_code, COALESCE(tax_name, tax_code), COALESCE(tax_type, ''),
                   COALESCE(tax_treatment, ''), COALESCE(rate, 0), COALESCE(calculation_mode, 'Exclusive'),
                   is_compound, is_recoverable,
                   COALESCE(output_account_code, ''), COALESCE(input_account_code, ''),
                   effective_from, effective_to, is_default, is_active,
                   COALESCE(data_status, 'USER_CONFIGURED'),
                   COALESCE(source_url, ''), COALESCE(source_reference, '')
            FROM company_tax_rules
            WHERE company_code=$company
              AND country_code=(SELECT country_code FROM company_tax_settings WHERE company_code=$company LIMIT 1)
            ORDER BY tax_code, effective_from DESC;
            """;
        Add(command, "$company", company);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var effectiveFrom = Convert.ToDateTime(reader.GetValue(10), CultureInfo.InvariantCulture);
            var effectiveTo = reader.IsDBNull(11)
                ? (DateTime?)null
                : Convert.ToDateTime(reader.GetValue(11), CultureInfo.InvariantCulture);
            list.Add(new TaxRuleRec(
                Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture) ?? string.Empty,
                Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) ?? string.Empty,
                Convert.ToString(reader.GetValue(2), CultureInfo.InvariantCulture) ?? string.Empty,
                Convert.ToString(reader.GetValue(3), CultureInfo.InvariantCulture) ?? string.Empty,
                Convert.ToDecimal(reader.GetValue(4), CultureInfo.InvariantCulture),
                Convert.ToString(reader.GetValue(5), CultureInfo.InvariantCulture) ?? "Exclusive",
                ToBoolean(reader.GetValue(6)),
                ToBoolean(reader.GetValue(7)),
                Convert.ToString(reader.GetValue(8), CultureInfo.InvariantCulture) ?? string.Empty,
                Convert.ToString(reader.GetValue(9), CultureInfo.InvariantCulture) ?? string.Empty,
                effectiveFrom,
                effectiveTo,
                ToBoolean(reader.GetValue(12)),
                ToBoolean(reader.GetValue(13)),
                Convert.ToString(reader.GetValue(14), CultureInfo.InvariantCulture) ?? "USER_CONFIGURED",
                Convert.ToString(reader.GetValue(15), CultureInfo.InvariantCulture) ?? string.Empty,
                Convert.ToString(reader.GetValue(16), CultureInfo.InvariantCulture) ?? string.Empty));
        }
        return list.AsReadOnly();
    }

    private static bool ToBoolean(object value)
        => value is bool flag
            ? flag
            : value is not null and not DBNull &&
              Convert.ToInt32(value, CultureInfo.InvariantCulture) == 1;

    public static IReadOnlyList<TaxCodeRec> LoadCodes(DateTime? asOfDate = null)
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
            var asOfExpression = DatabaseRuntime.IsPostgreSql() ? "CAST($asOf AS date)" : "$asOf";
            cmd.CommandText = $"""
                SELECT r.tax_code, COALESCE(r.tax_name, r.tax_code), COALESCE(r.rate, 0), COALESCE(r.country_code, ''), COALESCE(r.calculation_mode, 'Exclusive')
                FROM company_tax_rules r
                INNER JOIN company_tax_settings s ON s.company_code = r.company_code
                WHERE r.company_code = $company
                  AND r.country_code = s.country_code
                  AND s.tax_enabled = {activeValue}
                  AND r.is_active = {activeValue}
                  AND COALESCE(r.data_status, 'USER_CONFIGURED') IN ('USER_CONFIGURED', 'VERIFIED', 'APPROVED')
                  AND r.effective_from <= {asOfExpression}
                  AND (r.effective_to IS NULL OR r.effective_to >= {asOfExpression})
                  AND NOT EXISTS (
                      SELECT 1
                      FROM company_tax_rules newer
                      WHERE newer.company_code = r.company_code
                        AND newer.country_code = r.country_code
                        AND newer.tax_code = r.tax_code
                        AND newer.is_active = {activeValue}
                        AND newer.effective_from <= {asOfExpression}
                        AND (newer.effective_to IS NULL OR newer.effective_to >= {asOfExpression})
                        AND newer.effective_from > r.effective_from
                  )
                ORDER BY r.is_default DESC, r.tax_code;
                """;
            Add(cmd, "$company", AppState.CurrentCompanyCode ?? "");
            Add(cmd, "$asOf", (asOfDate ?? DateTime.Today).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new TaxCodeRec(
                    Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture) ?? "",
                    Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) ?? "",
                    Convert.ToDecimal(reader.GetValue(2), CultureInfo.InvariantCulture),
                    Convert.ToString(reader.GetValue(3), CultureInfo.InvariantCulture) ?? "",
                    Convert.ToString(reader.GetValue(4), CultureInfo.InvariantCulture) ?? "Exclusive"));
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
              AND COALESCE(r.data_status, 'USER_CONFIGURED') IN ('USER_CONFIGURED', 'VERIFIED', 'APPROVED')
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
