# Purchase Cycle Completion — ERPNext Parity (Procure-to-Pay)

Branch: `feat/purchase-cycle-erpnext`
Base: `origin/work/full-app-stabilization-2026-11-18` @ `52a762adc98a3a2cd003c95eb13475b4c01c432e`
(the owner's own stabilization branch; he is still committing to it, so a rebase
onto his later commits may be needed before merge).

Method: audit first (six code-traced slices, evidence in
`~/workspace/research/p2p-audit/01..06`), then build. Every claim below was
verified against code, not trusted from the docs — the docs proved stale in
both directions (features denied by the guides exist; the parity matrix's
"Partial / verify" hides both finished work and real defects).

## 1. Audit verdict per stage

| Stage | Verdict | One-line summary |
|---|---|---|
| Material Request (PRQ) | PARTIAL | Real sequential multi-level approval + RBAC + PO conversion; **no budget check, no cancel/stop, no resubmit, no RFQ conversion, no schedule date/type** |
| Request for Quotation | PARTIAL | Exists as `doc_kind="RFQ"` on the shared engine; **no supplier invitation list, no dispatch, no MR→RFQ link** |
| Supplier Quotation | DONE (strongest) | Entry + comparison + award to PO with persisted links; terms/reference lost on award; whole-quotation award only |
| Purchase Order | PARTIAL | Solid lines/totals; **no payment terms, incoterm, currency/rate in OrdWin, stored UOM factor, per-line required-by**; per_received/per_billed computed-only; cancel orphans draft children; no budget check |
| Purchase Receipt | DONE with one BLOCKER | Posted atomically (stock + Dr Inventory / Cr GRNI 2203), hard over-receipt block, partial receipts, batch/serial/expiry capture, FEFO — **but the migration-056 QC gate is bypassed on this path** |
| Purchase Returns | DONE | Two source-line-allocated paths (pre-invoice RCR vs receipt, post-invoice PRT debit note vs invoice), cumulative guards under lock |
| Purchase Invoice | PARTIAL | Chain path: born Posted from PO, three-way qty invariant hard-enforced, GRNI cleared at historical rate; **chain invoices cannot carry the supplier bill reference (uniqueness unenforced on the main path), no bill_date, no tax-register rows** |
| Three-way match | DONE (opt-in) / PARTIAL | Policy engine (mig 082) + persisted evaluations (083) exist; Blocked blocks *payment allocation* fail-closed with audited override; **no per-line status, legacy invoices can never be evaluated, price variance aggregate-only** |
| Payment / allocation | DONE (core) | PayRecWin allocation in one validating transaction, `paid_amount` maintained, reversal fully audited (mig 079); **advances are not a concept** (no PO link, no prepayment account, no consumption at invoice) |
| GRNI | DONE / PARTIAL | Account 2203 + aging report with buckets + GL tie-out exist (matrix line is stale); **no exception view, two report defects** (detail window −1yr vs unbounded totals; GL row columns never populated) |
| Landed Cost | DONE / PARTIAL | Genuinely posted: valuation restatement + consumed split + balanced GL + party credit in one transaction, mirrored-voucher reversal; **payee always the receipt's supplier, one receipt per voucher, reversal split recomputed not stored, no dedicated PPV account** |
| Payable aging | PARTIAL | Ages party-ledger entries by entry date vs "now"; no as-of, no due-date basis, no per-invoice drill-down |
| Withholding / TDS | MISSING | Zero implementation. **Owner decision** (country-specific) — flagged, not built. |

## 2. Gap list — what this branch builds

### Requisition (audit slice 1)
- **R1 Budget check.** Requisition submit (and approval completion) consults
  `BudgetGuard` for the requisition's estimated amount against the
  account/department budget dimension available; Stop-mode blocks, Warn-mode
  records an override event. Today BudgetGuard is only reached from GL posting.
- **R2 Cancel / stop / close.** Workflow states + window actions to cancel a
  draft/submitted requisition (with reason), blocked once converted; close for
  fully converted.
- **R3 Resubmit after rejection.** Rejected requisitions can be edited and
  resubmitted (new approval round, events preserved).
- **R4 PRQ → RFQ conversion.** Add (PRQ, RFQ) to the permitted conversion
  pairs with persisted `document_links`, like PRQ → PO.
- **R5 Schedule date + request type.** `required_by` date (header, defaulted
  to lines) and request type (Purchase / Material Transfer / Material Issue /
  Manufacture) persisted end-to-end (migration, both providers).
- **R6 Field round-trips.** Department becomes a real persisted field (today
  smuggled into Notes); warehouse selection restored on load.

### RFQ / Supplier Quotation / PO (slice 2)
- **P1 RFQ supplier invitations.** Persist invited suppliers per RFQ with
  per-supplier status (Invited / Quoted / Declined); SQ creation picks up the
  invitation. (Dispatch by email is out of scope — no mail infrastructure
  decision exists; invitations are recorded and printable.)
- **P2 Award fidelity.** SQ terms + external reference carry onto the awarded
  PO (header fields, see P3); per-line (split) award: award a subset of SQ
  lines to a PO.
- **P3 PO header completion.** Payment terms (persisted text/code), incoterm,
  currency + exchange-rate fields in OrdWin for PO/RFQ (today forced to
  user-pref currency, rate 0).
- **P4 PO line completion.** Stored UOM conversion factor on the line
  (snapshotted from the material card at entry, editable, used at posting
  instead of re-deriving); per-line required-by date (defaults from header).
- **P5 Stored per_received / per_billed + derived status.** Persist
  percentages on the PO header (and per-line received/billed qty already
  tracked becomes stored percentages), maintained inside the receipt /
  invoice / return / reversal posting transactions; derived status
  (To Receive and Bill / To Bill / To Receive / Completed) computed from
  them and shown in OrdWin + OrderTrackWin; readable by SQL/reports.
- **P6 Dependency-aware cancel.** Cancelling a PO (or requisition) with
  *draft* downstream documents cancels those drafts in the same transaction
  (posted downstream already blocks). Amend-after-cancel: "Amend" creates a
  new draft PO copying the cancelled one, linked to it (ERPNext amend).
- **P7 Budget check at PO posting.** Posting a PO consults BudgetGuard on
  the PO total (commitment) — same Stop/Warn semantics as R1.

### Receipt (slice 3)
- **Q1 QC GATE BYPASS — blocker.** Chain receipts
  (`OrderDocumentRepository.CreatePurchaseReceiptAsync`) must run the same
  migration-056 quality gate as StkPost: QC-required lines divert to the
  quarantine warehouse with a Draft inspection; accepted qty flows to the
  target warehouse on inspection pass, rejected qty stays quarantined for
  return. No receipt of a QC-required material may land directly in sellable
  stock.
- **Q2 Rejected quantity at receipt.** Per-line rejected qty + reason:
  rejected qty is received into quarantine (or not received) without
  inflating received qty; returnable via RCR.
- **Q3 Receipt header facts.** Receipt date is user-settable (not forced to
  today); supplier delivery-note number persisted on the receipt.
- **Q4 Over-receipt tolerance.** Company setting (in the purchase match
  policy, mig 082 table) for receipt over-delivery tolerance %; the hard
  block applies beyond tolerance instead of at 100.00%.

### Invoice / match / identity (slice 4)
- **I1 Chain bill identity.** Chain purchase-invoice creation accepts the
  supplier bill number + bill date; persisted (`reference_no` + new
  `bill_date`); uniqueness per (company, supplier, normalized reference)
  enforced on the chain path too, raising a **catalogued error code**
  (registered in all 4 languages) instead of a plain exception. The manual
  path's check adopts the same code.
- **I2 Chain tax registration.** Chain purchase invoices write immutable
  `tax_transactions` INPUT rows through the tax engine like the manual path
  (line tax code + recoverability honoured; non-recoverable tax capitalized),
  so chain invoices appear in tax reporting.
