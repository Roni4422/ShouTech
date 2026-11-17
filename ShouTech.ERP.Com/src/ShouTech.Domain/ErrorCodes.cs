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

    // ---- ACC 5000-5999 -------------------------------------------------
    public const string JournalNotBalanced = "ACC-5001";
    public const string JournalTooFewLines = "ACC-5002";
    public const string JournalZeroAmount = "ACC-5003";
    public const string JournalPostingFailed = "ACC-5004";
    public const string JournalEntryNotEditable = "ACC-5005";
    public const string CreditLimitExceeded = "ACC-5006";
    public const string CostCenterAllocationInvalid = "ACC-5007";
    public const string CostCenterNotLeaf = "ACC-5008";

    // ---- INV 6000-6999 -------------------------------------------------
    public const string InventoryOperationFailed = "INV-6001";

    // ---- UI 7000-7999 --------------------------------------------------
    public const string WindowOperationFailed = "UI-7001";
    public const string ShellOperationFailed = "UI-7002";
    public const string WindowLoadFailed = "UI-7003";
    public const string NavigationFailed = "UI-7004";

    // ---- LIC 8000-8999 -------------------------------------------------
    public const string LicenseCheckFailed = "LIC-8001";
}
