# Phenix 10 Competitive Benchmark and ShouTech Upgrade Roadmap

**Target branch:** `work/full-app-stabilization-2026-11-18`  
**Benchmark source:** Official Phenix Systems / Phenix Software product pages and Phenix 10 release article  
**Assessment date:** 2026-11-20  
**Purpose:** Convert publicly documented Phenix 10 capabilities into a verifiable ShouTech ERP upgrade program. This is a product benchmark, not a claim that either product has been independently audited.

## 1. Benchmark scope and evidence rules

The primary benchmark is the vendor's published Phenix 10 feature article (dated 2023-12-24), supplemented by current Phenix product pages for accounting, inventory, POS, and manufacturing. Public pages describe marketed capabilities; they do not disclose every implementation detail, limitation, license tier, or current deployment status.

ShouTech must not claim parity merely because a window, class, service, or database table exists. A capability is accepted only when its complete workflow is implemented, persisted, permission-checked, auditable, localized, tested against SQLite and PostgreSQL where supported, and verified through end-to-end tests.

Status vocabulary:
- **Existing foundation:** related ShouTech components are present in the audited source inventory; end-to-end acceptance remains required.
- **Partial / verify:** a related workflow exists but coverage, correctness, or usability is not proven.
- **Gap / not evidenced:** the current audited inventory does not establish the capability.
- **New differentiator:** an enhancement ShouTech should implement beyond the published Phenix 10 description.

## 2. Phenix 10 published feature inventory

### 2.1 User interface and productivity
- User-selectable themes and dark mode.
- Detachable windows outside the main window and a multi-window workflow.
- Keyboard navigation between open windows (Ctrl+Tab); Shift/Ctrl modifiers for separate or comparison windows.
- Attachments that can contain arbitrary content.
- Resizable/zoomable table columns and ascending/descending sorting with sorting disabled when needed.
- Dashboard widgets from reports, including columns and charts.
- Main workspace split horizontally or vertically.
- Quick functions for frequently used cards and operations.
- Concurrent window/report work through a multithreaded window system.

### 2.2 Accounting and operational controls
- Minimum and maximum permitted currency values.
- A common-field lookup workflow linking invoices and vouchers.
- Invoice import/export with the warehouse management application.
- Visibility of stock balances across warehouses from material lookup inside invoices.
- Account credit limits enforced during invoicing.
- Voucher/invoice reconciliation during head-office/branch synchronization.
- Branch-specific account mapping for customer assets, unified customer accounts, cash closing, and cash differences.
- Transaction-level profit percentage in document information.

### 2.3 Integrations and connected operations
- Microsoft Power BI integration.
- E-commerce integrations including Salla, Shopify, WooCommerce, and Wix.
- Local kitchen printing from cloud/restaurant ordering workflows.
- Cloud ordering experiences including e-menu and drive-through applications.
- Supporting mobile applications for warehouse inventory, receiving, delivery, and synchronization.
- Driver/delivery company setup with commission by fixed amount, percentage, or both; GPS tracking is also described.

### 2.4 Reports and analytics
- All built-in reports can be included in custom reports.
- Custom field types such as text, dropdowns, and images in selected report types (Pro tier).
- Purchase planning report.
- Inventory movement analysis that evaluates turnover and movement over a period rather than relying only on a no-movement flag.
- Tree/grouped presentation of reports by a selected column.
- Sharing reports through WhatsApp.
- Pivot, aggregate, and chart capabilities, with permissions governing access.

### 2.5 Productivity, approvals, and security
- Administrative approvals and HR approvals.
- Full 64-bit execution and faster synchronization.
- Synchronization of item masters between branches.
- Printing image attachments and using user-card data in print templates.
- Appointment scheduling, permission controls, and manager notifications.
- Clipboard copy/paste restrictions for tables and reports.
- User-specific column visibility and table-feature restrictions.
- User working days and hours.
- Granular permissions to edit columns, create custom columns, copy/share data, and use pivot/aggregate/chart features.
- Import of manufacturing templates.

### 2.6 Product-suite capabilities advertised by Phenix
The vendor also markets accounting, inventory, restaurants, POS, manufacturing, fixed assets, HR, clinics, pharmacies, sales representatives, cloud, mobile applications, and integrations. Inventory pages specifically describe multi-unit support, product specifications, multiple barcodes, serial numbers, expiry dates, stock counts, reorder/shortage alerts, branch transfers, and landed-cost components. Manufacturing pages describe production tracking, BOM/model definition, material planning, production scheduling, workforce control, cost measurement, and cost variance reporting. POS pages describe barcode sales, unlimited POS points/users/items, cash-session reporting, peak-time analysis, cashier permissions, and stock-count integration.

