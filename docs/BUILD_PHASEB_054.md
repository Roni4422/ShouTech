<!-- source: docs/BUILD_PHASEB_054.md -->

# Phase B1 — Batch/Serial Tracking (Migration 054) — Build Record

Branch: `build/phaseb-054-batch-serial` (base: `fix/step13-visual` @ 40e23f9).
Specification: `~/workspace/research/phase-b-design.md` §2 + §7 decision table.
Status: **built and proven headlessly on SQLite; WPF runtime and live
PostgreSQL execution are still owed** (see §8). Not pushed, not merged.

---

## 1. Design vs built

| Design element (§2) | Built | Where |
|---|---|---|
| `stock_batches` (internal `batch_no` + `supplier_batch_no`, mfg/expiry, source doc, supplier, status) | ✅ as designed | `dba/mig/{sqlite,postgresql}/054_batch_serial.sql` |
| `stock_batch_balances` per (company, warehouse, batch) | ✅ | same |
| `stock_serials` (one row per physical unit, status lifecycle, current warehouse, party, warranty) | ✅ | same |
| `stock_movement_allocations` (movement ↔ batch/serial, signed qty, expiry snapshot) | ✅ | same |
| `materials.removal_strategy` (FIFO/FEFO) | ✅ column, default FIFO; FEFO auto-selected when the material tracks expiry | same + engine |
| FEFO allocation (expiry ASC nulls-last, then receipt order) | ✅ proven (harness a) | `BatchSerialAllocator` in `src/ShouTech.Infrastructure/BatchSerial.cs` |
| Serial issues require named serials; double-issue refused | ✅ INV-6007 (harness b) | allocator |
| Returns re-enter the original batch/serial | ✅ via the original document's allocations (harness c) | allocator |
| Transfers carry allocations to the destination warehouse | ✅ via `SourceMovementId` copy (harness) | allocator + `StkPost.PostTransfer` |
| ExpiredWin rebuilt on real batch expiry | ✅ (§5) | `RptQuery.ExpiredMaterialsAsync`, `Views/Reports/ExpiredWin.cs` |
| Traceability both directions + recall list | ✅ service-level (harness) | `BatchSerialService` |
| Batch directory / trace windows (BatchDirWin, BatchTraceWin, SerialTraceWin) | ❌ **GAP** — not built; traceability ships as the service + Batch Balances report (§7) | — |
| Capture at posting (picker dialog) | ✅ for InvoiceWin documents + POS checkout | `BatchSerialPickerWin`, `BatchSerialCaptureSvc` |
| Pre-054 stock: no fabricated batches (B3) | ✅ migration creates no batch rows; identity arrives via opening/count documents | migration |

### Owner decisions taken (design recommendations followed — flagged for Man)

- **B1 — expired batch issue: HARD BLOCK** (`INV-6010`) with a
  permission-gated override `INV.BATCH.EXPIREDOVERRIDE` (sensitive,
  seeded to Admin only). The override is asserted per line in the
  picker and the issue is recorded through the normal allocation rows.
- **B2 — POS serials: REQUIRED**, same capture as invoices
  (barcode-first entry in the picker; engine refuses without them).
- **B3 — existing stock: NO fabricated batches.** Migration 054 adds
  schema only. Pre-054 balances keep posting untracked until the
  material's flags are used through opening/count documents.
- **B4 — both batch numbers kept**: internal `batch_no` (the tracked
  identity) and `supplier_batch_no` (informational, captured at receipt).

## 2. Schema (migration 054, both providers, idempotent)

- `stock_batches(batch_id PK, company_id, material_id, batch_no,
  supplier_batch_no, mfg_date, expiry_date, source_doc_kind,
  source_doc_no, supplier_party_id, status, notes, created_by/at,
  updated_at)` — `UNIQUE(company_id, material_id, batch_no)`.
- `stock_batch_balances(company_id, warehouse_id, batch_id PK,
  qty_on_hand, reserved_qty, updated_at)`.
- `stock_serials(serial_id PK, company_id, material_id, serial_no,
  batch_id NULL, status InStock/Delivered/Returned/Consumed/Scrapped,
  current_warehouse_id, party_id, warranty_expiry, source_doc_kind,
  source_doc_no, created/updated)` — `UNIQUE(company_id, material_id, serial_no)`.
- `stock_movement_allocations(allocation_id PK, stock_movement_id FK,
  batch_id, serial_id, qty signed, expiry_date snapshot, created_at)`.
- `materials.removal_strategy TEXT NOT NULL DEFAULT 'FIFO'`
  (guarded add-column on both providers).
- Permissions seeded (ids 342–345): `INV.BATCH.VIEW`,
  `INV.BATCH.MANAGE`, `INV.SERIAL.VIEW`, `INV.BATCH.EXPIREDOVERRIDE`
  (sensitive). Role grants: Admin all; User view/manage/serial-view;
  Viewer views only.
- Idempotency: SQLite uses `IF NOT EXISTS` + the
  `-- @if-column-missing` guard + `INSERT OR IGNORE`; PostgreSQL uses
  `IF NOT EXISTS` / `ON CONFLICT DO NOTHING`. Applies cleanly after
  059–064 (checksum-skip ordering, same pattern as 053) — proven by
  harness gate (g2).

## 3. Engine allocation design

All allocation runs **inside the posting transaction** of
`StockValuationEngine.Apply` (the only writer of
`material_stock_movements`), after the movement + cost layers are
written, so a batch/serial failure rolls the whole posting back —
nothing partial persists (harness f).

`StockMovementRequest` gained optional, appended fields (pre-054
callers compile and behave identically): `BatchAllocations`,
`Serials`, `OriginalDocumentNo`, `SourceMovementId`,
`AutoAllocateSerials`, `AllowExpiredBatchIssue`, `PartyId`, `Actor`.
`StockMovementResult` exposes the recorded `AllocationRecords`.

Rules (`BatchSerialAllocator`):

- **Untracked / service / non-stocked materials never allocate** —
  byte-identical behavior to the pre-054 baseline (harness d).
- **Receipts** (qty_in > 0): batch-tracked lines must supply batch
  rows summing exactly to the quantity (INV-6005 missing, INV-6009
  mismatch); a missing expiry defaults to movement date +
  `shelf_life_days`; batches are upserted by (company, material,
  batch_no) and balances credited. Serial-tracked receipts require
  exactly one serial per whole unit; duplicates fail INV-6008.
- **Issues** (qty_out > 0): batch lines take explicit slices or
  auto-allocate — **FEFO** (expiry ASC, nulls last, then batch_id)
  when the material tracks expiry or its strategy is FEFO, else FIFO
  (batch_id). Expired batches are skipped; if only expired stock can
  cover, INV-6010 unless the override is asserted. Batch-balance
  shortfall fails INV-6006 (distinct from the warehouse-level
  negative-stock rule). Serial issues require the named serials to be
  InStock at the issuing warehouse (INV-6007) and flip them to
  Delivered (Consumed for manufacturing types); system-driven paths
  (chain deliveries, MFG) may auto-pick the oldest InStock serials.
- **Returns** (SRT/PRT with `OriginalDocumentNo`): coverage is checked
  against the original document's allocations **before any write**;
  quantities re-enter the original batches / flip the original
  serials back to InStock.
- **Transfers**: the in-leg copies the out-leg's allocations via
  `SourceMovementId` and moves serials' current warehouse.
- **Counts**: surplus is a receipt (batch/serial data required),
  shortage an issue; adjustments on tracked materials without data
  fail INV-6005 rather than silently skipping.

Writers wired through: `StkPost` (documents, counts, transfers),
`InvRepo.Post` (optional per-line capture map), the DLV chain
(auto-FEFO; serial auto-pick), `MfgOrderRepository` and
`CardStore.PostOrder` (raw-material allocations = genealogy; the
finished batch is named after the order document, serials generated
`{OrderNo}-{i}` when the finished material is serial-tracked).

## 4. Capture UI

`BatchSerialCaptureSvc.CaptureForPosting(doc, owner)` runs between
draft save and post in `InvoiceWin.PostDoc` and
`PosCheckoutWin.CompleteSale`. It skips quotations, transfers, and
returns that carry an original document (they re-enter automatically),
and for every remaining tracked line opens `BatchSerialPickerWin`:

- Receipt mode: editable batch rows (batch no, expiry, mfg date,
  supplier batch no, qty) validated to sum to the line quantity.
- Issue mode: live batch balances in FEFO order with the FEFO
  suggestion pre-filled; the user may override the split.
- Serial section: one serial per line; issues are pre-filled with the
  oldest in-stock serials; exact-count, distinct validation.
- The expired-override checkbox appears only on issues and only with
  `INV.BATCH.EXPIREDOVERRIDE`.
- Cancelling any line's picker aborts the post; the draft is kept.

Quantities are captured in line units and restated to base units via
the line's unit equivalent (Step 11), so allocations always match the
engine's base-unit movements.

## 5. ExpiredWin + Batch Balances

`ExpiredWin` is rebuilt on real data: `RptQuery.ExpiredMaterialsAsync`
joins `stock_batch_balances ⋈ stock_batches ⋈ materials` with
`qty_on_hand > 0` and a non-null expiry, keeping batches that are
expired or within the material's `alert_before_expiry_days` — the
same rule as `BatchSerialService.GetExpiredBatches` (harness e).
Columns: material, unit, batch, warehouse, expiry, days remaining,
on-hand, note (Expired / Near expiry). The old flag-only derivation
is deleted; no fabricated rows.

New `BatchBalWin` report (`Reports.BatchBalances`, ribbon key
`Inventory.BatchBalances` routed in `RibbonNav`): per-batch balances
with material, warehouse, expiry and status from
`RptQuery.BatchBalancesAsync` / `BatchSerialService.GetBatchBalances`.

