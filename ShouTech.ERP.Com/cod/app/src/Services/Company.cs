// ========================================================================
// FILE: Services/Company.cs
// PROJECT: SHOUTECH ERP — Company registry & creation
// Merged: CoModels + CoLocalStore + CoRegSvc + CoCreateSvc
// Interfaces ICoRegSvc / ICoCreateSvc remain in IServices.cs
// ========================================================================

#nullable enable

using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System;
using Microsoft.Data.Sqlite;
using Npgsql;
using NpgsqlTypes;
using ShouTech.App.Common;
using ShouTech.App.Configuration;

namespace ShouTech.App.Services
{

    // ========== CoModels.cs ==========
public sealed class NewCoReq
    {
        public string CompanyNameAr { get; set; } = string.Empty;
        public string CompanyNameEn { get; set; } = string.Empty;
        public string TaxNumber { get; set; } = string.Empty;
        public string License { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Website { get; set; } = string.Empty;
        public string Phone { get; set; } = string.Empty;
        public string Mobile { get; set; } = string.Empty;
        public string AddressLine1 { get; set; } = string.Empty;
        public string AddressLine2 { get; set; } = string.Empty;
        public string City { get; set; } = string.Empty;
        public string Country { get; set; } = string.Empty;
        public string Notes { get; set; } = string.Empty;
        public DateTime FiscalYearStart { get; set; }
        public DateTime FiscalYearEnd { get; set; }
        public string CurrencyCode { get; set; } = "SYP";
        public int DecimalPlaces { get; set; } = 2;
        public string CalendarType { get; set; } = "Gregorian";
        public string NumberGrouping { get; set; } = "3-3-3";
        public string LanguageCode { get; set; } = "ar-SY";
        public string CoaTemplateCode { get; set; } = "SIMPLE";
        public string DefaultWarehouseName { get; set; } = "Main";
        public string AdminUsername { get; set; } = string.Empty;
        public string AdminPassword { get; set; } = string.Empty;
        public string AdminFullName { get; set; } = string.Empty;
        public string DatabaseProvider { get; set; } = "SQLite";
        public bool PostgreSqlProducerApproval { get; set; }
        public string DbPath { get; set; } = string.Empty;
        public string AltDbPath { get; set; } = string.Empty;
        public string BackupPath { get; set; } = string.Empty;
        public string AltBackupPath { get; set; } = string.Empty;
        public bool AutoBackup { get; set; } = true;
        public string BackupSchedule { get; set; } = "Daily";
        public string BackupTime { get; set; } = "02:00";
    }

    public sealed class CoCreateResult
    {
        public bool Success { get; init; }
        public string Message { get; init; } = string.Empty;
        public string CompanyCode { get; init; } = string.Empty;
        public string CompanyNameAr { get; init; } = string.Empty;
        public bool SavedToDatabase { get; init; }
        public bool SavedLocally { get; init; }
    }

    public sealed class StoredCoRec
    {
        public Guid CompanyId { get; set; } = Guid.NewGuid();
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string NameEn { get; set; } = string.Empty;
        public string TaxNumber { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Website { get; set; } = string.Empty;
        public string Phone { get; set; } = string.Empty;
        public string Mobile { get; set; } = string.Empty;
        public string AddressLine1 { get; set; } = string.Empty;
        public string AddressLine2 { get; set; } = string.Empty;
        public string City { get; set; } = string.Empty;
        public string Country { get; set; } = string.Empty;
        public string License { get; set; } = string.Empty;
        public string Notes { get; set; } = string.Empty;
        public DateTime FiscalYearStart { get; set; }
        public DateTime FiscalYearEnd { get; set; }
        public string CurrencyCode { get; set; } = string.Empty;
        public int DecimalPlaces { get; set; } = 2;
        public string CalendarType { get; set; } = "Gregorian";
        public string NumberGrouping { get; set; } = "3-3-3";
        public string LanguageCode { get; set; } = "ar-SY";
        public string CoaTemplateCode { get; set; } = "SIMPLE";
        public string DefaultWarehouseName { get; set; } = string.Empty;
        public string AdminUsername { get; set; } = string.Empty;
        public string AdminPasswordHash { get; set; } = string.Empty;
        public string AdminPasswordSalt { get; set; } = string.Empty;
        public string AdminFullName { get; set; } = string.Empty;
        public string DatabaseProvider { get; set; } = "SQLite";
        public string DbPath { get; set; } = string.Empty;
        public string AltDbPath { get; set; } = string.Empty;
        public string BackupPath { get; set; } = string.Empty;
        public string AltBackupPath { get; set; } = string.Empty;
        public bool AutoBackup { get; set; } = true;
        public string BackupSchedule { get; set; } = "Daily";
        public string BackupTime { get; set; } = "02:00";
        public bool Enabled { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
		public DateTime? LastOpenedAt { get; set; }
    }

    public sealed class StoredCoFile
    {
        public List<StoredCoRec> Companies { get; set; } = new();
    }

    // ========== CoLocalStore.cs ==========
public static class CoLocalStore
    {
        private static readonly string StorePath = AppPaths.CompaniesIndex;

        public static async Task<List<StoredCoRec>> LoadAsync()
		{
			var all = await LoadAllAsync();

			return all
				.Where(c => c.Enabled)
				.OrderByDescending(c => c.LastOpenedAt ?? DateTime.MinValue)
				.ThenBy(c => c.Code)
				.ToList();
		}

        /// <summary>
        /// All index entries including soft-disabled (removed from window / pending restore).
        /// </summary>
        public static async Task<List<StoredCoRec>> LoadAllAsync()
        {
            try
            {
                if (!File.Exists(StorePath))
                    return new List<StoredCoRec>();

                var json = await File.ReadAllTextAsync(StorePath);
                var file = JsonSerializer.Deserialize<StoredCoFile>(json, JsonDefs.AppSetngRead);
                return file?.Companies?.ToList() ?? new List<StoredCoRec>();
            }
            catch
            {
                return new List<StoredCoRec>();
            }
        }

        public static async Task<List<StoredCoRec>> LoadDisabledAsync()
        {
            var all = await LoadAllAsync();
            return all.Where(c => !c.Enabled).ToList();
        }

        public static async Task SaveCompanyAsync(StoredCoRec company)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);

            var file = new StoredCoFile();
            if (File.Exists(StorePath))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(StorePath);
                    file = JsonSerializer.Deserialize<StoredCoFile>(json, JsonDefs.AppSetngRead) ?? new StoredCoFile();
                }
                catch
                {
                    file = new StoredCoFile();
                }
            }

