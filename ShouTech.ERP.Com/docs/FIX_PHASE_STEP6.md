# FIX PHASE — STEP 6: Valuation Unification (Phase B Migration 053) + RptQuery Swallow Removal

Branch: `fix/foundations-6-valuation` (base: `fix/foundations-5-invpost` @ a8e3d15).
Design: `~/workspace/research/phase-b-design.md` §1 (valuation) + §7 decision table.
Evidence: `~/workspace/research/sweep/stock-reports.md` (RC-1…RC-5),
`~/workspace/research/sweep/compare-stock-reports.md`.

## 1. The defects, in one paragraph

Stock valuation had **three writers with three different maths** (sweep RC-1):
`StkPost` valued outbound movements at the **selling price** (so returns
re-entered at sale price), `CommercialDocumentRepository` wrote chain
delivery movements at **`unit_cost = 0`** and never touched the average,
and `MfgOrderRepository` + `CardStore` each carried a **private copy** of
moving-average logic. The same stock was provably worth **70.00** in the
stock-balance report and **40.00** in the end-of-period report. Profit
reports (RC-2) costed from `materials.avg_cost`, which **no posting path
ever updated** — the sweep case reported a margin of **235 on a true
profit of 100**. `materials.cost_method` (WeightedAvg/FIFO/LIFO/Standard/
LastPurchase) was honored by **nothing** (RC-4). And `RptQuery.QueryAsync`
caught **every** exception and returned an empty result set (RC-5), so
any SQL/schema/provider failure rendered as a normal-looking "0 rows"
report.

## 2. Design-vs-built mapping (Phase B §1 + V-decisions)

| Design item | Disposition |
|---|---|
| One `StockValuationEngine`; every writer delegates | **Built.** `src/ShouTech.Infrastructure/StockValuationEngine.cs` + pure `ValuationMath.cs`. Verified mechanically: the only `INSERT INTO material_stock_movements` in the codebase is in the engine. |
| Migration 053: movement valuation columns, balance `stock_value`, `stock_cost_layers`, revaluation jobs, permissions | **Built**, both providers, idempotent; safe on DBs already at 059–061 (see §3). |
| FIFO layer queue (`stock_cost_layers`) | **Built.** FIFO oldest-first, LIFO newest-first, negative (deficit) layers settled by the next receipt (ERPNext queue behaviour). |
| Recalculate Costs job for back-dating | **Built as a service** (`StockRecalculationService`). The window surface is **flagged** (F-1) — it is a window build. |
| `materials.avg_cost` maintained by posting | **Built.** Engine refreshes the card average (company-wide Σvalue/Σqty) and `last_purchase_cost` on purchase receipts with every movement. |
| V1 — LIFO | **Recommendation taken:** engine ships LIFO, kept visible (regional parity; MatCard already offers it). |
| V2 — Back-dated policy | **Built as recommended:** posting-order valuation + manual Recalculate Costs; no automatic chronological reposting at post time. |
| V3 — Standard-cost variance | **Built as recommended:** report-only. Issues value at `standard_cost`; the receipt-vs-standard difference stays visible in the balance value; no variance GL account is posted. |
| V4 — GL COGS journals | The §7 table deferred this to Phase C, but the Fix Phase superseded it: Step 3 built the COGS journal (amounts from the pre-posting balance average) and **Step 6 re-sources those amounts from the engine** (§5). Step 3's account mapping is unchanged. |
| V5 — Valuation granularity | **Built as designed:** per material × warehouse. Per-batch valuation deferred; 053's layers carry no batch columns, and Step 14a (054) extends via movement allocations without changing the engine's value identity. |
| Batch/serial (054) | **Not built** (Step 14a). 053 leaves room: layers are per material×warehouse queues; allocations will reference movements, not rates. |

## 3. Migration 053

`dba/mig/{sqlite,postgresql}/053_valuation_engine.sql`:

- `material_stock_movements` gains `balance_qty_after`, `valuation_rate`,
  `stock_value_after`, `cost_amount` (signed: receipts +, issues −),
  `valuation_method`, `repost_batch_id`.
