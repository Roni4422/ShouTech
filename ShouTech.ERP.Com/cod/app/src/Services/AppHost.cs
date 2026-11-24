using System.Windows;
using ShouTech.App.Configuration;
using ShouTech.Application.Services;
using ShouTech.Domain.Interfaces;
using ShouTech.Infrastructure;

using ShouTech.Domain;
namespace ShouTech.App.Services
{
    /// <summary>
    /// Composition root for application-wide services (lightweight service locator).
    /// </summary>
    public static class AppHost
    {
        private static bool _initialized;

        public static ILogServ Logging { get; private set; } = null!;

        /// <summary>
        /// The numbered error catalog (docs/ERROR_CATALOG.md). Available
        /// before <see cref="Initialize"/> so even startup error paths can
        /// report with a catalog code: AppHost.Errors.Report(code, ex, context).
        /// </summary>
        public static IErrorCatalog Errors { get; } = new ErrorCatalogSvc(() => Localization.GetCurrentLanguage());
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
        public static BudgetService? Budgets { get; private set; }
        public static QualityService? Quality { get; private set; }
        public static CrmService? Crm { get; private set; }
        public static CommService? Commissions { get; private set; }
        public static ContractService? Contracts { get; private set; }
        public static HrService? Hr { get; private set; }
        public static PosService? Pos { get; private set; }
        public static ProjectService? Projects { get; private set; }
        public static AssetService? Assets { get; private set; }
        public static ReconciliationService? Reconciliation { get; private set; }
        public static PurchaseMatchService? PurchaseMatch { get; private set; }
        public static PurchaseLandedCostService? PurchaseLandedCosts { get; private set; }
        public static PurchaseRequisitionWorkflowService? PurchaseRequisitions { get; private set; }
        public static PurchaseDocumentAttachmentService? PurchaseAttachments { get; private set; }
        public static MasterCardService? MasterCards { get; private set; }
        public static ChequeService? Cheques { get; private set; }
        public static MfgOrderService? Manufacturing { get; private set; }
        public static BomService? Boms { get; private set; }
        public static PlanningService? Planning { get; private set; }
        public static OrderService? Orders { get; private set; }
        public static DefinitionService? Definitions { get; private set; }
        public static OperationsService? Operations { get; private set; }
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
            Budgets = CreatePhase3Service((provider, connection) => new BudgetService(new BudgetRepository(provider, connection), Permissions.Has), "Budgets");
            Quality = CreatePhase3Service((provider, connection) => new QualityService(new QualityRepository(provider, connection)), "Quality");
            Crm = CreatePhase3Service((provider, connection) => new CrmService(new CrmRepository(provider, connection)), "CRM");
            Commissions = CreatePhase3Service((provider, connection) => new CommService(new CommRepository(provider, connection), JournalStore), "Commissions");
            Contracts = CreatePhase3Service((provider, connection) => new ContractService(new ContractRepository(provider, connection)), "Contracts");
            Hr = CreatePhase3Service((provider, connection) => new HrService(new HrRepository(provider, connection)), "HR");
            Pos = CreatePhase3Service((provider, connection) => new PosService(new PosRepository(provider, connection)), "POS");
            Projects = CreatePhase3Service((provider, connection) => new ProjectService(new ProjectRepository(provider, connection)), "Projects");
            Assets = CreatePhase3Service((provider, connection) => new AssetService(new AssetRepository(provider, connection)), "Fixed Assets");
            Reconciliation = CreatePhase3Service((provider, connection) => new ReconciliationService(new ReconciliationRepository(provider, connection)), "Payment Reconciliation");
            MasterCards = CreatePhase3Service((provider, connection) => new MasterCardService(new MasterCardRepository(provider, connection)), "Master Cards");
            Cheques = CreatePhase3Service((provider, connection) => new ChequeService(new ChequeRepository(provider, connection), JournalStore), "Cheques");
            Manufacturing = CreatePhase3Service((provider, connection) => new MfgOrderService(new MfgOrderRepository(provider, connection)), "Manufacturing Orders");
            Boms = CreatePhase3Service((provider, connection) => new BomService(new BomRepository(provider, connection)), "Bills of Materials");
            Planning = CreatePhase3Service((provider, connection) => new PlanningService(new PlanningRepository(provider, connection)), "Manufacturing Planning");
            Orders = CreatePhase3Service((provider, connection) => CreateOrderService(provider, connection), "Orders");
            PurchaseRequisitions = CreatePhase3Service((provider, connection) => new PurchaseRequisitionWorkflowService(new PurchaseRequisitionWorkflowRepository(provider, connection), Orders!, Permissions.Has), "Purchase Requisitions");
            Definitions = CreatePhase3Service((provider, connection) => new DefinitionService(new DefinitionRepository(provider, connection)), "Definitions");
            Operations = CreatePhase3Service((provider, connection) => new OperationsService(new OperationsRepository(provider, connection)), "Operations");
            AccountingStructure = CreateAccountingStructureRepository();
            DatabaseHealth = CreateDatabaseHealth();

