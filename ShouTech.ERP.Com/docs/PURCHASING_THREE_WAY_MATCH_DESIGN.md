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

## Implementation order

1. Add SQLite and PostgreSQL migrations for company policy, invoice evaluations, and override audit.
2. Add provider-neutral policy/evaluation repository methods and domain records.
3. Integrate evaluation into the purchase invoice transaction.
4. Enforce the decision inside payment allocation transaction.
5. Add permission catalog entries, localized resources, and error catalog entries.
6. Add settings, invoice match panel, and override UI.
7. Add integration tests for both providers and run the full solution build/test pipeline.
8. Update `PURCHASE_TO_PAY_CYCLE.md` only after implementation and test evidence exist.

## Release gate

Do not call this feature complete until both providers pass the integration suite, all 15 regression cases are covered, payment allocation is proven to reject blocked invoices transactionally, and the UI is connected to persisted settings and permissions.
