<!-- source: docs/COMPETITIVE_GAP_ASSESSMENT_2026-11-21.md -->

# ShouTech ERP — Full Competitive Gap Assessment and Product Roadmap

**Assessment date:** 2026-11-21  
**Repository:** `Roni4422/ShouTech`  
**Target branch:** `work/full-app-stabilization-2026-11-18`  
**Reviewed branch head:** `ceb293b34e873c78cf0dcddcf8da38abd1c0f86b`  
**Product direction:** Offline-first Windows desktop ERP, with SQLite local operation and PostgreSQL support where the current application path supports it.  
**Purpose:** Establish what ShouTech already has, what remains unproven or missing compared with well-known ERP/accounting products, and the measurable work required before commercial release.

> **Executive conclusion:** ShouTech has a broad and credible implementation foundation, but source-file breadth is not equivalent to product parity. The highest-value work is not adding more windows. It is completing connected business lifecycles, proving accounting and stock integrity, closing purchasing and warehouse execution gaps, making tax packs legally supportable, and completing release-grade security, recovery, integration and usability. ShouTech must not be marketed as a complete replacement for Manager.io, ERPNext, Odoo, Al-Ameen 10 or Phenix 10 until the relevant acceptance gates in this document have passed.

---

## 1. How this assessment was performed

### 1.1 Evidence rules

This assessment distinguishes four kinds of evidence:

| Status | Meaning | What it does not mean |
|---|---|---|
| **Foundation present** | Relevant source files, services, views, migrations or tests exist in the audited branch. | It does not prove the workflow is complete or safe. |
| **Partial / verify** | Some working path exists, but important scenarios, providers, permissions, recovery or UI behavior are not fully evidenced. | It does not prove parity. |
| **Gap / not evidenced** | The current reviewed inventory does not establish a working capability. | It does not prove the feature is impossible or absent from every branch. |
| **Verified for stated scope** | A named test or CI job passed for a specific commit and scenario. | It does not certify all related workflows or commercial readiness. |

A capability is **Done** only when its real UI path, application/service logic, persistence, company scoping, permissions, audit, localization, reports/exports, exception handling, reversal/recovery and automated tests work together. Both SQLite and PostgreSQL must pass where both are claimed as supported.

### 1.2 Branch inventory snapshot

At reviewed head `ceb293b`, the recursive repository tree contained the following source-file counts:

| Inventory item | Count | Interpretation |
|---|---:|---|
| C# view/window-related source files under the current Views path | 161 | File count only; some files may be helpers, base classes or view models. |
| Service source files | 48 | Indicates broad service surface, not verified service completeness. |
| SQL migration files under SQLite and PostgreSQL migration paths | 131 | Indicates schema history volume, not schema correctness by itself. |
| C# test files matching the repository test naming filter | 39 | File count only; not the number of tests or their pass rate. |

These counts are a structural snapshot, not a score. Earlier inventories in the repository refer to different branches and dates; do not carry their Ribbon button counts or view counts forward without re-auditing the exact branch head.

### 1.3 Current CI evidence at the assessment point

- The SQLite migration smoke job on `ceb293b` completed successfully, including clean application, repeat application and existing migration-history checks.
- The PostgreSQL migration smoke job on `ceb293b` completed successfully, including clean application, repeat application and existing migration-history checks.
- Windows CI for `ceb293b` had completed restore and was still in the build step when this assessment was prepared. Its build and test result must therefore be treated as **pending**, not passed.
- Earlier commits have successful CI results, but those results do not certify the later branch head.

Live run links:
- Windows CI: https://github.com/Roni4422/ShouTech/actions/runs/44866779094
- Database migration smoke tests: https://github.com/Roni4422/ShouTech/actions/runs/44866779054

### 1.4 Reference products

This assessment uses publicly documented product capabilities as workflow benchmarks:

1. **Manager.io** — small-business accounting with general ledger, cash/bank, reconciliation, AR/AP, quotes/orders/invoices, inventory, fixed assets, payroll, manufacturing, reports, multi-currency, custom fields and history.
2. **ERPNext** — broad open-source ERP covering accounting, HR, CRM, manufacturing, procurement, sales, stock, assets, projects, POS, quality, support and developer APIs.
3. **Odoo** — integrated application suite spanning finance, sales, CRM, POS, subscriptions, ecommerce, website, inventory, manufacturing, PLM, purchase, maintenance, quality, HR, marketing, projects, helpdesk and customization.
4. **Al-Ameen 10** — Arabic-market accounting and warehouse benchmark; its vendor markets branches and warehouses, live stock updates, ZATCA e-invoicing, barcode/QR, expiry and serial tracking, approvals, POS, manufacturing, distribution, HR, cloud/local deployment and scheduled backup.
5. **Phenix 10** — regional benchmark for accounting, warehouse, POS, manufacturing, mobile/field sales, approvals, reporting and integrations. Public marketing claims recorded in `PHENIX10-COMPETITIVE-ROADMAP.md` require vendor demonstration and scope confirmation before being treated as verified product behavior.

**Comparison limitation:** Vendor feature pages are marketing/product descriptions, not independent technical audits. This document compares advertised workflows and published documentation; it does not claim to have installed and tested every competing product or verified every license tier.

---

## 2. Competitor-by-competitor comparison

### 2.1 Manager.io

**What Manager sets as a practical benchmark**

- General ledger, cash management, bank reconciliation, expense claims, AR/AP and customer/supplier statements.
- Quotes, sales orders, sales invoices, credit notes, delivery notes, purchase orders, purchase invoices, debit notes and goods receipts.
- Inventory items, locations, transfers, write-offs, production orders and inventory costing reports.
- Fixed assets and depreciation, employees and payslips, projects, multi-currency and tax codes.
- Custom fields, custom reports, comparative reports, drill-down and transaction history.
- Desktop, server and cloud deployment editions; the desktop edition is single-user while server/cloud editions support shared access.

**ShouTech comparison**

ShouTech has a broader visible module surface in areas such as CRM, POS, manufacturing, quality, HR, tax setup, reporting and desktop navigation. It also has SQLite/PostgreSQL migration paths and a native WPF desktop direction.

The most important parity gaps are not basic invoice screens. They are complete bank statement import and matching, reliable bank reconciliation, full sales and purchase document chains, purchase receipt/GRNI, asset lifecycle, recurring transactions, report customization/drill-down, history completeness and a dependable desktop-to-shared-server operating model.

**Required ShouTech upgrades**

1. Finish bank statement import (CSV and common bank formats), transaction matching, split/partial matching, fees and FX differences.
2. Finish receipts/payments allocation, advances, overpayments, refunds and settlement reversal.
3. Complete sales and purchase lifecycle progress, including delivery notes, goods receipts, returns, credit/debit notes and partial completion.
4. Complete asset acquisition, capitalization, depreciation, transfer, impairment, revaluation and disposal.
5. Complete report customization, saved filters, comparative periods and drill-through.
6. Prove multi-company data portability and safe backup/restore, including audit history.
7. Decide and document the boundary between single-user local SQLite and multi-user PostgreSQL deployments; do not imply desktop SQLite is a multi-user server database.

### 2.2 ERPNext

**What ERPNext sets as a practical benchmark**

- Accounting, tax, receivables/payables, payment entries, budgets, cost centres, fixed assets and consolidated financial reporting.
- Procurement lifecycle: material request, supplier quotation, purchase order, purchase receipt, purchase invoice and payment.
- Sales lifecycle: lead/opportunity, quotation, sales order, delivery, sales invoice, payment and returns.
- Stock ledger, warehouses, stock reconciliation, FIFO/moving-average valuation, batches, serial numbers, stock reservations, landed costs and reposting after backdated changes.
- Manufacturing with multi-level BOM, MRP, production plan, work orders, job cards, operations, capacity, subcontracting, scrap and quality checks.
- CRM, projects, timesheets, HR, payroll, POS, quality, support/helpdesk, assets and developer API.

**ShouTech comparison**

ShouTech has foundations for GL, party ledgers, sales chain, purchasing orders, inventory valuation/recalculation, batch/serial capture, POS, manufacturing, planning, quality, assets, projects, CRM, HR and reporting. Existing code includes services and repositories for many of these domains.

The critical gaps identified by the current process-parity documents include:
- Purchase requisition, budget/need validation and multi-level approval before RFQ/PO; supplier invoice external-reference uniqueness and attachment management; landed-cost allocation; complete lot/serial/expiry receipt traceability; and GRNI aging/exception reporting.
- Warehouse bin/location execution, picking, put-away, reservations and replenishment.
- Full manufacturing execution: routings, work centres, job cards, capacity and actual-versus-standard labor/overhead costing.
- Complete stock ledger replay/reconciliation and batch/serial/expiry acceptance.
- Complete asset lifecycle and all payroll/localization scenarios.
- Reliable bank reconciliation, payment allocation and settlement reversal.
- Service/helpdesk SLA and project profitability end-to-end evidence.

**Required ShouTech upgrades**

1. Complete the upstream procurement-control path: purchase requisitions, budget/need checks, approval matrices, segregation of duties, and conversion of approved requests into RFQs or purchase orders.
2. Make stock ledger entries the authoritative auditable source for quantity and value; test FIFO and weighted average, backdated entries, reposting and deterministic replay.
3. Complete warehouse execution with bins, pick/put-away, reservations, replenishment, cycle count and traceable movements.
4. Add multi-level, revision-controlled BOM; routings; work centres; job cards; MRP; capacity calendars; WIP; scrap/by-products; subcontracting; actual cost and variance.
5. Complete quality gates at receipt, production and dispatch, including non-conformance and corrective action.
6. Add complete source-to-ledger drill-through and period close acceptance.
7. Publish a stable developer API and integration contracts; protect them with scoped credentials and audit.

