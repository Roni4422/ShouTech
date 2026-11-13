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

## Stage 1 — Accounting and master-data persistence

Priority order:

1. Currency master and exchange-rate history.
2. Customer and supplier master records.
3. Bank and bank-account master records.
4. Cost centers and sales representatives.
5. Receipt/payment vouchers and invoice allocation.
6. Cheque batches and cheque lifecycle.
7. Journal reopen/load and complete field round-trip.
8. Sales and purchase order persistence and conversion.
9. Manufacturing order persistence and posting.
10. Report data verification and dashboard removal of hard-coded figures.

### Currency card — current implementation state

| Layer | Status | Evidence / next action |
|---|---|---|
| SQLite schema | Added, not yet runtime-tested | `dba/mig/sqlite/025_currency_master_rates.sql` |
| PostgreSQL schema | Added, not yet runtime-tested | `dba/mig/postgresql/025_currency_master_rates.sql` |
| Domain entities and repository contract | Added | `Currency.cs` and `ICurrencyRepository.cs` define currency and rate records and provider-neutral operations |
| Provider-neutral repository | Added, not yet integration-tested | `CurrencyRepository.cs` implements SQLite/PostgreSQL read, upsert, and rate-history queries with parameterized SQL |
| Application service | Added, not yet integration-tested | `CurrencyService.cs` validates three-letter codes, precision, positive rates, and min/max bounds |
| WPF `CurCard` integration | Currency master wired; runtime verification pending | Reuses the existing window and AppHost database configuration; loads saved currencies and persists create/update. Exchange-rate entry remains intentionally unavailable until the company's base-currency context is wired |
| Localization | Added for the currency master UI | Currency-card labels and load/save feedback are present in En, Ar, Fr and Tr JSON resources; full UI culture/RTL runtime check pending |
| Integration tests | Pending | SQLite full round-trip and migration re-run; PostgreSQL schema and repository parity. SQLite migration SQL passed an isolated in-memory schema/constraint smoke check; GitHub Actions SQLite and PostgreSQL clean-database migration runs and idempotent re-runs passed; repository-level round-trip tests remain pending |
| Permissions and audit | Partial | Successful currency saves are written to the existing operational log; permission enforcement and actor-level audit remain pending |

**Do not mark Currency Card complete until every field and every rate-history row survives save → close → reopen → compare, for both providers.**

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
