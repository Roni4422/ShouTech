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
- Purchase Receipt and complete receipt-to-bill flow, including received-not-invoiced (GRNI) accounting and duplicate-receipt protection.
- Warehouse bin/location execution, picking, put-away, reservations and replenishment.
- Full manufacturing execution: routings, work centres, job cards, capacity and actual-versus-standard labor/overhead costing.
- Complete stock ledger replay/reconciliation and batch/serial/expiry acceptance.
- Complete asset lifecycle and all payroll/localization scenarios.
- Reliable bank reconciliation, payment allocation and settlement reversal.
- Service/helpdesk SLA and project profitability end-to-end evidence.

**Required ShouTech upgrades**

1. Implement Purchase Receipt as a first-class document, with partial receipts, source-line quantities, GRNI, supplier invoice matching and return handling.
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
- [Accounting reversals and outbox](ACCOUNTING_REVERSALS_AND_OUTBOX.md)
- [Ribbon completion inventory](RIBBON_COMPLETION_INVENTORY.md)
- [Window inventory](WINDOW-INVENTORY.md)

---

## 11. Change control

- This assessment is tied to the branch and commit shown at the top. Re-audit it when the branch head changes materially.
- Every status change must cite a source file, test name, CI run and exact commit.
- If a feature's evidence is missing, keep its status at **Partial / verify** or **Gap / not evidenced**.
- Update this document, the module catalog and the developer roadmap when an acceptance gate changes.
- Keep all work on the approved stabilization branch unless the owner explicitly requests another branch.
- Do not merge into `main` without explicit approval.
