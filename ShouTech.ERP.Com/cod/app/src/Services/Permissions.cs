// ========================================================================
// FILE: Services/Permissions.cs
// PROJECT: SHOUTECH ERP — Permission catalog, runtime checks, admin
// Merged: IPermServ, PermCat, PermServ, PermAdminSvc
// ========================================================================

#nullable enable

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System;
using Microsoft.Data.Sqlite;
using Npgsql;
using NpgsqlTypes;

namespace ShouTech.App.Services
{

    // ========== IPermServ.cs ==========
/// <summary>
    /// Permission keys used across the system.
    /// Keep values stable after release.
    /// </summary>
    public static class PermKey
    {
        // System
        public const string SysAdmin = "SYS.ADMIN";
        public const string SysSettings = "SYS.SETTINGS";
        public const string SysUsers = "SYS.USERS";
        public const string SysBackup = "SYS.BACKUP";

        // Accounting
        public const string AccView = "ACC.VIEW";
        public const string AccCreate = "ACC.CREATE";
        public const string AccPost = "ACC.POST";
        public const string AccDelete = "ACC.DELETE";

        // Inventory
        public const string InvView = "INV.VIEW";
        public const string InvCreate = "INV.CREATE";
        public const string InvMove = "INV.MOVE";
        public const string InvDelete = "INV.DELETE";

        // Sales
        public const string SalesView = "SALES.VIEW";
        public const string SalesCreate = "SALES.CREATE";
        public const string SalesPost = "SALES.POST";

        // Purchase
        public const string PurchView = "PURCH.VIEW";
        public const string PurchCreate = "PURCH.CREATE";
        public const string PurchPost = "PURCH.POST";
    }

    public interface IPermServ
    {
        /// <summary>
        /// Returns true if the current user has the given permission.
        /// </summary>
        bool Has(string permissionKey);

        /// <summary>
        /// Returns true if the current user has ALL of the given permissions.
        /// </summary>
        bool HasAll(params string[] permissionKeys);

        /// <summary>
        /// Returns true if the current user has ANY of the given permissions.
        /// </summary>
        bool HasAny(params string[] permissionKeys);

        /// <summary>
        /// Loads permissions for the given user (called after login).
        /// </summary>
        Task LoadForUserAsync(string userId, CancellationToken ct = default);

        /// <summary>
        /// Clears loaded permissions (called on logout).
        /// </summary>
        void Clear();

        /// <summary>
        /// Returns a snapshot of currently loaded permissions.
        /// </summary>
        IReadOnlyCollection<string> GetCurrentPermissions();
    }

    // ========== PermCat.cs ==========
/// <summary>
    /// Central catalog of all application permissions.
    ///
    /// Permission format:
    ///
    ///     MODULE.RESOURCE.ACTION
    ///
    /// Example:
    ///
    ///     SALES.INVOICE.POST
    /// </summary>
    public static class PermCat
    {
        // =====================================================================
        // SYSTEM
        // =====================================================================

        public static class System
        {
            public const string Admin = "SYS.ADMIN";

            public const string SettingsView = "SYS.SETTINGS.VIEW";
            public const string SettingsUpdate = "SYS.SETTINGS.UPDATE";

            public const string UsersView = "SYS.USERS.VIEW";
            public const string UsersCreate = "SYS.USERS.CREATE";
            public const string UsersUpdate = "SYS.USERS.UPDATE";
            public const string UsersDisable = "SYS.USERS.DISABLE";
            public const string UsersDelete = "SYS.USERS.DELETE";

            public const string RolesView = "SYS.ROLES.VIEW";
            public const string RolesCreate = "SYS.ROLES.CREATE";
            public const string RolesUpdate = "SYS.ROLES.UPDATE";
            public const string RolesDelete = "SYS.ROLES.DELETE";

            public const string PermissionsView = "SYS.PERMISSIONS.VIEW";
            public const string PermissionsManage = "SYS.PERMISSIONS.MANAGE";
            public const string PermissionTemplatesManage =
                "SYS.PERMISSION_TEMPLATES.MANAGE";

            public const string Maintenance = "SYS.MAINTENANCE";
            public const string FiscalRollForward = "ACC.FISCAL.ROLL_FORWARD";
            public const string PluginsManage = "SYS.PLUGINS.MANAGE";
        }

        // =====================================================================
        // COMPANY / ORGANIZATION
        // =====================================================================

        public static class Company
        {
            public const string View = "ORG.COMPANY.VIEW";
            public const string Create = "ORG.COMPANY.CREATE";
            public const string Update = "ORG.COMPANY.UPDATE";
            public const string Delete = "ORG.COMPANY.DELETE";

            public const string BranchView = "ORG.BRANCH.VIEW";
            public const string BranchCreate = "ORG.BRANCH.CREATE";
            public const string BranchUpdate = "ORG.BRANCH.UPDATE";
            public const string BranchDelete = "ORG.BRANCH.DELETE";

            public const string WarehouseView = "ORG.WAREHOUSE.VIEW";
            public const string WarehouseCreate = "ORG.WAREHOUSE.CREATE";
            public const string WarehouseUpdate = "ORG.WAREHOUSE.UPDATE";
            public const string WarehouseDelete = "ORG.WAREHOUSE.DELETE";
        }

