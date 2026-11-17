# FIX PHASE — STEP 11: Router mode-stickiness + MFG report crashes + MFG unit equivalents

Branch `fix/step11-router-mfg` on top of `fix/step10-data-exchange`
(`49217ae`). **Not pushed, not merged** — awaiting the owner's call.

Commits:

| Commit | Content |
|---|---|
| `385a29c` | One `UnitEquivalents` converter in Infrastructure; chain/order repositories delegate to it |
| `02fb1d3` | MFG quantities cross into stock in base units (consumption, finished receipt, cancel symmetry) — MFG-2 |
| `f6824f0` | MFG variance/feasibility reports re-sourced to the migration-034 schema; feasibility Required in base units — MFG-1/MFG-4 |
| `5eecd1c` | Mode-aware router activation: `WindowActivator` + `IWindowMode` on all mode-ful windows; DatWin cache and ComWin scan mode-aware — S-2/S-3 |
| `2ec368c` | Harness-caught fix: tuple element shadowing `object.GetType()` in `WindowActivator.Show` |
| `0f86a4a` | Kern.Tests: `RouterMfgStep11Tests` (8 facts) |

No migration was added (none was needed — every fix works against the
schema that exists). No Lang keys were added (no new user-visible
strings; mode keys are internal identifiers), so Lang parity is
unchanged at 0 missing / 0 extra in all four catalogs, and no Lang or
Errors file differs from the base branch.

---

## 1. Router mode-stickiness (sweep S-2, S-3; Step-8 cheque pattern generalized)

### The policy (one, applied everywhere)

**The requested mode wins.** A window's identity for activation is
**(runtime type, ModeKey)**:

- a visible window of the same type **and the same mode** → reactivate
  it (restore from minimized + `Activate()`); the newly constructed
  instance is never shown;
- only a **different-mode** window of that type is open → open a
  **second instance** in the requested mode, side by side. An open
  window is never re-purposed to another mode: it holds per-mode /
  per-document state (a half-edited sales invoice must not become a
  purchase invoice; a cheque book must not flip direction);
- single-mode windows do not implement `IWindowMode` — both keys are
  null, the comparison degrades to the historical type-only behaviour,
  untouched.

This is exactly the rule Step 8 applied locally to cheques
(kind-aware dedupe); Step 11 deletes that local copy and makes the
rule the router's only rule. It also matches the professional ERPs:
ERPNext/Odoo open a second form for a second document type rather
than mutating the open one.

### Mechanism (router layer only — no per-call-site hacks)

- `cod/app/src/Services/WindowActivator.cs` (new):
  `IWindowMode { string ModeKey { get; } }`; `WindowModePolicy` — the
  pure decision core (`ModeKeyOf`, `IsMatch`, `Decide` →
  `ReactivateExisting | OpenNew`), no WPF state, unit-testable;
  `WindowActivator.Show(window, owner)` — scans visible windows,
  decides, activates or shows; failures are reported through
  `AppHost.Errors.Report(ErrorCodes.NavigationFailed, …)` and the
  window is still shown (a scan failure must never swallow a
  navigation request). `WindowActivator.TryActivateExisting<T>(modeKey)`
  for cached-window call sites.
- `RptNav.Show` and `RibbonNav.Show` now delegate to
  `WindowActivator.Show`. `RptNav`'s Step-8 `ShowCheque` kind-aware
  dedupe is deleted — cheques flow through the general policy via
  `ChqWin.ModeKey` with identical behaviour. `RibbonNav.ShowDef` goes
  through the same `Show` (`DefManagerWin.ModeKey == SetKey`, so two
  different definition sets can be open at once; the same set
  reactivates — the behaviour `ShowDef` already had, now central).
