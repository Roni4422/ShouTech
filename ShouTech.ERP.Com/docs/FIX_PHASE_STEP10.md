# Fix Phase — Step 10: Data Exchange (DatWin / STEI / Excel) Safety + Catalog Repair

Branch: `fix/step10-data-exchange` (based on `fix/step9-order-chain` @ `520d78c`).
Evidence: `~/workspace/research/sweep/data-exchange.md` (DX-01…DX-18),
`~/workspace/research/acceptance-sweep-report.md` §11,
`~/workspace/research/sweep/compare-data-exchange.md`.

**Scope honored:** the STEI container format/encryption (`SteiCrypto`,
manifest, signing) is untouched — this step governs its inputs and outputs.
DatWin's window shape is untouched; the only visible behavior change is one
new denial message on import (see "Comparison gate" below).

---

## 1. Per-defect closure (sweep IDs)

| ID | Severity | Status | How |
|---|---|---|---|
| DX-01 | BLOCKER | **CLOSED** | Dataset export is company-scoped end to end (§2). Proven per dataset by harness scenario (a): zero company-B rows in company A's exports, code-keyed and Guid-keyed tables alike. |
| DX-02 | BLOCKER | **CLOSED** | Catalog reconciled against the real post-Step-2/4/8 schema: 61 → 37 datasets, every one resolving to a real company-scoped table (§5). Validity proven programmatically (harness (d) + Kern.Tests fact). |
| DX-03 | MAJOR | **CLOSED** | `DATA.IMPORT` is now enforced in the service layer for STEI package import, STEI dataset import, Excel import and encrypted-workbook import (§3). Denial = catalog error `DATA-4004` + security-audit row. |
| DX-04 | MAJOR | **CLOSED** | Excel imports can no longer overwrite live balances (§4): balance columns are stripped and reported on master datasets; balance-table and document datasets are refused from spreadsheets outright. |
| DX-05 | MAJOR | **CLOSED (by catalog repair)** | The dead datasets behind the always-empty exports were removed; every remaining dataset maps to a live table. |
| DX-06 | MAJOR | **CLOSED** | Every import writes a `DATA_IMPORT` security-audit row (who/when/what/counts, rejected balance columns, skipped legacy tables); denials write `DATA_IMPORT_DENIED`. (Before this step, `DATA_IMPORT` events did not exist; only exports were audited, by Step 1.) |
| DX-07 | MAJOR | Open (owner flag) | Self-referential package signing is a format property of the proven STEI engine; out of the mandated boundary ("format/encryption not redesigned"). |
| DX-08 | MAJOR | Partially mitigated | Full-package export now also scopes child tables through their parents (§2), removing one class of cross-company import failures. The company-remap rule itself (code-vs-Guid fragility, `SourceCompanyCode` carrying a Guid) is engine behavior — flagged, not redesigned. |
| DX-09 | MINOR | Open (owner flag) | `BkpServ` still passes the hardcoded company `"001"` to full export/import. Left untouched: backup/restore is governed by `BACKUP.RESTORE`, and changing the company argument changes backup semantics beyond this step's mandate. |
| DX-10 | MAJOR | **CLOSED** | STEI import now repairs PostgreSQL sequences (`setval` past the max imported id for every imported table with a `nextval` identity default) inside the import transaction; failures are reported through `AppHost.Errors` without rolling the import back. |
| DX-11 | MAJOR | Open (owner flag) | Workbooks carry no installation marker; adding one is a workbook-format change. Mitigated indirectly: imports are permission-gated, company-forced per row, and audited. |
| DX-12 | MINOR | Open | BLOB columns are still skipped by the Excel reader (base64 round trip not implemented). |
| DX-13 | MAJOR | **CLOSED (by catalog repair)** | The non-round-tripping phantom datasets (valuation/batches/serials/HR/vouchers/…) were removed from the catalog; every remaining dataset round-trips through its real table. |
| DX-14 | MINOR | Open | Export of an empty dataset is still blocked by design in the window; unchanged. |
| DX-15 | MINOR | Partially closed | The duplicate SystemInfo column-header logic was left as is; the export model now uses the live UI direction (DX-17) and per-dataset package names are unique (DX-18). |
| DX-16 | MINOR | Partially closed | DatWin's stale comment claiming permission enforcement in the catalog was corrected; the window now pre-checks `DATA.IMPORT` before opening the file dialog, mirroring the existing `DATA.EXPORT` pattern (service layer remains the real gate). |
| DX-17 | MINOR | **CLOSED** | `DataExportSvc` no longer hardcodes `Rtl = true`; the export model uses `LangDirSv.IsRtl(App.CurrentLanguageCode)`. |
| DX-18 | MINOR | **CLOSED** | Dataset package file names are unique per dataset (`stei-dataset-<key>.stei`) instead of one shared name. |
| Step-2 flag F-3 | — | **CLOSED** | Old packages can no longer resurrect dropped legacy tables (§3.3): `accounts`, `customers`, `suppliers`, `bank_accounts` found in an incoming package are skipped — not created, not counted as failures — named in the result message and in the `DATA_IMPORT` audit row. Full-package export no longer offers them either. Proven by harness scenario (e). |