            var existing = file.Companies.FirstOrDefault(c =>
                string.Equals(c.Code, company.Code, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
                file.Companies.Remove(existing);

            file.Companies.Add(company);

            var temp = StorePath + ".tmp";
            var output = JsonSerializer.Serialize(file, JsonDefs.Indented);
            await File.WriteAllTextAsync(temp, output);
            File.Move(temp, StorePath, overwrite: true);
        }
		
		public static async Task MarkAsLastOpenedAsync(string companyCode)
		{
			if (string.IsNullOrWhiteSpace(companyCode))
				return;

			var all = await LoadAllAsync();

			var company = all.FirstOrDefault(c =>
				string.Equals(
					c.Code,
					companyCode,
					StringComparison.OrdinalIgnoreCase));

			if (company == null || !company.Enabled)
				return;

			company.LastOpenedAt = DateTime.UtcNow;

			await ReplaceIndexAsync(all);
		}

        public static async Task<StoredCoRec?> FindUserCompanyAsync(string companyCode, string username, string password)
        {
            var companies = await LoadAsync();
            var company = companies.FirstOrDefault(c =>
                string.Equals(c.Code, companyCode, StringComparison.OrdinalIgnoreCase));

            if (company == null)
                return null;

            if (!string.Equals(company.AdminUsername, username, StringComparison.OrdinalIgnoreCase))
                return null;

            return PwdHash.Verify(password, company.AdminPasswordHash, company.AdminPasswordSalt)
                ? company
                : null;
        }

        public static async Task<StoredCoRec?> FindCompanyAdminByPasswordAsync(string companyCode, string password)
        {
            if (string.IsNullOrWhiteSpace(companyCode) || string.IsNullOrEmpty(password))
                return null;

            var companies = await LoadAsync();
            var company = companies.FirstOrDefault(c =>
                string.Equals(c.Code, companyCode, StringComparison.OrdinalIgnoreCase));

            if (company == null)
                return null;

            return PwdHash.Verify(password, company.AdminPasswordHash, company.AdminPasswordSalt)
                ? company
                : null;
        }

        public static async Task<StoredCoRec?> FindPasswordOwnerAsync(string password)
        {
            if (string.IsNullOrEmpty(password))
                return null;

            var companies = await LoadAsync();
            foreach (var company in companies)
            {
                if (PwdHash.Verify(password, company.AdminPasswordHash, company.AdminPasswordSalt))
                    return company;
            }

            return null;
        }

        public static async Task<string> GenerateNextCodeAsync()
        {
            var companies = await LoadAllAsync();
            var max = 0;

            foreach (var code in companies.Select(c => c.Code))
            {
                if (int.TryParse(code, out var n) && n > max)
                    max = n;
            }

            return (max + 1).ToString("000", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Soft-remove from company window: keep the record with Enabled=false so Restore can find it.
        /// </summary>
        public static async Task RemoveFromIndexAsync(string companyCode)
        {
            if (string.IsNullOrWhiteSpace(companyCode))
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            var file = new StoredCoFile();
            if (File.Exists(StorePath))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(StorePath);
                    file = JsonSerializer.Deserialize<StoredCoFile>(json, JsonDefs.AppSetngRead) ?? new StoredCoFile();
                }
                catch
                {
                    file = new StoredCoFile();
                }
            }

            var hit = false;
            foreach (var c in file.Companies)
            {
                if (string.Equals(c.Code, companyCode, StringComparison.OrdinalIgnoreCase))
                {
                    c.Enabled = false;
                    hit = true;
                }
            }

            // If never indexed, still write a tombstone so restore UI can list it after full-delete paths.
            if (!hit)
            {
                file.Companies.Add(new StoredCoRec
                {
                    Code = companyCode,
                    Name = companyCode,
                    NameEn = companyCode,
                    Enabled = false
                });
            }

            var temp = StorePath + ".tmp";
            var output = JsonSerializer.Serialize(file, JsonDefs.Indented);
            await File.WriteAllTextAsync(temp, output);
            File.Move(temp, StorePath, overwrite: true);
        }

        public static async Task RestoreInIndexAsync(string companyCode)
        {
            if (string.IsNullOrWhiteSpace(companyCode))
                return;

            var all = await LoadAllAsync();
            var rec = all.FirstOrDefault(c =>
                string.Equals(c.Code, companyCode, StringComparison.OrdinalIgnoreCase));
            if (rec == null)
                return;
            rec.Enabled = true;
            await SaveCompanyAsync(rec);
        }

        public static async Task ReplaceIndexAsync(IEnumerable<StoredCoRec> companies)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            var file = new StoredCoFile
            {
                Companies = companies?.ToList() ?? new List<StoredCoRec>()
            };
            var temp = StorePath + ".tmp";
            var output = JsonSerializer.Serialize(file, JsonDefs.Indented);
            await File.WriteAllTextAsync(temp, output);
            File.Move(temp, StorePath, overwrite: true);
            await Task.CompletedTask;
        }

    }

    // ========== CoRegSvc.cs ==========
/// <summary>
    /// Company registry: database + local index (companies.json).
    /// </summary>
    public class CoRegSvc : ICoRegSvc
    {
        public async Task<List<CompanyInfo>> GetCompaniesAsync()
        {
            var merged = new Dictionary<string, CompanyInfo>(StringComparer.OrdinalIgnoreCase);

            foreach (var company in await LoadFromDatabaseAsync())
            {
                if (!string.IsNullOrWhiteSpace(company.Code))
                    merged[company.Code] = company;
            }

            foreach (var local in await CoLocalStore.LoadAsync())
            {
                if (string.IsNullOrWhiteSpace(local.Code) || merged.ContainsKey(local.Code))
                    continue;

                merged[local.Code] = new CompanyInfo
                {
                    Code = local.Code,
                    Name = local.Name,
                    NameEn = local.NameEn,
                    Database = string.IsNullOrWhiteSpace(local.DbPath) ? "LOCAL" : local.DbPath,
                    Enabled = local.Enabled,
                    IsDefault = false
                };
            }

            // Configuration defaults are not company registrations. Only return
            // companies discovered in the connected registry or local index.
            return merged.Values.OrderBy(c => c.Code).ToList();
        }

        public async Task<bool> HasCompaniesAsync()
        {
            var companies = await GetCompaniesAsync();
            return companies.Any(c => c?.Enabled == true);
        }

        /// <summary>
        /// Rebuild local index from database + keep local-only entries that are still enabled.
        /// Returns the number of indexed companies.
        /// </summary>
        public static async Task<int> RebuildCompanyIndexAsync()
        {
            var fromDb = await LoadFromDatabaseAsync();
            var localAll = await CoLocalStore.LoadAllAsync();
            var byCode = new Dictionary<string, StoredCoRec>(StringComparer.OrdinalIgnoreCase);

            // Keep disabled (soft-removed from window) entries so Restore still sees them.
            foreach (var c in localAll)
                byCode[c.Code] = c;

            foreach (var db in fromDb)
            {
                if (string.IsNullOrWhiteSpace(db.Code))
                    continue;

                if (byCode.TryGetValue(db.Code, out var existing))
                {
                    existing.Name = db.Name ?? existing.Name;
                    existing.NameEn = db.NameEn ?? existing.NameEn;
                    // Active in DB → must be enabled in index
                    existing.Enabled = true;
                }
                else
                {
                    byCode[db.Code] = new StoredCoRec
                    {
                        Code = db.Code,
                        Name = db.Name ?? db.Code,
                        NameEn = db.NameEn ?? db.Name ?? db.Code,
                        Enabled = true
                    };
                }
            }

            var list = byCode.Values.OrderBy(c => c.Code).ToList();
            await CoLocalStore.ReplaceIndexAsync(list);
            return list.Count;
        }

        /// <summary>
        /// Soft-delete in database (is_deleted) then remove from local index.
        /// </summary>
        public static async Task<bool> DeleteCompanyFullyAsync(string companyCode)
        {
            if (string.IsNullOrWhiteSpace(companyCode))
                return false;

            var deletedInDb = await TrySoftDeleteInDatabaseAsync(companyCode);
            await CoLocalStore.RemoveFromIndexAsync(companyCode);
            return deletedInDb;
        }

