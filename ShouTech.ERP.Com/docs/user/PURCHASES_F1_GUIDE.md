# ShouTech ERP — Purchases Help (F1)

## Purchase-to-pay overview

Use this sequence for a normal supplier purchase:

1. Create and post a Purchase Order.
2. Receive the goods or services using one or more Goods Receipts.
3. Create the Supplier Invoice from the received, uninvoiced quantities.
4. Record the supplier payment using a cash or bank payment voucher.
5. Allocate the payment to the open supplier invoice in Payment Reconciliation.

A purchase order is a commitment. It does not increase stock or create a supplier payable by itself.

## 1. Create a Purchase Order

1. Open **Purchases → Purchase Orders**.
2. Select the supplier and currency.
3. Add material or service cards, quantities, agreed unit prices, discounts, and the delivery details.
4. Save the order as a draft and review the totals.
5. Post the order when it is approved.

Use a material card for each stock item and for each non-stock service. A service card must be configured as non-stock/service so it is expensed rather than added to inventory.

## 2. Receive goods

1. Open the posted purchase order.
2. Choose **Receive Goods**.
3. The receipt grid starts with the remaining ordered quantities.
4. Enter the quantity physically received now. You can receive less than the remaining quantity.
5. Confirm the warehouse and post the receipt.
6. Repeat with another receipt when the supplier delivers the balance.

The application prevents receiving more than the remaining purchase-order quantity.

### Accounting effect at receipt

- Stock-tracked materials increase inventory at discounted purchase cost.
- Non-stock/service items are accrued to the purchase expense account without creating stock movements.
- The other side is credited to the Goods Received Not Invoiced (GRNI) control account.
- A receipt does not create a supplier payable.

## 3. Enter the supplier invoice

1. Open the purchase order after at least one goods receipt is posted.
2. Choose **Create Supplier Invoice**.
3. The grid shows received quantities that have not yet been invoiced.
4. Enter the supplier's actual unit prices and discounts.
5. If the company's tax system is enabled, the tax rate is taken from the company's configured tax rules. If tax is disabled, the tax column is hidden and taxable posting is rejected.
6. Review the totals and post the supplier invoice.

The invoice cannot exceed the quantities already received and not yet invoiced. If a delivery is still outstanding, receive it before invoicing that quantity.

### Accounting effect at invoice

- GRNI is cleared at the historical base value recorded by the receipt.
- Differences in supplier price or exchange rate are posted to the purchase variance account.
- Recoverable input tax is split to the configured input-tax account when available.
- Accounts Payable is credited for the supplier invoice total.
- Inventory is not increased again by the supplier invoice.

## 4. Record a supplier payment

1. Open the cash/bank payment voucher window.
2. Select the supplier as the party.
3. Select the cash or bank account and enter the payment amount, date, currency, and reference.
4. Save and post the voucher.

Posting the voucher records the supplier debit and the cash/bank credit. It does not silently mark a specific invoice as paid.

## 5. Allocate the payment

1. Open **Payment Reconciliation**.
2. Select the supplier.
3. Select the unallocated payment.
4. Select the open supplier invoice.
5. Enter the amount to allocate and confirm.

A payment can be allocated partially or across multiple invoices, subject to the available payment and invoice balances. The invoice's paid amount and the payment's available amount are updated in the same transaction.

## 6. Common messages

| Message | What to do |
|---|---|
| No received quantity available to invoice | Post a goods receipt first. |
| Invoice quantity exceeds received, unbilled quantity | Reduce the invoice quantity or post the missing receipt. |
| Receipt quantity exceeds remaining purchase-order quantity | Check previous receipts and enter only the outstanding quantity. |
| Tax is disabled for this company | Ask an authorized user to review the company's tax activation decision and tax rules. |
| Payment or invoice is not listed in reconciliation | Confirm that the supplier, company, document status, and currency are correct; verify that the voucher and invoice are posted. |

## 7. Audit and corrections

- Do not delete posted purchase orders, receipts, invoices, or payment vouchers to correct accounting history.
- Use the application's authorized reversal, return, or credit-note workflow when available.
- The current purchase invoice workflow does not yet enforce uniqueness of the supplier's external invoice number or attach the original supplier file. Retain those originals in the approved document-control process until that feature is delivered.
- Press **F1** from a purchase window to open the relevant help topic when contextual help is enabled.
