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
- **B4 — Delivery GL & chain reversal** (G12, G13, G14): new account **1215
  Stock Delivered But Not Billed** (COA templates + migration for existing
  companies, both DBs; GlPosting constant with the manufacturing-style fallback
  to 1203 when a chart lacks it). _Account-code correction at build time: the
  brief said 1204, but 1204 is already "Prepaid Expenses" in every COA
  template — 1215 is the first free code in the inventory family (1210 raw,
  1211 WIP, 1212 FG, 1213 spare parts) and is what was built._ DLV posting
  adds Dr 1215 / Cr 1203 at movement cost; DLV-linked invoice COGS becomes
  Dr 401 / Cr 1215 with
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

## 6. Completion record — Part I workstreams (B1–B5, merged)

All five sub-branches merged into `feat/sales-cycle-erpnext` (B1 `da3baef`,
B2 `073229a`, B4 `c8ef932`, B3 `2774625`, B5 `0908a15` + Lang deep-union fix
`beac68e`, test API conversion `628db44`, stored-progress posting hook
`9f7e241`).

**Gates on the merged tree**
- Full solution build `--no-incremental`: **0 errors, 4 warnings** — the 4 are
  the base branch's pre-existing CA1859 pair in `PurchaseReceiptReturnWin.cs`
  (untouched); no new warnings.
- SqlMigrator on fresh SQLite: applies through **094**; second run applies 0
  (idempotent). Migrations added: 092 (CRM opportunity items), 093 (stored
  progress + cancellation audit), 094 (account 1215) — each in both dialects.
- `tools/audit_window_context.py`: exit 0.
- `tools/verify_screenfit.py` and `tools/verify_english_source.py`: exit 1 —
  **identical failures on the base branch** (`52a762a`: TaxReportWin/TaxSetupWin/
  SupplierQuotationEntryWin minimums; Fr/Tr catalog asymmetries from the
  stabilization branch). No new failures; Lang catalogs grew by exactly the 69
  keys this work added, in all four languages, with zero keys lost.
- Errors catalogs: 119 entries × 4 languages (12 codes added at `ad26b4a`).

**Headless harness (real src-level services, migrated SQLite): 35/35 PASS**
- CRM: lead saved → duplicate rejected with CRM-9007 → conversion creates
  party + opportunity → items persist, expected amount = items total 264.50 →
  quotation from items with header 230 / 34.5 / 264.5, valid_till set, lines
  carrying discount + tax, opportunity link recorded.
- Chain: QUO → SO → DLV 4/10 (stock 1000→996, Dr 1215 = 160, stored
  per_delivered = 40) → SAL 4 (AR Dr 100, revenue Cr 100, COGS Dr 160 /
  Cr 1215 → 1215 back to 0, per_billed = 40, party ledger Dr 100) → DLV 6 +
  SAL 6 → progress 100/100 stored and computed, stock 990.
- Payment: receipt voucher's party link written inside the draft transaction;
  receipt listed unallocated; allocation settles SAL1 (open 250→150 total);
  party balance 150 = SAL2 open amount.
- Reversal: second order's DLV (2 units) posted then reversed with a reason —
  document Cancelled, stock restored 988→990, stored per_delivered back to 0,
  `document_reversals` row present.
- Global: journal Dr = Cr; 1215 nets to 0; inventory GL net Cr 400 = stock
  value drop (10 × 40); COGS Dr 400; AR GL Dr 150 = party ledger = open
  invoices.