- **I3 Match evaluation completeness.** (a) Evaluation can be created on
  demand for any posted purchase invoice (legacy/pre-policy invoices are no
  longer permanently un-blockable). (b) Per-line variance detail (qty, rate,
  amount variance per line) stored in the evaluation snapshot and viewable.
- **I4 PRT unwinds the match.** Posting a purchase return (debit note)
  unwinds the 078 receipt↔invoice matches and the PO billed quantity for the
  returned lines, so returned goods can be re-received/re-invoiced; reversal
  of the PRT re-winds them.
- **I5 Debit-note own reference.** PRT accepts its own supplier credit-note
  reference instead of silently copying the original invoice's.

### Payment / GRNI (slice 5)
- **Y1 Supplier advances.** A payment voucher can be linked to a PO as an
  advance: posted Dr Advances-to-Suppliers (new COA account, seeded both
  providers) / Cr Cash-Bank instead of Dr AP; the advance is visible on the
  PO; when a purchase invoice for that PO is allocated payment, advance
  balances are offered and consumed by allocation (advance → AP clearing
  entries in the allocation transaction), with reversal symmetry.
- **Y2 GRNI exception view.** GrniWin gains an Exceptions mode: (a) received
  lines older than a threshold with remaining qty (aged open receipts),
  (b) invoiced-without-receipt lines (manual-path invoices with no 078
  match), (c) receipts whose GL 2203 tie-out differs per period. Fix the two
  defects: detail query period aligns with the reconciliation totals
  (no silent −1yr cutoff), and the GL balance/difference row columns are
  populated.
- **Y3 Payable aging by due date.** SupAgingWin gains an as-of date and
  ages open purchase invoices by due date (invoice `due_date`; new field if
  absent) with per-invoice drill-down rows; entry-date mode remains.

### Landed cost (slice 6)
- **L1 Per-charge payee.** Each landed-cost charge may name a payee party
  (shipper/broker/other supplier); the party-ledger credit goes to the
  payee (GL credit stays AP control). Default remains the receipt supplier.
- **L2 Already-booked charges.** Per-charge credit mode: `Payable` (default)
  or `AlreadyBooked` — the latter credits the charge's expense account
  instead of AP, so freight already invoiced is not double-counted.
- **L3 Exact reversal.** The inventory/consumed split per allocation is
  stored at post time (migration); reversal replays the stored split
  exactly instead of recomputing at reversal time.
- **L4 Multi-receipt vouchers.** A landed-cost voucher may select lines
  from more than one receipt (picker over posted receipts), with per-line
  exclusion.
- **L5 PPV account.** Invoice-vs-receipt price variance posts to a
  dedicated Purchase Price Variance account (seeded, both providers)
  instead of Purchases 402, so PPV is separately reportable.

### Gate hygiene inherited from the base branch
- **H1** Fix the 2 CA1859 warnings in `PurchaseReceiptReturnWin.cs`
  (4 warning instances) at the cause → build 0/0.
- **H2** `verify_screenfit.py` exit 0: 13 windows exceed the 684×384
  minimum cap on the base (incl. PosCheckoutWin — the owner's approved fix
  from main `3cfe5e2` is cherry-picked; the rest get the same treatment:
  real compaction + scroll guards, purchase windows first).
- **H3** `verify_english_source.py` exit 0: align the allow-list with the
  approved locations already accepted on main (docs comparison tables,
  migrations seeds, Errors catalog, STEI fixtures).

### Explicitly NOT built (with reason)
- Drop-ship, inter-company PO, supplier scorecard: ERPNext extended
  features outside the procure-to-pay cycle the owner named; no trace in
  his parity matrix acceptance gate.
- Email dispatch of RFQ/PO: no mail/SMTP infrastructure decision exists.
- Withholding/TDS at payment: country-specific; the tax engine's country
  catalog itself says "requires local setup" — **owner decision**.
- RFQ/PO print already exists (basic); not restyled here.

## 3. ERPNext comparison (per stage)

### Material Request
| Aspect | ERPNext | ShouTech before | After this branch |
|---|---|---|---|
| Types | Purchase, Material Transfer, Material Issue, Manufacture, Customer Provided | none | R5: type persisted (Customer Provided excluded — service concept differs) |
| Required-by | schedule date per line | none | R5 header + line default |
| Approval | optional Workflow (multi-level via config) | built-in sequential multi-level, RBAC, no self-approval | kept (exceeds ERPNext core); R1 adds budget check |
| Budget check | via Budget dimension at submit | absent | R1 |
| Cancel/Stop | Cancel + Stop | none | R2 |
| Resubmit | amend after cancel | rejected = terminal | R3 resubmit; P6 amend pattern |
| Conversion | → RFQ, → PO, per-line partial, per_ordered | → PO only, whole doc, one PO | R4 → RFQ added; whole-doc conversion kept (partial conversion flagged as remaining gap) |
| Statuses | Draft/Submitted/Partially Ordered/Ordered/Stopped/Cancelled | workflow states only | derived ordered % shown |

### RFQ / Supplier Quotation
| Aspect | ERPNext | ShouTech before | After |
|---|---|---|---|
| RFQ suppliers | child table, per-supplier status, email dispatch | none | P1 invitations + status (dispatch excluded, see §2) |
| SQ from RFQ | lines pulled from RFQ | 1:1 enforced match | kept |
| Comparison | quotation comparison report | built (base-currency normalised) | kept |
| Award | create PO per supplier from SQ | whole-SQ award, link persisted | P2 split award + terms/reference carried |

### Purchase Order
| Aspect | ERPNext | ShouTech before | After |
|---|---|---|---|
| Currency | currency + conversion rate on header | SQ-path only; OrdWin none | P3 |
| Terms | payment terms template + schedule | none | P3 (terms persisted; schedule = text-level) |
| Incoterm | incoterm + named place | none | P3 |
| UOM factor | stored conversion_factor per line | derived at posting | P4 stored snapshot |
| Per-line dates | schedule date per line | none | P4 |
| per_received / per_billed | stored %, maintained on post/cancel | computed on demand only | P5 stored + maintained in posting txns |
| Status | To Receive and Bill / To Bill / To Receive / Completed / Closed | Draft/Posted/Closed/Cancelled + display derivation | P5 derived status shown |
| Close | Close button, reopen | close, no reason/reopen | kept + reason recorded |
| Cancel/amend | cancel blocked by children; Amend creates linked copy | posted children block; drafts orphaned; no amend | P6 |
| Budget | budget check on submit | none at PO | P7 |

### Purchase Receipt
| Aspect | ERPNext | ShouTech before | After |
|---|---|---|---|
| Batch/serial/expiry | per-line entry at receipt | built (capture modal → 054 tables, FEFO) | kept |
| Rejected qty | rejected qty + rejected warehouse | none | Q2 (via quarantine) |
| QC | inspection required flag diverts | gate exists but **bypassed on chain path** | Q1 wired |
| Over-receipt | tolerance % | hard block at 100% | Q4 tolerance setting |
| Receipt date / DN ref | posting date + supplier delivery note | date forced today, no DN ref | Q3 |
| GL | Dr Stock / Cr GRNI (SRBNI) | Dr 1203 / Cr 2203 | kept |
| Returns | negative receipt vs original | RCR vs original receipt line (081) | kept |

### Purchase Invoice / match
| Aspect | ERPNext | ShouTech before | After |
|---|---|---|---|
| Bill identity | bill_no + bill_date, duplicate warning per supplier | manual path only; chain path cannot set it | I1 both paths + catalogued error |
| Three-way match | PO↔PR↔PI line links; over-billing blocked | qty hard invariant; rate via policy (082) | kept + I3 per-line variance + legacy evaluation |
| Match consequence | submit blocked on violation | payment allocation blocked (fail-closed, audited override) | kept (documented design difference) |
| Tax register | taxes table → GL + reports | manual path full; chain path invisible | I2 |
| Debit note | return PI vs original | PRT vs original (080) | + I4 match unwind, I5 own credit-note ref |
| Attachments | File attachments | built for purchase docs (086) | kept |

