# Manager.io Competitive Benchmark and ShouTech Upgrade Plan

**Target branch:** `work/full-app-stabilization-2026-11-18`  
**Assessment date:** 2026-11-20  
**Benchmark scope:** Public Manager.io feature pages, user guides, release notes, and the public repository README/license notice.  
**Purpose:** Identify proven workflow and usability ideas that can strengthen ShouTech ERP. This document does not claim that every feature is available in every Manager edition or localization.

## 1. Product positioning

Manager is a capable small-business accounting product with an offline desktop edition and a self-hosted server edition. Its documented scope covers general ledger, cash/bank, AR/AP, sales and purchasing documents, inventory, production orders, payroll, fixed assets, tax, multi-currency, and extensive reporting. The vendor's public guides are particularly useful because they explain operational workflows, not just menu names.

Manager is a useful benchmark for workflow simplicity and broad accounting coverage. ShouTech's intended target is a broader commercial/institutional ERP, so the goal is to take the strongest patterns and implement them with deeper warehouse, manufacturing, POS, branch, audit, and enterprise controls—not to copy its architecture or source.

## 2. Manager capabilities verified in public documentation

### 2.1 Financial accounting and treasury
- General ledger, journal entries, chart of accounts, cash management, receipts, payments, inter-account transfers, and bank reconciliation.
- Accounts receivable/payable, customer and supplier statements, aged receivables/payables, remittance advice, late-payment fees, credit notes and debit notes.
- Accrual and cash-basis accounting, multi-currency, exchange-rate handling, VAT/GST/sales tax, withholding tax, and lock dates.
- Bank statement imports and bank rules; cash-flow reporting and forecasts.
- Comparative financial statements, project/division accounting, and capital accounts.

### 2.2 Sales and purchasing lifecycle
- Sales and purchase quotes, orders, invoices, delivery notes, goods receipts, credit notes, debit notes, and recurring transactions.
- Document conversion/copy workflows (for example quote → order → invoice, or order → delivery/receipt) and status tracking.
- Billable time and billable expenses that can flow into invoicing.
- Customer/supplier portals are listed in the current guide navigation; implementation scope and edition availability should be checked before treating them as baseline requirements.

### 2.3 Inventory and production
- Inventory items, inventory kits, inventory locations, transfers, write-offs, reorder points, quantity-to-order, quantity-to-deliver, and quantity-to-receive.
- Item-level valuation policy documented for FIFO and weighted average.
- Inventory costing worksheets/corrections and cost drill-down to underlying transactions.
- Production orders, production stages, input material consumption, finished-goods output, and allocation of non-inventory production costs.
- Inventory quantity/value by location, inventory profit margin, price lists, and costing calculation reports.

### 2.4 Payroll and assets
- Employees and payslips, configurable payslip items, and recurring payslips.
- Fixed assets, depreciation entries, intangible assets, amortization entries, and supporting asset reports.
- Attachments and custom fields for many master and transaction types.

### 2.5 Reporting and data analysis
- Standard financial statements: Profit & Loss, Balance Sheet, Cash Flow, Statement of Changes in Equity, and Trial Balance.
- General-ledger transaction and summary reports, aged AR/AP, customer/supplier statements, tax reports, inventory quantity/value/location, inventory margin, fixed asset and depreciation reports, payroll reports, and forecasts.
- Advanced Queries: user-defined select columns, filters, sorting, grouping, and saved queries against tabular data.
- Custom Reports: query-based reports using ledger data, calculations, filters, ordering, and grouping.
- Editable column layouts and copy-to-clipboard workflows.

### 2.6 Productivity, automation, and administration
- Batch Create, Batch Update, Batch View, and Batch Delete workflows, including spreadsheet copy/paste with a review step before commit.
- Custom fields on many master records, transaction headers, and transaction lines.
- Form defaults, recurring transaction templates, email templates, PDF output, attachments and attachment folders.
- User permissions, business localization, date/number formats, themes, divisions, projects, and accounting lock dates.
- Access Tokens and API endpoints for integrations and automation.
- Transaction history/audit features.
- Desktop mode works offline for one user; the self-hosted server edition supports multiple users through a browser. Public documentation describes backup/restore and moving business data between devices.

## 3. ShouTech capability mapping

The ShouTech repository already has a broad WPF desktop foundation: reporting designer/repository, custom reports window, formula engine, many operational report windows, batch/serial support, sales-chain services, POS, manufacturing, payroll/HR, fixed assets, multi-company, SQLite/PostgreSQL migrations, and API documentation. Presence of code is not acceptance evidence; every workflow below still needs service, persistence, permissions, localization, and end-to-end verification.