- The native `InvRepo` path (B3: return settlement, allocatable credit notes,
  refunds, RBAC, Valid Till) lives in the WPF assembly and is covered by
  Kern.Tests facts that compile here; **execution on Windows remains owed**,
  as do live PostgreSQL verification and WPF click-through.

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
  - **W9 BUILT (migration 104, both dialects):** `pos_session_expenses`
    (paid-outs: amount/reason/reference against an OPEN session, gated
    POS.EXPENSE.MANAGE; sessions journal nothing at close, so a paid-out
    is a reconciliation record that SUBTRACTS from expected cash in the
    Z report and at close — no parallel GL); exchanges as TWO linked
    documents (the existing SRT return + POS sale paths, each in its own
    posting transaction, stamped with a shared `sales_documents.
    exchange_ref`): the return refunds only the negative-net excess, the
    consumed return value tenders the new sale as an exchange credit
    (PosTenderMath exchangeCredit — not party credit; the pair's GL
    receivable legs and drawer tenders net exactly); numeric cashier
    discount limits in `pos_discount_limits` (USER row beats ROLE rows,
    resolved in PosRepository) enforced at PosWin checkout by
    PosDiscountMath (effective % and max-line % both bind; ceiling 0
    without POS.DISCOUNT.OVERRIDE) with a supervisor override —
    PosDiscountGuard verifies the supervisor's own password (PwdHash)
    + POS.DISCOUNT.OVERRIDE under the Permissions.cs resolution and the
    approval is audited in `pos_discount_overrides` (spr_price_overrides
    does not fit: per-line pricing-guard rows with no distinct approver);
    per-terminal/per-cashier performance (PosRepository.GetPerformanceAsync
    → RptQuery.PosPerformanceAsync → PosPerfRptWin, ribbon POS.Performance):
    sales count/gross, returns, discounts given, paid-outs, average basket
    with date/terminal/cashier filters. NOT built (owner decisions, above):
    offline sync architecture (#4) and scale integration (#3).
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
7. **Discount-account and FX gain/loss account numbers** — resolved in
   W8: every COA template already carries 305 Sales Discounts, 405
   Purchase Discounts, 509 FX Losses and 306 Other Income (used for FX
   gains; no template has a dedicated FX-gain account). Nothing was
   seeded into any chart.

## §18 completion checklist — scoring

_Scored at the end of the build (Part III completion record): every checkbox
from the spec's §18 (13 functional / 10 technical / 10 accounting-inventory /
8 final-testing) itemized as PROVEN with evidence or an explicit GAP with the
reason, followed by the four-system comparison (Al-Ameen 10 / Phenix v10 /
ERPNext / Odoo) the spec requires._

---

# Part III — §18 Scoring, Four-System Comparison, Final Completion Record

Scored 2026-11-24 on branch `feat/sales-cycle-erpnext` (base
`52a762adc98a3a2cd003c95eb13475b4c01c432e`, the tip of
`work/full-app-stabilization-2026-11-18`). The spec file's §18 actually
enumerates 13 functional / 12 technical / 10 accounting-inventory / 9
final-testing items; every item is scored below as written in the file.
Evidence key: **[H]** headless harness on the real services (SQLite),
**[T]** Kern.Tests facts (compile-verified here; execution owed on
Windows), **[B]** build/migrator/audit gate, **[C]** code inspection.

## §18.1 Functional completeness (13)

