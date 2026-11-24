# SOFT_DELETE_STUDY — Recycle Bin completion (phase-gate study)

Owner directive (Man, 2026-11-24 17:01): deleting ANYTHING — any document,
transaction/movement, invoice, card, any entity — must be a SOFT delete into
a RECYCLE BIN (سلة المحذوفات). From the bin the user can RESTORE the item or
delete it PERMANENTLY. This study is committed before the first build commit
of the phase, per the owner's phase-gate rule.

## 1. What the benchmarks do (researched 2026-11-24)

### ERPNext (docs.frappe.io, "Immutable Ledger" + "Deleting and Restoring Documents")
- A submitted (posted) document **cannot be deleted**; it must be **cancelled**
  first. Cancellation keeps the original GL / Stock Ledger rows and adds
  **opposite rows** for the same voucher: combined effect zero, audit trail
  intact. Reports exclude cancelled rows by default ("Show Cancelled Entries"
  is an opt-in filter).
- Deleted documents land in **Settings → Data → Deleted Documents**; from
  there they can be restored. **Only System Manager** role may restore.
- A cancelled document that was deleted after cancellation is restored under
  a **new name**; a cancelled posting document linked to ledger entries is
  normally **prevented from being deleted at all**, because it is the source
  of its original + reversal ledger rows.

### Odoo
- Two distinct operations: **Archive** (`active = false` — the record leaves
  every working list but keeps all history) and **Delete** (`unlink`).
- Posted journal entries **cannot be deleted**; correction is by reversal or
  reset-to-draft where the journal/lock-date permits. Referenced or locked
  records **refuse** delete; the product steers you to archive instead.

### SAP / Dynamics (global practice)
- Posted accounting documents are never deleted; they are **reversed** by a
  reversal document. Retention/archiving is a separate, admin-governed
  process with retention periods — the ledger itself is not purgeable from
  the application.

### Al-Ameen / Phenix
- No recycle-bin concept is documented in any public material found (help
  text, vendor pages, the screenshot/video evidence gathered this session).
  Their delete model is permission-guarded direct delete with an activity
  recorder. **A full bin with restore is therefore a differentiator, not a
  parity item** — recorded honestly, not presented as benchmark behaviour.

## 2. Where ShouTech stands today (audited on `reduce` @ be18204)

- **Cards already soft-delete.** `is_deleted` (+ `deleted_at`/`deleted_by` on
  the newer tables) is set by AccRepo.Delete, Inventory.SoftDelete /
  SoftDeleteWarehouse / SoftDeleteGroup, MasterCardRepository,
  DefinitionRepository, ProductCategoryRepo, PosTermRepo, and others.
- **A bin exists.** `RecycleBinSvc` lists/restores/purges a reviewed catalog
  of master-data tables; `BinWin` (BaseWin, Bronze tint, bottom buttons)
  opens from the shell; `deletion_audit_log` (migration 011) plus DB
  triggers record every flag flip and hard delete with a row snapshot.
- **The gaps this phase closes:**
  1. Commercial documents appear in the bin **only while Draft**
     (`DraftOnly` in the catalog). A **posted** invoice/voucher/order cannot
     be deleted at all — the document windows (InvoiceWin, OrdWin) and the
     journal window (JrnWin) contain **no delete action**.
  2. `journal_entries` has **no `is_deleted` columns at all**; posted manual
     journals/vouchers can only be reversed as a business correction, never
     binned.
  3. `sales_documents` / `purchase_documents` / `parties` /
     `units_of_measure` / `cheques` / `pos_sessions` lack
     `deleted_at`/`deleted_by`, so the bin cannot show who/when for them.
  4. BinWin has **no filters** (type / deleted-by / date).
  5. Card purge surfaces raw database errors; a still-referenced card must
     fail with a cataloged, localized message instead.

## 3. The design (what will be built)

### 3.1 One mechanism, central
A new app-layer service **`DocBinSvc`** (Services, InvRepo style — direct
parameterized ADO in one transaction per operation) owns the document and
journal bin lifecycle. `RecycleBinSvc` remains the bin's read model and the
card engine; it learns to list posted documents and journal entries and to
route their restore/purge through `DocBinSvc`. No per-window delete logic:
windows call the service.

### 3.2 Posting semantics — status-preserving compensation
The hard requirement: a binned **posted** document must move **no** balance,
and a restore must reinstate its effects **exactly once**.