### Payment / GRNI
| Aspect | ERPNext | ShouTech before | After |
|---|---|---|---|
| Payment Entry | dedicated doc, references table | CshWin voucher + PayRecWin allocation | kept (allocation is transactional + audited) |
| Advances | advance against PO, consumed by PI | not a concept | Y1 |
| GRNI report | SRBNI account + exception reporting | aging report exists; no exceptions | Y2 exceptions + defect fixes |
| Payable aging | Accounts Payable: as-of, due-date, per-invoice | entry-date buckets vs now | Y3 |

### Landed Cost Voucher
| Aspect | ERPNext | ShouTech before | After |
|---|---|---|---|
| Basis | amount/qty/weight/manual per receipt item | amount/qty/manual, one receipt | L4 multi-receipt + line exclusion |
| Valuation | restates remaining stock rate | built (engine, exact receipt layers) | kept |
| Consumed stock | expense adjustment posted | built (split at post) | L3 split stored → exact reversal |
| GL credit | expense accounts of charges | always AP payable | L1 payee + L2 already-booked mode |
| Cancel | reverses valuation + GL | mirrored voucher, split recomputed | L3 exact |

## 3b. Owner's report reconciliation (scope amendment, 2026-11-24)

The owner supplied his full implementation report
(`~/workspace/user/files/Purchase_cycle.txt`, the ChatGPT-side record of the
stabilization work). Where it differs from §1-§3 above, **it supersedes**.
Adopted wholesale:

- **Definition of done = the report's §12 acceptance criteria** (16
  functional / 6 accounting / 7 technical checkboxes). The final report for
  this branch itemizes every checkbox as proven (with evidence) or an
  explicit gap. Nothing weaker counts as "complete".
- **Work order = the report's §11 outstanding list**, Criticals first:
  (1) landed-cost liability duplication, (2) landed-cost posting/reversal
  verification across **all five allocation methods — Value / Quantity /
  Weight / Volume / Manual** (the audit found only amount/qty/manual wired;
  Weight/Volume are build-or-prove items), (3) valuation + COGS integration
  verification, (4) GRNI exception workbench + period-end GL reconciliation,
  (11) consolidated integration tests on **both** SQLite and PostgreSQL
  paths. High: (5) batch/serial identity across receipts/returns/all
  movements incl. partials, (6) requisition permission enforcement + full
  approval scenarios incl. PostgreSQL concurrency serialization (base
  commit d12eb0ac), (7) attachment audit history + secure storage +
  permissions verification, (8) supplier invoice ↔ receiving reconciliation,
  (9) returns + supplier credit-note accounting verification, (10) document
  numbering uniqueness + concurrency-safety across ALL purchasing
  documents, (12) final comparison against **four** systems.
- **Critical 1 design (supersedes L2's account-only mode):** a landed-cost
  charge may LINK to an existing posted supplier document that already
  carries the liability. A linked charge creates **no** new party liability
  and **no** new AP credit; posting reclasses the cost out of the account
  the linked document originally debited (read from its posted journal).
  A document can be consumed by at most one posted LCV (catalogued error
  otherwise); LCV reversal releases the link and restores accounts exactly;
  the linked document is never modified. Unlinked charges keep the L1
  per-charge-payee liability path.
- **Comparison (§12 of the report):** §3's tables are extended per stage
  to four systems — ERPNext, Odoo, Al-Ameen 10, Phenix v10 — before the
  branch is called done.
- The report's 10-stage cycle (Requisition → Approval → RFQ → Quotation
  Comparison → PO → Goods Receipt → Supplier Invoice → Landed Costs →
  Inventory/Accounting Posting → Returns & Reconciliation) is the harness
  scenario's spine.
- The report states comprehensive purchasing tests were deliberately
  postponed and are NOT confirmed — the consolidated harness on this
  branch is that test pass. Windows/PostgreSQL execution remains owed and
  will be stated as such.

## 4. Build results

### Workstream A1 — Purchase Requisition completion (R1–R6 + permission/approval amendment)

Branch `feat/p2p-a1-requisition`. Migration **092** (SQLite + PostgreSQL).

Pillars checklist — PurchaseRequisitionWin / requisition workflow:
- **UI** ✓ — window gains Required-by date picker, Request Type combo
  (Purchase / MaterialTransfer / MaterialIssue / Manufacture), real
  Department field; new bottom-bar actions Convert to RFQ, Cancel
  Requisition, Close Requisition (all action buttons moved to the bottom
  bar per the standing layout rule); approvals grid gains a Round column
  so prior rounds stay visible; minimum compacted 900×600 → 660×372 with
  a ScrollViewer guard (PosCheckoutWin pattern) — `verify_screenfit.py`
  PASS for this window.
- **DB (both providers)** ✓ — migration 092: `required_by_date`,
  `request_type`, `department` on the document store; workflow status
  CHECK widened with Cancelled/Closed; `round_no` on workflow +
  approvals (uniqueness moves to round+step); `rfq_doc_id`; PRQ→RFQ
  single-link unique index; permissions PUR.REQ.CANCEL (524) /
  PUR.REQ.CLOSE (525). SqlMigrator double-run on scratch SQLite:
  applies once, second run 0.
- **Linking** ✓ — (PRQ, RFQ) added to the permitted conversion pairs in
  `OrderDocumentRepository.CreateLinkedDraftAsync`; the link persists in
  `document_links` exactly once (unique index + reservation discipline
  shared with PRQ→PO); shell context (company/language/theme) inherited
  via BaseWin as before.
- **Posting** ✓ (non-posting by design, budget-guarded) — requisitions
  still post no journal; R1 adds the budget control at submit (below).
- **Persistence** ✓ — required-by / type / department / warehouse all
  round-trip create → update → load (harness + Kern.Tests proof);
  department no longer smuggled into Notes, and migration 092 moves
  trivially parseable legacy `Department: …`-prefixed Notes values into
  the column (verified against the migrated schema).
- **Downstream** ✓ — conversion targets are real RFQ/PO drafts on the
  shared engine; workflow events are append-only across rounds,
  cancels, closes, conversions and budget overrides.

R1 budget-check design choice (documented per the workstream brief):
a requisition carries no account or cost-center coding, so the check
charges its estimated total (grand total × exchange rate) to the
**Account budget dimension of the canonical Purchases account
(GlAccounts.Purchases, "402")** — the account an approved requisition's
spend lands on at invoice posting — via the new
`BudgetGuard.CheckCommitment`, which reuses the posting guard's active
budgets, annual + accumulated-monthly horizons, posted-GL actuals (net
of reversals), and Stop/Warn semantics. Stop throws the same catalogued
ACC-5018/5019, rolls the submission back, and records Blocked
budget-control events after the rollback (the JournalRepository
boundary pattern); Warn lets the submit through and writes a
`BudgetOverride` workflow event plus the guard's Warned budget-control
events. CostCenter/Project budgets cannot be charged by a requisition
(no attribution exists on it) and are skipped, exactly as the posting
guard skips unattributable lines. The check runs at submit (including
resubmission); approval completion re-checks nothing new — approvers
decide on a submission that already passed the gate.

Permission enforcement (owner report §11 item 6): every service
operation re-checks its PUR.REQ.* permission in
`PurchaseRequisitionWorkflowService` (VIEW/EDIT/APPROVE/CONVERT/CANCEL/
CLOSE); denial now throws the catalogued `AppError` AUTH-2005 instead
of an uncatalogued exception. New error codes PUR-1001..1005
(edit-lock, cancel-blocked, close-invalid, state-invalid,
reason-required) are registered in ErrorCodes and in all four Errors
catalogs (parity: 112 codes × 4 languages, 0 missing); Lang keys for
the new window controls exist in all four Lang catalogs (56
PurchaseRequisition keys × 4, 0 missing).

