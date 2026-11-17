namespace ShouTech.Domain;

/// <summary>
/// The single source of truth for error-catalog code strings. The matching
/// names/messages/hints live in the per-language catalog files
/// (cod/app/src/Resources/Errors/{En,Ar,Fr,Tr}.json) and are indexed in
/// docs/ERROR_CATALOG.md.
///
/// Numbering scheme (domain prefix + 4 digits):
///   SYS  0001-0999  system / unhandled / configuration
///   DB   1000-1999  database, migration, company database files
///   AUTH 2000-2999  sign-in, lockout, password policy
///   CO   3000-3999  company open / create / switch
///   DATA 4000-4999  data export / import
///   ACC  5000-5999  accounting: journal, posting, reconciliation
///   INV  6000-6999  inventory / orders
///   UI   7000-7999  windows / shell
///   LIC  8000-8999  licensing
///
/// RULE: codes are immutable once assigned. A retired code is never
/// reused; a changed meaning gets a new code.
/// </summary>
public static class ErrorCodes
{
    // ---- SYS 0001-0999 -------------------------------------------------
    public const string Unknown = "SYS-0001";
    public const string DispatcherUnhandled = "SYS-0002";
    public const string AppDomainUnhandled = "SYS-0003";
    public const string UnobservedTaskException = "SYS-0004";
    public const string NonFatalInternalFailure = "SYS-0005";
    public const string ConfigurationLoadFailed = "SYS-0006";
    public const string FileAccessFailed = "SYS-0007";
    public const string StartupFailed = "SYS-0009";
    public const string SettingsLoadFailed = "SYS-0010";
    public const string SettingsSaveFailed = "SYS-0011";

    // ---- DB 1000-1999 --------------------------------------------------
    public const string DatabaseConnectionFailed = "DB-1001";
    public const string DatabaseFileMissing = "DB-1002";
    public const string MigrationFailed = "DB-1003";
    public const string DatabaseNotInitialized = "DB-1004";
    public const string DatabaseQueryFailed = "DB-1005";
    public const string UnsupportedDatabaseProvider = "DB-1006";

    // ---- AUTH 2000-2999 ------------------------------------------------
    public const string InvalidCredentials = "AUTH-2001";
    public const string AccountLocked = "AUTH-2002";
    public const string AccountLockedNow = "AUTH-2003";
    public const string PasswordPolicyViolation = "AUTH-2004";
    public const string PermissionDenied = "AUTH-2005";
    public const string PermissionLoadFailed = "AUTH-2006";

    // ---- CO 3000-3999 --------------------------------------------------
    public const string CompanyRecordNotFound = "CO-3001";
    public const string CompanyDatabasePathMissing = "CO-3002";
    public const string PostgresDatabaseNameMissing = "CO-3003";
    public const string PostgresConnectionMissing = "CO-3004";
    public const string CompanyLoadFailed = "CO-3005";
    public const string CompanyOpenFailed = "CO-3006";
    public const string CompanyCoaTemplateNotFound = "CO-3007";

    // ---- DATA 4000-4999 ------------------------------------------------
    public const string ExportPermissionDenied = "DATA-4001";
    public const string ExportFailed = "DATA-4002";
    public const string ImportFailed = "DATA-4003";
    public const string ImportPermissionDenied = "DATA-4004";
    public const string BalanceImportNotAllowed = "DATA-4005";
    public const string DocumentImportRequiresPackage = "DATA-4006";

    // ---- ACC 5000-5999 -------------------------------------------------
    public const string JournalNotBalanced = "ACC-5001";
    public const string JournalTooFewLines = "ACC-5002";
    public const string JournalZeroAmount = "ACC-5003";
    public const string JournalPostingFailed = "ACC-5004";
    public const string JournalEntryNotEditable = "ACC-5005";
    public const string CreditLimitExceeded = "ACC-5006";
    public const string CostCenterAllocationInvalid = "ACC-5007";
    public const string CostCenterNotLeaf = "ACC-5008";
    public const string CostCenterUnknown = "ACC-5009";
    public const string CostCenterRequired = "ACC-5010";
    public const string DocumentPostingAccountMissing = "ACC-5011";
    public const string DocumentAlreadyPostedToGl = "ACC-5012";
    public const string DocumentTotalMismatch = "ACC-5013";
    public const string DocumentKindNotPostable = "ACC-5014";
    public const string ExchangeRateMissing = "ACC-5015";
    public const string BankStatementNotReconciled = "ACC-5016";
    public const string StatementLineAlreadyMatched = "ACC-5017";
    // Phase B 055 (Budgets): the design proposed ACC-5009/5010 for the
    // budget guard, but Fix Phase Step 3 consumed those two codes for
    // cost-center validation (CostCenterUnknown / CostCenterRequired),
    // so the guard takes the next free ACC codes instead.
    public const string BudgetExceededAnnual = "ACC-5018";
    public const string BudgetExceededMonthly = "ACC-5019";

    // ---- INV 6000-6999 -------------------------------------------------
    public const string InventoryOperationFailed = "INV-6001";
    public const string StockValuationFailed = "INV-6002";
    public const string RecalculationFailed = "INV-6003";
    public const string RecalculationClosedPeriod = "INV-6004";
    public const string BatchSerialRequired = "INV-6005";
    public const string BatchStockInsufficient = "INV-6006";
    public const string SerialUnavailable = "INV-6007";
    public const string SerialDuplicate = "INV-6008";
    public const string BatchAllocationMismatch = "INV-6009";
    public const string BatchExpired = "INV-6010";
    // Phase B 056 (Quality): the QC gate and inspection lifecycle take
    // the next free INV codes. INV is the design's family for the gate
    // ("coded error (INV range)").
    public const string QualityGateBlocked = "INV-6011";
    public const string QualityQuarantinedSale = "INV-6012";
    public const string QualityOutgoingInspectionRequired = "INV-6013";
    public const string QualityInspectionStateInvalid = "INV-6014";
    public const string QualityInspectionQtyMismatch = "INV-6015";
    public const string QualityTemplateInvalid = "INV-6016";

    // ---- UI 7000-7999 --------------------------------------------------
    public const string WindowOperationFailed = "UI-7001";
    public const string ShellOperationFailed = "UI-7002";
    public const string WindowLoadFailed = "UI-7003";
    public const string NavigationFailed = "UI-7004";

    // ---- LIC 8000-8999 -------------------------------------------------
    public const string LicenseCheckFailed = "LIC-8001";
}
