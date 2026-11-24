# Period Close — study and as-built design

Branch: `feat/period-close` · Base: `origin/reduce` @ `4eafea21`
Migration taken: **127** (both dialects) — flag: `feat/permissions-templates`
carries a colliding `125_permission_templates.sql` and `feat/licensing` may
take further numbers; renumber at merge if another branch lands first.

## 1. What already exists (verified in code, not assumed)

| Piece | Where | State |
|---|---|---|
| `fiscal_periods` table: `status` OPEN/CLOSED/LOCKED, `closed_by`, `closed_at` | migration 009 (both dialects) | complete |
| Period-close engine: refuses close while draft journals exist in the period, while any posted journal in it is unbalanced, and out of chronological order; sets CLOSED + closed_by/at in one transaction | `AccountingStructureRepository.ClosePeriodAsync` | **orphaned — no caller anywhere** |
| Journal post guard: DB triggers refuse posting an entry whose period is not OPEN and matching the entry date; direct reopen of CLOSED/LOCKED periods refused at DB level | migration 009 triggers | complete (backstop) |
| GL resolver: every document GL posting (`GlPosting.PostInTransaction`) and every manual journal post (`JournalRepository.PostAsync`) resolves the period through `GlPosting.ResolveOpenPeriod`, which refuses a non-open period | `src/Infra/GlPosting.cs` | refuses, but with a generic non-catalog `InvalidOperationException` that does **not** name the period and cannot distinguish "no period defined" from "period closed" |
| Stock posting: `StkPost.ApplyDocument` / `PostTransfer` / `PostCount` | `cod/app/src/Services/StkPost.cs` | **no period check at all** — stock-only documents (stock entry/exit, opening stock, transfers, counts) bypass the GL guard entirely |
| Year close UI + engine | `RolloverWin`, `OperationsService.CloseFiscalYearAsync` | complete (year only) |
| Month period-close UI | — | **absent** (the long-standing open flag this build closes) |
| Reports: trial-balance integrity lists periods by name | `OperationsRepository.GetTrialBalanceIntegrityAsync` → `MaintenanceWin` | period rows carry **no status** |
| Permissions | `PermCat` (reflection catalog) + runtime seeding in `Data.cs`; `ACC.FISCAL.ROLL_FORWARD` is the model (protected code, not granted to the default user role) | pattern to follow |
| Audit | `security_audit_log` (migration 029); `fiscal_periods.closed_by/closed_at` | no period event history |

Posting-path trace (every path lands on a traced choke point):

- Manual journals → `JournalRepository.PostAsync` → `ResolveOpenPeriod` ✔
- Invoices, returns, vouchers, orders (all `InvRepo.Post`) → `GlPosting.PostInTransaction` → `ResolveOpenPeriod` ✔ GL; stock side → `StkPost.ApplyDocument` ✘ (fixed by this build)
- POS sales/returns → build `InvDoc` → `InvRepo.Post` ✔/✘ as above
- Stock entry/exit, opening stock → `StkPost.ApplyDocument` only ✘ (fixed)
- Transfers → `StkPost.PostTransfer` ✘ (fixed); counts → `StkPost.PostCount` ✘ (fixed)
- Manufacturing → `MfgOrderRepository` posts GL via `PostInTransaction` inside the same transaction as its `StockValuationEngine` movements, so a GL refusal rolls the stock back with it ✔
- HR payroll, fixed assets → `GlPosting.PostInTransaction` ✔

## 2. Benchmark patterns (functional scope only; form is our own)

- **ERPNext** separates the two controls this build must not conflate:
  the *Period Closing Voucher* moves P&L to equity (ShouTech's year close
  already does this job via RolloverWin + the closing-entry requirement),
  while an *Accounting Period* blocks document submission in a finished
  range, and *Accounts Frozen Upto* adds a broad cut-off with a controlled
  role exception. ERPNext also blocks a new closing while an earlier
  period is still open — the same sequential rule our engine enforces.
  ShouTech's `fiscal_periods.status` is the Accounting Period analogue;
  this build gives it its missing window and its missing stock-side guard.