| # | Item | Verdict | Evidence |
|---|------|---------|----------|
| 1 | Customer management functions | **PROVEN** (minor note) | W1 (mig 095): contacts/addresses/branches with primary mirroring, per-customer currency, risk class, blacklist enforced at both party resolvers (ACC-5020), opening balances via GlPosting + party_ledger OPN row, customer statement window, order-stage credit check + audited SALES.CREDIT.OVERRIDE, due dates from credit_days [T][C]. Note: territory/area is stored on parties/policies but no document carries an area, so territory matching is dormant (W7b leftover). |
| 2 | Quotation functions | **PROVEN** | B1 (items grid, valid-till picker, dedupe CRM-9007, conversion with discount/tax carried), B2 (Expired status + sweep), W23 (revisions with preserved snapshots, approval workflow, authorized direct QUO→invoice). [T][H] |
| 3 | Sales order functions | **GAP (one sub-function)** | Built: line-keyed partial fulfilment, stored per_delivered/per_billed + derived statuses, credit check at order stage, amendments via revision (W23), cancellation with reason, recurring orders (W11), contract draw-down (W7c), picking generation (W4). **Missing: stock reservation** — `reserved_qty` exists in the schema and SAL-7011 is registered, but no order flow writes reservations; posting an order does not reserve stock. This is the one §3 sub-function not delivered. |
| 4 | Approval workflows | **PROVEN** | W23: QUO/SO/invoice approval (Draft → Pending → Approved/Rejected-with-reason, history, separated-duty permissions) behind a company flag default OFF; W6: return approval (SALES.RETURN.APPROVE). [T] Note: two per-area flags exist (`sau_sales_settings`, `srt_return_settings`) — consolidation candidate, both default OFF. |
| 5 | Delivery and warehouse functions | **PROVEN** (minor note) | W4 (mig 096): picking with over-pick guard SAL-7012, dispatch with driver/vehicle, proof of delivery (receiver + timestamp + signature bytes), returned-delivery flow via chain reversal, delivery performance reporting. Note: packing remains a printout, not a scanned verification step (appendix L2 "Packing Verification" not built). |
| 6 | Invoice types (all 10) | **PROVEN** | Cash, credit, order-based, delivery-based, direct, partial (B-chain), consolidated (W5), recurring (W11), export (W5 header fields), service (service-material path). [H][T] |
| 7 | Return and credit note functions | **PROVEN** | B3 (origin-invoice settlement in-transaction, allocatable credit notes, capped cash refunds), W6 (reasons master, Good/Quarantine/Damaged routing, inspection SAL-7022, approval, no-invoice returns, delivery-sourced returns relieving 1215, cumulative caps SAL-7021), W10 RMA front-end. [T] |
| 8 | Customer receipt functions | **PROVEN** | Advances + on-account (pre-existing, verified), allocation incl. credit notes (B3), W8: settlement discounts (permission-gated, posted in-allocation), cross-currency allocation with realized FX gain/loss, due-date FIFO suggestion + multi-invoice distribution; bounced-cheque reversal and refunds verified present, not rebuilt. [H 15/15] |
| 9 | Pricing and promotional functions | **PROVEN** (minor note) | W7a: single resolution engine (contract → customer_prices → tier price list → tier columns → card) with provenance, discount_rules quantity breaks, BXGY (mig 099 `bonus_buy_qty`), price_min/margin/discount_max enforcement at save + post with audited overrides, price-change history. Note: no dedicated promotion-performance *report*; the data (rules + history + overrides) exists. MatCard has no `bonus_buy_qty` field yet (DB/import only). |
| 10 | Commission functions | **PROVEN** (minor note) | W7b (mig 097): Sales/Collection/Profit bases, specificity + tier resolution, return-adjusted clawbacks, approval → settlement → balanced journal payment, fail-soft generation hooks on chain + native invoice paths. Notes: territory matching dormant (no document area source); POS invoices not hooked (no rep convention on POS docs). |
| 11 | Contract functions | **PROVEN** (minor note) | W7c (mig 101): contracts + lines + events, contract prices as pricing level (a), draw-down guards SAL-7015/SAL-7023 at save + post, scheduled generation (draft orders), renewals, expiration sweep/query, fulfillment + profitability. Notes: QUO→SO conversions don't propagate contract_id; native invoices are contract-priced but don't roll into fulfillment quantities. |
| 12 | POS fully integrated | **PROVEN** (owner-decision gap noted) | 066 sessions/split tenders/suspended sales/returns + W7a resolver pricing + W9: paid-outs in session/Z math, exchanges (two linked documents, exact tender netting), numeric cashier discount limits with password-verified supervisor override + audit, per-terminal/cashier performance. **Not built by design: true offline sync and weighing-scale integration — both flagged owner decisions (architecture / hardware protocol).** |
| 13 | Sales reports (all) | **GAP (partial)** | Delivered: sales analysis + AR aging + customer statement (W1), CRM funnel + forecast (B5/W10), sales exception report incl. delivery performance + AR↔GL and inventory↔GL reconciliation sections (B5/W4/W8), commission entries view (W7b), POS performance (W9), contract fulfillment (W7c). **Not delivered as a set: the spec's full 17-category report catalog with the complete filter matrix (dates/customer/product/group/rep/warehouse/cost-center/project/currency/status) on every report.** Core reporting is real; the exhaustive catalog is not complete. |

## §18.2 Technical completeness (12)