## 6. Comparison tables (owner gate)

Modeled on phase-b-design §2.1 and the sweep compare files
(`compare-materials.md`, `compare-stock-reports.md`).

### Batch/serial capture (BatchSerialPickerWin + engine)

| Capability | Al-Ameen V10 | Phenix v10 | ERPNext | Odoo | ShouTech 054 |
|---|---|---|---|---|---|
| Batch with expiry on receipt | ✅ | ✅ | ✅ | ✅ | ✅ picker rows, expiry defaults from shelf life |
| FEFO issue allocation | ✅ | ✅ | ✅ | ✅ (removal strategy) | ✅ FEFO/FIFO per material |
| Serial per unit, named at issue | ✅ | ✅ | ✅ | ✅ | ✅ named; auto-pick only on system paths |
| Expired batch blocked at issue | ✅ | ✅ | ✅ | ⚠️ warning | ✅ hard block + permission override (B1) |
| Return to original batch/serial | ✅ | ✅ | ✅ | ✅ | ✅ via original document allocations |
| Full movement traceability / recall | ✅ | ✅ | ✅ | ✅ | ✅ service both directions + recall list; ⚠️ no trace window yet (GAP) |
| Batch balances report | ✅ | ✅ | ✅ | ✅ | ✅ BatchBalWin |

### Expired materials (ExpiredWin)

| Capability | Al-Ameen V10 | Phenix v10 | ERPNext | Odoo | ShouTech 054 |
|---|---|---|---|---|---|
| Source | real batch expiry | real batch expiry | Batch expiry report | lot expiry alerts | ✅ real `stock_batches.expiry_date` |
| Near-expiry window | ✅ | ✅ | ✅ | ✅ | ✅ per-material `alert_before_expiry_days` |
| Quantities per batch/warehouse | ✅ | ✅ | ✅ | ✅ | ✅ from `stock_batch_balances` |
| Days remaining | ✅ | ✅ | ✅ | ✅ | ✅ |

## 7. Pillars checklists

### BatchSerialPickerWin (new window)
- UI ✅ BaseWin, central Win11 tokens, bottom command bar (OK/Cancel),
  full RTL via ShellContext (audit lists it as central).
- DB ✅ reads balances/serials via `BatchSerialService`; writes land
  in `stock_movement_allocations` at post.
- Linking ✅ opened from InvoiceWin + PosCheckoutWin posting flows;
  shell company/language live at open.
- Posting ✅ its captures are the only path by which tracked invoice
  lines can post; engine enforces INV-6005/6009/6010.
- Persistence ⚠️ **GAP** — selections are captured at posting time and
  persisted as allocations; a saved *draft* does not store pending
  selections (re-captured on post). Allocations are the durable record.
- Downstream ✅ allocations feed traceability, recall, batch balances.

### ExpiredWin (rebuilt)
- UI ✅ RptBase schedule (sortable/resizable, bottom totals, print/export).
- DB ✅ real batch tables; zero fabricated rows.
- Linking ✅ same ribbon entry as before (`Reports.ExpiredMaterials`).
- Posting n/a (report). Persistence n/a (report).
- Downstream ✅ identical rule in `BatchSerialService.GetExpiredBatches`.

### BatchBalWin (new report)
- UI ✅ RptBase schedule, own tint via the report family.
- DB ✅ `stock_batch_balances ⋈ stock_batches`.
- Linking ⚠️ **GAP** — `RibbonNav` routes `Inventory.BatchBalances`,
  but whether a ribbon button carries that key depends on the ribbon
  definition data; the report is reachable wherever the key is bound.
- Posting/persistence n/a. Downstream ✅ same data as the service.

### Engine + migration (non-window)
- DB ✅ migration 054 both providers, idempotent (harness g/g2).
- Posting ✅ single writer; allocations in the posting transaction.
- Persistence ✅ allocations + balances + serial states are durable
  and reconcile with warehouse balances (harness a/c/transfer).
- Downstream ✅ trace service, ExpiredWin, BatchBalWin consume them.

## 8. Proof (headless, SQLite, this machine)

Harness: `~/workspace/research/fix-step54/batch54_harness.py` +
`harness/Program.cs` (real SqlMigrator + real engine/service);
full output in `~/workspace/research/fix-step54/harness-output.txt` —
**46 PASS / 0 FAIL**:

- (a) FEFO: receipts B-LATE (exp 2027) then B-EARLY (exp 2026);
  issue 12 → B-EARLY 10 + B-LATE 2; batch balances 0 / 8 exact;
  allocations recorded.
- (b) Serials: receipt SN-1..3; issue named SN-2 → Delivered, others
  InStock; double-issue refused INV-6007; duplicate receipt INV-6008.
- (c) Sales return re-enters original batch B-EARLY (+2, balance 2).
- (d) Untracked material: qty 6 / value 60 after 10@10 in, 4 out —
  identical to the pre-054 baseline; zero allocation rows.
- (e) Expired service: exactly B-OLD (expired, qty 5, −17d) and
  B-NEAR (+9d, alert 30) for the fixture material; far and drained
  batches excluded; company-wide scope verified.
- (f) Tracked receipt without batch data → INV-6005, transaction
  rolled back, nothing written; quantity mismatch → INV-6009.
- (B1) Expired-only issue → INV-6010; with override it posts.
- Transfer: in-leg inherits batch; per-warehouse batch balances 6/4.
- Trace: batch → [receipt, issue, return]; movement → allocations
  sum −12; serial timeline; recall list names the consuming document.
- (g) Migrator double-run idempotent; 054 recorded once; permissions
  seeded; (g2) 054 withheld → DB carries 059–064 → 054 applies
  cleanly afterwards and stays idempotent.

Kern.Tests: `tests/Kern.Tests/BatchSerialEngineTests.cs` (10 facts)
pins the same contract against real migrated DBs — **compiles here;
execution is owed on Windows** (net8.0-windows testhost cannot run on
this Linux VM).

### Gates
- Full build `--no-incremental`: **0 errors, 0 warnings**.
- `tools/audit_window_context.py`: **exit 0** (picker listed, central).
- Lang parity: **0 missing** vs En in Ar/Fr/Tr (21 new keys ×4).
- Errors catalogs: identical key sets ×4 (6 new codes INV-6005..6010).
- Migrator idempotent double-run: ✅ (harness g).

## 9. GAPs and owner flags

1. **No BatchDirWin / BatchTraceWin / SerialTraceWin windows.**
   Traceability is complete at the service layer and proven; the
   dedicated browse/trace windows are a follow-up window build.
2. **Drafts don't persist pending selections** (capture at post; see
   picker pillars). Persisting per-line selections on the draft would
   need draft-line columns — a design decision for Man.
3. **WhsWin stock-count dialog has no batch capture UI** — counts of
   tracked materials through that dialog fail honestly with INV-6005;
   InvoiceWin count documents capture fully.
4. **Chain/MFG serial issues auto-pick** the oldest InStock serials
   (no capture UI on those paths); batch auto-allocation is FEFO.
5. **Ribbon button** for `Inventory.BatchBalances` depends on ribbon
   definition data (route exists).
6. **WPF runtime + live PostgreSQL are owed**: the picker, ExpiredWin
   and BatchBalWin have not run on Windows; migration 054's PG file
   follows the 053 pattern but has not executed against live PG.
7. Decisions B1–B4 were taken per the design's recommendations
   (§1) — Man can reverse any of them; B1's override permission and
   B2's POS requirement are the two most visible.

---

<!-- source: docs/BUILD_PHASEB_055.md -->

# Build Report — Phase B2: Budgets (migration 055)

**Branch:** `build/phaseb-055-budgets` (worktree
`~/workspace/projects/ShouTech-build55`), based on the tip of
`build/phaseb-054-batch-serial` (`2b1a449`). **Not pushed, not merged.**
**Design source:** `~/workspace/research/phase-b-design.md` §3 (Budgets)
and the §7 decision table (BG1–BG3).
**Proof:** `~/workspace/research/fix-step55/budget55_harness.py` +
`harness/Program.cs`; output saved at
`~/workspace/research/fix-step55/harness-output.txt` — **ALL PASS**
(43 checks). Kern.Tests facts: `tests/Kern.Tests/BudgetGuardTests.cs`
(9 facts; compile here, execution owed on Windows like the rest of the
suite).

