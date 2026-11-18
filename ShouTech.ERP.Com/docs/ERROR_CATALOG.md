# ShouTech ERP — Error Catalog (the index)

Every error in ShouTech has a stable **code**. The catalog is the index that
maps a code to the error's name and its localized message. At runtime the code
is looked up in the catalog and the right error — in the user's current
language — is shown and logged. Support and maintenance work from the code:
any error dialog and any error log line carries it, and this document is the
human-readable index behind it.

- Catalog files: `cod/app/src/Resources/Errors/{En,Ar,Fr,Tr}.json`
  (separate from the Lang UI catalogs on purpose, to avoid merge conflicts).
- Service: `ErrorCatalogSvc` (`cod/app/src/Services/ErrorCatalog.cs`),
  exposed as `AppHost.Errors`.
- Exception type: `AppError` (`src/ShouTech.Domain/AppError.cs`) with the
  code constants in `src/ShouTech.Domain/ErrorCodes.cs`.
- A legacy, unrelated in-app helper also exists
  (`Localization/ErrorCatalog.cs` + `Resources/Errors/Errors.json`, with
  `SYS-1xxx`-style codes used by a few windows for grid validation). It is
  **not** this catalog and its code space does not overlap; it is left
  untouched.

## 1. Numbering scheme

Codes are `DOMAIN-NNNN`: a domain prefix plus a zero-padded 4-digit number
from a reserved range per domain.

| Prefix | Range | Domain |
|---|---|---|
| SYS | 0001–0999 | System / unhandled / configuration / files |
| DB | 1000–1999 | Database, connection, migration |
| AUTH | 2000–2999 | Sign-in, lockout, password policy |
| CO | 3000–3999 | Company open / create / switch |
| DATA | 4000–4999 | Export / import (DatWin) |
| ACC | 5000–5999 | Accounting: journals, posting, reconciliation |
| INV | 6000–6999 | Inventory / orders |
| UI | 7000–7999 | Windows, shell, navigation |
| LIC | 8000–8999 | Licensing |

### Rules

1. **Codes are immutable once assigned.** A code's meaning never changes and
   a retired code is never reused. Log files and support history refer to
   codes forever, so renumbering would corrupt that history. To change an
   error's wording, edit its catalog message; to change its meaning, assign
   a new code.
2. **English is the base layer.** Every code must exist in `En.json` with a
   `name` and a `message`. Other languages overlay per entry; a missing
   translation falls back to English for that entry (same pattern as the
   Lang catalogs). Seeded entries are translated in all four languages.
3. **Arabic text lives only in `Ar.json`** (the standing English-only source
   rule; the error catalogs follow the same exception as the Lang catalogs).
4. Messages may contain `{0}`, `{1}` placeholders, filled from
   `AppError.Args` / `Resolve(code, args)`.

### How to add a new error

1. Pick the next free number in the right domain range (check this index
   and `ErrorCodes.cs`).
2. Add a constant to `src/ShouTech.Domain/ErrorCodes.cs`.
3. Add the entry to all four `Resources/Errors/*.json` files (at minimum
   `En.json`; the entry is incomplete until the other three carry real
   translations).
4. Add a row to the index table in §3 of this document.
5. Use it: `throw new AppError(ErrorCodes.X, message, args)` at a throw
   site, or `AppHost.Errors.Report(ErrorCodes.X, ex, "File.Operation")`
   in a catch block.

## 2. Lookup and fallback behavior

`AppHost.Errors.Resolve(code, args)` returns `ErrorInfo`:

- The entry is read from the English base layer, overlaid with the current
  language's entry when one exists (per-field fallback: a translated entry
  missing `hint` keeps the English hint).
- `message` is formatted with `args` when placeholders are present.
- An **unknown/unregistered code** resolves to the SYS-0001 entry with
  `IsKnown = false` and `RequestedCode` preserved, so the log still records
  exactly which code was requested (see §5). The user sees the generic
  SYS-0001 message — never a raw exception message.
