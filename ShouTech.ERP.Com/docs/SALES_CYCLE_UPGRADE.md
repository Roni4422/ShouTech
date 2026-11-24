# Sales Cycle Upgrade — ERPNext Parity (lead-to-cash)

Branch: `feat/sales-cycle-erpnext`, based on `work/full-app-stabilization-2026-11-18`
tip `52a762adc98a3a2cd003c95eb13475b4c01c432e`.
Status: **audit complete; build in progress on this branch.** This document is the
stage-by-stage gap list (with code evidence), the ERPNext comparison table, the
build plan, and the completion record. A stage is not done until its comparison
row and its pillars checklist (UI / DB / linking / posting / persistence /
downstream) are proven.

Benchmark chain (ERPNext Selling + CRM):
Lead → Opportunity → Quotation → Sales Order → Delivery Note → Sales Invoice →
Payment Entry, with returns/credit notes, partial delivery and partial billing,
`per_delivered` / `per_billed` tracking, and GL/stock/COGS/tax reconciliation at
every posting. The parity matrix (`ERP-PROCESS-PARITY-MATRIX.md`) rates this
cycle **Foundation found**; its acceptance gate is: *convert lead → quote →
order → partial delivery → invoice → partial receipt; preserve source links;
reconcile AR, revenue, tax, COGS and stock; support return/refund/reversal.*

## 1. What already exists and is sound (no rebuild)

- Single canonical document store: quotations/orders/deliveries/invoices are
  `sales_documents` rows (`QUO/SO/DLV/SAL`); a CRM quotation *is* a `QUO`
  document edited in InvoiceWin (decision C3, migration 057). No second quote
  module.
- Chain conversions QUO→SO, SO→DLV, DLV→SAL copy lines with price/discount/tax
  preserved and record `document_links` atomically; source must be Posted
  (`SalesChainService.cs:40-138`, `CommercialDocumentRepository.cs:193-275`).
- Delivery posting enforces the sales-order ceiling transactionally for linked
  deliveries (`CommercialDocumentRepository.cs:769-895`, PG `FOR UPDATE`),
  issues stock through `StockValuationEngine` at the material's cost method,
  applies unit equivalence and the outgoing QC gate, and exempts
  service/non-stock materials (`PostSalesAsync:458-548`).
- Invoice GL is balanced and stored-total based: Dr Receivable / Cr Revenue /
  Cr Output VAT, plus Dr COGS / Cr Inventory from delivery movement cost
  (`CommercialDocumentRepository.cs:545-605`, `GlPosting.cs:15-28`); the native
  path posts `InvDoc.PostingTotal` (stored grand total wins, `InvDoc.cs:264`).
- Payments: cash-receipt vouchers write the party-ledger credit **in the same
  transaction as the GL** (`JournalRepository.cs:~635-680`); allocation is a
  separate capped, locked step (`ReconciliationRepository.AllocateAsync`) with
  audited reversal (`payment_allocation_reversal_audit`).
- Returns (native path) are the strongest area: created from the original
  invoice with link, cumulative per-line enforcement in the posting
  transaction, stock back at the **original** issue rate
  (`StkPost.cs:175-186`), AR + revenue + VAT reversal
  (`InvRepo.cs:2668-2705`), and full reversal is a compensating document that
  releases allocations and mirrors settlement — never a delete
  (`InvRepo.cs:1739-2178`). Posted documents cannot be deleted anywhere.
- CRM stage engine: per-company pipeline stages, mandatory lost reasons
  (CRM-9006), full stage-history audit in the same transaction
  (`CrmRepository.cs:479-545`); lead conversion is one transaction into the
  unified `parties` master (`CrmRepository.cs:273-386`); CRM RBAC is seeded
  and checked per action in every CRM window.
- Quotation posting is effect-free (no GL/ledger/stock) — proven by tests.

## 2. ERPNext comparison table (per stage)

