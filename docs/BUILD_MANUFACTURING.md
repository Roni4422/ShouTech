# BUILD — Manufacturing full build-out (branch `build/manufacturing`)

Base: tip of `build/phaseb-057-crm` (`f48dcd7`). Commits: `8270314` (core:
migration 058 + repositories/services), `3785553` (windows + wiring + Lang),
plus the tests/harness/doc commit. **Not pushed, not merged.**

Starting point (sweep `compare-mfg-hr-crm.md` §1): manufacturing at **22% of
parity (4/18 CORE)** — a one-step consume-and-produce order, no BOM master
(the ribbon's BOM button opened a blank order, MFG-6), no staged workflow,
no WIP accounting, no planning.

## Per-priority status

| # | Priority | Status |
|---|---|---|
| 1 | BOM master (migration 058) + BomWin + ribbon fix | **BUILT + PROVEN** |
| 2 | MfgWin completion + staged workflow | **BUILT + PROVEN** (one deviation, flagged below) |
| 3 | WIP + variance GL through GlPosting | **BUILT + PROVEN** |
| 4 | Planning (MRP-lite) + MfgPlanWin + feasibility | **BUILT + PROVEN** |
| 5 | Batch/serial in MFG flows | **BUILT** (engine auto-allocation; see note) |

## 1. BOM master

**Schema (migration 058, both providers, idempotent):** `mfg_boms`
(company, material code, version, is_active, is_default, output qty + unit,
notes; unique (company, material, version) among live rows) and
`mfg_bom_lines` (component code, qty in the line's unit, scrap %, per-line
warehouse, standard unit cost snapshot). Permissions `MFG.PLANNING.*` are
also seeded here (BOM permissions `MFG.BOM.VIEW/MANAGE` already existed from
migration 004).

**Rules (BomRepository / BomService):**
- Save takes each line's standard-cost snapshot from the valuation engine's
  current base rate × the line unit's UnitEquivalents factor (balance
  average → card avg → last purchase → standard fallback chain).
- Explosion is multi-level and cycle-safe twice over: a save-time
  transitive guard and an explosion path guard both refuse with catalog
  code **MFG-1001** (`ErrorCodes.BomCycleDetected`). Requirement per line =
  qty × (requested ÷ BOM output qty) × (1 + scrap/100), in the line's unit;
  `BaseQty` is the same quantity in the material's base unit via
  UnitEquivalents. Leaves aggregate per (material, warehouse, snapshot
  rate, unit) so amounts stay exact.
- Rolled standard cost = Σ round(line qty × snapshot, 2) ÷ output qty
  (scrap is a quantity allowance, not a costed input — ERPNext pattern).
- **BomWin** (new, Fern tint): list + editor, material pickers from the
  material master, version activation (active/default flags), explosion
  preview grid, bottom command bar, `MFG.BOM.VIEW/MANAGE` gating, all
  failures through `AppHost.Errors` with catalog codes. The ribbon entry
  `Manufacturing.ManufacturingModels` now opens BomWin (**MFG-6 fixed**).

## 2. MfgWin completion + staged workflow

**Header gains:** BOM link (picker) with **Fetch/Explode** filling the lines
from the explosion at BOM snapshot costs (**MFG-3**: line costs are never a
silent zero again), planned start/end dates, sales-order link (doc id + no),
cost center, and split warehouses — source / WIP / target / scrap, each
defaulting from the header warehouse. The finished item is a material
picker, not free text.

**Lines gain:** kind (RAW / COST / BYPRODUCT / SCRAP — COST and service
lines were previously unreachable from the UI), required vs consumed /
produced quantities, per-line source warehouse, a stage-qty column driving
partial issues/receipts, by-product cost-share %, and the **StandardUnitCost
column is now BOUND (MFG-4 fixed)**.

**State machine:** `Draft → Confirmed → InProcess → Completed → Closed`,
plus legacy `Posted` and `Cancelled`. Confirm validates and zeroes the
staged actuals baseline (the draft estimate — lines × entered costs — must
not leak into actuals; harness-caught defect, fixed). Issue may repeat
(partial issues); Finish may repeat (partial receipts) and records
by-products and scrap; Close settles; Cancel reverses symmetrically at any
pre-Close stage. Every stage writes a `mfg_production_events` row
(Confirm/Issue/Finish/Close/Cancel with seq, qty, amount, journal doc no).
`mfg_orders` gains bom link, planned dates, sales-order link, cost center,
the four warehouses, produced_qty, accumulated_cost, wip_balance,
variance_amount, standard_variance (migration 058 widens the status CHECK
on both providers). `GetAllAsync`'s `LIMIT 1000` is removed (MFG-9).

