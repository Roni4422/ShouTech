# Sales-to-Cash Cycle

Status: implementation in progress on `work/full-app-stabilization-2026-11-18`.

This document defines the ShouTech sales lifecycle and the accounting and inventory invariants that must hold across it. It is a working implementation record, not a claim that every sales feature is complete.

## 1. Reference patterns

The workflow is informed by established ERP implementations:

- [ERPNext Selling](https://docs.frappe.io/erpnext/user/manual/en/selling): quotation, sales order, delivery note, sales invoice, payment entry, and customer account flow.
- [ERPNext Delivery Note](https://docs.frappe.io/erpnext/user/manual/en/stock/delivery-note): delivery as a stock-impacting document linked to the originating sales order.
- [ERPNext Sales Invoice](https://docs.frappe.io/erpnext/user/manual/en/accounts/sales-invoice): invoice, tax, receivable, and accounting effects.
- [Odoo Sales](https://www.odoo.com/documentation/18.0/applications/sales/sales.html): quotation-to-order conversion, invoicing policies, and delivery-based billing.
- [OCA sale-workflow](https://github.com/OCA/sale-workflow): modular extensions for sales workflow controls and document behavior.

ShouTech should preserve the document chain and quantities rather than treating quotations, orders, deliveries, invoices, and returns as unrelated forms.

## 2. Canonical document chain

```text
CRM Lead / Opportunity
        |
        v
Sales Quotation (QUO)
        |
        v
Sales Order (SO)
        |
        +-------------------------------+
        |                               |
        v                               v
Direct Sales Invoice (SAL)       Delivery Note (DLV)
(no DLV exists)                  (partial or multiple)
                                        |
                                        v
                                 Sales Invoice (SAL)
                                        |
                                        v
                              Customer Receipt / Payment
                                        |
                                        v
                              Invoice Allocation / Reconcile
                                        |
                                        v
                              Customer Statement / Aging

Posted Sales Invoice (SAL) ----> Sales Return / Credit Note (SRT)
POS Sale (POS) ----------------> POS Return (SRT, POS session flow)
```

A sales order may be fulfilled by multiple delivery notes. A delivery note is not itself a sales invoice. An invoice created from a delivery must not issue stock a second time.

## 3. Accounting and inventory invariants

1. A posted sales order reserves the commercial commitment but does not issue inventory.
2. Posting a delivery note validates cumulative delivered quantity against the posted source order inside the posting transaction, then issues stock through the valuation engine.
3. A sales invoice created from a delivery note posts receivable, revenue, tax, and COGS using the cost captured by that delivery. It must not create another stock issue.
4. Direct invoicing from a sales order remains available only when no delivery note has been posted for that order; that path issues stock and posts COGS itself.
5. The invoice-to-order and delivery-to-order links must both be retained so order progress reports show delivered and billed quantities correctly.
6. Concurrent invoice or delivery posting must serialize on the source document and re-check remaining quantities after acquiring the lock.
7. A posted sales return is a credit note and stock receipt, not a deletion or edit of the original invoice. It must reference the original invoice and preserve the original line identity.
8. Return quantities are cumulative by original line. A return cannot exceed the original sold quantity less all earlier posted returns.
9. Return lines preserve original customer, currency, material, unit, warehouse, price, discount, and tax settings. Header discount and extra charges are allocated proportionally.
10. Customer refunds are separate cash/bank settlement actions. A credit note can reduce receivables or create a customer credit balance; it must not invent a cash refund.

## 4. ShouTech implementation record

### Implemented in the current work

- Sales quotation-to-order and sales order progress services already exist in the commercial document chain.
- Added `SalesDeliveryWin` and routed the sales delivery command to it. It loads a posted sales order, displays ordered/delivered/remaining quantities, and creates partial delivery drafts.
- Added `OrderService.CreateSalesDeliveryAsync` and `PostSalesDeliveryAsync`.
- Delivery posting revalidates the source order under a database transaction and rejects cumulative over-delivery before stock is committed.
- Added delivery-note-to-sales-invoice conversion. The invoice is linked to its delivery and source sales order.
- Direct sales-order invoicing is blocked once a posted delivery exists, preventing the same goods from being issued twice.
- Delivery-linked invoice posting routes through the commercial chain, skips duplicate batch/serial capture and stock issue, and posts COGS from the delivery's recorded stock movement cost.
- Until line-level delivery-cost allocations exist, a delivery note is invoiced as one complete invoice. Partial invoicing of a single delivery note is intentionally rejected rather than risking duplicated COGS.
- Added source-order row locking to direct sales invoice conversion to prevent concurrent invoices from consuming the same remaining quantity.
- Added `SalesReturnWin` for partial customer returns against posted sales invoices.
- Added transactional return validation in `InvRepo.Post`: original document identity, customer and currency, line identity, unit, warehouse, price, discount, tax, proportional header adjustments, and cumulative returned quantity are rechecked at posting.
- Preserved original line numbers for partial returns and full reversals.
- POS return data now carries the original tax calculation mode and exchange rate so POS returns continue to satisfy the same source-line controls.
- Added regression tests for partial delivery, over-delivery rejection, delivery-linked invoicing without a second stock issue, COGS posting, order progress, concurrent invoice conversion, and cumulative sales-return limits.

### Existing supporting capabilities to verify during end-to-end closure

- CRM lead and opportunity conversion to quotations.
- Customer receipt vouchers and payment allocation/reconciliation.
- Customer balances, statements, aging, sales analysis, and invoice lists.
- Customer credit-limit enforcement and tax opt-in behavior.
- User permissions for create, edit, approve, post, cancel, and refund actions.

## 5. Remaining work before declaring the cycle complete

- Add line-level cost allocation so one delivery note can be invoiced in multiple partial invoices safely.
- Add direct partial invoicing from an order when no delivery note is used.
- Complete the end-to-end verification of customer receipts, multi-invoice allocations, overpayments, on-account receipts, refunds, and reversal of allocations.
- Verify tax opt-in and tax-package behavior for sales quotations, orders, invoices, credit notes, and returns in each supported country.
- Add/verify approval routing, order credit holds, customer-specific price lists, discount limits, payment terms, and sales representative commissions.
- Add shipment/carrier/tracking and delivery-address handling where required by the business.
- Verify batch/serial traceability on returns against the original delivery lots and serials.
- Complete sales reports and permission coverage, including delivered-not-invoiced, ordered-not-delivered, customer profitability, sales by item/customer/representative, and return analysis.
- Run the full SQLite and PostgreSQL CI suites and resolve all failures before marking the cycle complete.

## 6. Required regression scenarios

- Quotation -> order -> multiple partial deliveries -> invoices -> customer receipts -> allocations -> customer statement.
- Direct order invoice with no delivery note, including stock and COGS exactly once.
- Delivery note -> invoice, proving stock is issued only by the delivery and COGS is recognized only once by the invoice.
- Concurrent delivery posting that would exceed the order quantity.
- Concurrent direct invoice conversion for the same order.
- Multiple delivery notes and one invoice per delivery, with order progress correctly aggregated.
- Partial sales return, repeated partial returns, exact full remaining return, and over-return rejection.
- Return of a paid invoice, customer credit balance, and subsequent cash/bank refund.
- POS partial return, including refund tender split, session reconciliation, and original line/batch/serial identity.
- SQLite and PostgreSQL parity for locks, decimal quantities, tax, FX, stock valuation, GL, and party ledger.

## 7. Engineering rules

- English-only source code, comments, and hard-coded UI strings. All translated text belongs in language/resource files.
- Database DDL and migrations belong under `dba`; services contain runtime behavior only.
- Posted commercial documents are never permanently deleted. Use cancellation or reversal documents with audit history.
- Every posting path must validate company scope, document state, source links, quantities, tax configuration, and accounting balance inside the posting boundary.