## 3. ShouTech baseline and competitive position

ShouTech remains an offline-first WPF desktop ERP using SQLite and PostgreSQL. Its architecture must stay appropriate for a desktop product; do not transplant web-only Phenix or Frappe implementation patterns.

The existing ShouTech ERP process-parity inventory records foundations for GL, AR/AP, inventory, POS, sales chains, manufacturing, quality, fixed assets, cash/bank, CRM, HR, tax, reports, security, and multi-company. It also explicitly marks many workflows as partial or still requiring end-to-end verification. See `ERP-PROCESS-PARITY-MATRIX.md` for the detailed ERPNext/Odoo process audit.

Current product strengths to preserve and extend:
- Desktop/offline-first operation with SQLite and PostgreSQL support.
- Explicit accounting and stock posting paths, with reversal/return workflows rather than destructive edits to posted history.
- Company isolation, role/permission foundations, and a central service/repository architecture.
- Existing sales-chain, POS, manufacturing, quality, fixed-asset, payroll, and reporting foundations.
- A shared WPF visual identity and central window infrastructure.

Critical quality rule: fix and stabilize existing financial and inventory workflows before adding broad surface area. A feature that can create duplicate GL entries, orphan source links, inconsistent stock, or unreconciled settlements is not a competitive feature.

## 4. Feature-by-feature ShouTech upgrade matrix

| Phenix capability | ShouTech baseline evidence | Target upgrade | Priority |
|---|---|---|---|
| Themes and dark mode | Theme resources exist; full user preference flow not proven | Central theme service, per-user persisted preference, complete control/template coverage, contrast/accessibility tests | P2 |
| Detachable and multi-window UI | WPF main-window/navigation foundations exist | Window host supporting tabbed, detached, modal, and comparison modes without duplicate data contexts | P2 |
| Ctrl+Tab / keyboard productivity | Existing navigation utilities | Global command routing, predictable focus, shortcut map, keyboard-only acceptance tests | P2 |
| Attachments | Data exchange and document foundations | Typed attachment service, previews, metadata, checksums, versioning, access control, backup/restore | P2 |
| Resizable/sortable grids | WPF grids and report infrastructure exist | Persist per-user/per-view column order, widths, visibility, sorting, filters, grouping and reset-to-default | P1 |
| Report-to-dashboard widgets | Dashboard/report foundations need verification | Safe widget catalog, parameterized queries, per-user dashboards, refresh policy, permission-aware drill-down | P1 |
| Split workspace and quick actions | Main window/Ribbon foundations | Workspace split and pinned quick actions while preserving the agreed two-bar main-window structure | P2 |
| Currency min/max | Multi-currency foundation exists | Currency precision/range rules, effective dates, per-company validation, explicit override permission and audit | P1 |
| Invoice/voucher common-field lookup | Document and voucher foundations exist | Cross-document finder with exact/partial search, source links, status, amount, date and audit context | P1 |
| Invoice/WMS exchange | Import/export foundation exists | Versioned exchange contract, idempotent imports, preview/validation, conflict report and retry-safe sync | P1 |
| Cross-warehouse balances in invoice lookup | Stock services/reports exist | Fast availability matrix by warehouse, reserved/available/on-hand quantities and freshness timestamp | P1 |
| Credit limits | Credit guard foundation exists | Configurable hard/soft limits, overdue exposure, pending orders, user overrides, approvals and audit | P1 |
| Branch-specific account mapping | Multi-branch/accounting foundations need verification | Effective-dated branch mappings, cash closing/difference accounts, unified customer ledger, mapping validation | P1 |
| Profit margin in document | Valuation and invoice data foundations exist | Show gross margin and margin percent using actual valuation, landed costs, discounts, tax treatment and permissions | P1 |
| Power BI | No accepted connector evidence in the current audit | Read-only, least-privilege analytics export/API, stable semantic model and incremental refresh | P3 |
| Salla / Shopify / WooCommerce / Wix | No accepted production connector evidence | Provider adapters, webhook inbox/outbox, mapping, idempotency, retry/dead-letter handling and reconciliation | P3 |
| Custom reports and custom fields | Report designer and custom reports exist | Extend all permitted report sources; typed calculated fields, validation, safe query model and versioning | P1 |
| Purchase planning | Purchasing foundation exists | Demand/supply planning with lead times, reorder policy, open PO/receipt visibility and suggested quantities | P2 |
| Inventory turnover analysis | Stock ledger/valuation/report foundations exist | Period-based turnover, days on hand, ABC/XYZ, dead/slow/fast movers, seasonality and drill-down | P1 |
| Tree/grouped report view | Report infrastructure exists | Reusable grouping/tree engine, subtotal correctness, expand/collapse, export fidelity | P1 |
| WhatsApp report sharing | No accepted integration evidence | User-approved share/export workflow with privacy controls; no silent transmission of financial data | P3 |
| Pivot/aggregate/chart reports | Reporting infrastructure exists | Reusable pivot engine, saved layouts, aggregation validation, chart drill-through and permission enforcement | P1 |
| Administrative and HR approvals | Approval foundations exist in parity inventory; coverage needs verification | Configurable approval matrices, amount thresholds, delegation, escalation, separation of duties and immutable history | P1 |
| 64-bit and concurrency | .NET 8 build exists; packaging/runtime profile needs verification | x64 packaging, asynchronous I/O, UI-thread isolation, bounded concurrency and performance benchmarks | P2 |
| Branch synchronization | Sync foundations need end-to-end verification | Offline-first outbox/inbox, conflict policies, idempotency keys, voucher/document reconciliation and observability | P1 |
| Print templates and attachments | Print infrastructure exists; full metadata binding needs verification | Template versioning, user/company metadata, attachment rendering, PDF preview and deterministic output tests | P2 |
| Delivery commissions/GPS | Delivery-related foundations need verification | Commission rules, route assignments, mobile sync, consent-aware location tracking and retention controls | P3 |
| Appointments and notifications | Appointment foundations need verification | Calendar resources, permissions, conflict detection, reminder queue and notification audit | P3 |
| Clipboard and column permissions | RBAC foundation exists | Field/column-level policy enforced in UI and export/service layers, not UI hiding alone | P1 |
| User working schedules | User/security foundation exists | Effective-dated schedules, time-zone handling, grace periods, emergency override and audit | P2 |
| Manufacturing template import | Manufacturing and data exchange foundations exist | Versioned BOM/template import, preview, unit validation, substitution mapping and rollback-safe commit | P1 |
| Batch/serial/expiry and landed costs | Batch/serial and valuation foundations exist | Full traceability, FEFO/FIFO policy, recalls, landed-cost allocation and valuation reconciliation | P1 |
| POS cashier/session analytics | POS foundations exist | Session lifecycle, cash-up, tender variance, peak periods, cashier permissions and offline replay tests | P1 |

