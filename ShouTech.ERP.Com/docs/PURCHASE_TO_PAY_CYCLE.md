<!-- source: docs/PURCHASE_TO_PAY_CYCLE.md -->

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

## 8. GRNI as-of reconciliation

- The GRNI report is evaluated as of the selected date and lists the remaining quantities and base-currency values by posted receipt line after posted supplier-invoice matches and posted pre-invoice returns.
- Its reconciliation summary compares the open receipt-side GRNI value with the posted general-ledger balance of control account 2203 through the same date. The difference is shown in base currency.
- A non-zero difference is an exception for investigation, not an instruction to post a balancing journal automatically. Users must identify missing receipt/invoice/return postings, manual control-account journals, date cut-off errors, or legacy data differences before an authorized adjustment is posted.
- Reversed journal entries are included net of their posted reversal, so a properly reversed posting does not continue to inflate the control balance.
- The report is a diagnostic tie-out; a controlled period-end reconciliation workbench with saved sign-off, exception ownership, and adjustment/reversal workflow remains a release requirement.

## 9. Landed-cost allocation foundation

- The allocation engine supports proportional distribution by base goods value, base quantity, weight, volume, and explicit manual line amounts.
- Monetary allocations are rounded to four decimal places; the final eligible receipt line absorbs the rounding remainder so each charge reconciles exactly to its base-currency amount.
- Manual allocations must reference receipt lines and reconcile to the charge total without reversing the sign of the charge.
- The provider-specific voucher, charge, allocation, and audit tables are defined in migrations 087 for SQLite and PostgreSQL.
- The posting workflow is not considered complete until allocated cost is incorporated into the stock valuation/layer replay, consumed-stock variance, supplier liability, and GL transaction atomically, with reversal and partial-consumption tests. A draft schema or calculation alone must not be treated as a posted landed-cost feature.

## 10. Integrity and acceptance checks

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

This implementation matches by purchase order, material/service identity, and quantity. RFQ responses are compared line-by-line and normalized to base currency; supplier award remains an explicit user decision. The company-scoped three-way-match control is now implemented across the commercial-document engine and application shell:
- Migrations 082 and 083 create opt-in company policy, immutable invoice evaluations, append-only override audit, dedicated permissions, and the unique override/evaluation guard for SQLite and PostgreSQL.
- Purchase invoice conversion evaluates price, tax, and base-currency variances inside the same posting transaction as the invoice and GRNI/GL writes.
- Payment allocation checks the active company policy and the latest evaluation inside its allocation transaction. An enabled policy fails closed when the invoice has no evaluation, when the evaluation was made under an older policy version, or when the decision is invalid. Blocked decisions require an override for that exact evaluation.
- The policy repository supports optimistic concurrency and rejects stale settings updates. Releases require a reason, must target the latest Blocked evaluation, and cannot be approved by the same user who evaluated the invoice.
- Match evaluation snapshots include the exact receipt/invoice source-line identities and monetary/FX comparison values; the review window displays these details.
- Authorized users can re-evaluate an invoice against the current policy version. The new evaluation is append-only and historical evaluations are retained.
- When the policy is enabled, the legacy manual supplier invoice posting route is blocked so it cannot bypass PO/receipt matching or settle directly.
- The application service enforces separate manage/release permissions and writes audit events. The Purchasing ribbon now opens the policy/evaluation review window; English, Arabic, French, and Turkish resources are included.
- Regression coverage now includes evaluator thresholds, blocked payment rejection, audited release followed by successful allocation, self-approval rejection, policy version conflicts, and service permission checks.