- **Odoo** uses lock dates: locking is an admin act, and changing the lock
  afterwards is itself an admin act that leaves a record. That is the
  model for reopen here: separate permission, mandatory reason, durable
  event record — never a silent flag flip.
- **Al-Ameen** (from the owner's help corpus): period/year close is
  preceded by surfacing unposted vouchers (سندات غير مرحّلة) so nothing is
  stranded by the lock — the same pre-close listing this window shows.

## 3. Design rulings

1. **One engine.** The window calls the existing
   `AccountingStructureRepository.ClosePeriodAsync` through the
   Operations layer (the live layer RolloverWin uses). No parallel close
   mechanism is created. The engine gains exactly one missing check —
   unposted **documents** (draft sales/purchase documents dated inside
   the period) alongside its existing draft-journal check — because a
   draft document dated in the period can never post after the close
   (the guard would refuse it) and must not be stranded silently.
2. **Close blocks on unposted items; the window lists them first.**
   Journal drafts (by assigned period) and draft documents (by date in
   range) are shown in the window before closing and refuse the close
   (ACC-5022) until posted or removed. This matches the engine's existing
   draft-journal philosophy and the Al-Ameen pre-close pattern.
3. **The guard speaks the catalog and names the period.**
   `ResolveOpenPeriod` keeps its contract, but when the date is covered
   by a CLOSED/LOCKED period (or a closed year) it now throws
   `AppError` **ACC-5021** naming the period and its date range, instead
   of the generic message. A new `GlPosting.EnsureOpenPeriodForDate`
   carries the same closed-period refusal to the stock paths
   (`StkPost`), with one deliberate difference: where **no** period
   covers the date at all, the stock guard stays silent (legacy
   companies without fiscal years keep posting stock, exactly as
   today); the GL resolver keeps its stricter existing refusal there.
4. **Permissions.** Two new protected catalog keys, modelled on
   `ACC.FISCAL.ROLL_FORWARD`: `ACC.PERIOD.CLOSE` and
   `ACC.PERIOD.REOPEN` (seeded by the existing runtime seeder; withheld
   from the default user role by the protected-codes set; administrators
   receive them through the administrator rule). The window disables its
   actions without the keys and re-checks through the audited
   `PermServ.Require` seam before acting — the same central
   denial-logging hook document actions use.
5. **Reopen is a recorded exception, not a toggle.** Migration 127 adds
   `fiscal_period_events` (CLOSE / REOPEN ledger with actor, reason,
   timestamp) and re-arms the DB reopen trigger in both dialects:
   CLOSED→OPEN is permitted only when a REOPEN event for that period
   exists recorded at or after the period's `closed_at`; LOCKED remains
   terminal. The reopen operation requires `ACC.PERIOD.REOPEN`, a
   non-blank reason, writes the event + a `security_audit_log` row and
   clears `closed_by/closed_at` in one transaction. CLOSE events are
   written by the engine's close transaction on the same ledger, so the
   full state history of every period is queryable.
6. **Reports show the state.** `TrialBalancePeriodRow` gains the
   period's status and the trial-balance integrity surface (the report
   that lists periods) marks closed periods as closed.
7. **Scope discipline.** Closing is per-company by construction: every
   query is keyed to the current company (code at the Operations layer,
   Guid inside the engine), and the harness proves a second company's
   same-named period stays open and postable.

## 4. New error codes (ACC family, next free block)

| Code | Name | Meaning |
|---|---|---|
| ACC-5021 | PeriodClosedPosting | A posting dated into a closed/locked period was refused; message names the period and its range |
| ACC-5022 | PeriodCloseUnpostedItems | Close refused: unposted journals/documents remain in the period |
| ACC-5023 | PeriodCloseOutOfOrder | Close refused: an earlier period is still open |
| ACC-5024 | PeriodNotClosable | Period not found, already closed, or locked |
| ACC-5025 | PeriodReopenReasonRequired | Reopen refused: a reason is required |
| ACC-5026 | PeriodCloseUnbalancedJournals | Close refused: a posted journal in the period is unbalanced |