> **Readiness note (per Man's standing correction):** this branch is
> *build- and harness-verified on Linux/SQLite*. It is **not**
> product-ready until the owed checks below run: Kern.Tests execution,
> WPF runtime behavior of BudgetWin/BudgetVarWin, and a live
> PostgreSQL pass. Those are listed plainly in §8.

---

## 1. What was built

| Layer | Piece |
|---|---|
| Migration 055 (SQLite + PostgreSQL, idempotent) | `budgets`, `budget_lines`, `budget_period_amounts`, `budget_control_events`; permissions `ACC.BUDGET.VIEW/EDIT/ACTIVATE` (346–348; ACTIVATE sensitive, Admin only; User VIEW+EDIT; Viewer VIEW) |
| Domain | `Entities/Budget.cs` (Budget, BudgetLine, BudgetVarianceRow, BudgetWarning, BudgetViolation, lookup DTOs, kind/status/action constants); `Interfaces/IBudgetRepository.cs`; error codes **ACC-5018/5019** (see §2) |
| Guard | `Infrastructure/BudgetGuard.cs` — static, runs **inside** the posting transaction after cost-center splits are written, before the entry flips to Posted; shared by `JournalRepository.PostAsync`, `JournalRepository.PostDocumentAsync` and `GlPosting.PostInTransaction` |
| Repository | `Infrastructure/BudgetRepository.cs` — budget CRUD (transactional wholesale line/distribution replace), lookups (fiscal years, accounts, cost centers, projects), variance via the guard's own actuals |
| Service | `Application/Services/BudgetService.cs` — validation (dimension-per-kind, duplicate lines, distribution sum = annual amount, periods 1–12) + permission-shaped API |
| Window | `Views/BudgetWin.cs` (BaseWin, **Copper** tint) — budget list + header editor + lines grid + 12-month distribution editor + bottom command bar; Activate/Close under `ACC.BUDGET.ACTIVATE` |
| Report | `Views/Reports/BudgetVarWin.cs` (RptBase) — budget vs actual vs remaining vs % consumed + accumulated-monthly pair, fed by `RptQuery.BudgetVarianceAsync` → the same repository actuals the guard uses |
| Wiring | `AppHost.Budgets` (created at Initialize, rebound on company switch), `PermCat.Accounting` budget constants, `RibbonNav` routes `Accounts.Budgets` / `Accounts.BudgetVariance`, JrnWin + CshWin surface Warn-mode warnings after posting |
| Lang | `Budget.*` (41 keys) + `Reports.BudgetVariance` in En/Ar/Fr/Tr — parity 0 missing; error catalogs ×4 + `docs/ERROR_CATALOG.md` updated for ACC-5018/5019 |

## 2. Design-vs-built mapping

The design's §3 was followed closely; the deviations are deliberate
and listed here.

| Design | Built | Notes |
|---|---|---|
| `budgets` header (fiscal_year_id, name, budget_against Account/CostCenter/Project, status Draft/Active/Closed, action_annual/action_monthly Stop/Warn/Ignore) | ✅ as designed | `budget_name` column name kept from the sketch |
| `fiscal_year_id TEXT` in the sketch | ⚠️ **INTEGER (SQLite) / BIGINT (PG)** | The real `fiscal_years` PK (migrations 001/002) is an autoincrement integer; the FK must match it. Deviation recorded in the migration header |
| `budget_lines` unique (budget, account, cost_center, project) | ✅ expression unique index folding NULL dimensions to sentinels (both providers), so duplicate detection works with NULLs |
| `budget_period_amounts` monthly distribution; no rows = equal split | ✅ as designed | Service enforces Σ(distribution) = annual amount when rows exist |
| `budget_control_events` (Warned/Blocked audit) | ✅ as designed | See §4 for the blocked-event transaction subtlety |
| Guard codes ACC-5009 (annual) / ACC-5010 (monthly) | ⚠️ **ACC-5018 / ACC-5019** | ACC-5009/5010 were consumed by fix step 3 (cost-center validation: unknown center / center required); ACC was taken through 5017, so the guard takes the next free pair. Recorded in `ErrorCodes.cs`, all four `Errors/*.json`, and `docs/ERROR_CATALOG.md` |
| Guard inside JournalRepository.PostAsync beside the cost splits | ✅ **and** in `GlPosting.PostInTransaction` | The mandate required document postings to be guarded identically; GlPosting is the shared core (step 3), so both manual journals and every document path pass the same guard |
| Actuals = Σ(debit − credit) over journal lines ⋈ cost splits | ✅ single implementation (`BudgetGuard.GetLineActuals`) used by the guard **and** the variance report | See §5 |
| ACC-5006 override precedent → BG1 no override | ✅ **BG1: no override in v1** — no `ACC.BUDGET.OVERRIDE` permission seeded; a Stop is final at posting time |
| BG2 actuals-only scope | ✅ commitments/encumbrances not modeled |
| BG3 include Project | ⚠️ **schema + window include Project; enforcement does not** | Journal lines carry **no project attribution anywhere** in the schema, so a Project budget has nothing to consume. Project lines are definable and appear in variance with actual 0. Enforcing them needs a project dimension on journal lines — a separate schema decision (flagged §8). This is the design's own caveat ("journal lines need a project dimension for enforcement") made explicit |
| Company-level guard mode (off/warn/block) | The design specifies **no** company-level switch; the per-budget `action_annual`/`action_monthly` ARE the modes. None was invented |
| `accounts.budget` decorative flag (sweep C-2) | ✅ **Given its designed meaning** — see §3 | — |

## 3. The `budget` flag — designed meaning, enforced

The canonical flag is `chart_of_accounts.budget` (migration 059; the
legacy `accounts` table was dropped in fix step 2). Rule:

- The guard enforces a budget line **only** when its account carries
  the flag. The flag is the account-level opt-in the design intended.
- `BudgetRepository.SaveAsync` sets the flag transactionally for every
  account that gains a budget line, so a saved budget is never
  silently unenforced.
- Clearing the flag on the account card remains an explicit,
  user-visible opt-out (pinned by a Kern.Tests fact and documented
  here — no third "half-matters" state).

## 4. Guard placement and mode semantics

**Placement.** `BudgetGuard.Check` runs inside the posting
transaction, after `GlPosting.ApplyCostCenters` has written the
entry's cost splits (actuals read splits, so the entry must be
measurable) and before the entry flips to Posted. It covers:

- manual journals — `JournalRepository.PostAsync`;
- document postings through the repository boundary —
  `JournalRepository.PostDocumentAsync`;
- embedded document postings — `GlPosting.PostInTransaction`
  (invoices, orders, manufacturing, assets, …).

