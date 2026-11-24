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

(Filled per workstream as the build lands — pillars checklist per window:
UI / DB / linking / posting / persistence / downstream.)

## 5. Gates

(Filled at completion.)

## 6. Proof

(Filled at completion — Kern.Tests facts + headless harness scenario.)

## 7. Owner flags

- Withholding/TDS: not built (country decision).
- Partial (per-line) requisition conversion and multiple POs per
  requisition: not built; whole-document conversion kept.
- Rebase note: base is the owner's stabilization tip @ 52a762a; he is
  still committing there — rebase before merge.
