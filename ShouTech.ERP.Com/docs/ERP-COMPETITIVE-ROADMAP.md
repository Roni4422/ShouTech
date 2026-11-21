# ShouTech ERP Competitive Gap Assessment and Delivery Roadmap

> **Current consolidated assessment (2026-11-21):** See [COMPETITIVE_GAP_ASSESSMENT_2026-11-21.md](COMPETITIVE_GAP_ASSESSMENT_2026-11-21.md) for the full current comparison against Manager.io, ERPNext, Odoo, Al-Ameen 10 and Phenix 10, including the current branch evidence snapshot, prioritized gaps and release acceptance gates. This document remains the detailed engineering roadmap and historical workflow/source review.


**Target:** Make ShouTech a commercially credible offline-first desktop ERP that can compete with Odoo and ERPNext in the business workflows relevant to its target customers.

**Baseline branch:** `work/full-app-stabilization-2026-11-18`  
**Baseline commit reviewed:** `dd96625861a78d697d78b102a4a7b6bbdf2112b2`  
**Assessment date:** 2026-11-19

## 1. Product direction

ShouTech must remain a native/offline-first desktop ERP. Do not copy Odoo's web architecture or ERPNext's Frappe/DocType implementation. Reuse their proven business concepts and process controls, implemented through ShouTech's existing Domain / App / Infra layers, SQLite-first local persistence, PostgreSQL support where already provided, and WPF Ribbon interface.

The benchmark is workflow completeness and accounting/inventory integrity, not the number of screens or tables.

## 2. Reference systems reviewed

- Odoo source repository: https://github.com/odoo/odoo
- Odoo application catalogue: https://www.odoo.com/page/all-apps
- Odoo accounting concepts: https://www.odoo.com/documentation/20.0/applications/finance/accounting/overview/main_concepts/in_odoo.html
- Odoo MRP module overview: https://github.com/odoo/odoo/blob/20.0/addons/mrp/README.md
- ERPNext source repository: https://github.com/frappe/erpnext
- ERPNext official documentation: https://docs.frappe.io/erpnext/introduction
- ERPNext selling workflow: https://docs.frappe.io/erpnext/selling
- ERPNext accounting effects: https://docs.frappe.io/erpnext/accounting-entries
- ERPNext module settings: https://docs.frappe.io/erpnext/module-settings

Important benchmark behaviours:
- Sales is a connected lifecycle: enquiry/lead -> quotation -> order -> delivery -> invoice -> payment, with valid shorter paths for counter sales, services and drop-shipping.
- Buying is a connected lifecycle: request/requisition -> supplier quotation -> purchase order -> receipt -> supplier invoice -> payment, with partial receipts, returns and landed-cost implications.
- Stock events have traceable source documents, warehouse/bin locations, valuation effects, reservations, serial/batch tracking and returns.
- Operational documents create the appropriate GL effects on posting; ordinary sales/purchases/payments should not be replaced by arbitrary manual journals.
- Manufacturing connects BOM revisions, demand/MRP, work orders/operations, material issue, finished output, scrap/by-products, work-in-progress and actual-vs-standard cost.
- Accounting includes multi-currency/base-currency valuation, taxes, dimensions/cost centres, budgets, bank reconciliation, receivables/payables ageing, period closing and audit trail.
- Asset, HR/payroll, project, quality, maintenance and support processes should integrate with accounting and operational cost rather than exist as isolated forms.

## 3. ShouTech code inventory: what exists is not yet proof of completeness

The audited tree contains substantial foundations:
- Domain entities/interfaces for commercial documents, journals, POS, CRM, manufacturing, BOM, planning, quality, projects, HR, fixed assets, budgets, cheques and reconciliation.
- Application services including SalesChainService, OrderService, PosService, MfgOrderService, BomService, PlanningService, QualityService, CrmService, ProjectService, HrService, AssetService, JournalService and ReconciliationService.
- Infrastructure repositories/engines including CommercialDocumentRepository, OrderDocumentRepository, PosRepository, MfgOrderRepository, StockValuationEngine, StockRecalculationService, GlPosting, FxRates, PartyLedger, AssetRepository and other module repositories.
- WPF inventory snapshot: 159 unique view/source basenames, 23 XAML/code-behind pairs, 63 report basenames. MatCard has been moved to Views/Inventory; the remaining UI has not yet been fully grouped by business module or runtime-verified.