- `RibbonNav`'s silent catches are gone (sweep S-7, router part):
  `Show`'s empty `catch {}` is replaced by the activator's reported
  path; `LockWorkStation`'s silent catch now reports
  `ErrorCodes.ShellOperationFailed`. (S-7's non-router silent catches
  — e.g. inside DatWin/Backup — were already handled in Step 10 or
  remain outside this step's mandate; see §5.)
- `MWin.OpenDataWindow(bool exportMode)` (S-3): the cached `_datWindow`
  is reactivated **only when its current `ModeKey` matches the
  requested mode** (`StructuredExport` for export, `Import`
  otherwise); otherwise an already-open same-mode instance is
  activated via `TryActivateExisting<DatWin>`, else a new instance is
  created (the stale-mode cached window stays open in its own mode —
  DatWin's in-window mode cards remain the way to flip a live
  instance deliberately, which is a user action, not a router
  substitution). `MainShVM.OpenCompanyWindow`'s existing-window scan
  now also requires `Mode == ComWinMode.Management`, so a visible
  Startup-mode `ComWin` can no longer satisfy a Management request.

### Mode-ful window inventory (every window the routers open in >1 mode)

| Window | Mode source | ModeKey | Notes |
|---|---|---|---|
| `InvoiceWin` | `InvDocKind` (9 kinds) + optional loaded document | `{Kind}:{docId\|"new"}` | A window showing saved doc 5 ≠ a new-doc window of the same kind |
| `CshWin` | `CshKind` Receipt/Payment | kind | `Kind` accessor added |
| `OrdWin` | `OrdKind` Sales/Purchase | kind | `Kind` accessor added |
| `ChqWin` | `ChqKind` Incoming/Outgoing | kind | Step-8 local dedupe retired into the general policy |
| `MfgVarianceWin` | string mode Total/Raw/Overhead/Finished | mode | |
| `ClosingAccountsWin` | string mode Operations/Trading | mode | |
| `DocSearchWin` | `DocSearchMode` Invoices/Quotations/Orders | mode | |
| `GroupManagerWin` | `GroupKind` Party/SalesRep | kind | `Kind` accessor added |
| `SupportWin` | `SupportWinMode` Support/Suggestion | mode | `Mode` accessor added |
| `DefManagerWin` | string set key | set key | Already mode-correct pre-step; now via the central policy |
| `DatWin` | `ExchangeMode` Import/Export/StructuredExport (ctor bool + live mode cards) | **current** mode (tracks live card switches) | MWin cache fixed per S-3 (above) |
| `ComWin` | `ComWinMode` Startup/Management | mode | MainShVM scan fixed (above) |
| `PrmWin` | target user id (ctor string) | user id | Two users' permission windows can coexist; same user reactivates |

Everything else the routers open is single-mode and untouched.
`WhsWin.OpenStockCount(bool)` is a modal inline dialog, not
router-deduped, so it is not part of this inventory. Windows opened
outside the routers (e.g. `DocSearchWin` opening a document window
directly) construct exactly the document requested and are unaffected.

### Harness-caught defect in this step's own code

The first harness run failed every reactivation scenario while the
pure policy checks passed: in `WindowActivator.Show` the open-window
projection's second tuple element was name-inferred from
`w.GetType()`, so `o.GetType()` in the decision calls invoked
`object.GetType()` **on the tuple** (the `ValueTuple` runtime type)
instead of the window's type — reactivation could never match. Fixed
in `2ec368c` (element named `Type` explicitly); all scenarios pass.
This is recorded because it is precisely the class of bug the
mandate's harness proof exists to catch: the policy was right, the
plumbing was not, and no build gate could see it.

---

## 2. Manufacturing reports (sweep MFG-1, MFG-4)

### Inventory — every MFG report query, crash status, disposition

| Report (window → query) | Before | After |
|---|---|---|
| `MfgCostsWin` → `RptQuery.MfgOrderCostsAsync` | worked | unchanged (regression-pinned by harness + tests) |
| `MfgVarianceWin` Total/Raw/Overhead/Finished → `RptQuery.MfgVarianceAsync` | **crashed on open, all modes** | fixed, re-sourced (below) |
| `MfgFeasibilityWin` → `RptQuery.MfgFeasibilityAsync` | **crashed on open** | fixed, re-sourced; Required restated in base units |
| `RepostWin` (mfg mode) → `MfgRepostAsync` | worked (header totals) | untouched — out of this step's defect set |

### Root cause

Both broken queries were written against a schema that never shipped:
they joined `o.order_id = l.order_id AND o.company_id = l.company_id`
and filtered `l.company_id = $company`. Migration 034 (the real MFG
schema) gives `mfg_orders` the primary key **`id`** and
`mfg_order_lines` **no `company_id` column at all** — company scope
lives only on the header. Every execution raised "no such column"
(SQLite) / the PostgreSQL equivalent. The harness reproduces the
crash from the base commit's exact SQL on a migrated fixture and
shows the fixed SQL returning the seeded rows (§6).

### Re-sourcing decisions (concepts, not just column names)

- **Line-level modes** (Total/Raw/Overhead) now join
  `o.id = l.order_id` and scope `o.company_id = $company`.
- **Mode semantics follow the domain that actually exists.**
  `MfgLineKinds.All` is exactly `RAW` and `COST`; the old filters
  hunted `OVERHEAD/ADDITIONAL/LABOUR/OTHER` and
  `FINISHED/OUTPUT/PRODUCT` line kinds that can never be written
  (MFG-4 — "absent by construction"). New mapping: **Total** = all
  lines; **Raw** = `line_kind='RAW'`; **Overhead** =
  `line_kind='COST'` — the additional-cost lines (services, labour
  charged as lines). The header `overhead_amount` lump has no
  standard counterpart, so it carries no line variance and stays out
  of this report (it is fully visible in `MfgOrderCosts`).
- **Finished** is a header-level concept (a finished item is the
  order header, never a line): the mode now reads `mfg_orders`
  directly — Amount = `total_cost`, Standard =
  `qty × standard_unit_cost`, Variance = the difference, in the same
  row shape as the line modes (`LineKind='FINISHED'`).
- **Feasibility** joins `l.order_id = o.id`, considers RAW lines of
  Draft orders, and keys the on-hand subquery by the header's
  company. Its `Required` column is now restated in **base units**
  through the invoice path's own converter (`StkPost.ToBaseUnits`):
  Required and OnHand were previously compared across units (a line
  asking 4 × 250 g read as "4 required" against a kilogram balance).
  The query also returns the line's material id (company+code join)
  so the conversion resolves the right material; a line whose item
  code resolves to no material keeps its raw quantity (nothing to
  convert against) — visible behaviour, not a silent catch: query
  failures follow the file's established `QueryAsync` pattern
  (catalog report + `AppError`).

---

## 3. Manufacturing unit equivalents (sweep MFG-2)

### The rule (owner's, from MATCARD_FIXES defect 5)

Stock moves in **base units**; a line expressed in unit N moves
`qty × equivalent(N)`, and any per-unit price restates as
`price ÷ equivalent(N)`. The invoice path has applied this since the
MatCard fix; manufacturing ignored it — `MfgOrderRepository` handed
the valuation engine the **display** quantities verbatim.

### One converter (no second implementation)

`src/ShouTech.Infrastructure/UnitEquivalents.cs` (new) is now the
single Infrastructure converter: `ResolveAsync(connection,
transaction, companyId?, materialId, unitName)` (units_json first,
unit1..unit5 column pairs as fallback, first unit = base with factor
1, base/unknown/free-typed/non-positive → 1 — the exact historical
rule) and `ToBase(qty, unitPrice, equivalent)`. The two private
copies in `CommercialDocumentRepository` and
`OrderDocumentRepository` are deleted; both delegate. The app-layer
`StkPost.ToBaseUnits` (used by invoice/card posting and now by
feasibility) already implemented the identical rule and is unchanged.

### Quantity-conversion trace (BOM line → stock)

```
MfgWin grid line (display unit kept on the document, e.g. 4 × "g250")
  → MfgOrder / MfgOrderLine persisted verbatim (display qty + unit)
  → MfgOrderRepository.PostAsync
      RAW line:  equivalent = UnitEquivalents.ResolveAsync(company-scoped)
                 baseQty    = line.Qty × equivalent            (4 × 0.25 = 1)
                 on-hand guard compares baseQty (was: display qty)
                 StockValuationEngine.Apply(QtyOut: baseQty)   → engine cost
      Finished:  finishedBaseQty = order.Qty × equivalent(finished unit)
                 receipt unit cost = TotalCost ÷ finishedBaseQty
                                   (order.UnitCost unchanged when eq = 1)
                 StockValuationEngine.Apply(QtyIn: finishedBaseQty)
  → CancelAsync mirrors both conversions exactly (finished guard +
    MFG_PRODUCE_REV use finishedBaseQty; raw re-entry converts
    qty AND unit cost via ToBase; the LinkedRate original-rate
    mechanism is untouched)
```

Persisted order totals stay display-based (`ComputeTotals`
unchanged): the document reads as the user typed it; only quantities
crossing into stock convert. `CardStore.PostOrder` (the migration-050
`manufacturing_orders` family) moved raw quantities through the
engine the same way and is converted via `StkPost.ToBaseUnits` for
consistency — it currently has **zero callers** (sweep MFG-7), so
this is correctness hygiene on a shipped writer, not a behaviour
change in any live flow; stated plainly so the owner is not misled.

**Worked example (harness-proven, §6):** RM base KG, `g250` = 0.25;
FG base PCS, `DOZEN` = 12. Order: produce 2 DOZEN of FG, BOM line
4 × g250 of RM @ typed 2.5, overhead 14, opening stock 2 KG @ 8.
Post → consumption movement **qty_out = 1** at engine avg cost
(cost_amount **−8**, not the typed price); RM balance 1 KG / 8;
finished receipt **qty_in = 24** at unit cost **1** (total cost
24 ÷ 24 base units); FG balance 24 / 24; GL completion entry
Dr = Cr = 8. Before this step the same order **could not post at
all** (guard compared display 4 > on-hand 2). Cancel restores
2 KG / 16 and 0 / 0. A 400 × g250 line (= 100 KG) is refused.

---

## 4. Comparison tables (gate — from `research/sweep/compare-mfg-hr-crm.md`, MFG parts)

Step 11 changes **correctness**, not features: no new MFG window,
field, or workflow was added (the build-out is a later build-phase
step). The tables below are the sweep's, with the Step-11 effect
marked; grades move only where a crashed/absent-by-construction item
became usable.

### 4.1 MfgWin — Production Order (sweep §1.1/§1.2; CORE 20, was 6 usable → 30%)

Condensed to the rows this step touches; the full 20-row field table
is unchanged by this step and lives in the sweep document.

| Workflow step | ShouTech before Step 11 | ShouTech after Step 11 | ERPNext | Odoo | Al-Ameen / Phenix ⚠ | SAP B1 |
|---|---|---|---|---|---|---|
| Reserve / check components | ❌ Feasibility crashes (MFG-1) | ✅ Feasibility opens; Required in base units | ✅ reserved qty; shortage | ✅ availability check | ✅ capability ⚠ | ✅ MRP / availability |
| Issue materials (planned vs actual) | ⚠ inside Post, **display qty** (MFG-2) | ⚠ inside Post, **base qty at engine cost** | ✅ Stock Entries | ✅ component moves | ✅ disbursement voucher ⚠ | ✅ Issue for Production |
| Receive finished | ⚠ inside Post, display qty | ⚠ inside Post, **base qty; unit cost = total ÷ base qty** | ✅ Manufacture entry, partial | ✅ Done + by-products | ✅ ⚠ | ✅ Receipt, partial |
| Cancel / reverse | ✅ stock reversal (display qty) | ✅ stock reversal, base-symmetric | ✅ reverses SL+GL | ✅ cancel/unbuild | ✅ ⚠ | ✅ reversals |

MfgWin grade itself is **unchanged at 30%** — the window gained no
CORE field or action; what changed is that its posting is now
quantity-correct and its companion reports open. (The sweep's §1.2
"Post to GL ❌ no journal" line is stale against the current code:
`MfgOrderRepository` has posted a balanced GL completion entry via
`GlPosting` since the Step-3/6 era — harness scenario M3 pins
Dr = Cr = engine consumption cost. WIP staging and variance
accounts remain absent; that is MFG-5's real residue and a
build-phase item.)

### 4.2 Manufacturing Reports (sweep §1.6; CORE 12, was 2 usable → 17%)

| Report | ShouTech after Step 11 | Counterpart equivalent | Verdict |
|---|---|---|---|
| Manufactured Material Costs | ✅ works (unchanged, regression-pinned) | ERPNext Work Order summary; Odoo Cost Analysis | Present |
| Finished Goods Cost Variance ("Total") | ✅ **opens**; all lines, actual vs standard | ERPNext planned vs actual; SAP B1 variance | **CORE — now present** |
| Raw Material Cost Variance ("Raw") | ✅ **opens**; RAW lines only | Per-component variance | **CORE — now present** (figures still depend on line standards being entered — MFG-4 residue, §7) |
| Overhead Cost Variance ("Overhead") | ✅ **opens**; COST lines (was: could never return a row) | Operating-cost variance | **CORE — now present** for line-level costs |
| Finished Goods Overhead Variance ("Finished") | ✅ **opens**; header-level finished variance (was: could never match) | — | **CORE — now present** |
| Feasibility / Capability | ✅ **opens**; Required in **base units** vs OnHand | Production Plan shortage; MO availability | **CORE — now present** |
| Repost Manufacturing | ⚠ ½ header totals only (unchanged) | ERPNext Repost Item Valuation | CORE (½) |
| Cost Analysis per order / WIP / Production-Scrap-Downtime / Batch genealogy | ❌ (unchanged — build phase) | ✅ counterparts | still absent |

**Reports grade after Step 11: 6½/12 CORE ≈ 54%** (from 17%) — the
five crashed/impossible CORE reports are usable; the remaining gap
is feature build-out, not correctness.

---

## 5. Per-defect closure vs sweep IDs

| ID | Defect | Closure |
|---|---|---|
| S-2 | Routers reactivate by type only → wrong mode shown | **Closed.** Central `WindowActivator` policy (type, ModeKey); `IWindowMode` on all 13 mode-ful windows; both routers delegate; Step-8 cheque special case generalized and deleted |
| S-3 | `MWin` caches one `DatWin`; mode argument ignored on re-open | **Closed.** Cache reactivates only on ModeKey match; same-mode open instance activated; otherwise a new instance in the requested mode |
| S-7 (router part) | `RibbonNav.Show` empty `catch {}`; `LockWorkStation` silent catch | **Closed for the router layer.** Activator reports `UI-7004` and still shows; lock reports `ShellOperationFailed`. Non-router silent catches belong to their own steps (DatWin/Backup were Step 10) |
| MFG-1 | Variance ×4 + feasibility crash on open (034 schema mismatch) | **Closed.** Both queries re-sourced to migration 034; harness reproduces the old crash and the new success on the same fixture |
| MFG-2 | MFG quantities ignore unit equivalents | **Closed.** Post + Cancel convert via the single `UnitEquivalents` converter; `CardStore.PostOrder` converted via `StkPost.ToBaseUnits`; feasibility Required in base units |
| MFG-4 | Variance modes hunt kinds that can never exist; line StandardUnitCost unenterable | **Modes closed** (Total/Raw/Overhead=COST/Finished=header). The MfgWin grid still offers no StandardUnitCost column — a window build item, out of this step's no-new-features mandate; flagged in §7 |
| MFG-3, MFG-5 (residue), MFG-6…MFG-9 | manual line costs; WIP/variance GL; BOM shell buttons; register caps/order | **Not touched** — build-phase items, explicitly out of mandate |

---

## 6. Proof — headless harness (SQLite), 65/65 PASS

`~/workspace/research/fix-step11/step11_harness.py` (+ C# harness in
`harness/`, Step-10 pattern: real SqlMigrator DLL migrates the
fixtures; the C# harness links the **real** `WindowActivator.cs` and
references the real Domain/Infrastructure/Application projects; the
only stand-ins are the WPF surface types `WindowActivator` touches
and `AppHost.Errors`, disclosed in `harness/Shims.cs`).

- **Migrator:** fresh DBs migrate clean; double-run idempotent
  (no migration in this step — gate verified anyway).
- **(a) Router policy:** two mode-ful families through the real
  activator — invoice kinds (Sales open → Purchase requested ⇒
  Purchase shown as a second instance, Sales **not** reactivated;
  same-kind request reactivates; loaded-doc ≠ new-doc identity) and
  MFG variance modes (Total open → Raw opens beside it; second Raw
  reactivates). Single-mode windows keep type-only reactivation.
  Pure `WindowModePolicy.Decide` cases pinned directly.
  **WPF activation itself (real window handles, focus, minimize
  restore) is owed on Windows** — the harness proves the decision
  and dispatch logic, not the shell's rendering.
- **(b) MFG reports:** the **exact SQL strings extracted from
  `RptQuery.cs`** run against a migrated fixture (posted order:
  lines 10 + 12 + 20, overhead 20, total 62, standard 5; draft order
  for feasibility). The base commit's SQL raises "no such column" on
  the same fixture (crash reproduced); the fixed SQL returns exact
  row sets for Total/Raw/Overhead/Finished + costs + feasibility.
  Hand-verified figures: RM-1 line variance **+2** (10 − 4×2), RM-2
  **−1.5** (12 − 3×4.5), COST line **+2** (20 − 18), finished
  **+12** (62 − 10×5), costs report variance **12**.
- **(c) MFG units end-to-end** (real `MfgOrderService` /
  `MfgOrderRepository` / `StockValuationEngine` / `GlPosting`):
  the §3 worked example — consumption exactly **1** base unit at
  engine cost −8; finished receipt **24** base units at unit cost 1;
  balances conserved; GL Dr = Cr = 8; cancel restores both sides;
  base-unit shortage refused with the order left Draft.
  `UnitEquivalents` resolution pinned directly (0.25 / 1 / 12 /
  unknown→1 / none→1; `ToBase(4, 2.5, 0.25) = (1, 10)`).

**Kern.Tests:** `RouterMfgStep11Tests` — 8 facts mirroring (a) at
the policy core and (b)/(c) end-to-end (feasibility asserted in base
units through the real `StkPost` conversion). Compiles at 0/0;
**execution owed on Windows** with the rest of the suite
(net8.0-windows testhost), as in every prior step.

---

## 7. Pillars checklists (touched windows)

PROVEN = exercised by the harness/tests above or by an unchanged
green gate; GAP = known, named, not hidden.

### MfgWin (posting path changed)
- UI: unchanged this step — GAP (standing): no StandardUnitCost grid
  column (MFG-4 residue), no BOM explosion (MFG-6) — build phase.
- DB: PROVEN — orders/lines persist display qty+unit verbatim
  (harness posts real orders through the real repository).
- Linking: PROVEN — opens via mode-aware router (single-mode);
  shell context untouched (audit exit 0).
- Posting: PROVEN — base-unit consumption/receipt at engine cost,
  GL completion entry balanced (harness M1–M3).
- Persistence: PROVEN — totals display-based and conserved
  (FG stock value = order total cost, harness M2).
- Downstream: PROVEN — costs/variance/feasibility reports read the
  posted result (§2); GAP: no WIP/variance GL accounts (MFG-5
  residue).

### MfgVarianceWin (all four modes)
- UI: PROVEN (unchanged shell; mode now part of router identity —
  two modes can be open side by side). DB: PROVEN — reads migration
  034 tables only. Linking: PROVEN — RptNav → WindowActivator with
  ModeKey. Posting: n/a (read-only report). Persistence: n/a.
- Downstream: PROVEN — figures hand-verified against the fixture
  (§6b); GAP: header `overhead_amount` has no standard, so it
  carries no variance line here (visible in MfgOrderCosts instead)
  — a modelling fact, disclosed in §2.

### MfgFeasibilityWin
- UI/DB/Linking: as above (single-mode window; query fixed).
- Downstream/correctness: PROVEN — Required restated in base units
  via `StkPost.ToBaseUnits` (Kern.Tests fact asserts 4 × g250 →
  Required 1 against OnHand 0.5); GAP: a line whose item code
  resolves to no material keeps its raw quantity (nothing to
  convert against).

### MfgCostsWin
- All pillars: PROVEN unchanged — query untouched and
  regression-pinned by harness + tests.

### Router-touched windows (InvoiceWin, CshWin, OrdWin, ChqWin,
### DocSearchWin, GroupManagerWin, SupportWin, DefManagerWin,
### DatWin, ComWin, PrmWin, ClosingAccountsWin)
- UI: unchanged except activation behaviour. DB/Persistence/
  Posting: untouched by this step (only a `ModeKey` accessor and a
  marker interface were added per window — class-declaration diffs
  are one line each).
- Linking: PROVEN at the policy/dispatch level (harness §6a);
  GAP — real WPF activation/focus on Windows is owed (§8).
- DatWin specifically: mode cards, DATA.EXPORT gating and audit
  from Step 10 untouched; only MWin's cached-instance selection
  changed (S-3).

---

## 8. Gates

| Gate | Result |
|---|---|
| Full build `dotnet build ShouTech.sln --no-incremental -p:EnableWindowsTargeting=true` | **0 errors, 0 warnings** |
| `tools/audit_window_context.py` | **exit 0** |
| Lang parity | **0 missing / 0 extra** in Ar/Fr/Tr vs En; this step changes no Lang/Errors file |
| Migrator idempotent double-run | **PASS** (harness; no migration added) |
| Headless harness | **65/65 PASS** (`~/workspace/research/fix-step11/step11_harness.py`) |

## 9. Owner flags / owed verification

1. **Windows run owed:** Kern.Tests execution (incl. the 8 new
  facts), real WPF router activation (second-instance focus,
  minimize-restore, DatWin cache behaviour in the live shell), and
  a live PostgreSQL pass of the re-sourced MFG queries (SQL is
  provider-agnostic and parameterized; verified headlessly on
  SQLite only).
2. **MFG-4 residue:** line `StandardUnitCost` still cannot be typed
  in the MfgWin grid, so Raw/Total variance figures are only as
  good as standards entered through other paths (the column exists
  end-to-end in DB/entity/repository). Adding the grid column is a
  window change — build phase, needs the owner's go-ahead like any
  feature.
3. **Overhead mode semantics** now mean "additional-cost (COST)
  lines". If the owner instead wants the header `overhead_amount`
  lump reported as its own variance row, that needs a standard
  overhead concept that does not exist yet — build phase.
4. `CardStore.PostOrder` remains a zero-caller writer (MFG-7); it
  was converted for consistency, but the owner may prefer deleting
  the migration-050 family outright in the build phase.
5. Nothing in this step is pushed or merged.