- `AppHost.Errors.Classify(exception)` maps common exception types to
  default codes for handlers that have no better code: `AppError` → its own
  code; `AggregateException` → its first inner exception; SQLite/PostgreSQL/
  `DbException` → DB-1005; `FileNotFoundException`/`DirectoryNotFoundException`/
  `IOException`/`UnauthorizedAccessException` → SYS-0007; `TimeoutException`
  → DB-1001; anything else → SYS-0001.

### Global handlers and the error dialog

`App.xaml.cs` registers three global handlers. Each resolves a code
(AppError code → Classify → handler fallback SYS-0002/SYS-0003/SYS-0004),
logs through the coded `LogError` overload (§5) with the full stack trace,
and — for the dispatcher and AppDomain handlers — shows a dialog built
**only** from the catalog: title `Application Error — <CODE>`, body = the
catalog message in the current language + the hint when one exists +
`(Error <CODE>)`. Raw exception text appears in the log only, never in the
dialog for a known error.

### AppError placement

`AppError` and `ErrorCodes` live in `src/ShouTech.Domain`, which has no
project references, so every library (Application, Infrastructure) and the
app can carry catalog codes without circular dependencies. Library code
throws; the app layer reports (see §6).

## 3. The index — all assigned codes

"When it fires" describes the wired call sites. Codes marked *reserved*
exist in the catalog and index but their call site is owned by another
change and not yet converted.

