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
using System.Globalization;
using Microsoft.Data.Sqlite;
using Npgsql;
using NpgsqlTypes;
using ShouTech.Domain;
using ShouTech.Infrastructure;

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
        /// Audited permission check for ACTION seams (W8, migration
        /// 103): like <see cref="Has"/>, but a denial is recorded as an
        /// AccessDenied row in sau_document_events (with the document
        /// context when the caller supplies one) before returning false.
        /// Plain <see cref="Has"/> stays the probe for UI state — it is
        /// called speculatively all over the shell and must never write.
        /// Action handlers that gate a real write call Require instead,
        /// which makes this the single central denial-logging hook.
        /// </summary>
        bool Require(string permissionKey, string docSide = "SALES", string? docKind = null, long docId = 0, string? docNo = null);

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
        /// The identifier may be the user's database id or username.
        /// Effective permissions are resolved through the user's roles
        /// (user_roles → role_permissions), then per-user overrides in
        /// user_permissions are applied (an explicit deny wins over a
        /// role grant; an explicit allow adds to it). A user holding
        /// ROLE_ADMINISTRATOR receives every active permission except
        /// explicit per-user denies.
        /// </summary>
        Task LoadForUserAsync(string userId, CancellationToken ct = default);

        /// <summary>
        /// Grants the full in-code permission catalog (PermCat.All) for
        /// the given user without touching the database. Fallback for a
        /// session that is already authenticated as an administrator
        /// whose database permission load failed (e.g. a local-store
        /// company administrator with no mirrored database user row):
        /// an administrator must never be locked out of the guarded
        /// windows by a loader failure. Never used for non-administrators.
        /// </summary>
        void GrantAllCatalogPermissions(string userId);

        /// <summary>
        /// Clears loaded permissions (called on logout).
        /// </summary>
        void Clear();

        /// <summary>
        /// True when the loaded user carries an explicit per-user DENY
        /// for the key (user_permissions effect = deny). Denies are
        /// tracked separately from the granted set because the DOC
        /// document matrix needs them: a deny of one DOC key subtracts
        /// that single document type from a broader generic grant.
        /// </summary>
        bool IsExplicitlyDenied(string permissionKey);

        /// <summary>
        /// Per-document-type probe (migration 125): allowed iff the
        /// DOC key is NOT explicitly denied AND the user holds the DOC
        /// key OR the pre-existing generic family key. UI-state probe —
        /// never writes an audit row.
        /// </summary>
        bool HasDoc(string docKey, string genericKey);

        /// <summary>
        /// Audited per-document-type action check: the
        /// <see cref="HasDoc"/> rule, with a denial recorded through
        /// the same AccessDenied seam as <see cref="Require"/> (the
        /// detail names both the DOC key and the generic fallback).
        /// </summary>
        bool RequireDoc(string docKey, string genericKey, string docSide = "SALES", string? docKind = null, long docId = 0, string? docNo = null);

        /// <summary>
        /// The Permission Inspector (PERMISSIONS_STUDY section 2.6):
        /// explains WHY a user does or does not hold a permission, by
        /// reading the same tables the loader reads — administrator
        /// role, which role's grant, which assigned template's grant,
        /// a per-user allow, an explicit per-user deny, or granted
        /// nowhere. The verdict therefore cannot disagree with
        /// enforcement. Works for any user id/username, not only the
        /// loaded session user.
        /// </summary>
        Task<PermExplanation> ExplainAsync(string userId, string permissionKey, CancellationToken ct = default);

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
            public const string CustomFieldsManage = "SYS.CUSTOM_FIELDS.MANAGE";
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

            // Phase B 054 batch/serial (migration 054 seeds).
            public const string BatchView = "INV.BATCH.VIEW";
            public const string BatchManage = "INV.BATCH.MANAGE";
            public const string SerialTraceView = "INV.SERIAL.VIEW";
            public const string BatchExpiredOverride = "INV.BATCH.EXPIREDOVERRIDE";

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

            public const string PaymentReconciliationView = "ACC.PAYREC.VIEW";
            public const string PaymentReconciliationAllocate = "ACC.PAYREC.ALLOCATE";

            // Phase B 055 budgets (migration 055 seeds).
            public const string BudgetView = "ACC.BUDGET.VIEW";
            public const string BudgetEdit = "ACC.BUDGET.EDIT";
            public const string BudgetActivate = "ACC.BUDGET.ACTIVATE";

            public const string TaxView = "ACC.TAX.VIEW";
            public const string TaxManage = "ACC.TAX.MANAGE";
            public const string TaxReportView = "ACC.TAX.REPORT.VIEW";
        }

        // =====================================================================
        // QUALITY (Phase B 056, migration 056 seeds)
        // =====================================================================

        public static class Quality
        {
            public const string ParamView = "QUALITY.PARAM.VIEW";
            public const string ParamEdit = "QUALITY.PARAM.EDIT";
            public const string TemplateView = "QUALITY.TEMPLATE.VIEW";
            public const string TemplateEdit = "QUALITY.TEMPLATE.EDIT";
            public const string InspectView = "QUALITY.INSPECT.VIEW";
            public const string InspectEdit = "QUALITY.INSPECT.EDIT";
            public const string InspectComplete = "QUALITY.INSPECT.COMPLETE";
            public const string AlertView = "QUALITY.ALERT.VIEW";
            public const string AlertEdit = "QUALITY.ALERT.EDIT";
        }

        // =====================================================================
        // CRM (Phase B 057, migration 057 seeds). NOTE: the older
        // PermCat.Customers class below also uses the CRM. prefix for
        // CUSTOMER-master permissions (CRM.CUSTOMER.*) — those are the
        // core customer-card permissions and stay untouched; the
        // pipeline permissions here are CRM.LEAD/OPP/ACTIVITY/STAGE.*.
        // No entitlement gate: licensing is postponed by the owner.
        // =====================================================================

        // =====================================================================
        // HR / PAYROLL (Build HR, migration 065 seeds). Leave approval
        // (LeaveApprove) and the negative-balance override
        // (LeaveOverride, sensitive) are separate from leave editing;
        // payroll posting (PayrollPost, sensitive) is separate from
        // payroll editing/generation — the step-1 separation pattern.
        // =====================================================================

        public static class Hr
        {
            public const string EmployeeView = "HR.EMPLOYEE.VIEW";
            public const string EmployeeEdit = "HR.EMPLOYEE.EDIT";
            public const string OrgView = "HR.ORG.VIEW";
            public const string OrgEdit = "HR.ORG.EDIT";
            public const string AttendanceView = "HR.ATTENDANCE.VIEW";
            public const string AttendanceEdit = "HR.ATTENDANCE.EDIT";
            public const string LeaveView = "HR.LEAVE.VIEW";
            public const string LeaveEdit = "HR.LEAVE.EDIT";
            public const string LeaveApprove = "HR.LEAVE.APPROVE";
            public const string LeaveOverride = "HR.LEAVE.OVERRIDE";
            public const string PayrollView = "HR.PAYROLL.VIEW";
            public const string PayrollEdit = "HR.PAYROLL.EDIT";
            public const string PayrollPost = "HR.PAYROLL.POST";
            public const string ReportView = "HR.REPORT.VIEW";
        }

        public static class Crm
        {
            public const string LeadView = "CRM.LEAD.VIEW";
            public const string LeadEdit = "CRM.LEAD.EDIT";
            public const string LeadConvert = "CRM.LEAD.CONVERT";
            public const string OppView = "CRM.OPP.VIEW";
            public const string OppEdit = "CRM.OPP.EDIT";
            public const string ActivityView = "CRM.ACTIVITY.VIEW";
            public const string ActivityEdit = "CRM.ACTIVITY.EDIT";
            public const string StageEdit = "CRM.STAGE.EDIT";
            // W10 (migration 105): campaigns master + outcome roll-up.
            public const string CampaignView = "CRM.CAMPAIGN.VIEW";
            public const string CampaignEdit = "CRM.CAMPAIGN.EDIT";
        }

        // =====================================================================
        // PROJECTS
        // =====================================================================

        public static class Projects
        {
            public const string View = "PRJ.PROJECT.VIEW";
            public const string Create = "PRJ.PROJECT.CREATE";
            public const string Update = "PRJ.PROJECT.UPDATE";
            public const string Delete = "PRJ.PROJECT.DELETE";
            public const string TaskManage = "PRJ.TASK.MANAGE";
        }

        // =====================================================================
        // FIXED ASSETS
        // =====================================================================

        public static class Assets
        {
            public const string View = "AST.ASSET.VIEW";
            public const string Create = "AST.ASSET.CREATE";
            public const string Update = "AST.ASSET.UPDATE";
            public const string Delete = "AST.ASSET.DELETE";
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
            /// <summary>W6 (migration 100): approves/rejects a sales
            /// return before posting and authorizes no-invoice returns.</summary>
            public const string ReturnApprove = "SALES.RETURN.APPROVE";

            public const string QuotationView = "SALES.QUOTATION.VIEW";
            public const string QuotationCreate = "SALES.QUOTATION.CREATE";
            public const string QuotationUpdate = "SALES.QUOTATION.UPDATE";
            public const string QuotationDelete = "SALES.QUOTATION.DELETE";
            public const string QuotationConvert = "SALES.QUOTATION.CONVERT";

            public const string OrderView = "SALES.ORDER.VIEW";
            public const string OrderCreate = "SALES.ORDER.CREATE";
            public const string OrderUpdate = "SALES.ORDER.UPDATE";
            public const string OrderCancel = "SALES.ORDER.CANCEL";
            public const string OrderConvert = "SALES.ORDER.CONVERT";

            public const string CashInvoiceCreate = "SALES.CASH.CREATE";
            public const string PriceOverride = "SALES.PRICE.OVERRIDE";

            // W1 (migration 095): posting past a party credit limit when
            // the posting user holds this key; every use is audited in
            // credit_overrides by PartyCreditGuard.
            public const string CreditOverride = "SALES.CREDIT.OVERRIDE";

            public const string DiscountOverride = "SALES.DISCOUNT.OVERRIDE";

            public const string InvoiceRepost = "SALES.INVOICE.REPOST";

            public const string ReportsView = "SALES.REPORTS.VIEW";

            // W23 document lifecycle (migration 098 seeds): approval is a
            // separated duty per document kind; the direct QUO -> SAL
            // shortcut and the generalized sales attachments have their own
            // sensitive keys.
            public const string QuotationApprove = "SALES.QUOTATION.APPROVE";
            public const string OrderApprove = "SALES.ORDER.APPROVE";
            public const string InvoiceApprove = "SALES.INVOICE.APPROVE";
            public const string QuotationDirectInvoice = "SALES.QUOTATION.DIRECTINVOICE";
            public const string AttachView = "SALES.ATTACH.VIEW";
            public const string AttachUpload = "SALES.ATTACH.UPLOAD";
            public const string AttachDelete = "SALES.ATTACH.DELETE";

            /// <summary>W8 (migration 103): grants a settlement
            /// (early-payment) discount while allocating a receipt or
            /// payment in PayRecWin. Sensitive, administrator-seeded.</summary>
            public const string SettlementDiscount = "SALES.SETTLEMENT.DISCOUNT";
            // W10 (migration 105): warranty registration + claims.
            // RMA deliberately has NO new key: its decisions reuse
            // ReturnApprove above, creation ReturnCreate.
            public const string WarrantyView = "SALES.WARRANTY.VIEW";
            public const string WarrantyManage = "SALES.WARRANTY.MANAGE";

            // Phase 09 commissions (migration 097 seeds).
            public const string CommissionView = "SALES.COMMISSION.VIEW";
            public const string CommissionManage = "SALES.COMMISSION.MANAGE";
            public const string CommissionApprove = "SALES.COMMISSION.APPROVE";
            public const string CommissionPay = "SALES.COMMISSION.PAY";

            // Section 10 contracts (migration 101 seeds).
            public const string ContractView = "SALES.CONTRACT.VIEW";
            public const string ContractManage = "SALES.CONTRACT.MANAGE";
            // W11 recurring schedules (migration 106 seeds).
            public const string RecurringView = "SALES.RECURRING.VIEW";
            public const string RecurringManage = "SALES.RECURRING.MANAGE";
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
            public const string OrderConvert = "PURCH.ORDER.CONVERT";

            public const string ReportsView = "PURCH.REPORTS.VIEW";

            public const string MatchPolicyManage = "PURCH.MATCH.POLICY.MANAGE";
            public const string MatchOverride = "PURCH.MATCH.OVERRIDE";
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

            // Build POS 066 (migration 066 seeds): applying a discount
            // at the till is a sensitive override, separate from
            // managing discount definitions; holding bills and the
            // session-less bypass are their own rights.
            public const string DiscountOverride = "POS.DISCOUNT.OVERRIDE";
            public const string HoldManage = "POS.HOLD.MANAGE";
            public const string SessionBypass = "POS.SESSION.BYPASS";

            // Sales cycle W9 (migration 104 seed): recording cash
            // paid out of the drawer during an open session.
            public const string ExpenseManage = "POS.EXPENSE.MANAGE";

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
            public const string PlanningView = "MFG.PLANNING.VIEW";
            public const string PlanningRun = "MFG.PLANNING.RUN";
            public const string PlanningConvert = "MFG.PLANNING.CONVERT";

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
        // DOCUMENTS (migration 125): the per-document-type matrix
        // DOC.<KIND>.<OP> — kinds are the InvRepo document kinds plus
        // the two order kinds; operations are the evidenced set
        // (view, create, update, delete, print, post = generate the
        // journal, fields = see sensitive fields, account = set the
        // document's account). Enforcement semantics live in PermServ
        // (HasDoc / RequireDoc): a DOC key ADDS a per-type right on
        // top of the generic family key, and an explicit per-user
        // DENY of a DOC key subtracts that one type again.
        // =====================================================================

        public static class Documents
        {
            /// <summary>All document kinds of the DOC family, in matrix order.</summary>
            public static readonly string[] Kinds =
            {
                "SAL", "SRT", "POS", "PUR", "PRT", "ENT", "EXT", "TRN", "OPN", "CNT", "QUO", "SO", "PO"
            };

            /// <summary>All operations of the DOC family, in matrix order.</summary>
            public static readonly string[] Operations =
            {
                "VIEW", "CREATE", "UPDATE", "DELETE", "PRINT", "POST", "FIELDS", "ACCOUNT"
            };

            /// <summary>Builds a DOC permission key from its parts.</summary>
            public static string Key(string kind, string operation)
                => $"DOC.{kind}.{operation}";

            public const string SalView = "DOC.SAL.VIEW";
            public const string SalCreate = "DOC.SAL.CREATE";
            public const string SalUpdate = "DOC.SAL.UPDATE";
            public const string SalDelete = "DOC.SAL.DELETE";
            public const string SalPrint = "DOC.SAL.PRINT";
            public const string SalPost = "DOC.SAL.POST";
            public const string SalFields = "DOC.SAL.FIELDS";
            public const string SalAccount = "DOC.SAL.ACCOUNT";

            public const string SrtView = "DOC.SRT.VIEW";
            public const string SrtCreate = "DOC.SRT.CREATE";
            public const string SrtUpdate = "DOC.SRT.UPDATE";
            public const string SrtDelete = "DOC.SRT.DELETE";
            public const string SrtPrint = "DOC.SRT.PRINT";
            public const string SrtPost = "DOC.SRT.POST";
            public const string SrtFields = "DOC.SRT.FIELDS";
            public const string SrtAccount = "DOC.SRT.ACCOUNT";

            public const string PosView = "DOC.POS.VIEW";
            public const string PosCreate = "DOC.POS.CREATE";
            public const string PosUpdate = "DOC.POS.UPDATE";
            public const string PosDelete = "DOC.POS.DELETE";
            public const string PosPrint = "DOC.POS.PRINT";
            public const string PosPost = "DOC.POS.POST";
            public const string PosFields = "DOC.POS.FIELDS";
            public const string PosAccount = "DOC.POS.ACCOUNT";

            public const string PurView = "DOC.PUR.VIEW";
            public const string PurCreate = "DOC.PUR.CREATE";
            public const string PurUpdate = "DOC.PUR.UPDATE";
            public const string PurDelete = "DOC.PUR.DELETE";
            public const string PurPrint = "DOC.PUR.PRINT";
            public const string PurPost = "DOC.PUR.POST";
            public const string PurFields = "DOC.PUR.FIELDS";
            public const string PurAccount = "DOC.PUR.ACCOUNT";

            public const string PrtView = "DOC.PRT.VIEW";
            public const string PrtCreate = "DOC.PRT.CREATE";
            public const string PrtUpdate = "DOC.PRT.UPDATE";
            public const string PrtDelete = "DOC.PRT.DELETE";
            public const string PrtPrint = "DOC.PRT.PRINT";
            public const string PrtPost = "DOC.PRT.POST";
            public const string PrtFields = "DOC.PRT.FIELDS";
            public const string PrtAccount = "DOC.PRT.ACCOUNT";

            public const string EntView = "DOC.ENT.VIEW";
            public const string EntCreate = "DOC.ENT.CREATE";
            public const string EntUpdate = "DOC.ENT.UPDATE";
            public const string EntDelete = "DOC.ENT.DELETE";
            public const string EntPrint = "DOC.ENT.PRINT";
            public const string EntPost = "DOC.ENT.POST";
            public const string EntFields = "DOC.ENT.FIELDS";
            public const string EntAccount = "DOC.ENT.ACCOUNT";

            public const string ExtView = "DOC.EXT.VIEW";
            public const string ExtCreate = "DOC.EXT.CREATE";
            public const string ExtUpdate = "DOC.EXT.UPDATE";
            public const string ExtDelete = "DOC.EXT.DELETE";
            public const string ExtPrint = "DOC.EXT.PRINT";
            public const string ExtPost = "DOC.EXT.POST";
            public const string ExtFields = "DOC.EXT.FIELDS";
            public const string ExtAccount = "DOC.EXT.ACCOUNT";

            public const string TrnView = "DOC.TRN.VIEW";
            public const string TrnCreate = "DOC.TRN.CREATE";
            public const string TrnUpdate = "DOC.TRN.UPDATE";
            public const string TrnDelete = "DOC.TRN.DELETE";
            public const string TrnPrint = "DOC.TRN.PRINT";
            public const string TrnPost = "DOC.TRN.POST";
            public const string TrnFields = "DOC.TRN.FIELDS";
            public const string TrnAccount = "DOC.TRN.ACCOUNT";

            public const string OpnView = "DOC.OPN.VIEW";
            public const string OpnCreate = "DOC.OPN.CREATE";
            public const string OpnUpdate = "DOC.OPN.UPDATE";
            public const string OpnDelete = "DOC.OPN.DELETE";
            public const string OpnPrint = "DOC.OPN.PRINT";
            public const string OpnPost = "DOC.OPN.POST";
            public const string OpnFields = "DOC.OPN.FIELDS";
            public const string OpnAccount = "DOC.OPN.ACCOUNT";

            public const string CntView = "DOC.CNT.VIEW";
            public const string CntCreate = "DOC.CNT.CREATE";
            public const string CntUpdate = "DOC.CNT.UPDATE";
            public const string CntDelete = "DOC.CNT.DELETE";
            public const string CntPrint = "DOC.CNT.PRINT";
            public const string CntPost = "DOC.CNT.POST";
            public const string CntFields = "DOC.CNT.FIELDS";
            public const string CntAccount = "DOC.CNT.ACCOUNT";

            public const string QuoView = "DOC.QUO.VIEW";
            public const string QuoCreate = "DOC.QUO.CREATE";
            public const string QuoUpdate = "DOC.QUO.UPDATE";
            public const string QuoDelete = "DOC.QUO.DELETE";
            public const string QuoPrint = "DOC.QUO.PRINT";
            public const string QuoPost = "DOC.QUO.POST";
            public const string QuoFields = "DOC.QUO.FIELDS";
            public const string QuoAccount = "DOC.QUO.ACCOUNT";

            public const string SoView = "DOC.SO.VIEW";
            public const string SoCreate = "DOC.SO.CREATE";
            public const string SoUpdate = "DOC.SO.UPDATE";
            public const string SoDelete = "DOC.SO.DELETE";
            public const string SoPrint = "DOC.SO.PRINT";
            public const string SoPost = "DOC.SO.POST";
            public const string SoFields = "DOC.SO.FIELDS";
            public const string SoAccount = "DOC.SO.ACCOUNT";

            public const string PoView = "DOC.PO.VIEW";
            public const string PoCreate = "DOC.PO.CREATE";
            public const string PoUpdate = "DOC.PO.UPDATE";
            public const string PoDelete = "DOC.PO.DELETE";
            public const string PoPrint = "DOC.PO.PRINT";
            public const string PoPost = "DOC.PO.POST";
            public const string PoFields = "DOC.PO.FIELDS";
            public const string PoAccount = "DOC.PO.ACCOUNT";
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
            AddNestedConstants(typeof(Quality), result);
            AddNestedConstants(typeof(Hr), result);
            AddNestedConstants(typeof(Crm), result);
            AddNestedConstants(typeof(Projects), result);
            AddNestedConstants(typeof(Assets), result);
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
            AddNestedConstants(typeof(Documents), result);

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
/// <summary>The single deciding source of an Inspector verdict
/// (PERMISSIONS_STUDY section 2.6). Resolution is one flat union, so
/// every verdict names exactly one source — never a chain.</summary>
public enum PermSourceKind
{
    /// <summary>Held because the user holds ROLE_ADMINISTRATOR.</summary>
    AdministratorRole,
    /// <summary>Granted by a role (SourceName = the role code).</summary>
    Role,
    /// <summary>Granted by an assigned permission template
    /// (SourceName = the template code).</summary>
    Template,
    /// <summary>Granted by a per-user allow override.</summary>
    UserAllow,
    /// <summary>Blocked by an explicit per-user deny (wins over
    /// every grant, administrators included).</summary>
    UserDeny,
    /// <summary>Not granted anywhere (or the key is not an active
    /// catalog permission).</summary>
    None
}

/// <summary>One Inspector verdict: the effective outcome plus the
/// deciding source that produced it.</summary>
public sealed record PermExplanation(bool Allowed, PermSourceKind Source, string? SourceName, string PermissionKey);

public sealed class PermServ : IPermServ
{
    private readonly HashSet<string> _perms = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _denies = new(StringComparer.OrdinalIgnoreCase);
    private string? _currentUserId;
    private string _roleCode = string.Empty;

    public bool Has(string permissionKey)
    {
        if (string.IsNullOrWhiteSpace(permissionKey))
            return false;
        return _perms.Contains(permissionKey.Trim());
    }

    /// <summary>
    /// Audited action-seam check (W8): a denial writes an AccessDenied
    /// row to sau_document_events (migration 103) naming the permission,
    /// the actor and the document context, then returns false. The
    /// denial stands even when the audit write itself fails — that
    /// failure is reported through the central error mechanism
    /// (DB-1005), never swallowed.
    /// </summary>
    public bool Require(string permissionKey, string docSide = "SALES", string? docKind = null, long docId = 0, string? docNo = null)
    {
        if (Has(permissionKey))
            return true;
        LogDenial(permissionKey, docSide, docKind, docId, docNo);
        return false;
    }

    public bool IsExplicitlyDenied(string permissionKey)
    {
        if (string.IsNullOrWhiteSpace(permissionKey))
            return false;
        return _denies.Contains(permissionKey.Trim());
    }

    public bool HasDoc(string docKey, string genericKey)
    {
        if (string.IsNullOrWhiteSpace(docKey) || string.IsNullOrWhiteSpace(genericKey))
            return false;
        return !IsExplicitlyDenied(docKey) && (Has(docKey) || Has(genericKey));
    }

    public bool RequireDoc(string docKey, string genericKey, string docSide = "SALES", string? docKind = null, long docId = 0, string? docNo = null)
    {
        if (HasDoc(docKey, genericKey))
            return true;
        LogDenial(docKey, docSide, docKind, docId, docNo, $"generic={genericKey}");
        return false;
    }

    /// <summary>The single denial-logging hook shared by
    /// <see cref="Require"/> and <see cref="RequireDoc"/>: an
    /// AccessDenied row in sau_document_events naming the permission,
    /// the actor and the document context. A failure of the audit
    /// write itself is reported through the central error mechanism
    /// (DB-1005), never swallowed — and never changes the denial.</summary>
    private void LogDenial(string permissionKey, string docSide, string? docKind, long docId, string? docNo, string? extraDetail = null)
    {
        try
        {
            var connectionString = DatabaseRuntime.GetConnectionString();
            if (!string.IsNullOrWhiteSpace(connectionString))
            {
                var detail = $"permission={permissionKey.Trim()}";
                if (!string.IsNullOrWhiteSpace(extraDetail))
                    detail += $"; {extraDetail}";
                DocEvents.RecordStandalone(
                    DatabaseRuntime.GetProvider(), connectionString,
                    AppState.CurrentCompanyCode ?? string.Empty,
                    docSide, docKind ?? string.Empty, docId, docNo,
                    DocEventTypes.AccessDenied, _currentUserId ?? string.Empty,
                    detail);
            }
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.DatabaseQueryFailed, ex, "PermServ.Require.AccessDeniedLog");
        }
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
        _denies.Clear();
        _roleCode = string.Empty;
        _currentUserId = userId.Trim();

        var provider = DatabaseRuntime.GetProvider();
        if (DatabaseRuntime.IsSqlite(provider))
        {
            await LoadSqliteAsync(connectionString, ct).ConfigureAwait(false);
            return;
        }

        await LoadPostgresAsync(connectionString, ct).ConfigureAwait(false);
    }

    public void GrantAllCatalogPermissions(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("UserId is required.", nameof(userId));

        _perms.Clear();
        _denies.Clear();
        foreach (var code in PermCat.All)
            _perms.Add(code);
        _currentUserId = userId.Trim();
        _roleCode = "ROLE_ADMINISTRATOR";
    }

    public void Clear()
    {
        _perms.Clear();
        _denies.Clear();
        _currentUserId = null;
        _roleCode = string.Empty;
    }

    public IReadOnlyCollection<string> GetCurrentPermissions() => _perms;

    // ------------------------------------------------------------------
    // Loader semantics (both providers, identical logic):
    //   effective(user) =
    //       (permissions granted to ANY of the user's active roles via
    //        role_permissions
    //        ∪ permissions granted by the user's assigned permission
    //          templates (migration 125: user_permission_templates →
    //          permission_template_grants; active, non-deleted
    //          templates of the user's own company or global ones)
    //        ∪ per-user allows in user_permissions)
    //       − per-user denies in user_permissions.
    //   The per-user deny codes are ALSO loaded into _denies so the
    //   DOC matrix can subtract a single document type from a broad
    //   generic grant (IsExplicitlyDenied).
    //   A user holding ROLE_ADMINISTRATOR is granted every active
    //   permission (the role's seeded meaning) minus per-user denies.
    // The previous implementation CROSS JOINed the permission catalog
    // and ignored role_permissions, which granted every user every
    // permission; the queries below resolve strictly through
    // user_roles → role_permissions and never enumerate the catalog
    // for non-administrators.
    // Existence is checked separately from the grant query so a real
    // user whose roles grant nothing loads successfully with an empty
    // set instead of being mistaken for a missing user.
    // ------------------------------------------------------------------

    private async Task LoadPostgresAsync(string connectionString, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        Guid resolvedId;
        await using (var resolve = new NpgsqlCommand("""
            SELECT u.user_id
            FROM public.users AS u
            WHERE u.is_active = TRUE AND u.is_deleted = FALSE
              AND (u.username = @UserId OR u.user_id =
                  CASE WHEN @UserId ~* '^[0-9a-f-]{36}$' THEN CAST(@UserId AS uuid) END)
            LIMIT 1;
            """, connection))
        {
            resolve.Parameters.AddWithValue("@UserId", _currentUserId!);
            var scalar = await resolve.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (scalar is not Guid guid)
            {
                _currentUserId = null;
                throw new UnauthorizedAccessException("The user is inactive, deleted, or does not exist.");
            }
            resolvedId = guid;
        }

        var isAdministrator = false;
        await using (var roles = new NpgsqlCommand("""
            SELECT r.role_code
            FROM public.user_roles AS ur
            INNER JOIN public.roles AS r ON r.role_id = ur.role_id
                AND r.is_active = TRUE AND r.is_deleted = FALSE
            WHERE ur.user_id = @Uid;
            """, connection))
        {
            roles.Parameters.AddWithValue("@Uid", resolvedId);
            await using var roleReader = await roles.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await roleReader.ReadAsync(ct).ConfigureAwait(false))
            {
                var roleCode = roleReader.GetString(0);
                if (string.Equals(roleCode, "ROLE_ADMINISTRATOR", StringComparison.OrdinalIgnoreCase))
                {
                    isAdministrator = true;
                    _roleCode = roleCode;
                }
                else if (string.IsNullOrWhiteSpace(_roleCode))
                {
                    _roleCode = roleCode;
                }
            }
        }

        await using var command = new NpgsqlCommand("""
            SELECT p.permission_code
            FROM public.permissions AS p
            WHERE p.is_active = TRUE
              AND (
                    @IsAdmin
                    OR EXISTS (
                        SELECT 1
                        FROM public.role_permissions AS rp
                        INNER JOIN public.user_roles AS ur ON ur.role_id = rp.role_id
                        INNER JOIN public.roles AS r ON r.role_id = ur.role_id
                            AND r.is_active = TRUE AND r.is_deleted = FALSE
                        WHERE ur.user_id = @Uid AND rp.permission_id = p.permission_id)
                    OR EXISTS (
                        SELECT 1
                        FROM public.user_permission_templates AS upt
                        INNER JOIN public.permission_templates AS pt ON pt.template_id = upt.template_id
                            AND pt.is_active = TRUE AND pt.is_deleted = FALSE
                            AND (pt.company_id IS NULL OR pt.company_id =
                                (SELECT u2.company_id FROM public.users AS u2 WHERE u2.user_id = @Uid))
                        INNER JOIN public.permission_template_grants AS ptg ON ptg.template_id = pt.template_id
                        WHERE upt.user_id = @Uid AND ptg.permission_id = p.permission_id)
                    OR EXISTS (
                        SELECT 1 FROM public.user_permissions AS up
                        WHERE up.user_id = @Uid AND up.permission_id = p.permission_id
                          AND up.effect = TRUE)
                  )
              AND NOT EXISTS (
                    SELECT 1 FROM public.user_permissions AS up
                    WHERE up.user_id = @Uid AND up.permission_id = p.permission_id
                      AND up.effect = FALSE);
            """, connection);
        command.Parameters.AddWithValue("@Uid", resolvedId);
        command.Parameters.AddWithValue("@IsAdmin", isAdministrator);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            _perms.Add(reader.GetString(0));

        await using var denyCommand = new NpgsqlCommand("""
            SELECT p.permission_code
            FROM public.user_permissions AS up
            INNER JOIN public.permissions AS p ON p.permission_id = up.permission_id
            WHERE up.user_id = @Uid AND up.effect = FALSE AND p.is_active = TRUE;
            """, connection);
        denyCommand.Parameters.AddWithValue("@Uid", resolvedId);
        await using var denyReader = await denyCommand.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await denyReader.ReadAsync(ct).ConfigureAwait(false))
            _denies.Add(denyReader.GetString(0));
    }

    private async Task LoadSqliteAsync(string connectionString, CancellationToken ct)
    {
        SqliteSchemaBootstrapper.EnsureDatabase(connectionString);
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        string resolvedId;
        await using (var resolve = new SqliteCommand("""
            SELECT u.user_id
            FROM users AS u
            WHERE u.is_active = 1 AND u.is_deleted = 0
              AND (u.username = @user_id OR u.user_id = @user_id)
            LIMIT 1
            """, connection))
        {
            resolve.Parameters.AddWithValue("@user_id", _currentUserId);
            var scalar = await resolve.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (scalar is not string id || string.IsNullOrWhiteSpace(id))
            {
                _currentUserId = null;
                throw new UnauthorizedAccessException("The user is inactive, deleted, or does not exist.");
            }
            resolvedId = id;
        }

        var isAdministrator = false;
        await using (var roles = new SqliteCommand("""
            SELECT r.role_code
            FROM user_roles AS ur
            INNER JOIN roles AS r ON r.role_id = ur.role_id
                AND r.is_active = 1 AND r.is_deleted = 0
            WHERE ur.user_id = @uid
            """, connection))
        {
            roles.Parameters.AddWithValue("@uid", resolvedId);
            await using var roleReader = await roles.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await roleReader.ReadAsync(ct).ConfigureAwait(false))
            {
                var roleCode = roleReader.GetString(0);
                if (string.Equals(roleCode, "ROLE_ADMINISTRATOR", StringComparison.OrdinalIgnoreCase))
                {
                    isAdministrator = true;
                    _roleCode = roleCode;
                }
                else if (string.IsNullOrWhiteSpace(_roleCode))
                {
                    _roleCode = roleCode;
                }
            }
        }

        await using var command = new SqliteCommand("""
            SELECT p.permission_code
            FROM permissions AS p
            WHERE p.is_active = 1
              AND (
                    @is_admin = 1
                    OR EXISTS (
                        SELECT 1
                        FROM role_permissions AS rp
                        INNER JOIN user_roles AS ur ON ur.role_id = rp.role_id
                        INNER JOIN roles AS r ON r.role_id = ur.role_id
                            AND r.is_active = 1 AND r.is_deleted = 0
                        WHERE ur.user_id = @uid AND rp.permission_id = p.permission_id)
                    OR EXISTS (
                        SELECT 1
                        FROM user_permission_templates AS upt
                        INNER JOIN permission_templates AS pt ON pt.template_id = upt.template_id
                            AND pt.is_active = 1 AND pt.is_deleted = 0
                            AND (pt.company_id IS NULL OR pt.company_id = '' OR pt.company_id =
                                (SELECT u2.company_id FROM users AS u2 WHERE u2.user_id = @uid))
                        INNER JOIN permission_template_grants AS ptg ON ptg.template_id = pt.template_id
                        WHERE upt.user_id = @uid AND ptg.permission_id = p.permission_id)
                    OR EXISTS (
                        SELECT 1 FROM user_permissions AS up
                        WHERE up.user_id = @uid AND up.permission_id = p.permission_id
                          AND up.effect = 1)
                  )
              AND NOT EXISTS (
                    SELECT 1 FROM user_permissions AS up
                    WHERE up.user_id = @uid AND up.permission_id = p.permission_id
                      AND up.effect = 0)
            """, connection);
        command.Parameters.AddWithValue("@uid", resolvedId);
        command.Parameters.AddWithValue("@is_admin", isAdministrator ? 1 : 0);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            _perms.Add(reader.GetString(0));

        await using var denyCommand = new SqliteCommand("""
            SELECT p.permission_code
            FROM user_permissions AS up
            INNER JOIN permissions AS p ON p.permission_id = up.permission_id
            WHERE up.user_id = @uid AND up.effect = 0 AND p.is_active = 1
            """, connection);
        denyCommand.Parameters.AddWithValue("@uid", resolvedId);
        await using var denyReader = await denyCommand.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await denyReader.ReadAsync(ct).ConfigureAwait(false))
            _denies.Add(denyReader.GetString(0));
    }


    // ------------------------------------------------------------------
    // Permission Inspector (PERMISSIONS_STUDY section 2.6)
    //
    // Decision order — the mirror image of the loader's set algebra:
    //   1. an explicit per-user deny decides first (it wins over
    //      everything, administrators included);
    //   2. ROLE_ADMINISTRATOR membership (the loader grants the whole
    //      active catalog to it);
    //   3. a grant by one of the user's roles (names the role);
    //   4. a grant by an assigned permission template (names it);
    //   5. a per-user allow override;
    //   6. granted nowhere. An unknown or inactive permission key is
    //      also "granted nowhere": the loader only ever surfaces
    //      active catalog rows.
    // ------------------------------------------------------------------

    public async Task<PermExplanation> ExplainAsync(string userId, string permissionKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("User ID is required.", nameof(userId));
        if (string.IsNullOrWhiteSpace(permissionKey))
            throw new ArgumentException("Permission key is required.", nameof(permissionKey));
        var key = permissionKey.Trim().ToUpperInvariant();

        var provider = DatabaseRuntime.GetProvider();
        var connectionString = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Permissions require a database connection.");
        if (DatabaseRuntime.IsPostgreSql(provider))
            return await ExplainPostgresAsync(connectionString, userId.Trim(), key, ct).ConfigureAwait(false);
        if (DatabaseRuntime.IsSqlite(provider))
            return await ExplainSqliteAsync(connectionString, userId.Trim(), key, ct).ConfigureAwait(false);
        throw new NotSupportedException($"Unsupported database provider: {provider}");
    }

    private static async Task<PermExplanation> ExplainPostgresAsync(string connectionString, string userId, string key, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        Guid resolvedId;
        await using (var resolve = new NpgsqlCommand(
            "SELECT user_id FROM public.users WHERE user_id::text = @Uid OR username = @Uid LIMIT 1", connection))
        {
            resolve.Parameters.AddWithValue("@Uid", userId);
            var raw = await resolve.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (raw is null or DBNull)
                throw new UnauthorizedAccessException($"User '{userId}' was not found in PostgreSQL.");
            resolvedId = (Guid)raw;
        }

        long permissionId;
        await using (var perm = new NpgsqlCommand(
            "SELECT permission_id FROM public.permissions WHERE permission_code = @Key AND is_active = TRUE LIMIT 1", connection))
        {
            perm.Parameters.AddWithValue("@Key", key);
            var raw = await perm.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (raw is null or DBNull)
                return new PermExplanation(false, PermSourceKind.None, null, key);
            permissionId = Convert.ToInt64(raw, CultureInfo.InvariantCulture);
        }

        async Task<long> CountAsync(string sql)
        {
            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@Uid", resolvedId);
            cmd.Parameters.AddWithValue("@Pid", permissionId);
            var raw = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return raw is null or DBNull ? 0 : Convert.ToInt64(raw, CultureInfo.InvariantCulture);
        }

        async Task<string?> ScalarTextAsync(string sql)
        {
            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@Uid", resolvedId);
            cmd.Parameters.AddWithValue("@Pid", permissionId);
            var raw = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return raw is null or DBNull ? null : Convert.ToString(raw, CultureInfo.InvariantCulture);
        }

        if (await CountAsync("""
            SELECT COUNT(*) FROM public.user_permissions
            WHERE user_id = @Uid AND permission_id = @Pid AND effect = FALSE
            """).ConfigureAwait(false) > 0)
            return new PermExplanation(false, PermSourceKind.UserDeny, null, key);

        if (await CountAsync("""
            SELECT COUNT(*) FROM public.user_roles AS ur
            INNER JOIN public.roles AS r ON r.role_id = ur.role_id
            WHERE ur.user_id = @Uid AND r.role_code = 'ROLE_ADMINISTRATOR'
            """).ConfigureAwait(false) > 0)
            return new PermExplanation(true, PermSourceKind.AdministratorRole, null, key);

        var roleCode = await ScalarTextAsync("""
            SELECT r.role_code FROM public.role_permissions AS rp
            INNER JOIN public.user_roles AS ur ON ur.role_id = rp.role_id
            INNER JOIN public.roles AS r ON r.role_id = rp.role_id
            WHERE ur.user_id = @Uid AND rp.permission_id = @Pid
            ORDER BY r.role_code LIMIT 1
            """).ConfigureAwait(false);
        if (roleCode is not null)
            return new PermExplanation(true, PermSourceKind.Role, roleCode, key);

        var templateCode = await ScalarTextAsync("""
            SELECT pt.template_code FROM public.user_permission_templates AS upt
            INNER JOIN public.permission_templates AS pt ON pt.template_id = upt.template_id
                AND pt.is_active = TRUE AND pt.is_deleted = FALSE
                AND (pt.company_id IS NULL OR pt.company_id =
                    (SELECT u2.company_id FROM public.users AS u2 WHERE u2.user_id = @Uid))
            INNER JOIN public.permission_template_grants AS ptg ON ptg.template_id = pt.template_id
            WHERE upt.user_id = @Uid AND ptg.permission_id = @Pid
            ORDER BY pt.template_code LIMIT 1
            """).ConfigureAwait(false);
        if (templateCode is not null)
            return new PermExplanation(true, PermSourceKind.Template, templateCode, key);

        if (await CountAsync("""
            SELECT COUNT(*) FROM public.user_permissions
            WHERE user_id = @Uid AND permission_id = @Pid AND effect = TRUE
            """).ConfigureAwait(false) > 0)
            return new PermExplanation(true, PermSourceKind.UserAllow, null, key);

        return new PermExplanation(false, PermSourceKind.None, null, key);
    }

    private static async Task<PermExplanation> ExplainSqliteAsync(string connectionString, string userId, string key, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        string resolvedId;
        await using (var resolve = new SqliteCommand(
            "SELECT user_id FROM users WHERE user_id = @uid OR username = @uid LIMIT 1", connection))
        {
            resolve.Parameters.AddWithValue("@uid", userId);
            var raw = await resolve.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (raw is null or DBNull)
                throw new UnauthorizedAccessException($"User '{userId}' was not found in SQLite.");
            resolvedId = Convert.ToString(raw, CultureInfo.InvariantCulture)!;
        }

        long permissionId;
        await using (var perm = new SqliteCommand(
            "SELECT permission_id FROM permissions WHERE permission_code = @key AND is_active = 1 LIMIT 1", connection))
        {
            perm.Parameters.AddWithValue("@key", key);
            var raw = await perm.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (raw is null or DBNull)
                return new PermExplanation(false, PermSourceKind.None, null, key);
            permissionId = Convert.ToInt64(raw, CultureInfo.InvariantCulture);
        }

        async Task<long> CountAsync(string sql)
        {
            await using var cmd = new SqliteCommand(sql, connection);
            cmd.Parameters.AddWithValue("@uid", resolvedId);
            cmd.Parameters.AddWithValue("@pid", permissionId);
            var raw = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return raw is null or DBNull ? 0 : Convert.ToInt64(raw, CultureInfo.InvariantCulture);
        }

        async Task<string?> ScalarTextAsync(string sql)
        {
            await using var cmd = new SqliteCommand(sql, connection);
            cmd.Parameters.AddWithValue("@uid", resolvedId);
            cmd.Parameters.AddWithValue("@pid", permissionId);
            var raw = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return raw is null or DBNull ? null : Convert.ToString(raw, CultureInfo.InvariantCulture);
        }

        if (await CountAsync("""
            SELECT COUNT(*) FROM user_permissions
            WHERE user_id = @uid AND permission_id = @pid AND effect = 0
            """).ConfigureAwait(false) > 0)
            return new PermExplanation(false, PermSourceKind.UserDeny, null, key);

        if (await CountAsync("""
            SELECT COUNT(*) FROM user_roles AS ur
            INNER JOIN roles AS r ON r.role_id = ur.role_id
            WHERE ur.user_id = @uid AND r.role_code = 'ROLE_ADMINISTRATOR'
            """).ConfigureAwait(false) > 0)
            return new PermExplanation(true, PermSourceKind.AdministratorRole, null, key);

        var roleCode = await ScalarTextAsync("""
            SELECT r.role_code FROM role_permissions AS rp
            INNER JOIN user_roles AS ur ON ur.role_id = rp.role_id
            INNER JOIN roles AS r ON r.role_id = rp.role_id
            WHERE ur.user_id = @uid AND rp.permission_id = @pid
            ORDER BY r.role_code LIMIT 1
            """).ConfigureAwait(false);
        if (roleCode is not null)
            return new PermExplanation(true, PermSourceKind.Role, roleCode, key);

        var templateCode = await ScalarTextAsync("""
            SELECT pt.template_code FROM user_permission_templates AS upt
            INNER JOIN permission_templates AS pt ON pt.template_id = upt.template_id
                AND pt.is_active = 1 AND pt.is_deleted = 0
                AND (pt.company_id IS NULL OR pt.company_id = '' OR pt.company_id =
                    (SELECT u2.company_id FROM users AS u2 WHERE u2.user_id = @uid))
            INNER JOIN permission_template_grants AS ptg ON ptg.template_id = pt.template_id
            WHERE upt.user_id = @uid AND ptg.permission_id = @pid
            ORDER BY pt.template_code LIMIT 1
            """).ConfigureAwait(false);
        if (templateCode is not null)
            return new PermExplanation(true, PermSourceKind.Template, templateCode, key);

        if (await CountAsync("""
            SELECT COUNT(*) FROM user_permissions
            WHERE user_id = @uid AND permission_id = @pid AND effect = 1
            """).ConfigureAwait(false) > 0)
            return new PermExplanation(true, PermSourceKind.UserAllow, null, key);

        return new PermExplanation(false, PermSourceKind.None, null, key);
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
    public sealed record PermissionItem(long Id, string Code, string Feature, string Operation, string Group,
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

    // The administration entry points historically received a USERNAME
    // (RibbonNav opens PrmWin with AppState.CurrentUser.Username) while
    // user_roles / user_permissions are keyed by the database user_id.
    // Every load/save therefore resolves the supplied identifier —
    // username or user_id — to the canonical user_id first; matching a
    // username against user_id directly loaded an all-denied baseline
    // and wrote orphaned override rows.
    private static async Task<string> ResolveSqliteUserIdAsync(SqliteConnection c, string identifier, CancellationToken ct)
    {
        await using var cmd = new SqliteCommand(
            "SELECT user_id FROM users WHERE username=@id OR user_id=@id LIMIT 1", c);
        cmd.Parameters.AddWithValue("@id", identifier);
        var scalar = await cmd.ExecuteScalarAsync(ct);
        if (scalar is not string id || string.IsNullOrWhiteSpace(id))
            throw new ArgumentException($"User '{identifier}' was not found.", nameof(identifier));
        return id;
    }

    private static async Task<Guid> ResolvePostgresUserIdAsync(NpgsqlConnection c, string identifier, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT user_id FROM public.users
            WHERE username = @id OR user_id =
                CASE WHEN @id ~* '^[0-9a-f-]{36}$' THEN CAST(@id AS uuid) END
            LIMIT 1
            """, c);
        cmd.Parameters.AddWithValue("@id", identifier);
        var scalar = await cmd.ExecuteScalarAsync(ct);
        if (scalar is not Guid id)
            throw new ArgumentException($"User '{identifier}' was not found.", nameof(identifier));
        return id;
    }

    private static async Task<IReadOnlyList<PermissionItem>> LoadSqliteAsync(string cs, string userId, CancellationToken ct)
    {
        await using var c = new SqliteConnection(cs); await c.OpenAsync(ct);
        var uid = await ResolveSqliteUserIdAsync(c, userId, ct);
        const string sql = @"
SELECT p.permission_id,p.permission_code,p.feature_code,p.operation_code,p.group_code,p.name_ar,p.name_en,p.is_sensitive,
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
        await using var cmd = new SqliteCommand(sql, c); cmd.Parameters.AddWithValue("@uid", uid);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var result = new List<PermissionItem>();
        while (await r.ReadAsync(ct)) result.Add(new(r.GetInt64(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetString(6),r.GetInt64(7)!=0,r.GetInt64(8)!=0,r.GetInt64(9)!=0));
        return result;
    }

    private static async Task<IReadOnlyList<PermissionItem>> LoadPostgresAsync(string cs, string userId, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(cs); await c.OpenAsync(ct);
        var uid = await ResolvePostgresUserIdAsync(c, userId, ct);
        const string sql = @"
SELECT p.permission_id,p.permission_code,p.feature_code,p.operation_code,p.group_code,p.name_ar,p.name_en,p.is_sensitive,
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
        while (await r.ReadAsync(ct)) result.Add(new(r.GetInt64(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetString(6),r.GetBoolean(7),r.GetBoolean(8),r.GetBoolean(9)));
        return result;
    }

    private static async Task SaveSqliteAsync(string cs, string userId, IReadOnlyDictionary<long, bool> effective, CancellationToken ct)
    {
        await using var c = new SqliteConnection(cs); await c.OpenAsync(ct);
        userId = await ResolveSqliteUserIdAsync(c, userId, ct);
        await using var tx = c.BeginTransaction();
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
        await using var c = new NpgsqlConnection(cs); await c.OpenAsync(ct);
        var uid = await ResolvePostgresUserIdAsync(c, userId, ct);
        await using var tx = c.BeginTransaction();
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

// ========================================================================
// PermTplSvc — permission template administration (PERMISSIONS_STUDY
// section 2.2). Templates are named, reusable grant sets that resolve
// as ONE FLAT UNION in the permission loader:
//
//     effective(user) = role grants
//                     ∪ template grants (via user_permission_templates)
//                     ∪ per-user allows
//                     − per-user denies
//
// Templates live outside role_permissions, so the SQLite bootstrapper's
// role-baseline rebuild can never destroy them. Seeded system
// templates (company_id NULL) are immutable through this service —
// Copy makes an editable company template from any template. Every
// mutation is guarded like PermAdminSvc (administrator session +
// SYS.PERMISSION_TEMPLATES.MANAGE) and writes its security_audit_log
// rows inside the same transaction.
// ========================================================================
public sealed class PermTplSvc
{
    public sealed record TemplateRow(long Id, string Code, string NameEn, string NameAr,
        string? Description, bool IsSystem, bool IsActive, int GrantCount, int AssignedUsers);

    public sealed record CatalogItem(long Id, string Code, string Group, string Feature,
        string Operation, string Name, string NameEn, bool Sensitive);

    public sealed record UserRow(string Id, string Username, string FullName, bool IsActive);

    // ------------------------------------------------------------------
    // Loads
    // ------------------------------------------------------------------

    /// <summary>Templates visible in the current company: the global
    /// (system) templates plus this company's own, deleted excluded.</summary>
    public static async Task<IReadOnlyList<TemplateRow>> ListTemplatesAsync(CancellationToken ct = default)
    {
        var (provider, cs) = RequireConnection();
        if (DatabaseRuntime.IsSqlite(provider))
            return await ListTemplatesSqliteAsync(cs, ct).ConfigureAwait(false);
        if (DatabaseRuntime.IsPostgreSql(provider))
            return await ListTemplatesPostgresAsync(cs, ct).ConfigureAwait(false);
        throw new NotSupportedException($"Unsupported database provider: {provider}");
    }

    /// <summary>The full active permission catalog with stored group
    /// codes — the same rows PermAdminSvc shows, without a user
    /// baseline: the grant editor's universe.</summary>
    public static async Task<IReadOnlyList<CatalogItem>> LoadCatalogAsync(CancellationToken ct = default)
    {
        var (provider, cs) = RequireConnection();
        if (DatabaseRuntime.IsSqlite(provider))
            return await LoadCatalogSqliteAsync(cs, ct).ConfigureAwait(false);
        if (DatabaseRuntime.IsPostgreSql(provider))
            return await LoadCatalogPostgresAsync(cs, ct).ConfigureAwait(false);
        throw new NotSupportedException($"Unsupported database provider: {provider}");
    }

    /// <summary>Users of the current company (assignment checklist +
    /// inspector picker).</summary>
    public static async Task<IReadOnlyList<UserRow>> ListCompanyUsersAsync(CancellationToken ct = default)
    {
        var (provider, cs) = RequireConnection();
        if (DatabaseRuntime.IsSqlite(provider))
            return await ListUsersSqliteAsync(cs, ct).ConfigureAwait(false);
        if (DatabaseRuntime.IsPostgreSql(provider))
            return await ListUsersPostgresAsync(cs, ct).ConfigureAwait(false);
        throw new NotSupportedException($"Unsupported database provider: {provider}");
    }

    /// <summary>The grant permission ids and assigned user ids of one
    /// template. A template outside the visible scope (another
    /// company's, or deleted) raises PRM-8506.</summary>
    public static async Task<(IReadOnlyList<long> Grants, IReadOnlyList<string> UserIds)> LoadTemplateDetailAsync(long templateId, CancellationToken ct = default)
    {
        var (provider, cs) = RequireConnection();
        if (DatabaseRuntime.IsSqlite(provider))
            return await LoadDetailSqliteAsync(cs, templateId, ct).ConfigureAwait(false);
        if (DatabaseRuntime.IsPostgreSql(provider))
            return await LoadDetailPostgresAsync(cs, templateId, ct).ConfigureAwait(false);
        throw new NotSupportedException($"Unsupported database provider: {provider}");
    }

    // ------------------------------------------------------------------
    // Mutations (guarded; audited in-transaction)
    // ------------------------------------------------------------------

    /// <summary>Creates (templateId = 0) or updates a company template
    /// together with its full grant set and assignment set. System
    /// templates refuse with PRM-8504, an unknown id with PRM-8506, a
    /// code used by any visible template with PRM-8503, and an
    /// assignee from another company with PRM-8505.</summary>
    public static async Task<long> SaveTemplateAsync(long templateId, string code, string nameEn, string nameAr,
        string? description, bool isActive, IReadOnlyCollection<long> grantIds, IReadOnlyCollection<string> userIds,
        CancellationToken ct = default)
    {
        EnsureTemplateManager();
        if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("Template code is required.", nameof(code));
        if (string.IsNullOrWhiteSpace(nameEn)) throw new ArgumentException("Template English name is required.", nameof(nameEn));
        var (provider, cs) = RequireConnection();
        if (DatabaseRuntime.IsSqlite(provider))
            return await SaveSqliteAsync(cs, templateId, code.Trim(), nameEn.Trim(), (nameAr ?? string.Empty).Trim(),
                description, isActive, grantIds, userIds, ct).ConfigureAwait(false);
        if (DatabaseRuntime.IsPostgreSql(provider))
            return await SavePostgresAsync(cs, templateId, code.Trim(), nameEn.Trim(), (nameAr ?? string.Empty).Trim(),
                description, isActive, grantIds, userIds, ct).ConfigureAwait(false);
        throw new NotSupportedException($"Unsupported database provider: {provider}");
    }

    /// <summary>Copies any visible template (system included) into a
    /// new editable company template with the same grants. Assignments
    /// are NOT copied — they are per-company decisions.</summary>
    public static async Task<long> CopyTemplateAsync(long sourceTemplateId, string newCode, CancellationToken ct = default)
    {
        EnsureTemplateManager();
        if (string.IsNullOrWhiteSpace(newCode)) throw new ArgumentException("Template code is required.", nameof(newCode));
        var (provider, cs) = RequireConnection();
        if (DatabaseRuntime.IsSqlite(provider))
            return await CopySqliteAsync(cs, sourceTemplateId, newCode.Trim(), ct).ConfigureAwait(false);
        if (DatabaseRuntime.IsPostgreSql(provider))
            return await CopyPostgresAsync(cs, sourceTemplateId, newCode.Trim(), ct).ConfigureAwait(false);
        throw new NotSupportedException($"Unsupported database provider: {provider}");
    }

    /// <summary>Soft-deletes a company template and removes its
    /// assignments, so the loader stops honoring it at once.</summary>
    public static async Task DeleteTemplateAsync(long templateId, CancellationToken ct = default)
    {
        EnsureTemplateManager();
        var (provider, cs) = RequireConnection();
        if (DatabaseRuntime.IsSqlite(provider))
        {
            await DeleteSqliteAsync(cs, templateId, ct).ConfigureAwait(false);
            return;
        }
        if (DatabaseRuntime.IsPostgreSql(provider))
        {
            await DeletePostgresAsync(cs, templateId, ct).ConfigureAwait(false);
            return;
        }
        throw new NotSupportedException($"Unsupported database provider: {provider}");
    }

    // ------------------------------------------------------------------
    // Shared helpers
    // ------------------------------------------------------------------

    private static (string Provider, string ConnectionString) RequireConnection()
    {
        var provider = DatabaseRuntime.GetProvider();
        var cs = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException("Permission templates require a database connection.");
        return (provider, cs);
    }

    private static void EnsureTemplateManager()
    {
        if (!UsrSession.IsAdministrator || !AppHost.Permissions.Has(PermCat.System.PermissionTemplatesManage))
            throw new UnauthorizedAccessException("Only an Administrator with permission-template rights can change permission templates.");
    }

    private static string ActorUsername()
        => UsrSession.Current?.Username ?? AppState.CurrentUser?.Username ?? "system";

    private static AppError NotFound()
        => new(ErrorCodes.PermissionTemplateNotFound, "The permission template was not found.");

    private static AppError SystemImmutable()
        => new(ErrorCodes.PermissionTemplateSystemImmutable, "System templates cannot be changed or deleted.");

    private static AppError DuplicateCode()
        => new(ErrorCodes.PermissionTemplateCodeDuplicate, "A permission template with this code already exists.");

    private static AppError CompanyMismatch()
        => new(ErrorCodes.PermissionTemplateCompanyMismatch, "A template can only be assigned to users of its own company.");

    // ------------------------------------------------------------------
    // SQLite implementations
    // ------------------------------------------------------------------

    private static async Task<string> ResolveCompanySqliteAsync(SqliteConnection c, SqliteTransaction? tx, CancellationToken ct)
    {
        var code = AppState.CurrentCompanyCode;
        if (string.IsNullOrWhiteSpace(code))
            throw new InvalidOperationException("Permission templates require an open company.");
        await using var cmd = new SqliteCommand(
            "SELECT company_id FROM companies WHERE company_code = @code AND is_deleted = 0 LIMIT 1", c, tx);
        cmd.Parameters.AddWithValue("@code", code.Trim());
        var raw = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (raw is null or DBNull)
            throw new InvalidOperationException($"Company '{code}' was not found.");
        return Convert.ToString(raw, CultureInfo.InvariantCulture)!;
    }

    private static async Task AuditSqliteAsync(SqliteConnection c, SqliteTransaction tx, string companyId, string eventType, string details, CancellationToken ct)
    {
        var actor = ActorUsername();
        string? actorId = null;
        await using (var a = new SqliteCommand("SELECT user_id FROM users WHERE username = @u AND company_id = @cid LIMIT 1", c, tx))
        {
            a.Parameters.AddWithValue("@u", actor);
            a.Parameters.AddWithValue("@cid", companyId);
            var raw = await a.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (raw is not null and not DBNull) actorId = Convert.ToString(raw, CultureInfo.InvariantCulture);
        }
        await using var cmd = new SqliteCommand(
            "INSERT INTO security_audit_log(company_id, user_id, username, event_type, details) VALUES(@cid, @uid, @user, @event, @details)", c, tx);
        cmd.Parameters.AddWithValue("@cid", companyId);
        cmd.Parameters.AddWithValue("@uid", (object?)actorId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@user", actor);
        cmd.Parameters.AddWithValue("@event", eventType);
        cmd.Parameters.AddWithValue("@details", details);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<TemplateRow>> ListTemplatesSqliteAsync(string cs, CancellationToken ct)
    {
        try
        {
            await using var c = new SqliteConnection(cs);
            await c.OpenAsync(ct).ConfigureAwait(false);
            var companyId = await ResolveCompanySqliteAsync(c, null, ct).ConfigureAwait(false);
            const string sql = """
                SELECT t.template_id, t.template_code, t.name_en, t.name_ar, t.description, t.is_system, t.is_active,
                       (SELECT COUNT(*) FROM permission_template_grants g WHERE g.template_id = t.template_id),
                       (SELECT COUNT(*) FROM user_permission_templates a WHERE a.template_id = t.template_id)
                FROM permission_templates t
                WHERE t.is_deleted = 0 AND (t.company_id IS NULL OR t.company_id = '' OR t.company_id = @cid)
                ORDER BY t.is_system DESC, t.template_code
                """;
            await using var cmd = new SqliteCommand(sql, c);
            cmd.Parameters.AddWithValue("@cid", companyId);
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var result = new List<TemplateRow>();
            while (await r.ReadAsync(ct).ConfigureAwait(false))
                result.Add(new TemplateRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4), r.GetInt64(5) != 0, r.GetInt64(6) != 0,
                    Convert.ToInt32(r.GetInt64(7)), Convert.ToInt32(r.GetInt64(8))));
            return result;
        }
        catch (AppError) { throw; }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.PermissionTemplateLoadFailed, ex, "PermTplSvc.ListTemplates.Sqlite");
            throw;
        }
    }

    private static async Task<IReadOnlyList<CatalogItem>> LoadCatalogSqliteAsync(string cs, CancellationToken ct)
    {
        try
        {
            await using var c = new SqliteConnection(cs);
            await c.OpenAsync(ct).ConfigureAwait(false);
            const string sql = """
                SELECT permission_id, permission_code, group_code, feature_code, operation_code, name_ar, name_en, is_sensitive
                FROM permissions WHERE is_active = 1 ORDER BY display_order, permission_code
                """;
            await using var cmd = new SqliteCommand(sql, c);
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var result = new List<CatalogItem>();
            while (await r.ReadAsync(ct).ConfigureAwait(false))
                result.Add(new CatalogItem(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
                    r.GetString(5), r.GetString(6), r.GetInt64(7) != 0));
            return result;
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.PermissionTemplateLoadFailed, ex, "PermTplSvc.LoadCatalog.Sqlite");
            throw;
        }
    }

    private static async Task<IReadOnlyList<UserRow>> ListUsersSqliteAsync(string cs, CancellationToken ct)
    {
        try
        {
            await using var c = new SqliteConnection(cs);
            await c.OpenAsync(ct).ConfigureAwait(false);
            var companyId = await ResolveCompanySqliteAsync(c, null, ct).ConfigureAwait(false);
            const string sql = """
                SELECT user_id, username, full_name, is_active FROM users
                WHERE company_id = @cid AND is_deleted = 0 ORDER BY username
                """;
            await using var cmd = new SqliteCommand(sql, c);
            cmd.Parameters.AddWithValue("@cid", companyId);
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var result = new List<UserRow>();
            while (await r.ReadAsync(ct).ConfigureAwait(false))
                result.Add(new UserRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3) != 0));
            return result;
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.PermissionTemplateLoadFailed, ex, "PermTplSvc.ListUsers.Sqlite");
            throw;
        }
    }

    /// <summary>Loads a template header inside the visible scope or
    /// throws PRM-8506. Returns (company_id as stored, is_system).</summary>
    private static async Task<(string? CompanyId, bool IsSystem)> LoadTemplateHeaderSqliteAsync(
        SqliteConnection c, SqliteTransaction? tx, long templateId, string companyId, CancellationToken ct)
    {
        await using var cmd = new SqliteCommand("""
            SELECT company_id, is_system FROM permission_templates
            WHERE template_id = @id AND is_deleted = 0
              AND (company_id IS NULL OR company_id = '' OR company_id = @cid)
            LIMIT 1
            """, c, tx);
        cmd.Parameters.AddWithValue("@id", templateId);
        cmd.Parameters.AddWithValue("@cid", companyId);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false)) throw NotFound();
        return (r.IsDBNull(0) ? null : r.GetString(0), r.GetInt64(1) != 0);
    }

    private static async Task<(IReadOnlyList<long> Grants, IReadOnlyList<string> UserIds)> LoadDetailSqliteAsync(string cs, long templateId, CancellationToken ct)
    {
        try
        {
            await using var c = new SqliteConnection(cs);
            await c.OpenAsync(ct).ConfigureAwait(false);
            var companyId = await ResolveCompanySqliteAsync(c, null, ct).ConfigureAwait(false);
            await LoadTemplateHeaderSqliteAsync(c, null, templateId, companyId, ct).ConfigureAwait(false);
            var grants = new List<long>();
            await using (var g = new SqliteCommand("SELECT permission_id FROM permission_template_grants WHERE template_id = @id ORDER BY permission_id", c))
            {
                g.Parameters.AddWithValue("@id", templateId);
                await using var r = await g.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await r.ReadAsync(ct).ConfigureAwait(false)) grants.Add(r.GetInt64(0));
            }
            var users = new List<string>();
            await using (var u = new SqliteCommand("SELECT user_id FROM user_permission_templates WHERE template_id = @id ORDER BY user_id", c))
            {
                u.Parameters.AddWithValue("@id", templateId);
                await using var r = await u.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await r.ReadAsync(ct).ConfigureAwait(false)) users.Add(r.GetString(0));
            }
            return (grants, users);
        }
        catch (AppError) { throw; }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.PermissionTemplateLoadFailed, ex, "PermTplSvc.LoadDetail.Sqlite");
            throw;
        }
    }

    private static async Task<long> SaveSqliteAsync(string cs, long templateId, string code, string nameEn, string nameAr,
        string? description, bool isActive, IReadOnlyCollection<long> grantIds, IReadOnlyCollection<string> userIds, CancellationToken ct)
    {
        try
        {
            await using var c = new SqliteConnection(cs);
            await c.OpenAsync(ct).ConfigureAwait(false);
            await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct).ConfigureAwait(false);
            var companyId = await ResolveCompanySqliteAsync(c, tx, ct).ConfigureAwait(false);
            var actor = ActorUsername();

            if (templateId > 0)
            {
                var header = await LoadTemplateHeaderSqliteAsync(c, tx, templateId, companyId, ct).ConfigureAwait(false);
                if (header.IsSystem) throw SystemImmutable();
            }

            await using (var dup = new SqliteCommand("""
                SELECT COUNT(*) FROM permission_templates
                WHERE is_deleted = 0 AND template_code = @code AND template_id <> @id
                  AND (company_id IS NULL OR company_id = '' OR company_id = @cid)
                """, c, tx))
            {
                dup.Parameters.AddWithValue("@code", code);
                dup.Parameters.AddWithValue("@id", templateId);
                dup.Parameters.AddWithValue("@cid", companyId);
                if (Convert.ToInt64(await dup.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture) > 0)
                    throw DuplicateCode();
            }

            // Only ids that are real, active catalog permissions are stored.
            var validGrants = new List<long>();
            if (grantIds.Count > 0)
            {
                var active = new HashSet<long>();
                await using var all = new SqliteCommand("SELECT permission_id FROM permissions WHERE is_active = 1", c, tx);
                await using var r = await all.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await r.ReadAsync(ct).ConfigureAwait(false)) active.Add(r.GetInt64(0));
                foreach (var gid in grantIds.Distinct()) if (active.Contains(gid)) validGrants.Add(gid);
            }

            // Assignees must be users of this company (id or username accepted).
            var canonicalUsers = new List<string>();
            foreach (var candidate in userIds.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                await using var u = new SqliteCommand(
                    "SELECT user_id FROM users WHERE company_id = @cid AND is_deleted = 0 AND (user_id = @u OR username = @u) LIMIT 1", c, tx);
                u.Parameters.AddWithValue("@cid", companyId);
                u.Parameters.AddWithValue("@u", candidate);
                var raw = await u.ExecuteScalarAsync(ct).ConfigureAwait(false);
                if (raw is null or DBNull) throw CompanyMismatch();
                canonicalUsers.Add(Convert.ToString(raw, CultureInfo.InvariantCulture)!);
            }

            long id = templateId;
            if (id == 0)
            {
                await using var ins = new SqliteCommand("""
                    INSERT INTO permission_templates(company_id, template_code, name_ar, name_en, description, is_system, is_active, created_by)
                    VALUES(@cid, @code, @nameAr, @nameEn, @desc, 0, @active, @actor);
                    SELECT last_insert_rowid();
                    """, c, tx);
                ins.Parameters.AddWithValue("@cid", companyId);
                ins.Parameters.AddWithValue("@code", code);
                ins.Parameters.AddWithValue("@nameAr", nameAr);
                ins.Parameters.AddWithValue("@nameEn", nameEn);
                ins.Parameters.AddWithValue("@desc", (object?)description ?? DBNull.Value);
                ins.Parameters.AddWithValue("@active", isActive ? 1 : 0);
                ins.Parameters.AddWithValue("@actor", actor);
                id = Convert.ToInt64(await ins.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
            }
            else
            {
                await using var upd = new SqliteCommand("""
                    UPDATE permission_templates
                    SET template_code = @code, name_ar = @nameAr, name_en = @nameEn, description = @desc,
                        is_active = @active, updated_at = CURRENT_TIMESTAMP
                    WHERE template_id = @id
                    """, c, tx);
                upd.Parameters.AddWithValue("@code", code);
                upd.Parameters.AddWithValue("@nameAr", nameAr);
                upd.Parameters.AddWithValue("@nameEn", nameEn);
                upd.Parameters.AddWithValue("@desc", (object?)description ?? DBNull.Value);
                upd.Parameters.AddWithValue("@active", isActive ? 1 : 0);
                upd.Parameters.AddWithValue("@id", id);
                await upd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await using (var del = new SqliteCommand("DELETE FROM permission_template_grants WHERE template_id = @id", c, tx))
            {
                del.Parameters.AddWithValue("@id", id);
                await del.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            foreach (var pid in validGrants)
            {
                await using var g = new SqliteCommand("INSERT INTO permission_template_grants(template_id, permission_id) VALUES(@id, @pid)", c, tx);
                g.Parameters.AddWithValue("@id", id);
                g.Parameters.AddWithValue("@pid", pid);
                await g.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await using (var del = new SqliteCommand("DELETE FROM user_permission_templates WHERE template_id = @id", c, tx))
            {
                del.Parameters.AddWithValue("@id", id);
                await del.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            foreach (var uid in canonicalUsers)
            {
                await using var a = new SqliteCommand("INSERT INTO user_permission_templates(user_id, template_id, assigned_by) VALUES(@uid, @id, @actor)", c, tx);
                a.Parameters.AddWithValue("@uid", uid);
                a.Parameters.AddWithValue("@id", id);
                a.Parameters.AddWithValue("@actor", actor);
                await a.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await AuditSqliteAsync(c, tx, companyId,
                templateId == 0 ? "PERM_TEMPLATE_CREATED" : "PERM_TEMPLATE_UPDATED",
                $"template={code}; grants={validGrants.Count}; users={canonicalUsers.Count}", ct).ConfigureAwait(false);
            if (canonicalUsers.Count > 0)
                await AuditSqliteAsync(c, tx, companyId, "PERM_TEMPLATE_ASSIGNED",
                    $"template={code}; users={string.Join(",", canonicalUsers)}", ct).ConfigureAwait(false);

            await tx.CommitAsync(ct).ConfigureAwait(false);
            return id;
        }
        catch (AppError) { throw; }
        catch (UnauthorizedAccessException) { throw; }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.PermissionTemplateSaveFailed, ex, "PermTplSvc.Save.Sqlite");
            throw;
        }
    }

    private static async Task<long> CopySqliteAsync(string cs, long sourceTemplateId, string newCode, CancellationToken ct)
    {
        try
        {
            await using var c = new SqliteConnection(cs);
            await c.OpenAsync(ct).ConfigureAwait(false);
            await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct).ConfigureAwait(false);
            var companyId = await ResolveCompanySqliteAsync(c, tx, ct).ConfigureAwait(false);
            await LoadTemplateHeaderSqliteAsync(c, tx, sourceTemplateId, companyId, ct).ConfigureAwait(false);

            string nameAr, nameEn; string? description; bool active; string sourceCode;
            await using (var s = new SqliteCommand("SELECT template_code, name_ar, name_en, description, is_active FROM permission_templates WHERE template_id = @id", c, tx))
            {
                s.Parameters.AddWithValue("@id", sourceTemplateId);
                await using var r = await s.ExecuteReaderAsync(ct).ConfigureAwait(false);
                await r.ReadAsync(ct).ConfigureAwait(false);
                sourceCode = r.GetString(0); nameAr = r.GetString(1); nameEn = r.GetString(2);
                description = r.IsDBNull(3) ? null : r.GetString(3); active = r.GetInt64(4) != 0;
            }

            await using (var dup = new SqliteCommand("""
                SELECT COUNT(*) FROM permission_templates
                WHERE is_deleted = 0 AND template_code = @code
                  AND (company_id IS NULL OR company_id = '' OR company_id = @cid)
                """, c, tx))
            {
                dup.Parameters.AddWithValue("@code", newCode);
                dup.Parameters.AddWithValue("@cid", companyId);
                if (Convert.ToInt64(await dup.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture) > 0)
                    throw DuplicateCode();
            }

            long id;
            await using (var ins = new SqliteCommand("""
                INSERT INTO permission_templates(company_id, template_code, name_ar, name_en, description, is_system, is_active, created_by)
                VALUES(@cid, @code, @nameAr, @nameEn, @desc, 0, @active, @actor);
                SELECT last_insert_rowid();
                """, c, tx))
            {
                ins.Parameters.AddWithValue("@cid", companyId);
                ins.Parameters.AddWithValue("@code", newCode);
                ins.Parameters.AddWithValue("@nameAr", nameAr);
                ins.Parameters.AddWithValue("@nameEn", nameEn);
                ins.Parameters.AddWithValue("@desc", (object?)description ?? DBNull.Value);
                ins.Parameters.AddWithValue("@active", active ? 1 : 0);
                ins.Parameters.AddWithValue("@actor", ActorUsername());
                id = Convert.ToInt64(await ins.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
            }

            await using (var cp = new SqliteCommand(
                "INSERT INTO permission_template_grants(template_id, permission_id) SELECT @newId, permission_id FROM permission_template_grants WHERE template_id = @srcId", c, tx))
            {
                cp.Parameters.AddWithValue("@newId", id);
                cp.Parameters.AddWithValue("@srcId", sourceTemplateId);
                await cp.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await AuditSqliteAsync(c, tx, companyId, "PERM_TEMPLATE_COPIED",
                $"template={newCode}; from={sourceCode}", ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return id;
        }
        catch (AppError) { throw; }
        catch (UnauthorizedAccessException) { throw; }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.PermissionTemplateSaveFailed, ex, "PermTplSvc.Copy.Sqlite");
            throw;
        }
    }

    private static async Task DeleteSqliteAsync(string cs, long templateId, CancellationToken ct)
    {
        try
        {
            await using var c = new SqliteConnection(cs);
            await c.OpenAsync(ct).ConfigureAwait(false);
            await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct).ConfigureAwait(false);
            var companyId = await ResolveCompanySqliteAsync(c, tx, ct).ConfigureAwait(false);
            var header = await LoadTemplateHeaderSqliteAsync(c, tx, templateId, companyId, ct).ConfigureAwait(false);
            if (header.IsSystem || string.IsNullOrWhiteSpace(header.CompanyId)) throw SystemImmutable();

            string code;
            await using (var s = new SqliteCommand("SELECT template_code FROM permission_templates WHERE template_id = @id", c, tx))
            {
                s.Parameters.AddWithValue("@id", templateId);
                code = Convert.ToString(await s.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture) ?? string.Empty;
            }
            await using (var del = new SqliteCommand("DELETE FROM user_permission_templates WHERE template_id = @id", c, tx))
            {
                del.Parameters.AddWithValue("@id", templateId);
                await del.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await using (var upd = new SqliteCommand(
                "UPDATE permission_templates SET is_deleted = 1, updated_at = CURRENT_TIMESTAMP WHERE template_id = @id", c, tx))
            {
                upd.Parameters.AddWithValue("@id", templateId);
                await upd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await AuditSqliteAsync(c, tx, companyId, "PERM_TEMPLATE_DELETED", $"template={code}", ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        catch (AppError) { throw; }
        catch (UnauthorizedAccessException) { throw; }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.PermissionTemplateSaveFailed, ex, "PermTplSvc.Delete.Sqlite");
            throw;
        }
    }

    // ------------------------------------------------------------------
    // PostgreSQL implementations
    // ------------------------------------------------------------------

    private static async Task<Guid> ResolveCompanyPostgresAsync(NpgsqlConnection c, NpgsqlTransaction? tx, CancellationToken ct)
    {
        var code = AppState.CurrentCompanyCode;
        if (string.IsNullOrWhiteSpace(code))
            throw new InvalidOperationException("Permission templates require an open company.");
        await using var cmd = tx is null
            ? new NpgsqlCommand("SELECT company_id FROM public.companies WHERE company_code = @code AND is_deleted = FALSE LIMIT 1", c)
            : new NpgsqlCommand("SELECT company_id FROM public.companies WHERE company_code = @code AND is_deleted = FALSE LIMIT 1", c, tx);
        cmd.Parameters.AddWithValue("@code", code.Trim());
        var raw = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (raw is null or DBNull)
            throw new InvalidOperationException($"Company '{code}' was not found.");
        return (Guid)raw;
    }

    private static async Task AuditPostgresAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid companyId, string eventType, string details, CancellationToken ct)
    {
        var actor = ActorUsername();
        Guid? actorId = null;
        await using (var a = new NpgsqlCommand("SELECT user_id FROM public.users WHERE username = @u AND company_id = @cid LIMIT 1", c, tx))
        {
            a.Parameters.AddWithValue("@u", actor);
            a.Parameters.Add("@cid", NpgsqlDbType.Uuid).Value = companyId;
            var raw = await a.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (raw is not null and not DBNull) actorId = (Guid)raw;
        }
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO public.security_audit_log(company_id, user_id, username, event_type, details) VALUES(@cid, @uid, @user, @event, @details)", c, tx);
        cmd.Parameters.Add("@cid", NpgsqlDbType.Uuid).Value = companyId;
        cmd.Parameters.Add("@uid", NpgsqlDbType.Uuid).Value = (object?)actorId ?? DBNull.Value;
        cmd.Parameters.AddWithValue("@user", actor);
        cmd.Parameters.AddWithValue("@event", eventType);
        cmd.Parameters.AddWithValue("@details", details);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<TemplateRow>> ListTemplatesPostgresAsync(string cs, CancellationToken ct)
    {
        try
        {
            await using var c = new NpgsqlConnection(cs);
            await c.OpenAsync(ct).ConfigureAwait(false);
            var companyId = await ResolveCompanyPostgresAsync(c, null, ct).ConfigureAwait(false);
            const string sql = """
                SELECT t.template_id, t.template_code, t.name_en, t.name_ar, t.description, t.is_system, t.is_active,
                       (SELECT COUNT(*) FROM public.permission_template_grants g WHERE g.template_id = t.template_id),
                       (SELECT COUNT(*) FROM public.user_permission_templates a WHERE a.template_id = t.template_id)
                FROM public.permission_templates t
                WHERE t.is_deleted = FALSE AND (t.company_id IS NULL OR t.company_id = @cid)
                ORDER BY t.is_system DESC, t.template_code
                """;
            await using var cmd = new NpgsqlCommand(sql, c);
            cmd.Parameters.Add("@cid", NpgsqlDbType.Uuid).Value = companyId;
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var result = new List<TemplateRow>();
            while (await r.ReadAsync(ct).ConfigureAwait(false))
                result.Add(new TemplateRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4), r.GetBoolean(5), r.GetBoolean(6),
                    Convert.ToInt32(r.GetInt64(7)), Convert.ToInt32(r.GetInt64(8))));
            return result;
        }
        catch (AppError) { throw; }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.PermissionTemplateLoadFailed, ex, "PermTplSvc.ListTemplates.Postgres");
            throw;
        }
    }

    private static async Task<IReadOnlyList<CatalogItem>> LoadCatalogPostgresAsync(string cs, CancellationToken ct)
    {
        try
        {
            await using var c = new NpgsqlConnection(cs);
            await c.OpenAsync(ct).ConfigureAwait(false);
            const string sql = """
                SELECT permission_id, permission_code, group_code, feature_code, operation_code, name_ar, name_en, is_sensitive
                FROM public.permissions WHERE is_active = TRUE ORDER BY display_order, permission_code
                """;
            await using var cmd = new NpgsqlCommand(sql, c);
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var result = new List<CatalogItem>();
            while (await r.ReadAsync(ct).ConfigureAwait(false))
                result.Add(new CatalogItem(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
                    r.GetString(5), r.GetString(6), r.GetBoolean(7)));
            return result;
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.PermissionTemplateLoadFailed, ex, "PermTplSvc.LoadCatalog.Postgres");
            throw;
        }
    }

    private static async Task<IReadOnlyList<UserRow>> ListUsersPostgresAsync(string cs, CancellationToken ct)
    {
        try
        {
            await using var c = new NpgsqlConnection(cs);
            await c.OpenAsync(ct).ConfigureAwait(false);
            var companyId = await ResolveCompanyPostgresAsync(c, null, ct).ConfigureAwait(false);
            const string sql = """
                SELECT user_id, username, full_name, is_active FROM public.users
                WHERE company_id = @cid AND is_deleted = FALSE ORDER BY username
                """;
            await using var cmd = new NpgsqlCommand(sql, c);
            cmd.Parameters.Add("@cid", NpgsqlDbType.Uuid).Value = companyId;
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var result = new List<UserRow>();
            while (await r.ReadAsync(ct).ConfigureAwait(false))
                result.Add(new UserRow(r.GetGuid(0).ToString(), r.GetString(1), r.GetString(2), r.GetBoolean(3)));
            return result;
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.PermissionTemplateLoadFailed, ex, "PermTplSvc.ListUsers.Postgres");
            throw;
        }
    }

    private static async Task<(Guid? CompanyId, bool IsSystem)> LoadTemplateHeaderPostgresAsync(
        NpgsqlConnection c, NpgsqlTransaction? tx, long templateId, Guid companyId, CancellationToken ct)
    {
        await using var cmd = tx is null
            ? new NpgsqlCommand("""
                SELECT company_id, is_system FROM public.permission_templates
                WHERE template_id = @id AND is_deleted = FALSE
                  AND (company_id IS NULL OR company_id = @cid)
                LIMIT 1
                """, c)
            : new NpgsqlCommand("""
                SELECT company_id, is_system FROM public.permission_templates
                WHERE template_id = @id AND is_deleted = FALSE
                  AND (company_id IS NULL OR company_id = @cid)
                LIMIT 1
                """, c, tx);
        cmd.Parameters.AddWithValue("@id", templateId);
        cmd.Parameters.Add("@cid", NpgsqlDbType.Uuid).Value = companyId;
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false)) throw NotFound();
        return (r.IsDBNull(0) ? null : r.GetGuid(0), r.GetBoolean(1));
    }

    private static async Task<(IReadOnlyList<long> Grants, IReadOnlyList<string> UserIds)> LoadDetailPostgresAsync(string cs, long templateId, CancellationToken ct)
    {
        try
        {
            await using var c = new NpgsqlConnection(cs);
            await c.OpenAsync(ct).ConfigureAwait(false);
            var companyId = await ResolveCompanyPostgresAsync(c, null, ct).ConfigureAwait(false);
            await LoadTemplateHeaderPostgresAsync(c, null, templateId, companyId, ct).ConfigureAwait(false);
            var grants = new List<long>();
            await using (var g = new NpgsqlCommand("SELECT permission_id FROM public.permission_template_grants WHERE template_id = @id ORDER BY permission_id", c))
            {
                g.Parameters.AddWithValue("@id", templateId);
                await using var r = await g.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await r.ReadAsync(ct).ConfigureAwait(false)) grants.Add(r.GetInt64(0));
            }
            var users = new List<string>();
            await using (var u = new NpgsqlCommand("SELECT user_id FROM public.user_permission_templates WHERE template_id = @id ORDER BY user_id", c))
            {
                u.Parameters.AddWithValue("@id", templateId);
                await using var r = await u.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await r.ReadAsync(ct).ConfigureAwait(false)) users.Add(r.GetGuid(0).ToString());
            }
            return (grants, users);
        }
        catch (AppError) { throw; }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.PermissionTemplateLoadFailed, ex, "PermTplSvc.LoadDetail.Postgres");
            throw;
        }
    }

    private static async Task<long> SavePostgresAsync(string cs, long templateId, string code, string nameEn, string nameAr,
        string? description, bool isActive, IReadOnlyCollection<long> grantIds, IReadOnlyCollection<string> userIds, CancellationToken ct)
    {
        try
        {
            await using var c = new NpgsqlConnection(cs);
            await c.OpenAsync(ct).ConfigureAwait(false);
            await using var tx = await c.BeginTransactionAsync(ct).ConfigureAwait(false);
            var companyId = await ResolveCompanyPostgresAsync(c, tx, ct).ConfigureAwait(false);
            var actor = ActorUsername();

            if (templateId > 0)
            {
                var header = await LoadTemplateHeaderPostgresAsync(c, tx, templateId, companyId, ct).ConfigureAwait(false);
                if (header.IsSystem) throw SystemImmutable();
            }

            await using (var dup = new NpgsqlCommand("""
                SELECT COUNT(*) FROM public.permission_templates
                WHERE is_deleted = FALSE AND template_code = @code AND template_id <> @id
                  AND (company_id IS NULL OR company_id = @cid)
                """, c, tx))
            {
                dup.Parameters.AddWithValue("@code", code);
                dup.Parameters.AddWithValue("@id", templateId);
                dup.Parameters.Add("@cid", NpgsqlDbType.Uuid).Value = companyId;
                if (Convert.ToInt64(await dup.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture) > 0)
                    throw DuplicateCode();
            }

            var validGrants = new List<long>();
            if (grantIds.Count > 0)
            {
                var active = new HashSet<long>();
                await using var all = new NpgsqlCommand("SELECT permission_id FROM public.permissions WHERE is_active = TRUE", c, tx);
                await using var r = await all.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await r.ReadAsync(ct).ConfigureAwait(false)) active.Add(r.GetInt64(0));
                foreach (var pid in grantIds.Distinct()) if (active.Contains(pid)) validGrants.Add(pid);
            }

            var canonicalUsers = new List<Guid>();
            foreach (var candidate in userIds.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                await using var u = new NpgsqlCommand(
                    "SELECT user_id FROM public.users WHERE company_id = @cid AND is_deleted = FALSE AND (user_id::text = @u OR username = @u) LIMIT 1", c, tx);
                u.Parameters.Add("@cid", NpgsqlDbType.Uuid).Value = companyId;
                u.Parameters.AddWithValue("@u", candidate);
                var raw = await u.ExecuteScalarAsync(ct).ConfigureAwait(false);
                if (raw is null or DBNull) throw CompanyMismatch();
                canonicalUsers.Add((Guid)raw);
            }

            long id = templateId;
            if (id == 0)
            {
                await using var ins = new NpgsqlCommand("""
                    INSERT INTO public.permission_templates(company_id, template_code, name_ar, name_en, description, is_system, is_active, created_by)
                    VALUES(@cid, @code, @nameAr, @nameEn, @desc, FALSE, @active, @actor)
                    RETURNING template_id
                    """, c, tx);
                ins.Parameters.Add("@cid", NpgsqlDbType.Uuid).Value = companyId;
                ins.Parameters.AddWithValue("@code", code);
                ins.Parameters.AddWithValue("@nameAr", nameAr);
                ins.Parameters.AddWithValue("@nameEn", nameEn);
                ins.Parameters.AddWithValue("@desc", (object?)description ?? DBNull.Value);
                ins.Parameters.AddWithValue("@active", isActive);
                ins.Parameters.AddWithValue("@actor", actor);
                id = Convert.ToInt64(await ins.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
            }
            else
            {
                await using var upd = new NpgsqlCommand("""
                    UPDATE public.permission_templates
                    SET template_code = @code, name_ar = @nameAr, name_en = @nameEn, description = @desc,
                        is_active = @active, updated_at = now()
                    WHERE template_id = @id
                    """, c, tx);
                upd.Parameters.AddWithValue("@code", code);
                upd.Parameters.AddWithValue("@nameAr", nameAr);
                upd.Parameters.AddWithValue("@nameEn", nameEn);
                upd.Parameters.AddWithValue("@desc", (object?)description ?? DBNull.Value);
                upd.Parameters.AddWithValue("@active", isActive);
                upd.Parameters.AddWithValue("@id", id);
                await upd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await using (var del = new NpgsqlCommand("DELETE FROM public.permission_template_grants WHERE template_id = @id", c, tx))
            {
                del.Parameters.AddWithValue("@id", id);
                await del.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            foreach (var pid in validGrants)
            {
                await using var g = new NpgsqlCommand("INSERT INTO public.permission_template_grants(template_id, permission_id) VALUES(@id, @pid)", c, tx);
                g.Parameters.AddWithValue("@id", id);
                g.Parameters.AddWithValue("@pid", pid);
                await g.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await using (var del = new NpgsqlCommand("DELETE FROM public.user_permission_templates WHERE template_id = @id", c, tx))
            {
                del.Parameters.AddWithValue("@id", id);
                await del.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            foreach (var uid in canonicalUsers)
            {
                await using var a = new NpgsqlCommand("INSERT INTO public.user_permission_templates(user_id, template_id, assigned_by) VALUES(@uid, @id, @actor)", c, tx);
                a.Parameters.Add("@uid", NpgsqlDbType.Uuid).Value = uid;
                a.Parameters.AddWithValue("@id", id);
                a.Parameters.AddWithValue("@actor", actor);
                await a.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await AuditPostgresAsync(c, tx, companyId,
                templateId == 0 ? "PERM_TEMPLATE_CREATED" : "PERM_TEMPLATE_UPDATED",
                $"template={code}; grants={validGrants.Count}; users={canonicalUsers.Count}", ct).ConfigureAwait(false);
            if (canonicalUsers.Count > 0)
                await AuditPostgresAsync(c, tx, companyId, "PERM_TEMPLATE_ASSIGNED",
                    $"template={code}; users={string.Join(",", canonicalUsers)}", ct).ConfigureAwait(false);

            await tx.CommitAsync(ct).ConfigureAwait(false);
            return id;
        }
        catch (AppError) { throw; }
        catch (UnauthorizedAccessException) { throw; }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.PermissionTemplateSaveFailed, ex, "PermTplSvc.Save.Postgres");
            throw;
        }
    }

    private static async Task<long> CopyPostgresAsync(string cs, long sourceTemplateId, string newCode, CancellationToken ct)
    {
        try
        {
            await using var c = new NpgsqlConnection(cs);
            await c.OpenAsync(ct).ConfigureAwait(false);
            await using var tx = await c.BeginTransactionAsync(ct).ConfigureAwait(false);
            var companyId = await ResolveCompanyPostgresAsync(c, tx, ct).ConfigureAwait(false);
            await LoadTemplateHeaderPostgresAsync(c, tx, sourceTemplateId, companyId, ct).ConfigureAwait(false);

            string nameAr, nameEn; string? description; bool active; string sourceCode;
            await using (var s = new NpgsqlCommand("SELECT template_code, name_ar, name_en, description, is_active FROM public.permission_templates WHERE template_id = @id", c, tx))
            {
                s.Parameters.AddWithValue("@id", sourceTemplateId);
                await using var r = await s.ExecuteReaderAsync(ct).ConfigureAwait(false);
                await r.ReadAsync(ct).ConfigureAwait(false);
                sourceCode = r.GetString(0); nameAr = r.GetString(1); nameEn = r.GetString(2);
                description = r.IsDBNull(3) ? null : r.GetString(3); active = r.GetBoolean(4);
            }

            await using (var dup = new NpgsqlCommand("""
                SELECT COUNT(*) FROM public.permission_templates
                WHERE is_deleted = FALSE AND template_code = @code
                  AND (company_id IS NULL OR company_id = @cid)
                """, c, tx))
            {
                dup.Parameters.AddWithValue("@code", newCode);
                dup.Parameters.Add("@cid", NpgsqlDbType.Uuid).Value = companyId;
                if (Convert.ToInt64(await dup.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture) > 0)
                    throw DuplicateCode();
            }

            long id;
            await using (var ins = new NpgsqlCommand("""
                INSERT INTO public.permission_templates(company_id, template_code, name_ar, name_en, description, is_system, is_active, created_by)
                VALUES(@cid, @code, @nameAr, @nameEn, @desc, FALSE, @active, @actor)
                RETURNING template_id
                """, c, tx))
            {
                ins.Parameters.Add("@cid", NpgsqlDbType.Uuid).Value = companyId;
                ins.Parameters.AddWithValue("@code", newCode);
                ins.Parameters.AddWithValue("@nameAr", nameAr);
                ins.Parameters.AddWithValue("@nameEn", nameEn);
                ins.Parameters.AddWithValue("@desc", (object?)description ?? DBNull.Value);
                ins.Parameters.AddWithValue("@active", active);
                ins.Parameters.AddWithValue("@actor", ActorUsername());
                id = Convert.ToInt64(await ins.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
            }

            await using (var cp = new NpgsqlCommand(
                "INSERT INTO public.permission_template_grants(template_id, permission_id) SELECT @newId, permission_id FROM public.permission_template_grants WHERE template_id = @srcId", c, tx))
            {
                cp.Parameters.AddWithValue("@newId", id);
                cp.Parameters.AddWithValue("@srcId", sourceTemplateId);
                await cp.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await AuditPostgresAsync(c, tx, companyId, "PERM_TEMPLATE_COPIED",
                $"template={newCode}; from={sourceCode}", ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return id;
        }
        catch (AppError) { throw; }
        catch (UnauthorizedAccessException) { throw; }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.PermissionTemplateSaveFailed, ex, "PermTplSvc.Copy.Postgres");
            throw;
        }
    }

    private static async Task DeletePostgresAsync(string cs, long templateId, CancellationToken ct)
    {
        try
        {
            await using var c = new NpgsqlConnection(cs);
            await c.OpenAsync(ct).ConfigureAwait(false);
            await using var tx = await c.BeginTransactionAsync(ct).ConfigureAwait(false);
            var companyId = await ResolveCompanyPostgresAsync(c, tx, ct).ConfigureAwait(false);
            var header = await LoadTemplateHeaderPostgresAsync(c, tx, templateId, companyId, ct).ConfigureAwait(false);
            if (header.IsSystem || header.CompanyId is null) throw SystemImmutable();

            string code;
            await using (var s = new NpgsqlCommand("SELECT template_code FROM public.permission_templates WHERE template_id = @id", c, tx))
            {
                s.Parameters.AddWithValue("@id", templateId);
                code = Convert.ToString(await s.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture) ?? string.Empty;
            }
            await using (var del = new NpgsqlCommand("DELETE FROM public.user_permission_templates WHERE template_id = @id", c, tx))
            {
                del.Parameters.AddWithValue("@id", templateId);
                await del.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await using (var upd = new NpgsqlCommand(
                "UPDATE public.permission_templates SET is_deleted = TRUE, updated_at = now() WHERE template_id = @id", c, tx))
            {
                upd.Parameters.AddWithValue("@id", templateId);
                await upd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await AuditPostgresAsync(c, tx, companyId, "PERM_TEMPLATE_DELETED", $"template={code}", ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        catch (AppError) { throw; }
        catch (UnauthorizedAccessException) { throw; }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.PermissionTemplateSaveFailed, ex, "PermTplSvc.Delete.Postgres");
            throw;
        }
    }
}
}
