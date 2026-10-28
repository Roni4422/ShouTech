// ========================================================================
// FILE: cod/app/src/Configuration/AppConfig.cs
// PROJECT: SHOUTECH ERP V10 - Central Configuration
// Merged: AppCfgSvc + AppSetng + AppPaths
// ========================================================================

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ShouTech.App.Common;

namespace ShouTech.App.Configuration
{

    // ========================================================================
    // AppPaths — absolute runtime paths under %LocalAppData%\ShouTech\ERP
    // Single source of truth. Do not hardcode LocalApplicationData paths elsewhere.
    // ========================================================================
    public static class AppPaths
    {
        /// <summary>%LocalAppData%\ShouTech\ERP</summary>
        public static string Root { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ShouTech", "ERP");

        public static string Database => Path.Combine(Root, "Database");
        public static string Data     => Path.Combine(Root, "data");
        public static string Backups  => Path.Combine(Root, "Backups");
        public static string Security => Path.Combine(Root, "Security");
        public static string Cfg      => Path.Combine(Root, "cfg");
        public static string Sync     => Path.Combine(Root, "sync");
        public static string Logs     => Path.Combine(Root, "Logs");

        public static string CompaniesIndex => Path.Combine(Data, "companies.json");
        public static string UserPrefs      => Path.Combine(Root, "userprefs.json");
        public static string DeviceJson     => Path.Combine(Cfg, "device.json");
        public static string DeviceId       => Path.Combine(Cfg, "device.id");
        public static string LicenseLocal   => Path.Combine(Cfg, "license.local.json");

        public static string CompanyBackup(string? companyCode) =>
            Path.Combine(Backups, string.IsNullOrWhiteSpace(companyCode) ? "default" : companyCode);
    }