| Manager capability | ShouTech evidence in audited tree | Upgrade action for ShouTech | Priority |
|---|---|---|---|
| GL, AR/AP, cash/bank, journals | Accounting services, reports, and voucher flows exist; correctness still under stabilization | Complete source-linked posting, reconciliation, reversals, period locks, bank reconciliation, and ledger integrity tests | P0 |
| Quotes/orders/invoices/returns/delivery/receipts | Sales-chain and commercial-document foundations exist | Complete document conversion graph, partial fulfillment, return/refund settlement, idempotent posting, and audit trail | P0 |
| Recurring transactions | Not proven by the current file inventory | Add scheduler-backed templates with preview, next-run, pause, catch-up policy, idempotency, and approval | P2 |
| Multi-currency and tax | Currency/tax foundations exist | Verify rate sourcing, realized/unrealized FX, rounding, tax components, withholding, localization, and tax reconciliation | P1 |
| Bank reconciliation/import | Bank reporting exists; complete reconciliation workflow not proven | Add statement import adapters, duplicate detection, matching rules, split matching, unmatched queue, and audit | P1 |
| FIFO / weighted average costing | Stock valuation foundation exists; policy parity not proven | Make valuation policy explicit and versioned per company/item; add replayable cost layers/average calculation and reconciliation worksheet | P0 |
| Reorder point and demand | Shortage/stock reports exist | Add min/max, lead time, safety stock, supplier MOQ, open supply/demand, and purchase suggestions | P1 |
| Inventory locations and transfers | Warehouse and transfer foundations exist | Complete bin/location-level stock, reservation, two-sided transfer, in-transit balances, and traceability | P1 |
| Production orders/stages | Manufacturing foundations and cost/variance reports exist | Complete BOM revisions, routing/stages, WIP, partial output, scrap/by-products, labor/overhead allocation, and variance | P1 |
| Payroll and fixed assets | HR/payroll and fixed asset foundations exist | Verify lifecycle end-to-end: payroll accrual/payment, depreciation schedules, disposal, revaluation, and GL reconciliation | P1 |
| Custom fields | Not established as a universal typed metadata framework | Build safe typed custom fields for master/header/line/report with validation, indexing, migration, localization, and permissions | P2 |
| Advanced Queries | Report designer/formula engine/custom report window exist | Add a safe metadata-driven query builder (select/filter/sort/group/aggregate), saved views, parameterization, and permission-aware field catalog | P1 |
| Custom reports | Report designer, models, repository, renderer exist | Expand data sources to ledger and operational facts; add reusable calculations, report transformations, versioning, and drill-through | P1 |
| Batch create/update | Batch/serial capture exists; generic CRUD batch operations not proven | Add spreadsheet template export, staging preview, validation summary, row-level errors, atomic/partial modes, and retry-safe commit | P1 |
| Batch delete | Soft-delete/recycle-bin foundations exist | Batch soft-delete only; require safeguards for posted records and preserve audit/restore semantics | P2 |
| Form defaults and copy-to | Invoice and document forms exist | Central per-user/per-company defaults, copy-to conversions preserving custom fields and validating source/target semantics | P2 |
| Attachments | Not proven as a complete shared service | Add content store, metadata, checksum, preview, versioning, permissions, retention, and backup integration | P2 |
| API access tokens | API documentation exists | Token issuance/revocation, scopes, expiry, secret hashing, rate limits, audit, idempotency, and integration tests | P1 |
| Audit history | Deletion audit exists | Extend to field-level before/after events, actor, timestamp, correlation ID, source document, and tamper-evident retention | P1 |
| Business localization and multi-business | Multi-company/language foundations exist | Verify company isolation, per-company fiscal settings, regional tax packs, RTL, date/number formats, and cross-company authorization | P1 |
| Offline and multi-user modes | Desktop/offline WPF app with SQLite/PostgreSQL | Keep offline-first; provide optional secured LAN/server sync with durable outbox/inbox and conflict resolution | P1 |
| Backup/restore | Backup and restore windows exist | Add automated backup schedules, integrity verification, encryption, restore drills, retention policies, and disaster-recovery tests | P1 |

## 4. Features ShouTech should implement better than Manager