The active branch now persists source-line match identities and monetary/FX details inside each immutable evaluation snapshot. The Purchasing match-review window displays that snapshot and provides a permission-checked re-evaluation action when the company policy version changes; re-evaluation appends a new version and preserves prior decisions. The legacy InvoiceWin/InvRepo manual supplier-invoice route is blocked when three-way matching is enabled, because it has no PO/receipt lineage and can settle immediately. The full solution build and complete SQLite/PostgreSQL integration suites for this increment are still awaiting a completed CI run; provider parity, tenant isolation, and race-condition coverage remain release gates. Price differences continue to post to the Purchases variance account. Foreign-currency payment allocation is deliberately blocked rather than converted implicitly. Supplier remittance advice, bank-file reconciliation, and supplier statement matching remain follow-up capabilities.

## Competitive control design references

The next matching/approval increment should preserve the current received-quantity control and add an explicit payment eligibility decision rather than silently weakening quantity validation:

- Odoo's three-way matching compares PO, receipt, and vendor bill, and exposes a "Should Be Paid" decision. Its documentation positions the control as a payment gate, with exceptions reviewable by an authorized user: https://www.odoo.com/documentation/18.0/applications/finance/accounting/payables/supplier_bills/manage.html
- Odoo separates bill control by ordered quantities versus received quantities; three-way matching is intended to operate with received-quantity control: https://www.odoo.com/documentation/18.0/applications/inventory_and_mrp/purchase/manage_deals/control_bills.html
- ERPNext exposes company-level requirements for PO and receipt links and allows narrowly scoped supplier exceptions; it also highlights rate-change controls and the trade-off between controls and document-entry effort: https://docs.frappe.io/erpnext/buying-settings

ShouTech already enforces received quantities at invoice creation, so the next step must not copy a weaker payment-only gate. It should add a per-company and (later) supplier-specific tolerance policy for unit price, tax, and absolute base-currency variance; calculate and persist a match result (Pass, Warning, Blocked, or authorized Override); retain who approved each override and why; and make payment allocation reject a Blocked invoice until an authorized release is recorded. Defaults must remain non-breaking and explicit; no silent country-, supplier-, or user-derived policy should be activated.

## Regression coverage added in the active development branch

- `PurchaseInvoice_SubsetSourceLine_MatchesExactReceiptLine` verifies that an invoice containing only a later PO line is matched to that exact receipt line, with the persisted invoice-line identity and matched quantity checked against `purchase_receipt_invoice_matches`.
- This complements the partial receipt-return source-line test and the existing multi-receipt / partial-invoice matching coverage. CI for the current branch head must pass before this increment is considered verified.

---

<!-- source: docs/PURCHASE_TO_PAY_WORKFLOW.md -->

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
- Each invoice line retains its original purchase-order line number and is allocated FIFO against receipt lines for that same source line. Duplicate material cards on separate PO lines therefore retain their own quantity and cost history. The allocation is persisted in
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
- A voucher-party link writes the supplier debit to the party ledger in the same posting transaction. The posting repository verifies the voucher-party link belongs to the journal's company and requires the full voucher amount and linked amount to debit the configured payable control account (2201); a balanced voucher that debits an expense account instead is rejected.
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
- Supplier payment voucher appears in the unallocated supplier-payment list; its full balanced amount and the exact linked amount must both debit the payable control account.
- A supplier-linked payment voucher that debits an expense account instead of Accounts Payable is rejected before any party-ledger settlement is written.
- Payment allocation settles a GUID-scoped invoice while retaining the company-code payment key.
- Cross-currency supplier payment allocation is rejected without changing invoice/payment balances.
- Reversing an allocated supplier payment releases invoice balances and retains the full allocation-release audit record.
- Partial purchase debit notes retain original invoice-line identity and cannot cumulatively return more than the posted supplier invoice quantity.
- Concurrent returns against the same supplier invoice cannot consume the same remaining quantity.
- SQLite and PostgreSQL migrations are repeatable.

## Sourcing request entry

A supplier-neutral Request for Quotation (RFQ) can now be created from
**Orders → Request for Quotation**. It uses the shared commercial-document
engine, supports item lines and a response deadline, can be saved as a draft,
posted, loaded by document number, and printed. A supplier is not mandatory
on the RFQ header because one request may be sent to multiple suppliers.

