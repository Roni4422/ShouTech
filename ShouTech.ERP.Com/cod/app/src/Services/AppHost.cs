using System.Windows;
using ShouTech.App.Configuration;
using ShouTech.Application.Services;
using ShouTech.Domain.Interfaces;
using ShouTech.Infrastructure;

namespace ShouTech.App.Services
{
    /// <summary>
    /// Composition root for application-wide services (lightweight service locator).
    /// </summary>
    public static class AppHost
    {
        private static bool _initialized;

        public static ILogServ Logging { get; private set; } = null!;
        public static VisuId Visual { get; private set; } = new VisuId();
        public static INavServ Navigation { get; private set; } = null!;
        public static IAuthSvc Auth { get; private set; } = new AuthServ();
        public static IDataServ Data { get; private set; } = new DataServ();
        public static ICoCreateSvc CompanyCreation { get; private set; } = new CoCreateSvc();
        public static IPermServ Permissions { get; private set; } = new PermServ();
        public static IBkpServ Backup { get; private set; } = null!;
        public static IConnSvc Connectivity { get; private set; } = null!;
        public static IDevInfoSv DeviceInfo { get; private set; } = null!;
        public static ILicenSvc License { get; private set; } = null!;
        public static ISyncSvc Sync { get; private set; } = null!;
        public static IOnlCoord OnlineCoordinator { get; private set; } = null!;

        // Application services
        public static ISessionManager Session { get; private set; } = new SessionManager();
        public static IAuditService Audit { get; private set; } = null!;
        public static JournalService? Journal { get; private set; }
        public static JournalRepository? JournalStore { get; private set; }
        public static AccountingStructureRepository? AccountingStructure { get; private set; }
        public static IDatabaseHealth? DatabaseHealth { get; private set; }

        public static ILocSvc Localization =>
            System.Windows.Application.Current is App app
                ? app.Localization
                : _fallbackLocalization;

        private static readonly LocSvc _fallbackLocalization = new();

        public static void Initialize()
        {
            if (_initialized)
                return;

            Logging = new LogServ();

            try
            {
                AppCfgSvc.Load();
                Logging.LogFileAccess(
                    "cfg/appset.json",
                    "Application Configuration",
                    success: true);
            }
            catch (System.Exception ex)
            {
                Logging.LogFileAccess(
                    "cfg/appset.json",
                    "Application Configuration",
                    success: false,
                    reason: ex.Message);
            }

            // Visual identity — Light only
            Visual = new VisuId();

            Navigation = new NavServ(Logging);
            Auth = new AuthServ();
            Data = new DataServ();
            CompanyCreation = new CoCreateSvc();
            Permissions = new PermServ();

            Journal = CreateJournalService();
            AccountingStructure = CreateAccountingStructureRepository();
            DatabaseHealth = CreateDatabaseHealth();

            Connectivity = new ConnServ(Logging);
            DeviceInfo = new DevInfoSv(Logging);
            License = new LicenSvc(Logging, DeviceInfo);
            Sync = new SyncServ(Logging, DeviceInfo);
            Backup = new BkpServ(Logging, DeviceInfo, Sync);

            OnlineCoordinator = new OnlCoord(
                Connectivity,
                License,
                Backup,
                Sync,
                DeviceInfo,
                Logging);

            // Audit service
            Audit = new AuditService(Logging);

            // Initialize visual identity.
            // VisuId handles the visual resources and saved accent color.
            Visual.Initialize();

            var connectionString = DatabaseRuntime.GetConnectionString();

            Logging.LogFileAccess(
                "SHOUTECH_CONNECTION_STRING",
                "Database Connection",
                success: !string.IsNullOrWhiteSpace(connectionString),
                reason: string.IsNullOrWhiteSpace(connectionString)
                    ? "not configured"
                    : null);

            _initialized = true;

            Logging.LogInfo(
                "AppHost",
                "Services initialized (offline-first)");

            // Register system startup audit
            _ = Audit.LogSystemStartAsync();
        }

        public static void RebindDatabaseContext()
        {
            try
            {
                // Re-read connection string from current config.
                var cs = DatabaseRuntime.GetConnectionString();

                // DatabaseRuntime reads the current configuration
                // when the connection is requested.
            }
            catch
            {
                // Intentionally ignored.
            }
        }

