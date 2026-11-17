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