---

## 2. Company-scoping mechanism (mandate 1)

The schema has a dual company-key reality (documented by Steps 2/4/9): most
tables key rows by the company **code** (`A01`), while five FK-declaring
tables key by the company's **internal Guid** (`chart_of_accounts`,
`fiscal_years`, `brands`, `product_categories`, `units_of_measure`).
Scoping therefore always matches **the company's own two keys — code OR
Guid — and never another company's**.

### 2.1 Dataset exports (`DataExportSvc.BuildScopedExportQueryAsync`)

Every dataset export is built as a parameterized query:

- **Company-keyed tables:** `WHERE CAST(company_id AS TEXT) IN (@scopeCode,
  @scopeGuid)` (one parameter when the keys coincide). CAST normalizes Guid
  storage differences between SQLite (TEXT) and PostgreSQL (uuid) — the same
  idiom the STEI engine already uses.
- **Party split:** `Customers`/`Suppliers` read the one canonical `parties`
  table with an added `kind = 'C' / 'S'` predicate; `CustomerGroups`/
  `SupplierGroups` do the same over `party_groups`. A customer export can
  therefore never contain a supplier (or the other company).
- **Document kinds:** `Sales`/`SalesReturns`/`Purchases`/`PurchaseReturns`
  read `sales_documents`/`purchase_documents` with the `doc_kind` sets the
  document layer itself uses (`SAL,POS` / `SRT` / `PUR` / `PRT`).
- **Line tables** (no company column of their own) are scoped through their
  parent: `doc_id IN (SELECT doc_id FROM sales_documents WHERE …)` (and the
  journal equivalent).
- **Companies table:** the `Company` dataset exports exactly the active
  company's row (`code = @scopeCode`).
- **Fail-closed:** a dataset whose table has no company key and no scoped
  parent join throws instead of exporting unfiltered. The old
  export-everything fallback (and the import-side company-filter fallback in
  the STEI engine) were already removed by earlier steps; this step removes
  the last unfiltered read (`SELECT *` in `DataExportSvc`).

### 2.2 STEI full-package exports (`SteiSvc.ReadRowsAsync`)

Tables with a `company_id` keep the engine's existing dual-key filter.
This step adds the missing half: **company-less child tables are scoped
through their parents** (`BuildChildScopeWhereAsync`) — for every
single-column foreign key whose parent table carries `company_id`, a row
travels only when its parent belongs to the active company. Before this,
child tables traveled whole: another company's document/journal lines leaked
into the package and then failed their foreign keys on import (their parents
are company-filtered out), rolling the entire import back. Genuinely global
tables (no company-scoped parent, e.g. `permission_groups`) still travel
whole; identity tables (`users`, `roles`, `user_roles`, …) remain excluded
as before.

### 2.3 Imports

Imports were already company-forcing per row (the importer stamps the target
company key onto every row, in the correct code-vs-Guid shape — the shape map
was corrected for the five Guid-keyed tables, which previously received the
company *code* in a Guid foreign-key column). Rows whose own `doc_kind` /
party `kind` contradicts the chosen dataset are now refused per row instead
of being imported under the wrong dataset.

---

## 3. Import gating + audit (mandate 2)

### 3.1 The gate

New `Services/DataExchangeGuard.cs`, following the Step-1 pattern for
destructive operations:

- `RequireImportPermission(operation)` checks
  `AppHost.Permissions.Has(PermCat.Data.Import)` (`DATA.IMPORT`).
