# ShouTech Developer Roadmap

> **Competitive benchmark:** The current full product comparison and evidence-based gap assessment is maintained in [COMPETITIVE_GAP_ASSESSMENT_2026-11-21.md](COMPETITIVE_GAP_ASSESSMENT_2026-11-21.md). Use it together with this staged delivery roadmap; it does not replace the engineering Definition of Done.


**Status:** Living engineering plan  
**Product:** ShouTech ERP Company  
**Active stabilization branch:** `work/full-app-stabilization-2026-11-18`  
**Last roadmap review:** 2026-11-20

This is the canonical forward-looking engineering roadmap. It describes the order in which ShouTech should be stabilized, completed, expanded, tested, and prepared for commercial release. It is not a claim that every listed capability is already implemented.

## 1. Product and engineering objective

Build a dependable, commercial, offline-first ERP platform with integrated accounting, inventory, sales, purchasing, CRM, POS, manufacturing, reporting, localization, and controlled AI assistance.

Use international products as references for business workflows and quality expectations, not as permission to copy proprietary source code or to transplant incompatible architectures. Preserve ShouTech's WPF desktop product direction and its supported SQLite and PostgreSQL providers.

The product benchmark is workflow correctness and operational completeness—not screen count, class count, table count, or marketing feature lists.

## 2. Non-negotiable engineering principles

1. **Accounting integrity:** posted financial documents are corrected through controlled cancellation/reversal documents. Never silently delete or rewrite posted history.
2. **Inventory integrity:** every stock movement has a source, destination where applicable, quantity, valuation effect, actor, timestamp, and idempotency/replay behavior.
3. **One business operation, one committed effect:** document posting, stock updates, ledger entries, tax snapshots, and outbox events must share a transaction or a rigorously recoverable workflow.
4. **Company isolation:** every company-owned record and operation must be scoped to the active company and tested against cross-company access.
5. **Tax is opt-in:** choosing a country does not activate tax. The company administrator must explicitly enable it. Declining must not create or apply tax codes, rates, postings, or reports automatically.
6. **Database ownership:** active table definitions belong in `dba/` provider migrations. Runtime services must not become an alternative schema-management system.
7. **Provider parity:** supported SQLite and PostgreSQL behavior must be tested. Do not assume SQL syntax, types, locking, date handling, or parameter semantics are interchangeable.
8. **Localization:** English is the source/default language. Do not hardcode Arabic or other translated UI text in code; use resource files. Preserve RTL and culture-aware formatting.
9. **Security by enforcement:** permissions must be checked in service/application boundaries, not only by hiding buttons or controls.
10. **Evidence-based completion:** a feature is complete only after its end-to-end workflow, persistence, authorization, audit, localization, provider behavior, and automated tests pass.

## 3. Delivery sequence and release gates

### Gate 0 — Stabilize the existing product

**Priority:** P0 · **Release blocker**

- Resolve all current Windows build, test, and database migration CI failures.
- Establish a reproducible clean build and repeatable test baseline.
- Verify clean-install and upgrade migrations on SQLite and PostgreSQL.
- Audit critical financial posting paths for duplicate posting, unbalanced journals, missing source links, incorrect tax snapshots, and partial commits.
- Verify reversal/cancellation behavior and prevent destructive edits to posted documents.
- Verify company isolation, permissions, backup/restore, and audit logging.
- Remove or explicitly document dead, duplicate, or misleading execution paths.

**Exit criteria:** clean build; all required tests green; migrations apply twice safely; core ledger and stock reconciliation fixtures balance; no known P0 data-loss or security defect.

### Phase 1 — Accounting and commercial document lifecycle

**Priority:** P0/P1

- General ledger, chart of accounts, fiscal periods, period locks, year close, recurring journals, dimensions/cost centres, budgets.
- Sales lifecycle: lead/quotation/order/delivery/invoice/receipt/return/credit note.
- Purchase lifecycle: requisition/RFQ/quotation/order/receipt/supplier invoice/payment/return/debit note.
- Document numbering, status transitions, partial fulfillment, source-document links, approvals, and audit.
- Multi-currency, realized/unrealized FX, rounding, receivables/payables ageing, bank reconciliation.
- Tax activation and effective-dated rules with jurisdiction-specific configuration.

