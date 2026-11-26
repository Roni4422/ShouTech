# Error Reporting Coverage Audit

Owner rule (2026-11-17): **every working file must report the errors that
happen in it** through the central mechanism, because that makes maintenance
easier. This document is the proof: a per-file audit of `cod/app/src`
before and after the `feat/error-catalog` sweep, the library projects, and
the exemption list. The rule itself is documented as a permanent coding
standard in `docs/ERROR_CATALOG.md` §6.

## How the audit works

Mechanical scan of every `.cs` file: each `catch` block is extracted
(brace-matched) and classified as **reporting** when its body calls the
central reporting path (`AppHost.Errors.Report(...)`, any
`LogError/LogWarning/LogSecurity/LogOperation/LogAuthAttempt/LogFileAccess/
LogLifecycle/LogDebug` overload, or `SafeLog` inside the catalog service)
or propagates (`throw`). A catch whose body is empty or comment-only is
**silent**. The scan is re-runnable; the numbers below are its output.

### Totals (cod/app/src)

| Measure | Before | After |
|---|---|---|
| `.cs` files | 130 | 131 (+ `Services/ErrorCatalog.cs`) |
| Catch blocks | 537 | 542 |
| Catches with no report and no rethrow | 450 | 14 |
| Silent catches (empty / comment-only) | 234 | 11 |
| Files using central logging | 33 | all files with catches |

The 14 remaining non-reporting catches are exactly the documented
exemptions, and zero anywhere else: LogServ's 3 sink guards and the
error catalog's terminal guard inside `Report` (these cannot report
through the mechanism they implement), plus DatWin's 10 catches (owned
by the parallel fixes branch — see §5). The 11 remaining silent catches
are those same 4 terminal guards plus DatWin's 7 silent catches.

## 1. Startup chain — 100% first (owner's coverage order)

The sweep began with the startup chain in execution order; nothing in it
may fail silently. Per-file proof (before → after):

| File (execution order) | Before | Catches now | All reporting |
|---|---|---|---|
| App.xaml.cs (entry + global handlers) | 46 catches, 32 silent | 46 | YES |
| Views/SplWin.xaml.cs (splash) | 18 catches, 13 silent | 18 | YES |
| Views/Lang.xaml.cs (first-run language) | 8 catches, 6 silent | 8 | YES |
| Views/StpWiz.xaml.cs (setup wizard) | 12 catches, 8 silent | 12 | YES |
| Views/ComWin.xaml.cs (companies) | 20 catches, 8 silent | 20 | YES |
| Views/LogWin.xaml.cs (login window) | 6 catches, 6 silent | 6 | YES |
| ViewModels/LogWinVM.cs (login logic) | no catches | 0 | YES — reports failed sign-ins via `AuthResult.ErrorCode` (AUTH-2001/2002/2003) |
| Services/Auth.cs (authentication) | 9 catches, 2 silent | 9 | YES |
| Views/MWin.xaml.cs (main shell) | 15 catches, 10 silent | 15 | YES |
| ViewModels/MainShVM.cs (shell VM) | 7 catches, 4 silent | 7 | YES |

Startup failure points are additionally *coded*, not just reported:
company-open verification throws `AppError` CO-3001/3002/3003/3004,
DB-1002/1004/1006; company-list load reports CO-3005; sign-in failures
carry AUTH-2001/2002/2003; the global handlers log SYS-0002/0003/0004
with coded lines from the first run.

## 2. Per-file classification — before vs after (all app files)

Before-classes: **REPORTS-CENTRALLY** (already used the central log),
**SILENT-CATCH** (had empty/comment-only catches), **MESSAGEBOX-ONLY**
(errors shown ad hoc, never logged), **NO-HANDLING** (no reporting of any
kind). Counts before: 33 REPORTS-CENTRALLY, 29 SILENT-CATCH,
8 MESSAGEBOX-ONLY, 60 NO-HANDLING.