        private static async Task<bool> TrySoftDeleteInDatabaseAsync(string companyCode)
        {
            try
            {
                var connectionString = ResolveConnectionString();
                if (string.IsNullOrWhiteSpace(connectionString))
                    return false;

                var provider = DatabaseRuntime.GetProvider();
                if (DatabaseRuntime.IsSqlite(provider))
                {
                    SqliteSchemaBootstrapper.EnsureDatabase(connectionString);
                    await using var connection = new SqliteConnection(connectionString);
                    await connection.OpenAsync();
                    await using var cmd = new SqliteCommand(
                        "UPDATE companies SET is_deleted=1, is_active=0 WHERE company_code=@code",
                        connection);
                    cmd.Parameters.AddWithValue("@code", companyCode);
                    return await cmd.ExecuteNonQueryAsync() > 0;
                }

                if (DatabaseRuntime.IsPostgreSql(provider))
                {
                    await using var connection = new NpgsqlConnection(connectionString);
                    await connection.OpenAsync();
                    await using var cmd = new NpgsqlCommand("""
                        UPDATE public.companies
                        SET is_deleted = TRUE, is_active = FALSE
                        WHERE company_code = @code
                        """, connection);
                    cmd.Parameters.AddWithValue("code", companyCode);
                    return await cmd.ExecuteNonQueryAsync() > 0;
                }
            }
            catch
            {
                return false;
            }

            return false;
        }


        /// <summary>
        /// Soft-deleted companies still present in the database.
        /// </summary>
        public static async Task<List<CompanyInfo>> GetDeletedCompaniesAsync()
        {
            var merged = new Dictionary<string, CompanyInfo>(StringComparer.OrdinalIgnoreCase);

            // 1) Soft-deleted rows in database
            try
            {
                var connectionString = ResolveConnectionString();
                if (!string.IsNullOrWhiteSpace(connectionString))
                {
                    var provider = DatabaseRuntime.GetProvider();
                    if (DatabaseRuntime.IsSqlite(provider))
                    {
                        SqliteSchemaBootstrapper.EnsureDatabase(connectionString);
                        await using var connection = new SqliteConnection(connectionString);
                        await connection.OpenAsync();
                        await using var command = new SqliteCommand("""
                            SELECT company_code, company_name_ar, company_name_en
                            FROM companies
                            WHERE is_deleted=1
                            ORDER BY company_name_ar
                            LIMIT 1000
                            """, connection);
                        await using var reader = await command.ExecuteReaderAsync();
                        while (await reader.ReadAsync())
                        {
                            var code = reader["company_code"]?.ToString() ?? string.Empty;
                            if (string.IsNullOrWhiteSpace(code)) continue;
                            merged[code] = new CompanyInfo
                            {
                                Code = code,
                                Name = reader["company_name_ar"]?.ToString() ?? string.Empty,
                                NameEn = reader["company_name_en"]?.ToString() ?? string.Empty,
                                Enabled = false,
                                IsDefault = false
                            };
                        }
                    }
                    else if (DatabaseRuntime.IsPostgreSql(provider))
                    {
                        await using var connection = new NpgsqlConnection(connectionString);
                        await connection.OpenAsync();
                        await using var command = new NpgsqlCommand("""
                            SELECT company_code, company_name_ar, company_name_en
                            FROM public.companies
                            WHERE COALESCE(is_deleted, FALSE) = TRUE
                            ORDER BY company_name_ar
                            LIMIT 1000;
                            """, connection);
                        await using var reader = await command.ExecuteReaderAsync();
                        while (await reader.ReadAsync())
                        {
                            var code = reader["company_code"]?.ToString() ?? string.Empty;
                            if (string.IsNullOrWhiteSpace(code)) continue;
                            merged[code] = new CompanyInfo
                            {
                                Code = code,
                                Name = reader["company_name_ar"]?.ToString() ?? string.Empty,
                                NameEn = reader["company_name_en"]?.ToString() ?? string.Empty,
                                Enabled = false,
                                IsDefault = false
                            };
                        }
                    }
                }
            }
            catch
            {
                // continue with local disabled entries
            }

            // 2) Local index entries marked Enabled=false (delete from window)
            try
            {
                foreach (var local in await CoLocalStore.LoadDisabledAsync())
                {
                    if (string.IsNullOrWhiteSpace(local.Code) || merged.ContainsKey(local.Code))
                        continue;
                    merged[local.Code] = new CompanyInfo
                    {
                        Code = local.Code,
                        Name = local.Name,
                        NameEn = local.NameEn,
                        Enabled = false,
                        IsDefault = false
                    };
                }
            }
            catch
            {
            }

            return merged.Values
                .OrderBy(c => c.Name)
                .ToList();
        }

        /// <summary>
        /// Restore a soft-deleted company: clear delete flags and re-index.
        /// </summary>
        public static async Task<bool> RestoreCompanyAsync(string companyCode)
        {
            if (string.IsNullOrWhiteSpace(companyCode))
                return false;

            _ = await TryRestoreInDatabaseAsync(companyCode);
            await CoLocalStore.RestoreInIndexAsync(companyCode);
            // Rebuild merges active DB rows; preserves restored local entry.
            await RebuildCompanyIndexAsync();
            // If only local-disabled (never soft-deleted in DB), still success.
            return true;
        }

        private static async Task<bool> TryRestoreInDatabaseAsync(string companyCode)
        {
            try
            {
                var connectionString = ResolveConnectionString();
                if (string.IsNullOrWhiteSpace(connectionString))
                    return false;

                var provider = DatabaseRuntime.GetProvider();
                if (DatabaseRuntime.IsSqlite(provider))
                {
                    SqliteSchemaBootstrapper.EnsureDatabase(connectionString);
                    await using var connection = new SqliteConnection(connectionString);
                    await connection.OpenAsync();
                    await using var cmd = new SqliteCommand(
                        "UPDATE companies SET is_deleted=0, is_active=1 WHERE company_code=@code AND is_deleted=1",
                        connection);
                    cmd.Parameters.AddWithValue("@code", companyCode);
                    return await cmd.ExecuteNonQueryAsync() > 0;
                }

                if (DatabaseRuntime.IsPostgreSql(provider))
                {
                    await using var connection = new NpgsqlConnection(connectionString);
                    await connection.OpenAsync();
                    await using var cmd = new NpgsqlCommand("""
                        UPDATE public.companies
                        SET is_deleted = FALSE, is_active = TRUE
                        WHERE company_code = @code AND COALESCE(is_deleted, FALSE) = TRUE
                        """, connection);
                    cmd.Parameters.AddWithValue("code", companyCode);
                    return await cmd.ExecuteNonQueryAsync() > 0;
                }
            }
            catch
            {
                return false;
            }

            return false;
        }


