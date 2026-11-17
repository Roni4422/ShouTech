# FIX PHASE — STEP 2: Unify the Chart of Accounts

**Branch:** `fix/foundations-2-coa` (worktree `~/workspace/projects/ShouTech-fix2`)
**Base:** tip of `fix/foundations-1-rbac` (`fb56fbc`) — the foundations chain is sequential.
**Date:** 2026-11-17 · **Status:** implemented, gated, NOT pushed, NOT merged.
**Evidence base:** `~/workspace/research/sweep/journal-coa.md`,
`~/workspace/research/sweep/compare-journal-coa.md`,
`~/workspace/research/acceptance-sweep-report.md`.

---

## 1. The defect, in one paragraph

The app kept **two charts of accounts with no synchronization**.
`accounts` (SQLite mig 012 / PG mig 014) — integer `account_id`, `company_id` =
company **code** — was written by company-creation seeding, `CoaWin.SeedDefaultChart`
and `AccRepo.Save`, and read by CoaWin/AccCard and the Trial Balance; it carried
cached `balance/debit/credit` columns **nothing maintained** (C-2).
`chart_of_accounts` (SQLite mig 009 / PG mig 011) — Guid `account_id`,
`company_id` = company **Guid** — was read by `JournalRepository.ResolveAccountIdAsync`,
the posting triggers, `ChequeRepository` and `MasterCardRepository`, while its only
writer (`AccountingStructureRepository.CreateAccountAsync`) had **no UI caller**.
On a fresh company the chart existed only in `accounts`, so JrnWin Save threw
*"Account code '131' is missing…"* (J-1/C-1, harness-proven in the sweep): the
journal was unusable with the visible chart. GL reports read the cached counters
and/or joined integer↔Guid keys, producing fiction or empty results (R-1/R-2).
The account lifecycle guard compared journal lines' Guid strings against the
`accounts` integer Id, so an account with posted movements could be deleted (C-3).

## 2. Decision: the canonical store is `chart_of_accounts`

**`chart_of_accounts` (Guid-keyed) is the one canonical chart of accounts.**
The legacy `accounts` table is **dropped** by migration 059 after its data is
merged in. Rationale, weighed from the evidence:

| Factor | `accounts` | `chart_of_accounts` | Verdict |
|---|---|---|---|
| Posted history | No journal line references it | `journal_entry_lines.account_id` **is** its Guid; posting triggers (SQLite mig 009 / PG 011 `shouttech_validate_journal_post`) and fiscal close validate against it | Chart wins decisively — unifying here rewrites **zero** posted history |
| Schema discipline | Free-text `posting_mode`/`status`, cached counters, no type column (type guessed from code prefixes — R-2's root cause) | `account_type` enum (ASSET/LIABILITY/EQUITY/REVENUE/EXPENSE), boolean flags, FK to `companies`, unique (company, code) | Chart |
| UI gravity | CoaWin/AccCard fields (name_ar, risk, tax_rate, reconcile/taxable flags, budget, is_default, description, audit columns) exist only here | Lacks those fields | Tie-breaker: the fields are **carried onto the chart** as extension columns (§3) — gravity moves, history doesn't have to |
| Other readers | Trial Balance (rewritten, §5) | Cheques, master cards, bank rec, fiscal close | Chart |

The alternative (make `accounts` canonical) would have required rewriting every
posted `journal_entry_lines.account_id` from Guid to integer and re-pointing the
posting triggers — i.e. rewriting accounting history to save a UI table. Rejected.

**Why drop, not a deprecated view:** a compatibility view cannot synthesize the
legacy integer `account_id`s honestly (they never had a stable mapping to Guids),
and any surviving writable-looking surface invites a future dual-write. After 059
there is exactly **one writer path** (`AccRepo` + `AccountingStructureRepository.CreateAccountAsync`
+ company-creation seeding, all on `chart_of_accounts`) and **one reader path**
(every former `accounts` reader re-pointed, §5). No code in the tree references
the `accounts` table after this step (verified by repository-wide search; the only
remaining matches are the loyalty table `loyalty_accounts`, migration history
files, and this documentation).

## 3. Migration 059 — `059_coa_unification.sql` (SQLite + PostgreSQL)

One transactional migration per provider, numbered **059** (053 stays reserved).
Idempotent: every statement is guarded (`-- @if-column-missing` directives on
SQLite, `ADD COLUMN IF NOT EXISTS` / catalog checks on PostgreSQL, terminal
`DROP TABLE IF EXISTS`), and the migrator's checksum ledger skips it on re-run —
verified by double-run on both an empty database and a populated pre-unification
fixture (§7e).

### 3.1 Schema changes to `chart_of_accounts`

Extension columns carried over from `accounts` (the UI's field set):
`name_ar`, `risk` (default `'Low'`), `cost_center_mode` (default `'Unspecified'`),
`allow_reconcile`, `taxable`, `foreign_currency`, `cost_required`, `budget`,
`is_default`, `tax_rate`, `description`, `updated_at`, `deleted_at`, `deleted_by`.
The cached `balance`/`debit`/`credit` columns are **deliberately not carried
over** — balances are computed from posted lines everywhere (§5); nothing may
read a stored balance for reporting, and after 059 none exists to read.

### 3.2 Data mapping (explicit)

For every non-deleted `accounts` row:

- **Company resolution (code → Guid):** `accounts.company_id` holds the company
  **code**; the row joins `companies` on `company_code = accounts.company_id`,
  falling back to a direct `company_id` match. Rows whose company cannot be
  resolved are **not migrated** (they belong to no company the registry knows;
  the fixture in §7e proves the skip is silent-safe and loss-free for real rows).
- **Identity (int → Guid):** if a chart row already exists for (company, code),
  it **keeps its existing Guid** — this is what preserves every
  `journal_entry_lines` link, because journal lines only ever referenced chart
  Guids. Accounts-only rows receive a **fresh Guid** (SQLite: the mig-009
  randomblob v4 pattern; PostgreSQL: deterministic
  `md5(company || ':059:' || code)::uuid`, so a re-run converges instead of
  duplicating; `ON CONFLICT DO NOTHING` backs both passes).
- **Field precedence on duplicates:** the chart row keeps its `account_id` and
  `account_type`; the `accounts` row wins for descriptive fields and flags
  (names, risk, tax, reconcile, status) because the UI maintained them there.
- **Derived fields for accounts-only rows:** `account_name` = `name_en`
  (fallback `name_ar`); `is_postable` = `posting_mode <> 'Summary'`;
  `is_active` = `status <> 'Disabled'`; `is_deleted` = OR of both stores;
  `account_type` derived from the code root the seed scheme already uses:
  1→ASSET, 2→LIABILITY, 3→EQUITY, 5→REVENUE, everything else (4 Purchases,
  6 Expenses, custom roots) → EXPENSE. **Owner flag F-2** (§9).
- **Parents:** `parent_code` is resolved to the parent's Guid in a second pass,
  after all rows exist, so ordering never matters.
- Finally `accounts` is dropped (SQLite also drops its indexes first).

### 3.3 Companion change inside 059: fiscal periods backfill

Mandate 2 (journal must save **and post** on a fresh company) exposed a second
fresh-company gap: company creation produced a fiscal **year** but no fiscal
**periods**, and the posting engine requires an open period containing the entry
date. Migration 059 backfills monthly OPEN periods tiling each open fiscal
year's actual start..end dates (≤13 periods, respecting the
`period_number BETWEEN 1 AND 13` check) for every year that has none, and
company creation now seeds the same periods in the creation transaction
(§4). Years that already have periods are untouched.

## 4. Company creation seeds the canonical chart — one place

`Services/Company.cs`: both provider paths now insert the template chart into
`chart_of_accounts` in two passes (rows with NULL parent, then parent Guid
updates), keyed by the new company's **Guid**, with `account_type` from the
shared `AccRepo.AccountTypeForCode` derivation, and seed the fiscal periods via
the shared `BuildFiscalPeriods` helper (SQLite + PostgreSQL). `CoaWin.SeedDefaultChart`
(the empty-chart fallback) already wrote through `AccRepo`, so it lands in the
canonical store unchanged in shape. There is no remaining `INSERT INTO accounts`
anywhere in the tree.

## 5. One writer / one reader path — what moved

| Surface | Before | After |
|---|---|---|
| `AccRepo` (CoaWin/AccCard data layer, `Services/Accounting.cs`) | CRUD on `accounts` (int Id, code-keyed company) | Fully rewritten on `chart_of_accounts`. `AccRec.Id` is now the Guid string. Balances/Debit/Credit are **computed at load** from POSTED lines (status = 1) — C-2 closed at the source. Parent linkage is the Guid FK: renaming a code needs no descendant re-pointing; renumbering rewrites descendant codes + derived types transactionally. Company resolved code→Guid via `companies` per call (shell context honored at use time) |
| `Company.cs` seeding | `accounts` inserts, no periods | Canonical chart + periods, both providers (§4) |
| `RptQuery` GL family | TB read cached counters & ignored dates (R-1); BS/P&L joined `CAST(int AS TEXT)` = Guid (R-2); statement compared code to Guid ids and printed entry Guids as DocNo (R-3); CC trial same broken join (R-4); journal list printed Guids (R-5) | **All** GL queries rewritten onto posted `journal_entry_lines` joined to the chart on Guids, company-scoped via `companies` (the shell passes the code; `journal_entries.company_id` is the Guid). Trial Balance honors [from, to); BS/P&L classify by `account_type`, not code prefixes; statement filters by `account_code` and shows `document_number`; dormant accounts, cash position/flow, account comparison, cash journal, bank GL balances likewise. Window contracts (aliases) unchanged, so report windows needed no edits |
| `CardStore.BankBookBalance` | Cached-column read | Posted-lines sum via chart + companies |
| `RecycleBinSvc` | Two table defs (`accounts` + chart), chart def mis-keyed by code so deleted accounts never listed | Single chart def, Guid-resolved; deleted accounts list/restore correctly |
| `DataExportSvc` (DatWin datasets) | `accounts` dataset listed first, code-scoped | Accounts dataset resolves to `chart_of_accounts` |
| `OperationsRepository` (BankRecWin) | Tried `accounts` first (int ids), then chart | Chart only; new `ResolveChartCompanyAsync` maps the caller's company **code** to the Guid for chart/journal queries (marks stay code-keyed, as stored) |
| `JournalRepository` | — | **Untouched.** Its account resolution already read the chart; unification removes the mismatch instead of moving the journal (per mandate scope) |

## 6. Per-defect closure

| ID | Defect | Closure | Proof |
|---|---|---|---|
| J-1 / C-1 | Fresh company: JrnWin Save throws "Account code '131' is missing…" — journal unusable with the visible chart | Chart unified on the store the journal resolves against; UI writer (`AccRepo`), company seeding and journal reader now share it; fiscal-period gap fixed in 059 + creation | Harness (a)+(b); Kern.Tests `FreshCompany_UiChart_JournalSavesAndPosts` |
| C-2 | Cached `accounts.balance/debit/credit` maintained by nothing; card tiles and reports show fiction | Columns dropped with the table; `AccRec` balances and **all** GL reports compute from POSTED lines | Harness (c); Kern.Tests `AccRecBalances_AreComputedFromPostedLines_NotStored`, `Reports_ComputeFromPostedLines` |
| C-3 | Lifecycle guard compares Guid journal refs to integer Id → posted-to accounts deletable | Guard rewritten on the unified model: live posted totals **by Guid**, any-line reference count, and the generic reference scan now matches Guid-or-code text. Posted-to ⇒ cannot delete/disable | Harness (d); Kern.Tests `LifecycleGuard_BlocksPostedAccount_AllowsUnusedAccount` |
| R-1 | Trial Balance reads cached columns, ignores From/To | Recomputed from posted lines in [from, to) | Harness (c) incl. June-window zero check |
| R-2 | BS/P&L int↔Guid join → always empty | Guid joins + `account_type` classification | Harness (c) |
| R-3 | Account Statement compares code to Guid ids; Guid printed as DocNo | Filter by `account_code`; DocNo = `document_number` | Harness (c) |
| R-4 | Cost Center Trial same broken join | Rewritten on chart Guids + companies scoping | Code + build; runtime proof owed on Windows (report-family pass) |
| R-5 | Journal List shows Guids as DocNo | DocNo = `document_number` (fallback entry id) | Code + build; runtime proof owed on Windows |

Out of scope and untouched, as mandated: J-2 (invoice GL posting, step 3),
J-3 (cost-center validation, step 3), J-5 (base-currency lines, step 7),
J-6/J-8/J-9 (journal workflow).

## 7. Proof

### 7.1 Headless harness — `~/workspace/research/fix-step2/coa_harness.py` — **28/28 PASS**

Runs the **real SqlMigrator** (fix1 build for the pre-059 fixture, fix2 build for
the upgrade) and replays the app's own SQL shapes; GL reports execute with SQL
extracted **verbatim** from the new `RptQuery.cs`, and the lifecycle guard with
SQL extracted verbatim from the new `AccRepo`.

- **(a)** Company created via the app's seeding shapes → 11-account chart present
  in `chart_of_accounts`; no `accounts` table exists.
- **(b)** Codes 131/51/61 resolve through `JournalRepository`'s resolution SQL;
  draft inserts and **posts** (real posting triggers validate period/balance).
- **(c)** Trial Balance Cash = Dr 1000 / Cr 250 / Bal 750 = hand-computed; TB
  totals balance (1250 = 1250); June-only window all zeros (dates honored);
  Balance Sheet Cash 750; P&L Sales 1000 / Rent 250; Account Statement for code
  131 returns both lines with DocNos `JV-H-1`/`JV-H-2`; cash-flow sections
  classify the 750 net movement.
- **(d)** Guard SQL sees posted totals 1000/250 on account 131 → refused;
  unused account 132 has zero totals and zero references → allowed.
- **(e)** Pre-unification fixture (company + rows in **both** tables + a posted
  journal): 059 applies; second migrator run is a clean no-op; `accounts` gone;
  canonical codes = union {13, 131, 132, 51, 61} — nothing lost; the posted
  line still points at the **original Cash Guid**; duplicate 131 kept the chart
  identity and gained the UI fields (tax_rate 5.0, risk Low); accounts-only rows
  typed by code root (132→ASSET, 61→EXPENSE); Disabled→inactive; parent 131→13
  linked; orphan row (unresolvable company) skipped; no `balance` column exists.

### 7.2 Kern.Tests — `tests/Kern.Tests/CoaUnificationTests.cs` (6 facts)

`FreshCompany_UiChart_JournalSavesAndPosts` (real `JournalService` +
`JournalRepository` + `AccRepo`-seeded chart + real fiscal periods) ·
`Reports_ComputeFromPostedLines` (real `RptQuery` TB/BS/P&L/statement vs the same
hand-computed values, incl. the date-range check) ·
`AccRecBalances_AreComputedFromPostedLines_NotStored` ·
`LifecycleGuard_BlocksPostedAccount_AllowsUnusedAccount` (incl. `AccRepo.Delete`
throwing on the posted account and succeeding on the unused one) ·
`AccRepo_RoundTrip_PreservesTypeParentAndFlags` (incl. guarded renumber keeping
children attached) · `Migration_LeavesOneChartStore_AndDoubleRunIsIdempotent`.
**Execution is owed on Windows** (net8.0-windows testhost cannot run on this
Linux VM) — the project compiles at 0 errors / 0 warnings here.
`SteiRoundTripTests` was updated to assert fidelity (Arabic text, decimal, NULL)
on the canonical chart row instead of the dropped table.

### 7.3 What is NOT proven here

WPF runtime behavior (windows opening, clicking through JrnWin on a fresh
company) and **live PostgreSQL** execution of migration 059 and the rewritten
queries are owed on Windows / a live PG instance — no PG server exists in this
VM. The PG migration uses only standard idempotent constructs and mirrors the
SQLite logic statement-for-statement, but it has not been executed. This is
stated plainly per the standing rule: build + SQLite harness success is not
product readiness.

## 8. Gates

| Gate | Result |
|---|---|
| Full solution build `--no-incremental` | **0 errors, 0 warnings** |
| `tools/audit_window_context.py` | **exit 0** |
| Lang parity (En/Ar/Fr/Tr key sets, both catalog families) | **0 missing** (no keys added or removed) |
| Migrator idempotent double-run (SQLite) | PASS — empty DB and populated fixture (§7.1e) |
| Arabic outside Ar.json | None added (migration/test fixtures contain one Arabic sample string inside a **test** seed, matching the pre-existing STEI test's fidelity purpose; product code and migrations contain none) |
| Repository-wide legacy-table sweep | No `accounts` table references remain in product code |

## 9. Owner flags (design decisions surfaced, not silently taken)

- **F-1 — Fiscal periods (scope note, completed):** mandate 2 was impossible
  without periods; 059 backfills them and creation seeds them. The
  profile-edit path (`UpsertFiscalYear*` in `Company.cs`) still creates fiscal
  years **without** periods — left for the owner/step that owns company-profile
  editing.
- **F-2 — `account_type` derivation for accounts-only rows:** code roots
  1/2/3/5 map to ASSET/LIABILITY/EQUITY/REVENUE; **all other roots default to
  EXPENSE** (covers 4 Purchases and 6 Expenses in the seed scheme). If custom
  roots exist in the field with different natures, the owner should supply the
  mapping; affected rows are exactly the accounts-only rows of a pre-059 DB.
- **F-3 — Old STEI packages:** importing a pre-059 `.stei` package containing an
  `accounts` dataset will recreate that table as an orphan nothing reads
  (generic table import). Dataset governance belongs to the data-exchange step
  (sweep D-3); the export side already emits `chart_of_accounts`.
- **F-4 — AccCard serial display:** the card's read-only serial field showed the
  integer Id; it now shows a short fragment of the account Guid. Cosmetic only.
- **F-5 — Untouched sweep MINORs in these windows:** C-4/C-5 (localization and
  `"Header"` context-menu literals), C-7 (no New-Root button in CoaWin), C-8
  (seed writes English into `name_ar`), C-9 (Statement/Ledger open the same
  window), C-10 beyond the serial fragment. They are outside this step's
  mandate and are listed as GAPs in §11 rather than silently fixed or hidden.

## 10. Comparison tables (CoaWin / AccCard / JrnWin vs counterparts)

Condensed from `~/workspace/research/sweep/compare-journal-coa.md` (the full
audit, incl. SAP B1, remains the reference), with the **Step 2** column recording
exactly what this step changed. ⚠ = counterpart behavior asserted from product
knowledge in the sweep, not live-verified.

### 10.1 CoaWin — chart of accounts manager

| Capability | ShouTech before | Al-Ameen V10 ⚠ / Phenix v10 ⚠ | ERPNext | Odoo | **Step 2** |
|---|---|---|---|---|---|
| Tree / hierarchy | ✓ | ✓ | ✓ | ✗ flat + groups | unchanged (now fed from the canonical chart) |
| Search / filter | ✓ | ✓ | ✓ | ✓ | unchanged |
| Add Child / Edit (AccCard) | ✓ | ✓ | ✓ | ✓ | unchanged — but writes now land in the store the journal reads |
| Add Root | ✗ | ✓ | ✓ | ✓ | GAP (C-7, flag F-5) |
| Delete / Disable | ◐ in AccCard, guard blind (C-3) | ✓ blocked once used | ✓ blocked once used | archive | **Guard fixed** — posted-to accounts cannot be deleted/disabled (C-3) |
| Import / Export tree | ✓ CSV/XLSX/`.sta` | ✓ ⚠ | ✓ Importer | ✓ CSV | unchanged; upsert-by-code now targets the canonical store |
| Account Type / Root Type per node | ✗ — no type column; reports guessed from code prefixes | ✓ | ✓ Root Type + Account Type | ✓ Type | **Type now exists and is authoritative** (`account_type` on every row; reports classify by it). Tree still doesn't *display* it — display GAP |
| Balance per node | ✗ | ✓ ⚠ | ✓ | ✓ | **Data now real** (balances computed from posted lines in `AccRepo`); tree column still absent — display GAP |
| Drill to Statement | ◐ report couldn't resolve the account (R-3) | ✓ | ✓ | ✓ | **Fixed** — statement resolves by code (R-3) |
| Move / reparent | ◐ string-parsed parent, desync on rename (C-6) | ✓ ⚠ | ✓ | n/a | **Fixed structurally** — parent is a Guid FK; renumber keeps children attached (proven in tests) |
| Template chart at creation | ◐ hard-coded seed into the wrong table | ✓ ⚠ | ✓ per-country templates | ✓ localization packages | Seed now lands in the canonical chart + periods (§4); template gallery still GAP |

### 10.2 AccCard — account card

| Field / behavior | ShouTech before | Al-Ameen ⚠ | ERPNext `Account` | Odoo `account.account` | **Step 2** |
|---|---|---|---|---|---|
| Code / Name AR / Name EN / Parent / Is Group / Status | ✓ | ✓ | ✓ | ✓ | unchanged, persisted to the canonical chart |
| Root Type / Report Type | ✗ inferred from prefix | ✓ | ✓ Root Type + Report Type | derived from Type | **Root Type is now stored** (`account_type`, derived from code root at save) and drives BS/P&L; a dedicated Report Type field and an editable Type picker remain GAP |
| Account Type (Bank/Cash/Receivable…) | ✗ | ✓ | ✓ ~30 types | ✓ 17 types | GAP — classification spine beyond the 5 root types is a later step |
| Live Balance / Debit / Credit tiles | ✗ cached columns, 0.00 forever (C-2) | ✓ from ledger | ✓ from GL | ✓ from journal items | **Fixed** — computed from POSTED lines at load |
| Delete / Disable guard | ✗ blind to journal (C-3) | ✓ | ✓ | ✓ | **Fixed** (§6) |
| Allow Reconcile / Taxable / Tax Rate / Cost-center mode / Budget / Is Default / Risk | ✓ (ShouTech field set) | ◐ | ◐ | ✓ reconcile | unchanged fields, now on the canonical row; enforcement of cost-center mode at posting is step 3 (J-3) |
| Currency per account | ◐ checkbox only | ✓ | ✓ Account Currency | ✓ Currency | GAP (unchanged) |
| Opening balance / Freeze / Balance Must Be | ✗ | ◐/✓ ⚠ | ✓ | ◐ | GAP (unchanged) |
| Statement / Ledger buttons | ◐ statement broken (R-3) | ✓ | ✓ | ✓ | Statement target **fixed**; both buttons still open the one window (C-9, flag F-5) |

### 10.3 JrnWin — journal entry (unchanged window; unblocked)

JrnWin itself was **not modified** — this step removes the data-layer mismatch
underneath it. The comparison deltas that move:

| Path | Before | Counterparts | **Step 2** |
|---|---|---|---|
| Save draft with a CoaWin-visible account code | ✗ throws "Account code '131' is missing…" on every fresh company (J-1) | ✓ basic function in all | **Works** — draft saves with codes from the visible chart (harness b, Kern.Tests) |
| Post | ✗ unreachable on fresh companies (no resolvable accounts, no periods) | ✓ | **Works** — posting triggers validate against the same chart; periods exist |
| Load a posted entry / account names | ◐ names resolved as a save side-effect (J-7) | ✓ | Resolution path unchanged but now reads the store the UI maintains |
| Reverse / Print / AutoBalance | ✓ (engine sound) | ✓ | unchanged |
| Everything else in the sweep's JrnWin table (party per line, per-line FX = J-5, entry types, templates, approvals = J-4/J-6/J-8/J-9) | ✗/◐ | ✓ | **Out of scope** — steps 3/7 and the journal-workflow step, per mandate |

## 11. Pillars checklists (per touched window)

Legend: PROVEN = evidence in §7; GAP = explicitly not done.

### CoaWin (+ AccCard as its editor)
- **UI:** unchanged this step (tree, context menu, import/export, seed offer) — GAPs: no New-Root (C-7), no type/balance columns in the tree, menu literals (C-5).
- **DB:** PROVEN — all reads/writes on `chart_of_accounts` post-059; legacy table dropped; migrator double-run idempotent.
- **Linking:** PROVEN — parent Guid FK; renumber keeps children attached (Kern.Tests); recycle-bin lists deleted accounts again.
- **Posting:** PROVEN — accounts created here are exactly what `JournalRepository` resolves and the posting triggers validate (harness b).
- **Persistence:** PROVEN — every AccCard field maps to a canonical column (extension set §3.1); balances are computed, never stored.
- **Downstream:** PROVEN for TB/BS/P&L/statement (§7c); GAP: tree balance/type display.

### AccCard
- **UI:** unchanged except the serial field shows a Guid fragment (F-4).
- **DB / Persistence:** PROVEN — full field round-trip incl. flags, tax rate, parent, type derivation (Kern.Tests `AccRepo_RoundTrip…`).
- **Linking:** PROVEN — Statement button's report resolves the account by code (R-3); parent combo writes the Guid FK.
- **Posting:** PROVEN via lifecycle guard seeing real postings (C-3) and resolution (J-1).
- **Downstream:** PROVEN — card balances equal posted-line sums (C-2).

### JrnWin
- **UI:** untouched (by mandate).
- **DB:** PROVEN — drafts/posts land in `journal_entries`/`journal_entry_lines` against canonical Guids.
- **Linking:** PROVEN — CoaWin-visible codes resolve (the J-1 scenario).
- **Posting:** PROVEN — fresh-company save **and** post, trigger-validated (harness b; Kern.Tests).
- **Persistence:** PROVEN — posted entry reloads by document number with codes + amounts (Kern.Tests).
- **Downstream:** PROVEN — the entry's lines are what TB/BS/P&L/statement report (harness c).

### Report windows (TrialBalanceWin / BalSheetWin / IncStmtWin / AccStmtWin + RptBase family)
- **UI:** untouched; aliases preserved so no window code changed.
- **DB / Downstream:** PROVEN for TB/BS/P&L/statement/cash-flow on SQLite (harness c, verbatim SQL); GAP: live WPF rendering + the wider report family (R-4 class) verified by code + build only — Windows pass owed.
- **Posting / Persistence:** n/a (read-only surfaces); they now read only posted lines — drafts never leak into GL reports (status = 1 filter everywhere).

### BankRecWin / BinWin / DatWin (dataset surface)
- **DB / Linking:** re-pointed to the canonical chart (bank GL accounts, recycle bin, accounts dataset) — code + build PROVEN; runtime GAP: Windows pass owed.

## 12. Files changed

- `dba/mig/sqlite/059_coa_unification.sql`, `dba/mig/postgresql/059_coa_unification.sql` (new)
- `cod/app/src/Services/Accounting.cs` (AccRepo rewrite), `Company.cs` (canonical seeding + periods), `RptQuery.cs` (GL family), `CardStore.cs`, `RecycleBinSvc.cs`, `DataExportSvc.cs`
- `cod/app/src/Views/CoaWin.xaml.cs`, `AccCard.xaml.cs` (Guid Id callers)
- `src/ShouTech.Infrastructure/OperationsRepository.cs`, `src/ShouTech.Domain/Interfaces/IOperationsRepository.cs`
- `tests/Kern.Tests/CoaUnificationTests.cs` (new), `SteiRoundTripTests.cs` (canonical-store assertions)
- `docs/FIX_PHASE_STEP2.md` (this file)