Supplier responses are captured in the dedicated supplier quotation
entry window. Posted offers can be compared side by side, explicitly awarded,
and converted into a linked draft purchase order. A supplier quotation can
also be marked Lost without changing the RFQ or any sales document. These
workflows still require full end-to-end acceptance coverage before release
readiness can be claimed.

## Remaining procurement scope

Partial supplier debit notes against posted invoices are implemented in `cod/app/src/Views/PurchaseReturnWin.cs` and `cod/app/src/Services/InvRepo.cs`; migration 080 records cumulative line allocations.

Pre-invoice receipt returns are implemented separately in `cod/app/src/Views/PurchaseReceiptReturnWin.cs` and `src/Infra/OrderDocumentRepository.cs`; migration 081 records cumulative receipt-line return allocations.

Supplier invoice external references are unique per company and supplier (case-insensitive and whitespace-trimmed) for active purchase invoices. The application validates before save, and SQLite/PostgreSQL migration 084 adds matching partial unique indexes as the concurrency guard.

Receipt-return eligibility and invoice posting now count only match rows whose source supplier invoice remains posted and is not deleted. This protects replacement invoices and receipt-return quantities from stale match rows retained for inactive legacy invoices. Regression coverage exercises an inactive invoice with retained matches, a full returnable quantity, and a replacement invoice against the same receipt.

The current chain does not yet claim completion of:
- purchase requisition creation, budget/need validation, multi-level approval, and conversion of an approved requisition into an RFQ or purchase order;
- supplier invoice-document attachment management;
- freight, landed-cost allocation, customs and duty capitalization;
- complete lot/serial/batch/expiry capture and traceability in the purchase receipt workflow. The pre-invoice receipt-return path is already implemented separately through `PurchaseReceiptReturnWin` and the transactional `CreatePurchaseReceiptReturnAsync` operation; migration 081 persists receipt-line return allocations, and posting reverses stock/GRNI while excluding returned quantities from later invoice matching. This capability is implemented at source level but still requires end-to-end acceptance evidence.
- The GRNI report supports an as-of date and 0–30 / 31–60 / 61–90 / 90+ day aging buckets over the remaining base-currency receipt value. Invoice matches and receipt returns are restricted to documents posted on or before the selected as-of date, so later activity does not rewrite historical GRNI. The unmatched receipt/invoice exception workbench and a period-end reconciliation report remain outstanding.

The RFQ supplier-response, side-by-side comparison, explicit award, and supplier-quotation-to-draft-PO workflows are implemented in the application service and dedicated UI. Supplier quotations can also be marked Lost; this disposition is separate from awarding an offer. These capabilities still require the full application build and end-to-end acceptance run before release readiness can be claimed.

These remaining procurement areas must not be silently treated as implemented merely because
the core PO-to-payment path exists.

---

<!-- source: docs/PURCHASING_THREE_WAY_MATCH_DESIGN.md -->

# Purchasing Three-Way Match and Payment Eligibility

## Status

Design baseline for the next purchase-to-pay increment. This document does not claim that tolerance evaluation, approval overrides, or payment blocking are implemented. The existing received-quantity matching remains authoritative and must not be weakened.

## Existing invariants to preserve

1. A supplier invoice is created only from a posted purchase order in the active company.
2. Invoice quantities are consumed from posted receipt lines, not merely from ordered quantities.
3. Receipt-line and invoice-line identities, matched quantities, receipt/invoice costs, FX rates, base amounts, and variance are persisted in `purchase_receipt_invoice_matches`.
4. Invoice, matching rows, GRNI clearing, supplier ledger, and GL posting commit atomically.
5. Receipt returns reduce net received quantities and cannot be invoiced.
6. Supplier payment allocation is a separate operation and is currently allowed only for posted invoices with matching supplier, company, currency, and open balance.
7. Posted documents are corrected by reversal workflows; no destructive edit/delete path is introduced.

## Target behavior