### 2.3 Odoo

**What Odoo sets as a practical benchmark**

- Integrated finance, accounting, invoicing, expenses, documents and signatures.
- CRM, sales, POS, subscriptions, rental and sales operations.
- Website builder, ecommerce, blog, forum, live chat and eLearning.
- Inventory, manufacturing, PLM, purchase, maintenance and quality.
- Employees, recruitment, time off, appraisals, payroll, fleet and referrals.
- Marketing automation, email/SMS/social marketing, events and surveys.
- Projects, timesheets, field service, helpdesk, planning and appointments.
- No-code customization and a large application ecosystem.

**ShouTech comparison**

ShouTech's present product direction is a focused native desktop ERP, not a clone of Odoo's broad web business-app suite. It already has CRM, POS, manufacturing, inventory, HR, projects, quality, assets and reporting foundations. It does not yet demonstrate the breadth of Odoo's website/ecommerce, marketing, communications, eLearning, rental, PLM, field-service, helpdesk and customization ecosystem.

These should be treated as deliberate product-scope decisions, not automatically as release blockers for an offline-first accounting/ERP desktop product.

**Required ShouTech upgrades**

1. Prioritize deep integration of existing finance, sales, stock, POS, purchasing, manufacturing, assets and HR modules before adding a large app catalogue.
2. Implement a safe extension/plugin contract so future vertical modules can be added without coupling them to core accounting.
3. Build optional customer/supplier portal and ecommerce connectors only after the Integration Hub and idempotent sync foundations exist.
4. Add project timesheets, expense capture, service tickets and SLA tracking if the target customer segment requires service operations.
5. Add configurable approval/workflow rules and custom fields without permitting unsafe SQL or bypassing service-layer validation.
6. Provide API documentation, versioning, authentication, rate limiting, audit and backward compatibility.

### 2.4 Al-Ameen 10

**What Al-Ameen 10 sets as a regional benchmark**

The vendor's published material advertises:
- Accounting, branches and multiple warehouses managed from one control surface.
- Real-time inventory updates with sales transactions.
- Saudi ZATCA e-invoicing support, including Phase 2 marketing claims.
- Detailed cost centres and customizable financial reports.
- Barcode and QR stock counts, warehouse transfers, expiry and serial tracking.
- Unlimited users, detailed permissions and approval workflows.
- Local or cloud operation and scheduled backups.
- Sales, purchase, orders, POS, manufacturing and distribution.
- Fixed assets, HR and payroll offerings.

**ShouTech comparison**

ShouTech has the right foundations for many of these areas: warehouse and material cards, barcode windows, stock transfer/count paths, tax setup, POS, manufacturing, reports, users/permissions, assets, HR and backup.

However, the following are not yet established as fully accepted:
- ZATCA production compliance, authority connectivity, signing/clearance/reporting behavior and certification scope.
- Branch synchronization and voucher reconciliation under real offline/online interruption.
- Full batch/serial/expiry traceability from receipt through issue/return.
- Approval matrices with separation of duties and immutable decisions.
- Scheduled backup with automated restore drills and verifiable recovery point/objective.
- Consistent end-to-end integration of cost centres and financial statements.
- Regional payroll and tax rules verified against current laws.

**Required ShouTech upgrades**

1. Build Saudi ZATCA as a separate regulatory connector with versioned schemas, sandbox/production profiles, signing/certificate handling where required, QR/XML payload validation, submission status, retries, rejection handling, archive and audit.
2. Test branch-level offline operation, duplicate prevention, conflict resolution and reconciliation before marketing branch synchronization.
3. Complete expiry/serial/batch traceability and FEFO/FIFO issue policy.
4. Implement approval workflow rules for purchase, payment, discount override, journals, period reopen and sensitive master-data changes.
5. Complete scheduled encrypted backups, restore validation, retention and operator alerts.
6. Build jurisdiction-specific HR/payroll and tax packs with dated legal review.

### 2.5 Phenix 10

**Benchmark scope**

The existing `PHENIX10-COMPETITIVE-ROADMAP.md` records publicly advertised Phenix capabilities, including:
- Multi-window and keyboard productivity, themes, report dashboards and user-specific table behavior.
- Credit limits, multi-warehouse stock visibility, branch-specific account mappings and invoice/voucher reconciliation.
- Power BI and ecommerce integrations.
- Purchase planning, stock turnover analysis, grouped/pivot reports and report sharing.
- Approval workflows, HR approvals, manufacturing template import, appointments and notifications.
- Mobile/van sales, POS, restaurant e-menu, delivery/driver commissions and GPS field-force features.
- Vendor-advertised Saudi, Jordanian and Egyptian tax/e-invoicing integrations.

**ShouTech comparison**

ShouTech has broad desktop windows, report infrastructure, CRM, POS, stock, manufacturing, HR, tax and import/export foundations. However, the existence of these components does not establish parity with mobile field sales, cloud restaurant ordering, external ecommerce, Power BI, GPS tracking, advanced approvals or branch synchronization.

**Required ShouTech upgrades**

1. Add a central workflow/approval engine with thresholds, sequential/parallel approvals, delegation, escalation, rejection/resubmission and separation-of-duties controls.
2. Add Integration Hub adapters for ecommerce, Power BI/analytics and external providers; each connector must have mapping, credentials, health, retry, idempotency and reconciliation.
3. Add mobile/van-sales only as a governed client of the same catalog, price, stock, numbering, tax and posting services.
4. Add consent-based GPS/visit tracking only with role restrictions, retention rules, audit and privacy controls.
5. Complete report pivot/grouping, saved layouts, dashboards, drill-through and export parity.
6. Add branch synchronization only after durable inbox/outbox, conflict policy and reconciliation are proven.

---

## 3. Cross-product capability matrix

The table summarizes benchmark expectations and ShouTech's evidence-based state. It is intentionally conservative.

| Capability | Manager.io | ERPNext | Odoo | Al-Ameen 10 / Phenix 10 | ShouTech assessment | Priority |
|---|---|---|---|---|---|---|
| GL, journals, financial statements | Core | Core | Core | Core | Foundation present; critical posting/reversal and reconciliation tests remain | P0 |
| AR/AP, allocation, ageing | Core | Core | Core | Core | Partial / verify allocations, advances, refunds, settlement reversal and ageing | P0 |
| Sales lifecycle | Quotes/orders/invoices/credit notes/delivery | Lead-to-cash | CRM-to-cash | Sales/orders/invoices | Foundation present; partial delivery, linked states, returns and full reconciliation need acceptance | P1 |
| Purchase lifecycle | Quotes/PO/receipts/bills/debit notes | Request-to-pay | Purchase-to-pay | Purchase/orders | Partial; Purchase Receipt and GRNI are critical gaps | P1 |
| Bank statement import/reconciliation | Documented | Core | Core | Common benchmark | Foundation present; import formats, matching, partial match, fees and FX require proof | P1 |
| Multi-currency / FX | Core | Core | Core | Expected | Foundation present; realized/unrealized FX and cross-module rounding need end-to-end tests | P1 |
| Stock ledger and valuation | Inventory costing reports | FIFO/Moving Average and ledger | Valuation and traceability | Real-time stock and landed cost | Valuation/replay foundations present; complete ledger reconciliation and backdated scenarios unverified | P0/P1 |
| Bins, pick/put-away, reservations | Location transfers | Full stock execution | Advanced routes/operations | Warehouse operations | Gap / partial; implement location execution and reservations | P1 |
| Batch/serial/expiry | Product tracking varies by workflow | Batch/serial/expiry | Lots/serials | Advertised | Foundation present; lifecycle traceability, recall and expiry controls need tests | P1 |
| POS sessions/refunds | Not primary focus | POS | POS | POS/restaurant | Foundation present; offline retry, cash-up, multi-tender and refund reconciliation need tests | P1 |
| Manufacturing | Production orders | MRP, BOM, work orders, job cards | MRP, work centres, PLM | Manufacturing | Foundation present; routing/capacity/job cards/subcontracting and actual costing are gaps | P1 |
| Quality and maintenance | Limited compared with broad ERP suites | Quality/maintenance | Quality/maintenance | Industry-specific claims | Foundation present; gate enforcement, corrective actions and maintenance costing need proof | P2 |
| Fixed assets | Assets/depreciation | Full asset lifecycle | Assets | Advertised | Foundation present; acquisition/disposal/impairment/revaluation acceptance remains | P1 |
| CRM / projects / support | Projects | CRM/projects/helpdesk | CRM/projects/helpdesk | CRM/service claims vary | Foundation present; conversion, SLA and project profitability require tests | P2 |
| HR/payroll | Employees/payslips | HR/payroll | HR/payroll | HR/payroll | Partial / verify statutory country rules, approval and GL integration | P1/P2 |
| Tax localization / e-invoicing | Configurable tax codes | Global tax/compliance | Fiscal positions/localizations | Strong regional claims | Tax opt-in foundation exists; legal packs and authority connectors are not complete | P0 for claims; P1 delivery |
| Custom reports / dashboards | Custom reports/drill-down | Reports/dashboards | Spreadsheet/BI/dashboards | Pivot/grouped reports advertised | Reporting foundation present; builder, pivot, drill-down and saved user layouts need proof | P1 |
| Audit/history | History file with record-level changes | Document/ledger audit | Chatter/audit controls | Approval and audit claims vary | Security audit foundation exists; complete immutable business audit coverage not proven | P0 |
| Multi-company / branch | Multiple businesses | Multi-company | Multi-company | Branches / cloud | Company isolation foundations exist; branch synchronization and consolidation require proof | P0/P1 |
| Offline operation | Desktop offline | Usually server-centric | Deployment-dependent | Local/cloud options advertised | Core product differentiator; interruption, sync and recovery tests are required | P1 |
| Integrations / API | Access tokens/extensions | Developer API | Broad app ecosystem | External integrations advertised | Foundation partial; stable public API, Integration Hub and provider adapters remain | P2 |
| Mobile / ecommerce / portals | Browser editions, not a full Odoo-style app suite | Portal/web ecosystem | Broad native app catalogue | Mobile/van-sales/ecommerce claims | Not evidenced as complete; optional strategic scope after core stabilization | P3 |
| Installer/update/recovery | Desktop/server deployment | Server deployment tooling | Hosted/on-prem deployment | Local/cloud and scheduled backup claims | Release packaging, signed update, recovery and restore drills need acceptance | P0 |
| Documentation/help | Built-in guides | Extensive user/developer docs | Extensive documentation | Vendor training/support | User guide/F1 and developer docs foundations exist; coverage must follow released workflows | P1 |