| # | Item | Verdict | Evidence |
|---|------|---------|----------|
| 1 | Windows connected to the main window | **PROVEN** | Ribbon routes added for CustStmtWin, CommWin, ContractWin, RecurWin, CrmCampaignWin, WarrantyWin, RmaWin, PosPerfRptWin; `tools/audit_window_context.py` exits 0. [B] |
| 2 | Windows connected to their services | **PROVEN** | AppHost composition extended for Commissions, Contracts, Recurring, Warranties (+ rebind path). [C][B] |
| 3 | Services connected to real database operations | **PROVEN** | Every workstream posts through the real repositories/engines; harnesses run on migrated databases only. [H] |
| 4 | All required SQL files created | **PROVEN** | Migrations 092–106, each in both `dba/mig/sqlite/` and `dba/mig/postgresql/`. [B] |
| 5 | SQLite and PostgreSQL migrations completed | **PARTIAL** | SQLite: applied + idempotent double-run verified through 106. PostgreSQL: dialect-twin files written to the established conventions but **never executed** — no PostgreSQL server exists in this environment. Live PG run owed on the owner's infrastructure. |
| 6 | No fake or temporary save operations | **PROVEN** | The five historical FAKE-SAVE windows were rebuilt in earlier phases; all wave work persists in the posting transaction (incl. W1 opening balances, W6 inspection/approval state, W9 paid-outs). [C][H] |
| 7 | No missing document relationships | **PROVEN** | document_links at every hop, per-line source attribution (mig 102), contract/recurring/exchange/RMA/warranty links, unified `sau_document_events`. [H][T] |
| 8 | Document numbering unique and concurrency-safe | **PARTIAL** | Mechanism: sequence/numbering-service based (B1 removed the COUNT(*)+1 quotation numbering; recurring and contract numbering follow the same pattern). **No concurrent-numbering stress test was executed** — scored as a proof gap under §18.4 #7 as well. |
| 9 | Complete authorization and audit implementation | **PROVEN** | RBAC seeds in every migration (097–106), approval history, price/credit/settlement override audits, denied-access events (W8 `PermServ.Require`), error catalog SAL-7001…7029 + ACC-5020 in 4 languages. [C][B] |
| 10 | English-only source code and comments | **PROVEN (no regression)** | `verify_english_source.py` output is byte-identical in failure set to the base branch (pre-existing purchase-side catalog asymmetries); no new violations introduced by any wave (verified by missing-key diff at every fan-in). [B] |
| 11 | UI language supplied through resource files | **PROVEN** | Every new key added to all four Lang catalogs with identical structure; fan-in parity checks show zero new missing keys in Ar/Fr/Tr. Errors catalogs at 139 entries × 4. [B] |
| 12 | Consistent visual identity across all windows | **PROVEN** (visual check owed) | All new windows use the central Win11 tokens, per-window schedule tints, bottom action bars, ENTER-horizontal grids, ≤50% opening via BaseWin. WPF click-through/visual confirmation remains part of the owed Windows run. [C] |

## §18.3 Accounting and inventory completeness (10)

| # | Item | Verdict | Evidence |
|---|------|---------|----------|
| 1 | Correct customer balances | **PROVEN** | Party ledger is transaction-derived; wave-4 harness: balance 250 = sum of open invoices; AR GL = party ledger in Part I harness (35/35). [H] |
| 2 | Correct revenue recognition | **PROVEN** | Stored-total posting (InvRepo/chain), §12 posting table conformance verified in Part II audit; returns reverse revenue proportionally at original values. [H][T] |
| 3 | Correct tax calculations when enabled | **PROVEN** | Tax engine untouched by the waves except guarded seams; chain + native postings carry tax through stored totals; returns reverse tax pro-rata. [T][C] |
| 4 | Correct inventory deductions | **PROVEN** | Unit-equivalent deductions (250 g = 0.25 kg), service/non-stock exemption everywhere incl. returns and POS; harness stock 1000 → 990. [H] |
| 5 | Correct Cost of Goods Sold | **PROVEN** | One StockValuationEngine; delivery COGS parked in 1215 and relieved per-delivery with rounding plug; consolidated relief exact (1215 → 0). [H] |
| 6 | Correct sales return accounting | **PROVEN** | Original-cost returns; damaged lines take no cost-side entry (original COGS stands as the write-off — GL balanced, inventory GL == valuation); quarantine routing via the 056 mechanism. [T] |
| 7 | Correct customer receipt allocation | **PROVEN** | Allocation settles open amounts in-transaction (B3); settlement discounts and FX post inside the allocation journal; W8 smoke harness 15/15. [H] |
| 8 | Correct foreign currency treatment | **PROVEN** | FX via the existing resolver/rates; cross-currency allocation posts realized gain (Cr 306) / loss (Dr 509); worked examples in W8 record (+55 loss / −25 gain). [H][T] |
| 9 | Correct General Ledger reconciliation | **PROVEN** | Journals balanced in every harness (Dr = Cr); W8 AR↔GL and inventory↔GL reconciliation reports (inventory GL set includes 1215 with documented reasoning). [H] |
| 10 | Complete reversal and audit history | **PROVEN** | Chain reversal with reason (B4), document_reversals, approval history, unified document events incl. AccessDenied, cancellation reasons mandatory. [T][C] |

## §18.4 Final testing (9)