- On denial it reports catalog error **`DATA-4004`** through
  `AppHost.Errors`, writes a **`DATA_IMPORT_DENIED`** security-audit row
  (best-effort, never masking the denial), and throws `AppError(DATA-4004)`.
- The gate lives in the **service layer**, at the entry of every import
  entry point: `SteiSvc.ImportAsync`, `SteiSvc.ImportDatasetAsync`,
  `DataExportSvc.ImportExcelAsync`, `DataExportSvc.ImportEncryptedAsync`.
  DatWin additionally pre-checks before its file dialog (mirroring its
  existing `DATA.EXPORT` pattern) and shows the localized denial.
- Backup **restore** (`BkpServ.RestoreBackupAsync`) deliberately bypasses
  the data-exchange gate via the internal `ImportForRestoreAsync`: it stays
  governed by `BACKUP.RESTORE`, the stricter, purpose-built permission
  Step 1 wired. Simulations (dry runs) are not gated — they write nothing.

### 3.2 The audit

Every completed import writes one **`DATA_IMPORT`** row to
`security_audit_log` through the existing `SecurityAuditLog.WriteAsync`:
acting user, company, timestamp, and a details string carrying the scope
(full package / dataset key), source company, inserted / skipped-duplicate /
failed counts, skipped legacy tables, and rejected balance columns.
Audit-write failures are reported through `AppHost.Errors` and never fail
the import itself.

### 3.3 Legacy-table governance

`DataExchangeGuard.LegacyDroppedTables` = `accounts`, `customers`,
`suppliers`, `bank_accounts` (dropped by migrations 059/061, or retired by
Step 8). During package/dataset import these tables are partitioned out
**before** table creation: their rows are skipped (counted separately, not
as failures), the tables are never recreated, and the skip is named in the
import result message and the audit details. Full-package export skips them
too, so a package produced by the current build never carries them.

---

## 4. Excel balance-import rule (mandate 3)

**Chosen rule: reject — never adjust silently.** Balances and stock
quantities change only through posted movements (the Step-6 valuation
engine). A spreadsheet is master data, not a posting channel. The rule is
enforced per dataset policy (`DatasetImportPolicy` on every catalog entry):

| Policy | Datasets | Excel rule |
|---|---|---|
| **Master** | Products, Accounts, Customers, Suppliers, groups, Warehouses, Currencies, SalesReps, Brands, Categories, Units, Banks, CostCenters, Projects, FiscalYears | Imported (upsert or Replace). **Balance columns are stripped per row, never written**, collected into `ExcelImportResult.RejectedBalanceColumns`, surfaced in the result's skipped-columns list as `(balance - not imported)`, and named in the import audit. Stripped columns: `materials.qty_on_hand`, `materials.avg_cost` (derived caches owned by the valuation engine), `sales_rep_targets.actual_sales`, `sales_rep_targets.commission_earned` (derived actuals). (`chart_of_accounts` needs no entry: migration 059 already dropped its cached balance columns — the sweep's `accounts.balance` case is gone with the table.) |
| **EngineOwned** | OpeningStock (`material_stock_balances`), CostLayers (`stock_cost_layers`) | **Refused** with catalog error **`DATA-4005`**. These tables *are* balances/layers; there is no master part to import. Opening balances enter through the opening-stock posting flow, layers through goods receipts. |
| **PackageOnly** | Company, Sales/SalesReturns/Purchases/PurchaseReturns + their lines, StockMovements, ProductionOrders(+Lines), JournalEntries(+Lines), Cheques | **Refused** with catalog error **`DATA-4006`**. Documents and journals are ledger/stock facts; they exchange only as signed, encrypted STEI dataset packages (which re-validate on import), never as raw spreadsheet rows. Replace mode is likewise Master-only. |

The alternative (applying imported balances as adjustment movements through
the valuation engine) was considered and **not** chosen: a bulk spreadsheet
is not a stock-count document, and fabricating movements from it would post
unverifiable quantities into the ledger. If the owner wants spreadsheet
opening balances, the correct vehicle is the opening-stock/count flow, not
the dataset importer. **Owner flag:** confirm this rule.

---

## 5. Catalog reconciliation (mandate 4)