Add a configurable, company-scoped three-way control over purchase order, receipt, and supplier invoice values. The control evaluates quantity, unit price, tax, and base-currency variance. It must produce one persisted decision per posted supplier invoice:

- `Pass`: within all enabled tolerances.
- `Warning`: outside a warning threshold but not a blocking threshold.
- `Blocked`: exceeds a blocking threshold; payment allocation is prohibited.
- `Override`: a blocked invoice has an explicit, authorized, audited release.

The invoice must never be silently marked eligible merely because its quantity matched. Quantity over-receipt/over-invoice validation remains a hard invariant independent of monetary tolerance settings.

## Configuration model

Use a per-company policy record. New companies receive an explicit disabled policy so existing installations preserve current behavior until an authorized user enables the control. Country selection, supplier identity, tax country, and user role must never silently activate or infer a policy.

Initial policy fields:

- `company_id` (unique company key)
- `is_enabled`
- `price_warning_percent`, `price_block_percent`
- `tax_warning_percent`, `tax_block_percent`
- `base_variance_warning_amount`, `base_variance_block_amount`
- `require_receipt_match`
- `created_at`, `created_by`, `updated_at`, `updated_by`

All tolerances must be non-negative. A blocking threshold must be greater than or equal to its warning threshold. Zero means no variance is permitted for that dimension when the policy is enabled; null should not be used to mean zero. Store and compare monetary tolerances in company base currency.

Supplier-specific exceptions are a later extension. They must be explicit, company-scoped, permission-checked, and auditable; they must not override hard quantity controls.

## Persisted evaluation and audit

Persist an immutable evaluation snapshot for each invoice posting, including:

- company and invoice identity;
- policy version/snapshot used;
- evaluated dimensions and measured deltas;
- final decision and machine-readable reason codes;
- evaluator timestamp and actor;
- source match-row identities.

A re-evaluation after a policy change must create a new evaluation version, not overwrite the historical decision used when the invoice was posted or paid.

An override must be a separate append-only audit record containing invoice, prior decision, approver, timestamp, mandatory reason, and the resulting release decision. The caller must have a dedicated permission; the invoice creator must not gain override authority merely by creating the invoice.

## Transaction boundaries

### Invoice posting

Inside the existing purchase-order serialization transaction:

1. Lock and validate the posted purchase order.
2. Match invoice lines against remaining posted receipt lines.
3. Persist all receipt/invoice match rows.
4. Evaluate enabled policy using those exact match rows and invoice tax snapshots.
5. Persist the evaluation snapshot and decision.
6. Continue existing GRNI, supplier-ledger, and GL posting.
7. Commit all effects together.

If evaluation or persistence fails, roll back the entire invoice posting. If the policy is disabled, preserve the existing posting behavior and record an explicit `NotConfigured`/not-enforced state rather than inventing a pass result.

### Payment allocation

Inside `ReconciliationRepository.AllocateAsync`, after locking and reading the posted purchase invoice and before inserting `payment_allocations`:

- resolve the latest effective evaluation for that invoice;
- reject `Blocked` unless a valid authorized override exists;
- allow `Pass`, `Warning`, and authorized `Override`;
- keep all existing company, supplier, currency, payment remainder, and invoice open-balance checks;
- re-check the decision in the same transaction to prevent a time-of-check/time-of-use gap.

Invoices with no evaluation because the company policy is disabled remain compatible with the existing behavior. A policy enabled after invoice posting requires a controlled re-evaluation workflow before new allocations are accepted; do not infer historical compliance.

## UI and permissions

Add a company accounting/purchasing settings page for enabling the policy and setting tolerances. Show a clear preview of each threshold and base-currency unit. Add an invoice match panel showing PO, receipt, and invoice values by source line, measured variance, decision, and reason. Add a separate approval action for authorized users with mandatory reason entry.

The UI must not display an enabled toggle until the settings are persisted. Do not show a success message before the transaction commits. All visible text must use language resources; no hardcoded Arabic strings or comments.

## Required regression coverage