        /// <summary>
        /// Update company profile fields in database + local index (enterprise in-place edit).
        /// </summary>
        public static async Task<bool> UpdateCompanyProfileAsync(StoredCoRec profile)
        {
            if (profile == null || string.IsNullOrWhiteSpace(profile.Code))
                return false;

            var dbOk = await TryUpdateInDatabaseAsync(profile);

            // Always persist metadata to local index for offline / registry views.
            var existingList = await CoLocalStore.LoadAllAsync();
            var existing = existingList.FirstOrDefault(c =>
                string.Equals(c.Code, profile.Code, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                existing.CompanyId = profile.CompanyId;
                existing.Name = profile.Name;
                existing.NameEn = profile.NameEn;
                existing.TaxNumber = profile.TaxNumber;
                existing.Website = profile.Website;
                existing.Email = profile.Email;
                existing.Phone = profile.Phone;
                existing.Mobile = profile.Mobile;
                existing.AddressLine1 = profile.AddressLine1;
                existing.AddressLine2 = profile.AddressLine2;
                existing.City = profile.City;
                existing.Country = profile.Country;
                existing.CurrencyCode = profile.CurrencyCode;
                existing.License = profile.License;
                existing.Notes = profile.Notes;
                existing.FiscalYearStart = profile.FiscalYearStart;
                existing.FiscalYearEnd = profile.FiscalYearEnd;
                existing.DecimalPlaces = profile.DecimalPlaces;
                existing.CalendarType = profile.CalendarType;
                existing.NumberGrouping = profile.NumberGrouping;
                existing.LanguageCode = profile.LanguageCode;
                existing.CoaTemplateCode = profile.CoaTemplateCode;
                existing.DefaultWarehouseName = profile.DefaultWarehouseName;
                existing.AdminUsername = profile.AdminUsername;
                existing.AdminFullName = profile.AdminFullName;
                existing.DatabaseProvider = profile.DatabaseProvider;
                existing.DbPath = profile.DbPath;
                existing.AltDbPath = profile.AltDbPath;
                existing.BackupPath = profile.BackupPath;
                existing.AltBackupPath = profile.AltBackupPath;
                existing.AutoBackup = profile.AutoBackup;
                existing.BackupSchedule = profile.BackupSchedule;
                existing.BackupTime = profile.BackupTime;
                await CoLocalStore.SaveCompanyAsync(existing);
            }
            else
            {
                profile.Enabled = true;
                await CoLocalStore.SaveCompanyAsync(profile);
            }

            return dbOk;
        }

        private static async Task<bool> TryUpdateInDatabaseAsync(StoredCoRec profile)
        {
            try
            {
                var provider = string.IsNullOrWhiteSpace(profile.DatabaseProvider)
                    ? SchemaMigrator.NormalizeProvider(DatabaseRuntime.GetProvider())
                    : SchemaMigrator.NormalizeProvider(profile.DatabaseProvider);
                var connectionString = provider switch
                {
                    "sqlite" when !string.IsNullOrWhiteSpace(profile.DbPath) =>
                        DatabaseRuntime.NormalizeSqliteConnectionString(profile.DbPath.Trim()),
                    "postgresql" when !string.IsNullOrWhiteSpace(profile.DbPath) &&
                                      profile.DbPath.Contains('=', StringComparison.Ordinal) =>
                        profile.DbPath.Trim(),
                    _ => ResolveConnectionString()
                };
                if (string.IsNullOrWhiteSpace(connectionString))
                {
                    AppHost.Logging.LogWarning(
                        "CoRegSvc",
                        $"Database update skipped for company {profile.Code}: no connection string is configured for provider {provider}.");
                    return false;
                }

                if (provider == "sqlite")
                {
                    SqliteSchemaBootstrapper.EnsureDatabase(connectionString);
                    await using var connection = new SqliteConnection(connectionString);
                    await connection.OpenAsync();
                    await using var cmd = new SqliteCommand("""
                        UPDATE companies SET
                            company_name_ar = @nameAr,
                            company_name_en = @nameEn,
                            email = @email,
                            phone = @phone,
                            mobile = @mobile,
                            address_line1 = @address,
                            commercial_registration = @tax,
                            currency_code = @currency,
                            language_code = @language
                        WHERE company_code = @code AND is_deleted = 0
                        """, connection);
                    cmd.Parameters.AddWithValue("@nameAr", profile.Name ?? "");
                    cmd.Parameters.AddWithValue("@nameEn", profile.NameEn ?? profile.Name ?? "");
                    cmd.Parameters.AddWithValue("@email", (object?)profile.Email ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@phone", (object?)profile.Phone ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@mobile", (object?)profile.Mobile ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@address", (object?)profile.AddressLine1 ?? DBNull.Value);
                    var registration = string.IsNullOrWhiteSpace(profile.TaxNumber)
                        ? profile.License
                        : profile.TaxNumber;
                    cmd.Parameters.AddWithValue("@tax",
                        string.IsNullOrWhiteSpace(registration) ? DBNull.Value : registration.Trim());
                    cmd.Parameters.AddWithValue("@currency", profile.CurrencyCode ?? string.Empty);
                    cmd.Parameters.AddWithValue("@language", profile.LanguageCode ?? string.Empty);
                    cmd.Parameters.AddWithValue("@code", profile.Code);
                    if (await cmd.ExecuteNonQueryAsync() > 0)
                        return true;

                    await using var insert = new SqliteCommand("""
                        INSERT INTO companies
                            (company_id, company_code, company_name_ar, company_name_en,
                             email, phone, mobile, address_line1, commercial_registration,
                             currency_code, language_code)
                        VALUES
                            (@id, @code, @nameAr, @nameEn, @email, @phone, @mobile,
                             @address, @tax, @currency, @language)
                        ON CONFLICT(company_code) DO NOTHING
                        """, connection);
                    insert.Parameters.AddWithValue("@id", profile.CompanyId.ToString());
                    insert.Parameters.AddWithValue("@code", profile.Code);
                    insert.Parameters.AddWithValue("@nameAr", profile.Name ?? string.Empty);
                    insert.Parameters.AddWithValue("@nameEn", profile.NameEn ?? string.Empty);
                    insert.Parameters.AddWithValue("@email", (object?)profile.Email ?? DBNull.Value);
                    insert.Parameters.AddWithValue("@phone", (object?)profile.Phone ?? DBNull.Value);
                    insert.Parameters.AddWithValue("@mobile", (object?)profile.Mobile ?? DBNull.Value);
                    insert.Parameters.AddWithValue("@address", (object?)profile.AddressLine1 ?? DBNull.Value);
                    insert.Parameters.AddWithValue("@tax",
                        string.IsNullOrWhiteSpace(registration) ? DBNull.Value : registration.Trim());
                    insert.Parameters.AddWithValue("@currency", profile.CurrencyCode ?? string.Empty);
                    insert.Parameters.AddWithValue("@language", profile.LanguageCode ?? string.Empty);
                    var inserted = await insert.ExecuteNonQueryAsync() > 0;
                    if (!inserted)
                        AppHost.Logging.LogWarning(
                            "CoRegSvc",
                            $"Database update found company {profile.Code}, but it is not active and could not be updated.");
                    return inserted;
                }

                if (provider == "postgresql")
                {
                    await using var connection = new NpgsqlConnection(connectionString);
                    await connection.OpenAsync();
                    await using var cmd = new NpgsqlCommand("""
                        UPDATE public.companies SET
                            company_name_ar = @nameAr,
                            company_name_en = @nameEn,
                            email = @email,
                            phone = @phone,
                            mobile = @mobile,
                            address_line1 = @address,
                            commercial_registration = @tax,
                            currency_code = @currency,
                            language_code = @language
                        WHERE company_code = @code AND COALESCE(is_deleted, FALSE) = FALSE
                        """, connection);
                    cmd.Parameters.AddWithValue("nameAr", profile.Name ?? "");
                    cmd.Parameters.AddWithValue("nameEn", profile.NameEn ?? profile.Name ?? "");
                    cmd.Parameters.AddWithValue("email", (object?)profile.Email ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("phone", (object?)profile.Phone ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("mobile", (object?)profile.Mobile ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("address", (object?)profile.AddressLine1 ?? DBNull.Value);
                    var registration = string.IsNullOrWhiteSpace(profile.TaxNumber)
                        ? profile.License
                        : profile.TaxNumber;
                    cmd.Parameters.AddWithValue("tax",
                        string.IsNullOrWhiteSpace(registration) ? DBNull.Value : registration.Trim());
                    cmd.Parameters.AddWithValue("currency", profile.CurrencyCode ?? string.Empty);
                    cmd.Parameters.AddWithValue("language", profile.LanguageCode ?? string.Empty);
                    cmd.Parameters.AddWithValue("code", profile.Code);
                    if (await cmd.ExecuteNonQueryAsync() > 0)
                        return true;

                    await using var insert = new NpgsqlCommand("""
                        INSERT INTO public.companies
                            (company_id, company_code, company_name_ar, company_name_en,
                             email, phone, mobile, address_line1, commercial_registration,
                             currency_code, language_code)
                        VALUES
                            (@id, @code, @nameAr, @nameEn, @email, @phone, @mobile,
                             @address, @tax, @currency, @language)
                        ON CONFLICT(company_code) DO NOTHING
                        """, connection);
                    insert.Parameters.AddWithValue("id", profile.CompanyId);
                    insert.Parameters.AddWithValue("code", profile.Code);
                    insert.Parameters.AddWithValue("nameAr", profile.Name ?? string.Empty);
                    insert.Parameters.AddWithValue("nameEn", profile.NameEn ?? string.Empty);
                    insert.Parameters.AddWithValue("email", (object?)profile.Email ?? DBNull.Value);
                    insert.Parameters.AddWithValue("phone", (object?)profile.Phone ?? DBNull.Value);
                    insert.Parameters.AddWithValue("mobile", (object?)profile.Mobile ?? DBNull.Value);
                    insert.Parameters.AddWithValue("address", (object?)profile.AddressLine1 ?? DBNull.Value);
                    insert.Parameters.AddWithValue("tax",
                        string.IsNullOrWhiteSpace(registration) ? DBNull.Value : registration.Trim());
                    insert.Parameters.AddWithValue("currency", profile.CurrencyCode ?? string.Empty);
                    insert.Parameters.AddWithValue("language", profile.LanguageCode ?? string.Empty);
                    var inserted = await insert.ExecuteNonQueryAsync() > 0;
                    if (!inserted)
                        AppHost.Logging.LogWarning(
                            "CoRegSvc",
                            $"Database update found company {profile.Code}, but it is not active and could not be updated.");
                    return inserted;
                }
            }
            catch (Exception ex)
            {
                AppHost.Logging.LogError(
                    "CoRegSvc",
                    $"Database update failed for company {profile.Code}.",
                    ex);
                return false;
            }

            return false;
        }

private static string? ResolveConnectionString()
        {
            try
            {
                return DatabaseRuntime.GetConnectionString();
            }
            catch
            {
                var connectionString = AppCfgSvc.Current?.ConnectionStrings?
                    .FirstOrDefault(kvp => kvp.Key.Equals("Default", StringComparison.OrdinalIgnoreCase)).Value?.ConnectionString;
                if (string.IsNullOrWhiteSpace(connectionString))
                    connectionString = AppCfgSvc.Current?.ConnectionStrings?.Values?.FirstOrDefault()?.ConnectionString;
                return connectionString;
            }
        }

        private static async Task<List<CompanyInfo>> LoadFromDatabaseAsync()
        {
            var result = new List<CompanyInfo>();
            try
            {
                var connectionString = ResolveConnectionString();
                if (string.IsNullOrWhiteSpace(connectionString))
                    return result;

                var provider = DatabaseRuntime.GetProvider();
                if (DatabaseRuntime.IsSqlite(provider))
                    return await LoadFromSqliteAsync(connectionString);

                if (DatabaseRuntime.IsPostgreSql(provider))
                    return await LoadFromPostgreSqlAsync(connectionString);
            }
            catch
            {
                return result;
            }

            return result;
        }

        private static async Task<List<CompanyInfo>> LoadFromPostgreSqlAsync(string connectionString)
        {
            var result = new List<CompanyInfo>();
            try
            {
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand("""
                    SELECT company_code, company_name_ar, company_name_en
                    FROM public.companies
                    WHERE COALESCE(is_deleted, FALSE) = FALSE AND COALESCE(is_active, TRUE) = TRUE
                    ORDER BY company_name_ar
                    LIMIT 1000;
                    """, connection);
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var code = reader["company_code"]?.ToString() ?? string.Empty;
                    result.Add(new CompanyInfo
                    {
                        Code = code,
                        Name = reader["company_name_ar"]?.ToString() ?? string.Empty,
                        NameEn = reader["company_name_en"]?.ToString() ?? string.Empty,
                        Database = connection.Database,
                        Enabled = true,
                        IsDefault = string.Equals(code, AppCfgSvc.Current?.MultiCompany?.DefaultCompanyCode, StringComparison.OrdinalIgnoreCase)
                    });
                }
            }
            catch
            {
            }
            return result;
        }

        private static async Task<List<CompanyInfo>> LoadFromSqliteAsync(string connectionString)
        {
            var result = new List<CompanyInfo>();
            try
            {
                SqliteSchemaBootstrapper.EnsureDatabase(connectionString);
                await using var connection = new SqliteConnection(connectionString);
                await connection.OpenAsync();
                await using var command = new SqliteCommand("""
                    SELECT company_code, company_name_ar, company_name_en
                    FROM companies WHERE is_deleted=0 AND is_active=1
                    ORDER BY company_name_ar LIMIT 1000
                    """, connection);
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var code = reader["company_code"]?.ToString() ?? string.Empty;
                    result.Add(new CompanyInfo
                    {
                        Code = code,
                        Name = reader["company_name_ar"]?.ToString() ?? string.Empty,
                        NameEn = reader["company_name_en"]?.ToString() ?? string.Empty,
                        Database = connection.DataSource,
                        Enabled = true,
                        IsDefault = string.Equals(code, AppCfgSvc.Current?.MultiCompany?.DefaultCompanyCode, StringComparison.OrdinalIgnoreCase)
                    });
                }
            }
            catch
            {
            }
            return result;
        }
    }

    // ========== CoCreateSvc.cs ==========
public sealed class CoCreateSvc : ICoCreateSvc
    {
        public async Task<CoCreateResult> CreateAsync(NewCoReq request)
        {
            var companyId = Guid.NewGuid();
            var code = await ResolveCompanyCodeAsync();
            var pwdHashResult = PwdHash.Create(request.AdminPassword);
            var hash = pwdHashResult.Hash;
            var salt = pwdHashResult.Salt;
            var fullName = string.IsNullOrWhiteSpace(request.AdminFullName)
                ? request.AdminUsername
                : request.AdminFullName;

            // Provider-specific schema: apply all dba/mig scripts before inserting company data.
            var provider = SchemaMigrator.NormalizeProvider(
                string.IsNullOrWhiteSpace(request.DatabaseProvider)
                    ? DatabaseRuntime.GetProvider()
                    : request.DatabaseProvider);

            // Resolve connection for the selected provider (inline to avoid missing-helper build breaks).
            string? migrateCs;
            if (provider == "sqlite")
            {
                migrateCs = !string.IsNullOrWhiteSpace(request.DbPath)
                    ? DatabaseRuntime.NormalizeSqliteConnectionString(request.DbPath.Trim())
                    : ResolveConnectionString();
            }
            else if (!string.IsNullOrWhiteSpace(request.DbPath) &&
                     request.DbPath.Contains('=', StringComparison.Ordinal))
            {
                migrateCs = request.DbPath.Trim();
            }
            else
            {
                migrateCs = ResolveConnectionString();
            }

            if (string.IsNullOrWhiteSpace(migrateCs))
            {
                return new CoCreateResult
                {
                    Success = false,
                    Message = "Company database connection is not configured for schema creation.",
                    CompanyCode = string.Empty
                };
            }

            if (provider == "sqlite")
            {
                // Ensure directory exists for the new company SQLite file.
                try
                {
                    var ds = DatabaseRuntime.GetSqliteDataSource(
                        DatabaseRuntime.NormalizeSqliteConnectionString(migrateCs));
                    if (!string.IsNullOrWhiteSpace(ds) && ds != ":memory:")
                    {
                        var dir = Path.GetDirectoryName(ds);
                        if (!string.IsNullOrWhiteSpace(dir))
                            Directory.CreateDirectory(dir);
                    }
                }
                catch { /* path helpers must not block migration attempt */ }
            }

            var migration = await SchemaMigrator.ApplyAllAsync(provider, migrateCs);
            if (!migration.Success)
            {
                AppHost.Logging.LogError(
                    "CoCreateSvc",
                    $"Code=APP-3010; Operation=ApplySchemaMigrations; Provider={provider}; " +
                    $"FailedMigration={migration.FailedMigrationId}; Detail={migration.Message}",
                    new InvalidOperationException(migration.Message));

                return new CoCreateResult
                {
                    Success = false,
                    Message = migration.Message,
                    CompanyCode = string.Empty,
                    SavedToDatabase = false,
                    SavedLocally = false
                };
            }

            AppHost.Logging.LogInfo(
                "CoCreateSvc",
                $"Code=APP-3011; Operation=ApplySchemaMigrations; Provider={provider}; {migration.Message}");

            var record = new StoredCoRec
            {
                CompanyId = companyId,
                Code = code,
                Name = request.CompanyNameAr.Trim(),
                NameEn = request.CompanyNameEn.Trim(),
                TaxNumber = request.TaxNumber.Trim(),
                Website = request.Website.Trim(),
                Email = request.Email.Trim(),
                Phone = request.Phone.Trim(),
                Mobile = request.Mobile.Trim(),
                AddressLine1 = request.AddressLine1?.Trim() ?? string.Empty,
				AddressLine2 = request.AddressLine2?.Trim() ?? string.Empty,
				City = request.City?.Trim() ?? string.Empty,
				Country = request.Country?.Trim() ?? string.Empty,
                License = request.License.Trim(),
                Notes = request.Notes.Trim(),
                FiscalYearStart = request.FiscalYearStart.Date,
                FiscalYearEnd = request.FiscalYearEnd.Date,
                CurrencyCode = request.CurrencyCode,
                LanguageCode = request.LanguageCode,
                DecimalPlaces = request.DecimalPlaces,
                CalendarType = request.CalendarType,
                NumberGrouping = request.NumberGrouping,
                AdminUsername = request.AdminUsername.Trim(),
                AdminPasswordHash = hash,
                AdminPasswordSalt = salt,
                AdminFullName = fullName,
                DatabaseProvider = string.IsNullOrWhiteSpace(request.DatabaseProvider) ? "SQLite" : request.DatabaseProvider.Trim(),
                DbPath = request.DbPath.Trim(),
                BackupPath = request.BackupPath.Trim(),
                AltDbPath = request.AltDbPath.Trim(),
                AltBackupPath = request.AltBackupPath.Trim(),
                AutoBackup = request.AutoBackup,
                BackupSchedule = request.BackupSchedule.Trim(),
                BackupTime = request.BackupTime.Trim(),
                CoaTemplateCode = string.IsNullOrWhiteSpace(request.CoaTemplateCode) ? "SIMPLE" : request.CoaTemplateCode.Trim(),
                DefaultWarehouseName = string.IsNullOrWhiteSpace(request.DefaultWarehouseName) ? "Main" : request.DefaultWarehouseName.Trim()
            };

            var savedToDb = await TrySaveToDatabaseAsync(record, request);

            var savedLocally = false;
            try
            {
                await CoLocalStore.SaveCompanyAsync(record);
                savedLocally = true;
            }
            catch (Exception localEx)
            {
                AppHost.Logging.LogError(
                    "CoCreateSvc",
                    $"Code=APP-3004; Operation=SaveCompanyLocal; Window=StpWiz; Detail={localEx.Message}",
                    localEx);
            }

            if (savedLocally && !string.IsNullOrWhiteSpace(record.DbPath))
            {
                try { await SeedDefaultWarehouseAsync(record); }
                catch (Exception seedEx)
                {
                    // Warehouse seed failure does not roll back company registration.
                    AppHost.Logging.LogWarning(
                        "CoCreateSvc",
                        $"Code=APP-3005; Operation=SeedDefaultWarehouse; Detail={seedEx.Message}");
                }
            }

            // Success only when the company is actually registered for selection (local index and/or database).
            var success = savedLocally || savedToDb;
            string message;
            if (success && savedToDb && savedLocally)
                message = "Company created successfully and saved to the database.";
            else if (success && savedLocally && !savedToDb)
                message = "Company registered successfully (local company index). Database save was not available.";
            else if (success && savedToDb && !savedLocally)
                message = "Company saved to the database; local company index update failed.";
            else
                message = "Company was not created. Neither database nor local company index was updated.";

            if (!success)
            {
                AppHost.Logging.LogError(
                    "CoCreateSvc",
                    $"Code=APP-3002; Operation=CreateCompany; Window=StpWiz; SavedToDatabase={savedToDb}; SavedLocally={savedLocally}; Detail={message}",
                    new InvalidOperationException(message));
            }

            return new CoCreateResult
            {
                Success = success,
                CompanyCode = success ? code : string.Empty,
                CompanyNameAr = record.Name,
                SavedToDatabase = savedToDb,
                SavedLocally = savedLocally,
                Message = message
            };
        }

        private static async Task SeedDefaultWarehouseAsync(StoredCoRec record)
		{
			var dbPath = record.DbPath?.Trim();
			if (string.IsNullOrWhiteSpace(dbPath)) return;

			var name = string.IsNullOrWhiteSpace(record.DefaultWarehouseName)
				? "Main"
				: record.DefaultWarehouseName.Trim();

			var cs = "Data Source=" + dbPath + ";Mode=ReadWriteCreate;Cache=Shared";

			SqliteSchemaBootstrapper.EnsureDatabase(cs);

			await using var connection = new SqliteConnection(cs);
			await connection.OpenAsync();

			// MatRepo owns the warehouses schema.
			MatRepo.EnsureWarehouseSchema(connection);

			var companyId = record.CompanyId.ToString();

			await using (var check = connection.CreateCommand())
			{
				check.CommandText =
					"SELECT COUNT(1) FROM warehouses WHERE company_id=$c AND is_deleted=0;";
				check.Parameters.AddWithValue("$c", companyId);

				var scalar = await check.ExecuteScalarAsync();
				var count = scalar is null || scalar == DBNull.Value
					? 0L
					: Convert.ToInt64(scalar, CultureInfo.InvariantCulture);

				if (count > 0) return;
			}

			await using (var ins = connection.CreateCommand())
			{
				ins.CommandText = @"
					INSERT INTO warehouses
					  (company_id, card_no, warehouse_code,
					   warehouse_name_ar, warehouse_name_en,
					   is_default, is_active, is_deleted, created_at)
					VALUES
					  ($c, 0, 1, $nameAr, 'Main', 1, 1, 0, CURRENT_TIMESTAMP);";

				ins.Parameters.AddWithValue("$c", companyId);
				ins.Parameters.AddWithValue("$nameAr", name);

				await ins.ExecuteNonQueryAsync();
			}
		}

        private static async Task<string> ResolveCompanyCodeAsync()
        {
            var localCode = await CoLocalStore.GenerateNextCodeAsync();
            var dbCode = await TryGenerateDbCodeAsync();

            if (string.IsNullOrWhiteSpace(dbCode))
                return localCode;

            if (int.TryParse(localCode, out var localNum) &&
                int.TryParse(dbCode, out var dbNum))
            {
                return Math.Max(localNum, dbNum).ToString("000", CultureInfo.InvariantCulture);
            }

            return dbCode;
        }

        private static async Task<string?> TryGenerateDbCodeAsync()
        {
            try
            {
                var connectionString = ResolveConnectionString();
                if (string.IsNullOrWhiteSpace(connectionString))
                    return null;

                var provider = DatabaseRuntime.GetProvider();
                if (DatabaseRuntime.IsSqlite(provider))
                    return await TryGenerateSqliteCodeAsync(connectionString);

                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync();

                const string sql = """
                    SELECT MAX(
                        CASE
                            WHEN company_code ~ '^[0-9]+$' THEN company_code::integer
                        END)
                    FROM public.companies
                    WHERE is_deleted = FALSE;
                    """;

                await using var command = new NpgsqlCommand(sql, connection);
                var value = await command.ExecuteScalarAsync();
                var max = value is int i ? i : value is decimal d ? (int)d : 0;
                return (max + 1).ToString("000", CultureInfo.InvariantCulture);
            }
            catch
            {
                return null;
            }
        }

        private static async Task<bool> TrySaveToDatabaseAsync(StoredCoRec record, NewCoReq request)
        {
            try
            {
                var provider = SchemaMigrator.NormalizeProvider(request.DatabaseProvider);
                var connectionString = ResolveCreateConnectionString(request, provider);
                if (string.IsNullOrWhiteSpace(connectionString))
                {
                    AppHost.Logging.LogWarning(
                        "CoCreateSvc",
                        $"Database save skipped for company {record.Code}: no connection string is configured for provider {provider}.");
                    return false;
                }

                if (provider == "sqlite")
                    return await TrySaveToSqliteAsync(record, connectionString);

                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync();
                await using var transaction = await connection.BeginTransactionAsync();

                try
                {
                    const string insertCompany = """
                        INSERT INTO public.companies
                        (
                            company_id, company_code, company_name_ar, company_name_en,
                            email, phone, mobile, address_line1, commercial_registration,
                            currency_code, language_code, is_active, is_deleted
                        )
                        VALUES
                        (
                            @CompanyId, @Code, @Name, @NameEn,
                            @Email, @Phone, @Mobile, @Address, @License,
                            @Currency, @Language, TRUE, FALSE
                        );
                        """;

                    await using (var cmd = new NpgsqlCommand(insertCompany, connection, transaction))
                    {
                        cmd.Parameters.Add("@CompanyId", NpgsqlDbType.Uuid).Value = record.CompanyId;
                        cmd.Parameters.Add("@Code", NpgsqlDbType.Varchar).Value = record.Code;
                        cmd.Parameters.Add("@Name", NpgsqlDbType.Varchar).Value = record.Name;
                        cmd.Parameters.Add("@NameEn", NpgsqlDbType.Varchar).Value = record.NameEn;
                        cmd.Parameters.Add("@Email", NpgsqlDbType.Varchar).Value = NullIfEmpty(record.Email);
                        cmd.Parameters.Add("@Phone", NpgsqlDbType.Varchar).Value = NullIfEmpty(record.Phone);
                        cmd.Parameters.Add("@Mobile", NpgsqlDbType.Varchar).Value = NullIfEmpty(record.Mobile);
                        cmd.Parameters.Add("@Address", NpgsqlDbType.Varchar).Value = NullIfEmpty(record.AddressLine1);
                        cmd.Parameters.Add("@License", NpgsqlDbType.Varchar).Value = NullIfEmpty(record.License);
                        cmd.Parameters.Add("@Currency", NpgsqlDbType.Char).Value = record.CurrencyCode;
                        cmd.Parameters.Add("@Language", NpgsqlDbType.Varchar).Value =
                            string.IsNullOrWhiteSpace(record.LanguageCode) ? "ar" : record.LanguageCode;
                        await cmd.ExecuteNonQueryAsync();
                    }

                    var fiscalYear = record.FiscalYearStart.Year;
                    var dbName = string.IsNullOrWhiteSpace(record.DbPath)
                        ? $"SHOUTECH_{record.Code}_{fiscalYear}"
                        : Path.GetFileName(record.DbPath.TrimEnd('\\', '/'));

                    const string insertFiscal = """
                        INSERT INTO public.fiscal_years
                        (
                            company_id, fiscal_year, database_name, start_date, end_date, is_current, is_closed
                        )
                        VALUES
                        (
                            @CompanyId, @FiscalYear, @DbName, @StartDate, @EndDate, TRUE, FALSE
                        );
                        """;

                    await using (var cmd = new NpgsqlCommand(insertFiscal, connection, transaction))
                    {
                        cmd.Parameters.Add("@CompanyId", NpgsqlDbType.Uuid).Value = record.CompanyId;
                        cmd.Parameters.Add("@FiscalYear", NpgsqlDbType.Integer).Value = fiscalYear;
                        cmd.Parameters.Add("@DbName", NpgsqlDbType.Varchar).Value = dbName;
                        cmd.Parameters.Add("@StartDate", NpgsqlDbType.Date).Value = record.FiscalYearStart;
                        cmd.Parameters.Add("@EndDate", NpgsqlDbType.Date).Value = record.FiscalYearEnd;
                        await cmd.ExecuteNonQueryAsync();
                    }

                    const string insertUser = """
                        INSERT INTO public.users
                        (
                            company_id, username, password_hash, password_salt,
                            full_name_ar, full_name_en, email, must_change_password, is_active, is_deleted
                        )
                        VALUES
                        (
                            @CompanyId, @Username, @Hash, @Salt,
                            @FullName, @FullName, @Email, FALSE, TRUE, FALSE
                        );
                        """;

                    await using (var cmd = new NpgsqlCommand(insertUser, connection, transaction))
                    {
                        cmd.Parameters.Add("@CompanyId", NpgsqlDbType.Uuid).Value = record.CompanyId;
                        cmd.Parameters.Add("@Username", NpgsqlDbType.Varchar).Value = record.AdminUsername;
                        cmd.Parameters.Add("@Hash", NpgsqlDbType.Varchar).Value = record.AdminPasswordHash;
                        cmd.Parameters.Add("@Salt", NpgsqlDbType.Varchar).Value = record.AdminPasswordSalt;
                        cmd.Parameters.Add("@FullName", NpgsqlDbType.Varchar).Value = record.AdminFullName;
                        cmd.Parameters.Add("@Email", NpgsqlDbType.Varchar).Value = NullIfEmpty(record.Email);
                        await cmd.ExecuteNonQueryAsync();
                    }

                    var roleSeed = """
                        INSERT INTO roles (company_id, role_code, role_name) VALUES
                            (@CompanyId, 'ROLE_ADMINISTRATOR', 'Administrator'),
                            (@CompanyId, 'ROLE_USER', 'User'),
                            (@CompanyId, 'ROLE_VIEWER', 'Viewer')
                        ON CONFLICT (company_id, role_code) DO NOTHING;
                        """;
                    await using (var cmd = new NpgsqlCommand(roleSeed, connection, transaction))
                    {
                        cmd.Parameters.Add("@CompanyId", NpgsqlDbType.Uuid).Value = record.CompanyId;
                        await cmd.ExecuteNonQueryAsync();
                    }

                    const string adminRole = "SELECT role_id FROM roles WHERE company_id=@CompanyId AND role_code='ROLE_ADMINISTRATOR' LIMIT 1";
                    long roleId;
                    await using (var cmd = new NpgsqlCommand(adminRole, connection, transaction))
                    {
                        cmd.Parameters.Add("@CompanyId", NpgsqlDbType.Uuid).Value = record.CompanyId;
                        roleId = Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
                    }

                    const string attachAdmin = """
                        INSERT INTO user_roles (user_id, role_id)
                        SELECT u.user_id, @RoleId FROM users u
                        WHERE u.company_id=@CompanyId AND u.username=@Username
                        ON CONFLICT DO NOTHING;
                        """;
                    await using (var cmd = new NpgsqlCommand(attachAdmin, connection, transaction))
                    {
                        cmd.Parameters.Add("@CompanyId", NpgsqlDbType.Uuid).Value = record.CompanyId;
                        cmd.Parameters.Add("@Username", NpgsqlDbType.Varchar).Value = record.AdminUsername;
                        cmd.Parameters.Add("@RoleId", NpgsqlDbType.Bigint).Value = roleId;
                        await cmd.ExecuteNonQueryAsync();
                    }

                    const string grantAdministrator = """
                        INSERT INTO role_permissions (role_id, permission_id)
                        SELECT @RoleId, permission_id FROM permissions WHERE is_active = TRUE
                        ON CONFLICT DO NOTHING;
                        """;
                    await using (var cmd = new NpgsqlCommand(grantAdministrator, connection, transaction))
                    {
                        cmd.Parameters.Add("@RoleId", NpgsqlDbType.Bigint).Value = roleId;
                        await cmd.ExecuteNonQueryAsync();
                    }

                    await transaction.CommitAsync();
                    return true;
                }
                catch
                {
                    await transaction.RollbackAsync();
                    throw;
                }
            }
            catch (Exception ex)
            {
                AppHost.Logging.LogWarning("CoCreateSvc", $"Database save failed: {ex.Message}");
                return false;
            }
        }

        private static async Task<string?> TryGenerateSqliteCodeAsync(string connectionString)
        {
            SqliteSchemaBootstrapper.EnsureDatabase(connectionString);
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new SqliteCommand(
                "SELECT COALESCE(MAX(CAST(company_code AS INTEGER)), 0) FROM companies WHERE is_deleted=0", connection);
            var max = Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
            return (max + 1).ToString("000", CultureInfo.InvariantCulture);
        }

        private static async Task<bool> TrySaveToSqliteAsync(StoredCoRec record, string connectionString)
        {
            // Schema must already be applied by SchemaMigrator. EnsureDatabase only validates core objects.
            try
            {
                SqliteSchemaBootstrapper.EnsureDatabase(connectionString);
            }
            catch (Exception ex)
            {
                AppHost.Logging.LogWarning(
                    "CoCreateSvc",
                    $"Post-migration schema validation warning: {ex.Message}");
            }

            await using var connection = new SqliteConnection(
                DatabaseRuntime.NormalizeSqliteConnectionString(connectionString));
            await connection.OpenAsync();
            await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
            try
            {
                await ExecuteSqliteAsync(connection, transaction, """
                    INSERT INTO companies
                        (company_id, company_code, company_name_ar, company_name_en, email, phone, mobile, address_line1,
                         commercial_registration, currency_code, language_code)
                    VALUES (@id, @code, @name, @nameEn, @email, @phone, @mobile, @address, @license, @currency, @lang)
                    """, ("id", record.CompanyId.ToString()), ("code", record.Code), ("name", record.Name),
                    ("nameEn", record.NameEn), ("email", NullIfEmpty(record.Email)), ("phone", NullIfEmpty(record.Phone)),
                    ("mobile", NullIfEmpty(record.Mobile)), ("address", NullIfEmpty(record.AddressLine1)),
                    ("license", NullIfEmpty(record.License)), ("currency", record.CurrencyCode),
                    ("lang", string.IsNullOrWhiteSpace(record.LanguageCode) ? "ar" : record.LanguageCode));
                var fiscalYear = record.FiscalYearStart.Year;
                await ExecuteSqliteAsync(connection, transaction, """
                    INSERT INTO fiscal_years (company_id, fiscal_year, database_name, start_date, end_date)
                    VALUES (@companyId, @year, @dbName, @start, @end)
                    """, ("companyId", record.CompanyId.ToString()), ("year", fiscalYear),
                    ("dbName", string.IsNullOrWhiteSpace(record.DbPath) ? $"SHOUTECH_{record.Code}_{fiscalYear}" : Path.GetFileName(record.DbPath.TrimEnd('\\', '/'))),
                    ("start", record.FiscalYearStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                    ("end", record.FiscalYearEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
                var adminUserId = Guid.NewGuid().ToString();
                await ExecuteSqliteAsync(connection, transaction, """
                    INSERT INTO users
                        (user_id, company_id, username, password_hash, password_salt, full_name, full_name_ar, full_name_en, email)
                    VALUES (@userId, @companyId, @username, @hash, @salt, @fullName, @fullName, @fullName, @email)
                    """, ("userId", adminUserId), ("companyId", record.CompanyId.ToString()),
                    ("username", record.AdminUsername), ("hash", record.AdminPasswordHash), ("salt", record.AdminPasswordSalt),
                    ("fullName", record.AdminFullName), ("email", NullIfEmpty(record.Email)));
                await ExecuteSqliteAsync(connection, transaction, """
                    INSERT OR IGNORE INTO roles (company_id, role_code, role_name) VALUES
                        (@companyId, 'ROLE_ADMINISTRATOR', 'Administrator'),
                        (@companyId, 'ROLE_USER', 'User'),
                        (@companyId, 'ROLE_VIEWER', 'Viewer')
                    """, ("companyId", record.CompanyId.ToString()));
                await ExecuteSqliteAsync(connection, transaction, """
                    INSERT OR IGNORE INTO user_roles (user_id, role_id)
                    SELECT @userId, role_id FROM roles
                    WHERE company_id=@companyId AND role_code='ROLE_ADMINISTRATOR'
                    """, ("userId", adminUserId), ("companyId", record.CompanyId.ToString()));
                await ExecuteSqliteAsync(connection, transaction, """
                    INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
                    SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
                    WHERE r.company_id=@companyId AND r.role_code='ROLE_ADMINISTRATOR' AND p.is_active=1
                    """, ("companyId", record.CompanyId.ToString()));
                await transaction.CommitAsync();
                return true;
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        private static async Task ExecuteSqliteAsync(SqliteConnection connection, SqliteTransaction transaction,
            string sql, params (string Name, object Value)[] values)
        {
            await using var command = new SqliteCommand(sql, connection, transaction);
            foreach (var (name, value) in values)
                command.Parameters.AddWithValue("@" + name, value);
            await command.ExecuteNonQueryAsync();
        }

        private static object NullIfEmpty(string value)
            => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();

        private static string? ResolveCreateConnectionString(NewCoReq request, string provider)
        {
            if (provider == "sqlite")
            {
                if (!string.IsNullOrWhiteSpace(request.DbPath))
                    return DatabaseRuntime.NormalizeSqliteConnectionString(request.DbPath.Trim());
                return ResolveConnectionString();
            }

            if (!string.IsNullOrWhiteSpace(request.DbPath) &&
                request.DbPath.Contains('=', StringComparison.Ordinal))
                return request.DbPath.Trim();

            return ResolveConnectionString();
        }

        private static string? ResolveConnectionString()
        {
            try
            {
                return DatabaseRuntime.GetConnectionString();
            }
            catch
            {
                return null;
            }
        }
    }

}