            Connectivity = new ConnServ(Logging);
            DeviceInfo = new DevInfoSv(Logging);
            License = new LicenSvc(Logging, DeviceInfo);
            Sync = new SyncServ(Logging, DeviceInfo);
            Backup = new BkpServ(Logging, DeviceInfo, Sync, Permissions);

            OnlineCoordinator = new OnlCoord(
                Connectivity,
                License,
                Backup,
                Sync,
                DeviceInfo,
                Logging);

            // Audit service
            Audit = new AuditService(Logging);
            PurchaseAttachments = CreatePhase3Service((provider, connection) => new PurchaseDocumentAttachmentService(new PurchaseDocumentAttachmentRepository(provider, connection), Orders!, Permissions.Has, (user, action, details) => Audit.LogUserActionAsync(user, action, details)), "Purchase Attachments");
            PurchaseMatch = CreatePhase3Service((provider, connection) => new PurchaseMatchService(
                new PurchaseMatchPolicyRepository(provider, connection),
                Permissions.Has,
                (user, action, details) => Audit.LogUserActionAsync(user, action, details)), "Purchase Match Controls");
            PurchaseLandedCosts = CreatePhase3Service((provider, connection) => new PurchaseLandedCostService(new PurchaseLandedCostRepository(provider, connection), Permissions.Has), "Purchase Landed Costs");

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

