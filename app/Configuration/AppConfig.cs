// ========================================================================
// FILE: app/Configuration/AppConfig.cs
// PROJECT: SHOUTECH ERP V10 - Central Configuration
// Merged: AppCfgSvc + AppSetng + AppPaths
// RESTORED from 5b87ec2 with PasswordPolicySettings.MinLength = 3
// ========================================================================

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ShouTech.App.Common;
using ShouTech.App.Services;
using ShouTech.Domain;

namespace ShouTech.App.Configuration
{
    public static class AppPaths
    {
        public static string Root { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ShouTech", "ERP");
        public static string Database => Path.Combine(Root, "Database");
        public static string Data => Path.Combine(Root, "data");
        public static string Backups => Path.Combine(Root, "Backups");
        public static string Security => Path.Combine(Root, "Security");
        public static string Cfg => Path.Combine(Root, "cfg");
        public static string Sync => Path.Combine(Root, "sync");
        public static string Logs => Path.Combine(Root, "Logs");
        public static string CompaniesIndex => Path.Combine(Data, "companies.json");
        public static string UserPrefs => Path.Combine(Root, "userprefs.json");
        public static string DeviceJson => Path.Combine(Cfg, "device.json");
        public static string DeviceId => Path.Combine(Cfg, "device.id");
        public static string LicenseLocal => Path.Combine(Cfg, "license.local.json");
        public static string CompanyBackup(string? companyCode) =>
            Path.Combine(Backups, string.IsNullOrWhiteSpace(companyCode) ? "default" : companyCode);
    }

    public static class AppCfgSvc
    {
        private static AppSetng? _settings;
        private static readonly object _lock = new();
        private static string? _loadedPath;

        public static AppSetng Current
        {
            get
            {
                if (_settings == null)
                    Load();
                return _settings!;
            }
        }

        public static string? LoadedPath => _loadedPath;
        public static bool IsLoaded => _settings != null;

        public static void Load(string? explicitPath = null)
        {
            lock (_lock)
            {
                string? path = explicitPath;
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    var candidates = new[]
                    {
                        Path.Combine(AppContext.BaseDirectory, "cfg", "appset.json"),
                        Path.Combine(AppContext.BaseDirectory, "appset.json"),
                        Path.Combine(Directory.GetCurrentDirectory(), "cfg", "appset.json"),
                        Path.Combine(Directory.GetCurrentDirectory(), "appset.json"),
                    };
                    path = candidates.FirstOrDefault(File.Exists);
                }
                if (path == null || !File.Exists(path))
                {
                    _settings = CreateDefaultSettings();
                    _loadedPath = null;
                    return;
                }
                try
                {
                    var json = File.ReadAllText(path);
                    _settings = JsonSerializer.Deserialize<AppSetng>(json, JsonDefs.AppSetngRead)
                                ?? CreateDefaultSettings();
                    ApplyEnvironmentOverrides(_settings);
                    ApplyDevelopmentDefaults(_settings);
                    _loadedPath = path;
                }
                catch (Exception ex)
                {
                    AppHost.Errors.Report(ErrorCodes.ConfigurationLoadFailed, ex, "AppConfig.Load");
                    _settings = CreateDefaultSettings();
                    _loadedPath = null;
                }
            }
        }

        private static void ApplyEnvironmentOverrides(AppSetng settings)
        {
            var connection = Environment.GetEnvironmentVariable("SHOUTECH_DB_CONNECTION");
            if (!string.IsNullOrWhiteSpace(connection))
            {
                if (!settings.ConnectionStrings.TryGetValue("Default", out var defaultConnection))
                {
                    defaultConnection = new ConnectionStringSettings();
                    settings.ConnectionStrings["Default"] = defaultConnection;
                }
                defaultConnection.ConnectionString = connection;
            }
        }

        private static void ApplyDevelopmentDefaults(AppSetng settings)
        {
            if (!string.Equals(settings.Application.EnvironmentType, "Development", StringComparison.OrdinalIgnoreCase))
                return;
            settings.Application.RequireDongle = false;
            settings.Application.RequireLicense = false;
            settings.Security.RequireDongle = false;
            settings.Security.RequireLicense = false;
            if (settings.Licensing?.Activation != null)
            {
                settings.Licensing.Activation.RequireOnlineActivation = false;
                settings.Licensing.Activation.ActivationServer = string.Empty;
            }
        }