61 seeds → **37 datasets + SystemInfo**. Every kept dataset resolves to a
real table with a real company key (proven programmatically, §7d).

### Kept unchanged (13)
Company (`companies`), Products (`materials`), Accounts
(`chart_of_accounts`), Warehouses, Currencies, SalesReps, SalesRepTargets,
Banks (`banks`), CostCenters, Projects, FiscalYears, StockMovements
(`material_stock_movements`), Units (`units_of_measure`).

### Fixed mappings (12)
| Dataset | Was | Now |
|---|---|---|
| Customers | `customers` (dropped 061) | `parties` kind `C` |
| Suppliers | `suppliers` (dropped 061) | `parties` kind `S` |
| CustomerGroups | `customer_groups` (never existed) | `party_groups` kind `C` |
| SupplierGroups | `supplier_groups` (never existed) | `party_groups` kind `S` |
| Sales | `commercial_documents` (retired Step 9) | `sales_documents` kinds SAL,POS |
| SalesLines | `commercial_document_lines` (retired) | `sales_document_lines` via parent |
| SalesReturns | `commercial_documents` | `sales_documents` kind SRT |
| Purchases | `commercial_documents` | `purchase_documents` kind PUR |
| PurchaseLines | `commercial_document_lines` | `purchase_document_lines` via parent |
| PurchaseReturns | `commercial_documents` | `purchase_documents` kind PRT |
| OpeningStock | `material_balances` (never existed) | `material_stock_balances` |
| CostLayers | `stock_layers` (never existed) | `stock_cost_layers` |

### Added (3) — closing the acceptance report's CORE gap
JournalEntries (`journal_entries`), JournalEntryLines
(`journal_entry_lines` via parent), Cheques (`cheques`). Journal entries —
the system's core ledger fact — previously had **no** dataset at all.

### Removed (27) — tables that do not exist in the current schema
Branches, AccountTypes, ProductClassAttributes, ProductUnits,
CustomerContacts, CustomerAddresses, SupplierContacts, Employees,
Departments, JobTitles, WarehouseLocations, WarehouseZones, WarehouseTypes,
TaxCodes, StockAdjustments, StockAdjustmentLines, StockReservations,
Batches, SerialNumbers, ValuationMethods, ProductValuation, ValuationHistory,
WorkCenters, ReceiptVouchers, PaymentVouchers, PosSessions. (BankAccounts was
already removed by Step 8.) Each was a seed pointing at a table no migration
creates — the datasets could never appear or work (DX-02/DX-05/DX-13). If
HR/batches/valuation modules land later, their datasets return with real
tables. The Arabic catalog's dataset names were pruned to match.

---

## 6. Pillars checklist (data-exchange surface)