## 5. ShouTech differentiators required to beat the benchmark

The goal is not to match a marketing checklist. ShouTech should exceed it in the following areas:

1. **Accounting integrity by construction**
   - Every posted source document has exactly one linked accounting posting, with balanced lines and source traceability.
   - Idempotent posting and retry; no duplicate GL, stock, tax, or settlement effects.
   - Reversal/return documents preserve originals, use the original account mapping where required, and reconcile party, cash, stock, valuation, and GL effects.
   - Fiscal-period locks, audit events, and controlled reopening.

2. **Offline-first branch synchronization**
   - Durable local outbox/inbox, idempotent delivery, explicit conflict resolution, and branch-level reconciliation.
   - Synchronization must survive restart, network loss, duplicate messages, clock skew, and partial failure.
   - Never silently overwrite posted financial or inventory history.

3. **Inventory traceability and costing**
   - Lot/batch, serial, expiry, multi-unit equivalence, warehouse/bin, reservation, and stock availability at document-entry time.
   - Deterministic valuation and landed-cost allocation with replayable valuation history.
   - FEFO/FIFO/average policies where appropriate, plus traceable recalls and expiry alerts.

4. **Approval and segregation-of-duties engine**
   - Rules based on document type, value, margin, credit exposure, warehouse, branch, user, and exception.
   - Maker/checker controls, delegation, escalation, expiry, and tamper-evident approval history.
   - Enforce permissions inside services and repositories, not only by hiding controls.

5. **Analytics that reconcile**
   - Every dashboard KPI drills down to its source documents and ledger movements.
   - Inventory turnover, gross margin, aged debt, cash forecast, purchase planning, production variance, and branch performance.
   - Report definitions are versioned, permission-aware, and tested against known accounting fixtures.