- `material_stock_balances` gains `stock_value` — the valuation of record.
- New `stock_cost_layers` (FIFO/LIFO queue; index on
  company/warehouse/material/is_open/layer_date/layer_id) and
  `stock_revaluation_jobs` (Recalculate Costs audit trail).
- Backfills: balance `stock_value = available_qty × average_cost` where
  unset; legacy movements stamped `valuation_rate = unit_cost`,
  `cost_amount = (qty_in − qty_out) × unit_cost`,
  `valuation_method = 'Legacy'`; one opening layer per positive
  FIFO/LIFO balance (NOT EXISTS-guarded).
- Permissions `INV.VALUATION.VIEW` (340) and `INV.VALUATION.RECALC`
  (341, sensitive): Admin + User get both, Viewer view-only.
- **Position safety:** the foundations chain already carries 059–061 and
  the migrator is filename-ordered with checksum-skip. 053 depends only
  on 013-era objects + roles/permissions — never on 059–061 objects —
  so it applies cleanly on a database that already ran 059–061. Proven
  by the harness fixture gate (§8). SQLite uses
  `-- @if-column-missing` guards; PostgreSQL uses
  `ADD COLUMN IF NOT EXISTS` / `ON CONFLICT`. Double-run idempotent on
  both paths (SQLite proven here; PG owed on Windows, §8.3).

## 4. Engine architecture

`ValuationMath` (pure, no DB) owns the arithmetic over a
`ValuationState` (quantity, value, layer queue):

- **Receipts** enter at actual cost (exact amount for transfers-in,
  linked rate for returns, document cost otherwise). For layer methods
  a receipt first settles negative layers; the settlement delta
  (deficit rate → actual rate) is part of the movement's `cost_amount`.
- **Issues** are valued **only** from the balance/queue — never from a
  caller-supplied price, so a selling price can never enter valuation
  again. WeightedAvg at the moving average; FIFO/LIFO from the queue;
  Standard at `standard_cost`; LastPurchase at `last_purchase_cost`.
- **Invariants:** a movement's `cost_amount` is exactly the change in
  balance value (Σ movement `cost_amount` ≡ balance `stock_value`); an
  issue that empties stock costs exactly the value before it (rounding
  residue absorbed; value is exactly 0 at quantity 0). Rates round to
  6dp, money to 4dp, AwayFromZero.

`StockValuationEngine.Apply(connection, transaction, provider, request)`
runs inside the **writer's own transaction** (sync ADO, like GlPosting)
and writes the movement (with its valuation record), the balance
(`average_cost` = display rate value/qty), the layer queue, and the
material card. Insufficient stock on a material with
`allow_negative = 0` throws the cataloged **INV-6002**. New error codes:
INV-6002 `StockValuationFailed`, INV-6003 `RecalculationFailed`,
INV-6004 `RecalculationClosedPeriod` (all four Errors catalogs +
`docs/ERROR_CATALOG.md`).

### Writer delegation inventory

| Writer | Before | Now |
|---|---|---|
| `StkPost.ApplyDocument` (InvRepo documents) | moving average, outbound at selling price | engine; SRT resolves the original document's rate via `TryGetDocumentRate` |
| `StkPost.PostCount` | full-qty recount movements | engine, **delta-only** (COUNT_CLOSE); zero-delta lines write no movement (behaviour change, flagged F-4) |
| `StkPost.PostTransfer` | private average copy | engine out + engine in with the out leg's exact `cost_amount` (value conserved across warehouses) |
| `StkPost.MoveMaterialBalances` (card merge admin op) | re-derived value = qty × avg | merges the engine-maintained `stock_value`; layer queue re-pointed to the target material |
| `CommercialDocumentRepository` DLV | `unit_cost = 0`, average untouched | one engine call per line (unit-equivalence kept) |
| `MfgOrderRepository.Post/Cancel` | private copy; GL from entered line amounts | engine for consume/produce/reverse; raw re-enters at the original consumption rate; **GL amounts are the engine's consumption costs** |
| `CardStore.PostOrder` (MfgWin card path) | private `InsertMovement`/`ApplyBalance` copy | engine (`MFG-OUT`/`MFG-IN`); private helpers deleted |

