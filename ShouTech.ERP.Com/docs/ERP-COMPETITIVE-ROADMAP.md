# ShouTech ERP Competitive Gap Assessment and Delivery Roadmap

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

## 5. Immediate verified blockers from current CI

Latest inspected workflow run:
- Run: 44456677597
- Commit: dd96625861a78d697d78b102a4a7b6bbdf2112b2
- Result: 302 passed, 12 failed, 314 total.

Failures grouped by root cause:
1. Party-ledger reversal SQL assumes columns named `debit` / `credit` in the source settlement table; SQLite reports `no such column: debit` in POS refund and invoice reversal paths. Inspect the actual POS settlement schema and the party-ledger schema separately; do not guess or rename columns blindly.
2. One sales-chain posting fixture lacks active/postable account 1201 for the company.
3. Manufacturing test fixture has no opening stock for raw material RM-1; seed the test stock through the supported inventory schema, preserving insufficient-stock rollback tests.
4. Manufacturing fixture expects a `permissions` table that is absent from its minimal migrated database; either run the proper security migration or inject the intended permission provider in the fixture, without bypassing production authorization.
5. Unit-of-measure legacy round-trip fixture violates a foreign key; repair parent/child seed order and valid company references.
6. Sales-return GL test expected 40 but actual is 100; investigate source invoice/return quantity and valuation basis before changing the assertion or posting algorithm.

Do not mark the branch green until all 12 failures are classified and fixed, all tests pass on the resulting commit, and Debug/Release builds complete.

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