**Modes (per budget, per horizon).** For each affected Active budget
line the guard computes the projected actual (posted actual + this
entry's delta) against the annual limit and the accumulated-monthly
limit:

- `Ignore` — horizon not checked;
- `Warn` — posting proceeds; a `Warned` row is written to
  `budget_control_events` in-transaction and a `BudgetWarning` is
  returned to the caller (`PostAsync` now returns them; JrnWin/CshWin
  display them; `GlPostingResult.BudgetWarnings` carries them for
  document paths);
- `Stop` — the guard throws `AppError` ACC-5018 (annual) or ACC-5019
  (monthly); the posting transaction rolls back, so **nothing is
  written**. Because the rollback also erases in-transaction audit
  rows, the guard collects violations into a sink and
  `JournalRepository` records the `Blocked` events **after** rollback
  (best-effort; a recording failure is attached to the thrown error,
  never silent).

> **Bounded gap:** embedded GlPosting callers (invoice/order/MFG/asset
> repositories) are guarded identically and surface the coded error,
> but their Blocked events are not recorded — their posting cores have
> no post-rollback seam without restructuring `InvRepo.Post`. Warned
> events on those paths ARE recorded (in-transaction). Flagged for a
> future pass.

**Symmetric reversal.** Actuals count entries with status Posted **or
Reversed** (the originals). A reversal entry carries the exact
inverse lines, so original + reversal net to zero and headroom is
restored exactly — status-Posted-only counting would leave the
original consuming forever (harness scenario (d) pins this).

**Credits never block.** Only positive deltas (net debit consumption)
are checked; a credit posting reduces actuals and always passes.

## 5. Actuals query design

- **CostCenter budgets** read `journal_line_cost_splits` joined to
  `journal_entry_lines` and `journal_entries`: Σ(debit − credit) per
  budget center. A budget on a **parent** cost center covers its
  descendants (031 hierarchy), resolved by walking `parent_id` at
  guard time — no closure table exists, and budget checks are
  low-frequency, so the walk is the honest implementation.
- **Account budgets** read `journal_entry_lines` directly (no splits
  needed).
- **Cost-center-less rule (proof f):** a journal line with no cost
  center writes no splits, so it is **invisible to CostCenter
  budgets** but counts in full toward Account budgets. This mirrors
  the posting model (an unattributed cost cannot be charged to a
  center's budget) and is pinned by harness + tests.
- Fiscal scoping: the entry's date resolves through `fiscal_periods`
  to (fiscal_year_id, period_number); the monthly horizon uses
  year-to-date actuals through that period vs the accumulated
  distribution (stored rows authoritative; equal split
  annual × period / 12 otherwise).
- Drafts never count; nothing is cached — every figure is computed
  from posted journal data at check/read time, so the variance report
  and the guard can never disagree.

## 6. Comparison table (comparison gate)

Researched from the professional ERPs' documented budget models
(ERPNext Budget doctype + Budget Variance report; Odoo
account_budget / analytic budgets; Al-Ameen V10 and Phenix v10 budget
modules as modeled in the Phase B design):

| Capability | Al-Ameen V10 | Phenix v10 | ERPNext | Odoo | **ShouTech 055** |
|---|---|---|---|---|---|
| Budget dimensions | account × cost center | account × cost center/project | account × cost center **or** project (Budget Against) | analytic account / account | account × cost center, account × project (define-only, §2 BG3), account-only |
| Period granularity | fiscal year + monthly distribution | year + periods | monthly distribution table | date range per budget line | fiscal year + 12-month distribution (equal split default) |
| Enforcement at posting | warn/block options | validation on posting | **validate-on-post with block or warn** (company-level action) | none native (report-only) | **per-budget, per-horizon Stop/Warn/Ignore**, enforced in the single posting core for journals AND documents |
| Override for blocked posting | supervisor override | role-based | role-based budget override | n/a | **none in v1 (BG1)** — Stop is final |
| Variance report | budget vs actual report | budget execution report | Budget Variance Report (monthly/quarterly/annual) | budget vs actual analytic report | BudgetVarWin: annual + accumulated-monthly budget/actual/remaining/% |
| Overrun audit trail | log | log | via validation errors | n/a | `budget_control_events` — Warned **and** Blocked rows with limit/actual/overage snapshots |
| Actuals source | posted entries | posted entries | GL entries | analytic lines | posted journal lines ⋈ cost splits (never cached) |
| Project budgets enforced | partial | yes | yes | analytic | **GAP** — no project dimension on journal lines (§2) |

## 7. Pillars checklists

### BudgetWin (new)
- **UI ✅** — BaseWin standard: list + header editor + lines grid +
  distribution editor, bottom command bar, Copper schedule tint
  (new 44th central token — all 43 existing tints were claimed),
  full RTL via central LangDirSv, shell context via AppHost (audit
  classifies it central/rebound). Windows visual check owed (§8).
- **DB ✅** — migration 055 tables; every save is one transaction
  (header + wholesale lines/distribution + flag opt-ins).
- **Linking ✅** — RibbonNav `Accounts.Budgets`; company resolved from
  the shell (`AppState.CurrentCompanyCode` → Guid via the journal
  store); fiscal-year/account/center/project pickers fed from the
  live masters; Variance button opens BudgetVarWin.
- **Posting ✅** — the window defines budgets; enforcement lives in
  the posting core (§4) and is harness-proven.
- **Persistence ✅** — every field (name, against, status, both
  actions, notes, per-line account/dimension/amount, distribution)
  round-trips through BudgetRepository; the editor's in-memory draft
  state commits on Save (standard editor pattern, same as ProjWin).
- **Downstream ✅** — BudgetVarWin reads the same actuals; Warn
  warnings surface in JrnWin/CshWin; control events persist the audit
  trail.

### BudgetVarWin (new)
- **UI ✅** — RptBase standard (sortable/resizable grid, bottom
  totals opt-out per family pattern, print/export inherited),
  Silver family tint.
- **DB ✅** — read-only over 055 tables + posted journals.
- **Linking ✅** — RibbonNav `Accounts.BudgetVariance`; opened from
  BudgetWin's Variance button.
- **Posting** — n/a (report); **Persistence** — n/a (no inputs);
  **Downstream ✅** — figures identical to guard actuals by
  construction (shared `BudgetGuard.GetLineActuals`).

> **Ribbon presence note (pre-existing pattern, not a 055 gap):**
> RibbonNav routes both keys; whether the physical ribbon shows the
> buttons depends on the ribbon definition data, exactly as for
> Projects/Assets in Phase 3.

## 8. Proof, gates, and owed items

**Harness (SQLite, real migrator + real services)** — 43/43 PASS
(`harness-output.txt`):
- (a) budget 1000 → post 400 → actual 400 / remaining 600 / 40%;
  post 700 → **ACC-5018**, entry stays Draft, actual unchanged,
  Blocked event recorded; saving the budget set the chart flag.
- (b) Warn mode → 400 + 700 both post, warning returned (overage
  100), actual **1100 > 1000**, Warned event recorded.
- monthly: annual 12000 equal split → YTD 3500 > 3000 blocked with
  **ACC-5019**.
- (c) document posting via `PostDocumentAsync`/GlPosting blocked
  identically (ACC-5018, no journal entry created).
- (d) reversal → actual 400 → **0**; full 1000 consumable again.
- (e) unbudgeted account (and a Draft budget) → 99999 posts freely.
- (f) parent-center budget: child-center 400 consumes; center-less
  700 posts and does **not** consume; child-center 700 blocked.
- (g) fresh migrate ×2: 055 recorded exactly once, second run SKIP;
  late-arrival (064-level DB without 055) accepts 055 and stays
  idempotent; tables + 3 permissions present.

**Gates:**
- Full build `--no-incremental`: **0 errors, 0 warnings**.
- `tools/audit_window_context.py`: **exit 0** (BudgetWin central).
- Lang parity: **0 missing** vs En in Ar/Fr/Tr (Budget ×41 +
  Reports.BudgetVariance in all four); error catalogs ×4 in step.
- Migrator idempotent double-run: proven in harness (g)/(g2).

**Kern.Tests:** `BudgetGuardTests.cs` — 9 facts mirroring the harness
scenarios (annual/monthly Stop, custom distribution, Warn, document
path, reversal, unbudgeted/Draft, CC parent coverage + center-less
rule, flag opt-in/opt-out). Compiles 0/0 here; **execution owed on
Windows** (net8.0-windows testhost, same as the whole suite).

**Owed / owner flags:**
- **WPF runtime** (BudgetWin/BudgetVarWin visuals, RTL, tint) and
  **live PostgreSQL** run — owed, Windows-side.
- **BG3 enforcement gap:** Project budgets define-only until journal
  lines gain a project dimension — owner's schema call.
- **Blocked-event recording** for embedded GlPosting callers —
  bounded gap (§4), needs an InvRepo seam decision.
- **BG1:** if Man wants a supervisor override later, it slots in as
  an `ACC.BUDGET.OVERRIDE` permission + guard branch — deliberately
  not built.

---

<!-- source: docs/BUILD_PHASEB_056.md -->

# Build Phase B3 — Quality Management (migration 056)

Branch: `build/phaseb-056-quality` (from `build/phaseb-055-budgets` @
`dd85b0d`). Design source: `~/workspace/research/phase-b-design.md` §5
(Quality) and §7 (decision list). This document maps design → built,
records the Q1–Q4 decisions taken, the gate state machine, the schema,
the comparison tables, the per-window pillars checklists, the headless
proof, the gates, and the owner flags.

## 1. What was built

| Layer | Artifact |
|---|---|
| Migration | `dba/mig/sqlite/056_quality.sql`, `dba/mig/postgresql/056_quality.sql` — quality schema + per-company gate mode + material/warehouse columns + `QUALITY.*` permissions (349–357) |
| Domain | `Entities/Quality.cs` (parameters, templates, inspections, readings, alerts, constants), `Interfaces/IQualityRepository.cs`, error codes `INV-6011..6016` (`ErrorCodes.cs`) |
| Gate | `Infrastructure/QualityGate.cs` — in-transaction gate used by the posting paths (BudgetGuard pattern): receipt diversion, completion release/disposition, sale draw, outgoing check, reading evaluation |
| Data/service | `Infrastructure/QualityRepository.cs`, `Application/Services/QualityService.cs` |
| Posting hooks | `StkPost.ApplyDocument` (diversion + outgoing check + quarantine draw), `MfgOrderRepository.PostAsync` (InProcess diversion), `CommercialDocumentRepository` DLV path (outgoing check) |
| Windows | `Views/QcInspectWin.cs`, `Views/QcTemplateWin.cs`, `Views/QcAlertWin.cs`, `Views/Reports/QcSummaryWin.cs` |
| Wiring | `AppHost.Quality` (+ `AppServiceHost` pass-through, rebound on company switch), `PermCat.Quality`, `RibbonNav` routes `Inventory.QualityInspections/QualityTemplates/QualityAlerts/QualitySummary`, audit script knows `AppHost.Quality` |
| i18n | `Quality.*` (92 keys incl. `Col.*`) + `Reports.QualitySummary` in En/Ar/Fr/Tr; Errors `INV-6011..6016` in all 4 catalogs + `docs/ERROR_CATALOG.md` |

## 2. Design vs built — with the Q decisions

| Design element (§5) | Built | Notes |
|---|---|---|
| `quality_parameters` (code, names, type, unit) | ✅ as designed | Types: `Numeric` / `Value` / `PassFail` |
| `quality_templates` + `quality_template_lines` (criteria, mandatory flag, sample size) | ✅ as designed | Line criteria: `min_value`/`max_value` (Numeric), `accepted_value` (Value); sample size on the template |
| `quality_inspections` (type, ref document, material, batch, warehouse pair, quantities, status, disposition, inspector, due date) | ✅ as designed + `template_id`, `total_qty`, `party_id` | Status set is the design's `Draft/Accepted/PartiallyAccepted/Rejected`; `party_id` powers the supplier reject-rate summary |
| `quality_inspection_lines` + readings (up to 3 readings per characteristic) | ✅ as designed | Lines **snapshot** the template criteria (`min_value`/`max_value`/`accepted_value` copied at creation) so later template edits never rewrite inspection history; result per line `Pass/Fail` with an explicit override flag |
| `quality_alerts` (root cause, corrective/preventive action, owner, status) | ✅ as designed + `alert_type` + `message` | Alert types: `FailedInspection`, `GateWarning`; closing requires root cause + corrective action (Odoo rule) |
| **Q1 — gate default: Warn** | ✅ **Taken: Warn**, per-company | Stored in a dedicated `quality_settings` table (the schema has no generic per-company settings table — recorded adaptation). Settable from QcInspectWin (`QUALITY.TEMPLATE.EDIT`) |
| **Q2 — rejected stock home: dedicated quarantine warehouse** | ✅ **Taken** | `warehouses.is_quarantine`; one quarantine warehouse per company, auto-created by the gate on first use ("Quarantine"); never hand-picked as a document warehouse by the gate logic |
| **Q3 — formula criteria: DEFER** | ✅ **Taken: deferred** | Numeric / Value / PassFail only; no formula engine |
| **Q4 — outgoing inspection: INCLUDE** | ✅ **Taken: included** | Same Stop/Warn semantics on sales documents (`StkPost`) and chain deliveries (DLV): Stop refuses without an accepted outgoing inspection for the document (`INV-6013`); Warn posts and auto-creates the pending outgoing inspection + GateWarning alert; a REJECTED outgoing inspection blocks in **both** modes (`INV-6011`) |
| Quarantine warehouse concept | ✅ | Receipts of QC materials post into it through the valuation engine, so balances/batches/serials/cost are first-class 054 state |
| Receipt diversion (purchase / stock-in / MFG completion) | ✅ | `StkPost` inbound kinds + `MfgOrderRepository` completion (InProcess type). A Draft inspection is auto-created in the same transaction (idempotent per document+material+batch) |
| PASS release through the valuation engine | ✅ | Value-exact transfer pair (TRANSFER_OUT at the engine's computed cost + TRANSFER_IN with `ReceiptCostAmount` + `SourceMovementId`), so batch balances move and serials return to `InStock` at the destination under their original identities |
| Partial pass | ✅ | Accepted/rejected split per inspection; batch slice released under the same `batch_id`; status `PartiallyAccepted` |
| FAIL dispositions | ✅ | `ReturnToSupplier` (engine issue `QCR` out of quarantine), `Scrap` (engine issue `QCS`; serials marked `Scrapped`), `UseAsIs` (release), `Rework` / `Quarantine` (stay in quarantine). Disposition + quantities recorded on the inspection; a failure alert is raised for any rejected quantity |
| Alerts: failed + near/overdue | ✅ | Failed inspections and gate warnings are alert rows; the pending worklist carries a due date (receipt date + 3 days) and QcInspectWin shows the overdue count; `GetOverdueInspectionsAsync` feeds it (ExpiredWin pattern) |
| Quality summary / reject rate | ✅ | `QcSummaryWin` (RptBase): inspections, total/accepted/rejected qty and reject-rate % grouped by material or by supplier |
| CRM entitlement gating (design §5.6) | N/A | Quality is core (not the CRM module); no entitlement gate applies |

### Gate semantics — how the design's Stop/Warn meets the mandate

The mandate requires quarantine-first in all modes ("the received
quantity lands in QUARANTINE … hard gate = stock stays quarantined
until PASS; soft gate = design semantics"). The design's Warn mode
("warn but allow use") is therefore realized at the **point of use**,
which is also where the design puts the mode's teeth:

- **Both modes:** QC receipts divert to quarantine + Draft inspection.
  Quarantined stock is never part of the sellable balance.
- **Stop:** a sale whose quantity is only available in quarantine is
  refused (`INV-6012`); a delivery of an outgoing-QC material without
  an accepted outgoing inspection is refused (`INV-6013`); a rejected
  outgoing inspection refuses in both modes (`INV-6011`).
- **Warn:** the sale proceeds, drawing the shortfall from quarantine
  through the engine, and a `GateWarning` alert records the bypass;
  outgoing deliveries post and auto-raise the pending inspection.
- **Completion is the only release path** in both modes.

## 3. Gate state machine

```
Receipt (QC material) ──► stock @ quarantine warehouse (engine)
                      └─► Inspection [Draft] (due = receipt + 3d)
Draft ──SaveResults──► Draft (readings stored; auto result per line)
Draft ──Complete(accepted = total)──► Accepted ──► release pair ──► stock @ destination
Draft ──Complete(0 < accepted < total, disposition)──► PartiallyAccepted
        ├─► accepted part released (same batch/serial identity)
        └─► rejected part per disposition + FailedInspection alert
Draft ──Complete(accepted = 0, disposition)──► Rejected
        └─► rejected part per disposition + FailedInspection alert
Dispositions: ReturnToSupplier = QCR issue out of quarantine
              Scrap            = QCS issue out of quarantine (serials → Scrapped)
              UseAsIs          = released like a pass
              Rework/Quarantine= stays in quarantine (no movement)
Outgoing: delivery ──► CheckOutgoing: Accepted exists → pass
                       Rejected exists → INV-6011 (both modes)
                       none + Stop → INV-6013
                       none + Warn → post + Draft Outgoing inspection + alert
Sale: sellable ≥ qty → normal issue
      shortfall in quarantine + Stop → INV-6012
      shortfall in quarantine + Warn → split issue (sellable part +
      quarantine draw) + GateWarning alert
```

Invariants: completion validates accepted + rejected = inspected qty
(`INV-6015`), a rejected part requires a valid disposition, completed
inspections are immutable (`INV-6014`), mandatory template lines must
be judged before completion (auto-evaluation runs in-transaction;
unjudged mandatory lines block with `INV-6014`).

## 4. Schema (migration 056)

New tables: `quality_settings` (company PK, `gate_mode`),
`quality_parameters`, `quality_templates`, `quality_template_lines`,
`quality_inspections`, `quality_inspection_lines`,
`quality_inspection_readings`, `quality_alerts`.
Column adds: `materials.qc_template_in_id`, `materials.qc_template_out_id`,
`materials.qc_outgoing_required`, `warehouses.is_quarantine`.
Permissions: `QUALITY.PARAM.VIEW/EDIT`, `QUALITY.TEMPLATE.VIEW/EDIT`,
`QUALITY.INSPECT.VIEW/EDIT/COMPLETE`, `QUALITY.ALERT.VIEW/EDIT`
(Admin: all; User: views + edits + complete; Viewer: views).
Company keying follows the stock world (company **code**), consistent
with 054/055. Both providers; idempotent (SQLite `INSERT OR IGNORE` +
column guards; PG `ON CONFLICT` + `ADD COLUMN IF NOT EXISTS`).

## 5. Comparison tables (acceptance gate)

### QcInspectWin — inspection worklist + entry + completion

| Aspect | Al-Ameen V10 | Phenix v10 | ERPNext (Quality Inspection) | Odoo (Quality Checks) | ShouTech 056 |
|---|---|---|---|---|---|
| Trigger | Manual QC on goods-receipt | QC on receipt/production | Linked to Receipt/Delivery/Job Card, template-driven | Control points auto-create checks on operations | Auto-created on QC receipt (In), MFG completion (InProcess), delivery (Out, Warn); manual Outgoing creation |
| Result entry | Pass/fail per item | Measured values | Readings per specification with min/max, status auto | Measure / pass-fail check types | Up to 3 readings per characteristic; Numeric band / accepted value auto Pass/Fail; manual override is flagged |
| Quantities | Accept/reject qty | Accept/reject qty | Accepted + rejected qty fields | Whole-check pass/fail | Accepted + rejected split with PartiallyAccepted status |
| Rejection handling | Return / scrap vouchers | Return note | Rejected qty to a rejected warehouse | Alerts + corrective actions | Disposition on the inspection (Return/Scrap/Rework/UseAsIs/Quarantine) with the stock effect posted by the engine |
| Gate | Receipt blocked options | Optional | Stock blocked until inspection (rejected warehouse) | Control point can block the operation | Quarantine warehouse + Stop/Warn per company |
| Alerts | — | — | Review/action workflow | Quality Alerts with root cause | Quality alerts: close requires root cause + corrective action |

### QcTemplateWin — parameters, templates, material assignment

| Aspect | Al-Ameen V10 | Phenix v10 | ERPNext (Quality Goal / template) | Odoo (Control Points) | ShouTech 056 |
|---|---|---|---|---|---|
| Characteristics library | Test definitions | Test definitions | Item Quality Inspection Template parameters | Control point instructions | Quality parameters (Numeric/Value/PassFail, unit) |
| Template per item | Per item card | Per item | Template linked to item | Control point per product/operation | Templates by inspection type; materials assigned incoming/outgoing templates + QC flags in one grid (MatCard keeps its QC-required flag) |
| Tolerances | Min/max | Min/max | Min/max per reading row | Tolerance on measure checks | Min/max band or accepted value per line; mandatory flag; sample size |
| Formula criteria | — | — | Formula-based criteria exist | — | Deferred per Q3 |

### QcAlertWin — quality alerts

| Aspect | Al-Ameen V10 | Phenix v10 | ERPNext (Quality Review/Action) | Odoo (Quality Alerts) | ShouTech 056 |
|---|---|---|---|---|---|
| Source | — | — | Failed reviews | Failed checks auto-create alerts | Failed inspections + gate warnings auto-created |
| Workflow | — | — | Reviews with resolution | Root cause, corrective + preventive actions, owner, stages | Root cause + corrective required to close; preventive action + owner fields; Open/InProgress/Closed |

### QcSummaryWin — inspection summary / reject rate

| Aspect | Al-Ameen V10 | Phenix v10 | ERPNext | Odoo | ShouTech 056 |
|---|---|---|---|---|---|
| View | QC reports | QC reports | Quality Inspection summary reports | Quality check/alert analysis | Inspections, total/accepted/rejected qty, reject-rate % by material or supplier, RptBase totals/print |

## 6. Pillars checklists

### QcInspectWin
- UI ✅ BaseWin, Jade schedule tint, bottom command bar, shell context via AppHost (audit row present), RTL via central LangDirSv.
- DB ✅ reads/writes `quality_inspections/lines/readings`, `quality_settings` — all persisted; no UI-only fields.
- Linking ✅ ribbon route `Inventory.QualityInspections`; inspections link to source documents (kind/id/no), materials, batches; Alerts button cross-opens QcAlertWin.
- Posting ✅ completion posts the real engine movements (release pair / QCR / QCS) in one transaction with the inspection update.
- Persistence ✅ readings (≤3/line), results + override flag, completion quantities/disposition/inspector all saved via QualityService.
- Downstream ✅ balances, batch/serial state, alerts and the summary report all consume the same rows (harness-proven).

### QcTemplateWin
- UI ✅ BaseWin, Peridot tint, bottom bar. DB ✅ parameters/templates/lines/material flags fully persisted. Linking ✅ ribbon route; templates referenced by materials and snapshotted by inspections. Posting — N/A (definitions window; its effect flows through the gate). Persistence ✅ incl. soft-delete of lines on template save. Downstream ✅ gate + inspection creation consume definitions (harness-proven).

### QcAlertWin
- UI ✅ BaseWin, Garnet tint, bottom bar. DB ✅ alert workflow fields persisted. Linking ✅ ribbon route + open-from QcInspectWin; alerts link inspection + document. Posting — N/A. Persistence ✅ close rule enforced in the service. Downstream ✅ alert counts surface in QcInspectWin's status line (overdue) and the worklist.

### QcSummaryWin
- UI ✅ RptBase standard (filters, totals, print). DB ✅ aggregate over `quality_inspections` (+ parties for supplier names). Linking ✅ ribbon route `Inventory.QualitySummary`. Posting — N/A. Persistence — N/A (read model). Downstream ✅ reject-rate by material/supplier proven in the harness (20/50 = 40%).

### Touched windows/paths
- `StkPost` (all document windows using it): diversion + gates — PROVEN at the gate-sequence level by the harness/tests (the harness performs the exact calls `ApplyDocument` makes, in the same order, against the real engine).
- `MfgWin` path (`MfgOrderRepository.PostAsync`): QC finished goods divert + InProcess inspection in the posting transaction.
- Chain DLV (`CommercialDocumentRepository`): outgoing check per line.

## 7. Proof (headless, SQLite)

`~/workspace/research/fix-step56/quality56_harness.py` — migrates a
fresh DB twice with the real SqlMigrator, then runs the C# harness
(real QualityGate + StockValuationEngine + QualityRepository/Service).
**Result: ALL PASS** (driver + 60 harness checks), covering:

- (a) QC receipt → quarantine balance 100, sellable 0, Draft inspection auto-created with template lines snapshotted;
- (b) PASS → sellable 100, quarantine 0, stock value preserved (1000); partial pass on a batch → 30 released under the same batch id, 20 scrapped, one failure alert, `PartiallyAccepted`; serials InStock at quarantine → same serials InStock at destination;
- (c) FAIL ReturnToSupplier → quarantine drained, disposition recorded, sellable untouched; Quarantine disposition keeps stock quarantined;
- (d) Stop → sale refused `INV-6012`, stock untouched; Warn → sale draws from quarantine + GateWarning alert; over-sale still fails as insufficient; outgoing gate: Stop `INV-6013`, Warn posts + pending Outgoing inspection, accepted inspection unblocks Stop;
- (e) non-QC receipt/sale unchanged (no diversion, no inspection);
- (f) seeded overdue Draft inspection returned by the overdue query;
- (g) migrator double-run idempotent (APPLY then SKIP, one
  `schema_migrations` row), schema objects present, late arrival
  (056 applied onto a 064 database) works;
- alert close rule and summary reject-rate (40%) checks.

`tests/Kern.Tests/QualityGateTests.cs` — 10 xUnit facts mirroring the
harness scenarios (execution owed on Windows, see flags).

## 8. Gates

| Gate | Result |
|---|---|
| Full build `--no-incremental` | **0 errors, 0 warnings** |
| `tools/audit_window_context.py` | **exit 0** (Qc* rows present, 0 window-level direction pins) |
| Lang parity | **0 missing keys** in Ar/Fr/Tr vs En (pre-existing extras untouched) |
| Errors catalogs | INV-6011..6016 in En/Ar/Fr/Tr + `docs/ERROR_CATALOG.md` |
| Migrator idempotent double-run | ✅ (driver gate g) |

## 9. Owner flags / gaps

1. **WPF runtime owed** — the four windows compile and follow the
   BaseWin/RptBase patterns, but no Windows run has exercised them
   (open, RTL, save/complete clicks). Kern.Tests execution is likewise
   owed on Windows (net8.0-windows testhost).
2. **Live PostgreSQL owed** — the PG migration follows the 053–055
   patterns (`ADD COLUMN IF NOT EXISTS`, `ON CONFLICT`, uuid ids) but
   has not run against a live PG instance from this Linux machine.
3. **ReturnToSupplier document gap (bounded)** — the disposition posts
   the stock-out effect (`QCR` issue) and records the disposition, but
   does **not** auto-create a purchase-return draft document; the
   returns document flow does not exist yet. Flagged per the mandate.
4. **MFG order cancel vs QC-diverted production (bounded)** — cancel
   reads on-hand at the order's warehouse; for a QC-diverted order the
   stock sits in quarantine, so cancel fails honestly with the
   engine's insufficient-stock error until the inspection completes.
   No silent corruption; a QC-aware cancel is future work.
5. **Warn-mode draw with explicit batch/serial captures** — when a
   sale line carries explicit batch/serial selections they are honored
   exactly (no quarantine draw split for that line); the draw applies
   to auto-allocated lines, which is the standard invoice/POS path.
6. **MatCard template pickers** — the material card keeps its existing
   `quality_check_required` checkbox (same flag the gate reads);
   template assignment lives in QcTemplateWin's material grid.
   Extending the MatCard XAML with template combos is a possible
   follow-up, not a functional gap.
7. **Decisions taken** (flagged per the brief): Q1 = Warn default
   (per-company `quality_settings` table — adaptation recorded in §2);
   Q2 = dedicated quarantine warehouse; Q3 = formulas deferred;
   Q4 = outgoing gate included. Gate-mode reconciliation (quarantine
   in both modes, mode bites at point of use) is documented in §2.

---

<!-- source: docs/BUILD_PHASEB_057.md -->

# Build Phase B4 — CRM Pipeline (migration 057)

Branch: `build/phaseb-057-crm` (from `build/phaseb-056-quality` @
`87b3030`; the chain carries fix steps 1–13 + builds 054/055/056).
Design source: `~/workspace/research/phase-b-design.md` §4 (CRM) and
§7 (decision list). This document maps design → built, records the
C1–C3 decisions taken and the owner's entitlement postponement, the
schema, the pipeline/conversion design, the comparison tables, the
per-window pillars checklists, the headless proof, the gates, and the
owner flags.

## 1. What was built

| Layer | Artifact |
|---|---|
| Migration | `dba/mig/sqlite/057_crm.sql`, `dba/mig/postgresql/057_crm.sql` — `crm_stages`, `crm_leads`, `crm_opportunities`, `crm_opportunity_stage_history`, `crm_opportunity_documents`, `crm_activities`, `crm_lost_reasons` + `CRM.*` permissions (358–365) |
| Domain | `Entities/Crm.cs` (stages, lost reasons, leads, opportunities, history, document links, activities, pipeline board, constants), `Interfaces/ICrmRepository.cs`, error codes `CRM-9001..9006` (`ErrorCodes.cs`, new CRM 9000 block), `ModKey.Crm` registered |
| Data/service | `Infrastructure/CrmRepository.cs`, `Application/Services/CrmService.cs` (validation layer; repository re-checks state inside its transactions) |
| Windows | `Views/CrmLeadWin.cs`, `Views/CrmOppWin.cs`, `Views/CrmPipelineWin.cs`, `Views/CrmActivitiesWin.cs` — all `BaseWin`, bottom command bars, unique schedule tints (Orchid / Violet / Indigo / Coral) |
| Wiring | `AppHost.Crm` (+ `AppServiceHost` pass-through, rebound on company switch), `PermCat.Crm`, `RibbonNav` routes `CRM.Leads / CRM.Pipeline / CRM.Opportunities / CRM.Activities`, ribbon buttons on the MWin Customers tab (Follow-up group), audit script knows `AppHost.Crm` |
| i18n | `Crm.*` (95 leaf keys incl. `Col.*`) in En/Ar/Fr/Tr — identical key sets; Errors `CRM-9001..9006` in all 4 catalogs + `docs/ERROR_CATALOG.md` |
| Tests | `tests/Kern.Tests/CrmPipelineTests.cs` (6 facts: conversion, stage walk + weighted math, overdue activities, activity target rule, quotation no-effects, full round-trip) |

**Pre-existing CRM surface (honest baseline):** the sweep measured CRM
at 13% — an `appset.json` module entry ("Manage leads, opportunities
and follow-ups", enabled, no code behind it) plus the unrelated
`CRM.CUSTOMER.*` core customer-master permissions in `PermCat.Customers`.
There were no CRM tables, windows, routes or services. This build keeps
the module entry (now true), leaves the `CRM.CUSTOMER.*` permissions
untouched (they are core customer-card permissions), and builds the
pipeline fresh on migration 057.

## 2. Design vs built — with the C decisions

| Design element (§4) | Built | Notes |
|---|---|---|
| `ModKey.CRM` added to the canonical vocabulary | ✅ `ModKey.Crm = "CRM"` | Registered only — see the entitlement note below |
| CRM windows register `CRM.*` nav keys | ✅ RibbonNav `CRM.Leads/Pipeline/Opportunities/Activities` + MWin Customers-tab buttons with those keys | Shell gating can find them the day licensing lands |
| `CrmService` calls the entitlement gate at entry points | ❌ **Not built — owner override** | Licensing/entitlement work is **postponed by the owner**. CRM is fully functional under normal RBAC (`CRM.*` permissions, step-1 pattern) only. **No fake gate stands in its place.** Entitlement gating arrives with the postponed licensing work; the service and repository carry the note where the gate will go |
| `crm_stages` (per-company, editable, seeded New 10 → Qualified 25 → Proposition 50 → Negotiation 75 → Won 100 / Lost 0) | ✅ as designed | Seeded **per company on first use** by the repository (a migration cannot know future companies); stages are configuration (Odoo pattern), edited in CrmPipelineWin under `CRM.STAGE.EDIT` |
| `crm_leads` (own entity, status, source, campaign, rep, conversion links) | ✅ as designed | **C1 — Taken: lead is its own entity** (recommendation). Unqualified data never touches `parties` |
| `crm_opportunities` (party/lead links, stage, probability, amount, currency, close date, type, lost reason) | ✅ as designed | Status is **derived** from the stage's `stage_type` — no second status field |
| `crm_opportunity_documents` (dedicated link table) | ✅ as designed + surrogate `link_id` PK + `company_id` | Kept separate from `document_links` for the design's stated reason (TEXT CRM ids vs the integer document world) |
| `crm_activities` (exactly one of lead/opp/party, due + completion) | ✅ as designed | Exactly-one-target enforced in service **and** repository (`CRM-9005`); overdue is derived (Planned + past due), never a stored status |
| `crm_lost_reasons` (seeded Price / Competitor / No Budget / No Response / Timing / Project Cancelled) | ✅ as designed | Seeded per company on first use; required on a Lost move (`CRM-9006`, Odoo rule) |
| Stage-change history | ✅ **Added beyond the sketch** — `crm_opportunity_stage_history` | The task mandate requires every stage change recorded; creation writes the first row (from-stage null). Rows are read in insertion order (SQLite `rowid` / PG timestamp): `datetime('now')` has 1-second resolution and Guid ids are random, so timestamp+id ordering scrambled same-second moves — caught by the harness, fixed, pinned in tests |
| Lead conversion (one transaction → party + opportunity + links) | ✅ as designed | Party is inserted into the **unified master** (`parties`, kind `C`) with the same columns and the same `ledger_id` rule `MasterCardRepository` uses (above every parties/ledger identity in use) — no duplicated party logic, no second store. Party code auto-assigned `CUST-nnnn`; name prefers the lead's organization, else the lead name; phone/mobile/email/address/notes carried |
| Quotation = existing `InvoiceWin` Quotation kind | ✅ | **C3 — Taken: the app-layer quotation** (`InvDocKind.Quotation`, `sales_documents` doc_kind `QUO`), per the task mandate — NOT a separate quote module and NOT a parallel chain-only quote store. Ground truth that shapes the write: `InvRepo.Load` accepts **both** company keys since Fix Phase Step 9, while the step-9 chain (`OrderService.ConvertQuotationToOrderAsync`) requires the **Guid** key — so `CrmRepository.CreateQuotationAsync` writes the quotation row with the company's Guid key (resolved from `companies` by code). The same row therefore opens in InvoiceWin **and** converts onward through the step-9 chain. Multiple quotations per opportunity are supported (each create adds a link row) |
| Quotation → order → invoice follows the step-9 chain | ✅ proven end-to-end | Harness (d): CRM quotation (Draft, no effects) → chain `PostSalesAsync` (still effect-free) → `ConvertQuotationToOrderAsync` (order Draft + `document_links` QUO→SO) → CRM links the order → post + `ConvertToInvoiceAsync` → CRM links the invoice; only the invoice moves stock/GL |
| `CrmPipelineWin` board | ✅ grouped-list board | **C2 — Taken: grouped lists + move buttons** (recommendation), one section per stage with raw + weighted footers; Advance/Regress skip the Lost stage (losing needs a reason — done in CrmOppWin) |
| `CrmLeadWin` / `CrmOppWin` / `CrmActivitiesWin` | ✅ | OppWin carries the stage stepper, stage-history grid, documents grid (live status from `sales_documents`), activities are worked in CrmActivitiesWin (mine/open/overdue filters, complete-with-result in place) |
| Reports (funnel / win rate / stale opportunities) | ⚠️ **Partial** | The pipeline board's per-stage raw + weighted totals and the open weighted total are the funnel surface in v1; dedicated funnel / win-rate-by-rep / stale-opportunity **report windows** are a GAP (see §7) |
| Sales reps | ✅ stay CORE | CRM only references `sales_reps.code` (`assigned_rep_code`); reps, groups and targets were not moved, gated or rewritten |
| Permissions `CRM.LEAD.* / CRM.OPP.* / CRM.ACTIVITY.*` | ✅ + `CRM.STAGE.EDIT` | 8 permissions, display_order 358–365; ADMIN all, USER all but STAGE.EDIT, VIEWER the three VIEWs |
| Errors: small CRM range | ✅ `CRM-9001..9006` | New CRM 9000 block in `ErrorCodes.cs` (the LIC-precedent pattern): already-converted, lead state, stage invalid, quotation party/company unresolved, activity target, lost reason required |

## 3. Schema (migration 057, both providers)

Company keying is the **party world's company CODE** (TEXT) throughout
— the key `parties`/`sales_reps` use — except the quotation document
row itself, which carries the company Guid for the chain (see C3).

- `crm_stages(stage_id, company_id, name_en, name_ar, sequence, probability, stage_type[Open/Won/Lost], is_active, is_deleted, audit)`
- `crm_lost_reasons(reason_id, company_id, name_en, name_ar, is_active, is_deleted, audit)` — unique (company, name_en) among live rows
- `crm_leads(lead_id, company_id, lead_name, organization, email, phone, mobile, address, source, campaign, status[New/Working/Qualified/Converted/Unqualified], assigned_rep_code, converted_party_id → parties.id, converted_opportunity_id, notes, is_deleted, audit)`
- `crm_opportunities(opp_id, company_id, title, party_id → parties.id, lead_id → crm_leads, stage_id, probability, expected_amount, currency_code, expected_close_date, opp_type, source, campaign, assigned_rep_code, lost_reason_id, lost_notes, is_deleted, audit)`
- `crm_opportunity_stage_history(history_id, company_id, opp_id, from_stage_id, to_stage_id, from_probability, to_probability, changed_by, changed_at, notes)`
- `crm_opportunity_documents(link_id, company_id, opp_id, doc_kind, doc_id INTEGER, link_type[Quotation/Order/Invoice], created_by, created_at)` — unique (opp, kind, doc)
- `crm_activities(activity_id, company_id, subject, activity_type[Call/Meeting/Email/Task], due_at, completed_at, status[Planned/Done/Cancelled], lead_id/opp_id/party_id — exactly one, assigned_to, result_notes, is_deleted, audit)`

PG adaptations mirror 055/056: `uuid` PKs, `BOOLEAN` flags,
`TIMESTAMPTZ` audit stamps, `DATE` close date, `BIGINT` doc_id,
`ON CONFLICT` permission seeds. All writes are parameterized; every
multi-row write is one transaction; deletes are soft.

## 4. Pipeline & conversion design

- **Weighted value** = `expected_amount × probability / 100`, rounded
  to 4 dp (`CrmOpportunity.WeightedValue`); board column totals and the
  open-pipeline total are sums of the same property, so the card, the
  board and the harness can never disagree.
- **Stage moves** (`MoveStageAsync`, one transaction): stage +
  probability (stage default unless explicitly overridden), lost
  reason/notes written only for Lost targets (and required there),
  cleared when leaving Lost, plus the history row. Card saves never
  move the stage — moves exist only through this path, so the history
  is complete by construction.
- **Conversion** (`ConvertLeadAsync`, one transaction): refuse
  converted (`CRM-9001`) / unqualified (`CRM-9002`) → insert party
  (kind `C`, next `CUST-nnnn` code probed in-transaction, `ledger_id`
  = max(parties.ledger_id, party_ledger.party_id) + 1) → insert
  opportunity at the first Open stage (probability = stage default,
  type New Business, source/campaign/rep inherited) + creation history
  row → stamp lead `Converted` with both ids. Any failure rolls all
  three writes back.
- **Quotation creation** (`CreateQuotationAsync`, one transaction):
  opportunity must carry a party with a `ledger_id` (`CRM-9004`
  otherwise — convert/link first, the ERPNext/Odoo flow) → resolve the
  company Guid → next `QUO-yyyy-nnnn` number in the Guid namespace →
  insert the `sales_documents` Draft (party = `ledger_id`, totals from
  the supplied lines, or one line from title + expected amount) + line
  rows + the `crm_opportunity_documents` link. **No** `party_ledger`,
  journal or stock writes — Step 5's effect-free quotation, proven in
  the harness before and after the chain posts the quotation.
- **Activities**: due stored as `yyyy-MM-dd HH:mm` text; the overdue
  worklist is `status = Planned AND due_at < asOf` (lexicographic on
  the ISO text — correct for the stored format); completion stamps
  `completed_at` and keeps the result notes.

## 5. Comparison tables (acceptance gate)

### 5.1 Lead

| Capability | Al-Ameen V10 | Phenix v10 | ERPNext | Odoo | ShouTech 057 |
|---|---|---|---|---|---|
| Lead as separate entity | Rep/visit oriented; no public lead master | Rep management focus; no public lead master | ✅ Lead (person + organisation, source, campaign, territory) | ✅ `crm.lead` (lead/opportunity flag) | ✅ `crm_leads` (C1) with organisation, source, campaign |
| Lead statuses | — | — | Open / Working / Qualified / Converted… | Pipeline stages | ✅ New / Working / Qualified / Converted / Unqualified |
| Convert → customer + opportunity in one action | — | — | ✅ one action | ✅ create-or-link customer | ✅ one transaction → party (unified master) + opportunity + both links |
| Duplicate party store on conversion | — | — | No (Customer is the master) | No | ✅ No — inserts into `parties` itself; legacy stores stay dropped (061) |
| Assignment | Rep routes/targets | Rep + visits | Sales person / territory | Owner / team | ✅ CORE sales rep by code (reps stay core) |

### 5.2 Opportunity & pipeline

| Capability | Al-Ameen V10 | Phenix v10 | ERPNext | Odoo | ShouTech 057 |
|---|---|---|---|---|---|
| Pipeline stages | Not a headline module | Not a headline module | Status pipeline (Open → Quotation → Converted/Lost/Closed) | ✅ Kanban stages with default probability, fully configurable | ✅ Configurable stages (Open/Won/Lost) with default probability, seeded design pipeline |
| Probability / expected value / expected close | — | — | ✅ probability + expected closing | ✅ expected revenue + expected closing + probability | ✅ all three; weighted = amount × probability |
| Stage movement audited | — | — | Communications/timeline | Chatter | ✅ `crm_opportunity_stage_history` row per move (incl. creation) |
| Lost requires a reason | — | — | ✅ lost reasons recorded | ✅ lost reason required | ✅ `CRM-9006` + seeded reasons + notes |
| Board | — | — | List/report | ✅ Kanban (drag) | Grouped-list board + Advance/Regress buttons (C2); drag-and-drop deferred |
| Direct opportunity on an existing customer | — | — | ✅ | ✅ | ✅ party picker over the unified master |

### 5.3 Activities & quotations

| Capability | Al-Ameen V10 | Phenix v10 | ERPNext | Odoo | ShouTech 057 |
|---|---|---|---|---|---|
| Follow-up activities with deadlines | Visit plans (rep side) | Visit routes | ToDo / communications on the record | ✅ Activities (call/meeting/email) with deadlines + plans | ✅ Call/Meeting/Email/Task, due + completion + result, against lead/opp/party (exactly one) |
| Overdue worklist | — | — | ToDo lists | ✅ activity views/badges | ✅ Overdue filter + overdue counts in the worklist status bar |
| Opportunity → quotation | — | — | ✅ Quotation (standard sales document) | ✅ New Quotation (multiple/revisions normal) | ✅ Existing InvoiceWin Quotation kind (C3); multiple quotations per opportunity, each linked |
| Quotation → order → invoice chain | — | — | ✅ linked chain | ✅ sale order flow | ✅ Step-9 chain (`document_links` QUO→SO→…), CRM links order + invoice back onto the opportunity |
| Quotation side effects before conversion | — | — | None until order | None until order confirmed | ✅ None (Step 5): no GL / party ledger / stock — proven |

## 6. Pillars checklists

Legend: ✅ proven (harness/test/build evidence in §7) · 🪟 window code
complete but WPF runtime owed on Windows · GAP = listed in §8.

### CrmLeadWin
| Pillar | State |
|---|---|
| UI | ✅ code — list + full card (all lead fields), bottom bar (New/Save/Convert/Delete/Refresh/Close), Orchid tint |
| DB | ✅ `crm_leads` (057); every editor field persists (round-trip (e)) |
| Linking | ✅ rep picker from CORE `sales_reps`; conversion links stamped both ways; RibbonNav `CRM.Leads` + MWin button |
| Posting | ✅ conversion = the document effect: real party + opportunity through the existing master/engine paths in one transaction (no private GL/stock, ever) |
| Persistence | ✅ save/delete/convert all transactional via CrmService |
| Downstream | ✅ converted party is invoiceable immediately (unified master + ledger_id); opportunity appears on the board |

### CrmOppWin
| Pillar | State |
|---|---|
| UI | ✅ code — list + card + stage stepper + history grid + documents grid + link/open actions, Violet tint |
| DB | ✅ `crm_opportunities` + history + documents (057); card fields round-trip (e) |
| Linking | ✅ party picker (unified master), lead origin kept, stage/reason combos from company config, `CRM.Opportunities` route + button; opens quotations in InvoiceWin |
| Posting | ✅ quotation creation = effect-free document through the existing document store (Step 5); onward conversion is the step-9 chain's posting, not CRM's |
| Persistence | ✅ stage moves only via MoveStageAsync (history complete); lost reason enforced |
| Downstream | ✅ documents tab shows live doc status/totals; board totals include the opportunity |

### CrmPipelineWin
| Pillar | State |
|---|---|
| UI | ✅ code — grouped-list board (C2) with per-stage raw + weighted footers, stage + lost-reason configuration panel, Indigo tint |
| DB | ✅ reads `GetPipelineAsync`; stage/reason edits persist to `crm_stages`/`crm_lost_reasons` |
| Linking | ✅ Advance/Regress move through the same MoveStageAsync as the editor; Open launches CrmOppWin focused on the record |
| Posting | n/a by design — the board moves pipeline state (audited in history); it writes no documents |
| Persistence | ✅ every move lands in `crm_opportunity_stage_history` |
| Downstream | ✅ open weighted total is the funnel headline; Won/Lost columns close the pipeline picture |

### CrmActivitiesWin
| Pillar | State |
|---|---|
| UI | ✅ code — worklist with Open/Overdue filters + editor (target kind + target pickers), Coral tint |
| DB | ✅ `crm_activities` (057); subject/type/due/target/assignee/result all persist |
| Linking | ✅ targets resolve against leads/opportunities/parties of the same company; `CRM.Activities` route + button |
| Posting | n/a by design — activities carry no GL/stock effect in any compared ERP |
| Persistence | ✅ complete stamps `completed_at` + result in one transaction; completed rows leave the overdue list (c) |
| Downstream | ✅ overdue surface = the follow-up worklist the design requires; per-record activities readable via `GetActivitiesForAsync` |

## 7. Proof (headless, SQLite, real services)

Driver: `~/workspace/research/phaseb-057/crm57_harness.py`
(C# harness in `~/workspace/research/phaseb-057/harness/`) — migrates
with the real `tools/SqlMigrator`, then runs the real
`CrmService`/`CrmRepository` + the real step-9 chain services.
**Result: DRIVER ALL PASS / HARNESS ALL PASS** (57 checks).

- **(a) Conversion:** lead → qualify → convert → party row in `parties`
  with the lead's name/org/email/phone/mobile/address and a fresh
  `ledger_id`; lead `Converted` with both links; exactly one party row;
  `customers`/`suppliers` tables absent (no duplicate store); second
  conversion → `CRM-9001`; CORE rep offered by reference only.
- **(b) Stage walk:** New→Qualified→Proposition→Negotiation with
  weighted values 1000 → 2500 → 5000 → 7500 on amount 10,000 (10/25/50/
  75%), column totals equal at each stage; 4 history rows (creation +
  3 moves) with chained from/to probabilities; Lost without reason →
  `CRM-9006`; with reason → probability 0, reason + notes stored.
- **(c) Activities:** due-yesterday activity appears in
  `GetOverdueActivitiesAsync`; completing it removes it and leaves it
  Done with its result notes.
- **(d) Quotation chain:** quotation Draft `QUO-…` total 50 for the
  opportunity's party; **zero** `party_ledger` / `journal_entries` /
  `material_stock_movements` rows after creation **and** after chain
  posting; chain converts to a sales-order Draft (total carried,
  `document_links` QUO→SO written); CRM links order + invoice;
  converted invoice posts GL and deducts stock 100 → 98 — effects begin
  at conversion, exactly per Step 5/9.
- **(e) Round-trip:** fully populated lead and opportunity reload
  field-by-field equal (incl. party `ledger_id` resolution and the
  12,345.67 × 55% = 6,790.1185 weighted value).
- **(f) Migration:** first run `APPLY 057_crm`, second run
  `SKIP 057_crm` (idempotent double-run), exactly one ledger row, 7
  tables, 8 permissions; late arrival on a 064 database re-applies
  cleanly and grants all 8 permissions to an existing
  `ROLE_ADMINISTRATOR` (roles are app-seeded; a migrator-only DB has
  none — same as 053–056).

Kern.Tests: `CrmPipelineTests` (6 facts mirroring (a)–(e)) compile
green; **execution is owed on Windows** (net8.0-windows testhost, the
standing limitation of every phase).

## 8. Gates

| Gate | Result |
|---|---|
| Full build `--no-incremental` | ✅ **0 errors, 0 warnings** |
| `tools/audit_window_context.py` | ✅ exit 0; all four CRM windows listed as BaseWin/central |
| Lang parity | ✅ `Crm.*` = 95 leaf keys, identical sets in En/Ar/Fr/Tr; Errors `CRM-9001..9006` in all 4 (repo-wide pre-existing extras — Ar +90, Fr/Tr +9 vs En — are unchanged from the base branch and not CRM keys) |
| Migrator idempotent double-run | ✅ APPLY then SKIP (harness (f)), incl. late arrival |
| No Arabic outside Ar.json | ✅ new/changed code + SQL files contain no Arabic (stage/reason seed `name_ar` values are empty strings; Arabic lives only in the Ar catalogs) |
| WPF runtime | ⚠️ **Owed on Windows** — windows compile and follow the proven BaseWin patterns; visual/RTL pass not possible from Linux |
| Live PostgreSQL | ⚠️ **Owed** — PG migration + repository are provider-mirrored after 055/056; no live PG run in this environment |

## 9. Owner flags / gaps

1. **Entitlement gating is NOT built** (owner postponement): no
   `EnsureModule("CRM")` call exists anywhere; CRM is RBAC-only. When
   licensing lands, the gate hooks are `CrmService` entry points +
   the already-registered `ModKey.Crm` and `CRM.*` route keys.
2. **Dedicated CRM reports are a GAP:** funnel-by-stage beyond the
   board totals, win-rate by rep, and stale-opportunity (no activity
   in N days) report windows from design §4.2 were not built; the
   board + worklist cover the operational surfaces in v1.
3. **Drag-and-drop kanban** remains deferred per C2 (grouped-list
   board shipped).
4. **Quotation company-key note (C3):** CRM quotations are written
   Guid-keyed so the step-9 chain can convert them; `InvRepo.NextDocNo`
   counts only code-keyed rows, so a company mixing hand-made
   InvoiceWin quotations and CRM quotations could see both families
   number from their own sequences (numbers stay unique per
   (company, kind, doc_no) only within each key namespace — the
   pre-existing dual-key reality of the document tables, surfaced here
   rather than papered over).
5. **Converted-party code** is auto-assigned (`CUST-nnnn`); if the
   owner wants a dedicated CRM conversion numbering series or a code
   prompt at conversion time, that is a small follow-up in
   `CrmLeadWin`/`CrmRepository.NextPartyCodeAsync`.
6. **WPF + live PG verification owed** (see §8) — including the
   Windows test-suite run for `CrmPipelineTests`.