## 5. COGS wiring (Step 3's GlPosting, amounts from the engine)

`InvRepo.Post` now applies stock effects **before** building GL lines,
inside the same transaction. `AddCogs` reads the just-written movements
for the document: COGS = −Σ `cost_amount` (SRT: the re-entry value,
mirrored). This also fixes two latent errors in the old formula: it
multiplied the **raw line quantity** (chosen unit) instead of the base
quantity, and read the average **before** posting. Fallback to the
pre-posting average remains only for a stockable line the engine wrote
no movement for. Step 3's account mapping is untouched.
`MfgOrderRepository`'s completion journal likewise carries engine
consumption costs (Step 3 flag F-3 closed for the raw-material leg;
overhead still enters finished stock value via the order's computed
unit cost while the GL leg carries raw cost only — F-5).

## 6. Recalculate Costs (`StockRecalculationService`)

`RecalculateAsync(companyId, warehouseId?, materialId?, fromDate,
reason, userId)`:

- Writes a `stock_revaluation_jobs` row (Running → Done/Failed, with
  the cataloged error on failure) in its own committed write, so the
  job is auditable even when the replay fails.
- **Closed-period guard (INV-6004):** refuses when any fiscal period
  ending on/after `fromDate` is non-OPEN or its year is closed.
- Replays each (warehouse, material) movement chain in
  `(movement_date, stock_movement_id)` order through `ValuationMath`,
  restating `cost_amount` / `valuation_rate` / `stock_value_after` /
  `unit_cost` for rows ≥ `fromDate` and stamping `repost_batch_id`.
  Returns (sales/purchase) re-enter at their **original document's
  replayed rate** (via `original_doc_no`); transfers-in take the
  matching transfer-out's replayed value; layers and balances are
  rebuilt; card averages refreshed.
- **GL entries are NOT restated** by recalculation (F-2): the journal
  keeps the amounts posted at the time; the delta is reported on the
  job row for a manual adjustment decision.

## 7. RptQuery swallow inventory (RC-5) and replacements

| Site | Before | Now |
|---|---|---|
| `RptQuery.QueryAsync` (the single funnel for ~50 report queries) | catch-all → empty result; missing connection string → empty result | reports **DB-1005** via `AppHost.Errors.Report(...)` then throws cataloged `AppError(DB-1005)`; missing CS throws `AppError(DB-1004)`; cancellation rethrows untouched |
| `RptBase.RefreshAsync` | try covered only the data load | try now also covers definition/filter construction (filter choice loaders query via RptQuery); an `AppError` is not double-reported (source already logged it); failure shows `Report.LoadFailed` |
| `CustomReportsWin.LoadKeysAsync` | `catch { }` silent | reports DB-1005 centrally, window still opens |
| `CustomReportsWin.ResolveCompanyGuidAsync` | `catch → Guid.Empty` silent | reports DB-1005 centrally before the fallback |
| `CustomReportsWin.QueryReportKeysAsync` | empty list on missing CS | throws `AppError(DB-1004)` (reported by its caller) |

Verified non-swallows, left as-is: `RptNav.Show` (reports
NavigationFailed), `ReportRepo` (rollback + throw),
`FinancialAnalysisWin` (surfaces via MessageBox).

### Report valuation rewrites (one consistent valuation)

All read `COALESCE(cost_amount, (qty_in − qty_out) × unit_cost)` for
movement value (legacy rows fall back to their recorded cost) or the
balance `stock_value`:

- `StockBalancesAsync`: Value := `stock_value` (legacy fallback
  qty × avg); service / non-stocked materials excluded.
- `EndOfPeriodAsync`: Value := Σ `cost_amount` to the as-of date;
  AvgCost = Value/Balance; boundary changed to the end-exclusive
  pattern every other report uses (the old `<= date` dropped same-day
  timestamped movements on PostgreSQL — half of the 70-vs-40 split).