        // =====================================================================
        // INVENTORY
        // =====================================================================

        public static class Inventory
        {
            public const string ItemView = "INV.ITEM.VIEW";
            public const string ItemCreate = "INV.ITEM.CREATE";
            public const string ItemUpdate = "INV.ITEM.UPDATE";
            public const string ItemDelete = "INV.ITEM.DELETE";

            public const string CategoryView = "INV.CATEGORY.VIEW";
            public const string CategoryCreate = "INV.CATEGORY.CREATE";
            public const string CategoryUpdate = "INV.CATEGORY.UPDATE";
            public const string CategoryDelete = "INV.CATEGORY.DELETE";

            public const string WarehouseView = "INV.WAREHOUSE.VIEW";
            public const string WarehouseCreate = "INV.WAREHOUSE.CREATE";
            public const string WarehouseUpdate = "INV.WAREHOUSE.UPDATE";
            public const string WarehouseDelete = "INV.WAREHOUSE.DELETE";

            public const string StockView = "INV.STOCK.VIEW";
            public const string StockTransfer = "INV.STOCK.TRANSFER";
            public const string StockAdjust = "INV.STOCK.ADJUST";
            public const string StockCount = "INV.STOCK.COUNT";
            public const string StockPost = "INV.STOCK.POST";

            public const string BarcodeView = "INV.BARCODE.VIEW";
            public const string BarcodeGenerate = "INV.BARCODE.GENERATE";
            public const string BarcodeDesign = "INV.BARCODE.DESIGN";
            public const string BarcodePrint = "INV.BARCODE.PRINT";

            public const string PriceListView = "INV.PRICELIST.VIEW";
            public const string PriceListCreate = "INV.PRICELIST.CREATE";
            public const string PriceListUpdate = "INV.PRICELIST.UPDATE";

            public const string SerialView = "INV.SERIAL.VIEW";
            public const string SerialUpdate = "INV.SERIAL.UPDATE";

            public const string ExpiryView = "INV.EXPIRY.VIEW";

            public const string ItemDiscountView = "INV.DISCOUNT.VIEW";
            public const string ItemDiscountManage = "INV.DISCOUNT.MANAGE";
        }

        // =====================================================================
        // ACCOUNTING
        // =====================================================================

        public static class Accounting
        {
            public const string AccountView = "ACC.ACCOUNT.VIEW";
            public const string AccountCreate = "ACC.ACCOUNT.CREATE";
            public const string AccountUpdate = "ACC.ACCOUNT.UPDATE";
            public const string AccountDelete = "ACC.ACCOUNT.DELETE";

            public const string JournalView = "ACC.JOURNAL.VIEW";
            public const string JournalCreate = "ACC.JOURNAL.CREATE";
            public const string JournalUpdate = "ACC.JOURNAL.UPDATE";
            public const string JournalPost = "ACC.JOURNAL.POST";
            public const string JournalReverse = "ACC.JOURNAL.REVERSE";

            public const string LedgerView = "ACC.LEDGER.VIEW";

            public const string CustomerLedgerView =
                "ACC.CUSTOMER_LEDGER.VIEW";

            public const string CostCenterView = "ACC.COSTCENTER.VIEW";
            public const string CostCenterManage = "ACC.COSTCENTER.MANAGE";

            public const string CurrencyView = "ACC.CURRENCY.VIEW";
            public const string CurrencyManage = "ACC.CURRENCY.MANAGE";

            public const string ExchangeRateView = "ACC.EXCHANGE_RATE.VIEW";
            public const string ExchangeRateManage = "ACC.EXCHANGE_RATE.MANAGE";

            public const string TrialBalanceView = "ACC.TRIAL_BALANCE.VIEW";

            public const string FinancialStatementsView =
                "ACC.FINANCIAL_STATEMENTS.VIEW";

            public const string FinancialAnalysisView =
                "ACC.FINANCIAL_ANALYSIS.VIEW";

            public const string FinancialAnalysisAi =
                "ACC.FINANCIAL_ANALYSIS.AI";
        }

        // =====================================================================
        // VOUCHERS
        // =====================================================================

        public static class Vouchers
        {
            public const string View = "VCH.VOUCHER.VIEW";
            public const string Create = "VCH.VOUCHER.CREATE";
            public const string Update = "VCH.VOUCHER.UPDATE";
            public const string Delete = "VCH.VOUCHER.DELETE";
            public const string Post = "VCH.VOUCHER.POST";
            public const string Reverse = "VCH.VOUCHER.REVERSE";

            public const string ReceiptCreate = "VCH.RECEIPT.CREATE";
            public const string PaymentCreate = "VCH.PAYMENT.CREATE";

            public const string SecuritiesView = "VCH.SECURITY.VIEW";
            public const string SecuritiesManage = "VCH.SECURITY.MANAGE";

            public const string InstallmentView = "VCH.INSTALLMENT.VIEW";
            public const string InstallmentManage = "VCH.INSTALLMENT.MANAGE";

            public const string PaymentMethodManage =
                "VCH.PAYMENT_METHOD.MANAGE";
        }

        // =====================================================================
        // SALES
        // =====================================================================