| Pillar | Verdict | Evidence |
|---|---|---|
| UI | PROVEN (unchanged by design) | DatWin shape untouched (owner's rebuild); one new localized denial message (`DataExchange.ImportNotPermitted` ×4 languages). Window-context audit exit 0. |
| DB | PROVEN | No new migration (none needed — catalog/gate/audit reuse existing tables). Migrator idempotent double-run in the harness (3 fresh DBs + re-run). |
| Linking | PROVEN | Catalog datasets resolve to the canonical Step-2/4/8 tables (parties, chart_of_accounts, banks, sales/purchase documents); line datasets link through their parents on export and import. |
| Posting | PROVEN (by refusal) | The importer posts nothing: documents/journals/balances are refused from spreadsheets (DATA-4005/4006) so no unposted or double-posted facts can enter; STEI package import remains insert-only inside one transaction (rollback on any failure — harness-verified). |
| Persistence | PROVEN | Headless harness against the real service code: exports, imports, audit rows, balance strip, legacy skip all asserted in SQLite (§7). |
| Downstream | PROVEN | Import audit rows land in `security_audit_log` (the Activity Log surface); rejected balance columns surface in the import result and audit details; skipped legacy tables surface in the result message. |

## 7. Proof — headless harness (SQLite, real service code)

Harness: `~/workspace/research/fix-step10/` — compiles the **real**
`Backup.cs`, `DataExportSvc.cs`, `DataExchangeGuard.cs`, `XlsxWrt.cs` and the
real Domain project (only platform shims: DPAPI pass-through, a fixed
harness encryption key, SQLite-only runtime). **14/14 checks PASS:**

- **(a)** Two companies (code-keyed + Guid-keyed rows, kind-split parties,
  documents, journals, balances): exporting **every** dataset for company A
  contains **zero** company-B marker rows; per-dataset counts asserted
  (Customers=1/Suppliers=1 kind split, lines scoped via parents, Company=1).
- **(b)** Import without `DATA.IMPORT` → refused with **DATA-4004**, nothing
  written, `DATA_IMPORT_DENIED` audit row present. With permission → import
  succeeds and a `DATA_IMPORT` audit row carries `dataset=Products` and the
  row counts.
- **(c)** Balance workbook (`qty_on_hand=9999`, `avg_cost=999`) → master
  fields imported, live balances **unchanged (50 / 10)**, both columns
  reported rejected and named in the audit. OpeningStock → **DATA-4005**,
  balances untouched. Sales → **DATA-4006**, nothing written.
- **(d)** Programmatic catalog validity: exactly 37 seeds; every seed's
  table exists in the migrated schema and is company-scoped; legacy tables
  absent from schema and catalog.
- **(e)** STEI round trip on the current schema: full package DB1→DB2 lands
  company A's rows (material, Guid-keyed brand, sales doc + line), **zero**
  company-B rows, import audited; dataset package round trip into DB3
  carries A's material only. A hand-built legacy package containing the
  dropped `accounts` table imports successfully, does **not** recreate
  `accounts`, still imports its live table, and names the skip in the
  result message and the audit row.
- **(f)** No migration added; migrator double-run idempotent (verified in
  the harness across three fresh databases).

Kern.Tests: `DataExchangeStep10Tests` (7 facts mirroring the harness at the
service layer) + the pre-existing `SteiRoundTripTests` updated to grant the
session the import permission. **Test execution is owed on Windows**
(net8.0-windows test host, DPAPI) — the tests compile clean; the harness
above is the executed proof on this machine.

## 8. Gates

| Gate | Result |
|---|---|
| Full build `dotnet build ShouTech.sln --no-incremental` | **0 errors, 0 warnings** |
| `tools/audit_window_context.py` | **exit 0** |
| Lang parity | **missing = 0 in all four catalogs**; new key `DataExchange.ImportNotPermitted` present ×4; error catalogs En/Ar/Fr/Tr at 61 entries each. (`verify_english_source.py` exits 1 on pre-existing Ar/Fr/Tr *extra* keys — identical profile on the base branch; this step adds no extras and reduces Ar extras 114 → 90 by pruning dead dataset names.) |
| Migrator idempotent double-run | PASS (harness, 3 DBs) |
| Harness scenarios (a)–(e) | **14/14 PASS** |

## 9. Comparison gate

DatWin is **not** materially redesigned: no layout, shape, or workflow
change. The only visible behavior change is that an import attempted without
`DATA.IMPORT` now shows a localized denial message (and imports that would
previously have silently overwritten balances or failed against phantom
tables now either work against real tables or are refused with a catalog
message). Per the task's condition, the comparison table from
`compare-data-exchange.md` is therefore not reproduced; the CORE-parity gaps
it lists that this step closes are: journal-entries dataset missing (added),
phantom HR/manufacturing/valuation datasets (removed as non-existent).

## 10. Owner flags / owed verification

1. **Balance rule = reject** (§4) — confirm, or direct an opening-stock
   import flow instead.
2. **Documents via STEI packages only** (DATA-4006) — spreadsheet document
   import is refused by design.
3. DX-07 (self-referential signing), DX-09 (`BkpServ` hardcoded `"001"`),
   DX-11 (no installation marker in workbooks), DX-12 (BLOB columns),
   DX-14 (empty-dataset export) remain open — each needs a format or
   product decision beyond this step's boundaries.
4. **Owed:** WPF run of DatWin (denial message + import flows) and the
   Kern.Tests suite on Windows; live PostgreSQL pass of the scoped exports
   and the sequence repair (SQL is provider-shared and CAST-based; the
   SQLite path is harness-proven).
5. Known pre-existing constraint surfaced by the harness: the Excel upsert
   evaluates NOT NULL columns on the proposed row before conflict routing
   (both providers), so hand-made sparse workbooks must include NOT NULL
   columns — real exports always carry all columns, so round trips are
   unaffected.
