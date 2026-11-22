# Purchase-to-Pay Cycle

## Scope

This document describes the implemented purchasing flow in ShouTech ERP. The operational chain is:

`Purchase Order → Goods Receipt → Supplier Invoice → GRNI Reconciliation → Supplier Payment → Payment Allocation`

Purchase documents are company-scoped. Posted source documents remain auditable; corrections use reversal workflows rather than destructive edits.

## 0. Supplier RFQ, quotation capture, comparison, and award

- Create and post a Request for Quotation (RFQ) with required item lines, quantities, response deadline, and delivery requirements.
- Record each supplier response against the posted RFQ. Every response must identify every RFQ source line exactly once and retain requested item identity and quantity; omitted or substituted items are rejected.
- Capture supplier quotation reference, currency, exchange rate, validity date, promised delivery date, unit prices, discounts, taxes, and commercial terms.
- Compare posted supplier responses by item, including quoted quantity, unit price, discount, tax rate, line total, and normalized base-currency line total. Offers are ordered by normalized grand total and then promised delivery date; comparison is decision support, not an automatic award.
- Convert the selected, posted, unexpired supplier quotation into a draft purchase order. Supplier, currency, exchange rate, quoted terms, promised delivery date, and source-line identity are retained. The purchase order links back to the winning quotation.
- A quotation cannot create a second active purchase order. An expired quotation must be renewed before award.

## 1. Purchase order

- Create a purchase order for a supplier and add material or service lines.
- Save it as a draft, review quantities, prices, discounts, tax settings, currency, warehouse, and delivery details, then post it.
- Posting an order records the commitment and status only. It does not increase stock and does not create a supplier payable.
- The order is the source document for subsequent goods receipts and supplier invoices.

## 2. Goods receipt

- Create a receipt from a posted purchase order.
- Receive all or part of the ordered quantity. A later receipt can receive the remaining quantity.
- The receipt must belong to the same company as its purchase order. Quantities are checked against the order's remaining unreceived quantities.
- Stock-tracked items create inventory movements through the stock valuation engine. Service/non-stock lines do not create stock movements.
- The receipt and its stock movements, document links, and GRNI journal are committed in one database transaction.
- Accounting for stock items: debit Inventory and credit Goods Received Not Invoiced (GRNI).
- Accounting for non-stock/service lines: debit Purchases/Expense and credit GRNI.
- A receipt does not create a supplier payable. It records what has physically or operationally been received before the supplier invoice arrives.

## 3. Supplier invoice

- Create a supplier invoice from the posted purchase order.
- The invoice entry screen starts with received, not-yet-invoiced quantities. Users can enter a partial invoice quantity and change the supplier's actual unit price, discount, and tax rate.
- Invoice lines must refer to materials on the source order. A service line is also matchable when it has a material/service card.
- Invoice quantity cannot exceed the remaining unbilled quantity on posted receipts. Invoicing before a posted receipt is rejected.
- The repository matches invoice lines against posted receipt lines for the same purchase order and material. Partial invoices can consume part of a receipt; later invoices can consume the remainder and quantities from subsequent receipts.
- Each match records receipt and invoice line identities, matched quantity, both unit costs, both exchange rates, base amounts, and the resulting variance.
- The invoice, receipt-to-invoice links, match records, supplier ledger entry, and GL entry commit together.
- Supplier invoice posting does not receive stock again. The stock movement is created by the goods receipt only.

## 4. GRNI clearing and price/FX variance

- The invoice clears GRNI using the historical base value of the matched receipt quantities.
- The supplier invoice's net cost is compared with the matched receipt cost in base currency.
- A positive or negative difference is posted to the Purchases variance account; input VAT is posted separately when applicable; Accounts Payable is credited for the invoice gross amount.
- The invoice's payable and supplier party-ledger balance use the effective invoice exchange rate. The receipt-side GRNI value retains the receipt's historical base value.
- Tax behavior remains governed by the company's explicit tax-activation setting. Country selection alone must not activate tax.

## 5. Supplier payment