        public static class Sales
        {
            public const string InvoiceView = "SALES.INVOICE.VIEW";
            public const string InvoiceCreate = "SALES.INVOICE.CREATE";
            public const string InvoiceUpdate = "SALES.INVOICE.UPDATE";
            public const string InvoiceDelete = "SALES.INVOICE.DELETE";
            public const string InvoicePost = "SALES.INVOICE.POST";
            public const string InvoiceReverse = "SALES.INVOICE.REVERSE";

            public const string ReturnView = "SALES.RETURN.VIEW";
            public const string ReturnCreate = "SALES.RETURN.CREATE";
            public const string ReturnPost = "SALES.RETURN.POST";

            public const string QuotationView = "SALES.QUOTATION.VIEW";
            public const string QuotationCreate = "SALES.QUOTATION.CREATE";
            public const string QuotationUpdate = "SALES.QUOTATION.UPDATE";
            public const string QuotationDelete = "SALES.QUOTATION.DELETE";
            public const string QuotationConvert = "SALES.QUOTATION.CONVERT";

            public const string OrderView = "SALES.ORDER.VIEW";
            public const string OrderCreate = "SALES.ORDER.CREATE";
            public const string OrderUpdate = "SALES.ORDER.UPDATE";
            public const string OrderCancel = "SALES.ORDER.CANCEL";

            public const string CashInvoiceCreate = "SALES.CASH.CREATE";
            public const string PriceOverride = "SALES.PRICE.OVERRIDE";

            public const string DiscountOverride = "SALES.DISCOUNT.OVERRIDE";

            public const string InvoiceRepost = "SALES.INVOICE.REPOST";

            public const string ReportsView = "SALES.REPORTS.VIEW";
        }

        // =====================================================================
        // PURCHASING
        // =====================================================================

        public static class Purchasing
        {
            public const string InvoiceView = "PURCH.INVOICE.VIEW";
            public const string InvoiceCreate = "PURCH.INVOICE.CREATE";
            public const string InvoiceUpdate = "PURCH.INVOICE.UPDATE";
            public const string InvoiceDelete = "PURCH.INVOICE.DELETE";
            public const string InvoicePost = "PURCH.INVOICE.POST";

            public const string ReturnView = "PURCH.RETURN.VIEW";
            public const string ReturnCreate = "PURCH.RETURN.CREATE";
            public const string ReturnPost = "PURCH.RETURN.POST";

            public const string OrderView = "PURCH.ORDER.VIEW";
            public const string OrderCreate = "PURCH.ORDER.CREATE";
            public const string OrderUpdate = "PURCH.ORDER.UPDATE";
            public const string OrderCancel = "PURCH.ORDER.CANCEL";

            public const string ReportsView = "PURCH.REPORTS.VIEW";
        }

        // =====================================================================
        // POS
        // =====================================================================

        public static class Pos
        {
            public const string TerminalView = "POS.TERMINAL.VIEW";
            public const string TerminalManage = "POS.TERMINAL.MANAGE";

            public const string SessionOpen = "POS.SESSION.OPEN";
            public const string SessionClose = "POS.SESSION.CLOSE";
            public const string SessionReopen = "POS.SESSION.REOPEN";

            public const string InvoiceCreate = "POS.INVOICE.CREATE";
            public const string InvoiceDelete = "POS.INVOICE.DELETE";
            public const string InvoiceRefund = "POS.INVOICE.REFUND";

            public const string PriceOverride = "POS.PRICE.OVERRIDE";
            public const string QuantityOverride = "POS.QUANTITY.OVERRIDE";
            public const string ItemDelete = "POS.ITEM.DELETE";

            public const string DiscountManage = "POS.DISCOUNT.MANAGE";

            public const string SyncExecute = "POS.SYNC.EXECUTE";
            public const string ReportsView = "POS.REPORTS.VIEW";
        }

        // =====================================================================
        // CRM / CUSTOMERS
        // =====================================================================

        public static class Customers
        {
            public const string View = "CRM.CUSTOMER.VIEW";
            public const string Create = "CRM.CUSTOMER.CREATE";
            public const string Update = "CRM.CUSTOMER.UPDATE";
            public const string Delete = "CRM.CUSTOMER.DELETE";

            public const string PricingView = "CRM.CUSTOMER_PRICING.VIEW";
            public const string PricingManage = "CRM.CUSTOMER_PRICING.MANAGE";

            public const string DiscountView = "CRM.CUSTOMER_DISCOUNT.VIEW";
            public const string DiscountManage = "CRM.CUSTOMER_DISCOUNT.MANAGE";

            public const string BalanceView = "CRM.CUSTOMER_BALANCE.VIEW";
            public const string ReceivableView = "CRM.RECEIVABLE.VIEW";
            public const string ReceivableManage = "CRM.RECEIVABLE.MANAGE";

            public const string TargetView = "CRM.CUSTOMER_TARGET.VIEW";
            public const string TargetManage = "CRM.CUSTOMER_TARGET.MANAGE";

            public const string ReportsView = "CRM.CUSTOMER_REPORTS.VIEW";
        }

        // =====================================================================
        // REPRESENTATIVES
        // =====================================================================

        public static class Representatives
        {
            public const string View = "REP.REPRESENTATIVE.VIEW";
            public const string Create = "REP.REPRESENTATIVE.CREATE";
            public const string Update = "REP.REPRESENTATIVE.UPDATE";
            public const string Delete = "REP.REPRESENTATIVE.DELETE";