**Costing model (one engine, one poster):** issues are costed by the
valuation engine at actual cost (the line's entered cost is ignored for
stockable materials); service/COST lines enter WIP at entered cost.
Overhead (header + COST lines) is absorbed pro-rata per finish; the
completing finish takes the exact remainder. A finish's pool = WIP balance
+ absorbed overhead share; a partial finish takes pool × qty ÷ remaining
planned; by-products carve out their cost-share % (last by-product line
takes the rounding remainder); the finished good is received through the
engine at the exact remaining pool value, so FG unit cost =
(materials + overhead − by-product share) ÷ qty holds to the cent.

**DEVIATION (owner flag):** the task asked the one-step Post to be
re-implemented as Confirm+Issue+Finish composition. Post is instead left
byte-for-byte as the pre-build baseline — the existing `GlPostingTests`
pin its single `MFG` GL entry, and the staged path is the primary workflow
sharing the same valuation engine and the same GlPosting poster (no
parallel math anywhere). Re-pointing Post at the staged documents would
change its GL shape (one `MFG` entry → `MFG-ISSUE`/`MFG-FINISH` entries)
and break the pinned regression baseline (proof f). Owner decision needed:
keep Post as the legacy simple path (current state) or retire it in favour
of the staged buttons.

## 3. WIP + variance GL (GlPosting only — no second poster)

Accounts come from the chart's canonical industrial template, resolved per
posting with step-3-style optional-leg fallbacks:

| Leg | Primary | Fallback chain |
|---|---|---|
| Work in progress | **1211** | → Inventory 1203 |
| Finished goods | material `acc_inventory` → **1212** | → Inventory 1203 |
| Manufacturing variance | **406** | → COGS 401 |
| Applied overhead | **409** | → COGS 401 |

| Stage | GL (source type) |
|---|---|
| Issue | Dr WIP / Cr Inventory per component at engine cost (`MFG-ISSUE`); service lines Dr WIP / Cr Applied Overhead in the same entry |
| Overhead absorption at Finish | Dr WIP / Cr Applied Overhead (`MFG-OVHD`) |
| Finish | Dr Finished Goods + Dr by-product inventory / Cr WIP at the pool (`MFG-FINISH`) |
| Close | remaining WIP → Dr Variance / Cr WIP (mirrored if negative) (`MFG-CLOSE`); standard variance (actual − produced × standard) recorded on the order |
| Cancel | every staged entry reversed via `GlPosting.ReverseInTransaction`; stock re-entered at original issue rates |

SourceDocId = order id + `#` + event seq, so each stage entry is
individually reversible and duplicate-guarded by GlPosting.

## 4. Planning (MRP-lite)

`PlanningRepository.RunPlanningAsync` (PlanningService on top):
- **Demand:** posted sales-order lines (full ordered qty, base units) +
  reorder points (on-hand < min_qty → demand = max(reorder_qty,
  min_qty − on-hand)).
- **Netting:** delta processing per material — a material with an active
  BOM becomes a planned production order net of on-hand (stock netted
  once); requirements explode through the BOM and recurse; leaf shortages
  become purchase suggestions net of on-hand **and** incoming posted POs.
- **Conversions:** planned order → real Draft production order via
  `MfgOrderRepository.SaveDraft` with BOM-exploded lines (stamped by CAS);
  suggestion → real Draft purchase order via the existing
  `OrderDocumentRepository` document path (stamped with doc id/no).
- **Feasibility** (`CheckFeasibilityAsync`) groups the same explosion
  against on-hand + incoming — the plan and the answer cannot disagree.
- **MfgPlanWin** (new, Lagoon tint): run picker, planned-orders grid,
  suggestions grid, convert buttons, feasibility panel;
  `MFG.PLANNING.VIEW/RUN/CONVERT` gating (seeded 058, display order
  366–368). A Planning ribbon button was added to the Manufacturing tab.

## 5. Batch/serial in MFG flows

Built on build 054's engine capture/allocation, as the 054 design allows:
issues auto-allocate batches FEFO and auto-pick in-stock serials inside
`StockValuationEngine.Apply` (allocations in
`stock_movement_allocations` are the genealogy link); finished goods are
produced into a batch named `MO-<doc no>` with serials `MO-<doc>-<n>`
continuing across partial finishes; by-product/scrap receipts capture
tracking the same way; QC-required finished goods land in quarantine with
an In-Process inspection (056 pattern, preserved from the legacy path);
cancel consumes the produced batch/serials back and re-enters raw
materials into their original batches/serials. **Note:** MFG uses
auto-allocation throughout — there is no interactive batch picker inside
MfgWin (the app-wide `BatchSerialPickerWin` serves document flows);
flagged as a conscious scope choice for the owner.

## Comparison gate (vs Al-Ameen V10 / Phenix v10 / ERPNext / Odoo)

