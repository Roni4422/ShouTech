# Fix Phase — Step 9: Order-Chain UI Wiring + DocSearch Open Fix

**Branch:** `fix/step9-order-chain` (worktree `~/workspace/projects/ShouTech-fix9`), based on
`b2b40da` (tip of `fix/step8-cheques-bankrec`; carries steps 1–8). **Not pushed, not merged.**
**Commits:** `774deef` (migration 064 + conversion stock/COGS + dual-key search) ·
`4109309` (InvRepo TRN/CNT routing, dual-key Load/Post/Save, InvoiceWin target field + loud
load failure, DocSearch open-by-id + quotation conversion, OrdWin fixes, Lang ×4) ·
`653b3ca` (credit-guard ordering + tests) · this doc.

**Scope delivered:** DocSearch opens the document it found, for every indexed kind; the
order → invoice conversion posts stock + COGS through the one valuation engine; InvoiceWin's
StockTransfer (TRN) and InventoryCount (CNT) modes post through their dedicated engines
(`StkPost.PostTransfer` / `StkPost.PostCount`) instead of refusing with ACC-5014; the
quotation → sales-order conversion is reachable from DocSearch. No new document kinds, no
posting-math changes (steps 3/5/6 own the math — step 9 only routes to it).

---

## 1. Root cause found beyond the sweep: the company-key split-brain

The sweep documented the symptoms; tracing them exposed a deeper cause the sweep did not
name. `sales_documents.company_id` / `purchase_documents.company_id` hold **two different
keys** depending on which family wrote the row:

| Writer family | `company_id` value |
|---|---|
| InvRepo / RptQuery (InvoiceWin native documents) | company **CODE** (e.g. `ACME`) |
| OrderDocumentRepository / CommercialDocumentRepository (orders, chain documents, converted invoices) | company **Guid string** |

Consequences, all reproduced before fixing:

- DocSearch searches through the Guid-keyed order repository → native InvoiceWin invoices
  were **invisible** to search.
- `InvRepo.Load` filtered by the code only → a Guid-keyed converted invoice loaded as
  "not found" → the window fell back to a blank document (the visible DS-02/I-8 symptom).

**Fix (no schema change):** both readers now match **either of the company's own keys** —
`OrderDocumentRepository.ListAsync` resolves the code from the Guid via `companies`;
InvRepo `Load` / `Post` (header + status update) / `SaveDraft` UPDATE accept either key via
`ResolveCompanyGuidOrNull` (resolution failure is reported through `AppHost.Errors` and
degrades to code-only matching, never silently). This is a bridge, not a cure: unifying the
key namespace is a data-migration project of its own (see Owner flags).

---

## 2. Per-defect closure (vs sweep IDs)