- Create a Payment Voucher for the supplier, select party kind Supplier, choose the cash/bank account, and enter balanced voucher lines.
- Posting a linked supplier payment writes the debit settlement entry to the supplier party ledger in the same transaction as the journal posting. The repository enforces a debit to the payable control account (2201) for the exact linked amount; posting a supplier-linked voucher against an expense account is rejected.
- The payment does not silently settle an invoice merely because the supplier is selected. Use Payment Reconciliation to allocate the payment to one or more open supplier invoices.
- The reconciliation engine re-reads and validates the payment balance, supplier identity, document status, open amount, company scope, and currency inside one transaction.
- Allocations are recorded in `payment_allocations`; the purchase invoice's `paid_amount` is increased in that same transaction.
- Cross-currency allocation is rejected unless a proper conversion workflow is performed first. Equal numeric amounts in different currencies are not interchangeable.

## 6. Reversing a supplier payment

- Posted payment vouchers are reversed; they are not deleted.
- The reversal creates mirrored journal lines and a mirrored party-ledger entry.
- Any allocations made from the original payment voucher are removed in the same reversal transaction, and each affected purchase invoice's paid amount is reduced by the released allocation.
- Each released allocation is copied to `payment_allocation_reversal_audit` with its original allocation identity, payment ledger, invoice, amount, allocator, note, releasing user, and reversal journal. The active allocation is then removed and the invoice paid amount is restored. If an affected document or audit row cannot be written consistently, the entire reversal transaction rolls back.

## 7. Partial supplier returns and debit notes

- A partial purchase return is created from a posted supplier invoice through `PurchaseReturnWin`.
- The return must retain the original supplier, currency, exchange rate, item, unit, unit price, discount, tax settings, warehouse, and source invoice line identity.
- The original invoice's header discount and extra charges are allocated proportionally to the returned line subtotal.
- Cumulative returned quantity is checked against the original invoice line; a second return cannot consume quantities already returned by an earlier posted return.
- Migration 080 stores the return-line to original-invoice-line allocation, returned quantity, actor, and timestamp. Allocation rows are inserted in the same posting transaction as stock, GL, and supplier-ledger effects.
- The source invoice is serialized during return posting so concurrent partial returns cannot both consume the same remaining quantity.
- Full purchase-invoice reversals use the same source-line allocation guard; a partial return therefore prevents a later full reversal from returning more than the remaining quantity.
- Tracked materials require batch/serial capture during return posting.

- A separate pre-invoice return can be created from a posted goods receipt through `PurchaseReceiptReturnWin`; only quantities not already invoiced or returned are eligible.
- The receipt return, line allocations, stock issue, GRNI debit, valuation variance, and source link are committed atomically. It does not create a supplier payable or supplier debit note.
- Batch/serial-tracked goods reuse the original receipt's recorded batch/serial allocations during return posting. If a legacy receipt has no allocation history, the stock engine refuses to silently process the tracked return.
- Return allocations reduce the purchase order's net received quantity and are excluded from later invoice matching. This allows replacement goods to be received against the remaining purchase-order quantity without treating returned goods as available for invoicing.

## 8. Integrity and acceptance checks

The purchase-to-pay flow must pass the following checks before release:

1. A purchase order alone changes neither stock nor supplier payable.
2. A partial goods receipt changes stock only for stock-tracked lines and records GRNI.
3. A second receipt can complete the remaining ordered quantity without duplicating the first receipt.
4. A supplier invoice cannot be posted for quantities not yet received.
5. A partial supplier invoice consumes only the matched receipt quantities.
6. A later invoice can consume the remaining quantity across multiple receipts.
7. Supplier invoice posting does not create a second stock receipt.
8. Receipt and invoice cost/FX differences are recorded as auditable match rows and posted to the variance account.
9. Service/non-stock receipts accrue to expense rather than inventory.
10. A supplier payment is visible as an unallocated payment until assigned to invoice(s).
11. Allocation cannot exceed either the payment's available balance or the invoice's open balance.
12. Allocation between different currencies is rejected.
13. Reversing a supplier payment releases its allocations and restores the invoice's open balance atomically.
14. Every operation remains isolated to the active company and retains document links and user audit data.
15. A pre-invoice receipt return cannot exceed the receipt quantity remaining after posted invoice matches and prior returns.
16. A returned receipt quantity is excluded from order fulfilment and cannot be consumed by a later supplier invoice.
17. Returning goods and posting an invoice concurrently cannot consume the same receipt quantity; both operations serialize on the purchase order.

## Implementation map

