# DatWin export/import (.stei) — round-trip diagnosis and fix

Date: 2026-11-17 · Branch: `fix/rtl-and-datwin` · Engine: `SteiSvc` in
`cod/app/src/Services/Backup.cs` (the code behind DatWin's package
export/import and BkpServ backups).

## Format decision

**The .stei container format is unchanged** (ZIP: format.txt /
manifest.json / payload.bin / payload.tag / signing-public.key;
AES-256-GCM + PBKDF2-SHA256 310k; RSA-PSS-SHA256 signature;
FormatVersion 1). Every fix is in the export/import *logic*, so packages
exported by older builds remain importable by this build, and packages
exported by this build remain readable by older builds (they simply
contain no leaked foreign-company rows and no `schema_migrations`
bookkeeping table).

## How it was proven

A headless harness compiled the **real** `Services/Backup.cs` into a
console project (with disclosed shims: DPAPI `ProtectedData` pass-through —
Windows-only — and a settable `DatabaseRuntime` mirroring the production
SQLite semantics incl. `Foreign Keys=True`). It seeded a migrated company
database (fiscal year/period, COA, ledger accounts with Arabic names and
decimals, materials, sales documents, a **posted** journal entry created
the draft→lines→post way, projects, allocations, assets, stock, outbound
messages with Arabic body text), exported with `SteiSvc.ExportAsync`,
imported into a fresh migrated database with `SteiSvc.ImportAsync`, and
diffed the decrypted package against the target table by table. The same
scenarios are pinned in-repo as `tests/Kern.Tests/SteiRoundTripTests.cs`
(Windows execution).

## Before the fix (reproduced)

```
EXPORT: Success=True Tables=25 Rows=59
COVERAGE-DIFF brands: expected 0 company rows, package has 1
IMPORT-A: Success=False Partial=True Rows=50 FailedRows=9
IMPORT-A-MSG: Import completed with issues; no database changes were committed.
IMPORT-A-ERR: brands: FOREIGN KEY constraint failed
IMPORT-A-ERR: fiscal_periods: FOREIGN KEY constraint failed
IMPORT-A-ERR: journal_entries: 'Journal entries must be inserted as drafts
              and posted after lines are added'
IMPORT-A-ERR: journal_entry_lines: FOREIGN KEY constraint failed (x2)
IMPORT-A-ERR: material_group_items: FOREIGN KEY constraint failed
IMPORT-A-ERR: products: FOREIGN KEY constraint failed
IMPORT-A-ERR: project_tasks: FOREIGN KEY constraint failed
IMPORT-A-ERR: sales_document_lines: FOREIGN KEY constraint failed
DIFF-A: every seeded table missing in the target (total rollback)
```

### Root causes

1. **Journal posting-guard trigger** (`trg_journal_post_insert_requires_open_period`,
   migration 009) aborts any INSERT of an already-posted entry. The guard
   is a *workflow* rule (entries are created as drafts and posted through
   the app); applied to restore it makes every real company's package
   unimportable.
2. **Alphabetical insertion order** (the payload's table order) violates
   foreign keys: `fiscal_periods`→`fiscal_years`, `material_group_items`→
   `material_groups`/`materials`, `products`→`units_of_measure`, and the
   underscore sort puts `sales_document_lines`/`project_tasks` before
   their parents. Any single failure rolled back the **entire** import.
3. **Export company-filter fallback**: when the company filter matched
   zero rows in a table that contained data, the exporter re-read the
   table **unfiltered** — silently leaking other companies' rows into the
   package (and those rows then failed the target's FK to `companies`).
4. **Dishonest results**: `INSERT OR IGNORE` silently dropped conflicting
   rows, and `SteiResult.Rows` reported `total − failed` even when the
   transaction had rolled back and nothing was imported.
5. `schema_migrations` (engine bookkeeping with target-local timestamps)
   was exported as if it were company data.

## Fixes (all in `SteiSvc` unless noted)

1. **FK-topological import order** computed from the *target* database's
   FK metadata (SQLite `PRAGMA foreign_key_list`; PostgreSQL
   `information_schema`), parents before children, deterministic
   alphabetical tie-breaks.
2. **Multi-pass insertion**: rows failing with an FK violation are queued
   and retried after later tables land (resolves self-referencing tables
   and residual ordering edges); rows still failing after a no-progress
   pass are reported as failed rows.
3. **Trigger suspension inside the import transaction**: journal posting
   triggers on `journal_entries` are captured and dropped (SQLite) /
   `DISABLE TRIGGER USER` (PostgreSQL), then restored from the captured
   definitions **before commit**. DDL is transactional on both providers,
   so a rolled-back import leaves the target schema untouched — verified:
   all 4 triggers present in the target after import, and the posted
   entry lands with `status = 1`.
4. **Fallback removed**: a company filter that matches nothing exports
   nothing for that table. No cross-company data can enter a package.
5. **Honest accounting**: `InsertIgnoreAsync` returns the affected-row
   count; `SteiResult.Rows` is the number of rows *actually inserted*
   (0 on rollback); new `SteiResult.SkippedDuplicates` counts rows that
   already existed (import never overwrites); the result message states
   the skip count, and DatWin shows a localized “Skipped duplicates” line
   (`DataExchange.SkippedDuplicates` added to En/Ar/Fr/Tr catalogs).
6. **`schema_migrations` excluded** from export (documented in the
   `Excluded` set comment alongside the identity/security tables).

The Phase 4 governance in DatWin (DATA.EXPORT permission gate +
`security_audit_log` entries) is untouched. The Excel dataset path
(`DataExportSvc`) was audited at source level: it already parses with
`CultureInfo.InvariantCulture` and is single-table scoped, so it does not
share these failure classes.

## After the fix (same harness, same seed)

```
EXPORT: Success=True Tables=23 Rows=28
PACKAGE: 23 tables, 28 rows — coverage: package row counts match the
         active company's rows in every table (no leak)
IMPORT-A: Success=True Partial=False Rows=28 FailedRows=0
DIFF-A-SUMMARY: tables compared=23 clean=23 dirty=0 missingRows=0
                extraRows=0 changedRows=0 typeOnlyChanges=0
IMPORT-B (re-import): Success=True Rows=0 — "28 row(s) already existed
                and were skipped — import never overwrites existing rows."
IMPORT-C (target row locally edited, then re-import): the local value is
                preserved (never-overwrite semantics) and the skip is
                reported instead of silently diverging.
RESULT: ROUND TRIP CLEAN
```

Arabic text, decimals (19.99 / 500.25 / 1234.56 / 85000.00), dates,
NULLs and the posted journal state all round-trip byte-identically.

## Still owed

- PostgreSQL live run of the same scenarios (topological ordering uses
  `information_schema`; trigger suspension uses `ALTER TABLE … DISABLE
  TRIGGER USER`, which needs owner rights on `journal_entries`).
- Kern.Tests execution on Windows (test host is net8.0-windows; the
  signing key is DPAPI-protected).