| ID | Defect | Closure | Proof |
|---|---|---|---|
| **ORD-01** (BLOCKER) | Order → invoice conversion produced a Posted invoice + party ledger + link with **zero stock movement and no COGS** | `OrderDocumentRepository.ConvertToInvoiceAsync` now posts stock inside the conversion transaction through the ONE `StockValuationEngine` (step 6), keyed by company CODE (the stock world's key): sales issue at the material's cost method (never selling price), purchases receive at line cost restated per base unit, unit equivalents applied, service materials skipped, warehouse = line ?? header ?? company default, warehouse-active guard mirrored from StkPost. GL gains COGS legs Dr `401` / Cr `1203` when COGS > 0; the GL guard is `grandTotal > 0 \|\| cogs > 0` so a free-goods invoice still books its COGS entry. Stock runs **after** the ACC-5006 credit guard, so an over-limit conversion is refused before any stock/GL effect. | Harness (b): 2-line SO → invoice 160, balances 96/960 + 48/960, GL balanced with Dr 401 = 80; purchase conversion 105/1060, no COGS. Kern.Tests `OrderChainStep9Tests` (Windows-owed). |
| **ORD-07 / I-8 / DS-02** | DocSearch Open discarded the selected id and opened a **blank** SalesInvoice | `DocSearchWin.OpenSelected` rewritten: orders → `OrdWin` + `LoadOrderAsync(row.DocId)`; SAL/PUR/QUO/SRT/PRT → `new InvoiceWin(row.DocId, kind)`, then the opener **verifies** `LoadedDocumentId == row.DocId` (mismatch → window closed, `NavigationFailed` reported, status shows `DocSearch.OpenUnsupported`). Unmapped kinds refuse honestly instead of falling through to a blank invoice. `InvoiceWin(long, kind)` now **throws** when the document cannot be loaded — a blank masquerading as an open is impossible. | Harness (a): FindById returns the stored order + converted invoice (id + totals); source-chain checks (no `new InvoiceWin()` in DocSearchWin). Kern.Tests `Load_FindsGuidKeyedDocument_SoDocSearchOpensIt`. |
| **DS-02** | Quotation → Sales Order conversion existed service-side with **no UI caller** | New **Convert to Order** button in DocSearchWin (Quotations mode only), gated by `PermCat.Sales.QuotationConvert`, calling `AppHost.Orders.ConvertQuotationToOrderAsync` → creates a DRAFT sales order with the QUO→SO link. Errors reported via `AppHost.Errors` (the window previously swallowed them / showed raw text). Lang keys `DocSearch.ConvertToOrder`, `DocSearch.ConvertedToOrder`, `DocSearch.OpenUnsupported` ×4. | Harness (d): QUO → linked DRAFT SO, total carried. Kern.Tests `Quotation_ConvertToOrder_CreatesLinkedSalesOrderDraft`. |
| **I-6 / Step-5 F-1** | InvoiceWin StockTransfer mode wired to `InvRepo.Post`, which fabricated stock; step 5 made it refuse (ACC-5014) | `InvRepo.Post` routes TRN at the top to `PostDedicatedEngineDocument`: claim (guarded Draft→Posted) → `StkPost.PostTransfer` (two-sided, value-conserving, FIFO layers move) → compensate back to Draft on engine failure (explicit stranded-state error if the revert itself fails — the step-8 cheque saga pattern). Source = `WarehouseId`, target = the new persisted `TargetWarehouseId`, reason = Notes ?? DocNo; line quantities restated to base units via the new public `StkPost.ToBaseUnits`. The engine's `TRF-…` number is written to `journal_doc_no`. `InvPostingPolicy`: TRN → `None` (no generic effects, not blocked). | Kern.Tests `StockTransfer_PostsTwoSided_AndConservesValue` (997/3, Σ stock_value = 40,000, no ledger/GL rows). Harness (c) engine composition. |
| **I-7 / Step-5 F-1** | InventoryCount added the **full counted quantity** instead of the delta | Same routing: CNT → `StkPost.PostCount` (delta-only by construction). Counted-qty-0 lines are included (a real zero count must zero the balance). ACC-5014 no longer exists for either kind (survives only in historical comments). | Kern.Tests `InventoryCount_PostsDeltaOnly_NotFullCountedQty` (count 1003 vs book 1000 → movement +3). Harness (c): count 10 vs book 7 → +3. |
| **ORD-03** | OrdWin Close checked the **sales** cancel permission for purchase orders too | Close permission is now kind-conditional (sales vs purchase family). | Code-traced; OrdWin diff in `4109309`. |
| **ORD-10** | OrdWin drafts carried a warehouse **name** with `WarehouseId: null` on header and lines | `BuildDraft` resolves the warehouse id via `CardStore.ResolveWarehouseId` and applies it to the header and every line. | Code-traced; conversion warehouse fallback chain now starts from a real id. |
| **ORD-13** (partial) | Silent catches in the touched windows | DocSearchWin (`OnLoaded`/`Search`/`SetLost`/`Convert`) and OrdWin (`OnLoaded`/`RefreshProgressAsync`) now report through `AppHost.Errors` with catalog codes. | Diff review; audit exit 0. |
| — | Step-7 convention drift in OrdWin | Hardwired `ExchangeRate = 1m` → `0m` (0 = no document rate, step-7 convention). | Diff review. |

**Deliberately not fixed here:** ORD-02 (see Owner flags), ORD-05 (absent feature, flagged),
DS-01 (raw kind/status codes in the DocSearch grid — display-only, flagged).

---

## 3. Wiring map (entry point → handler → engine)

| Entry | Handler | Engine / repository | Effects |
|---|---|---|---|
| DocSearch **Open** (order row) | `DocSearchWin.OpenSelected` | `OrdWin.LoadOrderAsync(row.DocId)` → `AppHost.Orders` → `OrderDocumentRepository.FindByIdAsync` | Window shows the stored order; shell context via BaseWin/ShellContext; `Owner = this` |
| DocSearch **Open** (SAL/PUR/QUO/SRT/PRT row) | `DocSearchWin.OpenSelected` | `new InvoiceWin(row.DocId, kind)` → `InvRepo.Load` (dual-key) → `ApplyDoc`; opener verifies `LoadedDocumentId` | The stored document, or a loud refusal — never a blank |
| DocSearch **Convert to Order** (quotations) | `DocSearchWin.ConvertToOrder` (perm `Sales.QuotationConvert`) | `AppHost.Orders.ConvertQuotationToOrderAsync` → `SalesChainService` | DRAFT sales order + QUO→SO link in `document_links` |
| OrdWin **Convert to Invoice** | `OrdWin` convert action → `AppHost.Orders.ConvertToInvoiceAsync` | `OrderDocumentRepository.ConvertToInvoiceAsync` (one transaction): remaining-qty invoice (step-5 stored totals) → credit guard ACC-5006 → `StockValuationEngine` movements (CODE-keyed) → `GlPosting` (revenue/purchases + COGS 401/1203) → party ledger → link | Invoice Posted; order billed quantities/status update from links; progress truthful incl. partial |
| InvoiceWin **Post** (TRN) | `InvoiceWin.PostDoc` → `InvRepo.Post` | claim → `StkPost.PostTransfer` → engine out/in at identical value; `TRF-…` no. linked | Stock moves source → target; value conserved; no GL, no party ledger |
| InvoiceWin **Post** (CNT) | `InvoiceWin.PostDoc` → `InvRepo.Post` | claim → `StkPost.PostCount` (delta-only) | Balance adjusted by counted − book only |
| InvoiceWin **Post** (all other kinds) | unchanged | step-5 path (stored totals + step-3 GL + step-6 stock via `StkPost.ApplyDocument`, which still early-returns TRN/CNT as defense in depth) | unchanged |

All opened windows keep the standing conventions: `Owner` set, no Topmost, shell context
(company/language/theme/currency) applied centrally at open.

---

## 4. Migration 064 — justification

`InvDoc.TargetWarehouseId` existed in the model with **no column**: a transfer's
destination could not persist, so TRN could never post honestly. 064 adds
`target_warehouse_id INTEGER` + `target_warehouse_name TEXT` to `sales_documents` and
`purchase_documents` (SQLite + PostgreSQL, guarded/idempotent). That is the only schema
change in this step, and it exists solely because the wiring gap was a missing column.

---

## 5. Comparison tables (condensed from the sweep compare files, with step-9 deltas)

### 5.1 OrdWin — orders (source: `compare-orders-projects-assets.md` §1)

| Capability | Al-Ameen V10 ⚠ | Phenix v10 ⚠ | ERPNext | Odoo | ShouTech after step 9 |
|---|---|---|---|---|---|
| Convert → Invoice | ✓ executed-vs-remaining | ✓ | Create ▸ Invoice | Create Invoice (invoiceable lines) | ✓ **fixed**: remaining-qty only, **now with stock at engine cost + full GL incl. COGS** (was: ledger only) |
| Partial conversion | ✓ editable row qty | ✓ | qty editable on generated doc | invoiceable-lines wizard | ◐ repository bills all *remaining* qty (partial across conversions proven); **no quantity-picker UI** (flag) |
| Convert → Delivery/Receipt | ✓ | ✓ | Create ▸ Delivery Note / Purchase Receipt | picking on confirm | ✗ **absent feature** (ORD-05, flagged — service has no UI caller for DLV) |
| Post permission | ✓ roles | ✓ | Submit (workflow) | Confirm (To Approve option) | ✗ Post still has **no permission check** (ORD-02, flagged — no ORDER.POST code exists in the catalog) |
| Close permission | ✓ | ✓ | ✓ | ✓ | ✓ **fixed**: kind-correct family (ORD-03) |
| Warehouse on lines | ✓ per line | ✓ | ✓ per row | per picking | ✓ **fixed**: real `WarehouseId` resolved onto header + lines (ORD-10); per-line warehouse *column* still not editable in the grid |
| Currency / FX | ✓ | ✓ | ✓ rate | ✓ | ◐ rate follows step-7 convention (0 = none); no FX control on the window (ORD-14, not this step) |
| Fulfilled / Billed progress | ✓ | ✓ | ✓ two axes | ✓ | ✓ Billed axis truthful end-to-end after step 9; Fulfilled axis still dead (depends on ORD-05) |
| Payment terms / price list / doc discount | ✓ | ✓ | ✓ | ✓ | ✗ unchanged (CORE-missing list §1.7 of the compare file; not step-9 scope) |

### 5.2 InvoiceWin — invoicing (source: `compare-invoicing.md` §1)

| Capability | Al-Ameen V10 ⚠ | Phenix v10 ⚠ | ERPNext | Odoo | ShouTech after step 9 |
|---|---|---|---|---|---|
| Post → GL + stock + ledger | ✓ | ✓ | ✓ Submit | ✓ Confirm | ✓ (steps 3/5/6; unchanged here) |
| Stock Transfer document | ✓ two-sided transfer | ✓ | Stock Entry (Transfer) | Internal transfer picking | ✓ **fixed (F-1)**: TRN mode posts via `PostTransfer` — two-sided, value-conserving, destination persisted (064); ACC-5014 refusal gone |
| Inventory Count document | ✓ count + delta adjustment | ✓ | Stock Reconciliation | Inventory adjustment | ✓ **fixed (F-1)**: CNT mode posts via `PostCount` — **delta only** (was: full counted qty fabricated; then refused) |
| Open existing document by id | ✓ | ✓ | ✓ | ✓ | ✓ **fixed**: `(docId, kind)` constructor loads from DB and **throws** if absent; openers verify `LoadedDocumentId` |
| Warehouse per line | ✓ lookup | ✓ | ✓ per row | via moves | ~ name resolved to id at capture (unchanged); TRN target is now a first-class persisted field |
| Due date / terms / price list / batch-serial columns | ✓ | ✓ | ✓ | ✓ | ✗ unchanged (compare file §1 CORE-missing; batch/serial belongs to the Phase B 054 design, not this step) |

### 5.3 DocSearchWin — document finder (source: `compare-orders-projects-assets.md` §6)

| Capability | ERPNext List/Global Search | Odoo List + Search | Al-Ameen / Phenix ⚠ | ShouTech after step 9 |
|---|---|---|---|---|
| Open the found document | ✓ | ✓ | ✓ | ✓ **fixed for every indexed kind** (was: orders only; invoices/quotations opened blank) |
| Convert Quotation → Order | Create ▸ Sales Order | Confirm | ✓ convert | ✓ **fixed**: Convert to Order button (quotations mode, permission-gated, expiry enforced service-side) |
| Families covered | every DocType | every model | per-family browsers | ✓ invoices / quotations / orders, both sides — **and search now sees both company-key families** (native invoices were invisible) |
| Kind/status display | translated pill | translated | localized | ✗ still raw codes (DS-01, flagged) |
| Print / export of results | ✓ | ✓ | ✓ reprint | ✗ unchanged (flagged in compare §6.3; not step-9 scope) |
| Outstanding / progress columns | ✓ | ✓ residual | ✓ remaining | ✗ unchanged (data exists via `GetProgressAsync`; display not wired) |
| Set Lost | Lost Reason captured | — | — | ◐ unchanged: still no lost-reason capture (flagged) |

---

## 6. Pillars checklists (COMPLETE-PILLARS rule: PROVEN or GAP)

### OrdWin
| Pillar | Verdict | Evidence |
|---|---|---|
| UI | PROVEN (existing) + step-9 fixes code-traced | Close permission family, warehouse resolution, rate convention; WPF execution owed on Windows |
| DB | PROVEN | Orders/lines persist via OrderDocumentRepository (steps 1–8); conversion writes invoice + link in one transaction — harness (b) |
| Linking | PROVEN | QUO→SO and SO→SAL links in `document_links`; progress computed from links — harness (b)(d) |
| Posting | PROVEN | Conversion: GL balanced incl. COGS, stock at engine cost, party ledger, credit guard before effects — harness (b) |
| Persistence | PROVEN | Every converted field lands in stored rows (invoice header/lines/totals read back by FindById) — harness (a)(b) |
| Downstream | GAP (flagged) | RptQuery reports filter by company CODE only → Guid-keyed converted invoices are missing from sales/purchase reports (report family's step) |

### InvoiceWin
| Pillar | Verdict | Evidence |
|---|---|---|
| UI | PROVEN (existing) + target-warehouse field code-traced | TRN-only target field (existing Lang keys); loud load failure; WPF execution owed |
| DB | PROVEN | Migration 064 persists the transfer target; SaveDraft/Load round-trip asserted in Kern.Tests TRN fact |
| Linking | PROVEN | Opened by id from DocSearch with `LoadedDocumentId` verification; engine TRF number linked into `journal_doc_no` |
| Posting | PROVEN (repository level) | TRN two-sided + value-conserved, CNT delta-only — Kern.Tests facts + harness (c) composition |
| Persistence | PROVEN | Target warehouse survives save/load; status claim/compensation keeps Draft on engine failure |
| Downstream | GAP (minor, flagged) | CNT movements carry the engine-internal `CNT-…` number, not the CNT document's number (reason text carries the DocNo when Notes is empty) |

### DocSearchWin
| Pillar | Verdict | Evidence |
|---|---|---|
| UI | PROVEN (existing) + Convert button code-traced | Quotations-mode button, permission-gated; WPF execution owed |
| DB | PROVEN | Search reads both company-key families — harness (a) |
| Linking | PROVEN | Open-by-id for all indexed kinds with verification; QUO→SO link written — harness (a)(d) |
| Posting | n/a (finder) — opens/post actions delegate to the document windows' proven paths | — |
| Persistence | PROVEN | No window state; Set Lost writes through the chain repository (unchanged) |
| Downstream | GAP (flagged) | DS-01 raw codes in grid; no print/export; no outstanding/progress columns (compare §6.3) |

---

## 7. Proof

**Headless harness** — `~/workspace/research/fix-step9/step9_harness.py` (+ `harness/Program.cs`),
real SqlMigrator + real repositories/services on SQLite: **20/20 PASS**

- Migrator: fresh-DB apply incl. 064, double-run idempotent, 064 columns on both tables,
  064 recorded in `schema_migrations`.
- (a) DocSearch load path: order by stored id → that order (id + total 75); converted
  invoice by stored id → that invoice (75, Posted); search matches the company-CODE key
  (native row found) and returns both key families.
- (b) Conversion e2e: invoice carries 2 lines / total 160; stock deducted at engine cost
  (96/960, 48/960 — not selling price); GL balanced, Dr 401 COGS = 80 / Cr 1203 = 80,
  Dr 1201 = 160 / Cr 301 = 160; progress 100%; re-conversion refused; movements carry the
  invoice number. Partial: pre-billed 4/10 via the chain path (no stock), conversion bills
  only the remaining 6 (150), stock moves for 6 only, progress 40% → 100%. Purchase:
  receipt at line cost (105/1060), Dr 402 / Cr 2201, no COGS.
- (c) TRN/CNT engine composition exactly as `StkPost.PostTransfer`/`PostCount` perform it:
  count 10 vs book 7 → movement **+3 only**; transfer 3 → source 10→7, destination 0→3,
  total value conserved (100).
- (d) QUO → linked DRAFT sales order via `OrderService` (total 50).
- Source-chain assertions (WPF layer): DocSearch opens by id with no blank fallback and
  verifies `LoadedDocumentId`; `InvRepo.Post` routes TRN/CNT to
  `PostDedicatedEngineDocument` → `StkPost.PostTransfer` / `StkPost.PostCount`; the policy
  no longer blocks either kind.

**Kern.Tests (execution owed on Windows — net8.0-windows testhost cannot run here):**
new `OrderChainStep9Tests` (5 facts mirroring the harness scenarios),
`InvPostCorrectnessTests` (TRN two-sided/value-conserved fact, CNT delta-only fact,
dual-key Load fact, policy matrix updated), `OrderServiceTests` fixture extended with the
stock world; `PostingCompletenessTests` credit-guard fact protected by the guard-before-stock
ordering. Full solution compiles 0/0 with all of them.

**Stated plainly:** WPF window execution (DocSearch/OrdWin/InvoiceWin interaction) is
**owed on Windows**. Everything below the window layer is proven above.

---

## 8. Gates

| Gate | Result |
|---|---|
| Full build `--no-incremental` | **0 errors, 0 warnings** |
| `tools/audit_window_context.py` | **exit 0** (71 windows, 0 window-level direction pins) |
| Lang parity (`tools/verify_english_source.py`) | **exit 0 — missing = 0 in all 4 catalogs**; step 9 added exactly 3 keys ×4 (`DocSearch.ConvertToOrder`, `DocSearch.ConvertedToOrder`, `DocSearch.OpenUnsupported`) |
| Migrator idempotent double-run (incl. 064) | PASS (harness) |
| No Arabic outside Ar.json | PASS — the only Arabic added is inside `Ar.json` (the 3 keys) |
| PostgreSQL | 064 PG counterpart written (`ADD COLUMN IF NOT EXISTS`); live PG run still owed (no PG server on this machine — same standing caveat as steps 5–8) |

Pre-existing Lang *extras* (Ar +114, Fr +9, Tr +9 keys with no En counterpart) exist at the
base commit and are unchanged by this step; the project gate counts them as non-failing.

---

## 9. Owner flags (decisions / follow-ups for Man)

1. **ORD-02 — OrdWin Post has no permission check.** No `SALES.ORDER.POST` /
   `PURCH.ORDER.POST` code exists in the permission catalog; inventing one is step-1 RBAC's
   call, not step 9's. Decide: add the codes (then wiring is one line) or accept unprivileged Post.
2. **ORD-05 — Delivery/Receipt UI is an entirely absent feature.** SO→DLV / PO→RCV has a
   service (`SalesChainService.CreateFromSourceAsync`) with no UI caller for DLV and no
   window. Flagged per mandate, not built. Until it exists, the Fulfilled axis and
   per-order delivery status stay dead, and ShouTech's chain remains
   Quotation → Order → Invoice → Payment (counterparts all have the delivery step).
3. **Partial conversion has no quantity picker.** The repository truthfully bills all
   *remaining* quantities per conversion (proven), but the user cannot choose "bill 3 of
   the remaining 6 now" — counterparts expose editable quantities on the generated invoice.
   A picker belongs with the ORD-05 window work.
4. **QUO→SO from an InvoiceWin-native quotation fails honestly.** A quotation written by
   InvoiceWin is company-CODE-keyed; the chain repository parses `company_id` as a Guid and
   the DocSearch Convert button reports the failure instead of converting. Full fix =
   company-key namespace unification (the §1 bridge made reads tolerant; writes/conversions
   still assume one key per family).
5. **Converted invoices are missing from sales/purchase reports.** RptQuery filters by
   company CODE only; converted (Guid-keyed) invoices don't appear. Same root cause as
   flag 4; the report family's step should adopt the dual-key match.
6. **CNT document number.** Count movements carry the engine-internal `CNT-…` number; the
   CNT document's own number appears only in the reason text (when Notes is empty).
   Cosmetic traceability gap.
7. **DS-01 — DocSearch grid still shows raw kind/status codes** in all four languages.
   Display-only; a localization pass over the grid was out of this step's wiring scope.
8. **Lang extras** (Ar +114 / Fr +9 / Tr +9 vs En) pre-date this step — a catalog cleanup
   decision, untouched here.
