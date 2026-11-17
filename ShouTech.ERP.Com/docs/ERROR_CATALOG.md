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
| AUTH-2001 | InvalidCredentials | The username or password is incorrect | Sign-in failed. Internal code for every generic credential failure; the user only ever sees the generic message. |
| AUTH-2002 | AccountLocked | This account is locked. Sign-in was refused. | Sign-in refused because the account is locked (internal code; not shown to the user). |
| AUTH-2003 | AccountLockedNow | The account was locked after repeated failed sign-in attempts. | The sign-in attempt that caused the account to lock (internal code; not shown to the user). |
| AUTH-2004 | PasswordPolicyViolation | Administrator password does not meet the password policy: {0} | Administrator password refused at company creation by the enterprise password policy (CoCreateSvc). |
| CO-3001 | CompanyRecordNotFound | The local company record required to open this company was not found. | Local company record not found when opening a company (ComWin). |
| CO-3002 | CompanyDatabasePathMissing | Company database path is not set. | Company record has no database path configured (ComWin). |
| CO-3003 | PostgresDatabaseNameMissing | PostgreSQL database name is not set. | PostgreSQL company record has no database name (ComWin). |
| CO-3004 | PostgresConnectionMissing | Current PostgreSQL connection is not available. | No PostgreSQL connection string is available for the company (ComWin). |
| CO-3005 | CompanyLoadFailed | Failed to load companies | Loading the company list failed (ComWin). |
| CO-3006 | CompanyOpenFailed | Unable to open company | Default code for company open/switch failures reported inside ComWin and the company services. |
| DATA-4001 | ExportPermissionDenied | You do not have permission to export data (DATA.EXPORT). | *Reserved* — DatWin export refused for missing DATA.EXPORT permission. The gate lives in DatWin, which is owned by the parallel fixes branch; conversion lands with that branch. |
| DATA-4002 | ExportFailed | The export could not be completed. | Export failed (DataExportSvc and export paths). |
| DATA-4003 | ImportFailed | The import could not be completed. | Import failed (catalogued for the import paths; wired incrementally). |
| DB-1001 | DatabaseConnectionFailed | Could not connect to the database. | Database connection could not be opened (also Classify default for timeouts). |
| DB-1002 | DatabaseFileMissing | Company database file not found. | Company database file does not exist (ComWin company-open verification). |
| DB-1003 | MigrationFailed | A database migration failed. | Schema migration failed (SchMig). |
| DB-1004 | DatabaseNotInitialized | Company database file is not properly initialized. | Company database exists but is not initialized (no companies table; ComWin verification). |
| DB-1005 | DatabaseQueryFailed | A database operation failed. | A database query failed (Classify default for DB exceptions; dashboard collectors in DataServ). |
| DB-1006 | UnsupportedDatabaseProvider | Unsupported database provider. | Configured database provider is not supported (ComWin company-open). |
| INV-6001 | InventoryOperationFailed | The inventory operation could not be completed. | Default code for inventory operation failures (Inventory service and repositories). |
| LIC-8001 | LicenseCheckFailed | The license could not be verified. | License check failed (License service). |
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