Counterpart columns condensed from `compare-mfg-hr-crm.md` §1 (Al-Ameen /
Phenix are ⚠-marked there as analyst-knowledge; ERPNext/Odoo are the
documented references).

### BomWin (was: absent, 0/10)

| BOM surface (§1.3) | ERPNext | Odoo | Al-Ameen/Phenix ⚠ | ShouTech NOW | Class |
|---|---|---|---|---|---|
| Header: item, version, batch qty, UOM | ✅ | ✅ | ✅ | ✅ | CORE |
| Is Active / Is Default | ✅ | ✅ | ✅ | ✅ | CORE |
| Lines: item, qty, UOM, cost, scrap % | ✅ | ✅ | ✅ | ✅ (+ per-line warehouse) | CORE |
| Operations / routing on BOM | ✅ | ✅ | ✅ | ❌ | CORE |
| Multi-level + cost roll-up | ✅ | ✅ | ✅ | ✅ (explosion + rolled std cost) | CORE |
| Subcontracting BOM flag | ✅ | ✅ | ✅ | ❌ | CORE |
| By-products on BOM | ✅ | ✅ | — | ❌ (by-products live on the order) | EXT |
| Where-used / ECO / copy | ✅ | ✅ | — | ⚠ versions + activation only | EXT |

**Re-grade: 4 of 6 CORE (was 0).** Missing: operations/routing,
subcontracting flag.

### MfgWin (§1.1 CORE set 20; was 6/20 = 30%)

Now present: finished-item picker, BOM link + Fetch/Explode, planned
start/end, sales-order link, cost center, split warehouses, staged status
workflow, required vs consumed/produced, per-line warehouse, unit
conversion at posting (step 11 + staged), engine unit cost, BOUND line
standard cost, scrap lines + scrap warehouse, by-product lines with cost
share, batch/serial via 054, separate Issue / partial Finish / short-Close
actions, real GL behind every action. Still missing: operation link /
job cards, project link (cost center present), subcontracting,
duplicate/create-from-plan actions.
**Re-grade: ≈16/20 = 80% (was 30%).**

### Planning window (§1.4; was 0/9 CORE in section)

| Surface | ShouTech NOW |
|---|---|
| Production Plan / MRP → planned orders + purchase suggestions | ✅ (MRP-lite) |
| Scrap management | ✅ |
| Batch/serial traceability through production | ✅ (054 genealogy) |
| Workstation / Operation / Routing / Job Card | ❌ |
| Subcontracting order + receipt | ❌ |
| Capacity / downtime (EXT) | ❌ |

**Re-grade: 3/9 section CORE (was 0).**

### MFG CORE re-grade (§1.7 consolidated list, 16 items)

✅ 12 — BOM master; BOM→order explosion; planned dates + SO link + cost
center; separate partial Issue (planned vs consumed); separate partial
Finish + produced qty + short-close; scrap %/recording/warehouse +
by-products; automatic component costing (MFG-3) + unit equivalents
(MFG-2, step 11); production GL (MFG-5); WIP state/account/status;
production planning/MRP; line standard cost column (MFG-4); batch/serial
allocation (054 wiring).
⚠ 2 (½ each) — variance reports + feasibility (feasibility ✅ via the
planning service/panel; the four variance report windows now read real
variance figures but were not rebuilt in this build); register (row cap
removed; MfgListWin filters not added).
❌ 2 — work centres/operations/job cards; subcontracting.

**Module re-grade: 13 of the 16 previously-missing CORE items closed
(12 full + 2 half) → manufacturing ≈ 22% → ≈72% of the 18-point CORE set.**
The remaining blocking gaps are operations/routing/job cards and
subcontracting — both are module-sized builds of their own.

## Pillars checklists

**BomWin** — UI ✅ (list+editor+explosion preview, bottom bar, Fern tint,
RTL/shell context via BaseWin, audit exit 0) · DB ✅ (058 tables, both
providers) · Linking ✅ (ribbon MFG-6 fix, material pickers, default-BOM
resolution used by orders + planning) · Posting n/a (master data; its
snapshots feed order/planning costing ✅) · Persistence ✅ (full save/
delete round-trip via repository; soft delete) · Downstream ✅ (MfgWin
Fetch, planning explosion, feasibility all consume it).

**MfgWin** — UI ✅ (all header/line additions above; stage buttons with
status-driven enablement) · DB ✅ (058 columns + events table) · Linking
✅ (BOM, sales order, cost center into GL lines, shell context) · Posting
✅ (engine stock + GlPosting per stage, proven below) · Persistence ✅
(every new field bound in SaveDraft/reads; consumed/produced persisted
per stage) · Downstream ✅ (variance figures on the order for the
variance reports; stock/GL visible to all existing reports).

