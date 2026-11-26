# Open-Source Procurement Reference for ShouTech

**Target branch:** `work/full-app-stabilization-2026-11-18`

## Decision

Use mature open-source ERP implementations as functional and technical references, but implement the required business behavior inside ShouTech's existing C#/.NET, SQLite/PostgreSQL, inventory, and accounting architecture. Do not transplant a complete foreign ERP module. Direct source reuse requires a file-level license and distribution-model review.

## Reference projects

### 1. ERPNext — primary end-to-end buying benchmark

- Repository: https://github.com/frappe/erpnext
- Buying DocTypes: `erpnext/buying/doctype/`
- Purchase Order: `erpnext/buying/doctype/purchase_order/`
- RFQ: `erpnext/buying/doctype/request_for_quotation/`
- Supplier Quotation: `erpnext/buying/doctype/supplier_quotation/`
- Purchase Receipt and tests: `erpnext/stock/doctype/purchase_receipt/`
- Purchase Invoice and tests: `erpnext/accounts/doctype/purchase_invoice/`
- Reports: `erpnext/buying/report/`
- License: GPL-3.0. Direct copying or derivative distribution needs a copyleft compatibility review.

Use it to benchmark the full Material Request -> RFQ -> Supplier Quotation -> Purchase Order -> Purchase Receipt -> Purchase Invoice -> Payment Entry flow, partial receipts/invoices, document status/progress, buying controls, and reporting.

### 2. Apache OFBiz — complex purchase-to-pay accounting patterns

- Repository: https://github.com/apache/ofbiz-framework
- Procurement overview: https://cwiki.apache.org/confluence/spaces/OFBENDUSER/pages/392038849/Overview+of+Workflows+Relevant+to+Procurement+in+Apache+OFBiz
- Manufacturer workflow: https://cwiki.apache.org/confluence/spaces/OFBENDUSER/pages/440303716/Core+Apache+OFBiz+Workflows+for+Manufacturers
- Apache License 2.0; verify notices for each reused component.

Evaluate multiple receipts per PO, multiple invoices per PO, one payment allocated to several invoices, partial payments, explicit invoice approval/posting transitions, and payment void/cancellation.

### 3. OCA purchase-workflow — focused workflow extensions

- Repository: https://github.com/OCA/purchase-workflow
- Repository is AGPL-3.0; individual add-ons may declare different licenses in `__manifest__.py`. Check each add-on separately.
- Evaluate tier approvals, purchase security, RFQ numbering, UOM rounding, triple discounts, supplier calendars, and purchase price synchronization.

### 4. OpenBoxes — receiving and traceability

- Repository: https://github.com/openboxes/openboxes
- Purchasing guide: https://openboxes.cloud/docs/openboxes-guide/purchasing
- Project describes itself as available under the Eclipse Public License; verify repository/file notices before reuse.
- Evaluate receiving discrepancies, lot/expiry traceability, PO audit history, supplier lead times, and warehouse operations.

## Required ShouTech acceptance scope

- Internal purchase requisition with cost centre/project, required date, justification, budget reservation, tier approvals, delegation, and segregation of duties.
- RFQ to multiple suppliers, supplier responses, normalized comparison, award rationale, and conversion to PO preserving source-line identity.
- PO revisions, partial fulfilment, over/under-delivery tolerances, close/cancel/reopen, and immutable audit history.
- Partial receipts, accepted/rejected quantities, warehouse/bin selection, inspection, lot/batch/serial/expiry traceability, and return allocations.
- Supplier invoice duplicate detection by company + supplier + external invoice number; attachments; currency/rate; tax; freight; discounts; landed-cost allocation.
- Three-way matching by PO line, receipt line, and invoice line, with configurable tolerances and authorized exception approval.
- GRNI accrual/reversal/clearing, unmatched receipt/invoice workbench, aging, and period-end reconciliation.
- Supplier payments independent of invoices; many-to-many allocations, partial/combined payments, advances, credit notes, reversals/voids, and bank/cash reconciliation.
- Supplier aging, purchase analysis, price variance, supplier delivery performance, returns, open PO, receipt-not-invoiced, invoice-not-received, and audit reports.
- Idempotency, transaction boundaries, concurrency/locking, company isolation, permission checks, and equivalent SQLite/PostgreSQL behavior.

## ShouTech implementation rules

- Preserve existing document numbering, posting engine, inventory valuation, supplier subledger, company isolation, resource localization, and Ribbon navigation.
- Keep DDL in `dba`; maintain paired SQLite and PostgreSQL migrations.
- No Arabic hardcoded source strings or comments; use language resources.
- Never create a supplier payment automatically from a purchase invoice.
- Posted documents must be reversed/cancelled with audit records, never hard-deleted.
- A feature is complete only after UI, services, persistence, permissions, stock/accounting effects, reports, and automated acceptance evidence are verified.

## Implementation sequence

1. Map each reference capability to current ShouTech classes, tables, windows, and tests.
2. Identify missing invariants and schema gaps.
3. Implement missing workflows using existing ShouTech services and posting/valuation engines.
4. Add paired database migrations and provider-specific tests.
5. Add UI and localization after service contracts and persistence are stable.
6. Run full purchase-to-pay acceptance and migration suites on SQLite and PostgreSQL.
7. Publish a parity matrix with test evidence and remaining exceptions.

## License and attribution gate

Public source availability does not mean unrestricted copying. Preserve required upstream notices, record exact repository commit and file paths, and obtain a license compatibility review before incorporating GPL-3.0 or AGPL-3.0 code into a product with a different distribution model. This is a technical reference, not legal advice.