- `NegativeStockAsync`: re-sourced from `material_stock_balances`, so
  it agrees with Stock Balances' NEGATIVE filter by construction.
- `MaterialMovesAsync`: MoveValue := `cost_amount`; new
  `ValuationRate` and `BalanceValue` (stock_value_after) columns in
  the result set.
- `MostActiveMaterialsAsync`, `MaterialComparisonAsync`,
  `StagnantMaterialsAsync`, `BranchMaterialsAsync`: values from
  `cost_amount` / `stock_value`.
- `InvoiceProfitsAsync`, `MaterialStatisticsAsync`,
  `ItemProfitabilityAsync`: Cost := −Σ movement `cost_amount` for the
  document/material — per cost_method, in base units, at posting
  time; sales/purchase returns are **netted** (previously ignored).
  `ItemProfitabilityAsync` gains Cost/Profit/MarginPct (its window
  gains the matching columns, §9).

## 8. Proof

### 8.1 Headless harness — `~/workspace/research/fix-step6/valuation_harness.py` — **18/18 PASS**

Migrator gates (real SqlMigrator): full set incl. 053 on a fresh DB;
double-run idempotent; fixture DB migrated with 053 withheld (already
at 059–061) applies 053 cleanly afterwards and stays idempotent; 053
tables/columns/permissions verified. C# scenarios run the **real**
engine, `CommercialDocumentRepository` and `MfgOrderRepository`:

- S1 sweep sequence through the engine: sale valued at cost 10 (never
  the selling price 20); delivery costed −10 (was 0); return re-enters
  at the original 10; balance qty 6 / value **60**, and
  Σ `cost_amount` ≡ `stock_value` (the 70-vs-40 divergence is
  impossible when both reports read the engine's numbers); card
  `avg_cost` maintained by posting.
- S2 real CDR delivery note: movement `cost_amount` = −10; balance
  9 / 90, ledger agrees.
- S3 real MFG post: raw consumed at engine cost (balance 6 / 60),
  finished produced 2 / 40 (value conserved), journal Dr = Cr = 40
  from engine costs.
- S4 FIFO: issue of 12 costs 140 (oldest first); open layer 8 @ 20.
- S5 back-dated purchase + real `StockRecalculationService`: sale
  COGS restated 50 → 35, balance 15 @ 7 = 105, job recorded Done.
- Source checks: RptQuery throws/reports as designed; the engine is
  the only movement writer in the codebase.

### 8.2 Kern.Tests — `tests/Kern.Tests/ValuationEngineTests.cs` (9 facts)

Pure `ValuationMath` facts (weighted average + residue absorption,
FIFO order, LIFO order, deficit-layer settlement) and engine
integration facts on real migrated SQLite DBs (sweep sequence, FIFO
end-to-end, Standard-cost issue valuation, INV-6002 on disallowed
negative stock, back-dated purchase + recalculation). The test
project **compiles** here; execution is owed on Windows (net8.0-windows
testhost cannot run on this Linux VM).

### 8.3 What is NOT proven here

- **WPF runtime**: report windows rendering the new values/columns,
  and the RptBase error path in the live UI — owed on Windows.
- **Live PostgreSQL**: 053 PG migration, engine and recalculation
  against PG (the code is provider-parameterized and the PG SQL uses
  the same shapes as Steps 3–5; execution owed).
- Kern.Tests execution (see 8.2).

## 9. Gates

| Gate | Result |
|---|---|
| Full build `--no-incremental` | **0 errors, 0 warnings** |
| `tools/audit_window_context.py` | exit **0** |
| Lang parity | no Lang files touched; missing-key parity vs En = 0 in all catalogs (pre-existing Ar/Fr/Tr extras unchanged) |
| Errors catalogs | 4 × 194 entries, parity 0; INV-6002/6003/6004 added to all four + ErrorCodes.cs + ERROR_CATALOG.md |
| Arabic outside Ar.json | none in the diff (053 name_ar values are English per the 038 pattern) |
| Migrator | fresh + double-run + 059–061-fixture gates PASS (§8.1) |
| Harness | 18/18 PASS (§8.1) |

## 10. Comparison table (triggered: stock/profit report values and ItemProfitWin columns change)

Condensed from `~/workspace/research/sweep/compare-stock-reports.md`;
scope = only the axes Step 6 changes. (⚠ Al-Ameen/Phenix rows are
pattern-based per that audit — vendors publish no field-level docs.)

| Axis | Before Step 6 | After Step 6 | Al-Ameen V10 / Phenix v10 | ERPNext | Odoo |
|---|---|---|---|---|---|
| Stock value source | balance report: qty × avg; period report: Σ qty × recorded unit_cost — **disagreed (70 vs 40)** | ONE engine value: balance `stock_value` ≡ Σ movement `cost_amount` in every report | single inventory valuation per method | Stock Ledger Entry valuation rate; balance = Σ SLE value | AVCO/FIFO valuation layers, one value across reports |
| Valuation method | `cost_method` stored, honored by nothing | honored per material: WeightedAvg / FIFO / LIFO / Standard / LastPurchase | تقييم by method (weighted avg / FIFO) | per-item FIFO or Moving Average | per-category Standard / AVCO / FIFO |
| Outbound valuation | selling price (StkPost), 0 (chain DLV) | cost only, per method, from balance/queue | cost | valuation rate from ledger | cost from layers |
| Returns | re-enter at selling price | re-enter at the **original outbound cost** (linked rate) | original cost | linked against original SLE rate | original move value |
| Profit reports | line qty (chosen unit) × stale card `avg_cost`; returns ignored | −Σ movement `cost_amount` (base units, posting-time, per method); returns netted | COGS from inventory valuation | Gross Profit report from SLE/bin valuation | margin from move costs |
| Item profitability columns | Qty, Revenue, Tax, Total (no cost at all) | + Cost, Profit, Margin % with totals | material profits with cost + margin | Gross Profit per item with buying amount + margin | product margin on sales analysis |
| Back-dated documents | later movements keep wrong values forever | Recalculate Costs job replays in posting order and restates movements (GL not restated, F-2) | إعادة احتساب التكلفة reposting | Repost Item Valuation (automatic queue) | recompute via valuation layers |
| Report query failure | silent empty report | cataloged error (DB-1005) reported + surfaced in the window | error dialog | error/traceback surfaced | error dialog |

Remaining comparison gaps (not Step 6 scope): drill-down to source
document, group-by/subtotals, as-of balances beyond EndOfPeriod,
batch/serial dimensions (Step 14a).

## 11. Pillars checklists

### Stock valuation engine + migration 053
- UI: **GAP (flagged F-1)** — no new window; ItemProfitWin columns are
  the only UI change (below).
- DB: **PROVEN** — 053 both providers, idempotent, 059–061-safe
  (harness gates); backfills for legacy rows.
- Linking: **PROVEN** — every writer delegates (mechanical grep gate);
  movements carry document numbers; returns link via
  `original_doc_no` / document rate lookup.
- Posting: **PROVEN** — engine runs inside each writer's transaction;
  MFG GL and InvRepo COGS consume engine amounts (harness S3, tests).
- Persistence: **PROVEN** — movements, balances, layers, card averages
  and revaluation jobs all persisted transactionally; parameterized
  SQL throughout.
- Downstream: **PROVEN (code) / GAP (runtime)** — all stock & profit
  reports re-sourced to the engine's numbers (§7); live WPF rendering
  owed on Windows.

### Recalculate Costs (service)
- UI: **GAP (F-1)** — service only; `INV.VALUATION.RECALC` permission
  seeded for the future window.
- DB: **PROVEN** — job rows in `stock_revaluation_jobs`; movements
  restated with `repost_batch_id`.
- Linking: **PROVEN** — original-document rate resolution for returns;
  transfer pairing preserved.
- Posting: **GAP (F-2, by design)** — GL not restated; delta reported
  on the job.
- Persistence: **PROVEN** — replay is one transaction per company
  scope; job outcome recorded even on failure.
- Downstream: **PROVEN** — reports read the restated movements.

### ItemProfitWin (only materially changed window)
- UI: **PROVEN (code)** — Cost/Profit/MarginPct columns + totals added
  with existing Lang keys ×4 (no new keys); buttons remain in the
  standard RptBase bottom bar; shell context unchanged (audit exit 0).
- DB/linking/posting/persistence: **PROVEN** — reads the engine's
  movement costs via RptQuery; no writes.
- Downstream: n/a (terminal report).

### Report layer error surfacing (RptBase family)
- UI: **PROVEN (code) / GAP (runtime)** — failures show
  `Report.LoadFailed` instead of a fake empty grid; live check owed.
- DB/linking/posting/persistence: n/a (read path); errors persisted
  via the central log (LogServ) with catalog codes.

## 12. Owner flags

- **F-1 — Recalculate Costs + Valuation windows not built.** The
  design's Recalculate surface (and an InvValuationWin-style valuation
  viewer) are window builds; per the step boundary they are flagged,
  not built. The service, job table and `INV.VALUATION.*` permissions
  are in place for them.