            public const string TargetView = "REP.TARGET.VIEW";
            public const string TargetManage = "REP.TARGET.MANAGE";

            public const string CollectionView = "REP.COLLECTION.VIEW";
            public const string CollectionManage = "REP.COLLECTION.MANAGE";

            public const string DeviceView = "REP.DEVICE.VIEW";
            public const string DeviceManage = "REP.DEVICE.MANAGE";

            public const string RouteView = "REP.ROUTE.VIEW";
            public const string RouteManage = "REP.ROUTE.MANAGE";
        }

        // =====================================================================
        // MANUFACTURING
        // =====================================================================

        public static class Manufacturing
        {
            public const string BomView = "MFG.BOM.VIEW";
            public const string BomManage = "MFG.BOM.MANAGE";

            public const string OrderView = "MFG.ORDER.VIEW";
            public const string OrderCreate = "MFG.ORDER.CREATE";
            public const string OrderUpdate = "MFG.ORDER.UPDATE";
            public const string OrderCancel = "MFG.ORDER.CANCEL";

            public const string ProductionView = "MFG.PRODUCTION.VIEW";
            public const string ProductionExecute = "MFG.PRODUCTION.EXECUTE";
            public const string ProductionPost = "MFG.PRODUCTION.POST";
            public const string ProductionRepost = "MFG.PRODUCTION.REPOST";

            public const string CostView = "MFG.COST.VIEW";
            public const string VarianceView = "MFG.VARIANCE.VIEW";

            public const string ReportsView = "MFG.REPORTS.VIEW";
        }

        // =====================================================================
        // BANKING
        // =====================================================================

        public static class Banking
        {
            public const string BankView = "BANK.BANK.VIEW";
            public const string BankManage = "BANK.BANK.MANAGE";

            public const string AccountView = "BANK.ACCOUNT.VIEW";
            public const string AccountManage = "BANK.ACCOUNT.MANAGE";

            public const string ReconciliationView =
                "BANK.RECONCILIATION.VIEW";

            public const string ReconciliationExecute =
                "BANK.RECONCILIATION.EXECUTE";

            public const string BalanceView = "BANK.BALANCE.VIEW";
            public const string ForecastView = "BANK.FORECAST.VIEW";
        }

        // =====================================================================
        // DELIVERY
        // =====================================================================

        public static class Delivery
        {
            public const string DriverView = "DELIVERY.DRIVER.VIEW";
            public const string DriverManage = "DELIVERY.DRIVER.MANAGE";

            public const string OrderView = "DELIVERY.ORDER.VIEW";
            public const string OrderCreate = "DELIVERY.ORDER.CREATE";
            public const string OrderUpdate = "DELIVERY.ORDER.UPDATE";
            public const string OrderCancel = "DELIVERY.ORDER.CANCEL";

            public const string PricingView = "DELIVERY.PRICING.VIEW";
            public const string PricingManage = "DELIVERY.PRICING.MANAGE";

            public const string TrackingView = "DELIVERY.TRACKING.VIEW";

            public const string CashboxView = "DELIVERY.CASHBOX.VIEW";
            public const string CashboxClose = "DELIVERY.CASHBOX.CLOSE";
        }

        // =====================================================================
        // REPORTING
        // =====================================================================

        public static class Reporting
        {
            public const string View = "REPORT.VIEW";
            public const string Export = "REPORT.EXPORT";
            public const string Print = "REPORT.PRINT";

            public const string DesignerView = "REPORT.DESIGNER.VIEW";
            public const string DesignerManage = "REPORT.DESIGNER.MANAGE";

            public const string CustomView = "REPORT.CUSTOM.VIEW";
            public const string CustomManage = "REPORT.CUSTOM.MANAGE";

            public const string DashboardView = "REPORT.DASHBOARD.VIEW";
        }

        // =====================================================================
        // DATA
        // =====================================================================

        public static class Data
        {
            public const string Import = "DATA.IMPORT";
            public const string Export = "DATA.EXPORT";

            public const string PosImport = "DATA.POS.IMPORT";
            public const string PosExport = "DATA.POS.EXPORT";

            public const string CardImport = "DATA.CARD.IMPORT";

            public const string Validate = "DATA.VALIDATE";
        }

        // =====================================================================
        // BACKUP
        // =====================================================================

        public static class Backup
        {
            public const string View = "BACKUP.VIEW";
            public const string Create = "BACKUP.CREATE";
            public const string Restore = "BACKUP.RESTORE";
            public const string Verify = "BACKUP.VERIFY";
            public const string Delete = "BACKUP.DELETE";
            public const string Configure = "BACKUP.CONFIGURE";
        }

        // =====================================================================
        // AUDIT
        // =====================================================================

        public static class Audit
        {
            public const string View = "AUDIT.VIEW";
            public const string Export = "AUDIT.EXPORT";
            public const string SecurityEventsView =
                "AUDIT.SECURITY_EVENTS.VIEW";
        }

        // =====================================================================
        // LICENSING
        // =====================================================================

        public static class Licensing
        {
            public const string View = "LIC.LICENSE.VIEW";
            public const string Renew = "LIC.LICENSE.RENEW";
            public const string Activate = "LIC.LICENSE.ACTIVATE";
            public const string DeviceView = "LIC.DEVICE.VIEW";
            public const string DeviceManage = "LIC.DEVICE.MANAGE";
        }