| Stage | ERPNext | ShouTech before upgrade | Verdict |
|---|---|---|---|
| Lead | Own doctype; duplicate detection vs leads/contacts; owner, territory, industry; converts to Customer/Contact/Opportunity independently or linked to an existing customer | Own entity `crm_leads` with status CHECK + conversion guards (CRM-9001/9002); conversion is fused: always creates a **new** party + opportunity; **no duplicate detection**; thin fields (no website/industry/territory) | PARTIAL → G1, G2 |
| Opportunity | Items table flows into the quotation; status from stage; converts to Quotation with lines | Stage-derived status, history audit — solid; **no items table** (`057_crm.sql` has none), so the quotation conversion fabricates one line (title × expected amount) and the UI forces full re-entry in InvoiceWin (`CrmRepository.cs:650-652`, `CrmOppWin.cs:437`) | GAP → G3 |
| Quotation | Valid-until date settable; Open/Expired/Lost/Cancelled statuses; price list + pricing rules apply; converts to SO with all header context | `valid_till` column exists but **no sales UI can set it** (InvoiceWin has zero `ValidTill` references) so front-end quotes never expire; **no Expired status**; CRM-created quotes force tax=0, discount=0, exchange_rate=1, doc number by `COUNT(*)+1`; **no price-list resolution** in any quote path | GAP → G4, G5, G6, G7 |
| Quotation → SO | Lines + header (delivery date, terms, rep) carry over; duplicate conversion guarded | Lines/price/discount/tax copied, link recorded; **header fields dropped** (`PartyCode, DeliveryDate, ValidTill, CostCenter, Branch, SalesRep, Warehouse` — `SalesChainService.cs:125-138`); no explicit duplicate guard | PARTIAL → G8 |
| Sales Order | Stored `per_delivered`/`per_billed` (amount-weighted); derived status To Deliver / To Bill / Completed; per-line delivered/billed qty; per-line delivery dates | Percentages **computed on read only**, quantity-weighted, and **by material, not by source line** (sales branch of `GetProgressAsync`); no stored columns; no derived status outside one report label; no per-line dates; closing is manual and unconditional | GAP → G9, G10 |
| Delivery Note | Created per order line with remaining qty; posts COGS at delivery (Dr COGS via Stock Delivered But Not Billed / Cr Inventory under perpetual inventory); batch/serial selectable; cancellable with full reversal | Per-line remaining pre-fill exists in the window, but the creation API is **keyed by material** — duplicate-material lines collapse (`SalesDeliveryWin.cs:331-333` GroupBy); chain lines never carry `SourceLineNo`; **no GL at delivery** — inventory GL and stock ledger diverge for delivered-unbilled orders; serials auto-picked (FEFO), no selection UI; **a posted DLV cannot be reversed at all**, and a draft DLV cannot be cancelled from the UI | GAP → G11, G12, G13 |
| Sales Invoice (from DLV) | Partial invoicing of a delivery across multiple invoices; COGS recognised proportionally | **Partial invoicing is blocked outright**: a DLV must be billed completely in one invoice or posting throws (`GetDeliveryCogsForInvoiceAsync:1002-1022`); drafts can be created but cannot post | GAP → G14 |
| Sales Invoice (direct) | SO→SI with remaining unbilled qty, stock issued at invoice if no DN | Exists (`ConvertToInvoiceAsync`) — posts revenue with no COGS issue path differences vs native invoices; header discount on chain drafts is **hardcoded to 0** at creation (`CommercialDocumentRepository.cs:317-335`) | PARTIAL → G15 |
| Payment Entry | One document: pay against invoices, advances, partial, overpayment → outstanding updated | Functionally complete but two-step: RCV voucher (GL+ledger, one txn) then separate allocation; advances net AR directly (**matches ERPNext's party-account advance treatment** — accepted); overpayment stays as unallocated credit, no warning workflow | PARTIAL (accepted, see §4) |
| Outstanding | Stored `outstanding_amount` on the invoice, reduced by payments **and returns** | Derived `grand_total − paid_amount`; **a partial return does not reduce the original invoice's open amount** — document position and party-ledger position diverge, over-allocation possible (`InvRepo` SRT path; only full reversal and POS returns adjust it) | DEFECT → G16 |
| Credit note / return | Allocatable against other invoices; refund or credit balance | Credit is **not allocatable** — `ReconciliationRepository` only allocates `RCV/PAY/INS`; ordinary returns are credit-only: `SalesReturnWin` hardcodes `PaidAmount=0, PaymentMethod=Credit` — **no cash refund** except POS returns and full reversals | GAP → G17, G18 |
| Cancellation | Cancel reverses stock + GL + progress, dependency-aware | Posted chain DLV/SAL: **no reversal path exists** (the guard message tells the user to "create a return or reverse" — neither exists for chain documents); native invoice reversal is exemplary; status changes write **no actor/timestamp** (`OrderDocumentRepository.cs:485-492`) | GAP → G13, G19 |
| RBAC / errors | Permissions enforced; errors coded | CRM windows check RBAC; **`InvoiceWin` and `SalesReturnWin` perform no permission checks at all** (keys exist: `SALES.INVOICE.*`, `SALES.RETURN.*`); `DocSearchWin` SetLost lacks the check its Convert sibling has; the whole sales-chain backend (`OrderService`, `SalesChainService`, both repositories) throws **plain uncatalogued exceptions** — zero `ErrorCodes.` references | GAP → G20, G21 |
| Reports | Funnel/win-rate, lost quotations, ordered-not-delivered, delivered-not-invoiced, return analysis | **No CRM report windows at all** (no CRM entries in `RptNav.cs`); chain exception reports (ordered-not-delivered, delivered-not-invoiced) absent (`SALES_TO_CASH_CYCLE.md §5`) | GAP → G22 |
| Persistence | Every field saves | `OrdWin.BuildDraft` hardcodes `CostCenter/Branch/SalesRep = null` (no inputs exist); chain `paid_amount` never written at posting; voucher party link is written **outside** the journal draft transaction (`PartyLedgerSvc.cs:60-84`) — a crash leaves a silently unlinked draft | GAP → G23 |

## 3. Gap register and build plan

Build workstreams (parallel sub-branches, merged back into this branch):

- **B1 — CRM front-end** (G1, G2, G3): lead duplicate detection
  (email/phone vs leads + parties, catalog CRM-9007) and a link-existing-party
  conversion path; `crm_opportunity_items` (migration, both DBs) with entity,
  repository, service validation (CRM-9008) and an items grid in CrmOppWin;
  opportunity→quotation builds real lines from items and stops fabricating the
  title line; CRM quotation creation uses the document-number sequence (not
  `COUNT(*)+1`), the FX resolver for the opportunity currency, and accepts a
  valid-till date.
- **B2 — Chain conversion & progress** (G8, G9, G10, G15, G20-sales-chain):
  conversions carry `SourceLineNo` and the full header (party code, delivery
  date, valid-till, cost center, branch, sales rep, warehouse, notes);
  line-keyed (per-source-line) delivery/invoice creation replacing the
  material-keyed dictionary in service + SalesDeliveryWin; sales progress
  computed by source line (mirroring the purchase branch); stored
  `per_delivered`/`per_billed` on `sales_documents` (migration, both DBs)
  recomputed transactionally at every chain posting/conversion, with a derived
  status (To Deliver and Bill / To Deliver / To Bill / Completed) on the
  snapshot and shown in OrdWin/OrderTrackWin; chain draft creation persists
  header discount/extra instead of forcing 0; `Expired` quotation status +
  `ExpirePostedQuotationsAsync`; catalogued `AppError`s (SAL-7001…7008) for
  the chain services' user-facing validations; DocSearchWin SetLost RBAC.
- **B3 — Native invoice / payment / return** (G16, G17, G18, G20-windows, G23):
  partial return reduces the original invoice's open amount by auto-allocating
  the credit note to its origin invoice at posting; credit notes become
  allocatable sources in `ReconciliationRepository` + PayRecWin; SalesReturnWin
  gains a real refund path (payment method + refund amount wired into InvRepo's
  settlement leg); RBAC checks in InvoiceWin (create/update/post/reverse) and
  SalesReturnWin (view/create/post); InvoiceWin exposes **Valid Till** for
  quotations (InvDoc + InvRepo round-trip); voucher party link written inside
  the journal draft transaction.
- **B4 — Delivery GL & chain reversal** (G12, G13, G14): new account **1204
  Stock Delivered But Not Billed** (COA templates + migration for existing
  companies, both DBs; GlPosting constant with the manufacturing-style fallback
  to 1203 when a chart lacks it); DLV posting adds Dr 1204 / Cr 1203 at
  movement cost; DLV-linked invoice COGS becomes Dr 401 / Cr 1204 with
  **line-level pro-rata delivery-cost allocation**, enabling partial and
  multiple invoicing of one delivery (final invoice takes the rounding
  remainder); chain document reversal for posted DLV and chain SAL
  (compensating stock + GL via `document_reversals`, dependency guards:
  SAL-7005 downstream, SAL-7006 allocated payments) wired into
  SalesDeliveryWin / DocSearchWin; draft DLV cancellation.
- **B5 — Reports** (G22): CRM funnel window (pipeline by stage, win rate by
  rep, stale opportunities, lost/expired quotations) and sales-chain exception
  reports (ordered-not-delivered, delivered-not-invoiced), registered in
  RptNav, read-only over the same stored/computed state.

New error codes pre-registered for all workstreams: SAL-7001…SAL-7010,
CRM-9007, CRM-9008 (ErrorCodes.cs + En/Ar/Fr/Tr catalogs).

## 4. Accepted parity differences (deliberate, with reason)

- **Advances net the receivable account directly.** ERPNext also books
  advances against the party (receivable) account by default; a separate
  advances-liability account is an accounting-policy variant, not a parity
  requirement.
- **No unified Payment Entry document.** Receipt voucher + allocation covers
  the full functional gate (partial, advance, overpayment-as-credit,
  reversal with unallocation). A single-doctype UX is a redesign, not a
  functional gap. Flagged for the owner in §5.
- **Serial/batch selection on delivery is automatic (FEFO/FIFO).** The engine
  allocates and records the lots; a manual picker UI on the delivery window
  remains a gap (G24, not built — see §5).
- **Manual (unlinked) delivery notes bypass the order ceiling** — they have
  no order; ERPNext likewise permits direct delivery notes. Stock and QC
  rules still apply.
- **Percentages are quantity-weighted in computed progress**; the stored
  columns written by B2 use the same computation for consistency with every
  existing screen. ERPNext weights by amount — noted as a residual
  difference, not silently claimed.

## 5. Owner decisions flagged (not built)

1. **Approval routing** for quotations/orders (who approves, thresholds) —
   policy design, no spec exists.
2. **Credit hold at order stage** (soft/hard block before posting-time
   ACC-5006 enforcement) and **discount-limit / price-override limits** —
   policy values are the owner's.
3. **Opportunity stage automation** when its quotation is won/lost/converted —
   ERPNext leaves this manual; auto-propagation is a product choice.
4. **Unified Payment Entry window** replacing voucher + allocation two-step.
5. **Manual batch/serial picker** on the delivery window (G24).
6. **Per-line delivery dates** on the sales order (ERPNext carries a delivery
   date per SO line; ShouTech has header-level only).
7. **Shipment/carrier/tracking** data on deliveries (DeliveryDeskWin is a
   separate logistics surface; integration is a product decision).

## 6. Completion record

_To be filled at fan-in: per-workstream results, gate results (build 0/0,
migrator idempotency, audits), and the headless harness evidence for the full
chain: lead → opportunity → quotation → order → partial delivery (stock + COGS,
per_delivered) → partial invoice (AR + revenue + tax, per_billed) → partial
payment → remaining delivery + invoice → return against the original invoice at
original cost → all ledgers reconcile. Kern.Tests execution on Windows remains
owed._

---

# Part II — Owner Specification Coverage (Sales_Cycle.txt)

The owner supplied a complete sales-cycle specification (`Sales_Cycle.txt`,
§§1–19 + Arabic appendix L1–L4) which **supersedes the ERPNext-chain framing of
Part I** wherever they differ. Its §18 checklists are the definition of done;
its §17 phase order is the build order. A full spec-coverage audit was run
against the base branch (four read-only audits, file:line evidence). Verdicts
below are pre-build (B1–B5 workstreams from Part I are in flight and map to
Phases 01/02/03/04/05/06/07/12/13).

## Spec coverage summary (before phase-2 builds)

| Spec section | State | Headlines |
|---|---|---|
| §1 Customers (22 fn) | 5 exist, 11 partial, 6 absent | Cards/groups/history/aging/allocation exist. **Dead data:** price tier, `customer_prices`, CUSTOMER `discount_rules` and `credit_days` are stored but consumed by nothing. No branches, multi-address, multi-contact, per-customer currency, risk class, opening balances, party attachments; no customer statement (suppliers have one); blacklist = `is_active` only, and deactivation hides balances from reports |
| §2 Quotations | Partial | Chain + CRM paths exist (Part I G1–G10). No revisions/version preservation, no approve/reject-with-reason, no direct QUO→invoice conversion, no sales attachments, no email send |
| §3 Orders | Chain exists; controls absent | Reservation ABSENT (`reserved_qty` column never written), approval ABSENT (no `SALES.*.APPROVE` anywhere), amendments/revisions ABSENT for all documents, recurring ABSENT, cancel reason ABSENT (B2 fixing), no availability display/gate at entry, credit check only at invoice posting |
| §4 Delivery | DLV exists; fulfilment layer absent | Picking ABSENT; packing = printout only; DeliveryDesk (`delivery_requests`) is a **separate unlinked world** (free-text driver, no vehicle per delivery, no proof-of-delivery/signature, no delivery costing); returned deliveries ABSENT; performance reports ABSENT |
| §5 Invoices | Core exists | 10 types: order/delivery/direct exist; cash/credit/partial/service are modes; **consolidated, recurring, export ABSENT**; no approval step; no e-invoice; attachments purchase-only; live margin absent (report only); extra charges = one lump sum |
| §6 POS | Substantially built (066) | Terminals/sessions/tenders/held bills/returns/Z exist. ABSENT: scale integration, exchanges, cash expenses, true offline sync (PosSyncWin is a message-queue viewer). Cashier = current user's name; discount control binary, no numeric limits; performance report groups date+warehouse only |
| §7 Returns | Core exists (native path) | Against invoice only; no delivery-sourced returns, no authorized no-invoice returns, no inspection/condition routing (good/quarantine/damaged), reasons stuffed into Notes, no approval workflow, no RMA |
| §8 Receipts | Core exists | Cash/bank/cheque receipts, advances as unallocated credit, allocation + history exist; bounced cheques unwind GL. ABSENT: settlement discounts, cross-currency allocation + FX differences (hard-blocked), multi-method receipt vouchers, due-date-based auto-suggestion (no due dates exist), one-shot multi-invoice distribution, cheque→invoice allocation linkage |
| §9 Commissions | **Engine absent** | Rep master + flat % fields only; `sales_rep_targets.commission_earned` is a dead column. Nothing is calculated, adjusted, approved, settled, or paid |
| §10 Contracts | **Absent** | No entity/table/window/ribbon. `customer_prices` is the only neighbour and is not wired into pricing |
| §11 Pricing & Promotions | **No engine** | No price resolution anywhere: invoice prices are free-typed. Price lists/customer prices/tiers/rules are data-only. Promotions/BXGY/bundles ABSENT. `price_min`, `margin_target_pct`, `discount_max_pct` unenforced; `SALES.PRICE.OVERRIDE`/`SALES.DISCOUNT.OVERRIDE` **defined but never checked** |
| §12 Accounting | Posting table CONFORMS | All six standard postings verified in `InvRepo.BuildGlLines`/`GlPosting`. ABSENT: FX gain/loss posting, discount account (discount is netted into revenue), project dimension on sales docs/GL, AR subledger↔GL reconciliation, inventory valuation↔GL reconciliation (the purchase GRNI recon proves the pattern) |
| §13 Reports | 65 report windows | Categories absent: Returns, Contracts. Partial: Sales summary, Product Groups, Warehouses, Orders, Deliveries, Pricing analysis. Report framework filters cannot do customer-picker, rep, cost-center, project, or currency. Excel/PDF/print uniform via `RptBase` |
| §14 Permissions & Audit | Partial | Keys exist for most actions; ABSENT: `SALES.CREDIT.OVERRIDE` (hard block by design, documented in `PartyCreditGuard.cs`), discount limits, `SALES.RETURN.APPROVE`; **no unified document-events table**, no sales cancellation reasons, **no denied-access logging** |
| §15 Ribbon | No Sales tab | Sales is spread over Invoices/POS/Orders/Customers/SalesRep/Bonds tabs. Deliveries, Commissions, Contracts groups have **zero ribbon presence** |
| §16 Database | Mostly conforms | Standalone SQL in `dba/mig/{sqlite,postgresql}` 000–091. Spec prefixes not adopted (accepted — existing tables cover cus/sin/spr/pos; sqt/sor/sdl/srt/sre are kind-rows in shared tables by design). **sco and sct have no tables at all; sau (sales audit) absent** |
| Appendix L3 | Partial | CRM pipeline/leads/activities exist (057); campaigns = free-text field only; loyalty EXISTS (036); territories + visit routes + targets EXIST. Warranty = a `warranty_months` material attribute only; RMA ABSENT; after-sales = internal support tickets, not customer service |
| Appendix L4 | Absent | Intercompany, multi-country sales structure, drop shipping, consignment, subscriptions, e-commerce, sales forecasting — none exist |

## Phase-2 build plan (§17 order; after B1–B5 merge)

- **W1 — Customers (Phase 01):** contacts/addresses/branches tables + CstCard
  tabs; per-customer currency + risk classification; opening-balance posting;
  party-ledger customer statement (mirror the supplier one); suspension model
  (blacklist flag + reason, enforced, balances stay visible); credit check at
  quotation/order stage + `SALES.CREDIT.OVERRIDE` permission and override path
  in `PartyCreditGuard`; due dates from `credit_days` (due_date on documents,
  aging can use it); party attachments (generalize the 086 infrastructure).
- **W23 — Document lifecycle (Phases 02/03):** generic revision preservation
  (prior-version snapshots on amendment), quotation + order + invoice approval
  workflow (approve/reject-with-reason, `*.APPROVE` permissions, approval
  history), posted-order amendment via revision + controlled unpost (no posted
  downstream), QUO→invoice authorized conversion, sales-document attachments
  (with W1's generalization).
- **W4 — Delivery fulfilment (Phase 04):** picking requests from orders +
  pick confirmation; `delivery_requests` linked to DLVs (document reference)
  with dispatch from the delivery window; proof of delivery (delivered_at,
  received_by, signature image); delivery cost on the DLV; returned-delivery
  flow on the desk; delivery performance report.
- **W5 — Invoice completion (Phase 05):** consolidated invoicing
  (multi-delivery → one invoice); export-invoice header fields (Incoterms,
  ports, customs reference); live gross-profit/margin on InvoiceWin.
- **W6 — Returns completion (Phase 06):** structured return reasons master;
  condition routing (good → available, quarantine → QC warehouse, damaged →
  non-sellable location); inspection step; return approval workflow
  (`SALES.RETURN.APPROVE`); authorized no-invoice returns (permission-gated);
  delivery-sourced returns for uninvoiced deliveries.
- **W7a — Pricing & promotions (Phase 08):** one price-resolution path
  (contract → customer_prices → material_price_list by validity/unit/currency
  → tier columns → material price) consulted at InvoiceWin/OrdWin/POS entry;
  quantity-break + volume-discount application from `discount_rules`; BXGY
  from the dormant `bonus_*` fields; enforcement of `price_min`,
  `discount_max_pct`, `margin_target_pct` with `SALES.PRICE.OVERRIDE` /
  `SALES.DISCOUNT.OVERRIDE` checks at save and post; master price-change
  audit log.
- **W7b — Commissions (Phase 09):** `sco` tables (policies incl. tiered
  slabs, commission entries, settlements); entries generated at invoice
  posting (sales/profit basis) and at allocation (collection basis);
  return-adjusted; approval; settlement paid via voucher; commission window
  + rep performance figures.
- **W7c — Contracts (Phase 10):** `sct` tables (contracts + lines with
  agreed prices, contracted quantities, schedule); contract prices feed W7a
  resolution; orders link to contracts with draw-down; scheduled-delivery
  order generation; renewals; expiration alerts; fulfillment + profitability
  views.
- **W8 — Receipts remainder + audit & reconciliation (Phases 07/13):**
  settlement discounts at allocation (authorized, posted to a discount
  account); cross-currency allocation with realized FX gain/loss posting;
  due-date FIFO auto-suggestion + one-shot multi-invoice distribution in
  PayRecWin; `sau` document-events table written by the sales posting paths
  with a history viewer; denied-access logging; AR subledger↔GL and
  inventory↔GL reconciliation reports (mirroring the GRNI pattern).
- **W9 — POS remainder (Phase 11):** cash expenses/paid-outs in the session
  expected-cash math + Z report; exchanges (return + resale netted);
  numeric cashier discount limits; per-terminal/per-cashier performance
  grouping.
- **W10 — L3 extensions:** CRM campaigns master; warranty registration from
  sold serials + claims; RMA requests converting to authorized returns;
  pipeline-based sales forecast view.
- **Recurring engine (Phases 03/05, with W4):** schedule table + template
  documents + due-document generation for recurring orders/invoices (also
  the mechanical basis for subscription billing).

## Owner decisions flagged (Part II — not invented)

1. **E-invoice format/country** (ZATCA clearance etc.) — legal/format choice;
   no submission channel built.
2. **Email sending** for quotations/invoices — needs SMTP configuration and
   a sending policy.
3. **Weighing-scale hardware protocol** (POS) — device-dependent.
4. **Offline POS architecture** — store-and-forward target (central server?)
   is a product/infrastructure decision; the desktop app posts to its DB
   directly today.
5. **Intercompany sales model** and **e-commerce platform** (L4) — external
   system choices.
6. **Drop shipping / consignment models** (L4) — stock-ownership and
   purchase-chain policies; drop-ship also overlaps the parallel
   purchase-cycle branch.
7. **Discount-account and FX gain/loss account numbers** will follow the
   existing COA template structure (discount netted today; the spec's §12
   account list requires both accounts) — implemented in W7a/W8 using the
   template's next consistent codes and recorded in the completion record.

## §18 completion checklist — scoring

_Scored at the end of the build (Part III completion record): every checkbox
from the spec's §18 (13 functional / 10 technical / 10 accounting-inventory /
8 final-testing) itemized as PROVEN with evidence or an explicit GAP with the
reason, followed by the four-system comparison (Al-Ameen 10 / Phenix v10 /
ERPNext / Odoo) the spec requires._
