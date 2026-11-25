# Repository Structure and Bootstrap Audit

Branch: `reduce-2-upgrade`
Audited code baseline: `8584bee7e604da7758fff2f2a1aad0099d64d565`

## Repository inventory

- Recursive Git tree: 840 tracked files; tree response was complete and not truncated.
- Exact duplicate blob groups: 8.
- No exact-duplicate source-code groups were found.
- No files were deleted or merged in this phase. This is deliberate: all exact duplicates are migration assets whose identities/order must be preserved.

## Exact duplicate blobs and disposition

| Paths | Disposition |
|---|---|
| `dba/mig/postgresql/003_modules.sql` and `004_modules.sql` | Same bytes but distinct migration IDs. Keep both to preserve migration history and checksums. |
| `dba/mig/postgresql/009_accounting_indexes.sql` and `dba/mig/sqlite/007_accounting_indexes.sql` | Provider-specific migration copies. Keep. |
| `dba/mig/postgresql/013_deletion_audit.sql` and `dba/mig/sqlite/011_deletion_audit.sql` | Provider-specific migration copies. Keep. |
| `dba/mig/postgresql/015_journal_document_number_unique.sql` and `dba/mig/sqlite/013_journal_document_number_unique.sql` | Provider-specific migration copies. Keep. |
| `dba/mig/postgresql/084_supplier_invoice_reference_unique.sql` and `dba/mig/sqlite/084_supplier_invoice_reference_unique.sql` | Provider-specific migration copies. Keep. |
| `dba/mig/postgresql/120_invoice_line_notes.sql` and `dba/mig/sqlite/120_invoice_line_notes.sql` | Provider-specific migration copies. Keep. |
| `dba/mig/postgresql/121_uom_names.sql` and `dba/mig/sqlite/121_uom_names.sql` | Provider-specific migration copies. Keep. |
| `dba/mig/postgresql/126_barcode_template.sql` and `dba/mig/sqlite/126_barcode_template.sql` | Provider-specific migration copies. Keep. |

## Executable and startup paths

- Desktop WPF executable: `cod/app/src/App.xaml`, explicitly included as `ApplicationDefinition` by `cod/app/src/MainApp.csproj`; `App.xaml.cs` owns WPF lifecycle.
- Desktop composition root: `cod/app/src/Services/AppHost.cs`; this is not a second executable entry point.
- Windows background service: `svc/Program.cs`; a distinct deployable process, intentionally separate from desktop WPF.
- Developer/test harnesses: separate executable projects with their own `Program.cs`; these are not desktop launch paths.
- `src/App/App.csproj` is a class library, not an executable.
- The desktop project uses explicit XAML items and wildcard C# compile inclusion. Source removal/merging requires checking symbol references, XAML, resources, reflection, and project inclusion.

## Startup consolidation

The WPF `SchemaBootstrapper` now delegates to `SchemaMigrator.ApplyAllAsync`, the in-process migration engine also used by company creation. The previous embedded-resource SQL bootstrapper was removed: its expected resource names (`ShouTech.App.dba.lite.sql` and `ShouTech.App.dba.pg.sql`) had no tracked SQL source or explicit embedded-resource inclusion in `MainApp.csproj`. The old code silently returned when those resources were absent.

The replacement:
- uses the provider selected by `DatabaseRuntime`;
- applies the ordered provider-specific scripts from `dba/mig`;
- preserves a per-connection process cache and reset methods;
- marks a database initialized only after successful migration;
- returns without error when no connection string is configured, which is valid before a company database is selected;
- propagates migration failures rather than treating an absent bootstrap resource as success.

The desktop project copies both provider migration directories to `dba/mig` beside the executable, which supports `SchemaMigrator.FindRepositoryRoot` in build/publish output. The administrative `SqlMigrator` remains a separate CLI runner over the same migration files and history/checksum contract; consolidating the two runners is a separate refactor and was not mixed into this startup change.

## PostgreSQL migration repairs

The clean PostgreSQL smoke workflow exposed three consecutive failures, all repaired in their owning migration files:

1. `104_pos_remainder.sql`: `sales_documents.is_deleted` is `INT`, so the partial-index predicate now uses `is_deleted = 0`, not Boolean `FALSE`.
2. `112_rfq_invitations_backfill.sql`: integer deletion flags on purchase document rows now compare to `0`, not Boolean `FALSE`.
3. `115_report_designer.sql`: removed SQLite-only conditional-column directives from the PostgreSQL script and used native `ADD COLUMN IF NOT EXISTS` for idempotent additions.