**Exit criteria:** representative complete and partial workflows reconcile to the GL, subledgers, stock, cash/bank, and tax reports; retries do not duplicate effects; reversal paths are tested.

### Phase 2 — Inventory, warehouse, and POS

**Priority:** P1

- Multi-warehouse and bin/location balances, reservations, transfers and in-transit stock.
- FIFO/weighted-average valuation policy, landed costs, deterministic recalculation and stock count adjustments.
- Batch/lot/serial/expiry tracking, multiple units, barcodes, replenishment and shortage planning.
- POS sessions, cash drawer, multiple tenders, discounts/overrides, refunds, exchanges, offline operation and idempotent synchronization.
- Cash-up, shift close, Z reports, settlement and reconciliation.

**Exit criteria:** quantities and values reconcile by company/item/warehouse/location/lot; POS retries and offline recovery do not duplicate sales, payments, or movements.

### Phase 3 — Manufacturing and operational ERP

**Priority:** P1

- Versioned multi-level BOMs, routings, work centres and capacity.
- MRP, work orders, material issue/return, partial output, WIP, scrap/by-products and subcontracting.
- Actual versus standard costing, overhead/labour allocation and explainable variances.
- Quality inspections, non-conformance, corrective action, maintenance and equipment history.
- Fixed assets, depreciation, disposal, transfer and GL reconciliation.
- HR/payroll, attendance, leave and statutory calculations with country-specific packages.

**Exit criteria:** production quantities, WIP, cost layers, inventory valuation and accounting entries reconcile across normal, partial, failed, cancelled and reversed scenarios.

### Phase 4 — CRM, analytics, integrations and productivity

**Priority:** P1/P2

- Lead-to-opportunity-to-quotation conversion and customer activity history.
- Projects, tasks, timesheets, expenses, billing, support cases and service-level tracking.
- Report builder, parameterized queries, pivot/grouping, dashboards and drill-through.
- Import/export staging, validation previews, row-level errors, retry-safe commits and attachment management.
- Scoped API tokens, webhooks, provider adapters, durable inbox/outbox, retries and dead-letter handling.
- Per-user grid layouts, keyboard navigation, saved filters and consistent window behavior.

**Exit criteria:** every displayed KPI drills to authorized source records; imports are previewable and repeatable; integration retries are idempotent and observable.

### Phase 5 — AI-assisted business operations

**Priority:** P2 · **After trusted data and permissions**

- Natural-language report requests that compile to approved, parameterized, permission-aware queries.
- Document extraction for invoices and receipts with confidence scores and human approval.
- Forecasting for cash flow, demand, stock replenishment and sales, with assumptions and data lineage.
- Anomaly detection for duplicate invoices, unusual journals, pricing and inventory movements.
- Assistant actions only through authorized application services, explicit confirmation for consequential actions, and complete audit records.
- Retrieval from approved company documents with company isolation, access filtering, source citations and retention controls.

**Exit criteria:** AI cannot bypass permissions, invent postings, silently execute consequential actions, expose another company’s data, or present uncertain extraction as verified fact. Every action is reviewable and auditable.

### Phase 6 — Commercial release and operations

**Priority:** P0 before sale

- Signed installer and upgrade path, versioning, licensing and entitlement checks.
- Backup encryption, automated schedules, integrity verification, restore drills and retention.
- Diagnostics/support bundle, structured logs, error catalog, health checks and privacy-safe telemetry.
- Performance/load tests, concurrency tests, security review, dependency and vulnerability scanning.
- Upgrade compatibility, rollback/recovery instructions, deployment profiles and operator handbook.
- Release notes, migration notes, known limitations, support policy and reproducible build artifacts.

**Exit criteria:** release candidate passes the full acceptance matrix on clean and upgraded databases, supported operating environments, and realistic company datasets. No release is declared ready solely from a successful compile.

## 4. Priority definitions

| Priority | Meaning | Handling |
|---|---|---|
| P0 | Data integrity, security, build, migration, or release blocker | Fix before expanding dependent features |
| P1 | Core commercial workflow required for credible ERP operation | Complete after P0; test end-to-end |
| P2 | Advanced workflow, productivity, automation, or AI enhancement | Build on stable P0/P1 foundations |
| P3 | Optional integration or specialized capability | Deliver when demand, provider contract, and support model are clear |