These are evidence of implementation foundations only. A class, table, report or window does not count as a completed feature until its full business path, validations, permissions, persistence, accounting/stock effects, reversal, localization and tests are verified.

## 4. Gap matrix and required acceptance

| Business area | Existing evidence in ShouTech | Benchmark capability to verify/build | Acceptance gate |
|---|---|---|---|
| General ledger | JournalService, GlPosting, Accounting, chart/accounting entities | Automated source-document posting, dimensions, accrual/cash basis where applicable, recurring journals, allocations, period lock/close, audit | Every posted source document produces balanced GL; reversals preserve original and net correctly; trial balance and statements reconcile |
| Sales / AR | SalesChainService, OrderService, CommercialDocumentRepository, PartyLedger, invoice/order/delivery windows | Lead/quotation/order/delivery/invoice/credit note/receipt lifecycle, partial fulfilment, credit limits, price lists, discounts, tax, returns, dunning/ageing | Complete and partial paths pass; stock, AR, revenue, tax, COGS and cash reconcile for each path |
| Buying / AP | OrderDocumentRepository, OrderService, supplier ledger, purchasing document foundations | Requisition/RFQ/supplier quotation/PO/receipt/bill/payment, partial receipt, supplier return, price variance, landed cost, three-way match | Ordered/received/billed quantities and values reconcile; AP, stock, tax and GRNI clear correctly |
| Inventory / WMS | StockValuationEngine, StockRecalculationService, BatchSerial, warehouse/material windows and reports | Multi-warehouse locations/bins, reservations, replenishment, put-away/picking, transfers, cycle counts, lot/serial/expiry, valuation methods, landed cost | Quantity and value reconcile by item/warehouse/lot; no duplicate movements; negative-stock policy enforced; replay/recalculation is deterministic |
| POS | PosService, PosRepository, PosPosting, session/checkout/return windows | Offline shift/cash control, multiple tenders, customer credit, refunds, exchanges, price override permissions, cash-up/Z report, safe sync/idempotency | Reopen/retry/sync does not duplicate sales; refunds mirror settlements and stock; session totals equal cash/bank/GL |
| Manufacturing | BomService, MfgOrderService, MfgOrderRepository, PlanningService, Mfg windows/reports | Multi-level/revision-controlled BOM, routings/work centres/capacity, MRP, job cards, WIP, scrap/by-products, subcontracting, actual costing and variance | Material issue/output/WIP/GL agree; insufficient stock rolls back; cancellation restores balances; cost variance is explainable |
| Quality / Maintenance | QualityService, QualityGate, quality and maintenance windows | Incoming/in-process/final inspections, non-conformance, corrective actions, equipment schedules, preventive maintenance and downtime cost | Failed inspection blocks configured next step; corrective action and equipment history are traceable |
| Fixed assets | AssetService, AssetRepository, FixedAsset entity, AssetWin | Acquisition/capitalisation, depreciation books/methods, disposals, transfers, impairment/revaluation, maintenance, GL integration | Asset register ties to GL; schedules reproduce; disposal and period close are auditable |
| Cash / Bank / Cheques | Cash services, ChequeService, ReconciliationService, bank reconciliation window | Cash forecasts, bank statement import/matching, reconciliation rules, deposits, cheque lifecycle, bounced/replaced cheques, fees and FX differences | Reconciled statement balance ties to bank GL; unmatched items remain visible; cheque status changes post correct entries |
| CRM / Projects / Support | CrmService, ProjectService, SupportWin and related entities | Lead-to-opportunity-to-quote, activity history, project/task/time/expense/billing, support SLA and warranty | Sales pipeline converts without re-entry; project profitability reconciles to time, expenses and invoices; SLA aging is testable |
| HR / Payroll | HrService, HR entities and HR windows/reports | Employee lifecycle, attendance/shifts, leave accrual, payroll components, statutory deductions, loans/advances, payslip and GL posting | Payroll calculation reproducible; period approval/lock; employee privacy/role permissions; payroll journal balances |
| Tax / Compliance | TaxSvc and tax fields across documents | Tax groups, inclusive/exclusive pricing, withholding, recoverable/non-recoverable tax, tax returns and localization packs | Tax reports reconcile to GL by tax code and period; jurisdiction rules are configurable and tested |
| Analytics / Reporting | 63 report basenames and report infrastructure | Drill-down from KPI -> report -> source document, saved filters, scheduled exports, comparative periods, margin/stock/aging dashboards | Each report ties to source ledgers; date/company/branch filters are consistent; exports match on-screen totals |
| Platform / Security | Auth, Permissions, License, Backup, DataExchangeGuard, SchMig, Localization | Role/permission matrix, MFA option where suitable, encryption at rest/backup, tamper-evident audit, recovery, signed updater, license grace/offline activation, diagnostics | Clean-device install/upgrade/rollback; least-privilege tests; backup restore verified; secrets not stored as plaintext |
| Multi-company / Multi-branch | Company service and company-scoped repository patterns | Per-company data isolation, branches/warehouses, intercompany transactions, consolidated reporting, separate fiscal years/currencies | Cross-company leakage tests; intercompany balances reconcile; user access scoped to allowed companies/branches |

