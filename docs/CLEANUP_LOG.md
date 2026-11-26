<!-- source: CLEANUP_LOG.md -->

# ShouTech ERP v0.4 — Cleanup Log
**Date:** 2026-10-01  
**Action:** Safe archive of dead / unused / duplicate files  
**Policy:** Archive first (recoverable), never hard-delete without backup.

## Removed from active tree (moved to `_archived_dead/`)

### 1. Duplicate leftovers after rename scripts
| Archived file | Active replacement | Reason |
|---------------|-------------------|--------|
| `Converters/BoolVis.cs` | `BoolToVisibilityConverter.cs` | XAML uses long name only |
| `Navigation/RibbonModuleMapper.cs` | `RibModMap.cs` | Shell + MainShVM call `RibModMap` |
| `Views/Modules/ModWsVw.xaml` + `.cs` | `ModuleWorkspaceView.xaml` + `.cs` | `Shell.CreateWorkspace` uses ModuleWorkspaceView |
| `Converters/LangFlwCv.cs` | — | Unused (FlowDirection handled in code) |
| `Converters/LanguageToFlowDirectionConverter.cs` | — | Unused |

### 2. Empty placeholders (0 bytes)
- `bld/bld.bat`
- `bld/pkg.ps1`
- `bld/sgn.ps1`
- `LICENSE`
- `doc/dep.md`

### 3. Finished one-time rename scripts
- `tools/rename_short.ps1`
- `tools/rename_short2.ps1`
- `tools/rename_distinct.ps1`

## Additional professional fix
- Renamed `src/ShouTech.Infrastructure/Class1.cs` → `SqlJournalRepository.cs`  
  (file already contained the real `SqlJournalRepository` class — name was misleading)

## Safety verification performed
- No external references found for any removed type/class.
- MainApp still compiles all remaining `**/*.cs` and `**/*.xaml`.
- Domain / Application / Infrastructure project references unchanged.

## Recovery
All archived files are under `_archived_dead/` with original relative paths preserved by category.
To restore any file: copy it back to its original location.

## Next recommended steps
1. Implement real content for `bld/bld.bat`, `pkg.ps1`, `sgn.ps1` (enterprise build pipeline).
2. Expand unit tests under `tests/Kern.Tests/`.
3. Populate `int/fore.onnx` when AI forecasting model is ready.

---

<!-- source: PHASE6_CHANGELOG.md -->

# Phase 6 — Chart of Accounts and Fiscal Closing Controls

- Added company-scoped `chart_of_accounts` with account hierarchy, type, and posting eligibility.
- Added PostgreSQL `fiscal_years` and both-provider `fiscal_periods` schemas, indexes, and monthly period seeding for years represented by legacy journal history.
- Added backfill to assign legacy journal entries to matching periods without deleting business data.
- Journal draft creation now requires an open matching period and active, non-deleted, postable accounts from the same company.
- Posting is checked by both repository validation and database triggers; direct inserts as posted are also rejected when controls fail.
- Added `AccountingStructureRepository` methods to create fiscal years and accounts, close periods sequentially, and close fiscal years only after draft/balance checks and zero P&L balances.
- Prevented direct reopening of closed/locked periods and closed fiscal years.
- Exposed the repository from `AppHost.AccountingStructure`.
- Added `docs/ACCOUNTING_CHART_PERIODS_YEAR_CLOSE.md`.

Validation performed: all nine SQLite migrations execute on a fresh in-memory database; test cases verified period backfill for legacy entries, successful posting with active accounts and an open period, rejection of posting to a closed period, rejection of non-postable accounts, rejection of direct posted inserts into closed periods, and rejection of direct period reopening.

Limitations: the .NET SDK and a live PostgreSQL server were not available for this run, so C# compilation and PostgreSQL integration remain required. The income-statement closing entry is not auto-generated because retained-earnings account mapping and year-end policy must be configured per company; year closure blocks until revenue/expense balances are zero.