1. **True ERP-wide document graph:** every quote, order, delivery, invoice, return, receipt, payment, production order, and stock movement should retain explicit source/target links, partial quantities, status transitions, and audit history.
2. **Stronger warehouse operations:** multi-warehouse/bin stock, reservations, lot/batch/serial/expiry, FEFO, barcode workflows, replenishment, in-transit transfers, stock count variance approval, and traceable recalls.
3. **Manufacturing depth:** multi-level BOMs, versioned routings, work centers, capacity planning, labor and overhead, WIP, co-products/by-products, scrap, subcontracting, and actual-vs-standard variance.
4. **Enterprise approvals:** maker/checker, amount/margin/credit/warehouse/branch thresholds, delegation, escalation, separation of duties, and exception audit.
5. **Branch-first offline synchronization:** durable event queues, idempotency, conflict workflows, branch reconciliation, and restart/network-partition tests.
6. **Reconciled analytics:** every KPI drills down to underlying source documents and ledger/stock movements; no dashboard number should be a disconnected cache.
7. **Commercial deployment:** installer/updater, licensing, diagnostics, verified backups, migration recovery, support bundle, and deployment documentation.

## 5. Implementation order

### Gate 0 — Correctness before expansion
- Resolve all active CI failures.
- Correct invoice reversal account mapping and prove mirror postings.
- Verify sales/purchase source links and partial delivery/receipt allocations.
- Verify SQLite/PostgreSQL behavior and transaction ownership.
- Gate: Release build and full test suite pass; accounting/stock reconciliation fixtures are green.

### Phase 1 — Accounting and stock controls
- Bank reconciliation and statement import.
- Explicit FIFO/weighted-average policy and cost correction worksheet.
- Reorder planning, warehouse availability and reservations.
- Returns/credit/debit notes with settlement and source traceability.

### Phase 2 — Report and productivity platform
- Advanced query builder and reusable custom report engine.
- Saved grid layouts, custom fields, form defaults, copy-to, batch import/update preview.
- Drill-through from all financial and inventory KPIs.

### Phase 3 — Automation and integrations
- Recurring document templates and posting schedule.
- Scoped API tokens, audit events, webhook/outbox patterns.
- Secure attachments, email/PDF workflows, external accounting/e-commerce integrations.

### Phase 4 — Manufacturing and enterprise operations
- Multi-level BOM/routing/WIP and capacity planning.
- Approval matrices, branch synchronization, and enterprise access control.
- Payroll/fixed asset/tax lifecycle completion and commercial deployment readiness.

No phase is complete until its acceptance criteria pass in CI and its persisted data survives restart, backup, and restore tests.

## 6. Source-code and license caution

The public `managerhq/Manager` repository labels Manager as **source-available, not open source**, under the Functional Source License 1.1 / Apache 2.0 Future License. The repository README states that the license restricts competing use, including offering a commercial substitute, until the relevant version transitions to Apache 2.0 two years after release.

ShouTech is intended to be a competing commercial ERP. Therefore:
- Use Manager's public feature descriptions and user guides as product/workflow research.
- Do **not** copy, port, translate, adapt, or derive ShouTech implementation code from Manager's repository unless a qualified license review confirms the exact code/version is permitted for this use.
- Do not copy UI assets, branding, text, or distinctive product presentation.
- Implement independent designs from the requirements in this benchmark and ShouTech's own architecture.
- Review third-party notices and dependencies separately if any source is ever considered.

## 7. Official sources

- Manager feature overview: https://www2.manager.io/features/
- Manager guides index: https://www2.manager.io/guides
- Reports and custom reports: https://www.manager.io/guides/reports/
- Advanced Queries: https://www.manager.io/guides/advanced-queries/
- Custom Fields: https://www2.manager.io/guides/8941
- Inventory item form and valuation methods: https://www.manager.io/guides/inventory-item-form/
- Inventory management introduction: https://www2.manager.io/guides/11111
- Inventory locations: https://www.manager.io/guides/custom-inventory-locations/
- Inventory unit costs: https://www.manager.io/guides/inventory-unit-costs/
- Recurring transactions: https://www.manager.io/guides/recurring-transactions/
- Batch operations: https://www.manager.io/guides/batch-operations/
- Access tokens/API: https://www.manager.io/guides/access-tokens/
- Release history: https://www.manager.io/releases/
- Public source repository and license notice: https://github.com/managerhq/Manager

## 8. Benchmark caveats

Manager's feature pages and guides are vendor-published documentation, not an independent security, accounting, or performance audit. Some capabilities may vary by version, edition, or localization. Verify availability and behavior in a current Manager installation before treating a detail as a universal product guarantee.