## 5. Live correctness gate — execution log (2026-11-19)

The most recent **completed** CI run inspected at this update is:
- Run: 44466340520 (PR merge ref; branch head at that time: `7c8c6d63fe673ba74fd7d17b130fc9112863fd4a`).
- Release build: succeeded with **0 warnings / 0 errors**.
- Tests: **307 passed, 9 failed, 316 total**.
- Newer branch commit `da3bf2fd024d35dc4cf5687a6084e6948311f69c` adds missing GL accounts to the sales-chain posting fixture; its CI run 44467664452 was still in progress when this log was written.
- Follow-up commit `ccc3942ea69d3cc9b893fe921e4c9d8e02273e16` corrects the manufacturing test migration executor so conditional ALTER directives are evaluated in migration order, after preceding CREATE statements. Its CI run 44468374819 was queued at this update.
- Therefore, neither follow-up is marked verified until its own CI run completes.

### Failure classification from run 44466340520

| Failure group | Evidence | Corrective action / state |
|---|---|---|
| Manufacturing fixture — duplicate `card_no` | Five MFG tests failed while applying migration 016; the fixture evaluated all conditional-column directives before executing the earlier CREATE TABLE statements in that same migration. | Fixed in `ccc3942`: execute migration segments in order, evaluate each directive against the schema created by preceding segments. CI pending. |
| Sales-chain GL account 301 | Posting failed because the fixture did not seed an active, postable sales revenue account. | Fixture now seeds 301, 1203 and 401 alongside receivables in `da3bf2f`. CI pending. |
| POS partial return settlement | `PosPosting.PartiallyUnsettleOriginal` received SQLite `no such column: debit` when reading party-ledger settlement rows. | Open. Confirm the exact schema actually present in the failing test database before changing SQL or adding a compatibility migration. Preserve append-only settlement mirrors and partial-refund arithmetic. |
| Invoice reversal settlement | `InvRepo.UnsettleOriginalOnReversal` received the same missing-`debit` error in two reversal tests. | Open. Trace migration ownership/order and the exact live table columns; do not mask this with a zero/default balance. |
| Sales invoice GL test | Sales return test failed through the reversal settlement path above. | Treat as part of the party-ledger schema defect; keep the stock valuation and mirrored GL assertions unchanged. |

The current known blocker is not merely test setup: the party-ledger schema seen by the failing reversal tests does not expose the `debit` column expected by the runtime query, despite the canonical SQLite commercial-document migration defining `debit` and `credit`. This discrepancy must be diagnosed at the database-creation/migration boundary. Add a schema assertion immediately after migration in the relevant fixtures and trace any pre-existing table/bootstrap path that could cause `CREATE TABLE IF NOT EXISTS` to preserve an incompatible table.

### Required evidence before closing Phase A

1. CI on the latest branch commit: Release build 0 warnings/errors and all tests passing.
2. SQLite schema assertions for `party_ledger`, warehouses and manufacturing tables after clean migration.
3. Fresh SQLite migration run and upgrade run from a representative pre-existing database.
4. PostgreSQL migration smoke run, including the same business schemas.
5. Reversal/partial-refund regression tests proving party ledger, allocations, paid amount, inventory and GL reconcile; do not delete posted financial history.

## 6. Delivery order

### Phase A — Correctness gate (current)
- Resolve all CI failures by root cause.
- Add regression tests for sales/purchase returns, partial settlement reversal, POS refund, manufacturing issue/output/cancel, multi-currency, permissions, and unit conversions.
- Verify both SQLite and PostgreSQL migration paths and accounting/stock reconciliation.
- Keep posted financial documents immutable; use reversal/credit-note flows with complete audit trail.