PostgreSQL concurrency discipline for the new writes (the d12eb0ac
pattern — lock the workflow row `FOR UPDATE` inside the transaction
before any read-modify-write; SQLite relies on its single-writer
transaction):
- Submit/resubmit: workflow row read `FOR UPDATE` (PG) before the
  state check and round increment; the budget check runs inside the
  same transaction after the lock, so two concurrent submits of one
  requisition serialize on the row.
- Decide: unchanged from d12eb0ac (`FOR UPDATE OF w`).
- Cancel: workflow row read `FOR UPDATE` (PG); the conversion-link
  check and the guarded status update run under that lock.
- Close: workflow row read `FOR UPDATE` (PG) before the guarded
  `status='Converted'` update.
- MarkConvertedToRfq / Begin / Abort conversion: `FOR UPDATE` (PG),
  same as the existing PO conversion methods; Abort's link check now
  covers RFQ links too.
- Budget events: Warned rows are written inside the submit transaction;
  Blocked rows are append-only inserts in a short follow-up transaction
  after the rollback — no read-modify-write, so no lock is needed.

Proof: Kern.Tests `PurchaseRequisitionCompletionTests`
(13 facts, compile-verified; execution owed on Windows) + a headless
harness executing the same scenarios against a real migrated SQLite
database on Linux — 39/39 checks PASS, including migration
idempotency, budget Stop/Warn, round-2 resubmission, single RFQ link,
cancel/close, header round-trips, AUTH-2005 denials, the 3-approver
chain, mid-chain rejection, self-approval and step-skip rejection.
Speaks to owner-report §12 lines: "Internal requisitions work from
creation through approval" and "Approval permissions and sequential
approvals work correctly".