| File | Before class | Catches before | Silent before | Catches now | Status now |
|---|---|---|---|---|---|
| App.xaml.cs | REPORTS-CENTRALLY | 46 | 32 | 46 | ALL REPORT |
| Common/Code128.cs | MESSAGEBOX-ONLY | 4 | 0 | 4 | ALL REPORT |
| Common/Common.cs | NO-HANDLING | 0 | 0 | 0 | ALL REPORT |
| Configuration/AppConfig.cs | SILENT-CATCH | 2 | 1 | 2 | ALL REPORT |
| Configuration/UsrPref.cs | SILENT-CATCH | 2 | 2 | 2 | ALL REPORT |
| Converters/BoolToVisibilityConverter.cs | NO-HANDLING | 0 | 0 | 0 | ALL REPORT |
| Interaction/DataGridTableBehavior.cs | REPORTS-CENTRALLY | 8 | 6 | 8 | ALL REPORT |
| Interaction/EntityContextAction.cs | NO-HANDLING | 0 | 0 | 0 | ALL REPORT |
| Localization/ErrorCatalog.cs | NO-HANDLING | 1 | 0 | 1 | ALL REPORT |
| Localization/Loc.cs | NO-HANDLING | 0 | 0 | 0 | ALL REPORT |
| Localization/LocProps.cs | NO-HANDLING | 0 | 0 | 0 | ALL REPORT |
| Localization/UiLoczr.cs | NO-HANDLING | 0 | 0 | 0 | ALL REPORT |
| Navigation/RibModMap.cs | NO-HANDLING | 0 | 0 | 0 | ALL REPORT |
| Reporting/FormulaEngine.cs | NO-HANDLING | 2 | 0 | 2 | ALL REPORT |
| Reporting/InvTpl.cs | NO-HANDLING | 0 | 0 | 0 | ALL REPORT |
| Reporting/Invoices/InvDoc.cs | NO-HANDLING | 0 | 0 | 0 | ALL REPORT |
| Reporting/ReportDesignerVM.cs | NO-HANDLING | 3 | 0 | 3 | ALL REPORT |
| Reporting/ReportModels.cs | NO-HANDLING | 0 | 0 | 0 | ALL REPORT |
| Reporting/ReportRepo.cs | NO-HANDLING | 1 | 0 | 1 | ALL REPORT |
| Reporting/RptDef.cs | NO-HANDLING | 0 | 0 | 0 | ALL REPORT |
| Reporting/RptRend.cs | NO-HANDLING | 0 | 0 | 0 | ALL REPORT |
| Services/Accounting.cs | NO-HANDLING | 2 | 0 | 2 | ALL REPORT |
| Services/AppHost.cs | REPORTS-CENTRALLY | 5 | 1 | 5 | ALL REPORT |
| Services/Auth.cs | REPORTS-CENTRALLY | 9 | 2 | 9 | ALL REPORT |
| Services/Backup.cs | REPORTS-CENTRALLY | 14 | 4 | 14 | ALL REPORT |
| Services/CashDrawerService.cs | NO-HANDLING | 3 | 0 | 3 | ALL REPORT |
| Services/Company.cs | REPORTS-CENTRALLY | 22 | 6 | 22 | ALL REPORT |
| Services/Data.cs | REPORTS-CENTRALLY | 7 | 5 | 7 | 4/7 report |
| Services/DataExportSvc.cs | REPORTS-CENTRALLY | 7 | 4 | 7 | ALL REPORT |
| Services/DocSchema.cs | NO-HANDLING | 0 | 0 | 0 | ALL REPORT |
| Services/ErrorCatalog.cs | NEW (this branch) | - | - | 5 | 4/5 report |
| Services/IServices.cs | REPORTS-CENTRALLY | 0 | 0 | 0 | ALL REPORT |
| Services/InvPrn.cs | NO-HANDLING | 0 | 0 | 0 | ALL REPORT |
| Services/InvRepo.cs | SILENT-CATCH | 7 | 1 | 7 | ALL REPORT |
| Services/Inventory.cs | SILENT-CATCH | 11 | 5 | 11 | ALL REPORT |
| Services/License.cs | NO-HANDLING | 3 | 0 | 3 | ALL REPORT |
| Services/Localization.cs | SILENT-CATCH | 6 | 6 | 6 | ALL REPORT |
| Services/MatGroupRepo.cs | SILENT-CATCH | 2 | 1 | 2 | ALL REPORT |
| Services/Online.cs | REPORTS-CENTRALLY | 17 | 2 | 17 | ALL REPORT |
| Services/PdfWrt.cs | NO-HANDLING | 0 | 0 | 0 | ALL REPORT |
| Services/Permissions.cs | NO-HANDLING | 0 | 0 | 0 | ALL REPORT |
| Services/ProductCategoryRepo.cs | SILENT-CATCH | 4 | 2 | 4 | ALL REPORT |
| Services/RecycleBinSvc.cs | REPORTS-CENTRALLY | 3 | 3 | 3 | ALL REPORT |
| Services/RibbonReportRouter.cs | NO-HANDLING | 0 | 0 | 0 | ALL REPORT |
| Services/RptNav.cs | SILENT-CATCH | 1 | 1 | 1 | ALL REPORT |
| Services/RptQuery.cs | NO-HANDLING | 1 | 0 | 1 | ALL REPORT |
| Services/SchMig.cs | SILENT-CATCH | 3 | 1 | 3 | ALL REPORT |
| Services/Security.cs | SILENT-CATCH | 11 | 3 | 11 | ALL REPORT |
| Services/ShcTemp.cs | SILENT-CATCH | 3 | 2 | 3 | ALL REPORT |
| Services/Shell.cs | REPORTS-CENTRALLY | 0 | 0 | 0 | ALL REPORT |
| Services/StkPost.cs | NO-HANDLING | 4 | 0 | 4 | ALL REPORT |
| Services/TaxSvc.cs | SILENT-CATCH | 6 | 2 | 6 | ALL REPORT |
| Services/VisuId.cs | SILENT-CATCH | 5 | 4 | 5 | ALL REPORT |
| Services/Win11Chrome.cs | SILENT-CATCH | 6 | 6 | 6 | ALL REPORT |
| Services/WinPref.cs | SILENT-CATCH | 5 | 3 | 5 | ALL REPORT |
| Services/XlsxWrt.cs | NO-HANDLING | 0 | 0 | 0 | ALL REPORT |
| Services/XptCry.cs | SILENT-CATCH | 2 | 1 | 2 | ALL REPORT |
| Services/XptServ.cs | MESSAGEBOX-ONLY | 3 | 0 | 3 | ALL REPORT |
| ViewModels/BaseVM.cs | NO-HANDLING | 0 | 0 | 0 | ALL REPORT |
| ViewModels/DockedWinItem.cs | NO-HANDLING | 0 | 0 | 0 | ALL REPORT |
| ViewModels/LogWinVM.cs | NO-HANDLING | 0 | 0 | 0 | ALL REPORT |
| ViewModels/MainShVM.cs | REPORTS-CENTRALLY | 7 | 4 | 7 | ALL REPORT |
| ViewModels/ModWsVM.cs | NO-HANDLING | 0 | 0 | 0 | ALL REPORT |
| ViewModels/PrmWinVM.cs | NO-HANDLING | 0 | 0 | 0 | ALL REPORT |
| ViewModels/RibbonTabViewModel.cs | NO-HANDLING | 0 | 0 | 0 | ALL REPORT |
| Views/AccCard.xaml.cs | REPORTS-CENTRALLY | 5 | 0 | 5 | ALL REPORT |
| Views/AppState.cs | NO-HANDLING | 0 | 0 | 0 | ALL REPORT |
| Views/AssetWin.cs | MESSAGEBOX-ONLY | 3 | 0 | 3 | ALL REPORT |
| Views/BarWin.cs | SILENT-CATCH | 2 | 1 | 2 | ALL REPORT |
| Views/BaseWin.cs | SILENT-CATCH | 2 | 2 | 2 | ALL REPORT |
| Views/BcdDesWin.cs | REPORTS-CENTRALLY | 4 | 1 | 4 | ALL REPORT |
| Views/BcdGenWin.cs | REPORTS-CENTRALLY | 4 | 0 | 4 | ALL REPORT |
| Views/BinWin.xaml.cs | SILENT-CATCH | 4 | 1 | 4 | ALL REPORT |
| Views/BkpWin.xaml.cs | REPORTS-CENTRALLY | 27 | 11 | 27 | ALL REPORT |
| Views/BnkCard.cs | REPORTS-CENTRALLY | 0 | 0 | 0 | ALL REPORT |
| Views/CashDrawer.cs | NO-HANDLING | 0 | 0 | 0 | ALL REPORT |
| Views/CatWin.xaml.cs | SILENT-CATCH | 30 | 15 | 30 | ALL REPORT |
| Views/CcCard.cs | REPORTS-CENTRALLY | 0 | 0 | 0 | ALL REPORT |
| Views/ChqWin.cs | REPORTS-CENTRALLY | 0 | 0 | 0 | ALL REPORT |
| Views/CoaWin.xaml.cs | MESSAGEBOX-ONLY | 7 | 0 | 7 | ALL REPORT |
| Views/ColChsWin.xaml.cs | NO-HANDLING | 0 | 0 | 0 | ALL REPORT |
| Views/ComWin.xaml.cs | SILENT-CATCH | 20 | 8 | 20 | ALL REPORT |
| Views/CshWin.cs | REPORTS-CENTRALLY | 0 | 0 | 0 | ALL REPORT |
| Views/CstCard.cs | REPORTS-CENTRALLY | 0 | 0 | 0 | ALL REPORT |
| Views/CurCard.cs | REPORTS-CENTRALLY | 0 | 0 | 0 | ALL REPORT |
| Views/DatWin.xaml.cs | REPORTS-CENTRALLY | 11 | 7 | 11 | 1/11 report |
| Views/InvoiceWin.cs | SILENT-CATCH | 17 | 8 | 17 | ALL REPORT |
| Views/JrnWin.cs | REPORTS-CENTRALLY | 4 | 0 | 4 | ALL REPORT |
| Views/Lang.xaml.cs | SILENT-CATCH | 8 | 6 | 8 | ALL REPORT |
| Views/LogWin.xaml.cs | SILENT-CATCH | 6 | 6 | 6 | ALL REPORT |
| Views/MWin.xaml.cs | SILENT-CATCH | 15 | 10 | 15 | ALL REPORT |
| Views/MatCard.xaml.cs | SILENT-CATCH | 25 | 12 | 25 | ALL REPORT |
| Views/MatDirWin.xaml.cs | SILENT-CATCH | 4 | 1 | 4 | ALL REPORT |
| Views/MatGroupWin.xaml.cs | REPORTS-CENTRALLY | 3 | 0 | 3 | ALL REPORT |
| Views/MfgWin.cs | REPORTS-CENTRALLY | 0 | 0 | 0 | ALL REPORT |
| Views/Modules/DashVw.xaml.cs | NO-HANDLING | 1 | 0 | 1 | ALL REPORT |
| Views/Modules/LangVM.cs | NO-HANDLING | 0 | 0 | 0 | ALL REPORT |
| Views/Modules/ModuleWorkspaceView.xaml.cs | NO-HANDLING | 0 | 0 | 0 | ALL REPORT |
| Views/Modules/ReportGridVw.xaml.cs | NO-HANDLING | 0 | 0 | 0 | ALL REPORT |
| Views/OrdWin.cs | REPORTS-CENTRALLY | 0 | 0 | 0 | ALL REPORT |
| Views/PayRecWin.cs | MESSAGEBOX-ONLY | 2 | 0 | 2 | ALL REPORT |
| Views/PosCheckoutWin.cs | MESSAGEBOX-ONLY | 1 | 0 | 1 | ALL REPORT |
| Views/PosWin.cs | SILENT-CATCH | 4 | 1 | 4 | ALL REPORT |
| Views/PrmWin.xaml.cs | REPORTS-CENTRALLY | 2 | 0 | 2 | ALL REPORT |
| Views/ProjWin.cs | MESSAGEBOX-ONLY | 6 | 0 | 6 | ALL REPORT |
| Views/RepCard.cs | REPORTS-CENTRALLY | 0 | 0 | 0 | ALL REPORT |
| Views/Reports/AccBalWin.cs | NO-HANDLING | 0 | 0 | 0 | ALL REPORT |
| Views/Reports/AccStmtWin.cs | NO-HANDLING | 0 | 0 | 0 | ALL REPORT |
| Views/Reports/BalSheetWin.cs | NO-HANDLING | 0 | 0 | 0 | ALL REPORT |
| Views/Reports/CustAgingWin.cs | NO-HANDLING | 0 | 0 | 0 | ALL REPORT |
| Views/Reports/CustBalWin.cs | NO-HANDLING | 0 | 0 | 0 | ALL REPORT |
| Views/Reports/IncStmtWin.cs | NO-HANDLING | 0 | 0 | 0 | ALL REPORT |
| Views/Reports/ItemProfitWin.cs | NO-HANDLING | 0 | 0 | 0 | ALL REPORT |
| Views/Reports/JrnListWin.cs | NO-HANDLING | 0 | 0 | 0 | ALL REPORT |
| Views/Reports/MatListWin.cs | NO-HANDLING | 0 | 0 | 0 | ALL REPORT |
| Views/Reports/MatMoveWin.cs | NO-HANDLING | 0 | 0 | 0 | ALL REPORT |
| Views/Reports/PriceListWin.cs | NO-HANDLING | 0 | 0 | 0 | ALL REPORT |
| Views/Reports/PurListWin.cs | NO-HANDLING | 0 | 0 | 0 | ALL REPORT |
| Views/Reports/SalListWin.cs | NO-HANDLING | 0 | 0 | 0 | ALL REPORT |
| Views/Reports/ShortListWin.cs | NO-HANDLING | 0 | 0 | 0 | ALL REPORT |
| Views/Reports/StkAdjRptWin.cs | NO-HANDLING | 0 | 0 | 0 | ALL REPORT |
| Views/Reports/StkBalWin.cs | NO-HANDLING | 0 | 0 | 0 | ALL REPORT |
| Views/Reports/SupAgingWin.cs | NO-HANDLING | 0 | 0 | 0 | ALL REPORT |
| Views/Reports/SupBalWin.cs | NO-HANDLING | 0 | 0 | 0 | ALL REPORT |
| Views/Reports/TrialBalanceWin.cs | NO-HANDLING | 0 | 0 | 0 | ALL REPORT |
| Views/RestoreCoWin.xaml.cs | MESSAGEBOX-ONLY | 2 | 0 | 2 | ALL REPORT |
| Views/RptBase.cs | NO-HANDLING | 1 | 0 | 1 | ALL REPORT |
| Views/SerWin.xaml.cs | REPORTS-CENTRALLY | 15 | 6 | 15 | ALL REPORT |
| Views/SplWin.xaml.cs | SILENT-CATCH | 18 | 13 | 18 | ALL REPORT |
| Views/StpWiz.xaml.cs | REPORTS-CENTRALLY | 12 | 8 | 12 | ALL REPORT |
| Views/WhsWin.xaml.cs | REPORTS-CENTRALLY | 24 | 7 | 24 | ALL REPORT |
Notes on the table:

- `Services/Data.cs` shows 4/7 reporting: the 3 non-reporting catches are
  LogServ's own sink guards (`WriteHeader`, `Write`, `TrimIfTooLarge`) —
  documented exemption; the logger cannot report through itself.
- `Services/ErrorCatalog.cs` (new in this branch): the only non-reporting
  catches are the terminal guards inside `Report` and `SafeLog` —
  documented exemption; its other internal failures report via `SafeLog`.
- `Views/DatWin.xaml.cs`: untouched in this branch (see §5).

## 3. Files with no catch blocks (60)

These files contain no `catch`, so there is no local swallow point. They
fall into two groups:

**Exempt — pure declarations, models, constants (no operations that can
fail locally):**
`Common/Common.cs`, `Converters/BoolToVisibilityConverter.cs`,
`Localization/Loc.cs`, `Localization/LocProps.cs`,
`Navigation/RibModMap.cs`, `Reporting/ReportModels.cs`,
`Reporting/RptDef.cs`, `Reporting/Invoices/InvDoc.cs`,
`Services/DocSchema.cs`, `Services/IServices.cs`,
`Services/Permissions.cs`, `ViewModels/DockedWinItem.cs`,
`ViewModels/RibbonTabViewModel.cs`, `Views/AppState.cs`,
`Views/Modules/LangVM.cs`.