        public static void Reload()
        {
            lock (_lock)
            {
                _settings = null;
                _loadedPath = null;
            }
            Load();
        }

        private static AppSetng CreateDefaultSettings()
        {
            return new AppSetng
            {
                Application = new ApplicationSettings
                {
                    Name = "ShouTech Enterprise Management System",
                    NameEn = "ShouTech Enterprise Management System",
                    ShortName = "ShouTech ERP",
                    ShortNameEn = "ShouTech ERP",
                    Version = "10.6.0",
                    Build = "20260801",
                    Copyright = "© 2026 ShouTech. All rights reserved.",
                    Environment = "Development",
                    RequireDongle = true,
                    RequireLicense = true,
                    CheckFileIntegrity = true,
                    MinSplashSeconds = 4.5
                },
                Languages = new LanguagesSettings { Default = "ar", Fallback = "en" },
                Currency = new CurrencySettings { DefaultCode = "SYP", DefaultSymbol = "ل.س" },
                Security = new SecuritySettings
                {
                    RequireDongle = true,
                    RequireLicense = true,
                    SessionTimeoutMinutes = 30,
                    MaxLoginAttempts = 5,
                    PasswordPolicy = new PasswordPolicySettings { MinLength = 3, MaxLength = 128 }
                },
                Company = new CompanySettings
                {
                    DefaultCurrencyCode = "SYP",
                    DefaultLanguage = "en",
                    TimeZone = "Asia/Damascus"
                },
                MultiCompany = new MultiCompanySettings { Enabled = true }
            };
        }

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        public static void Save()
        {
            try
            {
                if (Current is null) return;
                var path = LoadedPath;
                if (string.IsNullOrWhiteSpace(path)) return;
                var json = JsonSerializer.Serialize(Current, JsonOptions);
                File.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                AppHost.Errors.Report(ErrorCodes.ConfigurationLoadFailed, ex, "AppConfig.Save");
            }
        }
    }

    public class AppSetng
    {
        public ApplicationSettings Application { get; set; } = new();
        public CompanySettings Company { get; set; } = new();
        public Dictionary<string, ConnectionStringSettings> ConnectionStrings { get; set; } = new();
        public DatabaseSettings Database { get; set; } = new();
        public PathsSettings Paths { get; set; } = new();
        public LanguagesSettings Languages { get; set; } = new();
        public CurrencySettings Currency { get; set; } = new();
        public ModulesSettings Modules { get; set; } = new();
        public BrandingSettings Branding { get; set; } = new();
        public SecuritySettings Security { get; set; } = new();
        public AiSettings AI { get; set; } = new();
        public LoggingSettings Logging { get; set; } = new();
        public LicensingSettings Licensing { get; set; } = new();
        public MultiCompanySettings MultiCompany { get; set; } = new();
        public IntegrationsSettings Integrations { get; set; } = new();
        public OnlineServicesSettings OnlineServices { get; set; } = new();
    }

    public class ApplicationSettings
    {
        public string Name { get; set; } = "ShouTech Enterprise Management System";
        public string NameEn { get; set; } = "ShouTech Enterprise Management System";
        public string ShortName { get; set; } = "ShouTech ERP";
        public string ShortNameEn { get; set; } = "ShouTech ERP";
        public string BrandTagline { get; set; } = "";
        public string BrandTaglineAr { get; set; } = "";
        public string VisionStatement { get; set; } = "";
        public string VisionStatementAr { get; set; } = "";
        public string TargetMarket { get; set; } = "";
        public string PrimaryAudience { get; set; } = "";
        public string EnterpriseStyle { get; set; } = "";
        public string Version { get; set; } = "10.6.0";
        public string Build { get; set; } = "20260801";
        public DateTime BuildDate { get; set; } = new DateTime(2026, 8, 1);
        public string Company { get; set; } = "ShouTech Software";
        public string CompanyEn { get; set; } = "ShouTech Software";
        public string Copyright { get; set; } = "© 2026 ShouTech. All rights reserved.";
        public string Website { get; set; } = "https://www.shoutech.com";
        public string SupportEmail { get; set; } = "support@shoutech.com";
        public string Environment { get; set; } = "Development";
        public string EnvironmentType { get; set; } = "Development";
        public string InstanceId { get; set; } = "ST-ERP-001";
        public string ClusterId { get; set; } = "ST-CLUSTER-001";
        public bool AllowMultipleInstances { get; set; }
        public bool RequireDongle { get; set; } = true;
        public bool RequireLicense { get; set; } = true;
        public bool CheckFileIntegrity { get; set; } = true;
        public bool EnableAutoUpdate { get; set; } = true;
        public int AutoUpdateCheckIntervalHours { get; set; } = 24;
        public double MinSplashSeconds { get; set; } = 4.5;
        public double StartupDelaySeconds { get; set; } = 2;
        public bool EnableTelemetry { get; set; }
        public string TelemetryEndpoint { get; set; } = "";
    }