- Purchase-order, receipt, invoice, matching, GRNI, and purchase variance: `src/Infra/OrderDocumentRepository.cs`
- Application workflow: `src/App/Services/OrderService.cs`
- Order/receipt/invoice and RFQ UI: `cod/app/src/Views/OrdWin.cs`
- Supplier quotation capture and posting from an RFQ: `cod/app/src/Views/SupplierQuotationEntryWin.cs`
- Supplier quotation comparison, line review, and award to a linked draft purchase order: `cod/app/src/Views/SupplierQuotationCompareWin.cs`
- Invoice-linked partial purchase return / debit note UI and validation: `cod/app/src/Views/PurchaseReturnWin.cs` and `cod/app/src/Services/InvRepo.cs`
- Pre-invoice goods receipt return UI: `cod/app/src/Views/PurchaseReceiptReturnWin.cs`; transactional stock/GRNI return and net-receipt matching: `src/Infra/OrderDocumentRepository.cs`
- Supplier payment voucher: `cod/app/src/Views/CshWin.cs`
- Supplier payment allocation: `src/Infra/ReconciliationRepository.cs` and `cod/app/src/Views/PayRecWin.cs`
- Voucher reversal and allocation release/audit: `src/Infra/JournalRepository.cs`, `src/Infra/GlPosting.cs`, and `src/Infra/PartyLedger.cs`; audit schema: `dba/mig/sqlite/079_payment_allocation_reversal_audit.sql` and `dba/mig/postgresql/079_payment_allocation_reversal_audit.sql`
- Receipt/invoice matching schema: `dba/mig/sqlite/078_purchase_receipt_invoice_matches.sql` and `dba/mig/postgresql/078_purchase_receipt_invoice_matches.sql`
- Purchase-invoice return allocation schema: `dba/mig/sqlite/080_purchase_return_line_allocations.sql` and `dba/mig/postgresql/080_purchase_return_line_allocations.sql`
- Pre-invoice receipt-return allocation schema: `dba/mig/sqlite/081_purchase_receipt_return_line_allocations.sql` and `dba/mig/postgresql/081_purchase_receipt_return_line_allocations.sql
- Regression coverage: `tests/Kern.Tests/OrderChainStep9Tests.cs` and `tests/Kern.Tests/PartyUnificationTests.cs`

## Known boundaries

This implementation matches by purchase order, material/service identity, and quantity. RFQ responses are compared line-by-line and normalized to base currency; supplier award remains an explicit user decision. It does not yet implement a configurable three-way-match tolerance matrix for price, tax, delivery, or supplier-specific policy; price differences are currently posted to the Purchases variance account. Foreign-currency payment allocation is deliberately blocked rather than converted implicitly. Approval routing, supplier remittance advice, bank-file reconciliation, supplier statement matching, and configurable three-way-match tolerances remain follow-up capabilities.

## Competitive control design references

The next matching/approval increment should preserve the current received-quantity control and add an explicit payment eligibility decision rather than silently weakening quantity validation:

- Odoo's three-way matching compares PO, receipt, and vendor bill, and exposes a "Should Be Paid" decision. Its documentation positions the control as a payment gate, with exceptions reviewable by an authorized user: https://www.odoo.com/documentation/18.0/applications/finance/accounting/payables/supplier_bills/manage.html
- Odoo separates bill control by ordered quantities versus received quantities; three-way matching is intended to operate with received-quantity control: https://www.odoo.com/documentation/18.0/applications/inventory_and_mrp/purchase/manage_deals/control_bills.html
- ERPNext exposes company-level requirements for PO and receipt links and allows narrowly scoped supplier exceptions; it also highlights rate-change controls and the trade-off between controls and document-entry effort: https://docs.frappe.io/erpnext/buying-settings

ShouTech already enforces received quantities at invoice creation, so the next step must not copy a weaker payment-only gate. It should add a per-company and (later) supplier-specific tolerance policy for unit price, tax, and absolute base-currency variance; calculate and persist a match result (Pass, Warning, Blocked, or authorized Override); retain who approved each override and why; and make payment allocation reject a Blocked invoice until an authorized release is recorded. Defaults must remain non-breaking and explicit; no silent country-, supplier-, or user-derived policy should be activated.

## Regression coverage added in the active development branch

- `PurchaseInvoice_SubsetSourceLine_MatchesExactReceiptLine` verifies that an invoice containing only a later PO line is matched to that exact receipt line, with the persisted invoice-line identity and matched quantity checked against `purchase_receipt_invoice_matches`.
- This complements the partial receipt-return source-line test and the existing multi-receipt / partial-invoice matching coverage. CI for the current branch head must pass before this increment is considered verified.
