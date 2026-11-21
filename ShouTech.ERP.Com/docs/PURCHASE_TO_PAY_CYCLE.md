# Purchase-to-Pay Cycle

## Scope

This document describes the implemented purchasing flow in ShouTech ERP. The operational chain is:

`Purchase Order → Goods Receipt → Supplier Invoice → GRNI Reconciliation → Supplier Payment → Payment Allocation`

Purchase documents are company-scoped. Posted source documents remain auditable; corrections use reversal workflows rather than destructive edits.

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
- Posting a linked supplier payment writes the debit settlement entry to the supplier party ledger in the same transaction as the journal posting.
- The payment does not silently settle an invoice merely because the supplier is selected. Use Payment Reconciliation to allocate the payment to one or more open supplier invoices.
- The reconciliation engine re-reads and validates the payment balance, supplier identity, document status, open amount, company scope, and currency inside one transaction.
- Allocations are recorded in `payment_allocations`; the purchase invoice's `paid_amount` is increased in that same transaction.
- Cross-currency allocation is rejected unless a proper conversion workflow is performed first. Equal numeric amounts in different currencies are not interchangeable.

## 6. Reversing a supplier payment

- Posted payment vouchers are reversed; they are not deleted.
- The reversal creates mirrored journal lines and a mirrored party-ledger entry.
- Any allocations made from the original payment voucher are removed in the same reversal transaction, and each affected purchase invoice's paid amount is reduced by the released allocation.
- Each released allocation is copied to `payment_allocation_reversal_audit` with its original allocation identity, payment ledger, invoice, amount, allocator, note, releasing user, and reversal journal. The active allocation is then removed and the invoice paid amount is restored. If an affected document or audit row cannot be written consistently, the entire reversal transaction rolls back.

## 7. Integrity and acceptance checks

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

## Implementation map

- Purchase-order, receipt, invoice, matching, GRNI, and purchase variance: `src/Infra/OrderDocumentRepository.cs`
- Application workflow: `src/App/Services/OrderService.cs`
- Order/receipt/invoice UI: `cod/app/src/Views/OrdWin.cs`
- Supplier payment voucher: `cod/app/src/Views/CshWin.cs`
- Supplier payment allocation: `src/Infra/ReconciliationRepository.cs` and `cod/app/src/Views/PayRecWin.cs`
- Voucher reversal and allocation release/audit: `src/Infra/JournalRepository.cs`, `src/Infra/GlPosting.cs`, and `src/Infra/PartyLedger.cs`; audit schema: `dba/mig/sqlite/079_payment_allocation_reversal_audit.sql` and `dba/mig/postgresql/079_payment_allocation_reversal_audit.sql`
- Receipt/invoice matching schema: `dba/mig/sqlite/078_purchase_receipt_invoice_matches.sql` and `dba/mig/postgresql/078_purchase_receipt_invoice_matches.sql`
- Regression coverage: `tests/Kern.Tests/OrderChainStep9Tests.cs` and `tests/Kern.Tests/PartyUnificationTests.cs`

## Known boundaries

This implementation matches by purchase order, material/service identity, and quantity. It does not yet implement a configurable three-way-match tolerance matrix for price, tax, delivery, or supplier-specific policy; price differences are currently posted to the Purchases variance account. Foreign-currency payment allocation is deliberately blocked rather than converted implicitly. Approval routing, supplier remittance advice, bank-file reconciliation, and supplier statement matching remain separate follow-up capabilities.