## 5. Window

`PeriodCloseWin` (BaseWin; grid tint **Tuff** — a new brush added to
the central palette, since all 98 existing tints were already claimed
in the census), opened from Other Tools beside Rollover
(`OtherTools.PeriodClose`). Fiscal-year selector for the current
company, periods grid (number, name, dates, status, closed by/at),
unposted-items grid for the selected period, Close and Reopen (with
reason) actions in the bottom bar. Inherits live shell context through
BaseWin like every sibling window; ≤50% first-open via the central
policy. The close record itself (status, closed_by/at, event ledger)
is the persisted state; the year selection is navigation state, at
parity with RolloverWin.

## 6. Proof

`tools/PeriodHarness` (InfoHarness pattern) on seeded test companies —
see its PASS/FAIL list in the completion report. The first live close
on a real company and the Windows click-through remain the owner's run.

## 7. Owner evidence addendum — the fiscal-period control (Phenix, advanced options)

Owner-supplied (2026-11-24): in Phenix's Tools → Advanced Options there is a
fiscal-period control — a **checkbox** that activates when the period ends;
**deactivating** it keeps the program working by continuing into another
financial period (**it adds another year**). Under the checkbox are **two
fields: period start and period end**. Follow-up ruling: the checkbox is
**checked by default** — period-end activation is ON out of the box, and a
fresh company starts with it on.

Functional scope adopted (IP rule — our own form, no copied layout):

- **One store, no parallel settings.** The period bounds already live in
  `fiscal_years.start_date/end_date` (the 12 `fiscal_periods` derive from
  them; journal entries attach to periods by date). The checkbox state joins
  the same store: migration 127 adds `fiscal_years.period_end_activation`
  (default ON in both dialects — every existing row and every newly created
  year starts activated; the year creators insert without naming the
  column, so the default applies).
- **Surfaces (named consumers both ways).** (a) OptWin gains a company-scope
  **Fiscal** section (same `BeginSection` idiom as Backup/Numbering): the
  activation checkbox + the current (latest) fiscal year's start/end
  fields, read from `fiscal_years` through the Operations layer.
  (b) PeriodCloseWin reads the same rows — its periods grid is built from
  them and it mirrors the activation state + bounds of the selected year.
- **Deactivation = the audited roll-forward.** Unchecking the box is a
  deliberate act: it asks for confirmation, requires
  `ACC.FISCAL.ROLL_FORWARD` (RolloverWin's own permission — reconciled, not
  duplicated) via `PermServ.Require`, then in ONE transaction flips the
  latest year's flag OFF, creates the next fiscal year with its 12 OPEN
  periods using the exact semantics of the existing
  `CreateNextFiscalYearAsync` (day after the latest end, same length — the
  path RolloverWin's "Create next year" button already owns), and writes a
  `security_audit_log` row (`FISCAL_YEAR_ROLL_FORWARD`, actor + years).
  The new year is created with activation ON (schema default), so the
  control re-arms for the new period — matching "deactivate → the program
  continues into another financial period".
- **While active at period end**, the period is treated as ended per the
  close semantics of §3: dates beyond the year's end have no open period,
  so the posting resolver refuses them; PeriodCloseWin surfaces a
  "period ended" status for an activated latest year whose end has passed.
- **Bounds are display fields, not free edits.** In our schema the bounds
  are structural (period rows and journal period assignments derive from
  them); free-text editing would desync posted history. The sanctioned
  mutation is the audited roll-forward above, which replaces the displayed
  bounds with the next year's. This is the deliberate reconciliation of the
  Phenix pattern with our schema, recorded here rather than shipping a
  dangerous fake edit.
