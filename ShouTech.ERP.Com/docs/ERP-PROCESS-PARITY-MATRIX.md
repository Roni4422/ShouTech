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
| Procure-to-pay | Material Request, RFQ, Supplier Quotation, Purchase Order, Purchase Receipt, Purchase Invoice, Payment Entry | Purchase requisition/RFQ, PO, receipt, vendor bill, payment | OrderService, OrderDocumentRepository, supplier ledger and purchasing document foundations | Partial / verify | Requisition → RFQ → supplier quote → PO → partial receipt → bill → partial payment; reconcile ordered/received/billed quantities, AP, tax, inventory and received-not-billed clearing |
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
