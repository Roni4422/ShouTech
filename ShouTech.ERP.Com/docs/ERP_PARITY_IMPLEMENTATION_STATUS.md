# ERP Parity Implementation Status

## Product contract

ShouTech remains a Windows desktop ERP with its existing visual identity, Ribbon, window conventions, and offline operating model. ERPNext is a functional reference only; implementation is original C#/.NET code and ShouTech-owned SQL/resources.

- SQLite is the standard standalone database.
- PostgreSQL is retained as an optional customer-controlled on-premise provider.
- Normal business operations must not require internet access.
- Existing WPF appearance, navigation, themes, and Ribbon are preserved.
- New business logic belongs in `src/ShouTech.Domain`, `src/ShouTech.Application`, and `src/ShouTech.Infrastructure`; do not expand legacy service logic under `cod/`.
- Schema changes are additive, provider-paired, versioned migrations under `dba/mig/`. Never edit an applied migration.
- English is the default resource language. All user-facing text must use localization resources; no Arabic literals or Arabic comments in source files.
- A feature is complete only after database persistence, reload, validation, audit, permissions, UI integration, localization, and provider tests pass.

## Authoritative ERPNext parity priority matrix

The owner-approved matrix below is the source of truth for scope and sequencing. "Partial" means inspect and complete existing capabilities; do not create duplicate subsystems or tables. "No" under UI changes means preserve the existing ShouTech appearance, Ribbon, navigation, themes, and window layouts. Add only the minimum controls required to expose a missing operation inside an existing window.

| ERPNext area | Current ShouTech status | Missing backend work | Priority | UI changes |
|---|---|---|---|---|
| Accounting | None / partial | General ledger, journal lifecycle, AR/AP subledgers, tax calculation and posting, reconciliation | P0 | No |
| Stock | Partial | Permanent stock ledger, valuation engine, FIFO/Moving Average, batch and serial lifecycle | P0 | No |
| Sales | Partial | Complete quotation, sales order, delivery, invoice lifecycle and posting | P0 | No |
| Buying | Partial | Complete purchase order, receipt, purchase invoice, supplier returns, approvals | P1 | No |
| Manufacturing | None | BOM, work orders, job cards, material consumption and finished-goods posting | P2 | No |
| HR / Payroll | None | Employee records, attendance, leave, payroll calculations and accounting integration | P2 | No |
| POS | None | Offline POS transactions, shifts, cash movements, closing and stock/accounting posting | P2 | No |
| Workflow | None | Document state machine, approval rules, role-based transitions, audit history | P0 | No |
| Migration | None | Backup/restore, validated import/export, schema migration and recovery tooling | P0 | No |
| Localization | Partial | Complete resource coverage, culture fallback, formatting, Arabic RTL verification | P1 | No |

## Delivery order and stage gates

### P0 — Core financial and operational integrity
1. **Accounting:** verify existing GL/journal code and schemas; implement missing ledger posting, AR/AP, tax, settlement/allocation, reversal and reconciliation. Do not create parallel ledgers.
2. **Stock:** complete stock ledger and valuation from actual posted stock documents; add batch/serial tracking with traceable movements.
3. **Sales:** complete the quotation → sales order → delivery → invoice lifecycle, including status transitions, posting, cancellation/reversal and stock/GL effects.
4. **Workflow:** implement reusable document states, transition guards, approval policies, authorization and immutable transition audit.
5. **Migration:** implement verified backup, restore, import/export and upgrade/recovery paths for offline SQLite first, with PostgreSQL parity.

### P1 — Procurement and localization
6. **Buying:** complete purchase order → receipt → purchase invoice, supplier returns, approvals, stock/GL posting and reconciliation.
7. **Localization:** close missing resource keys, enforce resource-only UI text, culture-aware dates/numbers/currency, and test Arabic RTL without changing the visual design.

### P2 — Extended operations
8. **Manufacturing:** BOM, work orders, job cards, material issue/consumption, output and costing.
9. **HR / Payroll:** employee master, attendance/leave, payroll periods, calculations, payslips and GL integration.
10. **POS:** fully offline checkout, shifts, cash-in/out, closing reconciliation, receipt numbering and stock/GL integration.

## Implementation rules

- Inspect and reuse existing entities, services, repositories, migrations, windows, and resource files before adding anything.
- Add a new file only when the existing architecture has no suitable extension point; explain why in the implementation notes.
- All schema definitions stay under `dba`; migrations are additive, versioned and paired for SQLite/PostgreSQL. Never edit an applied migration.
- Preserve the existing WPF visual identity and Ribbon. Backend completion does not authorize a redesign.
- New business logic belongs in `src/ShouTech.Domain`, `src/ShouTech.Application`, and `src/ShouTech.Infrastructure`; avoid expanding legacy `cod/` service logic.
- No Arabic literals or Arabic comments in source code. User-facing translations belong in language resources.
- Every document posting must be transactional, idempotency-safe, company-scoped, permission-checked, and auditable. Posted documents are corrected by cancellation/reversal, not destructive deletion.
- A feature is complete only after persistence, reload, validation, failure-path behavior, audit, permissions, localization, SQLite/PostgreSQL parity, and automated tests pass.

## Current active work — Stage 1 master-data persistence

### Currency master
The currency master/rate schema, domain contract, service and provider-neutral repository were added in PR #17. The existing Currency Card and AppHost were reused rather than replaced. SQLite repository round-trip coverage is present. Currency rate entry remains unavailable in the UI until the active company's base currency is sourced from the real company context. UI runtime save/reopen, permissions and actor-level audit still require evidence.

### Customer and supplier master — backend foundation in progress
The current `CstCard` already contains customer/supplier/both classification and the required contact, tax, credit, pricing-tier and GL-link fields, but its save/delete handlers are deliberately blocked because no party persistence path exists. Existing commercial-document tables only store party snapshots/IDs and do not provide a full master record. Therefore PR #17 adds a shared company-scoped `parties` master table with C/S/B kinds, paired SQLite/PostgreSQL migration 026, domain record, repository contract, validation service, provider-neutral repository, AppHost registration and initial SQLite persistence/tenant-isolation tests. These additions are a backend foundation only: CstCard integration, list/reload/edit, safe deactivation, audit/permissions, localized validation and PostgreSQL runtime tests remain pending. The existing card will be reused; no duplicate customer or supplier window is planned.

### Verification
The GitHub Actions runs associated with the earlier currency commit completed successfully for Windows build/tests, SQLite migrations and PostgreSQL migrations. The new party-master commits have since changed the branch head, so the latest branch revision must complete CI again before this stage is considered validated.

## Completion gates

A stage is ready for merge only when:

- The solution builds with zero warnings and zero errors.
- Relevant automated tests pass.
- SQLite and PostgreSQL migrations apply cleanly and idempotently.
- No existing applied migration is edited.
- Save/reload tests prove all header and child-row values.
- Posting is atomic and produces correct ledger/stock effects where applicable.
- Failure paths never show a success message.
- All new UI text is localized and the existing ShouTech visual identity is retained.
- The audit document is updated with evidence, not assumptions.

## Deferred / excluded from the offline desktop target

Web storefront, mandatory cloud hosting, browser-only workflows, and mandatory online activation are not implementation requirements. Optional integrations must fail safely and must not block offline accounting, inventory, sales, purchasing, or reporting.