        // =====================================================================
        // UPDATES
        // =====================================================================

        public static class Updates
        {
            public const string Check = "UPDATE.CHECK";
            public const string Install = "UPDATE.INSTALL";
            public const string ManualInstall = "UPDATE.MANUAL_INSTALL";
        }

        // =====================================================================
        // AI
        // =====================================================================

        public static class Ai
        {
            public const string Use = "AI.USE";
            public const string FinancialAnalysis =
                "AI.FINANCIAL_ANALYSIS";
            public const string InventoryAnalysis =
                "AI.INVENTORY_ANALYSIS";
            public const string SalesAnalysis =
                "AI.SALES_ANALYSIS";
            public const string Forecast =
                "AI.FORECAST";
            public const string Assistant =
                "AI.ASSISTANT";
        }

        // =====================================================================
        // SYSTEM HELP
        // =====================================================================

        public static class Help
        {
            public const string View = "HELP.VIEW";
            public const string Support = "HELP.SUPPORT";
            public const string Suggestions = "HELP.SUGGESTIONS";
            public const string About = "HELP.ABOUT";
        }

        // =====================================================================
        // CATALOG
        // =====================================================================

        private static readonly Lazy<ReadOnlyCollection<string>> _all =
            new(BuildCatalog);

        /// <summary>
        /// Returns every registered permission key.
        /// </summary>
        public static IReadOnlyCollection<string> All => _all.Value;

        /// <summary>
        /// Checks whether a permission key is registered.
        /// </summary>
        public static bool Contains(string? permissionKey)
        {
            if (string.IsNullOrWhiteSpace(permissionKey))
                return false;

            return _all.Value.Contains(
                permissionKey.Trim(),
                StringComparer.OrdinalIgnoreCase);
        }

        // =====================================================================
        // INTERNAL CATALOG BUILDER
        // =====================================================================

        private static ReadOnlyCollection<string> BuildCatalog()
        {
            var result = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

            AddNestedConstants(typeof(System), result);
            AddNestedConstants(typeof(Company), result);
            AddNestedConstants(typeof(Inventory), result);
            AddNestedConstants(typeof(Accounting), result);
            AddNestedConstants(typeof(Vouchers), result);
            AddNestedConstants(typeof(Sales), result);
            AddNestedConstants(typeof(Purchasing), result);
            AddNestedConstants(typeof(Pos), result);
            AddNestedConstants(typeof(Customers), result);
            AddNestedConstants(typeof(Representatives), result);
            AddNestedConstants(typeof(Manufacturing), result);
            AddNestedConstants(typeof(Banking), result);
            AddNestedConstants(typeof(Delivery), result);
            AddNestedConstants(typeof(Reporting), result);
            AddNestedConstants(typeof(Data), result);
            AddNestedConstants(typeof(Backup), result);
            AddNestedConstants(typeof(Audit), result);
            AddNestedConstants(typeof(Licensing), result);
            AddNestedConstants(typeof(Updates), result);
            AddNestedConstants(typeof(Ai), result);
            AddNestedConstants(typeof(Help), result);

            return new ReadOnlyCollection<string>(
                result.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                      .ToList());
        }

        private static void AddNestedConstants(
            Type type,
            HashSet<string> target)
        {
            foreach (var field in type.GetFields(
                         global::System.Reflection.BindingFlags.Public |
                         global::System.Reflection.BindingFlags.Static |
                         global::System.Reflection.BindingFlags.FlattenHierarchy))
            {
                if (field.FieldType != typeof(string))
                    continue;

                if (!field.IsLiteral || field.IsInitOnly)
                    continue;

                if (field.GetRawConstantValue() is string value &&
                    !string.IsNullOrWhiteSpace(value))
                {
                    target.Add(value);
                }
            }
        }
    }

    // ========== PermServ.cs ==========
public sealed class PermServ : IPermServ
{
    private readonly HashSet<string> _perms = new(StringComparer.OrdinalIgnoreCase);
    private string? _currentUserId;
    private string _roleCode = string.Empty;

    public bool Has(string permissionKey)
    {
        if (string.IsNullOrWhiteSpace(permissionKey))
            return false;
        return _perms.Contains(permissionKey.Trim());
    }

    public bool HasAll(params string[] permissionKeys)
    {
        if (permissionKeys == null || permissionKeys.Length == 0)
            return true;
        foreach (var key in permissionKeys)
            if (!Has(key))
                return false;
        return true;
    }

    public bool HasAny(params string[] permissionKeys)
    {
        if (permissionKeys == null || permissionKeys.Length == 0)
            return false;
        foreach (var key in permissionKeys)
            if (Has(key))
                return true;
        return false;
    }