**Covered by propagation — operational code whose failures cannot
vanish:** exceptions propagate to the calling window/service (which
reports) or to the global dispatcher handler (which logs a coded line and
shows the catalog dialog). This group includes the print/export writers
(`Services/InvPrn.cs`, `Services/PdfWrt.cs`, `Services/XlsxWrt.cs`), the
report windows under `Views/Reports/` (thin `RptBase` subclasses;
`RptBase` itself reports), the ribbon master-card windows
(`Views/BnkCard.cs`, `Views/CcCard.cs`, `Views/CshWin.cs`,
`Views/ChqWin.cs`, `Views/CstCard.cs`, `Views/CurCard.cs`,
`Views/MfgWin.cs`, `Views/OrdWin.cs`, `Views/RepCard.cs`),
`Services/RibbonReportRouter.cs`, `Services/Shell.cs`,
`Reporting/InvTpl.cs`, `Reporting/RptRend.cs`,
`Interaction/EntityContextAction.cs`, `Localization/UiLoczr.cs`,
`ViewModels/BaseVM.cs`, `ViewModels/ModWsVM.cs`,
`ViewModels/PrmWinVM.cs`, `Views/ColChsWin.xaml.cs`,
`Views/Modules/ModuleWorkspaceView.xaml.cs`,
`Views/Modules/ReportGridVw.xaml.cs`. `ViewModels/LogWinVM.cs` is listed
in §1: it has no catches but reports sign-in failures explicitly.