| Code | Name | Message (En) | When it fires |
|---|---|---|---|
| ACC-5001 | JournalNotBalanced | Journal entry debit and credit totals must balance. | Journal entry is not balanced (debits != credits) — thrown by JournalEntry.EnsureBalanced. |
| ACC-5002 | JournalTooFewLines | A journal entry requires at least two lines. | Journal entry has fewer than two lines — thrown by JournalEntry.EnsureBalanced. |
| ACC-5003 | JournalZeroAmount | A journal entry must have a non-zero amount. | Journal line amount is zero or negative — thrown by JournalEntry.EnsureBalanced. |
| ACC-5004 | JournalPostingFailed | The journal entry could not be posted. | Journal posting failed (catalogued for posting paths; wired incrementally). |
| ACC-5005 | JournalEntryNotEditable | Only a draft journal entry can be edited. Posted and reversed entries are immutable. | JournalService.UpdateDraftAsync was called for a posted or reversed entry — thrown by JournalRepository.UpdateDraftAsync. |
| ACC-5006 | CreditLimitExceeded | Posting this document would exceed the party credit limit. Limit: {0}; current balance: {1}; document total: {2}. | An invoice post (InvoiceWin, chain posting, or order convert-to-invoice) would push the party balance over its configured credit limit — thrown by PartyCreditGuard. Hard block: no SALES.CREDIT.OVERRIDE permission exists in the seeds (PRICE/DISCOUNT overrides do not cover credit), so no override path is honored. |
| ACC-5007 | CostCenterAllocationInvalid | The allocation for cost center '{0}' is invalid: its percentages must total 100 and every target must be an active leaf cost center. | At journal posting, the active allocation of a line's cost center has percentages not totalling 100, or a target that is a group / inactive / deleted center — thrown by JournalRepository posting. |
| ACC-5008 | CostCenterNotLeaf | Cost center '{0}' is a group or inactive cost center and cannot receive postings. | At journal posting, a line is booked directly to a group or inactive cost center that has no allocation — thrown by JournalRepository posting. |
| ACC-5009 | CostCenterUnknown | Cost center '{0}' does not match any active cost center for this company and cannot be posted. | At journal/document posting, a line's cost-center code resolves to no cost_centers master row — thrown by GlPosting.ApplyCostCenters (Step 3, J-3). |
| ACC-5010 | CostCenterRequired | Account '{0}' requires a cost center; the journal line has none. | At journal/document posting, a line booked to an account flagged cost_required carries no cost center — thrown by GlPosting.ApplyCostCenters (Step 3, J-3). |
| ACC-5011 | DocumentPostingAccountMissing | Document posting account '{0}' is missing, inactive, deleted, or not postable for this company. | At document GL posting, a mapped account code does not resolve against chart_of_accounts — thrown by GlPosting.ResolveAccount (Step 3, J-2). |
| ACC-5012 | DocumentAlreadyPostedToGl | Document '{0}' already has a posted general-ledger entry and cannot be posted twice. | At document GL posting, a live journal entry already exists for the same source document — thrown by GlPosting.PostInTransaction (Step 3, J-2). |
| ACC-5013 | DocumentTotalMismatch | Document '{0}' stored total {1} does not match the total recomputed from its lines ({2}); posting uses the stored total. | At invoice posting, the stored grand_total disagrees with lines − header discount + header extra + tax — reported (not thrown) by InvRepo.Post; the stored total wins for GL, party_ledger and the credit guard (Step 5, I-1). |
| ACC-5014 | DocumentKindNotPostable | Document kind '{0}' cannot be posted through the invoice posting path. | At invoice posting, a kind whose correct posting engine is not InvRepo (stock transfer / inventory count via InvoiceWin would fabricate stock, sweep I-6/I-7) is blocked — thrown by InvRepo.Post (Step 5). |
| ACC-5015 | ExchangeRateMissing | No exchange rate is available for currency '{0}': the document carries no rate and no master rate exists on or before {1}. | At journal/document posting, a foreign-currency entry whose header/document rate is absent (<= 0) and for which currency_rates holds no rate on or before the entry date — thrown by FxRates.Resolve (Step 7, J-5). Nothing is written. |
| ACC-5016 | BankStatementNotReconciled | The bank statement cannot be closed while its difference is not zero. | At BankRecWin statement close (OperationsService.CloseBankStatementAsync, Step 8, BNK-01): Difference = closing − opening − matched book total is non-zero, so the statement still disagrees with the books and stays open. |
| ACC-5017 | StatementLineAlreadyMatched | The statement line is already matched to a book line. | At statement-line matching (OperationsService / OperationsRepository, Step 8): the line must be unmatched before it can be matched to a different book line. |
| ACC-5018 | BudgetExceededAnnual | Posting this entry would exceed the annual budget '{0}'. Limit: {1}; actual before this entry: {2}; this entry: {3}; overage: {4}. | At journal/document posting, the entry would push the (account, cost center) actual for the fiscal year past an Active budget line whose annual action is Stop — thrown by BudgetGuard inside the posting transaction (Phase B 055); the whole posting rolls back and the block is recorded in budget_control_events. The Phase B design proposed ACC-5009 for this code, but Step 3 had already consumed ACC-5009/5010 for cost-center validation, so the guard takes the next free ACC codes. No override exists in v1 (decision BG1). |
| ACC-5019 | BudgetExceededMonthly | Posting this entry would exceed the accumulated monthly budget '{0}' through this month. Limit: {1}; actual before this entry: {2}; this entry: {3}; overage: {4}. | Same guard as ACC-5018, for the accumulated-monthly horizon (budget distributed through the entry's month vs actuals through the same month) when the budget's monthly action is Stop — thrown by BudgetGuard (Phase B 055). |
| AUTH-2001 | InvalidCredentials | The username or password is incorrect | Sign-in failed. Internal code for every generic credential failure; the user only ever sees the generic message. |
| AUTH-2002 | AccountLocked | This account is locked. Sign-in was refused. | Sign-in refused because the account is locked (internal code; not shown to the user). |
| AUTH-2003 | AccountLockedNow | The account was locked after repeated failed sign-in attempts. | The sign-in attempt that caused the account to lock (internal code; not shown to the user). |
| AUTH-2004 | PasswordPolicyViolation | Administrator password does not meet the password policy: {0} | Administrator password refused at company creation by the enterprise password policy (CoCreateSvc). |
| AUTH-2005 | PermissionDenied | You do not have permission to perform this operation. | A permission-gated window or destructive action was refused because the signed-in user's loaded permission set lacks the required catalog permission (RBAC gates: PrmWin open, BkpWin create/restore, ComWin permanent delete, BinWin purge, RestoreCoWin restore). Reported via AppHost.Errors at every denial; the user sees the localized denial message. |
| AUTH-2006 | PermissionLoadFailed | The signed-in user's permissions could not be loaded. | Loading the signed-in user's effective permissions into the session failed after a successful sign-in (AuthServ session wiring). Non-administrators continue with an empty permission set (fail closed); administrators fall back to the full catalog so a loader failure can never lock an administrator out. |
| CO-3001 | CompanyRecordNotFound | The local company record required to open this company was not found. | Local company record not found when opening a company (ComWin). |
| CO-3002 | CompanyDatabasePathMissing | Company database path is not set. | Company record has no database path configured (ComWin). |
| CO-3003 | PostgresDatabaseNameMissing | PostgreSQL database name is not set. | PostgreSQL company record has no database name (ComWin). |
| CO-3004 | PostgresConnectionMissing | Current PostgreSQL connection is not available. | No PostgreSQL connection string is available for the company (ComWin). |
| CO-3005 | CompanyLoadFailed | Failed to load companies | Loading the company list failed (ComWin). |
| CO-3006 | CompanyOpenFailed | Unable to open company | Default code for company open/switch failures reported inside ComWin and the company services. |
| CO-3007 | CompanyCoaTemplateNotFound | The selected chart of accounts template could not be found. | Company creation refused because the selected COA template code does not resolve to a loadable .shc template (CoCreateSvc); creation never falls back to a different chart silently. |
| DATA-4001 | ExportPermissionDenied | You do not have permission to export data (DATA.EXPORT). | *Reserved* — DatWin export refused for missing DATA.EXPORT permission. The gate lives in DatWin, which is owned by the parallel fixes branch; conversion lands with that branch. |
| DATA-4002 | ExportFailed | The export could not be completed. | Export failed (DataExportSvc and export paths). |
| DATA-4003 | ImportFailed | The import could not be completed. | Import failed (catalogued for the import paths; wired incrementally). |
| DATA-4004 | ImportPermissionDenied | You do not have permission to import data (DATA.IMPORT). | Step 10 — every data-exchange import (STEI package, dataset package, Excel, .shx) refused by the service-layer gate in DataExchangeGuard when DATA.IMPORT is missing; the denial is also written to the security audit log (DATA_IMPORT_DENIED). |
| DATA-4005 | BalanceImportNotAllowed | Balances and stock quantities cannot be imported from a spreadsheet. They change only through posted stock movements. | Step 10 — Excel import of an engine-owned balance dataset (opening stock / cost layers) refused; balance columns on master datasets are stripped and reported instead of overwriting live balances. |
| DATA-4006 | DocumentImportRequiresPackage | Documents and journal entries cannot be imported from a spreadsheet. Exchange them as an encrypted .stei dataset package. | Step 10 — Excel import of a transactional dataset (sales/purchase documents and lines, stock movements, journal entries and lines, production orders, cheques) refused; the .stei dataset-package path (insert-ignore, never overwrites) remains the exchange route. |
| DB-1001 | DatabaseConnectionFailed | Could not connect to the database. | Database connection could not be opened (also Classify default for timeouts). |
| DB-1002 | DatabaseFileMissing | Company database file not found. | Company database file does not exist (ComWin company-open verification). |
| DB-1003 | MigrationFailed | A database migration failed. | Schema migration failed (SchMig). |
| DB-1004 | DatabaseNotInitialized | Company database file is not properly initialized. | Company database exists but is not initialized (no companies table; ComWin verification). |
| DB-1005 | DatabaseQueryFailed | A database operation failed. | A database query failed (Classify default for DB exceptions; dashboard collectors in DataServ). |
| DB-1006 | UnsupportedDatabaseProvider | Unsupported database provider. | Configured database provider is not supported (ComWin company-open). |
| INV-6001 | InventoryOperationFailed | The inventory operation could not be completed. | Default code for inventory operation failures (Inventory service and repositories). |
| INV-6002 | StockValuationFailed | Stock valuation failed for material '{0}'. | Thrown by StockValuationEngine when a movement cannot be valued (e.g. an issue exceeds the available quantity for a material that does not allow negative stock) (Step 6, Phase B 053). |
| INV-6003 | RecalculationFailed | The Recalculate Costs job failed. | Thrown by StockRecalculationService when a replay fails; the job row in stock_revaluation_jobs is marked Failed with the error (Step 6, Phase B 053). |
| INV-6004 | RecalculationClosedPeriod | Recalculate Costs cannot rewrite movements in a closed fiscal period (from date '{0}'). | Thrown by StockRecalculationService when the replay range touches a closed/locked fiscal period or a closed fiscal year (Step 6, Phase B 053). |
| INV-6005 | BatchSerialRequired | Batch or serial data is required to post material '{0}'. | Thrown by the batch/serial allocator (StockValuationEngine path) when a batch/serial-tracked material is posted without the required batch or serial data (Phase B 054). |
| INV-6006 | BatchStockInsufficient | Batch '{0}' does not hold enough stock for material '{1}'. | Thrown when an issue allocation exceeds the chosen batch's balance at the warehouse, or FEFO/FIFO auto-allocation cannot cover the issue from batch balances (Phase B 054). |
| INV-6007 | SerialUnavailable | Serial '{0}' is not available in stock for material '{1}'. | Thrown when an issued serial does not exist, is not InStock, or sits at another warehouse; also on a double issue of the same serial (Phase B 054). |
| INV-6008 | SerialDuplicate | Serial '{0}' already exists for material '{1}'. | Thrown when a receipt tries to create a serial number that already exists for the material (database UNIQUE is the backstop) (Phase B 054). |
| INV-6009 | BatchAllocationMismatch | The batch/serial allocations do not match the movement quantity for material '{0}'. | Thrown when batch allocations do not sum to the movement quantity, the serial count differs from the quantity, or a serial quantity is not a whole number (Phase B 054). |
| INV-6010 | BatchExpired | Batch '{0}' for material '{1}' expired on {2} and cannot be issued. | Thrown when an issue allocates an expired batch without the INV.BATCH.EXPIREDOVERRIDE permission path (decision B1) (Phase B 054). |
| INV-6011 | QualityGateBlocked | A rejected quality inspection blocks document '{1}' for material '{0}'. | Thrown by QualityGate.CheckOutgoing when a REJECTED outgoing inspection exists for the document — blocks in both gate modes (Phase B 056). |
| INV-6012 | QualityQuarantinedSale | Material '{0}' is in quarantine awaiting quality inspection and cannot be sold in Stop gate mode (document '{1}'). | Thrown by QualityGate.PlanQuarantineDraw when a sale's shortfall exists only in quarantine and QC_GATE_MODE = Stop (Phase B 056). |
| INV-6013 | QualityOutgoingInspectionRequired | Material '{0}' requires an accepted outgoing quality inspection before document '{1}' can be delivered (Stop gate mode). | Thrown by QualityGate.CheckOutgoing in Stop mode when no accepted outgoing inspection exists for the document (Phase B 056, decision Q4). |
| INV-6014 | QualityInspectionStateInvalid | The quality inspection for document '{0}' cannot change state from '{1}'. | Thrown when a completed (non-Draft) inspection is edited or completed again, when a mandatory line is unjudged at completion, or when an inspection does not exist (Phase B 056). |
| INV-6015 | QualityInspectionQtyMismatch | Accepted and rejected quantities for document '{0}' do not add up to the inspected quantity, or the disposition is missing. | Thrown by QualityGate completion when accepted + rejected ≠ total, quantities are negative, or a rejected part has no valid disposition (Phase B 056). |
| INV-6016 | QualityTemplateInvalid | Quality template '{0}' is invalid. | Thrown by QualityService/QualityRepository when a template references an unknown or duplicated parameter, or a line's criteria do not match its parameter kind (Phase B 056). |
| LIC-8001 | LicenseCheckFailed | The license could not be verified. | License check failed (License service). |
| CRM-9001 | CrmLeadAlreadyConverted | Lead '{0}' has already been converted. | Thrown by CrmRepository.ConvertLeadAsync when the lead already carries conversion links (Phase B 057). |
| CRM-9002 | CrmLeadStateInvalid | Lead '{0}' cannot change state as requested. | Thrown when an unqualified lead is converted, a lead is missing, or a status edit tries to forge Converted (Phase B 057). |
| CRM-9003 | CrmOpportunityStageInvalid | Opportunity stage '{0}' is invalid. | Thrown when an opportunity or stage move references a stage/opportunity that does not exist for the company (Phase B 057). |
| CRM-9004 | CrmQuotationPartyMissing | A quotation cannot be created for '{0}'. | Thrown when an opportunity has no customer party, or the company Guid needed for the chain-compatible quotation cannot be resolved (Phase B 057, decision C3). |
| CRM-9005 | CrmActivityTargetInvalid | Activity '{0}' must target exactly one of a lead, an opportunity or a party. | Thrown when an activity carries zero or several targets, or a completion targets a non-planned activity (Phase B 057). |
| CRM-9006 | CrmLostReasonRequired | A lost reason is required when opportunity '{0}' is marked lost. | Thrown by CrmRepository.MoveStageAsync when the target stage is Lost and no lost reason was supplied (Phase B 057). |
| MFG-1001 | BomCycleDetected | Bill of materials for '{0}' references itself directly or through other bills of materials; the cycle must be removed before the BOM can be used. | Thrown by BomRepository explosion / save validation when a BOM references its own finished material directly or transitively (Build 058). |
| MFG-1002 | BomNotFoundOrInactive | Bill of materials '{0}' was not found or is not active for this company. | Thrown when an explosion, order fetch or planning run references a missing/inactive BOM version (Build 058). |
| MFG-1003 | MfgOrderStageInvalid | Manufacturing order '{0}' cannot perform this action in status '{1}'. | Thrown by the staged workflow (Confirm/Issue/Finish/Close/Cancel) when the order's status does not allow the transition (Build 058). |
| MFG-1004 | MfgIssueExceedsRequired | Issuing {1} of '{0}' would exceed the required quantity on the order. | Thrown by MfgOrderRepository.IssueAsync when a staged issue exceeds the line's remaining required quantity (Build 058). |
| MFG-1005 | MfgFinishExceedsPlanned | Receiving {1} of '{0}' would exceed the planned production quantity. | Thrown by MfgOrderRepository.FinishAsync when a receipt exceeds the order's remaining planned quantity (Build 058). |
| MFG-1006 | PlanningConversionInvalid | Planning row '{0}' cannot be converted in status '{1}'. | Thrown by PlanningRepository conversions when the row is not Planned/Suggested or was already converted (Build 058). |
| HR-1001 | HrEmployeeCodeDuplicate | Employee code '{0}' already exists for this company. | Thrown by HrRepository.SaveEmployeeAsync on a duplicate employee code (Build HR 065). |
| HR-1002 | HrEmployeeInvalid | Employee '{0}' has invalid data and cannot be saved. | Thrown when an employee card fails validation (missing code/name/hire date, terminated without a termination date) (Build HR 065). |
| HR-1003 | HrLeaveBalanceInsufficient | Leave balance is insufficient for '{0}': the requested days exceed the available balance. | Thrown by HrRepository.ApproveLeaveAsync when approval would take the balance negative and the caller holds no HR.LEAVE.OVERRIDE (Build HR 065). |
| HR-1004 | HrLeaveStateInvalid | The leave application cannot change state from '{0}'. | Thrown when approve/reject/cancel targets an application that is not in an actionable status (Build HR 065). |
| HR-1005 | HrPayrollAlreadyPosted | Payroll run '{0}' is already posted. | Thrown when a posted run is regenerated, edited or posted again; reverse it first (Build HR 065). |
| HR-1006 | HrPayrollStateInvalid | Payroll run '{0}' is not in a state that allows this operation. | Thrown for missing/empty/zero runs, un-finalize or reversal of a non-posted run, payslip-line edits on a posted run, system-line tampering, and unpaid deductions that exceed the expense debits (Build HR 065). |
| HR-1007 | HrSalaryStructureInvalid | Salary structure '{0}' is invalid. | Thrown when a structure or assignment fails validation (no lines, bad calc values, missing structure/employee) (Build HR 065). |
| HR-1008 | HrMasterInUse | This record is still referenced by other data and cannot be deleted. | Thrown when deleting a department/designation/employee/leave type that other HR data references; employees with payslips can never be deleted (Build HR 065). |
| HR-1009 | HrEmployeeNotPayable | Employee '{0}' is terminated and cannot receive a salary assignment or payroll. | Thrown by HrService.SaveAssignmentAsync for a terminated employee (Build HR 065). |
| HR-1010 | HrMasterCodeDuplicate | Code '{0}' already exists for this company. | Thrown on duplicate department/designation/leave-type/component/structure codes (Build HR 065). |
| SYS-0001 | UnknownError | An unexpected error occurred. | Fallback for any error with no better code; also what an unregistered code resolves to. |
| SYS-0002 | DispatcherUnhandled | An unexpected error interrupted the current operation. | Unhandled exception on the UI dispatcher (global handler fallback). |
| SYS-0003 | AppDomainUnhandled | An unexpected error occurred in the application. | Unhandled AppDomain exception (global handler fallback). |
| SYS-0004 | UnobservedTaskException | A background task failed. | Task exception that was never observed (global handler fallback). |
| SYS-0005 | NonFatalInternalFailure | A non-critical internal operation failed and was recovered. | Default code for non-fatal internal failures caught and reported inside services and helpers. |
| SYS-0006 | ConfigurationLoadFailed | The application configuration could not be loaded. | Application configuration (appset.json) failed to load. |
| SYS-0007 | FileAccessFailed | A file could not be accessed. | File access failure (Classify default for IO/unauthorized-access exceptions). |
| SYS-0009 | StartupFailed | The application could not start. | Failure inside the splash/startup sequence (SplWin, Lang). |
| SYS-0010 | SettingsLoadFailed | Settings could not be loaded; defaults were used. | User/window preferences failed to load (defaults are used). |
| SYS-0011 | SettingsSaveFailed | Settings could not be saved. | User/window preferences failed to save. |
| UI-7001 | WindowOperationFailed | The window operation could not be completed. | Default code for failures caught inside a window's operations. |
| UI-7002 | ShellOperationFailed | A main window operation could not be completed. | Default code for failures caught inside the main shell (MWin) and its view model. |
| UI-7003 | WindowLoadFailed | The window could not be loaded. | Window failed to load (catalogued for window-load paths; wired incrementally). |
| UI-7004 | NavigationFailed | Navigation could not be completed. | Navigation/report routing failed (RptNav). |

## 4. The reporting helper (one line per file)

Any file reports an error from its catch block in one line:

```csharp
catch (Exception ex)
{
    AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "MatCard.SaveAsync");
    // ...existing handling continues unchanged...
}
```

`Report(code, exception, context)` never throws. It resolves the catalog
entry and writes a coded error line (§5) whose message is
`<CatalogName> | <context>`, appending `| UnregisteredCode=<code>` when the
requested code is not in the catalog. The context is always
`File.Operation`, so a log line names the exact file and operation that
failed — this is what makes per-file maintenance practical.

## 5. Log format — logs are compatible with this catalog

`LogServ` (in `Services/Data.cs`) writes `log.log` and mirrors every error
line to `errors.log`. Compatibility with the catalog is **structural**: the
logging API itself takes the code, so no caller can produce an uncoded
error line by forgetting to paste a code into message text.

`ILogServ` has a coded overload:

```csharp
void LogError(string category, string code, string message, Exception? ex = null);
```

Every error line has the fixed format:

```
[yyyy-MM-dd HH:mm:ss.fff] [SYS-ERROR] [Category] [Company=001] [User=admin] [Code=AUTH-2002] [Name=AccountLocked] <message> | Exception: <type>: <message> | StackTrace: <trace>
```

- The bracketed prefix segments are positional and unchanged from before:
  timestamp, level, category, company, user.
- **`[Code=…]` and `[Name=…]` immediately follow the user segment, at a
  fixed position at the start of the message body.** `Code` is always a
  code that exists in the English catalog index above; `Name` is that
  entry's catalog name. Any error line can therefore be matched back to
  this index mechanically (parse the two segments, look the code up).
- When a caller reports a code that is not registered, the line is stamped
  with the resolved fallback (`SYS-0001`/`UnknownError`) and the originally
  requested code is appended to the message as `| ReportedCode=<code>`, so
  no information is lost and the invariant "every emitted code exists in
  the index" always holds.
- The pre-existing un-coded `LogError(category, message, ex)` overload
  keeps working for all current call sites: it derives the code from the
  exception (`AppError.Code` when present, otherwise `Classify`) and
  delegates to the coded overload. Un-coded calls therefore still emit
  fully coded lines.
- The exception type, message, and stack trace still follow the message,
  as before.

Example lines:

```
[2026-11-17 09:15:02.441] [SYS-ERROR] [Errors] [Company=001] [User=admin] [Code=AUTH-2002] [Name=AccountLocked] AccountLocked | LogWinVM.LoginAsync
[2026-11-17 09:16:40.102] [SYS-ERROR] [Global] [Company=001] [User=admin] [Code=DB-1005] [Name=DatabaseQueryFailed] Dispatcher unhandled exception. | Exception: Microsoft.Data.Sqlite.SqliteException: ...
```

## 6. The standing rule — every file reports its errors

This is a permanent coding standard for ShouTech (owner directive,
2026-11-17):

1. **Every working file reports the errors that happen in it** through the
   central mechanism: `AppHost.Errors.Report(code, ex, "File.Operation")`
   in the app project; the log line carries the catalog code, the catalog
   name, and the file/operation context.
2. **Silent catches are forbidden.** A `catch` block must report, rethrow,
   or wrap-and-throw. Intentionally non-fatal catches keep their
   log-and-continue behavior — reporting is added, behavior is not changed.
3. **Libraries throw, the app reports.** Code in `src/` (Domain,
   Application, Infrastructure) does not log and does not swallow: it lets
   exceptions propagate or throws `AppError` with a catalog code, and the
   app layer reports them. (The repositories' rollback-and-rethrow pattern
   is exactly this.)
4. **New codes follow §1**: next free number in the domain, constant in
   `ErrorCodes.cs`, entries in the four JSON catalogs, row in the index.

### Documented exemptions (the only non-reporting catches)

- **LogServ's own sink guards** (`Services/Data.cs`: `WriteHeader`,
  `Write`, `TrimIfTooLarge`). The logger cannot report through itself —
  calling `Report` there would recurse into the failing write path. These
  three catches exist so logging can never crash the application.
- **The error catalog's terminal fallback** (`Services/ErrorCatalog.cs`:
  the catch inside `Report` itself, and `SafeLog`'s own guard). Same
  reason: the reporting path cannot report through itself.
- **DatWin** (`Views/DatWin.xaml.cs`, 10 catches): owned by the parallel
  fixes branch (`fix/rtl-and-datwin`); listed in the coverage audit and
  converted there / on merge, not in this branch.

Coverage proof, per file, lives in `docs/ERROR_REPORTING_COVERAGE.md`.
