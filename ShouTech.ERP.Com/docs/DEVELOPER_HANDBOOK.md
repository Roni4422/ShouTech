# ShouTech Developer Handbook

This handbook defines the minimum safe way to change ShouTech. The [Developer Roadmap](DEVELOPER_ROADMAP.md) determines what should be built and in what order; this handbook defines how to build it.

## 1. Before changing code

- Read the repository [README](../README.md), [Developer Guide](../README.dev.md), and [Architecture](../ARCHITECTURE.md).
- Read the relevant feature design and current module status in [Module Catalog](MODULE_CATALOG.md).
- Identify the active branch, current CI state, database migration baseline, and relevant tests.
- Search for existing implementations before creating new services, tables, windows, or alternate business paths.
- Treat older assessments as dated evidence, not necessarily current truth.

## 2. Architecture and dependency rules

ShouTech is a transitional WPF desktop application. The repository contains a newer Domain/Application/Infrastructure structure alongside established WPF services and inventory code. Do not assume every module has already migrated to the newer layers.

- Domain: business entities, value objects, enums and contracts; no WPF or provider-specific SQL.
- Application: use cases, orchestration, authorization requirements and business workflows.
- Infrastructure: provider-specific persistence, external adapters and technical implementations.
- WPF client: views, view models, navigation, localization and presentation orchestration.
- Existing `cod/app` and `cod/cor` code may still own live workflows. Trace usage before moving or replacing it.
- `svc` owns background processing; `tools/SqlMigrator` owns deployment-time schema migration.
- `sec` is a separate native C++ project; do not assume it is included in the .NET solution build.

Avoid new parallel accounting, stock, tax, numbering, authorization, or persistence engines. Reuse the authoritative service path or document and test a deliberate migration.

## 3. Database and persistence

- SQLite and PostgreSQL are the supported active providers.
- Put active table and index definitions in `dba/mig/sqlite` and `dba/mig/postgresql`.
- Do not embed table-creation DDL in runtime services or views.
- Keep provider migrations equivalent in business meaning, constraints, indexes and defaults.
- Never edit an already-applied migration. Add a new forward migration.
- Preserve migration IDs and checksums. Test both a clean database and an existing database upgrade.
- Back up existing data before baselining or applying schema changes.
- Use parameterized SQL; never concatenate user input into SQL.
- Scope every company-owned query by company identity and test cross-company denial.
- Use transactions for related document, stock, tax, GL, and outbox writes. Define rollback and retry behavior.
- Avoid provider-specific assumptions about booleans, dates, decimal precision, GUIDs, generated IDs, locking and conflict clauses.

Read [Database Migrations](DATABASE_MIGRATIONS.md) and [DBA Schema Ownership Audit](DBA_SCHEMA_OWNERSHIP_AUDIT.md) before schema work.

## 4. Accounting and operational invariants

- Every journal must balance in its transaction currency and base currency according to the configured precision and exchange-rate policy.
- Posted documents are immutable business history. Correct them with linked cancellation/reversal/credit/debit documents.
- Posting must be idempotent and must retain source document references.
- Period locks must be enforced in the service layer, not only in the UI.
- Subledgers must reconcile to control accounts.
- Stock quantity and valuation must be traceable to source movements and costing policy.
- Returns, refunds, reversals and cancellations must reverse or compensate all relevant effects—not only the visible document status.
- Tax rules must be effective-dated and company-specific. Country selection alone must never activate taxation.
- Never use floating-point types for monetary calculations. Use decimal/numeric database types and explicit rounding rules.
- Define behavior for partial fulfillment, partial payment, exchange differences, discounts, landed cost, write-offs and rounding before implementing a workflow.

## 5. Security, privacy and audit

- Enforce permissions at service/application boundaries for every read and write operation.
- Treat UI visibility as usability, not authorization.
- Never store plaintext passwords, API secrets, payment credentials or database credentials in source control.
- Use least-privilege service accounts and scoped, expiring, revocable API credentials.
- Do not include secrets, full payment data or unnecessary personal data in logs/audit details.
- Audit sensitive configuration changes, posting, reversal, imports/exports, permission changes and administrative actions.
- A best-effort audit writer is not a substitute for transactional audit requirements. If an event must never be lost, write it in the same transaction or use a transactional outbox.
- AI features must use the caller's company scope and permissions; external model calls require explicit data-governance review.

## 6. UI, windows and localization

- Keep the main window's agreed structure: one menu bar and one Ribbon.
- Reuse the established base-window, navigation, numbering, validation and visual infrastructure.
- Input windows opened as New must generate the next valid number from persisted state; prevent duplicate numbers under concurrency.
- Search, filters, sorting and report date ranges must operate on real persisted data.
- Reports must be distinguishable from data-entry windows and provide appropriate filtering and drill-through.
- Use resource keys for all UI strings. English is the default/source language.
- Do not add Arabic or other translated text directly to source code, comments, SQL messages or UI markup; add resource entries instead.
- Verify RTL layout, keyboard navigation, date/number/currency culture, accessibility and text expansion.
- Do not claim a window is complete until its main-window navigation, permissions, persistence, validation, localization and tests are connected.

## 7. Tests and quality gates

At minimum, run the applicable checks:

1. Restore and build the solution in Release configuration.
2. Run the complete .NET test suite.
3. Run targeted accounting, inventory, tax, CRM/POS or other affected tests.
4. Apply all migrations to clean SQLite and PostgreSQL databases.
5. Re-run migrations against the same databases to prove repeatability.
6. Upgrade a representative prior schema and verify data preservation.
7. Run source-language and localization checks.
8. Test permission denial, company isolation, validation, rollback, retries and duplicate requests.
9. Inspect test logs and warnings; do not hide failures by weakening assertions.
10. Link CI runs and summarize what was and was not executed.

A queued or running CI job is not a passing test. A successful build is not proof of business correctness.

## 8. Pull request requirements

Every implementation PR should state:

- Business problem and user workflow.
- Affected modules, services, windows, schemas and providers.
- Design decision and why existing paths are reused or replaced.
- Migration impact, backup needs and rollback/recovery plan.
- Tests added and exact commands or CI runs.
- Known gaps, assumptions, performance and security implications.
- Documentation updated and status in the roadmap/module catalog.

Keep changes reviewable. Do not combine unrelated large refactors with accounting or schema changes.

## 9. External references

Use the official sources listed in [Engineering Reference Library](ENGINEERING_REFERENCE_LIBRARY.md). Treat vendor docs as descriptions of intended workflows, not proof of internal implementation. Confirm tax, e-invoicing, payment and privacy obligations against the relevant official jurisdiction authority and current standards.