    // ========================================================================
    // AppCfgSvc — load / save appset.json
    // ========================================================================
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

                        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "cfg", "appset.json")),
                        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "cfg", "appset.json")),
                        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "cfg", "appset.json")),

                        @"D:\SyrianHouse\Tool\shoutech_erp_v9\cfg\appset.json",
                        @"D:\Tools\Down2\shoutech_erp_v9\cfg\appset.json"
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
                catch (Exception)
                {
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

        /// <summary>
        /// </summary>
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
                    Name = "Ø´Ùˆ ØªÙŠÙƒ Ù„Ø¥Ø¯Ø§Ø±Ø© Ø§Ù„Ù…Ø¤Ø³Ø³Ø§Øª",
                    NameEn = "ShouTech Enterprise Management System",
                    ShortName = "Ø´Ùˆ ØªÙŠÙƒ ERP",
                    ShortNameEn = "ShouTech ERP",
                    BrandTagline = "Unified Business Intelligence for Regional Growth",
                    BrandTaglineAr = "Ø°ÙƒØ§Ø¡ Ø£Ø¹Ù…Ø§Ù„ Ù…ÙˆØ­Ø¯ Ù„Ù†Ù…Ùˆ Ø§Ù„Ù…Ø¤Ø³Ø³Ø§Øª Ø§Ù„Ø¥Ù‚Ù„ÙŠÙ…ÙŠØ©",
                    VisionStatement = "Empower regional enterprises with secure, intelligent, and scalable ERP.",
                    VisionStatementAr = "Ù†Ù‚ÙˆÙ‘Ù… Ø§Ù„Ù…Ø¤Ø³Ø³Ø§Øª Ø§Ù„Ø¥Ù‚Ù„ÙŠÙ…ÙŠØ© Ø¨Ø°ÙƒØ§Ø¡ ÙˆØ£Ù…Ø§Ù† ÙˆÙ‚Ø§Ø¨Ù„ÙŠØ© ØªÙˆØ³Ø¹ Ø¹Ø§Ù„ÙŠØ©.",
                    TargetMarket = "Middle East & North Africa",
                    PrimaryAudience = "SMB + Corporate + Multi-company groups",
                    EnterpriseStyle = "Enterprise / Arabic-first / Offline-first",
                    Version = "10.6.0",
                    Build = "20260801",
                    Copyright = "Â© 2026 ShouTech. All rights reserved.",
                    Environment = "Development",
                    RequireDongle = true,
                    RequireLicense = true,
                    CheckFileIntegrity = true,
                    MinSplashSeconds = 4.5
                },
               
                Languages = new LanguagesSettings
                {
                    Default = "ar",
                    Fallback = "en"
                },
                Currency = new CurrencySettings
                {
                    DefaultCode = "SYP",
                    DefaultSymbol = "Ù„.Ø³"
                },
                Security = new SecuritySettings
                {
                    RequireDongle = true,
                    RequireLicense = true,
                    SessionTimeoutMinutes = 30,
                    MaxLoginAttempts = 5
                },
                Company = new CompanySettings
                {
                    BrandSlogan = "Building trust through enterprise automation",
                    BrandSloganAr = "Ù†Ø¨Ù†ÙŠ Ø§Ù„Ø«Ù‚Ø© Ø¹Ø¨Ø± Ø£ØªÙ…ØªØ© Ù…Ø¤Ø³Ø³ÙŠØ© Ù…ØªÙ‚Ø¯Ù…Ø©",
                    MarketRegion = "Middle East",
                    OperationCenter = "Damascus - Syria",
                    CommercialName = "Ø´Ùˆ ØªÙŠÙƒ",
                    CommercialNameEn = "ShouTech",
                    Address = "Ø¯Ù…Ø´Ù‚ - Ø³ÙˆØ±ÙŠØ§",
                    AddressEn = "Damascus - Syria",
                    DefaultCurrencyCode = "SYP",
                    DefaultLanguage = "en",
                    TimeZone = "Asia/Damascus"
                },
                MultiCompany = new MultiCompanySettings
                {
                    Enabled = true,
                    DefaultCompanyCode = string.Empty,
                    Companies = new System.Collections.Generic.List<CompanyInfo>()
                }
            };
        }

        // ═══════════════════════════════════════════════════════════
        // v0.2.0 additions
        // ═══════════════════════════════════════════════════════════
		private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
		{
			WriteIndented = true
		};
        public static void Save()
		{
			try
			{
				if (Current is null) return;
				var path = LoadedPath;
				if (string.IsNullOrWhiteSpace(path)) return;

				var json = System.Text.Json.JsonSerializer.Serialize(Current, JsonOptions);
				System.IO.File.WriteAllText(path, json);
			}
			catch { }
		}
}

    // ========================================================================
    // AppSetng — settings model (deserialized from cfg/appset.json)
    // ========================================================================
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

    // -------------------- Application --------------------
    public class ApplicationSettings
    {
        public string Name { get; set; } = "شو تيك لإدارة المؤسسات";
        public string NameEn { get; set; } = "ShouTech Enterprise Management System";
        public string ShortName { get; set; } = "شو تيك ERP";
        public string ShortNameEn { get; set; } = "ShouTech ERP";
        public string BrandTagline { get; set; } = "Unified Business Intelligence for Regional Growth";
        public string BrandTaglineAr { get; set; } = "ذكاء أعمال موحد لنمو المؤسسات الإقليمية";
        public string VisionStatement { get; set; } = "Empower regional enterprises with secure, intelligent, and scalable ERP.";
        public string VisionStatementAr { get; set; } = "نقوّم المؤسسات الإقليمية بذكاء وأمان وقابلية توسع عالية.";
        public string TargetMarket { get; set; } = "Middle East & North Africa";
        public string PrimaryAudience { get; set; } = "SMB + Corporate + Multi-company groups";
        public string EnterpriseStyle { get; set; } = "Enterprise / Arabic-first / Offline-first";
        public string Version { get; set; } = "10.6.0";
        public string Build { get; set; } = "20260801";
        public DateTime BuildDate { get; set; } = new DateTime(2026, 8, 1);
        public string Company { get; set; } = "شو تيك للبرمجيات";
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

    // -------------------- Company --------------------
    public class CompanySettings
    {
        public string Id { get; set; } = "ST-COMP-001";
        public string Name { get; set; } = "شو تيك للبرمجيات";
        public string NameEn { get; set; } = "ShouTech Software";
        public string CommercialName { get; set; } = "شو تيك";
        public string CommercialNameEn { get; set; } = "ShouTech";
        public string BrandSlogan { get; set; } = "Building trust through enterprise automation";
        public string BrandSloganAr { get; set; } = "نبني الثقة عبر أتمتة مؤسسية متقدمة";
        public string MarketRegion { get; set; } = "Middle East";
        public string OperationCenter { get; set; } = "Damascus - Syria";
        public string TaxId { get; set; } = "";
        public string CommercialRegister { get; set; } = "";
        public string VATNumber { get; set; } = "";
        public string Address { get; set; } = "دمشق - سوريا";
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

    // -------------------- ConnectionString --------------------
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

    // -------------------- Database --------------------
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
        public string Assembly { get; set; } = "";
        public string ProviderFactory { get; set; } = "";
    }

    public class MigrationSettings
    {
        public bool Enabled { get; set; } = true;
        public bool AutoMigrate { get; set; } = true;
        public string MigrationsPath { get; set; } = "./dba/mig";
        public string SeedDataPath { get; set; } = "./dba/seed";
        public bool BackupBeforeMigration { get; set; } = true;
    }

    public class BackupSettings
    {
        public bool Enabled { get; set; } = true;
        public bool AutoBackup { get; set; } = true;
        public int BackupIntervalHours { get; set; } = 24;
        public int MaxBackups { get; set; } = 3;
        public string BackupPath { get; set; } = "./bkp";
        public bool CompressBackup { get; set; } = true;
        public bool EncryptBackup { get; set; } = true;
        public string BackupEncryptionKey { get; set; } = "";
    }

    public class RetrySettings
    {
        public int MaxRetryCount { get; set; } = 3;
        public int RetryDelaySeconds { get; set; } = 5;
        public int MaxRetryDelaySeconds { get; set; } = 30;
        public bool EnableExponentialBackoff { get; set; } = true;
        public bool EnableCircuitBreaker { get; set; } = true;
        public int CircuitBreakerThreshold { get; set; } = 5;
        public int CircuitBreakerDurationSeconds { get; set; } = 60;
    }

    // -------------------- Paths --------------------
    public class PathsSettings
    {
        public string Root { get; set; } = "./";
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

    public class DataPaths
    {
        public string Folder { get; set; } = "./data";
        public string Documents { get; set; } = "./data/documents";
        public string Images { get; set; } = "./data/images";
        public string Attachments { get; set; } = "./data/attachments";
        public string Templates { get; set; } = "./data/templates";
        public string Archives { get; set; } = "./data/archives";
    }

    public class BackupPaths
    {
        public string Folder { get; set; } = "./bkp";
        public string Database { get; set; } = "./bkp/db";
        public string Files { get; set; } = "./bkp/files";
        public string System { get; set; } = "./bkp/system";
    }

    public class LogsPaths
    {
        public string Folder { get; set; } = "./log";
        public string Application { get; set; } = "./log/app";
        public string Security { get; set; } = "./log/security";
        public string Audit { get; set; } = "./log/audit";
        public string Performance { get; set; } = "./log/performance";
        public string Error { get; set; } = "./log/error";
    }

    public class ReportsPaths
    {
        public string Folder { get; set; } = "./rpt";
        public string Generated { get; set; } = "./rpt/generated";
        public string Templates { get; set; } = "./rpt/templates";
        public string Cache { get; set; } = "./rpt/cache";
        public string Exports { get; set; } = "./rpt/exports";
    }

    public class TempPaths
    {
        public string Folder { get; set; } = "./tmp";
        public string Cache { get; set; } = "./tmp/cache";
        public string Uploads { get; set; } = "./tmp/uploads";
        public string Downloads { get; set; } = "./tmp/downloads";
        public string Process { get; set; } = "./tmp/process";
    }

    public class ConfigPaths
    {
        public string Folder { get; set; } = "./cfg";
        public string License { get; set; } = "./cfg/license.dat";
        public string Settings { get; set; } = "./cfg/settings.json";
        public string Security { get; set; } = "./cfg/security.json";
        public string Modules { get; set; } = "./cfg/modules.json";
    }

    public class ModulesPaths
    {
        public string Folder { get; set; } = "./modules";
        public string Plugins { get; set; } = "./plugins";
        public string Extensions { get; set; } = "./extensions";
        public string Integrations { get; set; } = "./integrations";
    }

    public class AssetsPaths
    {
        public string Folder { get; set; } = "./Assets";
        public string Images { get; set; } = "./Assets/images";
        public string Icons { get; set; } = "./Assets/icons";
        public string Fonts { get; set; } = "./Assets/fonts";
        public string Themes { get; set; } = "./Assets/themes";
        public string Styles { get; set; } = "./Assets/styles";
        public string Sounds { get; set; } = "./Assets/sounds";
    }

    public class ResourcesPaths
    {
        public string Folder { get; set; } = "./Resources";
        public string Localization { get; set; } = "./Resources/localization";
        public string Templates { get; set; } = "./Resources/templates";
        public string Help { get; set; } = "./Resources/help";
        public string Docs { get; set; } = "./Resources/docs";
    }

    public class CachePaths
    {
        public string Folder { get; set; } = "./cache";
        public string Images { get; set; } = "./cache/images";
        public string Data { get; set; } = "./cache/data";
        public string Reports { get; set; } = "./cache/reports";
        public string Sessions { get; set; } = "./cache/sessions";
    }

    // -------------------- Languages --------------------
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
        public string NameEn { get; set; } = "";
        public string Culture { get; set; } = "";
        public bool IsRTL { get; set; }
        public string Icon { get; set; } = "";
        public bool Default { get; set; }
        public bool Enabled { get; set; } = true;
        public string ResourceFile { get; set; } = "";
        public string DateFormat { get; set; } = "dd/MM/yyyy";
        public string TimeFormat { get; set; } = "HH:mm:ss";
        public string FirstDayOfWeek { get; set; } = "Saturday";
    }

    public class TranslationServiceSettings
    {
        public bool Enabled { get; set; }
        public string Provider { get; set; } = "GoogleTranslate";
        public string ApiKey { get; set; } = "";
        public string Endpoint { get; set; } = "";
    }

    // -------------------- Currency --------------------
    public class CurrencySettings
    {
        public string DefaultCode { get; set; } = "SYP";
        public string DefaultSymbol { get; set; } = "ل.س";
        public CurrencyInfo SystemCurrency { get; set; } = new();
        public List<CurrencyInfo> Available { get; set; } = new();
        public ExchangeRatesSettings ExchangeRates { get; set; } = new();
        public RoundingSettings Rounding { get; set; } = new();
    }

    public class CurrencyInfo
    {
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public string NameEn { get; set; } = "";
        public string Symbol { get; set; } = "";
        public int DecimalPlaces { get; set; } = 2;
        public string SubUnitName { get; set; } = "";
        public int SubUnitRatio { get; set; } = 100;
        public string Format { get; set; } = "{0:N2} {1}";
        public double ExchangeRate { get; set; } = 1.0;
        public bool IsActive { get; set; } = true;
        public bool IsDefault { get; set; }
        public string Position { get; set; } = "Before";
        public string ThousandsSeparator { get; set; } = ",";
        public string DecimalSeparator { get; set; } = ".";
    }

    public class ExchangeRatesSettings
    {
        public string Provider { get; set; } = "CentralBank";
        public int UpdateIntervalHours { get; set; } = 6;
        public bool AutoUpdate { get; set; } = true;
        public string BaseCurrency { get; set; } = "SYP";
        public bool UseManualRates { get; set; } = true;
        public Dictionary<string, double> ManualRates { get; set; } = new();
    }

    public class RoundingSettings
    {
        public bool Enabled { get; set; } = true;
        public double Step { get; set; } = 0.01;
        public string Method { get; set; } = "MidPointRounding";
    }

    // -------------------- Modules --------------------
    public class ModulesSettings
    {
        public List<string> Enabled { get; set; } = new();
        public string DefaultStartupModule { get; set; } = "Dashboard";
        public List<ModuleInfo> AllModules { get; set; } = new();
    }

    public class ModuleInfo
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string NameEn { get; set; } = "";
        public string Description { get; set; } = "";
        public string DescriptionEn { get; set; } = "";
        public string Version { get; set; } = "10.6.0";
        public int Order { get; set; }
        public bool Enabled { get; set; } = true;
        public bool Required { get; set; }
        public string Icon { get; set; } = "";
        public List<string> Dependencies { get; set; } = new();
    }

    // -------------------- Branding --------------------
    public class BrandingSettings
    {
        
    }

   

    public class FontsSettings
    {
        public string PrimaryFont { get; set; } = "Segoe UI";
        public string SecondaryFont { get; set; } = "Tahoma";
        public string ArabicFont { get; set; } = "Tahoma";
        public string TitleFont { get; set; } = "Segoe UI";
        public string MonospaceFont { get; set; } = "Consolas";
        public FontSizesSettings FontSizes { get; set; } = new();
    }

    public class FontSizesSettings
    {
        public int Tiny { get; set; } = 8;
        public int Small { get; set; } = 10;
        public int Normal { get; set; } = 12;
        public int Medium { get; set; } = 14;
        public int Large { get; set; } = 16;
        public int XLarge { get; set; } = 20;
        public int XXLarge { get; set; } = 24;
        public int Title { get; set; } = 32;
        public int Header { get; set; } = 48;
    }

    public class ImagesSettings
    {
        public string LogoPath { get; set; } = "Assets/images/logo.png";
        public string SplashScreenPath { get; set; } = "Assets/images/splash.png";
        public string FaviconPath { get; set; } = "Assets/images/favicon.ico";
        public string AppIconPath { get; set; } = "Assets/images/app-icon.ico";
        public string LoginBackgroundPath { get; set; } = "Assets/images/login-bg.jpg";
        public string DashboardBackgroundPath { get; set; } = "Assets/images/dashboard-bg.jpg";
        public string EmptyStatePath { get; set; } = "Assets/images/empty-state.png";
    }

    

    public class AnimationsSettings
    {
        public bool Enabled { get; set; } = true;
        public int Duration { get; set; } = 200;
        public string Easing { get; set; } = "EaseInOut";
        public bool EnableTransitions { get; set; } = true;
        public bool EnableFadeEffects { get; set; } = true;
        public bool EnableSlideEffects { get; set; } = true;
    }

    // -------------------- Security --------------------
    public class SecuritySettings
    {
        public bool RequireDongle { get; set; } = true;
        public bool RequireLicense { get; set; } = true;
        public string LicenseFile { get; set; } = "./cfg/license.dat";
        public int SessionTimeoutMinutes { get; set; } = 30;
        public int SessionExtensionMinutes { get; set; } = 5;
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
        public int MinLength { get; set; } = 6;
        public int MaxLength { get; set; } = 32;
        public bool RequireUpperCase { get; set; } = true;
        public bool RequireLowerCase { get; set; } = true;
        public bool RequireNumbers { get; set; } = true;
        public bool RequireSpecialChars { get; set; }
        public int ExpirationDays { get; set; } = 90;
        public int PreventReuseCount { get; set; } = 5;
        public bool AllowCommonPasswords { get; set; }
    }

    public class TwoFactorAuthSettings
    {
        public bool Enabled { get; set; }
        public string Type { get; set; } = "SMS";
        public string SMSProvider { get; set; } = "Twilio";
        public string SMSApiKey { get; set; } = "";
        public string SMSPhoneNumber { get; set; } = "";
        public string EmailProvider { get; set; } = "Smtp";
        public string EmailAddress { get; set; } = "";
    }

    public class AuditSettings
    {
        public bool Enabled { get; set; } = true;
        public string LogLevel { get; set; } = "All";
        public long MaxLogEntries { get; set; } = 1000000;
        public int RetentionDays { get; set; } = 365;
        public bool ExcludeSensitiveData { get; set; } = true;
    }

    public class EncryptionSettings
    {
        public bool Enabled { get; set; } = true;
        public string Algorithm { get; set; } = "AES-256";
        public int KeySize { get; set; } = 256;
        public string Mode { get; set; } = "CBC";
        public string KeyFile { get; set; } = "./cfg/encryption.key";
        public string SaltFile { get; set; } = "./cfg/encryption.salt";
    }

    public class PermissionsSettings
    {
        public string DefaultRole { get; set; } = "User";
        public bool AllowCustomRoles { get; set; } = true;
        public bool AllowPermissionOverride { get; set; } = true;
    }

    // -------------------- AI --------------------
    public class AiSettings
    {
        public bool Enabled { get; set; }
        public string Provider { get; set; } = "Ollama";
        public string Model { get; set; } = "deepseek-coder-v2";
        public string Host { get; set; } = "http://localhost:11434";
        public string ApiKey { get; set; } = "";
        public int TimeoutSeconds { get; set; } = 30;
        public int MaxTokens { get; set; } = 4096;
        public double Temperature { get; set; } = 0.7;
        public double TopP { get; set; } = 0.9;
        public bool UseCache { get; set; } = true;
        public int CacheSize { get; set; } = 1000;
        public bool EnableLogging { get; set; } = true;
        public AiFeatures Features { get; set; } = new();
        public LlmSettings LLM { get; set; } = new();
    }

    public class AiFeatures
    {
        public bool InventoryPrediction { get; set; } = true;
        public bool SalesForecasting { get; set; } = true;
        public bool CustomerSegmentation { get; set; } = true;
        public bool AnomalyDetection { get; set; } = true;
        public bool SmartRecommendations { get; set; } = true;
        public bool NaturalLanguageQueries { get; set; } = true;
        public bool AutomatedReporting { get; set; } = true;
    }

    public class LlmSettings
    {
        public string Provider { get; set; } = "Ollama";
        public string Model { get; set; } = "deepseek-coder-v2";
        public string Endpoint { get; set; } = "http://localhost:11434/api/chat";
        public double Temperature { get; set; } = 0.7;
        public int MaxTokens { get; set; } = 4096;
        public int Timeout { get; set; } = 60;
    }

    // -------------------- Logging --------------------
    public class LoggingSettings
    {
        public Dictionary<string, string> LogLevel { get; set; } = new();
        public List<LogProviderSettings> Providers { get; set; } = new();
        public PerformanceLoggingSettings Performance { get; set; } = new();
        public MetricsSettings Metrics { get; set; } = new();
    }

    public class LogProviderSettings
    {
        public string Name { get; set; } = "";
        public bool Enabled { get; set; } = true;
        public string Level { get; set; } = "Information";
        public string Path { get; set; } = "";
        public string Format { get; set; } = "Json";
        public int MaxFileSizeMB { get; set; } = 100;
        public int MaxFiles { get; set; } = 30;
        public bool RollOnSize { get; set; } = true;
        public string Table { get; set; } = "";
        public int BatchSize { get; set; } = 100;
    }

    public class PerformanceLoggingSettings
    {
        public bool EnablePerformanceLogging { get; set; } = true;
        public bool LogSlowQueries { get; set; } = true;
        public int SlowQueryThresholdMs { get; set; } = 500;
        public bool LogMemoryUsage { get; set; } = true;
        public int MemoryCheckIntervalMinutes { get; set; } = 5;
        public bool LogCpuUsage { get; set; } = true;
        public int CpuCheckIntervalMinutes { get; set; } = 5;
    }

    public class MetricsSettings
    {
        public bool Enabled { get; set; } = true;
        public List<MetricsProvider> Providers { get; set; } = new();
    }

    public class MetricsProvider
    {
        public string Name { get; set; } = "";
        public bool Enabled { get; set; } = true;
        public string Endpoint { get; set; } = "";
        public int Port { get; set; }
    }

    // -------------------- Licensing --------------------
    public class LicensingSettings
    {
        public string Type { get; set; } = "PerUser";
        public int MaxUsers { get; set; } = 10;
        public int MaxCompanies { get; set; } = 1;
        public int MaxWarehouses { get; set; } = 5;
        public int MaxProducts { get; set; } = 10000;
        public string ExpiryDate { get; set; } = "2027-01-01";
        public List<string> Features { get; set; } = new();
        public DongleInfoSettings DongleInfo { get; set; } = new();
        public ActivationSettings Activation { get; set; } = new();
    }

    public class DongleInfoSettings
    {
        public int VendorId { get; set; }
        public int ProductId { get; set; }
        public string SerialNumber { get; set; } = "";
    }

    public class ActivationSettings
    {
        public bool RequireOnlineActivation { get; set; } = true;
        public string ActivationServer { get; set; } = "";
        public bool OfflineActivationAllowed { get; set; } = true;
        public bool HardwareIdBased { get; set; } = true;
    }

    // -------------------- MultiCompany --------------------
    public class MultiCompanySettings
    {
        public bool Enabled { get; set; } = true;
        public string DefaultCompanyCode { get; set; } = string.Empty;
        public List<CompanyInfo> Companies { get; set; } = new();
        public BranchesSettings Branches { get; set; } = new();
    }

    public class CompanyInfo
    {
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public string NameEn { get; set; } = "";
        public string Database { get; set; } = "";
        public bool IsDefault { get; set; }
        public bool Enabled { get; set; } = true;
    }

    public class BranchesSettings
    {
        public bool Enabled { get; set; } = true;
        public string DefaultBranchCode { get; set; } = "001";
        public List<BranchInfo> Branches { get; set; } = new();
    }

    public class BranchInfo
    {
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public string NameEn { get; set; } = "";
        public string Address { get; set; } = "";
        public string Phone { get; set; } = "";
        public bool IsDefault { get; set; }
        public bool Enabled { get; set; } = true;
    }

    // -------------------- Integrations --------------------
    public class IntegrationsSettings
    {
        public RestApiSettings RestApi { get; set; } = new();
    }

    public class RestApiSettings
    {
        public bool Enabled { get; set; } = true;
        public string Endpoint { get; set; } = "";
        public string SwaggerEndpoint { get; set; } = "";
    }

    // -------------------- Online Services --------------------
    public class OnlineServicesSettings
    {
        public bool Enabled { get; set; } = true;
        public int ConnectivityCheckIntervalSeconds { get; set; } = 60;
        /// <summary>Mandatory background sync/backup interval (default: every hour).</summary>
        public int BackgroundSyncIntervalSeconds { get; set; } = 3600;
        /// <summary>Minimum hours between automatic backups while app is running.</summary>
        public int BackupIntervalHours { get; set; } = 1;
        /// <summary>Max backup copies kept on server per device (1 or 2).</summary>
        public int MaxServerBackupCopies { get; set; } = 2;
        public bool BackupOnStartup { get; set; } = true;
        public string SyncEndpoint { get; set; } = "/sync";
        public string BackupUploadEndpoint { get; set; } = "/backup/upload";
        public string DeviceRegisterEndpoint { get; set; } = "/device/register";
        public string LicenseValidateEndpoint { get; set; } = "/license/validate";
    }
}