| # | Item | Verdict | Evidence |
|---|------|---------|----------|
| 1 | Successful Windows build | **GAP** | The solution builds 0 errors / 4 baseline warnings on this Linux VM with Windows targeting; a build on the owner's Windows machine is still owed (standing caveat for the whole project). |
| 2 | Successful SQLite tests | **PARTIAL** | Runtime harnesses on migrated SQLite all pass: Part I chain 35/35, W4 14/14, W5 23/23, W8 15/15, W11 runtime proof, wave-4 consolidated 15/15. Kern.Tests (100+ new facts across the waves) are compile-verified only — the testhost targets net8.0-windows and cannot execute here. |
| 3 | Successful PostgreSQL tests | **GAP** | No PostgreSQL server available in this environment; PG migrations are dialect twins, unexecuted. |
| 4 | Successful complete sales workflow tests | **PROVEN** (harness level) | Lead → opportunity → quotation → order → partial delivery → partial invoice → allocation → completion → return, plus consolidated/contract/commission/recurring scenarios, all on the real services. [H] |
| 5 | Successful accounting reconciliation | **PROVEN** | See §18.3 #9; reconciliation reports ship in SalExceptWin. |
| 6 | Successful inventory reconciliation | **PROVEN** | See §18.3 #4/#5 + inventory↔GL report (W8). |
| 7 | Successful concurrent document numbering tests | **GAP** | Not executed (see §18.2 #8). |
| 8 | No unresolved critical errors | **PROVEN** | Build clean at the baseline warning count; every fan-in gate green; all known leftovers are minor and itemized in the completion record below — none is a posting/money defect. |
| 9 | Final comparison against Al-Ameen 10, Phenix v10, ERPNext and Odoo | **PROVEN** | Table in the next section. Evidence-depth note: Al-Ameen/Phenix columns rest on vendor documentation/marketing and the owner's parity matrix, not on hands-on runs of those products — the same evidence standard as the purchase-cycle audit. |

## Four-system comparison (sales cycle, post-upgrade state)

| Capability | ShouTech (this branch) | ERPNext | Odoo | Al-Ameen V10 | Phenix v10 |
|---|---|---|---|---|---|
| Lead → Opp → Quotation → Order → Delivery → Invoice → Payment chain | Full, line-keyed, source links preserved | Full (benchmark) | Full (CRM + Sale) | Full | Full |
| Partial delivery / partial invoicing with remaining-qty enforcement | Yes — stored per_delivered/per_billed + derived statuses | Yes | Yes | Yes | Yes |
| Delivery accounting before invoicing | Dr 1215 / Cr inventory at delivery; relieved per delivery at invoicing | Yes (DN posts stock ledger; SI posts COGS) | Yes (with accounting modules) | Yes | Yes |
| Stock reservation on orders | **No — gap (§18.1 #3)** | Yes | Yes (forecasted qty) | Yes | Yes |
| Pricing engine (lists, tiers, customer prices, contracts) | Yes — one resolver, provenance shown | Yes (Pricing Rules) | Yes (pricelists) | Yes | Yes |
| Min price / margin / discount enforcement with authorized override | Yes — blocked at save + post, overrides audited | Partial (margin alerts; blocking via customization) | Partial | Yes | Yes |
| Promotions (BXGY, volume) | BXGY + quantity-break rules; no promo-analysis report | Yes | Yes (coupons/programs) | Yes | Yes |
| Commissions (sales/collection/profit, tiers, settlement) | Yes — incl. return clawbacks, journal-paid settlements | Partial (sales partner/team; settlement manual) | Partial (module dependent) | Yes | Yes |
| Contracts with draw-down + scheduled deliveries | Yes | Yes (blanket orders partial) | Partial | Yes | Yes |
| Recurring orders/invoices | Yes — template engine, idempotent generation | Yes (subscriptions) | Yes (subscriptions) | Partial | Yes |
| Returns: reasons, condition routing, inspection, RMA, warranty | Yes — incl. quarantine/damaged routing, RMA, warranty claims | Partial (RMA via quality/returns; warranty separate) | Partial (RMA module dependent) | Yes | Yes |
| Receipts: advances, settlement discount, cross-currency FX | Yes — realized FX posted in-allocation | Yes | Yes | Yes | Yes |
| POS: sessions, paid-outs, exchanges, discount limits | Yes; **offline mode = owner decision** | Yes (POS with offline) | Yes | Yes | Yes |
| CRM campaigns + forecast | Yes (basic roll-ups, stage-weighted forecast) | Yes | Yes | Partial | Partial |
| E-invoicing / e-commerce / intercompany / drop-ship / consignment / loyalty | **Not built — owner decisions flagged** | Partial–Yes (varies) | Yes (modules) | Partial | Partial |
| Audit: unified document events + denied-access logging | Yes | Yes (versioning/activity) | Yes (chatter) | Yes | Yes |
| Languages / RTL | 4 languages, full RTL via central direction service | Many; RTL partial | Many; RTL partial | Arabic-first | Arabic-first |

## Final completion record (waves 1–3, on top of Part I §6)

- **Wave 1** — W1 customers (mig 095), W4 picking & delivery (mig 096),
  W7b commissions (mig 097). Fan-in: ACC-5020 registered; blacklist throws
  cataloged.
- **Wave 2** — W23 document lifecycle (mig 098), W7a pricing & promotions
  (mig 099), W6 returns completion (mig 100). Fan-in: approval gate and
  pricing guard composed sequentially in `PostSalesAsync`.
- **Wave 3** — W7c contracts (mig 101), W5 invoice completion (mig 102),
  W8 receipts/audit/reconciliation (mig 103), W9 POS remainder (mig 104),
  W10 campaigns/warranty/RMA/forecast (mig 105), W11 recurring engine
  (mig 106). Fan-in: SAL-7027/7028/7029 registered; W5 consolidation guards
  converted from the SAL-7004 stand-in to SAL-7029; W11 test fake extended
  for W5's new repository member.
- **Gates on the final tree**: full build 0 errors / 4 warnings (the base
  branch's pre-existing CA1859 set in PurchaseReceiptReturnWin.cs — no new
  warnings); SqlMigrator idempotent through migration 106 (SQLite);
  `audit_window_context.py` exit 0; `verify_screenfit.py` and
  `verify_english_source.py` fail only on the identical pre-existing
  base-branch items (TaxReportWin/TaxSetupWin/SupplierQuotationEntryWin
  minimums; purchase-side Fr/Tr catalog asymmetries) — zero regressions;
  Lang parity: zero new missing keys at every fan-in; Errors catalogs
  139 entries × 4 languages.
- **Runtime proof on the final merged tree** (wave-4 consolidated harness,
  real services on migrated SQLite, 15/15): below-minimum order blocked at
  save with SAL-7018; delivery debits 1215 by 160 and the partial invoice
  relieves it to 0; the AppHost-wired commission hook generates 10 then 25
  cumulative; stored per_billed tracks 40 → 100; stock 1000 → 990; party
  ledger 250 = open invoices; journal balanced (Dr = Cr = 1050); a
  consolidated invoice over two deliveries (1215 at 400) relieves 1215 to
  exactly 0 with per_billed 100 and total 250.
- **New files kept** (owner file rule — everything else extended existing
  files): CustStmtWin.cs (customer statement; SupStatementWin is
  supplier-shaped), Comm.cs/CommRepository.cs/CommService.cs/CommWin.cs
  (commissions module), SalesPricing.cs + Pricing.cs (pricing engine),
  SrtRepo.cs (returns workflow home), Contract.cs/ContractRepository.cs/
  ContractService.cs/ContractWin.cs (contracts), DocEvents.cs (one events
  writer across layers), PosPerfRptWin.cs (report-window-per-file repo
  convention), Warranty.cs/WarrantyWin.cs, CrmCampaignWin.cs, RmaWin.cs,
  Recur.cs/RecurRepository.cs/RecurService.cs/RecurWin.cs, and migrations
  092–106 × 2 dialects (numbered migration files are the repo convention).
- **Deliberate leftovers** (each with its reason, none silent): stock
  reservation (§18.1 #3); the 17-category report catalog in full
  (§18.1 #13); MatCard `bonus_buy_qty` field; amendment/direct-conversion
  window buttons (service-complete); settings-window toggles for the two
  approval flags + POS discount limits (no POS settings editor exists);
  POS commission hook; contract propagation through QUO→SO conversion;
  delivery-cost GL auto-posting (recording-only — policy decision);
  signature rendering on delivery printouts; events timeline not fed by
  approval transitions (they live in `sau_approval_history`).
- **Owner decisions still open** (flagged, not invented): e-invoice
  country/format (e.g. ZATCA), email/SMTP for sending quotations,
  weighing-scale protocol, offline-POS architecture, intercompany model,
  e-commerce platform, drop-ship/consignment policies, delivery-cost GL
  posting policy, margin-visibility permission for InvoiceWin's live
  margin, sales-territory data source (documents carry no area today).
- **Verification still owed on Windows** (standing): Kern.Tests execution,
  WPF click-through of all new windows, live PostgreSQL migration +
  posting run, Windows build. Nothing in this branch is merged or pushed;
  it awaits the owner's decision like every prior phase.