    public class CompanySettings
    {
        public string Id { get; set; } = "ST-COMP-001";
        public string Name { get; set; } = "ShouTech Software";
        public string NameEn { get; set; } = "ShouTech Software";
        public string CommercialName { get; set; } = "ShouTech";
        public string CommercialNameEn { get; set; } = "ShouTech";
        public string BrandSlogan { get; set; } = "";
        public string BrandSloganAr { get; set; } = "";
        public string MarketRegion { get; set; } = "Middle East";
        public string OperationCenter { get; set; } = "Damascus - Syria";
        public string TaxId { get; set; } = "";
        public string CommercialRegister { get; set; } = "";
        public string VATNumber { get; set; } = "";
        public string Address { get; set; } = "Damascus - Syria";
        public string AddressEn { get; set; } = "Damascus - Syria";
        public string Phone { get; set; } = "";
        public string Mobile { get; set; } = "";
        public string Email { get; set; } = "info@shoutech.com";
        public string Website { get; set; } = "https://www.shoutech.com";
        public string LogoPath { get; set; } = "Assets/company-logo.png";
        public string FaviconPath { get; set; } = "Assets/favicon.ico";
        public string DefaultCurrencyCode { get; set; } = "SYP";
        public string DefaultLanguage { get; set; } = "en";
        public string TimeZone { get; set; } = "Asia/Damascus";
        public string DateFormat { get; set; } = "dd/MM/yyyy";
        public string TimeFormat { get; set; } = "HH:mm:ss";
        public NumberFormatSettings NumberFormat { get; set; } = new();
    }

    public class NumberFormatSettings
    {
        public string DecimalSeparator { get; set; } = ".";
        public string GroupSeparator { get; set; } = ",";
        public int DecimalPlaces { get; set; } = 2;
    }

    public class ConnectionStringSettings
    {
        public string Provider { get; set; } = "SQLite";
        public string ConnectionString { get; set; } = "";
        public bool Encrypted { get; set; }
        public bool ReadOnly { get; set; }
        public int CommandTimeout { get; set; } = 60;
        public int ConnectionTimeout { get; set; } = 30;
        public int MaxPoolSize { get; set; } = 100;
        public int MinPoolSize { get; set; } = 5;
        public bool EnablePooling { get; set; } = true;
        public string ApplicationIntent { get; set; } = "ReadWrite";
    }

    public class DatabaseSettings
    {
        public string DefaultProvider { get; set; } = "SQLite";
        public List<ProviderInfo> SupportedProviders { get; set; } = new();
        public MigrationSettings Migration { get; set; } = new();
        public BackupSettings Backup { get; set; } = new();
        public RetrySettings Retry { get; set; } = new();
    }

    public class ProviderInfo
    {
        public string Name { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public bool Enabled { get; set; } = true;
    }

    public class MigrationSettings
    {
        public bool AutoMigrate { get; set; } = true;
        public bool BackupBeforeMigrate { get; set; } = true;
    }

    public class BackupSettings
    {
        public bool Enabled { get; set; } = true;
        public bool AutoBackup { get; set; } = true;
        public int BackupIntervalHours { get; set; } = 24;
        public int MaxBackups { get; set; } = 30;
        public int RetentionDays { get; set; } = 30;
        public string Schedule { get; set; } = "Daily";
        public string BackupPath { get; set; } = "";
        public bool CompressBackup { get; set; } = true;
        public bool EncryptBackup { get; set; }
    }

    public class RetrySettings
    {
        public int MaxRetries { get; set; } = 3;
        public int DelayMilliseconds { get; set; } = 1000;
    }

    public class PathsSettings
    {
        public DataPaths Data { get; set; } = new();
        public BackupPaths Backup { get; set; } = new();
        public LogsPaths Logs { get; set; } = new();
        public ReportsPaths Reports { get; set; } = new();
        public TempPaths Temp { get; set; } = new();
        public ConfigPaths Config { get; set; } = new();
        public ModulesPaths Modules { get; set; } = new();
        public AssetsPaths Assets { get; set; } = new();
        public ResourcesPaths Resources { get; set; } = new();
        public CachePaths Cache { get; set; } = new();
    }