Left out, deliberately: partial (per-line) requisition conversion and
multiple POs per requisition (whole-document conversion kept, as
flagged in §7); request types other than Purchase persist and display
but do not yet convert to stock entries / work orders (their downstream
documents are other workstreams' scope); PostgreSQL execution of
migration 092 and the new writes is code-reviewed against the SQLite
run and the d12eb0ac discipline but, like all PG verification on this
branch, still owes a live PostgreSQL run on Windows.

### Workstream A2 — GRNI exceptions + payable aging (Y2, Y3) — branch `feat/p2p-a2-grni`

**Y2 — GRNI exception view (`GrniWin`, ReportKey `Purchasing.GRNI`, same
window + same report permission as before; no new nav entry, so the
existing gate applies unchanged):**
- UI ✅ — a View filter (Detail / Exceptions) in the existing RptBase
  window: central styling, bottom action bar, ENTER-horizontal grid,
  RTL inherited. Exceptions mode lists (a) open receipt lines at/past a
  user-set age threshold in days (default 90) with remaining qty, base
  value, age and aging buckets; (b) invoiced-without-receipt invoices
  (invoice no/date/supplier/base amount, age); (c) per-period GL 2203
  tie-out rows (period, cumulative open-receipt base, cumulative GL
  balance, cumulative difference) for every period whose cumulative
  difference is not zero.
- DB ✅ — read-only; queries in `RptQuery` (`InvoicedWithoutReceiptAsync`,
  `GrniTieOutByPeriodAsync`): company-scoped (dual code/Guid key join,
  as in `PurchaseInvoiceListAsync`), parameterized, provider-neutral
  SQL (period = yyyy-MM prefix of the ISO date text on both providers).
- Defect 1 fixed ✅ — the detail query's From filter no longer defaults
  to −1 year: empty From = all open receipts; a set From bounds the
  detail AND the reconciliation identically (new
  `GoodsReceivedNotInvoicedReconciliationAsync(DateTime?, DateTime)`
  overload; the old signature delegates unbounded).
- Defect 2 fixed ✅ — `GlBalanceBase`/`DifferenceBase` are populated on
  every detail row from the reconciliation query (previously totals-only,
  grid columns always empty).
- Period-end workbench ✅ (owner report §5 / §11 item 4 / §12) — detail,
  exceptions and reconciliation are one workbench on ONE boundary: the
  filter is labelled "Period-end (as of)" (`Reports.Filter.PeriodEndAsOf`,
  ×4 languages) and receipts, invoice matches, receipt returns and
  GL 2203 entries are all cut at that same date in every query the
  window runs — an as-of period-end run reproduces that period-end
  exactly, with no period+1 activity on either side.
- Defect 3 found + fixed while proving the period-end scenario ✅ — the
  reconciliation CTE's pro-rata factor `(remaining / qty)` ran BEFORE
  the multiplication; on SQLite, integer-stored quantities make that an
  integer division (3/5 → 0), zeroing every partially-matched line on
  the receipt side of the tie-out (PostgreSQL NUMERIC unaffected). An
  explicit `* 1.0` factor now guards the detail query, the
  reconciliation and the by-period tie-out so all three always agree.
- Posting — N/A (report). Downstream ✅ — export/print via RptBase cover
  both modes; the exception columns are in the column chooser.

**Y3 — Payable aging by due date (`SupAgingWin`, ReportKey
`AP.SupplierAging`, same gate as before):**
- UI ✅ — an "Aging basis" filter: Entry date (supplier summary, the
  historical view) or Due date (one row per open purchase invoice:
  invoice, supplier, invoice date, due date, days overdue, document
  open amount, and the base open amount placed in the same 4 buckets +
  Balance). Both modes take an explicit As-of date; entry-date mode now
  ages `party_ledger` entries against it (entries after as-of excluded)
  via `PartyAgingAsync(bool, DateTime, ct)` — the old signature
  delegates with today, so CustAgingWin is unchanged.
- DB ✅ — migration **094** (SQLite + PostgreSQL):
  `purchase_documents.due_date` + index. Invoices predating it have NULL
  and age by doc-date fallback (`COALESCE(due_date, doc_date)`).
- Persistence ✅ — due date is stamped at invoice creation on BOTH
  paths, inside the insert transaction: chain
  (`OrderDocumentRepository.InsertDocumentAsync` → PUR only; explicit
  `CommercialDocumentDraft.DueDate` wins, else doc date + supplier
  `parties.credit_days`, else doc date) and manual
  (`InvRepo.SaveDraft` → same rule; `InvRepo.Load` reads it back).
  Editable for PUR in InvoiceWin (Due date field, purchase-invoice
  header only; empty = derive from supplier terms at save).
- Posting — N/A (no GL effect; due date is a terms attribute).
  Downstream ✅ — `RptQuery.PayableDueDateAgingAsync` computes
  DaysOverdue + bucket in SQL (provider-aware day difference, base
  amounts), proven headlessly (below).
- Lang ✅ — 21 new keys (`Reports.*` mode/column/filter/exception keys +
  `Invoice.DueDate`) in all 4 catalogs, 0 missing for touched keys;
  catalog parity deltas vs En unchanged from the base branch.

**Proof (headless, this Linux host):**
- Full build `--no-incremental`: 0 errors; warnings = only the 4 known
  base CA1859 in `PurchaseReceiptReturnWin.cs` (left per instructions).
- SqlMigrator double-run on a fresh SQLite DB: 094 applies on run 1,
  SKIP on run 2; `due_date` column + index present.
- The exact SQL of the new/changed queries was extracted from `Rpt.cs`
  and executed against a migrated DB seeded like the Kern.Tests
  fixtures: aged receipt remaining 10 / 200 with the fully-invoiced
  receipt at 0; invoiced-without-receipt returns the manual invoices
  and excludes the chain invoice with 078 matches; due-date aging
  places invoices at 43 days → 31-60, −31 → 0-30, 165 (doc-date
  fallback) → 90+, and excludes the fully paid invoice; reconciliation
  unbounded OpenReceiptBase = 200 vs From-bounded (2026-01-01) = 0.
- Period-end scenario executed headlessly the same way (chain-shaped
  documents + GL 2203 entries across Nov/Dec 2026): as of 2026-11-30
  the detail shows only the November receipt (remaining 3 / 36) and
  OpenReceiptBase = GlBalanceBase = 36, Difference = 0; as of
  2026-12-31 both sides move to 96, difference still 0 — this is the
  §12 evidence for "GRNI exceptions and period-end reconciliation work
  correctly" (functional) and "GRNI balances reconcile with received
  and invoiced goods" (accounting), at query level.
- `tools/audit_window_context.py` exit 0; `tools/verify_english_source.py`
  exit 0.
- Kern.Tests facts added (`tests/Kern.Tests/GrniPayableAgingTests.cs`,
  6 facts): the three query behaviours above + due-date capture on the
  manual path (derived 2026-01-01 + 45d = 2026-02-15, explicit date
  persisted verbatim, Load round-trip) and on the chain path
  (PO → receipt → invoice stamps invoice date + 45d) + the period-end
  scenario through the REAL chain (real GL 2203 postings): as of
  period-1 end GRNI total = GL 2203 balance = 36 with period-2
  excluded from both sides, and the receipt's age crossing the
  90-day exception threshold only at the later as-of date
  (27 days at period-1 end → 104 days on 2027-02-15, still 36 open).
  Tests compile here; execution is owed on Windows (net8.0-windows
  testhost), as with the rest of the suite. PostgreSQL execution of
  the new queries is likewise owed (SQL written provider-neutral;
  only SQLite was exercised headlessly).

### A3 — Landed cost completion (L1–L5 + Critical-1 redesign) — branch `feat/p2p-a3-landed`

Owner report (Purchase_cycle.txt §4.3 / §11 #1–3) superseded the original
L2 account-only design: the final model is two-mode. A charge is either
**Unlinked** (`Payable`: the voucher creates the liability to its payee)
or **Linked** (the liability already exists on a posted supplier
document; posting reclasses the cost out of that document and creates
NO new liability) — never both.

- **L1 Per-charge payee.** Migration 093 adds `payee_party_id`
  (parties.ledger_id) per charge; the window charge grid has a parties
  picker (default = receipt supplier). Posting writes one party-ledger
  credit per effective payee; the GL credit stays on AP control 2201.
  Reversal mirrors to the same payees.
- **L2 Linked mode (Critical-1, primary).** A Linked charge stores
  `linked_document_id` + `linked_account_code`, the latter resolved at
  draft time from the linked document's **posted journal** (exactly one
  debit account outside the clearing/control set {1203, 2201, 2203,
  1205, 1201, 415}; same currency; charge ≤ that debit — otherwise
  PUR-1005). Posting credits that account (reclass), writes **no**
  party-ledger entry and **no** AP credit. **Duplicate prevention:** a
  supplier document can be consumed by at most one posted,
  non-reversal voucher — enforced at draft creation and re-checked at
  posting (PUR-1004); the link picker stops offering consumed
  documents. Reversal copies the link onto the reversal voucher (which
  never counts as consuming), marks the original Cancelled, and thereby
  releases the document for a future voucher; the linked document
  itself is never modified. The interim `AlreadyBooked` mode is no
  longer creatable (superseded by Linked); its PostCore branch is kept
  only so interim-093 drafts remain postable.
- **L3 Exact reversal.** Migration 093 stores `inventory_amount` /
  `consumed_amount` per allocation at post time; reversal allocations
  copy the negated split and `StockValuationEngine` replays it (new
  `storedInventoryAdjustment` parameter) instead of recomputing from
  post-reversal stock — a reversal after further consumption mirrors
  the original journal line-for-line while the layer rate still
  unwinds exactly. `LandedCostConsumptionAllocator` is now wired in as
  the single production split implementation inside the engine
  (previously dead code with tests only).
- **L4 Multi-receipt vouchers.** The window picks from posted receipts
  (new `ListReceiptsAsync`) with a per-line inclusion grid
  (`ListReceiptLinesAsync` — stock-valued lines only, so service /
  non-stock lines can never enter landed cost); the repository takes
  receipt selections, allocations stay keyed per receipt line, and
  migration 093 adds the `purchase_landed_cost_receipts` link table
  (header `receipt_doc_id` remains the primary receipt). Manual
  allocations are keyed by global line ID across receipts.
- **L5 PPV account.** New dedicated account **415 Purchase Price
  Variance**: `GlAccounts.PurchasePriceVariance`, seeded by migration
  093 into every existing company carrying 402 (both providers, under
  402's parent) and into all COA `.shc` templates that carry the
  purchase group (new companies). The invoice-vs-receipt variance lines
  in `OrderDocumentRepository` (the only change there — two
  `GlLineRequest`s) now post to 415 instead of Purchases 402.
- **Window pillars.** UI: receipt picker + line grid + payee /
  credit-mode / linked-document charge columns, all localized in 4
  languages (54 PurchaseLandedCost keys at parity). DB: migration 093
  both providers. Linking: shell context unchanged (company/language
  via BaseWin), link table + linked-document relationship persisted.
  Posting: one transaction for post/reverse as before; RBAC unchanged
  (`PUR.LANDEDCOST.*` — no new actions). Persistence: payee, credit
  mode, linked document/account, and the post-time split are all stored
  columns. Downstream: party ledger per payee, GL reclass visible on
  the linked document's account, PPV separately reportable on 415.
  Screenfit: minimum 850×560 → **660×372** with real compaction + a
  body ScrollViewer guard; action bar moved to the bottom (layout
  rule). `verify_screenfit` for this window: FAIL → **PASS**.
- **Errors.** New catalogued codes PUR-1004 (document already linked)
  and PUR-1005 (linked document invalid), plus PUR-1001..1003, in
  ErrorCodes.cs, all 4 Errors catalogs (112 codes each, parity 0
  missing), and docs/ERROR_CATALOG.md.
- **Proof (Kern.Tests, SQLite; compile-verified here, execution owed
  on Windows).** New/updated facts in OrderChainStep9Tests:
  per-charge payee credits the payee's ledger, not the supplier's
  (incl. reversal mirror); linked charge reclasses Dr Expense 404 →
  inventory/COGS with carrier balance unchanged, no AP line, no party
  rows; double consumption rejected at draft (PUR-1004) and at post;
  linked reversal restores the expense account + receipt valuation
  exactly, leaves the linked document untouched, and frees the link
  (re-link posts again); reversal after ADDITIONAL consumption
  replays the stored split (journal mirrors the original exactly);
  multi-receipt voucher allocates across two receipts with one line
  excluded (and all-excluded receipt → PUR-1003); weight-basis E2E
  allocates from material-card `weight_net` snapshots (all five bases
  also covered by LandedCostAllocatorTests); PPV lands on 415 (four
  pre-existing variance assertions moved 402 → 415). §12 evidence:
  "all five supported methods" ✓ tests; "landed costs correctly
  update inventory valuation" ✓; "duplicate supplier liabilities are
  prevented" ✓; "no duplicate liabilities or valuation adjustments"
  ✓; "reversals preserve original transactions and audit history" ✓
  (mirrored voucher + events, linked document never modified).

### Workstream B2 — Supplier advances (Y1) — branch `feat/p2p-b2-advances`

Migration **095** (SQLite + PostgreSQL). A payment voucher can now be
recorded as an advance against one purchase order and consumed by that
order's invoices, with the prepayment carried in its own asset account
instead of inside Accounts Payable.

Pillars checklist — supplier advances:
- **UI** ✓ — CshWin (Payment mode only) gains an "Advance for PO"
  checkbox + Purchase Order No. field in the header form (bottom action
  bar unchanged; an empty settlement-line account defaults to 1215);
  PayRecWin's payments grid gains the Advance PO column and a "Reverse
  advance allocation" bottom-bar action; OrdWin's PO status line shows
  advance total / consumed / remaining. All keys in 4 languages.
- **DB (both providers)** ✓ — migration 095: `po_doc_id` + `is_advance`
  on `voucher_party_links`; `source_kind` ('PAYMENT' default /
  'ADVANCE'), `advance_po_doc_id`, `clearing_journal_id` on
  `payment_allocations`; new COA account **1215 Advances to
  Suppliers** seeded into every existing company carrying the purchase
  group (402), under the same parent as Inventory 1203 (1210–1214 are
  manufacturing inventory codes in the industrial templates, so 1215
  is the first code free in every template), and added to all 15 COA
  `.shc` templates that carry 402.
- **Linking** ✓ — the advance link follows the existing
  `voucher_party_links` pattern (resolved party + amount snapshot at
  draft save, now + PO); shell context inherited via BaseWin as
  before; the invoice's PO is resolved through the existing
  `document_links` chain (PO → invoice, or PO → receipt → invoice).
- **Posting** ✓ — posting an advance voucher enforces, inside the
  journal transaction, a Posted PO of the same supplier/company
  (PUR-1012) and debits **1215 for exactly the linked amount** (the
  2201 control enforcement learns the advance exception); the party
  ledger writes an `ADV` settlement entry (supplier debit) so the
  supplier statement shows the prepayment. Allocation of the advance
  in PayRecWin posts, in the allocation transaction, **Dr AP 2201 /
  Cr Advances 1215** (GlPosting, source `ADV_ALLOC`, entry id stored
  on the allocation row) and increments the invoice `paid_amount`
  exactly like a normal allocation. RBAC unchanged (existing voucher
  + `ACC.PAYREC.ALLOCATE` permissions; no new action).
- **Persistence** ✓ — advance flag/PO, allocation source/PO/clearing
  journal are stored columns; OrdWin reads totals back from them.
- **Downstream** ✓ — supplier statement/party balance net the ADV
  entry against the payable; advances appear in PayRecWin as their
  own allocatable source (never as a normal payment: consumed counts
  only `source_kind='ADVANCE'` allocations and vice versa).
- **Reversal symmetry** ✓ — reversing an advance allocation
  (PayRecWin action / `ReconciliationRepository.UnallocateAsync`)
  mirrors the clearing entry, releases `paid_amount`, writes the
  migration-079 audit row and deletes the allocation, restoring the
  advance balance exactly. Reversing the advance voucher itself while
  any of it is consumed is blocked with **PUR-1013** (thrown inside
  the shared reversal core, so the whole reversal rolls back); after
  unallocation the voucher reversal mirrors the ADV settlement like
  any other voucher. Cross-PO consumption is blocked with
  **PUR-1012** (advance for PO-A cannot pay PO-B's invoice, nor a
  manual invoice with no PO). PUR-1011 is reserved for a parallel
  workstream; the advance codes are 1012/1013.

Proof: Kern.Tests `PartyUnificationTests` gains 5 advance facts
(advance posts Dr 1215 / never AP + supplier balance nets −600;
advance voucher debiting 2201 rejected; cross-PO allocation throws
PUR-1012 and writes nothing; own-PO allocation clears 250, sets
paid_amount, advance remainder 150; allocation reversal restores the
advance, voucher reversal blocked with PUR-1013 while consumed and
allowed after unallocation with the party ledger netting to zero) —
compile-verified here, execution owed on Windows. The same scenarios
were also executed headlessly on this Linux host through the real
`JournalRepository`/`ReconciliationRepository` against a
SqlMigrator-built SQLite database: **22/22 checks PASS**, including
the PUR-1012/PUR-1013 paths. Gates: build `--no-incremental`
0 errors / 0 warnings; SqlMigrator double-run idempotent (095 applies
once; seeded-company run produces 1215 with 1203's parent, second run
applies 0); `audit_window_context.py`, `verify_english_source.py`,
`verify_screenfit.py` all exit 0; Lang parity for touched keys
0 missing ×4 (catalog deltas vs En unchanged from base: Ar 12,
Fr/Tr 19 pre-existing); Errors parity 119 codes ×4, 0 missing.
PostgreSQL execution of migration 095 and the new queries is
code-reviewed (provider-neutral SQL, BOOLEAN/TRUE forms) but, like
all PG verification on this branch, still owes a live PostgreSQL run.
### B1 — Shared document core: PO/RFQ/SQ, receipt, invoice/match (P1–P7, Q1–Q4, I1–I5) — branch `feat/p2p-b1-core`

Migrations **096** (core columns), **097** (RFQ invitations + P5
backfill), **098** (PRT match-unwind audit) — both providers each.
New catalogued error **PUR-1011** (duplicate supplier bill reference)
in ErrorCodes.cs, all 4 Errors catalogs (parity 0 missing), and
docs/ERROR_CATALOG.md. No new code files: everything extends
OrderDocumentRepository / OrderService / OrdWin / InvRepo /
PurchaseMatchPolicyRepository in place.

- **Q1 QC gate on chain receipts (blocker — closed).** The purchase
  branch of `PostConversionStockAsync` now runs the migration-056
  gate exactly as StkPost does, through the shared `QualityGate`
  service: a QC-required line's movement lands in the quarantine
  warehouse and creates the Draft inspection (destination = the
  receipt's target warehouse); the receipt GL/stock value is booked
  into quarantine at receipt value, and the existing QC release path
  (`QualityGate.CompleteInTransaction`) moves accepted qty to the
  target warehouse at the same value; rejected qty stays in
  quarantine, returnable via RCR. Service / non-stock lines never
  reach the gate (existing stockable guard).
- **Q2 Rejected qty at receipt.** Per-line rejected qty + reason in
  the OrdWin receipt dialog, persisted on the receipt line
  (`rejected_qty` / `reject_reason`, mig 096; reason required when
  qty > 0). **Model decision:** rejected-at-the-gate goods are
  recorded but NOT received — no stock, no GL, no PO progress —
  because they never enter the books; goods rejected later by QC
  inspection are the ones that sit in quarantine under Q1.
- **Q3 Receipt date + delivery note.** The receipt dialog date is
  editable (default today) and posts on the picked date; the
  supplier delivery-note number is persisted on the receipt header
  (`delivery_note_no`, mig 096).
- **Q4 Over-receipt tolerance.** `receipt_over_tolerance_pct` on
  `purchase_match_policies` (mig 096), editable in
  PurchaseMatchPolicyWin; the receipt hard block applies beyond
  ordered × (1 + tolerance) at material, per-source-line and
  allocation level. Default 0 = previous behaviour.
- **P5 Stored percentages + derived status.** `per_received` /
  `per_billed` stored on the document header (mig 096) and
  maintained inside the receipt, receipt-return, chain-invoice and
  PRT posting transactions (PRT via InvRepo's refresh); backfilled
  for existing POs by mig 097. Derived status (To Receive and Bill /
  To Bill / To Receive / Completed) computed from the stored values,
  shown in the OrdWin header progress line and as a column in
  OrderTrackWin.
- **P3 PO header completeness.** `payment_terms` + `incoterm`
  persisted (mig 096); currency + exchange rate editable in OrdWin
  for PO/RFQ (no longer forced to user-pref/0; empty rate keeps the
  master-rate fallback). SQ award carries the SQ's currency, rate,
  payment terms, incoterm and supplier reference onto the PO.
- **P4 Stored UOM factor + per-line required-by.** `uom_factor`
  snapshotted from the material card (`UnitEquivalents`) at line
  entry, persisted per line (mig 096) and carried onto derived
  receipt/invoice lines; `line_required_by` per line, defaulting to
  the header date.
- **P1 RFQ invitations.** `rfq_invitations` (mig 097): OrdWin RFQ
  mode gains an invitations dialog (invite by supplier code, status
  grid, decline action); creating an SQ against the RFQ flips that
  supplier's invitation to Quoted with `responded_at`.
- **P2 Split award.** The comparison window's lines grid is
  multi-select: awarding with a proper subset selected creates the
  PO from those lines only (per-line links preserved); no selection
  keeps the whole-quotation award.
- **P6 Dependency-aware cancel + amend.** Cancelling a document
  cancels its DRAFT downstream documents in the same transaction
  (posted downstream already blocked). Amend on a cancelled PO
  creates a new draft PO copying header + lines, linked via
  `amended_from_doc_id` (mig 096) + a document_links row; OrdWin
  Amend button (cancelled POs, `PUR.ORDER.CREATE`).
- **P7 Budget check at PO posting.** Posting a draft PO runs
  `BudgetGuard.CheckCommitment` on the PO total (base amount) with
  the same Stop/Warn semantics as requisition submit; blocked events
  are recorded after rollback, mirroring the journal pattern.
- **I1 Chain bill identity.** The OrdWin invoice dialog captures the
  supplier bill number + bill date; persisted to `reference_no` +
  new `bill_date` (mig 096). Uniqueness per (company, supplier,
  normalized ref) enforced on the chain path with **PUR-1011**; the
  manual path's plain `InvalidOperationException` in
  `InvRepo.EnsureUniqueSupplierInvoiceReference` now throws the same
  catalogued error (existing test updated to expect it).
- **I2 Chain tax registration.** Chain purchase invoices write
  `tax_transactions` INPUT rows through the tax engine, mirroring
  `InvRepo.WriteTaxTransactions`: per-line tax code (line → PO line
  → active company rule matching the line rate), recoverability from
  the rule, non-recoverable tax capitalized into the GRNI clearing
  amount; GL stays balanced (Dr GRNI + Dr/Cr PPV + Dr Input VAT
  (recoverable) = Cr AP grand total). Taxed lines without company
  tax setup fail loudly, as on the manual path.
- **I3 Match completeness.** (a) On-demand evaluation:
  `PurchaseMatchPolicyRepository.ReevaluateLatestAsync` builds and
  persists a first evaluation for a posted purchase invoice that has
  none (previously it threw), unblocking allocation review from the
  policy window. On-demand snapshots carry tax variance 0 — legacy
  chain invoices hold no PO tax expectation to compare against.
  (b) Per-line variance (qty / rate / amount) is stored in every new
  evaluation snapshot (`lineVariance`) and viewable in the policy
  window's history detail.
- **I4 PRT unwinds match.** PRT posting (InvRepo, same transaction
  and lock discipline as the allocation validation) decrements the
  mig-078 receipt↔invoice matched quantities FIFO per invoice line
  and refreshes the PO's stored per_billed, recording every unwind
  in `purchase_return_match_unwinds` (mig 098) so returned goods can
  be re-invoiced. `InvRepo.RewindPurchaseReturnMatches` replays the
  stored unwinds exactly. **Leftover:** the base has no PRT-reversal
  UI/entry point (ReversePostedInvoice covers SAL/PUR originals
  only), so the re-wind is a service operation awaiting that entry
  point; the unwind itself is live on every PRT post.
- **I5 PRT own reference.** PurchaseReturnWin no longer copies the
  original invoice's reference; a new optional "Supplier Credit
  Note Ref." field is stored on the PRT header
  (`supplier_credit_note_ref`, mig 096; 4-language key).
- **Proof (Kern.Tests, SQLite; compile-verified here, execution
  owed on Windows).** New B1 facts in OrderChainStep9Tests:
  QC-required receipt diverts to quarantine (sellable stock
  unchanged, Draft inspection created, delivery note persisted) and
  the QC release moves accepted qty at the same value leaving the
  rejected unit in quarantine; over-receipt blocked at default 0,
  allowed at ordered × 1.10 with a 10% policy, blocked beyond;
  stored percentages + derived-status transitions across partial
  receipt/invoice to Completed; cancel cascades a draft child and is
  blocked by a posted receipt; amend copies lines and links
  `amended_from`; duplicate chain bill number rejected with
  PUR-1011 (normalized, cross-PO); chain invoice writes the INPUT
  tax_transactions row and the recoverable VAT GL split; RFQ
  invitation flips Invited → Quoted on SQ creation and a split award
  produces a one-line PO carrying SQ currency + terms; PRT unwinds
  the 078 match + stored per_billed, stores its own credit-note
  reference, rewind restores the match exactly, and re-invoicing
  the returned qty succeeds. InvPostCorrectnessTests' duplicate
  reference fact now expects AppError PUR-1011.

### Workstream W1 — Withholding tax / TDS — branch `feat/p2p-w1-withholding`

Migration **099** (SQLite + PostgreSQL). Built on the existing tax
engine (migrations 067–091) — no parallel tax system, no country
hard-coding, no jurisdiction rates seeded anywhere.

**Configuration.** A tax rule (`company_tax_rules`) can be marked
withholding (`is_withholding`): rate, withholding PAYABLE control
account (`withholding_account_code`), optional threshold amount +
mode (`withholding_threshold_mode` = `PerDocument` | `Cumulative`).
Configured in TaxSetupWin (existing window, extended: withholding
checkbox, payable account, threshold, mode — under the existing
`ACC.TAX.MANAGE` permission and the existing TaxRuleSaved audit
event). Supplier-level default: `parties.withholding_tax_code`.
Determination at invoice posting, in order: (1) invoice-level
selection (`purchase_documents.withholding_tax_code`, set on the
manual path's draft header), (2) supplier default, (3) the company's
single default withholding rule. Withholding rules are excluded from
the ordinary VAT determination (`TaxSvc.LoadCodes` /
`TryLoadActiveRate` filter them out).

**Base definition.** The withholding base EXCLUDES VAT — manual
path: grand total − tax total; chain path: lines subtotal — which is
the engine's existing VAT-exclusive base mechanics. Thresholds:
`PerDocument` — an invoice whose base is below the threshold is not
withheld (the evaluation is still recorded, amount 0, so the register
shows it was evaluated); `Cumulative` — the supplier's calendar-year
withholding bases (stored register rows) accumulate and only the
excess above the threshold is withheld on the crossing invoice.

**GL shape of a withheld invoice** (base 10,000, VAT 15%, WHT 5%):
Dr Purchases/GRNI 10,000 (+ Dr non-recoverable VAT where applicable),
Dr Input VAT recoverable 1,500, **Cr AP 2201 GROSS 11,500**,
**Dr AP 2201 500** (the deduction leg), **Cr withholding payable
(rule account; seeded default 2215) 500**. The entry balances; the
supplier's party-ledger credit is **NET 11,000** — the amount actually
payable — so statements, open amounts and allocation all see net.
Settlement at posting (paid-at-posting) is likewise capped at net.
Withholding above the gross payable is rejected (PUR-1015).

**Register.** One row per evaluated invoice in `tax_transactions`:
`tax_type='WITHHOLDING'`, `tax_treatment='Withholding'`,
`tax_direction='INPUT'`, `is_recoverable=0`, `line_no=0` (header
level, additive extension of the register vocabulary), base/rate/
amount stored. The computed outcome also persists on
`purchase_documents` (`withholding_tax_code/rate/base/amount/
account_code`) for audit and cumulative evaluation.

**Allocation / advances.** `ReconciliationRepository` open amount =
total − withholding − paid (both the open-documents list and
`AllocateAsync`); allocating the net fully settles; allocating above
net is rejected by the existing open-amount check and writes nothing.
Advances interplay unchanged and proven: an advance (Dr 1215) consumed
against a withheld invoice clears Dr 2201 / Cr 1215 against the NET
open amount only — the withheld portion is never allocatable.

**Reversal.** Reversing a withheld invoice mirrors the deduction:
the journal reversal debits the withholding payable and re-credits
AP (the manual path's return/reversal flow posts the mirrored GL and
a NET party debit), and a NEGATIVE withholding register row is
written under the reversing document — original rows stay immutable.

**Reporting / certificates.** TaxReportWin gains a Withholding tab
(existing window extended; `TaxReportingSvc.LoadWithholding`,
permission `ACC.TAX.REPORT.VIEW` unchanged): per supplier per period —
taxable base, rate, amount withheld, invoice references (register
rows joined to `purchase_documents`), and a certificate reference
derived from the register rows (`WHT-<period>-<supplier>-<tax code>`).
Data comes from `tax_transactions` only.

**Errors.** PUR-1014 WithholdingConfigurationInvalid, PUR-1015
WithholdingExceedsPayable, PUR-1016 WithholdingRuleNotFound — in
ErrorCodes.cs, all 4 Errors catalogs (123 codes ×4, real
translations), docs/ERROR_CATALOG.md. No new permissions were needed:
configuration uses `ACC.TAX.MANAGE`, reporting `ACC.TAX.REPORT.VIEW`
(the existing TAX patterns).

**Seeded account.** Migration 099 seeds **2215 Withholding Tax
Payable** (LIABILITY, under AP's parent) into every company already
carrying 2201, and the account is appended to the 15 `.shc` COA
templates that carry 2201 — justified because the mechanism needs a
sensible default payable target while remaining fully configurable
per rule. Representation note: `chart_of_accounts` carries only
`account_name` + `name_ar` columns and the templates only ar/en
(+fr in simple-french), so the template entry carries the names in
the languages the template format supports; no 4-language name store
exists in the COA to extend.

Pillars checklist:
- **UI** ✓ — TaxSetupWin (rule configuration) + TaxReportWin
  (withholding tab) extended in place; no new window; size caps
  untouched (both already ≤ cap; screenfit ALL PASS).
- **DB (both providers)** ✓ — migration 099 SQLite + PostgreSQL,
  idempotent (`@if-column-missing` guards, seed guarded); SqlMigrator
  double-run: APPLY+OK once, second run SKIP/0.
- **Linking** ✓ — determination chains invoice → supplier default →
  company default rule; register rows link to the source document;
  report joins register → documents → parties.
- **Posting** ✓ — both invoice paths (chain
  `OrderDocumentRepository.CreatePurchaseInvoiceAsync`, manual
  `InvRepo.Post`) withhold identically through the shared
  `WithholdingTax` evaluator (`src/Infra/Withholding.cs` — new file,
  justified: the evaluator must serve both the infra-layer chain
  repository and the app-layer InvRepo, and TaxSvc is app-layer);
  reversal mirrors on the manual path.
- **Persistence** ✓ — rule config, supplier default, per-invoice
  outcome and register rows all stored; originals immutable.
- **Downstream report** ✓ — withholding tab totals tie to the
  register sum and to the GL 2215 movement (proven in harness (f)).

**Invoice-time, not payment-time.** ERPNext's TDS model withholds on
the invoice (the liability to the authority arises when the expense
is booked), which is what is built. Payment-time TDS (withhold when
the payment voucher posts, common where the law keys on payment) is
the deliberate alternative NOT built: it would need the allocation
layer to split every payment and re-open withheld invoices; flagged
for the owner if a jurisdiction requires it.

**Proof.** Executed headless harness `tools/W1Harness` (real
assemblies, SQLite): **36 PASS / 0 FAIL** — (a) manual invoice GL
shape above + net party balance + register rows; (b) net allocation
settles, above-net rejected writing nothing; (c) reversal mirrors GL
+ register, frees the allocation, originals intact; (d)
below-threshold evaluated with amount 0; (e) cumulative crossing
withholds the excess only (6,000 → 0, next 6,000 → 100); (f) register
totals == GL 2215 movement for both companies; (g) chain-path invoice
withholds identically via the supplier default, and an advance
consumed against it nets correctly. Kern.Tests mirror:
`tests/Kern.Tests/WithholdingTaxTests.cs` (8 facts) — compile-verified,
execution owed on Windows. PostgreSQL live run owed.

## 5. Gates

(Filled at completion.)

### B1 gates (this workstream)

- Build `--no-incremental`: 0 errors, 0 warnings.
- Migrator (SQLite): fresh run applies through 098; second run
  applies 0 (096–098 SKIP); new columns/tables verified present
  exactly once. PostgreSQL execution owed (no PG server here).
- `audit_window_context.py` exit 0; `verify_screenfit.py` exit 0
  (ALL PASS); `verify_english_source.py` exit 0.
- Lang parity for touched keys 0 missing (19 keys ×4); Errors
  parity 0 missing (PUR-1011 in all 4 catalogs; full code sets
  identical across languages).
- Kern.Tests execution owed on Windows (net8.0-windows testhost).

### A3 gates (this workstream)

- Build `--no-incremental`: 0 errors; warnings = only the 4 known
  CA1859 in PurchaseReceiptReturnWin.cs (not this workstream).
- Migrator (SQLite): fresh run through 092 → seed company + 402 →
  093 applies once (PPV 415 seeded) → second run applies 0.
  PostgreSQL execution owed (no PG server in this environment).
- `audit_window_context.py` exit 0; `verify_english_source.py` exit 0;
  Lang parity for touched keys 0 missing (54/54 ×4); Errors parity
  0 missing (112 ×4). `verify_screenfit.py` still exits 1 overall on
  11 pre-existing base windows owned by other workstreams (H2);
  PurchaseLandedCostWin itself passes.
- Kern.Tests execution owed on Windows (net8.0-windows testhost).

### W1 gates (this workstream)

- Build `--no-incremental`: 0 errors, 0 warnings (solution + Kern.Tests project).
- Migrator (SQLite): fresh run APPLY+OK 099; second run SKIP (0 new);
  099 columns verified on company_tax_rules / parties /
  purchase_documents. PostgreSQL execution owed (no PG server here).
- `audit_window_context.py` exit 0; `verify_screenfit.py` exit 0
  (ALL PASS); `verify_english_source.py` exit 0.
- Errors parity: 123 codes ×4 languages, 0 missing, no duplicate
  keys, valid JSON. Lang parity for touched keys: 0 missing ×4
  (pre-existing base deltas unchanged: Ar 12 / Fr 19 / Tr 19).
- Executed harness tools/W1Harness: 36 PASS / 0 FAIL.
- Kern.Tests execution owed on Windows (net8.0-windows testhost).

## 6. Proof

(Filled at completion — Kern.Tests facts + headless harness scenario.)

## 7. Owner flags

- Withholding/TDS: **DECIDED-BUILD (owner decision 2026-11-24) and
  DELIVERED** by workstream W1 (see §4 W1, migration 099): generic
  configurable mechanism on the existing tax engine — withholding tax
  rules (rate, payable account, PerDocument/Cumulative threshold),
  supplier defaults, invoice-time deduction on BOTH posting paths (AP
  gross, withholding payable credited, supplier balance net), net
  allocation, reversal mirror, per-supplier per-period withholding
  report + certificate reference from the tax register. **Remaining
  open item — the country-pack question:** jurisdiction-specific
  CONTENT (which rates/thresholds/accounts a given country's pack
  should ship) is still the owner's call; nothing country-specific is
  seeded. Payment-time TDS (withhold at payment instead of invoice)
  was deliberately not built — invoice-time is the ERPNext model;
  flag if a jurisdiction requires payment-time.
- Partial (per-line) requisition conversion and multiple POs per
  requisition: not built; whole-document conversion kept.
- Rebase note: base is the owner's stabilization tip @ 52a762a; he is
  still committing there — rebase before merge.