---

## 4. What ShouTech already has — evidence inventory

The following are **foundations visible in the current repository**, not blanket declarations of completion:

### 4.1 Platform and database
- WPF desktop application and shared main-window/base-window infrastructure.
- SQLite and PostgreSQL migration directories and a standalone migration tool.
- Company, authentication, permission, licensing, backup, localization and data exchange services.
- Migration smoke tests for both database providers.
- Document-number sequence allocation and database-specific schema-validation cache work in the stabilization branch.

### 4.2 Finance and commercial operations
- Accounting/journal/GL posting services and chart-of-accounts/reporting windows.
- Sales invoices, quotations/orders/delivery foundations, party ledgers, payment/receipt workflows and credit guard foundations.
- Purchasing/order foundations and supplier ledger/reporting.
- Cash, bank, cheques, payment reconciliation and accounting report windows.
- Tax settings, rule editor, tax snapshots, tax GL posting and tax report foundations.

### 4.3 Inventory and operations
- Material/category/group/warehouse cards, barcodes, transfer, adjustment and stock reports.
- Stock posting and valuation/recalculation foundations.
- Batch/serial capture and picker foundations.
- POS sessions, checkout, return, sync and cash reporting windows.
- BOM, manufacturing orders, planning and manufacturing reports.
- Quality inspection/alerts/templates and maintenance foundations.

### 4.4 Other modules
- CRM leads/opportunities/activities/pipeline.
- Projects and support foundations.
- Fixed asset service/window foundations.
- HR employee, organization, attendance, leave and payroll foundations.
- Dashboards, custom report windows, export/print services and F1 help foundations.

**Evidence source:** Current source tree plus `ERP-PROCESS-PARITY-MATRIX.md`, `MODULE_CATALOG.md`, `DEVELOPER_ROADMAP.md`, `PHENIX10-COMPETITIVE-ROADMAP.md` and the domain-specific implementation status documents.

---

## 5. The real gaps that prevent ShouTech from matching the leading products

### 5.1 P0 — Integrity, security and release blockers

These items must be closed before expanding the feature surface.

#### A. Posting must be atomic or recoverable

- A single business posting must not leave GL, stock, tax, party ledger, settlement, cash and source-document status in conflicting states.
- Cross-engine workflows such as a stock engine committing separately from the commercial document status require a durable state machine and reconciliation worker.
- The new `Posting` state is a useful safety boundary, but it can remain stuck after a process interruption. A visible stuck state is safer than falsely claiming Draft or Posted; it is not the final recovery solution.
- Add a reconciliation queue that can inspect source document, engine document, stock movements, GL effects and posting claims; determine whether to finalize, compensate or require manual approval.
- Never blindly retry a document whose stock or GL side effects may already have committed.

**Acceptance:** inject process failures after each durable step; restart; reconcile to exactly one valid final state; assert no duplicate GL, stock, tax or settlement effects.

#### B. Finish the CI correctness gate

- Windows Release build and full test suite must pass on the same exact release commit.
- SQLite and PostgreSQL migrations must pass clean install, repeat application and representative upgrade-from-existing-database scenarios.
- Add migration schema assertions for critical tables after each test fixture setup.
- Publish the exact commit SHA, test totals, skipped tests and failure logs.
- No feature is marked Verified based on older CI results.

**Acceptance:** one immutable commit has green build, all required tests, SQLite and PostgreSQL smoke tests and no unexplained skips.

#### C. Transactional accounting and reversal integrity

- Every source posting must have a stable source key and idempotency protection.
- GL debits and credits must balance in both transaction currency and base currency where applicable.
- Reversal must preserve the original document, original ledger rows and source links.
- Partial refunds/returns must reverse only the relevant quantity/value/settlement; full reversal must net correctly.
- Period locks, closed-year controls and backdated posting policy must apply consistently to every entry path.
- Reconcile control accounts to customer/supplier schedules and stock accounts to valuation.

**Acceptance:** automated end-to-end matrix for cash/credit sales, purchases, returns, partial/full payments, tax, multi-currency, discounts, charges, POS refunds and reversal.

#### D. Security and audit completeness

- Enforce RBAC and company/branch scope in service/application boundaries, not only by hiding UI buttons.
- Audit all material changes and sensitive actions, including before/after values or a safe structured delta, actor, timestamp, company, source document, reason and correlation ID.
- A best-effort audit write is not equivalent to immutable transactional audit. Critical audit events must be transactionally committed or written through a durable outbox.
- Test backup encryption, secret handling, credential rotation, lockout, least privilege and restore.
- Add MFA/stronger authentication options where deployment and customer requirements justify them; never weaken the user's configured password policy.

**Acceptance:** authorization tests for every critical service; cross-company and cross-branch isolation tests; tamper detection; audit survives restore and business-record deletion.

#### E. Commercial deployment and recovery

- Signed installer and signed update packages.
- Clean install, upgrade, rollback/recovery and uninstall behavior.
- Encrypted backup, scheduled backup, retention, integrity verification and restore drill.
- Diagnostic bundle with privacy-safe logs and actionable error codes.
- License activation, offline grace/recovery and clear entitlement behavior.
- Performance and concurrency testing on realistic company datasets.

**Acceptance:** a clean Windows machine can install, create a company, operate offline, back up, restore to another machine and upgrade without losing accounting history.

### 5.2 P1 — Core ERP workflows

#### A. Complete purchase receipt and GRNI

Current purchase-order foundations do not by themselves prove a complete procure-to-pay cycle.

Required:
- Requisition/RFQ/supplier quote/PO.
- Purchase Receipt with partial receipt and source-line identity.
- Received-not-invoiced (GRNI) accounting.
- Supplier invoice matching against received quantities/values.
- Over/under delivery tolerance and authorized override.
- Purchase return, debit note and supplier credit.
- Landed-cost allocation and subsequent valuation adjustments.

**Acceptance:** partial and full receipt/billing, over/under receipt, return-before-bill and return-after-bill; AP, GRNI, stock quantity/value, tax and GL reconcile.

#### B. Warehouse execution and traceability

Required:
- Warehouse hierarchy and bin/location model.
- Put-away, picking, packing, dispatch and internal transfer with in-transit state where needed.
- Reservations against sales/manufacturing orders.
- Replenishment and reorder policies using lead time and open supply.
- Lot/batch/serial/expiry traceability through receipt, transfer, production, sale, return and recall.
- FEFO where expiry applies; FIFO or other configured issue policy where required.
- Cycle counting and controlled adjustment approval.
- Negative-stock policy configurable by company/item/warehouse with explicit override permission.

**Acceptance:** quantity and valuation reconcile by company, item, warehouse, bin, batch/serial and date; traceability can identify affected receipts and customers for a recall.

#### C. Complete sales and receivables lifecycle

Required:
- Lead/opportunity/quotation/order/delivery/invoice/receipt.
- Partial delivery, backorder, partial invoice, advance and payment allocation.
- Credit limit and overdue exposure; soft/hard blocks and audited override.
- Price lists, customer-specific pricing, promotions, discounts, freight/extra charges and tax.
- Sales return, credit note, refund and replacement.
- Dunning/collection workflow and customer statement.

**Acceptance:** every document tracks ordered, delivered, invoiced, paid and returned quantities/amounts; no child document can cause parent closure while residual quantity remains.

#### D. POS and cash management

Required:
- Multiple POS profiles, tills, devices and warehouses.
- Shift open/close, opening float, cash drops, paid-in/out and cashier variance.
- Cash/card/bank/wallet/on-account split tenders.
- Offline checkout queue, safe replay, idempotent synchronization and conflict handling.
- Refund, exchange, void-before-post, price override and discount approval.
- Z report, settlement reconciliation and cashier performance.
- Device/payment-provider adapters with safe credential storage.

