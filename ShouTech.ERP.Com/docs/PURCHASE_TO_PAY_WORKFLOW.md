# Purchase-to-Pay Workflow

## Scope

This document records the purchase-to-pay implementation on
`work/full-app-stabilization-2026-11-18`. It is an implementation contract,
not a claim that every procurement feature in the ERP is complete.

## Current transaction chain

`Purchase Order → Goods Receipt (RCV) → Supplier Invoice (PUR) → Supplier Payment Voucher (PAY) → Payment Reconciliation`

### 1. Purchase order

- A posted purchase order is the commercial commitment.
- Posting the order does not receive stock and does not create a supplier payable.
- The order remains the parent document for receipts and supplier invoices.
- Fulfilment and billing progress are calculated from linked posted documents.

### 2. Goods receipt

- A receipt can be created only from a posted purchase order in the active company.
- The receipt UI defaults each line to the outstanding quantity and permits partial quantities.
- Receipt posting validates material identity, positive quantities, and the remaining ordered quantity.
- Receipt document, stock movement, GRNI journal, and PO-to-receipt link commit in one database transaction.
- Stock-tracked materials increase inventory at discounted purchase cost and the receipt-date exchange rate.
- Non-stock/service materials do not create stock movements; their receipt value is debited to the purchases/expense account.
- Both paths credit the GRNI control account (2203).
- Receipt posting does not create a supplier payable or supplier-ledger entry.
- Repeated submission cannot receive beyond the PO's remaining quantity.

### 3. Supplier invoice and three-way matching

- The supplier invoice UI defaults to received-but-not-yet-invoiced quantities and allocates duplicate material lines in source-line order.
- Users can enter actual supplier unit prices and discounts. Tax is sourced from company tax rules and the tax-rate column is hidden when tax is disabled.
- The repository rejects taxable supplier invoices unless the company explicitly enabled its tax system; selecting a country alone does not activate tax.
- A supplier invoice cannot be posted before a receipt or for quantities beyond posted, unbilled receipts.
- Each invoice line is allocated FIFO against receipt lines. The allocation is persisted in
  `purchase_receipt_invoice_matches`, including receipt/invoice line IDs, quantities,
  historical costs, exchange rates, and base-currency variance.
- Invoice posting does not move stock a second time.
- GRNI is debited at the receipt's historical base value.
- Price and exchange-rate variance is posted to the purchases/variance account (402).
- Recoverable input tax is split to the input-VAT account when that account is configured;
  otherwise tax remains in purchase cost.
- The supplier payable is credited for the invoice total.
- PO-to-invoice and matched receipt-to-invoice links are written in the same transaction.

### 4. Supplier payment and reconciliation

- Supplier payments are posted through the existing cash/bank journal voucher flow.
- A voucher-party link writes the supplier debit to the party ledger in the same posting transaction. The posting repository also requires the voucher to debit the configured payable control account (2201) for exactly the linked amount; a balanced voucher that debits an expense account instead is rejected.
- Payment Reconciliation allocates the payment to the open supplier invoice and updates
  `purchase_documents.paid_amount` atomically. Allocation is rejected when the payment and invoice
  currencies differ; there is no implicit same-number cross-currency settlement.
- Reversing an allocated supplier payment releases its active allocations and restores each
  affected invoice's open balance inside the journal-reversal transaction. Each released allocation
  is preserved with allocator, note, amount, invoice identity, releasing user, and reversal journal
  in `payment_allocation_reversal_audit`.
- The payment ledger uses the company code while order-chain documents use the stable company
  identifier. Migration 077 adds `payment_allocations.document_company_id` so both scopes are
  retained and allocations remain company-isolated.

## Database ownership

- Runtime code does not create tables.
- Migration 077: `payment_allocations.document_company_id` for dual company-key reconciliation.
- Migration 078: `purchase_receipt_invoice_matches` for line-level three-way matching and variance audit.
- Migration 079: `payment_allocation_reversal_audit` for retained allocation provenance when a linked payment voucher is reversed.
- GRNI control mapping currently uses account 2203 (accrued liabilities); the match table is the
  receipt-level subledger supporting reconciliation.

## Required regression coverage

- Partial receipt, second receipt, and partial supplier invoices across receipt lines.
- No invoice before receipt.
- Over-receipt rejected without changing stock.
- Receipt updates last purchase cost and posts inventory/GRNI.
- Supplier price variance posts separately from GRNI.
- Service receipts accrue to expense without changing stock.
- Supplier payment voucher appears in the unallocated supplier-payment list and must post against the payable control account.
- Payment allocation settles a GUID-scoped invoice while retaining the company-code payment key.
- Cross-currency supplier payment allocation is rejected without changing invoice/payment balances.
- Reversing an allocated supplier payment releases invoice balances and retains the full allocation-release audit record.
- SQLite and PostgreSQL migrations are repeatable.

## Remaining procurement scope

The current chain does not yet claim completion of:
- purchase requisition approval and multi-supplier RFQ quotation comparison;
- supplier invoice external-reference uniqueness and document attachment;
- freight, landed-cost allocation, customs and duty capitalization;
- lot/serial/batch/expiry capture at receipt;
- supplier returns, debit/credit notes, and reversal/replacement workflows;
- GRNI aging, unmatched receipt/invoice exception workbench, and period-end reconciliation report.

These are follow-on acceptance areas; they must not be silently treated as implemented merely because
the core PO-to-payment path exists.