        private static JournalService? CreateJournalService()
        {
            var connection = ResolveDatabaseConnection();

            if (string.IsNullOrWhiteSpace(connection))
            {
                connection =
                    AppCfgSvc.Current.ConnectionStrings?
                        .Values
                        .FirstOrDefault()?
                        .ConnectionString;
            }

            if (string.IsNullOrWhiteSpace(connection))
            {
                Logging.LogWarning(
                    "AppHost",
                    "Journal persistence is unavailable: no database connection configured.");

                return null;
            }

            var provider = AppCfgSvc.Current.ConnectionStrings?
                .FirstOrDefault(item => item.Key.Equals("Default", System.StringComparison.OrdinalIgnoreCase))
                .Value?.Provider ?? AppCfgSvc.Current.Database?.DefaultProvider ?? "SQLite";

            try
            {
                JournalStore = new JournalRepository(provider, connection);
                return new JournalService(JournalStore);
            }
            catch (System.Exception ex)
            {
                Logging.LogWarning("AppHost", $"Journal persistence could not be initialized: {ex.Message}");
                return null;
            }
        }

        private static AccountingStructureRepository? CreateAccountingStructureRepository()
        {
            var connection = ResolveDatabaseConnection();
            if (string.IsNullOrWhiteSpace(connection))
                return null;

            var provider = AppCfgSvc.Current.ConnectionStrings?
                .FirstOrDefault(item => item.Key.Equals("Default", System.StringComparison.OrdinalIgnoreCase))
                .Value?.Provider ?? AppCfgSvc.Current.Database?.DefaultProvider ?? "SQLite";
            try
            {
                return new AccountingStructureRepository(provider, connection);
            }
            catch (System.Exception ex)
            {
                Logging.LogWarning("AppHost", $"Accounting structure services could not be initialized: {ex.Message}");
                return null;
            }
        }

        private static DatabaseHealth? CreateDatabaseHealth()
        {
            var connection = ResolveDatabaseConnection();
            if (string.IsNullOrWhiteSpace(connection))
                return null;

            var provider = AppCfgSvc.Current.ConnectionStrings?
                .FirstOrDefault(item => item.Key.Equals("Default", System.StringComparison.OrdinalIgnoreCase))
                .Value?.Provider ?? AppCfgSvc.Current.Database?.DefaultProvider ?? "SQLite";
            return new DatabaseHealth(provider, connection);
        }

        private static string? ResolveDatabaseConnection()
        {
            var connection =
                AppCfgSvc.Current.ConnectionStrings?
                    .FirstOrDefault(item =>
                        item.Key.Equals(
                            "Default",
                            System.StringComparison.OrdinalIgnoreCase))
                    .Value?
                    .ConnectionString;

            return string.IsNullOrWhiteSpace(connection)
                ? AppCfgSvc.Current.ConnectionStrings?
                    .Values
                    .FirstOrDefault()?
                    .ConnectionString
                : connection;
        }
    }

    public static class AppServiceHost
    {
        public static ILogServ Logging => AppHost.Logging;
        public static VisuId Visual => AppHost.Visual;
        public static INavServ Navigation => AppHost.Navigation;
        public static IAuthSvc Auth => AppHost.Auth;
        public static IDataServ Data => AppHost.Data;
        public static ICoCreateSvc CompanyCreation => AppHost.CompanyCreation;
        public static IPermServ Permissions => AppHost.Permissions;
        public static IBkpServ Backup => AppHost.Backup;
        public static IConnSvc Connectivity => AppHost.Connectivity;
        public static IDevInfoSv DeviceInfo => AppHost.DeviceInfo;
        public static ILicenSvc License => AppHost.License;
        public static ISyncSvc Sync => AppHost.Sync;
        public static IOnlCoord OnlineCoordinator => AppHost.OnlineCoordinator;
        public static ISessionManager Session => AppHost.Session;
        public static IAuditService Audit => AppHost.Audit;
        public static JournalService? Journal => AppHost.Journal;
        public static AccountingStructureRepository? AccountingStructure => AppHost.AccountingStructure;
        public static IDatabaseHealth? DatabaseHealth => AppHost.DatabaseHealth;
        public static ILocSvc Localization => AppHost.Localization;

        public static void Initialize() => AppHost.Initialize();
    }

    public static class LanguageDirectionService
    {
        public const string DefaultLanguageCode =
            LangDirSv.DefaultLanguageCode;

        public static bool IsRtl(string? languageCode) =>
            LangDirSv.IsRtl(languageCode);

        public static FlowDirection GetFlowDirection(
            string? languageCode) =>
            LangDirSv.GetFlowDirection(languageCode);

        public static void ApplyCulture(string languageCode) =>
            LangDirSv.ApplyCulture(languageCode);

        public static void ApplyToWindow(
            Window window,
            string? languageCode = null) =>
            LangDirSv.ApplyToWindow(window, languageCode);

        public static void ApplyToAllWindows(string languageCode) =>
            LangDirSv.ApplyToAllWindows(languageCode);

        public static string NormalizeLanguageCode(
            string? languageCode) =>
            LangDirSv.NormalizeLanguageCode(languageCode);

        // ═══════════════════════════════════════════════════════════
        // v0.2.0 additions — Company switch
        // ═══════════════════════════════════════════════════════════
    }
}