**Acceptance:** offline sale replay, duplicate submission, partial refund, split tender, shift close and payment-provider timeout do not duplicate stock, cash, payment or GL effects.

#### E. Manufacturing execution

Required:
- Versioned multi-level BOM and engineering change history.
- Routings, operations, work centres, shifts/calendars and capacity.
- Demand planning and MRP with lead times, safety stock and open purchase/production supply.
- Work orders, job cards, material reservation/issue/return, partial completion and WIP.
- Scrap, by-products, rework and subcontracting.
- Actual material, labor and overhead costing; standard-vs-actual variance.
- Quality gates at incoming, in-process and finished-goods stages.

**Acceptance:** planned and actual material/cost, output quantity, WIP, scrap, stock valuation and GL reconcile for partial, cancelled, failed and completed jobs.

#### F. Finance depth

Required:
- Bank statement import and reconciliation rules.
- Partial/multiple allocation, advances, on-account receipts/payments and write-offs.
- Realized/unrealized FX and currency revaluation.
- Budgets, commitments and actual-vs-budget reporting.
- Cost-centre/project/branch dimensions enforced on all relevant transactions.
- Recurring invoices/journals and scheduled transactions.
- Fiscal period close, lock dates, year-end close and controlled reopening.
- Cash-flow forecast and receivables/payables ageing.

**Acceptance:** bank statement ending balance matches reconciled bank GL; all subledgers tie to control accounts; closed periods reject unauthorized changes.

#### G. Assets, HR and tax localization

**Fixed assets**
- Complete acquisition/capitalization, depreciation books/methods, additions, transfers, impairment/revaluation, disposal and gain/loss.
- Asset maintenance and physical custodian/location history.
- Asset register reconciles to GL.

**HR/payroll**
- Employee lifecycle, contracts, shifts, attendance, overtime, leave accrual, loans/advances, benefits, deductions, approvals and payslips.
- Country-specific statutory calculations and effective dates.
- Payroll privacy, approval and period lock.
- Payroll journals reconcile to liabilities and cash/bank.

**Tax**
- Keep tax opt-in per company. Country selection alone must not activate tax.
- Complete each jurisdiction as a legally reviewed, versioned package: registration thresholds, tax types, inclusive/exclusive pricing, exemptions, withholding, reverse charge, rounding, filing frequency, return, payment/settlement and invoice requirements.
- Implement separate authority connectors (for example ZATCA and Egypt ETA) with their own test environments, schemas, credentials, signing and status handling.
- The existing tax catalog and starter rates are not proof of country compliance. Do not advertise a country as compliant until legal and technical conformance is signed off.

**Acceptance:** asset and payroll subledgers reconcile to GL; each tax jurisdiction passes date-effective calculation, reporting, reversal and authority sandbox scenarios.

### 5.3 P2 — Enterprise productivity and extensibility

1. Workflow/approval engine: configurable stages, amount thresholds, role separation, delegation, escalation, rejection, resubmission and decision history.
2. Report designer: safe query model, custom fields, calculated fields, pivot/grouping, saved views, scheduled delivery and drill-through.
3. Dashboard widgets: permission-aware KPIs, refresh rules, comparison periods and source-document navigation.
4. Integration Hub: versioned REST API, scoped tokens, webhooks, inbox/outbox, retries, dead-letter queue, idempotency, monitoring and provider adapters.
5. Attachments/document management: file type validation, size limits, malware scanning integration where appropriate, checksum, versioning, access policy, retention and backup.
6. Per-user UI preferences: grid column order/width/visibility, sorting, filters, pinned actions, themes, keyboard shortcuts and workspace layout.
7. Search: global and module-specific search with exact/partial matching, filters, permission-aware results and fast indexing.
8. Multi-branch synchronization: durable offline queue, conflict resolution, duplicate detection, branch reconciliation and observable sync status.
9. Projects/service: tasks, timesheets, expense capture, service cases, SLA timers, warranty, service inventory and project profitability.
10. Localization: all visible text from resources; culture-aware numbers/dates/currencies; RTL/LTR layout and report/print templates verified per language.
11. Accessibility and usability: keyboard-only navigation, focus order, contrast, scalable UI, screen-reader labels where practical and predictable error recovery.

### 5.4 P3 — Strategic extensions

These are valuable but should not delay core accounting, inventory or commercial readiness:

- Ecommerce and marketplace connectors (Salla, Shopify, WooCommerce and others).
- Customer/supplier self-service portals.
- Mobile/van-sales and delivery applications.
- Restaurant e-menu, kitchen display and table-service workflows.
- Power BI/analytics export and external data warehouse connectors.
- GPS/field-force tracking with explicit consent, role policy and retention.
- Marketing automation, eLearning, rental, PLM, fleet and other vertical applications.
- AI document extraction, forecasting and anomaly detection after permissions, data quality and audit are reliable.

---

## 6. Product architecture and differentiation strategy

ShouTech should not copy the web architecture of Odoo or ERPNext. Its differentiating position should be:

1. **Offline-first desktop reliability:** full local operation with SQLite and safe, explicit synchronization when connected.
2. **Accounting integrity:** linked source documents, append-only posted history, balanced GL, deterministic reversals and traceable settlement.
3. **Warehouse correctness:** real-time availability, traceability, valuation replay and auditable movement lineage.
4. **Regional readiness:** opt-in jurisdiction packs and authority-specific connectors, with legal evidence rather than guessed tax rates.
5. **Keyboard-efficient professional UI:** compact WPF workflows, fast entry, predictable numbering and one coherent visual identity.
6. **Data ownership and portability:** documented export, backup, restore and migration without proprietary lock-in.
7. **Extensible integration model:** public API and connector contracts that do not bypass business rules.
8. **Developer continuity:** stable schema ownership, migrations, tests, developer handbook and F1 user help.

### Architecture rules

- Preserve the WPF desktop product direction and existing Domain/Application/Infrastructure boundaries where implemented; do not create a second competing service path.
- New schema definitions belong in `dba/mig/sqlite` and `dba/mig/postgresql`; runtime services must not become an alternative DDL system.
- All financial/stock posting must use authoritative application services and idempotent operations.
- Company and branch scope must be enforced at the service/repository boundary.
- English is the source/default language. Do not hardcode Arabic or other translated UI text in code or comments; use resource files.
- Country selection never enables tax automatically; activation is an explicit company-level choice.
- Preserve the agreed main-window structure: menu bar plus Ribbon; do not add an extra third UI bar.
- Do not mark a Ribbon action complete until it opens the real window, loads real data, checks permission, persists changes and has error/empty/loading states.
- Do not copy competitor code, proprietary assets, schemas or branding. Re-implement business concepts independently and respect each product's license.

---

## 7. Prioritized execution roadmap

### Gate 0 — Current stabilization (P0, do first)

- Finish Windows Release build and full test run on the same exact commit as database CI.
- Resolve all test failures by root cause; do not weaken assertions to get a green run.
- Add crash-point tests and recovery/reconciliation for posting state transitions.
- Verify invoice, purchase, POS, transfer, inventory count, reversal, refund and settlement idempotency.
- Verify SQLite and PostgreSQL clean install, repeat migration and representative upgrade.
- Verify company isolation, service-layer permissions, audit and backup/restore.

**Exit gate:** one immutable commit has green Windows build/tests and both provider migration suites; critical posting, reversal and stock reconciliation scenarios pass.

### Phase 1 — Financial and purchasing lifecycle (P0/P1)

- Complete GL, AR/AP, payment allocation, bank reconciliation and period close.
- Complete Purchase Receipt and GRNI; complete sales/purchase returns and source-line progress.
- Finish tax snapshot/reversal and multi-currency tests.
- Reconcile all control accounts and subledgers.

**Exit gate:** end-to-end sales and procure-to-pay paths, including partial and failure scenarios, reconcile to GL, stock, tax and cash/bank.

### Phase 2 — Warehouse and POS execution (P1)

- Bin/location execution, reservations, pick/put-away, replenishment and cycle counts.
- Batch/serial/expiry traceability and valuation replay.
- POS offline queue, multi-tender, cash-up, shift close and refund reconciliation.

**Exit gate:** stock quantity and value reconcile by item/location/lot; POS retry and refund never duplicate effects.

### Phase 3 — Manufacturing and operational control (P1)

- Multi-level BOM revisions, routings, work centres, job cards, MRP, capacity and subcontracting.
- WIP, scrap, by-products, actual costing and variance.
- Quality and maintenance gates.

**Exit gate:** production and quality scenarios reconcile stock, WIP, valuation and GL.

### Phase 4 — Enterprise operations and localization (P1/P2)

- Fixed asset lifecycle, HR/payroll, projects/service and approval workflows.
- Legally reviewed tax packages and e-invoicing adapters by jurisdiction.
- Branch controls and consolidation where supported.

**Exit gate:** each module has an owner, documented workflow, authorization, audit, localization, end-to-end tests and country/legal evidence where relevant.

### Phase 5 — Reporting, integrations and usability (P2)

- Report builder, pivot, dashboard, drill-through, saved filters and exports.
- Public API, Integration Hub, webhooks, inbox/outbox and provider adapters.
- Keyboard productivity, per-user layouts, global search, consistent window design and accessibility.
- F1 user help and developer docs updated with each release.

**Exit gate:** reports tie to source data; connector retries are idempotent; every Ribbon action routes to a real, permission-checked workflow.

### Phase 6 — Commercial release (P0 release gate)

