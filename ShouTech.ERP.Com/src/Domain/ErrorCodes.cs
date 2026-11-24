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
///   CRM  9000-9999  customer relationship management
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
    public const string PartyBlacklisted = "ACC-5020";

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

    // ---- CRM 9000-9999 -------------------------------------------------
    // Phase B 057 (CRM): conversion / pipeline / activity state
    // violations. The design asked for "a small CRM error range
    // registered in ErrorCodes.cs (precedent: LIC range)"; CRM takes
    // the next free prefix block, 9000-9999.
    public const string CrmLeadAlreadyConverted = "CRM-9001";
    public const string CrmLeadStateInvalid = "CRM-9002";
    public const string CrmOpportunityStageInvalid = "CRM-9003";
    public const string CrmQuotationPartyMissing = "CRM-9004";
    public const string CrmActivityTargetInvalid = "CRM-9005";
    public const string CrmLostReasonRequired = "CRM-9006";
    // Sales-cycle upgrade (feat/sales-cycle-erpnext): duplicate detection
    // and opportunity-item validation.
    public const string CrmDuplicateLeadOrParty = "CRM-9007";
    public const string CrmOpportunityItemInvalid = "CRM-9008";

    // ---- MFG 1001-1999 (manufacturing build-out, migration 058) --------
    // The manufacturing module takes its own prefix block. Codes are
    // immutable once assigned, like every other range above.
    public const string BomCycleDetected = "MFG-1001";
    public const string BomNotFoundOrInactive = "MFG-1002";
    public const string MfgOrderStageInvalid = "MFG-1003";
    public const string MfgIssueExceedsRequired = "MFG-1004";
    public const string MfgFinishExceedsPlanned = "MFG-1005";
    public const string PlanningConversionInvalid = "MFG-1006";

    // ---- HR 1001-1999 (HR/Payroll build, migration 065) ----------------
    // The HR module takes its own prefix block, like MFG. (The legacy
    // catalog promises "Leave Balance Insufficient" etc. predate the
    // module; HR-1003 is that promise kept under the module's codes.)
    public const string HrEmployeeCodeDuplicate = "HR-1001";
    public const string HrEmployeeInvalid = "HR-1002";
    public const string HrLeaveBalanceInsufficient = "HR-1003";
    public const string HrLeaveStateInvalid = "HR-1004";
    public const string HrPayrollAlreadyPosted = "HR-1005";
    public const string HrPayrollStateInvalid = "HR-1006";
    public const string HrSalaryStructureInvalid = "HR-1007";
    public const string HrMasterInUse = "HR-1008";
    public const string HrEmployeeNotPayable = "HR-1009";
    public const string HrMasterCodeDuplicate = "HR-1010";

    // ---- PUR 1001-1999 (purchase requisition completion, migration 107) --
    // The purchasing workflow module takes its own prefix block, like
    // MFG / HR / POS. Budget overruns at requisition submit reuse the
    // BudgetGuard codes ACC-5018/5019 (identical Stop semantics).
    public const string RequisitionNotEditable = "PUR-1001";
    public const string RequisitionCancelBlocked = "PUR-1002";
    public const string RequisitionCloseInvalid = "PUR-1003";
    public const string RequisitionStateInvalid = "PUR-1004";
    public const string RequisitionReasonRequired = "PUR-1005";
    // Landed-cost completion (migration 108). Renumbered 1006-1010 at
    // integration: 1001-1005 were already taken by the requisition codes.
    public const string LandedCostAlreadyBookedNeedsAccount = "PUR-1006";
    public const string LandedCostPayeeNotFound = "PUR-1007";
    public const string LandedCostReceiptSelectionInvalid = "PUR-1008";
    public const string LandedCostDocumentAlreadyLinked = "PUR-1009";
    public const string LandedCostLinkedDocumentInvalid = "PUR-1010";
    // B1 chain completion (migration 111): the supplier bill reference is
    // unique per (company, supplier, normalized reference) on BOTH the
    // chain and the manual invoice paths.
    public const string SupplierBillReferenceDuplicate = "PUR-1011";
    // Supplier advances (migration 110).
    public const string AdvancePurchaseOrderMismatch = "PUR-1012";
    public const string AdvanceReversalBlockedWhileConsumed = "PUR-1013";
    // Withholding tax / TDS (migration 114, W1).
    public const string WithholdingConfigurationInvalid = "PUR-1014";
    public const string WithholdingExceedsPayable = "PUR-1015";
    public const string WithholdingRuleNotFound = "PUR-1016";

    // ---- POS 1001-1999 (POS full build, migration 066) ------------------
    // The POS module takes its own prefix block, like MFG / HR.
    public const string PosNoOpenSession = "POS-1001";
    public const string PosSessionAlreadyOpen = "POS-1002";
    public const string PosSessionStateInvalid = "POS-1003";
    public const string PosTenderMismatch = "POS-1004";
    public const string PosTenderInvalid = "POS-1005";
    public const string PosReturnExceedsSold = "POS-1006";
    public const string PosReturnOriginalInvalid = "POS-1007";
    public const string PosHeldBillNotFound = "POS-1008";
    public const string PosHeldBillStateInvalid = "POS-1009";
    public const string PosSessionlessNotAllowed = "POS-1010";

    // ---- SAL 7001-7999 (sales cycle upgrade, feat/sales-cycle-erpnext) --
    // Chain conversion / progress / reversal violations for the
    // QUO -> SO -> DLV -> SAL document chain and payment allocation.
    public const string ChainSourceNotPosted = "SAL-7001";
    public const string ChainQuantityExceedsRemaining = "SAL-7002";
    public const string ChainSourceFullyFulfilled = "SAL-7003";
    public const string ChainTransitionNotAllowed = "SAL-7004";
    public const string ChainReversalBlockedDownstream = "SAL-7005";
    public const string ChainReversalBlockedPayments = "SAL-7006";
    public const string ChainDocumentNotReversible = "SAL-7007";
    public const string QuotationExpired = "SAL-7008";
    public const string DeliveryCostAllocationFailed = "SAL-7009";
    public const string AllocationExceedsOpenAmount = "SAL-7010";
    // Phase 2 (owner sales spec): reservation / picking / commissions /
    // contracts / approvals.
    public const string StockReservationInvalid = "SAL-7011";
    public const string PickingExceedsRemaining = "SAL-7012";
    public const string CommissionPolicyInvalid = "SAL-7013";
    public const string CommissionSettlementInvalid = "SAL-7014";
    public const string ContractQuantityExceeded = "SAL-7015";
    public const string ApprovalStateInvalid = "SAL-7016";
    public const string DocumentRevisionConflict = "SAL-7017";
    public const string PriceBelowMinimum = "SAL-7018";
    public const string MarginBelowMinimum = "SAL-7019";
    public const string DiscountExceedsLimit = "SAL-7020";
    public const string ReturnQuantityExceeded = "SAL-7021";
    public const string ReturnInspectionRequired = "SAL-7022";
    public const string ContractInactive = "SAL-7023";
    public const string RecurringScheduleInvalid = "SAL-7024";
    public const string RmaStateInvalid = "SAL-7025";
    public const string WarrantyClaimInvalid = "SAL-7026";
    public const string SettlementDiscountInvalid = "SAL-7027";
    public const string CrossCurrencyAllocationInvalid = "SAL-7028";
    public const string ConsolidationMismatch = "SAL-7029";
}
