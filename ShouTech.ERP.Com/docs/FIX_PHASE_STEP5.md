# FIX PHASE — STEP 5: InvRepo.Post Correctness (Stored Totals, Kind Guards, Symmetric Reversals)

**Branch:** `fix/foundations-5-invpost` (worktree `~/workspace/projects/ShouTech-fix5`)
**Base:** tip of `fix/foundations-4-parties` (`1e7984a`) — the foundations chain is sequential; Step 3 added `GlPosting` + migration 060, Step 4 unified parties on `parties.ledger_id` + migration 061 and made `InvRepo.Post` settle collections/POS.
**Date:** 2026-11-17 · **Status:** implemented, gated, NOT pushed, NOT merged.
**Evidence base:** `~/workspace/research/sweep/invoicing-posting.md`, `~/workspace/research/acceptance-sweep-report.md` (worst finding #6, §2 invoicing I-1/I-2), `docs/FIX_PHASE_STEP3.md`, `docs/FIX_PHASE_STEP4.md` (owner flag 3 is this step's reversal item).
**No migration in this step** — every total/field correctness required was already in storage (see §2); migrations 062+ were not needed and 053 remains reserved.

---

## 1. The defects, in one paragraph

`InvRepo.Post` SELECTed the document's stored `subtotal, tax_amount, grand_total` and then **threw them away**: the `InvDoc` it built set none of them and left `LinesDiscountAmount`/`LinesExtraAmount` at their 0 defaults, so the amount it debited to `party_ledger`, fed to the credit guard (ACC-5006) and — since Step 3 — posted to the GL was `InvoiceValue` recomputed from the lines alone. A document whose lines are 1,000 + 150 tax with a 100 header discount **stores 1,050 and debited 1,150**. Worse, after Step 3 the recomputation also broke the GL entry itself: the revenue legs summed to the lines total (1,150) while the control leg carried the grand total, so any discounted/extra-charge invoice was actually **unbalanced and rejected by GlPosting (ACC-5001)**. The posting was also kind-blind: `IsSales()` includes Quotation and the party-ledger block ran for any kind with a party, so **posting a quotation created a real receivable** and stock documents credited a supplier. And reversals were asymmetric in three places: the reversal document dropped the original's header money fields (its stored total therefore never cancelled the original's), a return's GL carried no settlement leg (a paid invoice's cash leg never netted out), and the settlement Step 4 writes at posting time was left dangling on reversal (Step 4 owner flag 3).

## 2. Amount-flow analysis — where the stored total was discarded

The stored total is **correct at save time**: `SaveDraft → BindHeader` writes `$grand = doc.InvoiceValue` (= lines subtotal − `discount_amount` + `extra_amount` + tax, `InvDoc.cs`), alongside stored `subtotal` and `tax_amount`. The loss happened entirely inside `Post`:

| Stage | Before Step 5 | After Step 5 |
|---|---|---|
| `Post` header SELECT | selected `subtotal, tax_amount, grand_total` **and discarded all three**; `discount_amount`/`extra_amount` were not even selected | selects all five and assigns them: `LinesDiscountAmount`, `LinesExtraAmount`, and the new `InvDoc.StoredSubtotal / StoredTaxAmount / StoredGrandTotal` |
| Amount definition | `doc.GrandTotal` = recomputed from the just-loaded lines with discount/extra = 0 | `doc.PostingTotal` (= `StoredGrandTotal ?? GrandTotal`) — the stored figure is the single amount of record; the recomputed value is used only for never-saved in-memory drafts |
| GL (`BuildGlLines`, Step 3) | control leg from the recomputed figure; revenue/purchase legs from raw line sums → unbalanced whenever discount/extra ≠ 0 (ACC-5001) | control legs, revenue/purchase/returns legs and stock-doc legs all derive from `PostingTotal`/`PostingTaxAmount`; the header adjustment is allocated across the revenue/purchase legs (§3.1) so the entry balances **exactly on the stored total** |
| `party_ledger` | debit/credit = recomputed figure | debit/credit = `PostingTotal` |
| Credit guard (ACC-5006) | fed the recomputed figure | fed `PostingTotal` — the figure that will actually post (mandate 4) |
| Settlement cap (Step 4) | `Math.Min(PaidAmount, recomputed)` | `Math.Min(PaidAmount, PostingTotal)` |
| Disagreement handling | silent — the recomputation *became* the posted number | if lines + header adjustments in storage no longer recompute to the stored `grand_total`, the **stored total still wins** for every consumer and the disagreement is reported via the error catalog (**ACC-5013 `DocumentTotalMismatch`**, `AppHost.Errors.Report`, never silently recomputed) |

`InvRepo.Load` now also populates the `Stored*` fields, so any holder of a loaded document (InvoiceWin reload, the reversal flow) sees the same totals posting will use.

### 3.1 GL leg allocation

The header adjustment (−discount + extra, plus any stored-vs-lines residue reported under ACC-5013) is, in accounting terms, a revenue/purchase adjustment. `BuildGlLines` computes each line's base leg (line subtotal, plus line tax when tax is not split to a VAT account), then allocates the adjustment across lines **in proportion to |line subtotal|** with the rounding residue on the largest line. Per-material account overrides (`acc_sales`, `acc_purchase`, …) therefore keep their proportional share, the aggregated legs sum to exactly `grand − taxLeg`, and with the tax leg the entry balances on the stored total to the last 0.0001. COGS pairs are untouched (cost-based, self-balancing; Step 6 owns costing). StockIn/StockOut GL legs now carry the stored grand total directly (previously subtotal+tax recomputed).

## 3. Kind → effects matrix (enforced)

One definition — **`InvPostingPolicy.For(kind)`** in `InvRepo.cs` — enforced inside `InvRepo.Post` at the repository layer, so every caller (InvoiceWin, PosCheckoutWin, any future caller) is safe. `StkPost.ApplyDocument` additionally refuses transfer/count kinds itself (defense in depth for direct callers).

| Kind (InvRepo) | GL | Party ledger | Stock via StkPost | Settlement at posting | Credit guard |
|---|---|---|---|---|---|
| SalesInvoice (SAL) | ✓ Step-3 mapping | ✓ customer **debit** = stored total | out | ✓ (RCV + allocation) | ✓ on the debit |
| PosSale (POS) | ✓ tender legs | ✓ customer debit (if a party is resolved) | out | ✓ | ✓ |
| SalesReturn (SRT) | ✓ mirror, incl. settlement leg (§4) | ✓ customer **credit** | in | ✗ — a return is not a payment | ✗ — reduces exposure |
| PurchaseInvoice (PUR) | ✓ | ✓ supplier **credit** = stored total | in | ✓ (PAY + allocation) | ✓ on the credit (unchanged semantics) |
| PurchaseReturn (PRT) | ✓ mirror, incl. settlement leg | ✓ supplier **debit** | out | ✗ | ✗ |
| StockIn (ENT) | ✓ Dr Inventory / Cr Purchases at stored total | **✗** | in | ✗ | ✗ |
| StockOut (EXT) | ✓ Dr COGS / Cr Inventory at stored total | **✗** | out | ✗ | ✗ |
| OpeningStock (OPN) | ✗ (valuation event — Step 6, Step-3 flag F-4) | ✗ | in | ✗ | ✗ |
| **Quotation (QUO)** | **✗** | **✗ — never a receivable** | **✗** | **✗** | **✗** |
| StockTransfer (TRN) | — **BLOCKED in this path (ACC-5014)** — | | | | |
| InventoryCount (CNT) | — **BLOCKED in this path (ACC-5014)** — | | | | |

Notes on the blocked kinds: through `InvRepo` a transfer posted every line as qty-in at the single header warehouse (**stock from nothing**, sweep I-6 — `InvDoc.TargetWarehouseId` is not even persisted, so a correct two-sided transfer cannot be built from this document) and a count added the **full counted quantity instead of the delta** (I-7). The correct engines exist and are what the warehouse windows use — `StkPost.PostTransfer` (two-sided) and `StkPost.PostCount` (delta-based) — so this path now refuses with a cataloged `AppError` (ACC-5014) that InvoiceWin already renders as a localized catalog message. Wiring InvoiceWin's TRN/CNT modes to those engines is a UI job, flagged in §7 (F-1).

Kinds that do **not** flow through InvRepo, for completeness of the enumeration: the commercial-chain kinds (`CommercialDocKinds`: QUO/SO/DLV/SAL, RFQ/PO/RCV/PUR) live in `CommercialDocumentRepository`, which already guards them the same way (QUO/SO post with no side effects, DLV moves stock only, SAL books ledger+GL from its **stored** header totals); order/proforma kinds live in `OrderDocumentRepository` (orders post with no ledger/GL/stock; conversion creates the invoice and posts its stored totals). Neither needed changes for this step's mandate and neither was touched.

A quotation posted through InvRepo now does exactly one thing: its status flips to Posted inside the transaction. No `party_ledger` row, no GL entry (its `journal_doc_no` stays empty), no stock movement, no settlement, no guard evaluation — proven in §6 (c).

## 4. Reversal symmetry design

`ReversePostedInvoice` remains a return-document reversal (the product's established correction model), made fully symmetric:

1. **Header copy is complete.** The reversal `InvDoc` now copies *all* header money and context fields from the original: `LinesDiscountAmount`, `LinesExtraAmount`, `PaidAmount`, `PaymentMethod`, warehouse name (header and per-line), receipt no, reference, branch, sales rep, cost center, party/goods account names (previously all defaulted — sweep §2 BLOCKER). Its stored `grand_total` therefore equals the original's, and stored totals cancel exactly.
2. **GL mirrors the settlement leg.** Return GL legs (`BuildGlLines` SRT/PRT) now split the control leg exactly as the original did: the paid part through the settlement account (cash/bank by tender), only the unpaid remainder through Receivable/Payable. A paid invoice + its return now net to zero on **every** GL account (revenue vs its returns contra account net together, as designed in the chart). The purchase side's settlement account selection was unified to the same tender rule the sales side already used.
3. **Settlement un-does itself (Step 4 owner flag 3 — closed).** In the reversal's finalise transaction, `UnsettleOriginalOnReversal`:
   - **mirrors** every settlement entry written *at the original's posting* — identified precisely as `party_ledger` rows with `doc_kind IN ('RCV','PAY') AND doc_id = original` (the shape only `InvRepo.Post` writes; cash-voucher settlements carry `doc_id NULL` and installment collections use `INS`, so neither can be mistaken for one) — with debit/credit swapped, pointing at the reversal document. The ledger stays append-only; the settlement's net effect becomes zero;
   - **deletes every `payment_allocations` row against the original**. Allocations of *external* payments (receipt/payment vouchers allocated later in PayRecWin) simply free that payment again — the voucher's own ledger row stands, because the money really was received, and the party keeps the resulting credit balance (a refund owed / credit on account), which is the correct post-reversal position. A full reversal may leave no allocation against the original standing;
   - **returns the original's `paid_amount` to 0** — its settled state is exactly as if it had never been posted.
4. **Stock** already reversed symmetrically through the return's `StkPost` posting (quantities; valuation/avg-cost restatement is Step 6's, unchanged).
5. The pre-existing multi-transaction reservation design (Pending/Failed reconciliation) is unchanged; the un-settlement rides the existing finalise transaction, so it commits or fails with the reversal's completion record.

## 5. Per-defect closure

| Sweep ID | Defect | Closure |
|---|---|---|
| I-1 (§1 BLOCKER 1; report #6) | Post discards stored `grand_total`, debits the recomputed lines-only figure (1,050 stored → 1,150 debited); credit guard fed the same wrong figure | §2: stored totals loaded and consumed by GL + party_ledger + guard + settlement cap via `PostingTotal`; discrepancy reported ACC-5013, stored wins |
| I-1 consequence (found in this step) | After Step 3, a discounted/extra-charge invoice's GL entry was unbalanced (legs = lines total vs control = grand) → ACC-5001 rejection | §3.1 leg allocation: entry balances exactly on the stored total |
| I-2 (§1 BLOCKER 3) | Kind-blind ledger: quotation creates a receivable; stock kinds credit a supplier | §3: `InvPostingPolicy` enforced in `Post`; quotation has zero effects; ENT/EXT/OPN have no party ledger |
| §2 reversal BLOCKER | Reversal doc drops header money fields → stored totals don't cancel | §4.1 full header copy |
| Step 4 owner flag 3 | Reversing a paid invoice leaves its at-posting settlement dangling | §4.3 un-settlement: settlement mirrored, allocations deleted, `paid_amount` → 0 |
| §2 reversal MAJOR (GL part) | Return GL had no settlement leg, so a paid invoice's cash/bank leg never netted | §4.2 return settlement legs (SRT/PRT) |
| I-6 / I-7 (partial) | StockTransfer via InvoiceWin creates stock from nothing; InventoryCount adds the full counted qty | Blocked in this path (ACC-5014) + `StkPost.ApplyDocument` refuses both kinds; the dedicated engines remain the correct path. **The InvoiceWin TRN/CNT windows are not rewired** — flag F-1 |
| I-5 (Step-3/4 parts already closed) | `paid_amount` ignored at posting | Already closed by Steps 3+4 (GL settlement leg + ledger settlement); this step makes both consume the stored total and makes reversal mirror them |

Out of scope and untouched, as mandated: valuation/costing incl. reversal avg-cost restatement and movement `unit_cost` basis (Step 6); GL account mapping (Step 3); the party model (Step 4); the invoice UI and document schema (no schema change was needed — no migration); order→invoice stock (a different repository's defect, not InvRepo); the chain DLV guards (already kind-guarded, §3 note).

## 6. Proof

### 6.1 Headless harness — `~/workspace/research/fix-step5/invpost_harness.py` — **30/30 PASS**

Builds a DB with the fix5 migrator, seeds documents in the exact `SaveDraft`/`BindHeader` storage shape, and replays the new production posting shapes + source-checks the production code:

- (a) the sweep's exact case: lines 1,000 + tax 150 − discount 100 → **stored 1,050**; the old recomputed figure is demonstrated to be 1,150; party_ledger debit = **1,050**; the GL leg allocation sums to exactly 1,050 (entry balances); the credit guard on the stored figure passes a 1,100 limit that the old figure would have breached.
- (b) extra charges: stored 1,075 posts 1,075 to ledger and GL legs.
- (c) kind policy source-verified against `InvRepo.cs`/`StkPost.cs` (stored totals assigned in Post, all consumers on `PostingTotal`, ACC-5013/5014 wired, ledger/stock/GL gated by the policy, quotation = no effects, TRN/CNT blocked and refused by `StkPost`); a seeded quotation carries no ledger row.
- (d) paid invoice → reversal shapes: settlement mirror + allocation delete + `paid_amount` → 0 net the party ledger for the invoice to zero (200 Dr = 200 Cr across invoice, settlement, return, mirror).
- Migrator double-run idempotent (no new migration in this step).

### 6.2 Kern.Tests — `tests/Kern.Tests/InvPostCorrectnessTests.cs` (9 facts)

Real C# paths against real migrated SQLite databases, through the real `InvRepo` / `MasterCardRepository`:
(a) discounted sales invoice posts exactly 1,050 to GL (1201 Dr / 301 Cr) and party_ledger; the same for a purchase invoice to Payable/Purchases; (b) extra charges post the stored 1,075; (c) a posted Quotation has status Posted with **zero** party_ledger rows, zero GL entries, zero stock movements and an unchanged balance quantity; (d) a fully-paid cash invoice (settlement + allocation at posting) reversed through `ReversePostedInvoice` → party debit = credit in total, allocations gone, original `paid_amount` = 0, stock restored, reversal's stored grand = original's, GL nets to zero on cash/receivable/inventory/COGS individually and revenue-vs-returns combined; (e) credit guard: a 1,050-stored doc passes a 1,100 limit (the old 1,150 figure would have blocked), and a further doc that truly exceeds the limit blocks with ACC-5006; StockIn with a party creates no ledger row but moves stock; StockTransfer and InventoryCount throw ACC-5014 and fabricate nothing; the policy matrix itself is pinned by a fact. **Execution is owed on Windows** (net8.0-windows testhost cannot run on this Linux VM) — the project compiles at 0 errors / 0 warnings here.

### 6.3 What is NOT proven here

WPF runtime behavior (clicking Post/Reverse in InvoiceWin, the ACC-5014 message rendering for TRN/CNT modes — the window's existing `AppError` handler renders catalog messages, code-verified) and **live PostgreSQL** execution are owed on Windows / a live PG instance, as with every prior step. No query in this step is provider-specific except the two `is_deleted` literal forms in the un-settlement update, which follow the existing dual-provider pattern in the same file. Build + SQLite harness success is not product readiness, per the standing rule.

## 7. Gates

| Gate | Result |
|---|---|
| Full solution build `--no-incremental` | **0 errors, 0 warnings** |
| `tools/audit_window_context.py` | **exit 0** |
| Lang parity (En/Ar/Fr/Tr) | **0** missing/extra in both Lang and Errors catalogs (2 new error codes ×4 languages; no new Lang keys — no UI text added) |
| Arabic outside Ar.json | None (verified over the diff) |
| Migrator idempotent double-run (SQLite) | PASS (harness; no new migration) |
| Headless harness | **30/30 PASS** (§6.1) |

## 8. Owner flags

- **F-1 — InvoiceWin TRN/CNT modes now refuse to post (visible change, deliberate).** Posting a StockTransfer or InventoryCount from InvoiceWin previously *fabricated stock*; it now surfaces ACC-5014 and posts nothing. The windows themselves still open in those modes. Wiring them to `StkPost.PostTransfer`/`PostCount` (the WhsWin engines) — including persisting a target warehouse for transfers — is a UI build that should be scheduled; until then the warehouse windows are the correct route for transfers and counts.
- **F-2 — Reversal of an invoice allocated against external payments.** Un-allocation frees the external payment (its ledger row and the party's resulting credit balance stand, §4.3). If Man instead wants reversal *blocked* while external allocations exist, that is a one-line policy change in `UnsettleOriginalOnReversal` — the current behavior was chosen because a full reversal must not leave the invoice half-settled.
- **F-3 — Original document's status after reversal** still reads `Posted` (reversal state lives in `document_reversals`, pre-existing sweep §2 MAJOR). This step makes its *settled* state truthful (`paid_amount` 0, no allocations); changing the displayed status is a reporting/UI decision for Man.
- **F-4 — Standalone returns with a PaidAmount** now post a GL settlement leg (refund through cash/bank) mirroring the sales/purchase split. That is the accounting-correct reading of the field on a return; if Man intends return `paid_amount` to mean something else, the leg should be revisited.
- **F-5 — ACC-5013 is report-only by design.** A stored-vs-recomputed disagreement posts the stored total and logs the catalog code. If Man prefers a hard block on mismatch, the report call becomes a throw — flagged because silently *changing* the number was the original defect, and blocking could strand legacy rows whose stored totals are the only correct record.

## 9. Comparison table (InvoiceWin — materially changed posting behavior)

Changes in this step are repository-layer, **except** two behaviors a user can see: a discounted/extra-charge invoice now *posts successfully* (after Step 3 its GL entry was unbalanced and GlPosting rejected it with ACC-5001; before Step 3 it posted the wrong amount with no GL at all), and the TRN/CNT modes of InvoiceWin now refuse to post (F-1). Because behavior changes are visible, the comparison table is included (condensed from `~/workspace/research/sweep/compare-invoicing.md`; ⚠ = counterpart behavior asserted from product knowledge in the sweep, not live-verified).

| Capability | ShouTech before Step 5 | Al-Ameen V10 ⚠ / Phenix v10 ⚠ | ERPNext | Odoo | **Step 5** |
|---|---|---|---|---|---|
| Posted amount = document's stored total (header discount/extra honored) | ✗ stored 1,050 debited as 1,150 | ✓ | ✓ | ✓ | **✓** — one stored figure for GL, party ledger, guard, settlement |
| Discounted invoice posts a balanced GL entry | ✗ unbalanced (ACC-5001) after Step 3 | ✓ | ✓ | ✓ | **✓** — header adjustment allocated across revenue/purchase legs |
| Posting a quotation | ✗ created a receivable | ✓ no accounting effect | ✓ no GL/ledger until conversion | ✓ no journal until invoiced | **✓** — status only; no receivable, GL, stock or settlement |
| Stock documents (in/out) creating party balances | ✗ credited a supplier when a party was typed | ✓ no party effect | ✓ | ✓ | **✓** — no party ledger for ENT/EXT/OPN |
| Stock transfer / count from the invoice window | ✗ fabricated stock (in-from-nothing / full-qty add) | ✓ two-sided transfer / delta count | ✓ stock reconciliation (delta) | ✓ scrap/adjustment (delta) | **◐** — refused in this path (ACC-5014); dedicated warehouse engines do it correctly; window rewiring flagged (F-1) |
| Reversal of a paid invoice | ✗ settlement dangled; original stayed "paid"; GL cash leg never netted | ✓ full storno | ✓ credit note reverses payment allocation | ✓ reversal un-reconciles | **✓** — settlement mirrored, allocations removed, `paid_amount` → 0, GL nets to zero |
| Credit-limit check figure | ✗ recomputed (discount dropped) | ✓ document total | ✓ grand total | ✓ amount total | **✓** — the stored total that actually posts |

## 10. Pillars checklist — invoice/document surface (InvoiceWin / PosCheckoutWin via InvRepo)

- **UI:** GAP (deliberate) — no window code changed; the only visible deltas are correct posting outcomes and the ACC-5014 refusal in TRN/CNT modes (F-1). WPF click-through owed on Windows.
- **DB:** PROVEN — no schema change needed; stored header totals (`subtotal`, `discount_amount`, `extra_amount`, `tax_amount`, `grand_total`) already existed and are now actually consumed; migrator double-run idempotent.
- **Linking:** PROVEN — GL entries keep the Step-3 source link and now balance on the document's stored total; reversal documents link via `original_*` + `document_reversals` as before, with complete header context copied.
- **Posting:** PROVEN — stored-total posting to GL + party_ledger, kind→effects policy enforced at the repository layer, credit guard on the posting figure, settlement mirrored on reversal (harness §6.1 + Kern facts §6.2).
- **Persistence:** PROVEN — posting reads only persisted fields; the reversal persists the full copied header (discount/extra/paid/method/context) so its stored total cancels the original's.
- **Downstream:** PROVEN at data level — party balances, PayRecWin open amounts (allocations/`paid_amount` maintained symmetrically), and GL reports now all derive from the same stored totals; GAP: live report rendering owed on Windows.

## 11. Files changed

- `cod/app/src/Reporting/Invoices/InvDoc.cs` — `StoredSubtotal/StoredTaxAmount/StoredGrandTotal` + `PostingSubtotal/PostingTaxAmount/PostingTotal`.
- `cod/app/src/Services/InvRepo.cs` — stored-total load in `Post`/`Load`; ACC-5013 discrepancy report; `InvPostingPolicy` kind→effects enforcement; GL leg allocation + return settlement legs on stored totals; full reversal header copy; `UnsettleOriginalOnReversal`.
- `cod/app/src/Services/StkPost.cs` — `ApplyDocument` refuses StockTransfer/InventoryCount.
- `src/ShouTech.Domain/ErrorCodes.cs` + Errors catalogs ×4 + `docs/ERROR_CATALOG.md` — ACC-5013, ACC-5014.
- `tests/Kern.Tests/InvPostCorrectnessTests.cs` (new, 9 facts).
- `docs/FIX_PHASE_STEP5.md` (this file).