6. **Commercial-grade reliability**
   - Automated backup verification and restore drills, migration rollback strategy, health diagnostics, structured logs, error catalog, and upgrade recovery.
   - Performance budgets for large ledgers, stock cards, reports, and multi-window sessions.
   - Installer, update channel, licensing, support diagnostics, and documented deployment profiles.

## 6. Delivery sequence and release gates

### Phase 0 — Stabilization gate (must precede feature expansion)
- Resolve all current CI failures and warnings that indicate correctness or maintainability risk.
- Verify invoice reversal GL, party-ledger, stock, valuation, and settlement symmetry.
- Verify sales-chain atomicity, source-link integrity, partial quantities, and retry behavior.
- Keep SQLite/PostgreSQL schema behavior equivalent.
- Exit gate: clean Release build; all tests pass; no unresolved high-severity accounting/stock defects; CI green on the development branch.

### Phase 1 — High-value daily workflow improvements
- Persist grid layouts and column permissions.
- Add cross-document finder for invoices, vouchers, orders, receipts, and returns.
- Add warehouse availability matrix to material lookup in invoices.
- Strengthen credit exposure and margin visibility.
- Add period-based inventory turnover analysis.
- Exit gate: UI/service integration tests, permission tests, performance test on large datasets, and exact reconciliation with source ledgers.

### Phase 2 — Approvals, reports, and dashboards
- Complete administrative and HR approval matrices.
- Expand custom report designer, pivot/group/tree views, and dashboard widgets.
- Add report drill-through, saved user layouts, and export parity.
- Exit gate: every widget/report enforces company, branch, and user permissions and reconciles to the underlying ledger.

### Phase 3 — Branch and external integrations
- Build a durable offline synchronization engine with conflict and reconciliation workflows.
- Add versioned API/provider adapters for Power BI and e-commerce integrations.
- Add controlled report sharing and delivery integrations only after consent, authorization, and audit are implemented.
- Exit gate: duplicate webhook/retry/network partition tests, replayability, and reconciliation across branch databases.

### Phase 4 — Inventory, manufacturing, POS, and operational depth
- Complete batch/serial/expiry, reservations, landed costs, purchase planning, and mobile WMS workflows.
- Complete manufacturing revisions, scheduling, WIP, partial completion, scrap/by-products, and actual-vs-standard variance.
- Complete POS session/cash-up, multi-tender, offline retry, returns and cashier analytics.
- Exit gate: end-to-end stock valuation and GL reconciliation across all workflows.

### Phase 5 — Enterprise readiness and release
- Complete HR, fixed assets, tax localization, audit, backup/restore, installer, licensing, and support tooling.
- Run security, performance, migration, restore, and multi-company isolation test suites.
- Produce operator/admin documentation and a migration guide.
- Exit gate: release candidate accepted against a published commercial acceptance checklist.

## 7. Non-negotiable engineering rules

- Continue on `work/full-app-stabilization-2026-11-18`; do not merge into `main` without explicit approval.
- Keep code, comments, SQL identifiers, and resource keys in English. All user-facing text belongs in localization resources.
- Keep table DDL in `dba` migration scripts. Runtime services must not create or mutate schema.
- Do not split large source files without a measured maintainability reason.
- Keep the WPF desktop architecture; integrations must use explicit interfaces/adapters.
- Every schema change needs SQLite and PostgreSQL migrations plus upgrade tests.
- Every financial or stock workflow needs positive, negative, retry, concurrency, cancellation/reversal, and reconciliation tests.
- Do not mark a phase complete until its acceptance gate passes in CI.

## 8. Official benchmark sources

- Phenix 10 feature article (English): https://phenixsoft.com/en/blog/phenix-10-features
- Phenix 10 feature article (Arabic): https://phenixsoft.com/index.php/ar/blog/phenix-10-features
- Phenix product overview: https://www.phenixsystems.com/
- Phenix accounting: https://phenixsystems.com/services/products/accounting
- Phenix inventory: https://phenixsystems.com/services/products/Inventory
- Phenix POS: https://www.phenixsystems.com/services/products/pos
- Phenix manufacturing: https://www.phenixuae.com/en/manufacturing-accounting-software/
- Phenix inventory mobile application: https://phenixsoft.com/index.php/en/app/inventory

The vendor feature article is dated 2023-12-24. Current product pages may describe a broader product suite than the exact Phenix 10 release. Validate edition, regional availability, and licensing with the vendor before treating every listed capability as included in every Phenix 10 installation.