## 5. Definition of done for every feature

A feature may be marked **Done** only when all applicable items are satisfied:

- [ ] Business purpose, actors, preconditions, states, and exception paths are documented.
- [ ] UI is connected to the real application/service path; no fake success or placeholder persistence.
- [ ] Validation exists at the service boundary as well as in the UI.
- [ ] Permissions and company scope are enforced outside the presentation layer.
- [ ] Database changes are versioned in `dba/mig/sqlite` and `dba/mig/postgresql`.
- [ ] Transactions, retries, idempotency, concurrency, and failure recovery are addressed.
- [ ] Accounting, inventory, tax, and settlement effects are correct where applicable.
- [ ] Audit history records actor, event, time, entity/source, and meaningful context.
- [ ] English resources exist; all supported translations and RTL behavior are checked.
- [ ] Unit, integration, migration, and end-to-end tests cover success and failure paths.
- [ ] Developer docs, user-facing help, and release notes are updated.
- [ ] CI evidence is linked and the feature is reviewed against its acceptance criteria.

Use **Not started**, **In progress**, **Blocked**, **Implemented — unverified**, **Verified**, and **Done** consistently. Do not label a feature Done based only on a source file or UI window.

## 6. Required developer workflow

1. Start from the approved stabilization/development branch; do not commit directly to `main`.
2. Read `README.dev.md`, `ARCHITECTURE.md`, this roadmap, and the relevant module document.
3. Trace the complete existing path from UI to service/use case, repository, schema, and tests before changing it.
4. Write or update acceptance criteria and identify affected providers and modules.
5. Make the smallest coherent implementation; avoid broad rewrites without an approved migration plan.
6. Add provider migrations before depending on new schema.
7. Add tests for success, validation failure, permission denial, rollback, retries, and provider parity.
8. Run targeted tests, full solution build/tests, migration smoke tests, and language/source checks.
9. Update module status, architecture diagrams, docs index and changelog in the same change.
10. Open a pull request with evidence, limitations, migration impact, and rollback notes.

## 7. Canonical supporting documents

- [Developer Handbook](DEVELOPER_HANDBOOK.md) — coding, testing, database, security, localization and review rules.
- [Module Catalog](MODULE_CATALOG.md) — module boundaries, dependencies and evidence required for completion.
- [Engineering Reference Library](ENGINEERING_REFERENCE_LIBRARY.md) — external standards and product documentation to consult.
- [Competitive ERP Roadmap](COMPETITIVE_GAP_ASSESSMENT_2026-11-21.md) — broader process gap assessment.
- [ERP Process Parity Matrix](COMPETITIVE_GAP_ASSESSMENT_2026-11-21.md) — process-level benchmark.
- [ERPNext Parity Roadmap](COMPETITIVE_GAP_ASSESSMENT_2026-11-21.md) — ERPNext concepts and comparison.
- [Manager.io Benchmark](COMPETITIVE_GAP_ASSESSMENT_2026-11-21.md) — accounting/productivity benchmark.
- [Phenix 10 Roadmap](COMPETITIVE_GAP_ASSESSMENT_2026-11-21.md) — published capability benchmark.
- [Architecture Refactoring Plan](REFACTOR.md) — current architecture transition and file disposition.
- [Database Migrations](DATABASE_MIGRATIONS.md) — migration workflow and safeguards.
- [Accounting Reversals and Outbox](ACCOUNTING_CHART_PERIODS_YEAR_CLOSE.md) — posting and delivery invariants.
- [Multi-country Tax Roadmap](MULTI-COUNTRY-TAX-ENGINE-ROADMAP.md) — jurisdiction package requirements.
- [Window and Ribbon Inventories](WINDOW-INVENTORY.md), [Ribbon Completion Inventory](RIBBON_COMPLETION_INVENTORY.md) — UI coverage and gaps.

## 8. Roadmap maintenance

- Update this roadmap whenever a phase, priority, release gate, or architectural rule changes.
- Record status changes with evidence: commit/PR, test run, migration verification, and known limitations.
- Keep historical assessments dated; do not silently rewrite an old baseline as if it were current.
- Review P0/P1 status at every stabilization milestone and all phase exits.
- If implementation reality conflicts with documentation, inspect the code and tests, then correct the documentation in the same change.
