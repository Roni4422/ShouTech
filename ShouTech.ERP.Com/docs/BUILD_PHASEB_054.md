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