1. Disabled policy preserves current purchase invoice and payment allocation behavior.
2. Enabled policy with values inside thresholds records Pass and permits allocation.
3. Warning-only variance records Warning and permits allocation.
4. Price variance beyond block threshold records Blocked and rejects allocation.
5. Tax variance beyond block threshold records Blocked and rejects allocation.
6. Base-currency absolute variance beyond block threshold records Blocked and rejects allocation.
7. Authorized override with a non-empty reason permits allocation and records immutable audit data.
8. Unauthorized override is rejected.
9. Invoice creator cannot self-approve unless a separately granted permission explicitly permits it.
10. Policy changes do not rewrite historical evaluation snapshots.
11. Concurrent allocations cannot bypass a Blocked decision.
12. Concurrent invoice posting and receipt return cannot consume the same receipt quantity.
13. SQLite and PostgreSQL migrations and behavior remain equivalent.
14. Company A policy and evaluations cannot affect Company B.
15. A missing or corrupt enabled-policy record fails closed; it must not silently permit payment.

## Implementation status

The following foundation has been committed to the work branch:

1. SQLite and PostgreSQL migrations 082/083 create opt-in policy storage, immutable evaluation history, append-only override history, dedicated permissions, and a unique release-per-evaluation constraint.
2. Domain records and provider-neutral repositories read and update policies with optimistic version checks, return the latest evaluation per invoice, and append releases only for the latest Blocked evaluation.
3. Purchase invoice conversion evaluates price, tax, and base-currency variance inside the same transaction as invoice posting and GRNI/GL effects.
4. Payment allocation checks the current company policy and latest invoice evaluation inside the allocation transaction. Enabled policies fail closed for missing evaluations, stale policy versions, invalid decisions, and unreleased Blocked results.
5. Dedicated policy-manage and override permissions are seeded for administrators; the application service checks them on every write and emits audit events.
6. A Purchasing ribbon route opens the policy and recent-evaluation window. English, Arabic, French, and Turkish resources have been added.
7. Regression tests cover evaluator outcomes, blocked payment rejection, audited release, self-approval rejection, policy version conflicts, permission enforcement, source-line match snapshot persistence, and append-only re-evaluation after a policy change.
8. The evaluation review window displays the stored source-line snapshot and exposes a permission-checked re-evaluation action when the policy version is stale.
9. The legacy manual supplier invoice repository path refuses posting while the company three-way-match policy is enabled, preventing immediate settlement from bypassing PO/receipt controls.

## Remaining implementation and verification

1. Add controlled invoice re-evaluation when policy changes, retaining all prior evaluations and requiring the current policy version before payment.
2. Add a source-line drill-down to the invoice match window showing PO, receipt, invoice, quantities, unit prices, tax, exchange rates, and base-currency variance.
3. Verify the invoice entry paths used by the WPF application all create a match evaluation when a company policy is enabled; manual supplier invoices must not bypass the payment gate.
4. Add integration coverage for warning allocation, policy-version invalidation, missing evaluation, duplicate override, tenant isolation, and concurrent receipt-return/invoice/payment races.
5. Run the full solution build and all relevant SQLite and PostgreSQL integration tests; fix every failure before release.
6. Update `PURCHASE_TO_PAY_CYCLE.md` only with verified test evidence and close the remaining purchase reports, supplier statements, and exception workflow gaps.

## Release gate

Do not call the purchasing cycle or this feature complete until:
- both providers apply all migrations and pass the same integration scenarios;
- the active policy version is enforced transactionally at invoice posting and payment allocation;
- a Blocked invoice cannot be paid without an authorized, independently approved, reasoned override;
- all WPF supplier-invoice paths are covered, including legacy/manual entry paths;
- the source-line comparison and re-evaluation workflows are available to authorized users;
- legacy manual supplier invoice posting cannot bypass an enabled policy;
- purchase reports, returns, GRNI, supplier balances, and payment reconciliation pass end-to-end regression tests.