The earlier `095_party_structure.sql` repair also aligned three `party_id` foreign-key columns to PostgreSQL `UUID`, matching `parties.id`.

## Verification evidence

- Database migration smoke workflow on commit `f6e494ef9cf653beb902717758499b7af75d2b18`: SQLite clean install and same-database rerun passed; PostgreSQL clean install, same-database rerun, and migration-history check passed.
- Windows CI for the startup consolidation was repeatedly superseded by later documentation/comment-only commits. The final Windows CI run must be recorded against the final code commit before this phase is closed.
- No database migration file changed after the successful smoke workflow.

## Research basis

- Microsoft .NET Generic Host documentation describes the host's responsibilities for application startup, dependency injection, configuration, logging, and lifetime: https://learn.microsoft.com/dotnet/core/extensions/generic-host
- Microsoft DI guidance recommends small, well-factored services and avoiding unnecessary stateful static infrastructure: https://learn.microsoft.com/dotnet/core/extensions/dependency-injection-guidelines
- PostgreSQL documentation defines partial-index predicates as part of the index definition; the predicate must be valid for the actual underlying column types: https://www.postgresql.org/docs/current/sql-createindex.html

## Structural cleanup decision

The eight exact duplicate groups were inspected. Seven are intentional SQLite/PostgreSQL migration copies; the remaining pair are separately numbered PostgreSQL migrations. Removing or renaming any of these can break deployed migration ledgers, checksum verification, or replay order. No arbitrary source-file merges or deletions are justified by file count alone. The next source consolidation should target cohesive service responsibilities and preserve stable public APIs.

## Final Windows build and regression evidence

Windows CI run: https://github.com/Roni4422/ShouTech/actions/runs/45563844039
Code commit tested: `8ed1c27fb9e4d38e748625aac34823f4a77216ac`

- NuGet restore: passed.
- Windows Release build of the application and test project: passed.
- Full `Kern.Tests` run reached the 30-minute job limit and was cancelled by the runner. The log recorded 23 failing tests before cancellation; therefore this run is not a passing full-suite result.
- The failing test groups reported by the run were:
  - document numbering expectation and consolidated-invoice numbering;
  - generalized purchase attachments;
  - party unification (supplier-payment validation, opening-balance FX, blacklist validation);
  - sales contract fulfillment and sales-chain fiscal-period / warehouse setup;
  - credit-limit coded-error expectation;
  - delivery-cost decimal residual;
  - purchase-chain fixture schema and posting cases;
  - CRM qualification conversion;
  - user-preference normalization;
  - sales approval-history ordering and sales-chain posting cases.
- These failures are recorded as separate business-domain regression work items; they were not hidden or reclassified as successful by the structural/startup changes.

The SQLite and PostgreSQL migration smoke workflow remains successful on commit `f6e494ef9cf653beb902717758499b7af75d2b18`, with clean install, same-database rerun, and migration-history checks for both providers. No migration files changed after that successful run.


## Follow-up structural pass — 2026-11-25

Branch: `reduce-2-upgrade`
Snapshot commit before this audit update: `75c608f5d8d52d60f431cce509151566d7ecd75e`

- Recursive Git tree: **839 tracked files** (complete tree; not truncated).
- Removed the two stale workflow copies under `ShouTech.ERP.Com/.github/workflows/`. GitHub Actions discovers workflows only from the repository-root `.github/workflows/`; the active root workflows remain intact.
- Root workflows retained: `.github/workflows/windows-ci.yml` and `.github/workflows/db-migrations-smoke.yml`.
- Recomputed exact blob duplicates: **8 groups**, all SQL migration assets. Seven groups are cross-provider SQLite/PostgreSQL copies; one is two separately numbered PostgreSQL migrations (`003_modules.sql` and `004_modules.sql`). All are retained to protect migration identity, checksums, and replay history.
- No exact duplicate C# source blobs were found.
- Same-basename source files were not merged solely by name: `Localization/ErrorCatalog.cs` and `Services/ErrorCatalog.cs` expose different namespaces and responsibilities; `Services/Security.cs` and `cod/cor/Security.cs` are different security layers. The `cod/cor` inventory project is referenced by both the solution and desktop project, so it is not an orphan.
- Windows CI for the structural deletion commit `75c608f5d8d52d60f431cce509151566d7ecd75e` was running when this addendum was prepared; its final result must be recorded after completion.

The file-count reduction in this pass is **2 files**. No migration, source API, runtime resource, or executable entry point was changed.