    public class DataPaths { public string Root { get; set; } = ""; }
    public class BackupPaths { public string Root { get; set; } = ""; }
    public class LogsPaths { public string Root { get; set; } = ""; }
    public class ReportsPaths { public string Root { get; set; } = ""; }
    public class TempPaths { public string Root { get; set; } = ""; }
    public class ConfigPaths { public string Root { get; set; } = ""; }
    public class ModulesPaths { public string Root { get; set; } = ""; }
    public class AssetsPaths { public string Root { get; set; } = ""; }
    public class ResourcesPaths { public string Root { get; set; } = ""; }
    public class CachePaths { public string Root { get; set; } = ""; }

    public class LanguagesSettings
    {
        public string Default { get; set; } = "ar";
        public string Fallback { get; set; } = "en";
        public List<LanguageInfo> Available { get; set; } = new();
        public TranslationServiceSettings TranslationService { get; set; } = new();
    }

    public class LanguageInfo
    {
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public string NativeName { get; set; } = "";
        public bool IsRtl { get; set; }
        public bool Enabled { get; set; } = true;
    }

    public class TranslationServiceSettings
    {
        public bool Enabled { get; set; }
        public string Provider { get; set; } = "";
    }

    public class CurrencySettings
    {
        public string DefaultCode { get; set; } = "SYP";
        public string DefaultSymbol { get; set; } = "ل.س";
        public List<CurrencyInfo> Available { get; set; } = new();
        public ExchangeRatesSettings ExchangeRates { get; set; } = new();
        public RoundingSettings Rounding { get; set; } = new();
    }

    public class CurrencyInfo
    {
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public string Symbol { get; set; } = "";
        public int DecimalPlaces { get; set; } = 2;
    }

    public class ExchangeRatesSettings
    {
        public bool Enabled { get; set; }
        public string Provider { get; set; } = "";
        public string BaseCurrency { get; set; } = "SYP";
        public int UpdateIntervalHours { get; set; } = 6;
    }

    public class RoundingSettings
    {
        public string Mode { get; set; } = "AwayFromZero";
        public int DecimalPlaces { get; set; } = 2;
    }

    public class ModulesSettings
    {
        public List<string> Enabled { get; set; } = new();
        public string DefaultStartupModule { get; set; } = "Dashboard";
        public List<ModuleInfo> AllModules { get; set; } = new();
        public List<ModuleInfo> Available { get; set; } = new();
    }

    public class ModuleInfo
    {
        public string Id { get; set; } = "";
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public string NameEn { get; set; } = "";
        public string Version { get; set; } = "";
        public bool Enabled { get; set; } = true;
        public bool Required { get; set; }
        public int Order { get; set; }
    }

    public class BrandingSettings
    {
        public FontsSettings Fonts { get; set; } = new();
        public FontSizesSettings FontSizes { get; set; } = new();
        public ImagesSettings Images { get; set; } = new();
        public AnimationsSettings Animations { get; set; } = new();
    }

    public class FontsSettings
    {
        public string Primary { get; set; } = "";
        public string Secondary { get; set; } = "";
    }

    public class FontSizesSettings
    {
        public double Small { get; set; } = 12;
        public double Normal { get; set; } = 14;
        public double Large { get; set; } = 16;
    }

    public class ImagesSettings
    {
        public string Logo { get; set; } = "";
        public string Splash { get; set; } = "";
    }

    public class AnimationsSettings
    {
        public bool Enabled { get; set; } = true;
    }

    public class SecuritySettings
    {
        public bool RequireDongle { get; set; } = true;
        public bool RequireLicense { get; set; } = true;
        public int SessionTimeoutMinutes { get; set; } = 30;
        public int MaxLoginAttempts { get; set; } = 5;
        public int LockoutDurationMinutes { get; set; } = 15;
        public PasswordPolicySettings PasswordPolicy { get; set; } = new();
        public TwoFactorAuthSettings TwoFactorAuth { get; set; } = new();
        public AuditSettings Audit { get; set; } = new();
        public EncryptionSettings Encryption { get; set; } = new();
        public PermissionsSettings Permissions { get; set; } = new();
    }