### Phase B — Core commercial workflows
- Finish Sales and Buying end-to-end document chains.
- Finish warehouse execution: reservations, picking/packing, batch/serial/expiry, counts, replenishment and costing.
- Finish POS shift and refund accounting.
- Connect all posting, payment, stock and tax effects to GL/party ledgers.

### Phase C — Enterprise operations
- Complete MRP, work centres/routings, capacity planning, WIP and production variance.
- Complete quality, maintenance, assets, HR/payroll, projects and support workflows.
- Add intercompany and multi-branch controls where supported by target customer needs.

### Phase D — Usability and reporting
- Group windows by business module without splitting large files unnecessarily.
- Complete Ribbon routes, permissions, resource dictionaries, localization, search/filter/validation and consistent layouts.
- Provide drill-down dashboards and reports that reconcile to source transactions.

### Phase E — Commercial release
- Installer, updater, uninstall, offline activation/licensing, backup/restore, secure credential handling, audit/logging, migration/rollback and documentation.
- Run acceptance on a clean Windows machine with realistic multi-company datasets and full period close.

## 7. Non-negotiable engineering rules

- Work only on `work/full-app-stabilization-2026-11-18`; do not merge into `main` without explicit approval.
- Prefer existing files/services and existing architecture. Do not split large files unless there is a concrete technical need.
- Keep source code, comments and default resource keys in English; user-visible translations belong in resource files.
- All schema definitions/migrations belong under `dba`; runtime service files must not own embedded table DDL.
- Never weaken accounting, stock, security or validation rules just to make tests pass.
- No feature is complete until UI, route, permissions, localization, database, service, reporting, reversal and automated tests are connected.
- A module marked implemented must be backed by a passing workflow test, not merely a class or window name.

## 8. Definition of done

ShouTech can be called commercially ready only after:
1. All automated tests pass on the release commit.
2. SQLite and PostgreSQL migration/integration suites pass.
3. Sales, buying, inventory, POS, manufacturing and finance end-to-end acceptance scenarios reconcile.
4. Security, permissions, audit, backup/restore and clean installation are verified.
5. No critical data-loss, duplicate-posting, cross-company leakage or untraceable reversal defect remains.
6. Release package and operator/admin documentation are complete.


## 9. Source-level workflow comparison: Odoo 19.0 and ERPNext 15

This review follows executable model/service code, not only product feature lists. The
purpose is to transfer business invariants into ShouTech's desktop/offline architecture;
it is not a plan to copy either product's web framework.

### Reference implementation paths

Odoo:
- `addons/sale/models/sale_order.py` — order confirmation, line-level delivery and invoice progress.
- `addons/stock/models/stock_move.py` — reservations, move completion and returned-move lineage.
- `addons/account/models/account_move.py` — posting controls, posted-entry audit state and reversal workflow.
- `addons/mrp/models/mrp_production.py` — production orders, component consumption and finished output.

ERPNext:
- `erpnext/selling/doctype/sales_order/sales_order.py` — submit-time validation and per-delivered/per-billed progress.
- `erpnext/buying/doctype/purchase_order/purchase_order.py` — per-received/per-billed quantities and linked-document status.
- `erpnext/stock/stock_ledger.py` — immutable stock ledger entries and future-entry reposting after backdated changes.
- `erpnext/manufacturing/doctype/work_order/work_order.py` — BOM-driven production, material transfer and operation/job-card lifecycle.
- `erpnext/accounts/doctype/payment_entry/payment_entry.py` and `erpnext/accounts/general_ledger.py` — allocation, outstanding updates and GL posting.

Source links:
- Odoo sale: https://github.com/odoo/odoo/blob/19.0/addons/sale/models/sale_order.py
- Odoo stock: https://github.com/odoo/odoo/blob/19.0/addons/stock/models/stock_move.py
- Odoo accounting: https://github.com/odoo/odoo/blob/19.0/addons/account/models/account_move.py
- Odoo manufacturing: https://github.com/odoo/odoo/blob/19.0/addons/mrp/models/mrp_production.py
- ERPNext sales: https://github.com/frappe/erpnext/blob/version-15/erpnext/selling/doctype/sales_order/sales_order.py
- ERPNext purchasing: https://github.com/frappe/erpnext/blob/version-15/erpnext/buying/doctype/purchase_order/purchase_order.py
- ERPNext stock ledger: https://github.com/frappe/erpnext/blob/version-15/erpnext/stock/stock_ledger.py
- ERPNext manufacturing: https://github.com/frappe/erpnext/blob/version-15/erpnext/manufacturing/doctype/work_order/work_order.py
- ERPNext payments: https://github.com/frappe/erpnext/blob/version-15/erpnext/accounts/doctype/payment_entry/payment_entry.py
- ERPNext GL: https://github.com/frappe/erpnext/blob/version-15/erpnext/accounts/general_ledger.py