- **F-2 — Recalculation does not restate GL.** Journal entries keep
  posted amounts; the recalc job reports the value delta. Decide
  whether a follow-up should post a valuation-adjustment journal.
- **F-3 — Chain SAL has no COGS leg.** `CommercialDocumentRepository`
  posts revenue/receivable for chain sales invoices; the delivery
  note's movements now carry true `cost_amount`, but posting chain
  COGS needs a document_links traversal + mapping decision (Step 3's
  mapping covers InvRepo kinds). Flagged, not improvised.
- **F-4 — Count behaviour change.** Zero-delta count lines no longer
  write zero movements, and shortages/surpluses are delta-only
  (previously full-qty). This is the correct stock semantics; noted
  because movement history shape changes.
- **F-5 — MFG finished value vs GL.** Finished stock enters at the
  order's computed unit cost (raw + overhead, Step 5 behaviour), while
  the completion journal carries the engine's raw consumption cost
  only — the overhead portion of finished value has no GL leg (Step 3
  F-3 residue).
- **F-6 — FIFO purchase returns.** A purchase return consumes the
  layer queue normally (FIFO/LIFO order) rather than removing the
  specific purchase layer the design sketches; the value effect is
  identical under WeightedAvg and near-identical under FIFO unless
  rates moved between purchase and return. Linked-layer removal can
  land with 054's allocation work.
- **F-7 — LIFO shipped.** Per V1's recommendation the engine supports
  LIFO and it stays visible, although IAS 2/IFRS prohibits it; the
  owner may still choose to hide it in the MatCard list.

## 13. Files changed

- Migration: `dba/mig/sqlite/053_valuation_engine.sql`,
  `dba/mig/postgresql/053_valuation_engine.sql` (new).
- Engine: `src/ShouTech.Infrastructure/ValuationMath.cs`,
  `StockValuationEngine.cs`, `StockRecalculationService.cs` (new).
- Writers: `src/ShouTech.Infrastructure/CommercialDocumentRepository.cs`,
  `MfgOrderRepository.cs`; `cod/app/src/Services/StkPost.cs`,
  `CardStore.cs`, `InvRepo.cs` (COGS + ordering).
- Reports: `cod/app/src/Services/RptQuery.cs`,
  `cod/app/src/Views/RptBase.cs`, `cod/app/src/Views/CustomReportsWin.cs`,
  `cod/app/src/Views/Reports/ItemProfitWin.cs`.
- Errors: `src/ShouTech.Domain/ErrorCodes.cs`,
  `cod/app/src/Resources/Errors/{En,Ar,Fr,Tr}.json`,
  `docs/ERROR_CATALOG.md`.
- Tests: `tests/Kern.Tests/ValuationEngineTests.cs` (new).
- Proof: `~/workspace/research/fix-step6/valuation_harness.py` +
  `harness/` (C# harness against the real repositories).