## 4. Library projects (src/) — libraries throw, the app reports

`AppError`/`ErrorCodes` live in `ShouTech.Domain` (no project references,
so no cycles); Application and Infrastructure both reference Domain.
Audit of all 15 catch blocks in `src/` — **none are silent**:

- 12 catches in the Infrastructure repositories
  (`AccountingStructureRepository` ×4, `AssetRepository`,
  `CommercialDocumentRepository` ×2, `JournalRepository` ×3,
  `ProjectRepository` ×2, `ReconciliationRepository`): roll the
  transaction back and **rethrow** — correct library behavior; the app
  layer reports the propagated exception.
- `DatabaseHealth` ×2: catch returns a failure *result object* — the
  health-check API contract; the failure is data for the caller, not a
  swallowed error.
- `ShouTech.Application/Loc.cs` ×1: catch falls back to an empty
  localization map (deliberate degrade; callers surface missing keys).

`tests/Kern.Tests` has one catch (`JournalReversalGuardTests` cleanup +
rethrow) and no silent catches.

Deliberately not converted: several library throw sites raise
`InvalidOperationException` whose exact type is pinned by existing tests
(xUnit `Assert.Throws` is exact-type). Converting them to `AppError`
(subclass of `Exception`, not of `InvalidOperationException`) is a
separate, test-updating change. The one conversion made is
`JournalEntry.EnsureBalanced` → ACC-5001/5002/5003, with its two test
assertions updated to match.

## 5. DatWin — owned by the parallel fixes branch

`Views/DatWin.xaml.cs` has 10 non-reporting catches (7 silent). DatWin's
behavior is owned by `fix/rtl-and-datwin`; this branch does not edit it.
They are listed here so the gap is explicit, and the DATA-4001
export-permission code is catalogued as **reserved** in
`docs/ERROR_CATALOG.md` for that branch to wire. On merge, DatWin's
catches get the same one-line `Report` treatment.

## 6. Silent catches that were hiding user-visible failures

Noted during the sweep (behavior preserved; reporting added):

- `Services/Data.cs` dashboard collectors (`GetRecentDocumentsAsync`,
  `CountRows`, `CollectCommercialDocuments`, `CollectJournalDocuments`)
  swallowed all database failures and returned empty data — a dead
  database looked like an empty company. Now reports DB-1005.
- `Views/ComWin.xaml.cs` background index refresh and cached-index load
  swallowed failures — the class of failure behind "ComWin didn't let me
  in" left no trace. Now reports CO-3006.
- `Services/Backup.cs` backup-key recovery (filtered catch) silently
  regenerated key material on corruption. Now reports SYS-0005 first.