    public class PasswordPolicySettings
    {
        public int MinLength { get; set; } = 3;
        public int MaxLength { get; set; } = 128;
        public bool RequireUppercase { get; set; }
        public bool RequireLowercase { get; set; }
        public bool RequireDigit { get; set; }
        public bool RequireSpecialChar { get; set; }
    }

    public class TwoFactorAuthSettings
    {
        public bool Enabled { get; set; }
        public string Provider { get; set; } = "";
    }

    public class AuditSettings
    {
        public bool Enabled { get; set; } = true;
        public bool LogLogins { get; set; } = true;
        public bool LogDataChanges { get; set; } = true;
    }

    public class EncryptionSettings
    {
        public string Algorithm { get; set; } = "AES-256";
    }

    public class PermissionsSettings
    {
        public bool EnforceRbac { get; set; } = true;
    }

    public class AiSettings
    {
        public bool Enabled { get; set; }
        public AiFeatures Features { get; set; } = new();
        public LlmSettings Llm { get; set; } = new();
    }

    public class AiFeatures
    {
        public bool ReportAssistant { get; set; }
        public bool SmartSearch { get; set; }
    }

    public class LlmSettings
    {
        public string Provider { get; set; } = "";
        public string Endpoint { get; set; } = "";
        public string Model { get; set; } = "";
    }

    public class LoggingSettings
    {
        public string Level { get; set; } = "Information";
        public LogProviderSettings File { get; set; } = new();
        public PerformanceLoggingSettings Performance { get; set; } = new();
        public MetricsSettings Metrics { get; set; } = new();
    }

    public class LogProviderSettings
    {
        public bool Enabled { get; set; } = true;
        public string Path { get; set; } = "";
        public int RetentionDays { get; set; } = 30;
    }

    public class PerformanceLoggingSettings
    {
        public bool Enabled { get; set; }
        public int SlowQueryThresholdMs { get; set; } = 1000;
    }

    public class MetricsSettings
    {
        public bool Enabled { get; set; }
        public MetricsProvider Provider { get; set; } = new();
    }

    public class MetricsProvider
    {
        public string Name { get; set; } = "";
        public string Endpoint { get; set; } = "";
    }

    public class LicensingSettings
    {
        public DongleInfoSettings Dongle { get; set; } = new();
        public DongleInfoSettings DongleInfo { get; set; } = new();
        public ActivationSettings Activation { get; set; } = new();
        public List<string> Features { get; set; } = new();
        public string ExpiryDate { get; set; } = "";
    }

    public class DongleInfoSettings
    {
        public bool Required { get; set; } = true;
        public string SerialNumber { get; set; } = "";
    }

    public class ActivationSettings
    {
        public bool RequireOnlineActivation { get; set; } = true;
        public bool OfflineActivationAllowed { get; set; } = true;
        public bool HardwareIdBased { get; set; } = true;
        public string ActivationServer { get; set; } = "";
        public string LicenseValidateEndpoint { get; set; } = "/license/validate";
    }

    public class MultiCompanySettings
    {
        public bool Enabled { get; set; } = true;
        public string DefaultCompanyCode { get; set; } = "";
        public List<CompanyInfo> Companies { get; set; } = new();
        public BranchesSettings Branches { get; set; } = new();
    }

    public class CompanyInfo
    {
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public string NameEn { get; set; } = "";
        public string Database { get; set; } = "";
        public bool Enabled { get; set; } = true;
        public bool IsDefault { get; set; }
    }

    public class BranchesSettings
    {
        public bool Enabled { get; set; }
        public List<BranchInfo> Items { get; set; } = new();
    }

    public class BranchInfo
    {
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
    }

    public class IntegrationsSettings
    {
        public RestApiSettings RestApi { get; set; } = new();
    }

    public class RestApiSettings
    {
        public bool Enabled { get; set; }
        public string BaseUrl { get; set; } = "";
        public string Endpoint { get; set; } = "";
    }

    public class OnlineServicesSettings
    {
        public bool Enabled { get; set; }
        public bool BackupOnStartup { get; set; }
        public int BackgroundSyncIntervalSeconds { get; set; } = 3600;
        public int ConnectivityCheckIntervalSeconds { get; set; } = 60;
        public string BackupUploadEndpoint { get; set; } = "/backup/upload";
        public string DeviceRegisterEndpoint { get; set; } = "/device/register";
        public string LicenseValidateEndpoint { get; set; } = "/license/validate";
        public string SyncEndpoint { get; set; } = "/sync";
    }
}