The mechanism mirrors the product's own proven document-reversal arithmetic
(the return-document path nets GL to zero because the original entry keeps
`status = 1` and the compensation entry is also posted with swapped lines —
asserted by W1Harness scenario (c) and the trial balance's `status = 1`
convention). Binning therefore **never touches the original record's
status**; it adds compensation records in every effect store the document
touched, inside one transaction:

| Store | Neutralize (soft-delete of a posted doc) | Reinstate (restore) |
|---|---|---|
| GL (`journal_entries`) | New posted JE cloning each of the doc's JEs with debit/credit swapped, `reversal_of_entry_id` → original, source preserved, doc no `BIN-…`, **same entry_date** (the pair nets to zero in the doc's own period, so period reports are clean too) | New posted JE cloning the **original** lines as-is, `reversal_of_entry_id` → the bin JE, doc no `BIN-R-…` |
| Party ledger | Mirror rows: same doc_kind/doc_id, debit/credit swapped, note `BIN:NEUTRALIZE <event>` | Mirror rows again (swap back), note `BIN:REINSTATE <event>` — additive both ways, trail complete |
| Stock (`StockValuationEngine`) | For each **original** movement row of the document: an inverse movement through the one engine (qty_in/qty_out swapped; a re-entry carries the original movement's unit cost as `ReceiptUnitCost`), so balances/layers/average cost move through the same code that posted them | The original movements re-applied through the engine from the stored original rows |
| Tax register (`tax_transactions`) | Mirror rows with negated amounts under `source_doc_kind = '<KIND>-BIN'` (the register's unique key forbids a same-kind mirror; the register itself is append-only) | The `<KIND>-BIN` rows are **deleted** — the one bounded exception to append-only, forced by the register's unique key and recorded here; net register state returns to exactly the original rows |
| Settlement | Blocked upstream (see guards) — a document with payment allocations is never binned, so no allocation is ever left dangling | — |

Exactly-once is enforced by a new table **`bin_posting_events`**
(event_id, company, doc_table, doc_kind, doc_id, action
NEUTRALIZE/REINSTATE, journal_entry_id, actor, created_at): soft-delete
requires *no open NEUTRALIZE*; restore requires *an open NEUTRALIZE*;
both check-and-write inside the same transaction as the effects and the
header flag flip, so a double-click or a retry can never double-post.

**Why not `GlPosting.ReverseInTransaction`?** It flips the original entry
to `status = 2`. Several balance paths sum `status = 1` only (trial
balance, COA balances) while control reconciliations sum `IN (1, 2)` —
flipping the original would leave the swapped reversal counted alone
under the `status = 1` paths. Status-preserving compensation nets to zero
under **both** conventions and matches the already-proven return-document
arithmetic. Recorded as a deliberate deviation from the journal-reversal
engine, for ledger-report consistency.

**Closed periods:** the compensation JE carries the original entry_date.
The pair nets to zero in every period report, so no period total changes;
this is the same net effect as ERPNext excluding cancelled rows from
balances. Binning is therefore allowed for closed-period documents
(unlike a business reversal, which the product blocks in closed periods).

### 3.3 Guards — what blocks a soft delete (fail closed, cataloged errors)
- **Payment allocations exist** for the document → blocked (BIN-8202).
  Unallocate first (the reconciliation window owns that), exactly as
  ERPNext requires linked payments to be unlinked before cancel.
- **Chain links exist** (`document_links`, either side) → blocked
  (BIN-8203). Chain documents (deliveries, receipts, chain invoices,
  returns) are unwound through the chain's own flows first; a delivery's
  stock and the invoice's billing state can never be left inconsistent.
- **The document is one side of a completed `document_reversals` pair**
  → blocked (BIN-8203): binning one side would un-net the other.
- **Batch/serial lines**: neutralization drives the valuation engine with
  the original document number (the returns mechanism). If the engine
  refuses (e.g. serials have since moved on), the whole transaction rolls
  back and the delete fails with the engine's reason — never a partial bin.
- **POS sales (`PosSale`)**: excluded from binning in this phase — a POS
  sale belongs to a session whose Z totals must stay reproducible; POS
  corrections run through POS returns/voids. Named exclusion, BIN-8204
  if attempted programmatically.
- **Restore conflict (cards)**: if a live row reuses the binned row's
  unique key (e.g. material code), restore fails with BIN-8206 instead of
  a raw constraint error.
- **Purge of a still-referenced card** fails with BIN-8207.
- **Purge of a posted journal entry** is blocked (BIN-8205) — Odoo and
  this product's own journal rule agree: posted journals are never
  hard-deleted. Draft journals purge completely.

### 3.4 Purge semantics (the audit rule)
Purge removes the **business record** (document header + lines; card row;
draft journal + lines). For posted documents the **ledger chain stays**:
the original JE, its bin compensation/reinstatement JEs, party rows, stock
movements, tax rows and the `deletion_audit_log` snapshot remain forever —
they net to zero and are the accounting audit trail (the ERPNext rule:
ledger rows are evidence, never deleted through the application). The
document number is freed for reuse only in the sense that the header row
is gone; the audit log keeps what it was.

### 3.5 Permissions
- Soft-delete / restore: performed from the owning windows and the bin;
  the bin's restore follows the module delete permissions pattern already
  in the product.
- **Permanent purge from BinWin requires `SYS.ADMIN`** (ERPNext restricts
  restore/purge-class operations to System Manager; purge is the
  irreversible act, so it is admin-only here).

### 3.6 Coverage (what becomes soft-deletable in this phase)
- **Commercial documents** — all `InvDocKind`s in `sales_documents` /
  `purchase_documents` (invoices, returns, quotations, orders, stock
  in/out, opening stock, transfers, counts), Draft → flag only, Posted →
  full §3.2 semantics. Exclusions: `PosSale` (§3.3), chain-linked docs
  (guarded, not excluded by kind).
- **Journal entries** — manual journals and the voucher windows' entries
  (JrnWin; CshWin / PayRecWin post journal entries): Draft → flag,
  Posted → GL self-compensation + flag; purge of posted blocked.
- **Cards** — already soft-deleting; this phase completes their bin
  metadata (migration 122 adds `deleted_at`/`deleted_by` where missing),
  adds `cheques` to the bin catalog (flag-level, like other cards), and
  catalogues restore/purge failures (BIN-8206/8207).
- **Delete actions added** to InvoiceWin, OrdWin, JrnWin, CshWin,
  PayRecWin (bottom button bars, confirmation first). Binned journals
  disappear from JrnWin's list (read paths gain the `is_deleted` filter).
- **BinWin**: posted documents + journals listed with a status marker;
  filters for dataset/type, deleted-by, and deletion date range; purge
  gated on `SYS.ADMIN`.

### 3.7 Retention
No automatic purge/retention sweep in this phase: emptying the bin is a
manual, admin-only decision (SAP/Dynamics treat retention as a governed
policy, not a default). Recorded as a named non-goal.

## 4. Schema (migration 122, both dialects)
- `deleted_at` / `deleted_by` added where missing: `sales_documents`,
  `purchase_documents`, `parties`, `units_of_measure`, `cheques`,
  `pos_sessions`, `products`, `branches`, `brands`, `product_categories`,
  `material_groups` (guarded `@if-column-missing`, additive only).
- `journal_entries`: `is_deleted` (0/FALSE default), `deleted_at`,
  `deleted_by` added.
- New table `bin_posting_events` + index on (company_id, doc_table, doc_id).

## 5. Errors (BIN-82xx, ×4 languages, parity-checked)
BIN-8201 item not found / not in the bin · BIN-8202 allocated payments
block · BIN-8203 downstream links block · BIN-8204 kind excluded (POS
sale) · BIN-8205 posted-journal purge blocked · BIN-8206 restore unique
conflict · BIN-8207 purge still referenced · BIN-8208 state conflict
(already binned / not binned — the exactly-once guard).

## 6. Proof plan
A new headless harness `tools/BinHarness` (W1Harness pattern, real
services on a migrated SQLite DB):
post a sales invoice → assert GL/party/stock/tax effects → soft-delete →
assert every effect is back to the pre-post value and the doc is invisible
to InvRepo reads → restore → assert effects reinstated exactly → soft-delete
again → purge → assert header/lines gone, ledger chain nets to zero, bin
empty. Plus: draft document round-trip, posted journal round-trip,
allocation guard (BIN-8202), double-delete/double-restore exactly-once
(BIN-8208), and a card restore conflict (BIN-8206). Existing gates stay
green (build 0/0, migrator through 122 idempotent, 4 audit scripts,
errors/lang parity, P2pHarness 39/39, W1Harness 36/36).

## 7. What remains Windows-owed
Kern.Tests execution, WPF click-through of the new Delete buttons and the
filtered BinWin, and a live PostgreSQL run of migration 122 + the bin
flows — the same standing debt as every phase on this branch.