    public async Task LoadForUserAsync(string userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("UserId is required.", nameof(userId));

        var connectionString = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Permission loading requires SHOUTECH_CONNECTION_STRING.");

        _perms.Clear();
        _roleCode = string.Empty;
        _currentUserId = userId.Trim();

        var provider = DatabaseRuntime.GetProvider();
        if (DatabaseRuntime.IsSqlite(provider))
        {
            await LoadSqliteAsync(connectionString, ct).ConfigureAwait(false);
            return;
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        const string sql = """
            SELECT r.role_code, p.permission_code, up.effect AS user_effect
            FROM public.users AS u
            LEFT JOIN public.user_roles AS ur ON ur.user_id = u.user_id
            LEFT JOIN public.roles AS r ON r.role_id = ur.role_id
                AND r.is_active = TRUE AND r.is_deleted = FALSE
            CROSS JOIN public.permissions AS p
            LEFT JOIN public.role_permissions AS rp ON rp.role_id = r.role_id
                AND rp.permission_id = p.permission_id
            LEFT JOIN public.user_permissions AS up ON up.user_id = u.user_id
                AND up.permission_id = p.permission_id
            WHERE u.is_active = TRUE AND u.is_deleted = FALSE
              AND p.is_active = TRUE
              AND (u.username = @UserId OR u.user_id =
                  CASE WHEN @UserId ~* '^[0-9a-f-]{36}$' THEN CAST(@UserId AS uuid) END);
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@UserId", _currentUserId);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var found = false;
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            found = true;
            if (!reader.IsDBNull(0))
            {
                var roleCode = reader.GetString(0);
                if (string.Equals(roleCode, "ROLE_ADMINISTRATOR", StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(_roleCode))
                    _roleCode = roleCode;
            }
            if (!reader.IsDBNull(1))
            {
                var permission = reader.GetString(1);
                if (reader.IsDBNull(2) || reader.GetBoolean(2))
                    _perms.Add(permission);
                else
                    _perms.Remove(permission);
            }
        }

        if (!found)
        {
            _currentUserId = null;
            throw new UnauthorizedAccessException("The user is inactive, deleted, or does not exist.");
        }
    }

    public void Clear()
    {
        _perms.Clear();
        _currentUserId = null;
        _roleCode = string.Empty;
    }

    public IReadOnlyCollection<string> GetCurrentPermissions() => _perms;

    private async Task LoadSqliteAsync(string connectionString, CancellationToken ct)
    {
        SqliteSchemaBootstrapper.EnsureDatabase(connectionString);
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = new SqliteCommand("""
            SELECT r.role_code, p.permission_code, up.effect AS user_effect
            FROM users u
            LEFT JOIN user_roles ur ON ur.user_id=u.user_id
            LEFT JOIN roles r ON r.role_id=ur.role_id AND r.is_active=1 AND r.is_deleted=0
            CROSS JOIN permissions p
            LEFT JOIN role_permissions rp ON rp.role_id=r.role_id AND rp.permission_id=p.permission_id
            LEFT JOIN user_permissions up ON up.user_id=u.user_id AND up.permission_id=p.permission_id
            WHERE u.is_active=1 AND u.is_deleted=0 AND p.is_active=1
              AND (u.username=@user_id OR u.user_id=@user_id)
            """, connection);
        command.Parameters.AddWithValue("@user_id", _currentUserId);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var found = false;
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            found = true;
            if (!reader.IsDBNull(0))
            {
                var roleCode = reader.GetString(0);
                if (string.Equals(roleCode, "ROLE_ADMINISTRATOR", StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(_roleCode))
                    _roleCode = roleCode;
            }
            if (!reader.IsDBNull(1))
            {
                var permission = reader.GetString(1);
                if (reader.IsDBNull(2) || reader.GetBoolean(2)) _perms.Add(permission);
                else _perms.Remove(permission);
            }
        }
        if (!found)
        {
            _currentUserId = null;
            throw new UnauthorizedAccessException("The user is inactive, deleted, or does not exist.");
        }
    }

}

    // ========== PermAdminSvc.cs ==========
/// <summary>
/// Enterprise permission administration service.
/// Loads the tabbed permission catalog and persists per-user overrides.
/// </summary>
public sealed class PermAdminSvc
{
    public sealed record PermissionGroup(string Code, string Name, string NameEn, int DisplayOrder);
    public sealed record PermissionItem(long Id, string Code, string Feature, string Operation,
        string Name, string NameEn, bool Sensitive, bool BaselineAllowed, bool EffectiveAllowed);

    public static async Task<IReadOnlyList<PermissionGroup>> LoadGroupsAsync(CancellationToken ct = default)
    {
        var provider = DatabaseRuntime.GetProvider();
        var cs = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(cs)) throw new InvalidOperationException("Permission administration requires a database connection.");
        if (DatabaseRuntime.IsSqlite(provider))
            return await LoadGroupsSqliteAsync(cs, ct).ConfigureAwait(false);
        if (DatabaseRuntime.IsPostgreSql(provider))
            return await LoadGroupsPostgresAsync(cs, ct).ConfigureAwait(false);
        throw new NotSupportedException($"Unsupported database provider: {provider}");
    }

    public static async Task<IReadOnlyList<PermissionItem>> LoadForUserAsync(string userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("UserId is required.", nameof(userId));
        var provider = DatabaseRuntime.GetProvider();
        var cs = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(cs)) throw new InvalidOperationException("Permission administration requires a database connection.");
        if (DatabaseRuntime.IsSqlite(provider))
            return await LoadSqliteAsync(cs, userId.Trim(), ct).ConfigureAwait(false);
        if (DatabaseRuntime.IsPostgreSql(provider))
            return await LoadPostgresAsync(cs, userId.Trim(), ct).ConfigureAwait(false);
        throw new NotSupportedException($"Unsupported database provider: {provider}");
    }

    public static async Task SaveForUserAsync(string userId, IReadOnlyDictionary<long, bool> effectivePermissions, CancellationToken ct = default)
    {
        if (!UsrSession.IsAdministrator || !AppHost.Permissions.Has(PermCat.System.PermissionsManage))
            throw new UnauthorizedAccessException("Only an Administrator with permission-management rights can change user permissions.");
        if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("UserId is required.", nameof(userId));
        var provider = DatabaseRuntime.GetProvider();
        var cs = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(cs)) throw new InvalidOperationException("Permission administration requires a database connection.");
        if (DatabaseRuntime.IsSqlite(provider))
            await SaveSqliteAsync(cs, userId.Trim(), effectivePermissions, ct).ConfigureAwait(false);
        else if (DatabaseRuntime.IsPostgreSql(provider))
            await SavePostgresAsync(cs, userId.Trim(), effectivePermissions, ct).ConfigureAwait(false);
        else
            throw new NotSupportedException($"Unsupported database provider: {provider}");
    }

    private static async Task<IReadOnlyList<PermissionGroup>> LoadGroupsSqliteAsync(string cs, CancellationToken ct)
    {
        await using var c = new SqliteConnection(cs); await c.OpenAsync(ct);
        await using var cmd = new SqliteCommand("SELECT group_code,name_ar,name_en,display_order FROM permission_groups WHERE is_active=1 ORDER BY display_order,group_code", c);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var result = new List<PermissionGroup>();
        while (await r.ReadAsync(ct)) result.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3)));
        return result;
    }

    private static async Task<IReadOnlyList<PermissionGroup>> LoadGroupsPostgresAsync(string cs, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(cs); await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT group_code,name_ar,name_en,display_order FROM permission_groups WHERE is_active=TRUE ORDER BY display_order,group_code", c);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var result = new List<PermissionGroup>();
        while (await r.ReadAsync(ct)) result.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3)));
        return result;
    }

    private static async Task<IReadOnlyList<PermissionItem>> LoadSqliteAsync(string cs, string userId, CancellationToken ct)
    {
        await using var c = new SqliteConnection(cs); await c.OpenAsync(ct);
        const string sql = @"
SELECT p.permission_id,p.permission_code,p.feature_code,p.operation_code,p.name_ar,p.name_en,p.is_sensitive,
       CASE WHEN rp.permission_id IS NULL THEN 0 ELSE 1 END AS baseline_allowed,
       CASE WHEN up.effect IS NULL THEN CASE WHEN rp.permission_id IS NULL THEN 0 ELSE 1 END ELSE up.effect END AS effective_allowed
FROM permissions p
LEFT JOIN user_roles ur ON ur.user_id=@uid
LEFT JOIN roles r ON r.role_id=ur.role_id AND r.is_active=1 AND r.is_deleted=0
LEFT JOIN role_permissions rp ON rp.role_id=r.role_id AND rp.permission_id=p.permission_id
LEFT JOIN user_permissions up ON up.user_id=@uid AND up.permission_id=p.permission_id
WHERE p.is_active=1
ORDER BY p.display_order,p.permission_code;
";
        await using var cmd = new SqliteCommand(sql, c); cmd.Parameters.AddWithValue("@uid", userId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var result = new List<PermissionItem>();
        while (await r.ReadAsync(ct)) result.Add(new(r.GetInt64(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetInt64(6)!=0,r.GetInt64(7)!=0,r.GetInt64(8)!=0));
        return result;
    }

    private static async Task<IReadOnlyList<PermissionItem>> LoadPostgresAsync(string cs, string userId, CancellationToken ct)
    {
        if (!Guid.TryParse(userId, out var uid)) throw new ArgumentException("PostgreSQL UserId must be a GUID.", nameof(userId));
        await using var c = new NpgsqlConnection(cs); await c.OpenAsync(ct);
        const string sql = @"
SELECT p.permission_id,p.permission_code,p.feature_code,p.operation_code,p.name_ar,p.name_en,p.is_sensitive,
       CASE WHEN rp.permission_id IS NULL THEN FALSE ELSE TRUE END AS baseline_allowed,
       CASE WHEN up.effect IS NULL THEN CASE WHEN rp.permission_id IS NULL THEN FALSE ELSE TRUE END ELSE up.effect END AS effective_allowed
FROM permissions p
LEFT JOIN user_roles ur ON ur.user_id=@uid
LEFT JOIN roles r ON r.role_id=ur.role_id AND r.is_active=TRUE AND r.is_deleted=FALSE
LEFT JOIN role_permissions rp ON rp.role_id=r.role_id AND rp.permission_id=p.permission_id
LEFT JOIN user_permissions up ON up.user_id=@uid AND up.permission_id=p.permission_id
WHERE p.is_active=TRUE
ORDER BY p.display_order,p.permission_code;
";
        await using var cmd = new NpgsqlCommand(sql, c); cmd.Parameters.Add("@uid", NpgsqlDbType.Uuid).Value = uid;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var result = new List<PermissionItem>();
        while (await r.ReadAsync(ct)) result.Add(new(r.GetInt64(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetBoolean(6),r.GetBoolean(7),r.GetBoolean(8)));
        return result;
    }

    private static async Task SaveSqliteAsync(string cs, string userId, IReadOnlyDictionary<long, bool> effective, CancellationToken ct)
    {
        await using var c = new SqliteConnection(cs); await c.OpenAsync(ct); await using var tx = c.BeginTransaction();
        await EnsureNotAdministratorSqlite(c, tx, userId, ct);
        const string baselineSql = "SELECT COUNT(*) FROM role_permissions rp INNER JOIN user_roles ur ON ur.role_id=rp.role_id WHERE ur.user_id=@uid AND rp.permission_id=@pid";
        foreach (var pair in effective)
        {
            await using var b = new SqliteCommand(baselineSql,c,(SqliteTransaction)tx); b.Parameters.AddWithValue("@uid",userId); b.Parameters.AddWithValue("@pid",pair.Key);
            var baseline = Convert.ToInt64(await b.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture) > 0;
            if (baseline == pair.Value)
            {
                await using var d = new SqliteCommand("DELETE FROM user_permissions WHERE user_id=@uid AND permission_id=@pid",c,(SqliteTransaction)tx); d.Parameters.AddWithValue("@uid",userId); d.Parameters.AddWithValue("@pid",pair.Key); await d.ExecuteNonQueryAsync(ct);
            }
            else
            {
                await using var u = new SqliteCommand("INSERT INTO user_permissions(user_id,permission_id,effect,updated_at) VALUES(@uid,@pid,@effect,CURRENT_TIMESTAMP) ON CONFLICT(user_id,permission_id) DO UPDATE SET effect=excluded.effect,updated_at=CURRENT_TIMESTAMP",c,(SqliteTransaction)tx);
                u.Parameters.AddWithValue("@uid",userId); u.Parameters.AddWithValue("@pid",pair.Key); u.Parameters.AddWithValue("@effect",pair.Value?1:0); await u.ExecuteNonQueryAsync(ct);
            }
        }
        await tx.CommitAsync(ct);
    }

    private static async Task SavePostgresAsync(string cs, string userId, IReadOnlyDictionary<long, bool> effective, CancellationToken ct)
    {
        if (!Guid.TryParse(userId, out var uid)) throw new ArgumentException("PostgreSQL UserId must be a GUID.", nameof(userId));
        await using var c = new NpgsqlConnection(cs); await c.OpenAsync(ct); await using var tx = c.BeginTransaction();
        await EnsureNotAdministratorPostgres(c, tx, uid, ct);
        const string baselineSql = "SELECT COUNT(*) FROM role_permissions rp INNER JOIN user_roles ur ON ur.role_id=rp.role_id WHERE ur.user_id=@uid AND rp.permission_id=@pid";
        foreach (var pair in effective)
        {
            await using var b = new NpgsqlCommand(baselineSql,c,tx); b.Parameters.Add("@uid",NpgsqlDbType.Uuid).Value=uid; b.Parameters.Add("@pid",NpgsqlDbType.Bigint).Value=pair.Key;
            var baseline = Convert.ToInt64(await b.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture) > 0;
            if (baseline == pair.Value)
            {
                await using var d = new NpgsqlCommand("DELETE FROM user_permissions WHERE user_id=@uid AND permission_id=@pid",c,tx); d.Parameters.Add("@uid",NpgsqlDbType.Uuid).Value=uid; d.Parameters.Add("@pid",NpgsqlDbType.Bigint).Value=pair.Key; await d.ExecuteNonQueryAsync(ct);
            }
            else
            {
                await using var u = new NpgsqlCommand("INSERT INTO user_permissions(user_id,permission_id,effect,updated_at) VALUES(@uid,@pid,@effect,now()) ON CONFLICT(user_id,permission_id) DO UPDATE SET effect=EXCLUDED.effect,updated_at=now()",c,tx);
                u.Parameters.Add("@uid",NpgsqlDbType.Uuid).Value=uid; u.Parameters.Add("@pid",NpgsqlDbType.Bigint).Value=pair.Key; u.Parameters.Add("@effect",NpgsqlDbType.Boolean).Value=pair.Value; await u.ExecuteNonQueryAsync(ct);
            }
        }
        await tx.CommitAsync(ct);
    }

    private static async Task EnsureNotAdministratorSqlite(SqliteConnection c, SqliteTransaction tx, string userId, CancellationToken ct)
    {
        await using var cmd = new SqliteCommand("SELECT COUNT(*) FROM user_roles ur INNER JOIN roles r ON r.role_id=ur.role_id WHERE ur.user_id=@uid AND r.role_code='ROLE_ADMINISTRATOR'",c,tx); cmd.Parameters.AddWithValue("@uid",userId);
        if (Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture) > 0) throw new UnauthorizedAccessException("Administrator permissions are protected and cannot be edited from the user permission editor.");
    }
    private static async Task EnsureNotAdministratorPostgres(NpgsqlConnection c, NpgsqlTransaction tx, Guid userId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM user_roles ur INNER JOIN roles r ON r.role_id=ur.role_id WHERE ur.user_id=@uid AND r.role_code='ROLE_ADMINISTRATOR'",c,tx); cmd.Parameters.Add("@uid",NpgsqlDbType.Uuid).Value=userId;
        if (Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture) > 0) throw new UnauthorizedAccessException("Administrator permissions are protected and cannot be edited from the user permission editor.");
    }
}

}