- Signed installer/updater and documented deployment profiles.
- Backup encryption, scheduled backups, restore drills, retention and diagnostics.
- Security review, dependency scanning, performance/load/concurrency tests.
- Clean-install and upgrade acceptance on supported Windows environments.
- Release notes, migration notes, known limitations, support process and reproducible release artifacts.

**Exit gate:** a release candidate passes all required scenarios on clean and upgraded databases with no known P0 data-loss, security, accounting or stock defect.

---

## 8. Mandatory acceptance scorecard

Do not use subjective percentage scores. Mark each area using the status vocabulary in §1.1 and attach test evidence.

| Area | Required evidence before it can be marked Verified |
|---|---|
| General ledger | Balanced postings, source links, period locks, reversal, trial balance and financial statements |
| AR/AP | Allocation, advance, overpayment, refund, ageing, control-account reconciliation and reversal |
| Sales | Quote/order/delivery/invoice/receipt/return with partial fulfillment and audit |
| Purchasing | RFQ/PO/receipt/GRNI/bill/payment/return with three-way match |
| Inventory | Ledger, valuation, bins, reservation, batch/serial/expiry, backdating/replay and stock-to-GL reconciliation |
| POS | Offline replay, multi-tender, refund, shift close, Z report and cash/GL reconciliation |
| Manufacturing | BOM, MRP, routing, work order, job card, WIP, scrap, subcontract and variance |
| Quality/maintenance | Gate enforcement, non-conformance, corrective action and equipment history |
| Fixed assets | Acquisition, depreciation, transfer, impairment/revaluation, disposal and GL tie-out |
| HR/payroll | Attendance, leave, statutory calculations, approval, period lock and GL tie-out |
| Tax | Explicit opt-in, effective-dated jurisdiction rules, source snapshots, return, reversal and authority test |
| Security/audit | Least privilege, company isolation, immutable/durable audit, secret protection and restore |
| Reporting | Correct totals, saved filters, permissions, drill-through and export parity |
| Integration | API contract, scoped credentials, idempotency, retries, dead-letter and reconciliation |
| Deployment | Signed install/update, clean install, upgrade, rollback, backup and restore drill |
| Localization | Resource-only text, RTL/LTR, date/number/currency formats and print templates |

### Required scenario packs

At minimum, automated acceptance must include:

1. Sales cash invoice with tax disabled.
2. Sales credit invoice with tax enabled and customer credit limit.
3. Partial delivery and partial invoice followed by final delivery.
4. Sales return before and after payment; partial and full refund.
5. Purchase order with partial receipt, GRNI and partial supplier invoice.
6. Purchase return before and after supplier invoice/payment.
7. Bank statement import with exact, partial and unmatched transactions.
8. Multi-currency receipt/payment with realized FX.
9. Stock transfer with concurrent posting attempts and process interruption.
10. Inventory count with positive and negative variance.
11. Backdated stock receipt followed by valuation replay.
12. Batch/serial/expiry trace from supplier receipt to customer return.
13. POS offline sale, replay, duplicate message, split tender and partial refund.
14. Manufacturing partial issue/output, scrap, by-product, cancellation and variance.
15. Asset acquisition, depreciation, transfer and disposal.
16. Payroll calculation, approval, posting and period lock.
17. Tax activation declined during company creation; prove no tax rules/postings are created or applied.
18. Cross-company access denial and backup/restore preserving audit history.

---

## 9. Release claims ShouTech must not make yet

Until the evidence is complete, do not claim:
- Full feature parity with Odoo or ERPNext.
- Full Phenix 10 or Al-Ameen 10 replacement parity.
- Production ZATCA, Jordan, Egypt, EU, US or other country tax compliance based only on a country code or starter rate.
- Complete multi-branch offline synchronization.
- Complete MRP/capacity planning or manufacturing costing.
- Fully immutable/tamper-proof audit if critical events still use best-effort writes.
- Commercially ready installer/update/restore merely because the application builds.
- Multi-user SQLite operation.
- Complete end-to-end behavior based only on the presence of windows, services, tables or migrations.

Use precise release notes such as “foundation implemented,” “tested for SQLite,” “verified for the listed scenario,” or “country pack under legal review.”

---

## 10. Source register

The following official product pages and documentation were used as the public benchmark. Product capabilities and availability may vary by edition, configuration and jurisdiction; re-check before making release or marketing claims.

### Manager.io
- Features: https://www2.manager.io/features/
- User guides: https://www2.manager.io/guides/
- History/audit: https://www.manager.io/guides/history/
- Bank reconciliation: https://www.manager.io/guides/bank-reconciliations/
- Reports: https://www.manager.io/guides/reports/
- Editions and deployment: https://www.manager.io/guides/

### ERPNext / Frappe
- Official documentation: https://docs.erpnext.com/index
- Modules: https://frappe.io/ERPNext/Modules
- Stock valuation: https://docs.frappe.io/erpnext/fifo-and-moving-average
- Stock settings and negative stock: https://docs.frappe.io/erpnext/stock-settings
- Stock reconciliation: https://docs.frappe.io/erpnext/stock-reconciliation
- Assets lifecycle: https://docs.frappe.io/erpnext/assets/introduction
- Developer/API documentation: https://docs.frappe.io/

### Odoo
- ERP application overview: https://www.odoo.com/erp
- Application catalogue: https://www.odoo.com/page/all-apps
- Official documentation: https://www.odoo.com/documentation/

### Al-Ameen 10
- Vendor product overview and advertised capabilities: https://alameenksa.com/landingpage/

### Phenix 10
- Publicly advertised capabilities and evidence limitations are recorded in the repository document: [PHENIX10-COMPETITIVE-ROADMAP.md](PHENIX10-COMPETITIVE-ROADMAP.md).
- Treat vendor marketing material as a discovery source, not independent certification.

### Internal ShouTech evidence
- [Developer roadmap](DEVELOPER_ROADMAP.md)
- [Developer handbook](DEVELOPER_HANDBOOK.md)
- [Module catalog](MODULE_CATALOG.md)
- [ERP process parity matrix](ERP-PROCESS-PARITY-MATRIX.md)
- [Phenix 10 competitive roadmap](PHENIX10-COMPETITIVE-ROADMAP.md)
- [ERP competitive roadmap](ERP-COMPETITIVE-ROADMAP.md)
- [Tax engine implementation status](TAX-ENGINE-IMPLEMENTATION-STATUS.md)
- [Enterprise security controls](SECURITY_ENTERPRISE.md)
- [Accounting reversals and outbox](ACCOUNTING_CHART_PERIODS_YEAR_CLOSE.md)
- [Ribbon completion inventory](RIBBON_COMPLETION_INVENTORY.md)
- [Window inventory](RIBBON_COMPLETION_INVENTORY.md)

---

## 11. Change control

- This assessment is tied to the branch and commit shown at the top. Re-audit it when the branch head changes materially.
- Every status change must cite a source file, test name, CI run and exact commit.
- If a feature's evidence is missing, keep its status at **Partial / verify** or **Gap / not evidenced**.
- Update this document, the module catalog and the developer roadmap when an acceptance gate changes.
- Keep all work on the approved stabilization branch unless the owner explicitly requests another branch.
- Do not merge into `main` without explicit approval.


## Procurement implementation update

The purchase-to-pay core is now being implemented as a transactional document chain rather than a collection of disconnected window types:

`Purchase Order → Goods Receipt (RCV) → Supplier Invoice (PUR) → Supplier Payment Voucher (PAY) → Reconciliation`

Implemented on this branch:
- Partial purchase receipts with ordered-quantity controls and stock/GRNI posting in one transaction.
- Stock-tracked receipts update inventory at discounted cost and receipt-date FX; non-stock/service receipts accrue to expense without changing stock.
- Supplier invoices are limited to received, unbilled quantities and can use actual supplier prices and discounts.
- FIFO line-level receipt/invoice matches persist receipt and invoice line IDs, quantities, cost snapshots, FX rates, and variance.
- GRNI clears at historical receipt base value; purchase price/FX variance posts separately to account 402.
- Invoice posting does not receive stock a second time.
- Supplier payment vouchers can be allocated to GUID-scoped purchase invoices while retaining the company-code party-ledger scope.
- Supplier payment allocations are currency-checked; reversing an allocated payment restores invoice open balances and retains release provenance in migration 079 audit rows.

Implementation contract and limits: `docs/PURCHASE_TO_PAY_CYCLE.md` and `docs/PURCHASE_TO_PAY_CYCLE.md`.

The following remain open and must not be represented as completed procurement capability: purchase requisitions and multi-level approval; supplier invoice external-reference uniqueness and document attachments; freight/insurance/customs/duty landed-cost allocation and capitalization; full receipt-time lot/serial/batch/expiry capture and end-to-end traceability; GRNI aging, unmatched receipt/invoice exception workbench and period-end reconciliation reports. RFQ supplier responses, line comparison, explicit award, supplier-quotation-to-draft-PO, partial PO receipts, GRNI, received-quantity invoice matching, payment allocation, invoice-linked debit notes, and pre-invoice receipt returns have implementation paths on this branch; they remain subject to current-head CI and end-to-end acceptance.

---

<!-- source: docs/ERP-COMPETITIVE-ROADMAP.md -->

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

---

<!-- source: docs/PHENIX10-COMPETITIVE-ROADMAP.md -->

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


## 9. Related benchmark