### Business invariants to adopt

1. Every downstream document must retain its source document and source-line identity.
2. Delivered, received, invoiced and billed quantities are cumulative, line-level values;
   partial completion must not silently close the remaining quantity.
3. A posted source order cannot be cancelled while posted downstream delivery, receipt or
   invoice documents still depend on it. Reverse/cancel the child documents first.
4. Returns must point to the original movement/document where available and reverse the
   original cost/tax/settlement effect, not merely create a negative sales line.
5. Stock is valued from the stock ledger and valuation method; backdated movements must
   trigger deterministic replay of affected later movements.
6. Posted financial entries are append-only; correction is by a linked reversal with an
   auditable actor, reason and timestamp.
7. Payment allocation, party outstanding, cash/bank movement and GL posting must commit
   in one transaction or not commit at all.

### ShouTech source assessment

| Area | Verified foundation | Gap to close |
|---|---|---|
| Sales | `SalesChainService` supports quotation → order → delivery → invoice, including partial delivery quantities; `OrderDocumentRepository` calculates fulfilment/billing progress. | Surface partial states in the order UI and strengthen linked-document cancellation/closure rules. |
| Purchasing | `OrderService` supports RFQ/purchase-order workflows and purchase-invoice conversion; progress calculation already recognizes purchase receipts. | No complete purchase-receipt creation/posting service was found. Add PO → partial/full receipt → supplier invoice → payment, including received-not-invoiced (GRNI) accounting and no double stock receipt. |
| Inventory | `StockValuationEngine` supports weighted average/FIFO/LIFO, stock cost layers and replay; migrations 053/054 add valuation, batch and serial tracking. | Complete warehouse execution: location/bin-level picking and put-away, reservation visibility, replenishment and stock-count variance controls. |
| Manufacturing | `MfgOrderRepository` supports staged material issue, WIP, finished output, by-products/scrap and close/cancel paths. | Add routings, operation sequence, work centres, capacity/calendar planning, job cards and actual-vs-standard operation time/cost. |
| Accounting | Shared GL posting, base-currency amounts, FX rates, party ledger, payment allocations, budgets, cost centres and bank reconciliation are present. | Finish reversal/settlement regression coverage and reconcile all posting paths against the same accounting-period, currency and audit rules. |
| Fixed assets | Asset records and depreciation scheduling/posting are present. | Complete acquisition/capitalisation, transfer, disposal/sale, impairment and revaluation with linked GL entries and audit history. |
| POS | Sessions, held bills, split tenders, customer-on-account and return flows exist. | Complete cash-up/shift variance, tender reconciliation and return/refund edge-case acceptance tests. |

### Priority order

- **P0 — integrity:** all tests green; SQLite/PostgreSQL migration parity; posting, reversal,
  settlement, valuation and period-lock invariants.
- **P1 — purchasing:** implement Purchase Receipt and GRNI end-to-end before adding more
  purchasing screens; prevent duplicate receipt/invoice stock movements.
- **P1 — warehouse execution:** bin/location model, reservation and pick/put-away flows,
  replenishment and controlled cycle counts.
- **P1 — manufacturing execution:** routings, work centres, operation capacity and job cards.
- **P2 — fixed assets:** full asset lifecycle and depreciation/disposal/revaluation journals.
- **P2 — business visibility:** computed partial-delivery/receipt/billing states, source-to-ledger
  drill-down and exception work queues.
- **P3 — differentiators:** robust offline-first operation, safe multi-device synchronisation
  with conflict handling, verified backup/restore drills, keyboard-first workflows and
  explainable data-health checks.

### Product differentiation rule

ShouTech remains a native desktop ERP with local/offline operation. Odoo and ERPNext are
used as references for process correctness, auditability and breadth; their server/web
architecture is not copied. ShouTech should differentiate through reliable offline workflows,
fast keyboard-oriented entry, compact deployment, local data ownership, predictable recovery,
and consistent multilingual/RTL-LTR behavior. These differentiators are targets until their
acceptance tests and release packaging are complete.