        /// <summary>
        /// Rebinds every connection-bound service to the CURRENT runtime
        /// database. Each company has its own database (ComWin's
        /// ConfigureRuntimeDatabase rewrites the "Default" connection when
        /// the shell switches company and then calls this method), while
        /// Journal / Projects / Assets / Reconciliation /
        /// AccountingStructure / DatabaseHealth capture their connection at
        /// construction — without this rebind they would keep reading and
        /// writing the PREVIOUS company's database after a switch. All
        /// other services (Data, Auth, Permissions, backup/export) resolve
        /// DatabaseRuntime.GetConnectionString() per call and need no
        /// rebind. Never throws: a failed rebind leaves the affected
        /// services null and their windows report it honestly.
        /// </summary>
        public static void RebindDatabaseContext()
        {
            if (!_initialized)
                return;

            try
            {
                Journal = CreateJournalService();
                Budgets = CreatePhase3Service((provider, connection) => new BudgetService(new BudgetRepository(provider, connection), Permissions.Has), "Budgets");
                Quality = CreatePhase3Service((provider, connection) => new QualityService(new QualityRepository(provider, connection)), "Quality");
                Crm = CreatePhase3Service((provider, connection) => new CrmService(new CrmRepository(provider, connection)), "CRM");
            Commissions = CreatePhase3Service((provider, connection) => new CommService(new CommRepository(provider, connection), JournalStore), "Commissions");
            Contracts = CreatePhase3Service((provider, connection) => new ContractService(new ContractRepository(provider, connection)), "Contracts");
            Hr = CreatePhase3Service((provider, connection) => new HrService(new HrRepository(provider, connection)), "HR");
            Pos = CreatePhase3Service((provider, connection) => new PosService(new PosRepository(provider, connection)), "POS");
                Projects = CreatePhase3Service((provider, connection) => new ProjectService(new ProjectRepository(provider, connection)), "Projects");
                Assets = CreatePhase3Service((provider, connection) => new AssetService(new AssetRepository(provider, connection)), "Fixed Assets");
                Reconciliation = CreatePhase3Service((provider, connection) => new ReconciliationService(new ReconciliationRepository(provider, connection)), "Payment Reconciliation");
                Manufacturing = CreatePhase3Service((provider, connection) => new MfgOrderService(new MfgOrderRepository(provider, connection)), "Manufacturing Orders");
                Boms = CreatePhase3Service((provider, connection) => new BomService(new BomRepository(provider, connection)), "Bills of Materials");
                Planning = CreatePhase3Service((provider, connection) => new PlanningService(new PlanningRepository(provider, connection)), "Manufacturing Planning");
                Orders = CreatePhase3Service((provider, connection) => CreateOrderService(provider, connection), "Orders");
            PurchaseRequisitions = CreatePhase3Service((provider, connection) => new PurchaseRequisitionWorkflowService(new PurchaseRequisitionWorkflowRepository(provider, connection), Orders!, Permissions.Has), "Purchase Requisitions");
                PurchaseAttachments = CreatePhase3Service((provider, connection) => new PurchaseDocumentAttachmentService(new PurchaseDocumentAttachmentRepository(provider, connection), Orders!, Permissions.Has, (user, action, details) => Audit.LogUserActionAsync(user, action, details)), "Purchase Attachments");
                PurchaseMatch = CreatePhase3Service((provider, connection) => new PurchaseMatchService(
                    new PurchaseMatchPolicyRepository(provider, connection),
                    Permissions.Has,
                    (user, action, details) => Audit.LogUserActionAsync(user, action, details)), "Purchase Match Controls");
                PurchaseLandedCosts = CreatePhase3Service((provider, connection) => new PurchaseLandedCostService(new PurchaseLandedCostRepository(provider, connection), Permissions.Has), "Purchase Landed Costs");
                AccountingStructure = CreateAccountingStructureRepository();
                DatabaseHealth = CreateDatabaseHealth();

                Logging.LogInfo(
                    "AppHost",
                    $"Database context rebound to the current company database (company '{AppState.CurrentCompanyCode}').");
            }
            catch (Exception exL146)
            {
                AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, exL146, "AppHost.RebindDatabaseContext");
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

        /// <summary>
        /// OrderService factory with the Phase 09 commission hook wired:
        /// after a chain sales invoice posts, commission entries are
        /// generated through CommService. FAIL-SOFT by design — the hook
        /// catches and reports every failure itself, because a commission
        /// problem must never break invoice posting.
        /// </summary>
        private static OrderService CreateOrderService(string provider, string connection)
        {
            var orders = new OrderService(
                new OrderDocumentRepository(provider, connection),
                new CommercialDocumentRepository(provider, connection));
            orders.CommissionHook = async (companyKey, docId, userId, cancellationToken) =>
            {
                try
                {
                    if (Commissions is not null)
                        await Commissions.GenerateForInvoiceAsync(companyKey, docId, userId, cancellationToken);
                }
                catch (System.Exception ex)
                {
                    Errors.Report(ErrorCodes.WindowOperationFailed, ex, "AppHost.CommissionHook");
                }
            };
            return orders;
        }

        /// <summary>
        /// Shared factory for the Phase 3 module services (Projects, Fixed
        /// Assets, Payment Reconciliation): resolves the active database
        /// connection/provider exactly like the journal factory and never
        /// throws during startup - a missing database leaves the service null
        /// and the corresponding window reports it honestly.
        /// </summary>
        private static TService? CreatePhase3Service<TService>(
            Func<string, string, TService> factory, string name) where TService : class
        {
            var connection = ResolveDatabaseConnection();
            if (string.IsNullOrWhiteSpace(connection))
            {
                Logging.LogWarning("AppHost", $"{name} persistence is unavailable: no database connection configured.");
                return null;
            }

            var provider = AppCfgSvc.Current.ConnectionStrings?
                .FirstOrDefault(item => item.Key.Equals("Default", System.StringComparison.OrdinalIgnoreCase))
                .Value?.Provider ?? AppCfgSvc.Current.Database?.DefaultProvider ?? "SQLite";
            try
            {
                return factory(provider, connection);
            }
            catch (System.Exception ex)
            {
                Logging.LogWarning("AppHost", $"{name} persistence could not be initialized: {ex.Message}");
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

            var raw = string.IsNullOrWhiteSpace(connection)
                ? AppCfgSvc.Current.ConnectionStrings?
                    .Values
                    .FirstOrDefault()?
                    .ConnectionString
                : connection;

            // Phase 4: repositories receive centrally prepared connection
            // strings (SQLite foreign-keys pragma; PostgreSQL SSL mode from
            // SHOUTECH_PG_SSLMODE) — same preparation as DatabaseRuntime.
            return DatabaseRuntime.PrepareConnectionString(raw);
        }
    }

    public static class AppServiceHost
    {
        public static ILogServ Logging => AppHost.Logging;
        public static IErrorCatalog Errors => AppHost.Errors;
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
        public static BudgetService? Budgets => AppHost.Budgets;
        public static QualityService? Quality => AppHost.Quality;
        public static CrmService? Crm => AppHost.Crm;
        public static CommService? Commissions => AppHost.Commissions;
        public static ContractService? Contracts => AppHost.Contracts;
        public static HrService? Hr => AppHost.Hr;
        public static PosService? Pos => AppHost.Pos;
        public static ProjectService? Projects => AppHost.Projects;
        public static AssetService? Assets => AppHost.Assets;
        public static ReconciliationService? Reconciliation => AppHost.Reconciliation;
        public static PurchaseMatchService? PurchaseMatch => AppHost.PurchaseMatch;
        public static PurchaseLandedCostService? PurchaseLandedCosts => AppHost.PurchaseLandedCosts;
        public static MasterCardService? MasterCards => AppHost.MasterCards;
        public static ChequeService? Cheques => AppHost.Cheques;
        public static MfgOrderService? Manufacturing => AppHost.Manufacturing;
        public static BomService? Boms => AppHost.Boms;
        public static PlanningService? Planning => AppHost.Planning;
        public static OrderService? Orders => AppHost.Orders;
        public static PurchaseRequisitionWorkflowService? PurchaseRequisitions => AppHost.PurchaseRequisitions;
        public static PurchaseDocumentAttachmentService? PurchaseAttachments => AppHost.PurchaseAttachments;
        public static DefinitionService? Definitions => AppHost.Definitions;
        public static OperationsService? Operations => AppHost.Operations;
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