Manager.io is assessed separately because its strengths are especially relevant to accounting workflows, inventory valuation, advanced/custom reports, batch operations, recurring transactions, API access, and offline desktop usage. See [MANAGER-IO-COMPETITIVE-BENCHMARK.md](MANAGER-IO-COMPETITIVE-BENCHMARK.md) for the feature inventory, ShouTech gap mapping, implementation order, and source-license caution.

---

<!-- source: docs/ERPNEXT_PARITY_ROADMAP.md -->

# ShouTech ERP — ERPNext Feature-Parity Roadmap (Offline Desktop)

Goal: reach functional parity with [ERPNext](https://github.com/frappe/erpnext) as an **offline-first Windows desktop ERP**, and outperform **Al-Ameen ERP v10** in the SMB market.

**Deployment model (target):** ShouTech ERP is *offline-first, not offline-only*. The desktop client runs fully standalone (SQLite), and — when ready — can additionally connect to a central server (PostgreSQL) for multi-user/multi-branch operation, encrypted cloud backup, and outbox sync. Licensing is enforced by the SecEng native engine: server-side validation for connected deployments, and optional **hardware dongle** activation for offline machines.

> **Reference:** ERPNext is a web ERP (Python/Frappe, ~41k stars, actively updated). Its module list below was taken from the current `develop` branch module folders.

---

## 1. Legal Boundary — Read First

ERPNext is licensed under **GPL-3.0**. ShouTech ERP is proprietary.

- **Allowed:** studying *concepts* (workflows, document states, report definitions) and re-implementing them in our own C#/WPF/SQLite code.
- **NOT allowed:** copying ERPNext source code, schemas, or UI assets into this repository. That would force the whole product to GPL and destroy the proprietary license.
- All parity work must be clean-room: write from scratch against our own `src/ShouTech.*` layers and `dba/` schemas.

---

## 2. ERPNext Module Map vs ShouTech Coverage

ERPNext current modules: accounts, assets, buying, crm, edi, maintenance, manufacturing, projects, quality_management, selling, stock, subcontracting, support (helpdesk), telephony, shopping_cart/portal (website).

| ERPNext module | What it does | ShouTech today | Gap |
|---|---|---|---|
| **Accounts** | GL, AP/AR, payment reconciliation, taxes, budgets | GL, chart of accounts, journals, vouchers, periods, year close | Payment reconciliation & budgets missing |
| **Stock** | Multi-warehouse, batches, serial nos, valuation (FIFO/Moving Avg), stock ledger | Inventory core, warehouses, movements, barcodes | Batch/serial tracking, valuation methods, stock ledger audit |
| **Selling** | Quotations, SO, delivery notes, pricing rules | Orders, customer pricing | Quotation→SO→Delivery→Invoice chain incomplete |
| **Buying** | RFQ, PO, receipt, purchase invoices | Purchase invoices | RFQ/PO/receipt chain missing |
| **CRM** | Leads, opportunities, activities | CRM folder, leads, sales reps | Opportunity pipeline & activity logging |
| **Manufacturing** | BOM, work orders, MRP, job cards | BOM, production orders | MRP planning, job-card tracking |
| **Projects** | Projects, tasks, timesheets | — | Missing |
| **Assets** | Asset register, depreciation | — | Missing |
| **Quality Management** | QI, non-conformance | — | Missing |
| **Maintenance** | Asset maintenance visits | — | Missing (low priority) |
| **Support / Helpdesk** | Tickets, SLA | — | Missing (low priority for offline desktop) |
| **Subcontracting** | Subcontract POs | — | Missing |
| **EDI / Integrations** | E-invoicing, bank sync, shipping | Outbox webhooks only | Bank statement import, e-invoice (Kuwait NIC/PACS) |
| **Portal / Website** | Self-service portal, e-commerce | — | **Not applicable** — desktop-first is our positioning, not a gap |
| **HRMS** (separate repo) | Payroll, leave, attendance | HR folder (basic) | Payroll & leave |
| **POS** | POS | POS | Exists — parity OK |

---

## 3. Why ShouTech Beats Al-Ameen ERP v10

Positioning advantages to protect and advertise:

1. **Hybrid offline-first** — full standalone operation without internet; when a server is available, the same client connects for multi-user sync, encrypted cloud backup, and outbox webhooks. Al-Ameen v10 has no comparable online↔offline continuity.
2. **Modern stack** — .NET 8 / WPF / MVVM vs legacy WinForms-era code; dark mode, 4 languages (Ar/En/Fr/Tr) with proper RTL.
3. **Clean architecture path** — Domain/Application/Infrastructure split (Al-Ameen is monolithic).
4. **Auditability** — SHA-256-checked migrations, audit trail, RBAC with 209 permission keys, native C++ security engine.
5. **Per-fiscal-year database isolation** — superior to flat-file yearly archives.
6. **Licensing model** — per-user activation; server-connected deployments validate licenses server-side, offline machines enforce via **hardware dongle**.

---

## 4. Phased Roadmap

### Phase 1 — Close the accounting/inventory gaps (core ERP credibility)
- Payment reconciliation (open invoice matching against receipts/payments)
- Stock valuation methods (FIFO, Moving Average) + permanent stock ledger
- Batch & serial number tracking
- Quotation → Sales Order → Delivery Note → Invoice document chain with statuses (Draft/Submitted/Cancelled — ERPNext-style document workflow, re-implemented)
- RFQ → PO → Purchase Receipt → Purchase Invoice chain

### Phase 2 — Differentiators Al-Ameen lacks
- Asset register with depreciation schedules posting to GL
- Projects & timesheets
- Bank statement import (CSV/MT940) with auto-matching
- Kuwait e-invoicing (NIC/PACS) clearance integration via outbox worker
- MRP for manufacturing
- FastReport .NET report pack (currently "planned")

### Phase 3 — Polish & scale
- CRM opportunity pipeline with activities
- Quality management + subcontracting
- Payroll & leave (HRMS parity)
- Install/update framework (clickonce/MSIX) — ERPNext's weakness offline is deployment; ours must be one-click
- **Server mode (when ready):** central PostgreSQL server + sync service for multi-branch/multi-user; the WPF client must switch transparently between local SQLite and server PostgreSQL (both providers already exist in `ShouTech.Infrastructure`)
- **Dongle licensing (when ready):** SecEng dongle check at startup with grace period; server deployments validate licenses server-side so thin clients need no dongle

### Engineering rules for every phase
- Feature-complete = schema (`dba/mig`) + service + WPF view + RBAC permission key + tests (`Kern.Tests`) + docs entry.
- No new "legacy-style" code in `cod/`; all new features go through `src/ShouTech.Domain|Application|Infrastructure`.
- Journal reversal already creates compensating entries: `JournalRepository.ReverseAsync` posts a linked compensating entry (`reversal_of_entry_id`), marks the original reversed, and writes audit events in one transaction — see [ACCOUNTING_CHART_PERIODS_YEAR_CLOSE.md](ACCOUNTING_CHART_PERIODS_YEAR_CLOSE.md). This prerequisite for Phase 2 accounting features is satisfied.

---

*Created from an ERPNext parity review on 2026-11-10. Updated same day: hybrid server mode + dongle licensing added to the target deployment model.*

---

<!-- source: docs/MANAGER-IO-COMPETITIVE-BENCHMARK.md -->

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

---

<!-- source: docs/ERP-PROCESS-PARITY-MATRIX.md -->

# ERP Process Parity Matrix — ShouTech vs ERPNext vs Odoo

**Branch:** `work/full-app-stabilization-2026-11-18`  
**Assessment date:** 2026-11-19  
**Purpose:** Turn the competitive roadmap into testable process parity work. This is a source-inventory assessment, not a claim that every ShouTech service is production-complete.

## 1. Benchmark rules

ShouTech remains an offline-first WPF desktop ERP. Adopt proven business controls from ERPNext and Odoo without copying their web/Frappe architecture.

A class, window, table, or menu is implementation evidence only. A process is complete only when its end-to-end path, permissions, persistence, validation, accounting/stock effects, reversal, audit, localization, and tests work together. Drafts must not affect posted ledgers. Posted accounting and stock history must be corrected through controlled cancellation/reversal, credit/debit notes, returns, or supported reposting—not silent row deletion. Dependencies must be checked before cancellation or deletion.

## 2. Process parity matrix

Status meanings: **Foundation found** = relevant source components exist, full process not yet accepted; **Partial / verify** = some path exists but benchmark controls or end-to-end evidence remain; **Gap / not evidenced** = audited inventory does not prove capability.

| Process | ERPNext benchmark | Odoo benchmark | ShouTech evidence in audited branch | Assessment | ShouTech acceptance gate |
|---|---|---|---|---|---|
| Lead-to-cash | Lead/opportunity, quotation, sales order, delivery note, sales invoice, payment entry, returns/credit note | CRM opportunity/quotation, sales order, delivery, invoice, payment, credit note/return | CrmService, SalesChainService, OrderService, CommercialDocumentRepository, PartyLedger; CRM/order/invoice/delivery/return windows | Foundation found | Convert lead → quote → order → partial delivery → invoice → partial receipt; preserve source links; reconcile AR, revenue, tax, COGS and stock; support return/refund/reversal |
| POS / counter sale | POS profile/session, POS invoice, payments, returns | POS session, orders, payments, refunds, stock/accounting integration | PosService, PosRepository, PosPosting; PosWin, PosCheckoutWin, PosSessionWin, PosReturnWin, PosSyncWin | Foundation found | Multi-tender checkout, cash/card/on-account split, idempotent offline retry, shift opening/closing, cash-up/Z report, partial return/refund; no duplicate GL/stock/settlement |
| Procure-to-pay | Material Request, RFQ, Supplier Quotation, Purchase Order, Purchase Receipt, Purchase Invoice, Payment Entry | Purchase requisition/RFQ, PO, receipt, vendor bill, payment | OrderService, OrderDocumentRepository, supplier ledger, RFQ/quotation comparison and award, receipt/GRNI, three-way match, returns and payment allocation | Partial / verify | Implement purchase requisition with need/budget checks and multi-level approval; enforce supplier invoice external-reference uniqueness and attachments; complete landed-cost capitalization, receipt-time lot/serial/expiry traceability, GRNI aging/exception reporting; pass current-head SQLite/PostgreSQL and concurrency acceptance |
| Inventory / WMS | Item, warehouse, bins, stock entry, transfer, reconciliation, reservations, batch/serial, valuation and reposting | Products, locations, receipts/deliveries, internal transfers, inventory adjustments, lots/serials, valuation | StockValuationEngine, StockRecalculationService, BatchSerial; material, warehouse, transfer, adjustment, barcode and stock reports | Foundation found | Multi-warehouse/bin movement, reservations, picking/put-away, cycle count, lot/serial/expiry, returns, negative-stock policy, deterministic FIFO/average valuation and ledger reconciliation |
| GL / period close | Journal Entry, automated GL, dimensions, immutable ledger, cancellation/reversal, reposting, period closing | Journals, automated accounting, analytic accounting, lock dates, audit trail, reversal entries | JournalService, GlPosting, Accounting, chart of accounts, fiscal period services, reports | Foundation found; correctness gate open | Every posted source document creates balanced linked GL; no duplicate posting; reversals preserve originals; closed periods reject posting; trial balance and statements reconcile |
| AR / AP | Payment Entry, allocations, advances, ageing, dunning, credit limits | Register payment, outstanding reconciliation, follow-up, credit notes | PartyLedger, payment/receipt services, ageing and customer/supplier reports | Partial / verify | Partial allocations, advances, overpayments, refunds, write-offs, multi-currency differences, ageing and settlement reversals reconcile to GL |
| Returns / corrections | Return Sales/Purchase Invoice, credit/debit notes, cancellation and amendment, immutable ledger | Reverse transfer plus credit note/refund; vendor credit note; reversal | InvRepo reversal path, POS return flow, commercial document entities | Foundation found | Return before/after invoice; partial quantities; return stock at correct valuation; refund and AR/AP effects; preserve original and link reversal documents |
| Manufacturing | BOM revisions, Work Order, Job Cards/operations, material issue, WIP, finished output, scrap/by-products, subcontracting | BoM, manufacturing order, work orders, components, operations, scrap/by-products, work centers | BomService, MfgOrderService, MfgOrderRepository, PlanningService; Mfg/BOM/planning windows and reports | Foundation found | Multi-level revision-controlled BOM; plan/reserve; issue materials; WIP; partial completion; scrap/by-products; actual-vs-standard variance; subcontracting; cancellation restores stock and GL |
| Quality | Quality Inspection templates, incoming/in-process/final checks, non-conformance | Quality checks, quality alerts, control points, corrective actions | QualityService, QualityGate, QC windows | Partial / verify | Failed inspection blocks configured receipt/issue/production; non-conformance and corrective action trace to source lot/order |
| Fixed assets | Asset, purchase/capitalization, depreciation, sale/disposal, maintenance | Asset model, depreciation boards, acquisition, disposal, asset accounting | AssetService, AssetRepository, FixedAsset, AssetWin | Foundation found | Acquisition to GL; depreciation methods/books; additions, transfers, impairment/revaluation, disposal and gain/loss; asset register ties to GL |
| Cash / bank / cheques | Bank transactions, payment entries, bank reconciliation, cheque management | Bank import/sync, reconciliation, outstanding payments, cash journals | Cash services, ChequeService, ReconciliationService; bank/cash/cheque windows and reports | Foundation found | Statement import, matching rules, partial matches, fees, FX, bounced/replaced cheques; reconciled balance ties to bank GL |
| CRM / projects / support | Lead/opportunity, activity, project/task/timesheet, issue/SLA | CRM pipeline, activities, projects, timesheets, helpdesk/SLA | CrmService, ProjectService, SupportWin and CRM/project entities/windows | Partial / verify | Convert opportunity without re-entry; activity audit; project budgets/time/expenses/invoice profitability; SLA timers and warranty links |
| HR / payroll | Employee, attendance, leave, payroll entry, salary structure, statutory components | Employees, contracts, attendance, time off, payroll, payslips | HrService, HR entities, employee/attendance/leave/payroll windows and reports | Partial / verify | Approval chain, accruals, shifts/overtime, loans/advances, deductions, payroll period lock, privacy permissions, reproducible payroll journal |
| Tax / compliance | Tax templates/accounts, withholding, tax reports, country localization, e-invoicing | Tax grids, fiscal positions, tax reports, localizations, e-invoicing | TaxSvc and tax fields in commercial documents | Partial / verify | Inclusive/exclusive tax, recoverable/non-recoverable input tax, withholding, tax groups, jurisdiction packs; tax reports reconcile to GL |
| Reporting / analytics | Financial statements, stock ledger, ageing, dashboards, filters and drill-down | Accounting reports, inventory valuation, dashboards, pivot/graph, drill-down | 63 report basenames, Rpt infrastructure and reporting services | Foundation found | Company/branch/date filters, drill-down to source, consistent opening/closing balances, export parity and permission checks |
| Master data / lifecycle | Item/customer/supplier/account/warehouse masters; disabled/frozen states; deletion controls | Product/partner/account/location archive and relational constraints | MatCard, CatWin, MatDirWin, MatGroupWin, WhsWin, party/account cards; RecycleBinSvc | Partial / verify | Soft-delete/restore eligible masters; block deletion with active references; preserve code uniqueness and historical snapshots |
| Security / audit / multi-company | Role permissions, user permissions, immutable ledger, audit, backup/restore, company restrictions | ACLs, record rules, chatter/audit, lock dates, backup/restore and multi-company | Auth, Permissions, License, Backup, DataExchangeGuard, SchMig, Localization; Company service | Foundation found | Least privilege at UI and service layers; company/branch isolation; tamper-evident audit; encrypted backups; tested restore/migrations; offline license recovery |

## 3. Delete, archive, cancel, restore — explicit product decision

These systems do not use one universal delete semantic.

### ERPNext
- Draft submittable documents can be deleted.
- Submitted documents must normally be cancelled before deletion.
- Deleted Documents supports restore; restoring a cancelled document can assign a new name.
- Cancelled documents linked to GL/stock ledgers are normally protected; an explicit Accounts Settings option can permit deleting linked ledger rows, while Immutable Ledger prevents it.
- The recommended correction for posted transactions is cancellation/amendment, returns/credit notes, or reversal—not deleting ledger rows.

### Odoo
- Master records commonly support archive/unarchive instead of deletion when no longer used.
- Restrictive Audit Trail mode prevents deletion of tracked accounting records and limits them to cancellation or archive.
- Returns after delivery use reverse transfers; after invoicing, returns are paired with credit notes/refunds.
- Posted accounting changes are controlled through reversal/credit-note and lock-date workflows.

### ShouTech target policy (user-approved)
1. **Ordinary Delete = soft delete.** Set deletion state/metadata and move the record to Recycle Bin. Keep the row and relationships; hide it from normal lists and operational selection.
2. **Restore = restore original identity.** Reinstate the same record, identifiers and relationships if no conflicting active record exists. Log actor/time.
3. **Permanent Delete = explicit hard delete.** Only from Recycle Bin, with separate permission, confirmation and audit. Validate dependencies and use a transaction.
4. **Posted financial/stock documents = immutable business history.** Ordinary delete is not cancellation. Require cancellation/reversal/credit-debit note/return first. Even after cancellation, permanent deletion is blocked by default if it would erase ledger evidence or statutory history.
5. **Master cards = archive/soft delete.** If referenced by historical documents, retain the master or a historical snapshot; do not cascade-delete financial or stock history.
6. **Audit is independent of the deleted row.** Hard-delete audit events must survive the deleted business record; do not rely only on triggers attached to a row about to disappear.

References:
- ERPNext immutable ledger: https://docs.frappe.io/erpnext/immutable-ledger
- ERPNext delete submitted document: https://docs.frappe.io/erpnext/delete-submitted-document
- ERPNext restore deleted documents: https://docs.frappe.io/erpnext/restore-deleted-erpnext
- ERPNext linked GL deletion controls: https://docs.frappe.io/erpnext/delete-entries-linked-with-gl-entries
- ERPNext correcting submitted accounting entry: https://docs.frappe.io/erpnext/correct-submitted-accounting-entry
- Odoo audit trail: https://www.odoo.com/documentation/20.0/applications/finance/accounting/reporting.html
- Odoo sales process: https://www.odoo.com/documentation/19.0/applications/sales/sales.html
- Odoo returns/refunds: https://www.odoo.com/documentation/18.0/applications/sales/sales/products_prices/returns.html

## 4. Implementation order and gates

### Phase A — stabilize financial and stock correctness (blocker)
Finish current Release build/test CI; fix failures by root cause; prove clean-install and upgrade SQLite migrations; run PostgreSQL migration smoke tests; prove invoice posting, allocations, full reversal, partial return, POS refund, stock valuation and journal balance. No workflow expansion until this gate passes.

### Phase B — unified record lifecycle
Inventory every master/transaction table's key, company scope, foreign keys, posted state and dependency graph. Define canonical lifecycle metadata and audit event schema in DBA migrations for both providers. Implement one shared lifecycle service: SoftDelete, Restore, PermanentDelete, Archive/Deactivate. Replace direct deletes in services/windows; keep transient/temp cleanup separate. Add separate permissions: DeleteToBin, RestoreFromBin, PermanentDelete, Archive, Cancel, Reverse.

Acceptance tests: same-ID restore; duplicate-code conflict; dependency block; cross-company isolation; posted-document protection; audit survives hard delete; transaction rollback on partial failure.

### Phase C — complete sales and purchasing lifecycles
Complete lead/quote/order/delivery/invoice/payment/return with partials and backorders. Complete requisition/RFQ/quotation/PO/receipt/bill/payment with three-way matching and GRNI. Add dependency-aware cancel/amend and source-document traceability. Test stock vs service, cash vs credit, tax, multi-currency and partial fulfilment.

### Phase D — inventory, POS and manufacturing depth
Complete bins/locations, reservation, pick/pack, replenishment, cycle counts, batch/serial/expiry and valuation repost. Verify POS offline idempotency, multi-tender settlement, cash-up and refunds. Complete BOM revisions, routing/work centres, MRP, WIP, scrap/by-products, subcontracting and cost variance.

### Phase E — finance, people and compliance
Complete bank statement import/matching, cheque lifecycle, FX, ageing, dunning, budgets and period close. Complete asset disposals/transfers/impairment, payroll approvals/statutory localization, tax reports and e-invoice packs.

### Phase F — UI, reports and commercial readiness
Map each Ribbon action to a process and permission; remove orphan windows; add consistent filters, drill-down and export; complete localization/resource audit and offline installer/backup/restore/update tests; run role-based UAT scripts and publish known limitations.

## 5. Definition of parity

A ShouTech process is parity-accepted only when its happy path and key exception paths pass automated tests; persisted state and links survive restart; financial and stock ledgers reconcile; cancellation, return, restore and retry are idempotent and auditable; permissions are enforced in services; SQLite and PostgreSQL behave equivalently for supported workflows; and UI, reports, exports and localization represent the same underlying state.

A process remains **not verified** until evidence is recorded. Do not claim full Odoo/ERPNext parity or sale readiness before the relevant gates pass.


## 6. Phenix feature evidence — public marketing material supplied by the user

**Evidence boundary:** This section records capabilities explicitly advertised in the Phenix Systems posts supplied on 2026-11-20. Marketing statements are not independent proof of technical implementation, regulatory certification, service-level guarantees, or detailed feature behavior. Validate each item against product documentation, demonstrations, contracts, and country-specific legal requirements before treating it as a benchmark acceptance criterion.

| Phenix-advertised capability | Evidence in supplied posts | ShouTech target capability | Evidence status / next validation |
|---|---|---|---|
| Saudi e-invoicing and ZATCA integration | Posts advertise direct/automatic integration with the Saudi e-invoicing platform and ZATCA compliance | Country-specific Saudi e-invoicing connector; invoice validation, signing/sealing where required, clearance/reporting flows, QR/XML payloads as applicable, retries, response archive, audit trail and sandbox/production configuration | Marketing claim only; verify exact ZATCA phase/scope, supported document types, onboarding and certification status |
| Jordan tax compliance | Post advertises licensing/approval by the Jordan Income and Sales Tax Department | Jordan localization pack with tax rules, electronic invoicing integration where applicable, reporting and versioned regulatory configuration | Marketing claim only; verify license/approval scope and current technical requirements |
| Egypt e-invoicing | Post advertises support for electronic invoicing and integration with the Egyptian Tax Authority | Egypt localization pack with authority-specific submission, identifiers, signatures, status tracking, credit/debit notes and reconciliation | Marketing claim only; verify actual supported flows and approval status |
| Third-party integrations | Post names Salla, Rasd and Wasfaty | Integration Hub with provider adapters, API/webhook credentials, mapping, sync queue, retries, idempotency keys, monitoring and audit | Named integrations only; verify direction, objects, depth and availability of each connector |
| Van Sales mobile application | Posts advertise field sales app connected to central Phenix | Mobile sales app for route/visit plans, customer lookup, price/credit checks, orders, invoices, collections, returns and offline queue/sync | Advertised; verify offline behavior, supported devices, permissions and supported document types |
| GPS field-force tracking | Post advertises real-time GPS location during customer visits | Consent- and role-controlled location tracking, visit check-in/out, route history, business-hours policy and retention controls | Advertised; verify update frequency, precision, privacy policy and user controls |
| Mobile document creation | Post advertises configurable documents and issuing documents at customer location | Centrally governed mobile document templates, numbering, pricing/tax rules, validation and server-side posting | Advertised; verify which document types can be issued and whether drafts/posting are supported |
| POS application | Posts advertise fast sales, payment processing, invoices and central synchronization | POS terminal/session, cash drawer, multi-tender payments, receipt, discounts, returns/refunds, stock/GL posting, offline idempotent sync and shift close | Advertised; verify offline mode, devices, payment integrations and reconciliation |
| Restaurant e-menu | Post advertises tablet-based digital menu with item images/prices and live synchronization | Menu/catalog publishing, availability, modifiers, table/order flow, kitchen routing and synchronized pricing | Advertised; verify ordering, table management, kitchen display and payment scope |
| Administrative workflow | Post advertises approval paths for purchase requests, disbursement requests and financial documents | Configurable workflow engine with sequential/parallel approvers, thresholds, delegation, escalation, rejection/resubmission, segregation of duties and immutable decision log | Advertised; verify workflow designer, conditions, notifications, delegation and audit depth |
| Payroll / HR | General Phenix descriptions mention payroll and human resources | Complete HR/payroll lifecycle with country localization, approvals, accruals, statutory deductions and linked payroll journals | High-level product claim only; verify scope of HR modules and country coverage |
| Unified ERP | Posts describe sales, warehouses, HR and online-store integrations in one system | Unified master data and transaction services across desktop, mobile, POS, warehouse, accounting and external channels | Positioning claim; verify actual shared data model, licensing and module dependencies |

### Multi-country tax engine

The tax localization requirement is now specified in [MULTI-COUNTRY-TAX-ENGINE-ROADMAP.md](MULTI-COUNTRY-TAX-ENGINE-ROADMAP.md). This is a cross-cutting ERP capability and must be delivered through effective-dated jurisdiction packs, immutable tax snapshots, tax-to-GL/return reconciliation, and country-specific e-invoicing adapters. Saudi Arabia, Jordan, Egypt, the full GCC, Syria, Iraq, Lebanon, EU member states, the United States and additional jurisdictions are explicit coverage targets. A country is not considered supported until its legal rules, official technical specifications and conformance tests have been reviewed and passed.

### ShouTech architecture implications

1. Build a provider-neutral **Integration Hub**; do not hardwire Salla or any named provider into sales/accounting services. Each connector must be isolated behind a contract and have its own configuration, credential handling, mapping, health state, retry policy and audit.
2. Build **Tax Localization Packs** by jurisdiction and effective date. Tax rates, document schemas and authority endpoints must not be scattered as constants through invoice windows or posting services. A connector must distinguish validation, submission, acceptance, rejection, cancellation and credit/debit correction.
3. Treat **Van Sales, POS and EMenu** as clients of the same governed catalog, pricing, inventory, customer, document-numbering and posting services. Mobile/POS synchronization must be idempotent and conflict-aware; the client must never independently create authoritative accounting entries.
4. Implement **Workflow** as a reusable state machine/policy engine, not a collection of approval buttons. Enforce approval authorization in the service layer, preserve every decision, and prevent self-approval where segregation-of-duties rules prohibit it.
5. Add explicit privacy/security controls for GPS, customer data, payment data and integration credentials: least privilege, retention rules, encryption, access logging and revocation.
6. Keep country compliance claims separate. ZATCA, Jordanian and Egyptian requirements are distinct localization and certification tracks; passing one does not imply compliance with another.

### Recommended delivery sequence

- **P1 — Foundation first:** stabilize posting, stock, numbering, company isolation, audit and CI; no external connector may bypass these services.
- **P2 — Workflow and Integration Hub:** establish contracts, durable queue/outbox, idempotency, retries, observability and approval engine.
- **P3 — Saudi e-invoicing discovery and implementation:** verify current official ZATCA specifications and required onboarding/certification before implementation; test sandbox and production readiness separately.
- **P4 — POS and Van Sales:** reuse shared sales, inventory, payment and numbering services; add offline sync and device/session controls.
- **P5 — Restaurant EMenu and kitchen workflow:** publish controlled menu data and route orders through the same stock and sales services.
- **P6 — Jordan and Egypt localization:** separate regulatory discovery, implementation and acceptance tests for each country.

**Acceptance rule:** A marketing claim is not a ShouTech feature until the end-to-end flow is implemented, tested against the relevant authority/provider sandbox where available, audited, secured, and verified for failure/retry/reversal scenarios.