**MfgPlanWin** — UI ✅ (run → grids → convert; feasibility panel) · DB ✅
(058 planning tables) · Linking ✅ (demand from real SOs/POs; conversions
into real production/purchase documents) · Posting ✅ (converted orders
post through the staged workflow; converted POs through the existing
document path) · Persistence ✅ (runs/orders/suggestions persisted with
CAS-stamped conversions) · Downstream ✅ (converted documents are ordinary
Drafts in MfgWin / OrdWin).

## Proof (headless, SQLite, real migrator + real repositories)

Driver: `~/workspace/research/build-mfg/mfg_harness.py` (+ C# harness in
`~/workspace/research/build-mfg/harness/`) — **62/62 checks PASS**:

- (a) 2-level explosion exact: LEAF 5.3 KG / 5300 G base (scrap ×1.1, KG→G
  conversion), snapshot 10/KG, rolled SUB cost 5; direct + transitive
  cycles refused with MFG-1001.
- (b) staged order: issue 10+10 KG at engine cost 2 (entered 999 ignored)
  → WIP 40, GL Dr 1211/Cr 1203 = 40; finish 8/10 + by-product → pool share
  38.4, FG 34.56, by-product 3.84 (10% share), overhead Dr WIP/Cr 409 = 8,
  WIP remainder exactly 9.6; close → Dr 406/Cr 1211 = 9.6, WIP zero,
  standard variance 2.56 recorded; events Confirm,Issue,Issue,Finish,Close.
- (c) cancel after partial issue → stock restored, GL net zero including
  reversal entries, WIP zero.
- (d) completing finish: FG unit cost = (40 + 10 − 5)/10 = **4.5** exactly.
- (e) planning: SO demand 5 − stock 2 → planned order 3; LEAF suggestion
  1500 − 300 stock − 200 incoming = 1000 G; reorder suggestion 210;
  feasibility boundary exact (buildable at ×1, short 2000 G at ×5); both
  conversions create real Draft documents (order lines exploded 1.5 KG;
  PO line 1000 G).
- (f) regression: legacy one-step Post still creates exactly one live
  `MFG` GL entry, Dr 1203 = 20 balanced; cancel reverses it.
- (g) migrator double-run: 058 APPLY then SKIP, exactly one
  schema_migrations row, all six tables + staged columns present.

Kern.Tests facts for the same scenarios:
`tests/Kern.Tests/ManufacturingBuildTests.cs` (6 facts: explosion, cycle,
staged WIP/variance, cancel, by-product formula, planning) — compiles 0/0;
**execution is owed on Windows** (net8.0-windows testhost cannot run on
this Linux VM), as is the WPF visual pass. **Live PostgreSQL is owed**:
the PG 058 migration mirrors the SQLite one (uuid/boolean dialect) but no
PG server exists on this VM to execute it.

## Gates

- Full solution build `--no-incremental`: **0 errors, 0 warnings**.
- `tools/audit_window_context.py`: **exit 0** (BomWin/MfgPlanWin listed as
  central-context BaseWin windows).
- Lang parity: new keys identical in En/Ar/Fr/Tr (**0 missing** in any
  language; Arabic only in Ar.json). Pre-existing extras in Ar
  (DataExchange) and Fr/Tr (WhsWin) pre-date this build and were left
  untouched.
- Migrator idempotent double-run: ✅ (proof g).
- Error catalog: MFG-1001…MFG-1006 added to `ErrorCodes.cs`, all four
  `Errors/*.json`, and `docs/ERROR_CATALOG.md`.

## Owner flags

1. **Legacy Post deviation** (see §2): Post kept as the pinned baseline;
   staged path is primary. Decide whether Post should later be retired
   or re-pointed at the staged documents (changes its GL shape).
2. **Scrap receipts are not reversed on cancel** — scrap is consumed
   production loss by definition; its zero-value receipt into the scrap
   warehouse stands (documented in code). Revisit if the owner wants
   scrap restored on cancel.
3. **Planning demand** counts posted sales orders at full ordered qty
   (no delivered-qty netting — sales delivery state is not tracked
   per line today) and planning runs are company-wide snapshots, not
   incremental.
4. **No interactive batch picker in MfgWin** — auto-allocation per the
   054 design (see §5).
5. **Operations/routing/job cards and subcontracting remain unbuilt** —
   the two remaining CORE gaps; each is a module-sized build.
6. AppHost.RebindDatabaseContext now also rebinds Manufacturing (it
   previously did not) alongside the new Boms/Planning services — a
   shell-context correctness fix riding with this build.
7. WPF runtime verification + the Kern.Tests execution + live PG pass
   are owed on Windows, per the standing rule that build success is not
   product readiness.
