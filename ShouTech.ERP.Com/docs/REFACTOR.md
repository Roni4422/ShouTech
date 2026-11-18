# ShouTech ERP — Architecture Refactoring Program

**Status:** Phase 0 audit and target architecture definition  
**Working branch:** `work/full-app-stabilization-2026-11-18`  
**Protected baseline:** `main`  
**Scope:** Windows desktop ERP, SQLite/PostgreSQL, background service, migration CLI, native security component.

This program is an incremental re-engineering effort. It is not a rewrite and it is not permission to delete code based on filenames, age, or apparent lack of use.

## 1. Audit snapshot

The branch tree at the audit commit contained 749 tracked files and 71 directories. The largest concentration is `cod/app` (300 files), followed by provider migrations (113 files across SQLite/PostgreSQL paths), Domain (65), Infrastructure (32), Application (27), and tests (39).

Large implementation files requiring responsibility analysis include:

| File | Approximate size | Initial concern |
|---|---:|---|
| `cod/app/src/Services/Inventory.cs` | 134 KB | Inventory workflows, persistence and domain rules are likely too concentrated |
| `src/Infra/HrRepository.cs` | 125 KB | Large repository; inspect query/command and aggregate boundaries |
| `cod/app/src/Services/RptQuery.cs` | 112 KB | Report query catalog and execution concerns may be coupled |
| `cod/app/src/Services/InvRepo.cs` | 111 KB | Persistence and inventory behavior boundary needs review |
| `cod/app/src/Services/Company.cs` | 105 KB | Company lifecycle and provisioning concerns need separation |
| `src/Infra/MfgOrderRepository.cs` | 101 KB | Manufacturing persistence responsibilities need decomposition |
| `cod/app/src/Views/WhsWin.xaml.cs` | 98 KB | UI event handling and business logic boundary needs review |
| `cod/app/src/Views/MatCard.xaml.cs` | 87 KB | UI construction, validation and persistence coupling risk |

File size is a review signal, not a deletion criterion.

## 2. Verified architectural risks

1. The WPF executable references Domain, Application, Infrastructure and the older inventory core directly.
2. The WPF project references SQLite and Npgsql packages directly, while Infrastructure also references both providers. Database access is therefore not yet exclusively behind Infrastructure.
3. The WPF project disables SDK default compile/page discovery and manually includes broad recursive globs. Moving files without updating project item rules can silently change compilation and WPF resource behavior.
4. The legacy application services remain active alongside newer repositories and application services. A feature-by-feature call-path map is required before replacing either path.
5. The test project references the WPF executable and Infrastructure, so tests are not yet cleanly separated from desktop composition and UI dependencies.
6. SQL migration files are split by provider and must remain immutable once applied; schema changes require forward-only migrations and checksum discipline.
7. The repository contains archived SQL Server snapshots and legacy schema snapshots. They are reference artifacts, not active runtime code, and must remain clearly separated from supported SQLite/PostgreSQL paths.
8. `AppHost` is currently a composition root/service-locator boundary. It must become the single explicit dependency-composition boundary rather than allowing new service-locator dependencies to spread.

## 3. Target architecture

Keep the Windows WPF desktop product and its offline SQLite capability. Adopt a modular-monolith architecture with strict dependency direction:

```text
ShouTech.Desktop (WPF)
  ├── Presentation: Views, ViewModels, navigation, localization
  └── Application contracts only
          |
          v
ShouTech.Application
  ├── UseCases / Commands / Queries
  ├── Validation and authorization orchestration
  ├── DTOs and ports (interfaces)
  └── Module boundaries
          |
          v
ShouTech.Domain
  ├── Accounting
  ├── Inventory
  ├── Sales
  ├── Purchasing
  ├── Treasury
  ├── Manufacturing
  ├── HR
  ├── FixedAssets
  ├── CRM / Projects / Quality
  └── SharedKernel (small and stable)
          
ShouTech.Infrastructure implements Application ports
  ├── Persistence / SQLite / PostgreSQL
  ├── Repositories and transaction management
  ├── File, backup, import/export adapters
  └── External integrations

ShouTech.Worker -> Application contracts + Infrastructure composition
ShouTech.Migrator -> migration engine + provider adapters
ShouTech.NativeSecurity -> isolated native boundary
dba/ -> authoritative versioned schema and seed scripts
tests/ -> Domain, Application, Infrastructure, contract and end-to-end suites
```

Dependency rules:
- Domain references no UI, database provider, filesystem, network, or desktop framework.
- Application references Domain and defines ports; it does not reference WPF or provider packages.
- Infrastructure implements Application ports and owns provider-specific SQL and transaction adapters.
- Desktop references Application contracts and composes Infrastructure only at startup.
- Worker and Migrator have independent composition roots and do not reach into WPF.
- No new business rules may be implemented in code-behind or XAML event handlers.
- No new table DDL may be introduced in runtime services; `dba/` owns schema definitions and migrations.
- Keep the modular monolith; do not split into microservices without a proven deployment/scaling requirement.

## 4. Proposed module boundaries

Each business module should have explicit domain concepts, application use cases, persistence adapters, validation, permissions, reports, localization resources and tests. Initial module boundaries:

- Platform: company, tenant/company context, users, roles, permissions, audit, settings, localization, licensing.
- Accounting: chart of accounts, fiscal calendars, journals, posting, receivables, payables, tax, bank reconciliation, budgets, closing and reversals.
- Inventory: item master, categories, units, warehouses, stock ledger, valuation, batches/serials, transfers, counts and adjustments.
- Sales: quotations, orders, delivery, invoices, returns, customer balances and pricing.
- Purchasing: requisitions, purchase orders, receipts, supplier invoices, returns and supplier balances.
- Treasury: cash/bank accounts, receipts, payments, cheques and reconciliation.
- Manufacturing: BOM, routes, work orders, material issue/return, WIP, costing and quality gates.
- HR: employee records, attendance, leave, payroll and benefits.
- Fixed Assets: acquisition, depreciation, transfer, impairment and disposal.
- CRM/Projects/Quality/POS/Reporting: each with clear ownership and integration contracts.

A module must not write another module's tables directly. Cross-module behavior goes through Application use cases, domain events or explicitly defined integration contracts, with the same database transaction where accounting/stock consistency requires it.

## 5. Refactoring sequence and gates

### Phase 0 — Inventory and safety baseline
- Capture the full tree, project graph, package graph, startup flow, window routes, database access paths, migration inventory and test coverage.
- Identify every public entry point and every module's persistence owner.
- Record current build/test results and known failures.
- Do not delete or move production code in this phase.

**Exit gate:** architecture map and risk register reviewed; a reproducible baseline exists.

### Phase 1 — Build and dependency foundations
- Centralize common build properties and package versions without upgrading packages blindly.
- Replace fragile broad compile globs with explicit, predictable project item boundaries only after WPF resource inclusion is tested.
- Establish dependency rules and architecture tests.
- Separate test projects into domain/application/infrastructure/integration where practical.
- Preserve Windows x64/WPF runtime and offline SQLite packaging.

**Exit gate:** clean restore/build; architecture tests pass; existing UI resources are included; no behavior changes.

### Phase 2 — Platform foundation
- Introduce explicit dependency injection/composition and typed options.
- Define provider-neutral connection/transaction abstractions.
- Centralize company context, authorization, audit, clock, numbering and error handling.
- Add health/diagnostic reporting without leaking secrets.

**Exit gate:** startup, login, company switching, permissions, audit and both DB providers pass integration tests.

### Phase 3 — Strangler migration by business module
Migrate one vertical slice at a time. For each feature:
1. map current UI -> service -> repository -> tables -> reports;
2. define domain invariants and application use cases;
3. add characterization/regression tests against current behavior;
4. implement the new path behind an adapter;
5. run old/new parity checks on disposable databases;
6. switch the UI to the new use case;
7. remove the old path only after static and runtime reference checks plus full regression.

Recommended order: Platform -> Accounting core -> Inventory -> Sales/Purchasing -> Treasury -> Manufacturing -> HR -> Fixed Assets -> CRM/Quality/POS -> Reports.

**Exit gate:** module's business workflows and cross-module postings are covered; old path has no runtime callers; rollback path is documented.

### Phase 4 — UI decomposition and consistency
- Move business logic out of code-behind into ViewModels/use cases.
- Establish shared form/list/report/navigation patterns.
- Make localization resource-driven; no hardcoded Arabic UI strings or Arabic source comments.
- Keep the main shell's menu bar and Ribbon structure.
- Test every Ribbon command, window route, permission and close/reopen lifecycle.

**Exit gate:** window inventory is fully mapped; every visible command has a tested action or is intentionally disabled with explanation.

### Phase 5 — Data and migration hardening
- Maintain one authoritative schema definition per provider under `dba/`.
- Verify migration numbering, dependencies, checksums and idempotency.
- Test empty install, upgrade from supported versions, rollback/backup recovery and SQLite/PostgreSQL parity.
- Validate foreign keys, indexes, uniqueness, decimal precision, tenant/company isolation and referential integrity.
- Never rewrite already-applied migration files; add forward migrations.

**Exit gate:** clean-install and upgrade matrices pass on both providers; restore from backup is proven.

### Phase 6 — Remove dead code and release hardening
- Generate static dependency reports and runtime coverage evidence.
- Classify candidates as active, compatibility adapter, generated/resource, archival/reference, dead-confirmed or unresolved.
- Delete only dead-confirmed files; retain required historical migrations and legal/reference assets.
- Run full build, all tests, packaging, launch smoke tests and critical business journeys.

**Exit gate:** zero unexplained project references, zero unresolved startup paths, complete regression report and release artifact.

## 6. File disposition policy

Every file receives one of these classifications:

- **Active:** compiled, loaded, invoked, embedded or required at runtime.
- **Compatibility:** temporarily supports an old API/data format while migration is in progress.
- **Generated/resource:** generated code, XAML resources, language packs, templates, icons, certificates or native artifacts required by packaging.
- **Historical:** applied migrations, schema snapshots, archived import formats or reproducibility artifacts.
- **Dead-confirmed:** no static references, no reflection/XAML/configuration/DI registration, no build/package inclusion, no runtime/plugin entry point and no supported external contract.
- **Unresolved:** insufficient evidence; keep it and investigate.

Deletion requires a recorded path, reason, reference scan, build/test evidence and recovery commit. Historical migrations are never removed merely because they are no longer executed on a new database.

## 7. Quality gates

- Build: Debug and Release, Windows x64, warnings reviewed.
- Tests: Domain unit, Application use-case, Infrastructure provider contract, integration, UI navigation/smoke and end-to-end business journeys.
- Accounting: balanced postings, same-transaction stock/GL, fiscal-period locks, reversals, audit trail and no permanent deletion of posted documents.
- Inventory: stock ledger integrity, valuation layers, negative-stock policy, units, batch/serial traceability and warehouse isolation.
- Security: authorization on both UI and application boundary, password hashing/protected secrets, tenant/company isolation, safe import/export and audit.
- Data: SQLite/PostgreSQL schema parity, migrations, backup/restore, concurrency and precision.
- UX: every Ribbon/menu action maps to a working use case; localized labels and validation; keyboard navigation and accessibility.
- Operations: structured logs, stable error codes, diagnostics, crash-safe startup and upgrade path.

## 8. Explicit non-goals

- Do not rewrite the product in another language or framework.
- Do not replace WPF solely for fashion.
- Do not introduce microservices, message brokers or cloud dependencies into the offline desktop core without a concrete requirement.
- Do not remove SQLite support.
- Do not delete legacy files before proving they are unused.
- Do not claim full ERP parity based on the presence of windows, SQL tables or documentation alone.

## 9. Reference architecture research

The target borrows proven principles rather than copying another product's stack:

- ERPNext/Frappe: modular business capabilities, metadata-aware document models, centralized lifecycle and migration tooling.
- Odoo: explicit module boundaries, separation of presentation/business/data tiers, small cohesive methods, transactional behavior and module-local tests.
- ShouTech-specific adaptation: Windows WPF offline-first client, SQLite for local operation, PostgreSQL for multi-user deployments, provider-specific migrations, auditable accounting and controlled integration between modules.

References:
- https://docs.frappe.io/framework/user/en/basics/architecture
- https://docs.frappe.io/framework/user/en/basics/doctypes
- https://docs.frappe.io/framework/user/en/database-migrations
- https://www.odoo.com/documentation/19.0/developer/tutorials/server_framework_101/01_architecture.html
- https://www.odoo.com/documentation/19.0/contributing/development/coding_guidelines.html
- https://www.odoo.com/documentation/19.0/developer/tutorials/unit_tests.html

## 10. Current decision

This commit defines the audited baseline and the safe refactoring sequence. It does not claim the application has already been restructured. Production code movement begins only after the Phase 0 inventory is complete and each change can be built and tested independently.


## 11. Short naming convention

Core project directories now use concise, readable names: `src/Domain`, `src/App`, and `src/Infra`. Project filenames are correspondingly `Domain.csproj`, `App.csproj`, and `Infra.csproj`. The C# namespaces and assembly identities remain unchanged in this compatibility step.

Naming rules for subsequent work:
- Prefer one clear word for a folder: `Domain`, `App`, `Infra`, `Views`, `Reports`, `Tests`, `Migrations`.
- Use concise but meaningful PascalCase filenames: `Rpt.cs`, `InvRepo.cs`, `Company.cs`; avoid unexplained acronyms and one-letter names.
- Keep established domain acronyms only when they are common in the ERP codebase (GL, HR, POS, CRM, SQL).
- Do not shorten public type names, database table names, migration IDs, API routes or serialized field names as part of a filesystem rename; those are compatibility contracts.
- Keep namespaces and assembly names stable until a separate compatibility review approves a change.
- Rename a file only after checking project globs, XAML/resource pack URIs, reflection, scripts, CI paths, docs and packaging references.

## 12. Large-file preservation policy

Large, established source files are valuable project assets and are not refactoring targets merely because of their line count. Keep files such as the inventory, repository, company, reporting and HR implementation units intact unless a concrete defect or dependency-boundary problem requires a narrowly scoped change.

- Do not split a large file into many smaller files for stylistic reasons or to meet an arbitrary line-count target.
- Prefer improving the existing file in place: clarify method flow, remove proven duplication, strengthen validation and transactions, improve naming, document non-obvious invariants, and add focused tests.
- Add a new source file only when it represents a genuinely distinct responsibility, has a clear owner, and materially improves maintainability without multiplying navigation overhead.
- Preserve public types, established call paths and compatibility behavior unless a tested migration is included.
- Before any structural change, inspect the complete file, its callers, project inclusion rules, XAML/resource references and tests. Make one bounded change at a time.
- File size alone is never evidence that code is dead, poorly designed or should be deleted.

**Decision:** ShouTech will favor cohesive, well-organized implementation files over artificial fragmentation. Refactoring quality is measured by correctness, cohesion, testability and ease of safe change—not by the number or average size of files.


## 13. Window inventory and migration

The source-level inventory is maintained in [WINDOW-INVENTORY.md](WINDOW-INVENTORY.md). It is a starting index only: a filename is not proof that a type is a live window, and a C#-only file may be a programmatic window or a support type.

### 13.1 Migration rules

- Do not split large window code-behind files merely to reduce file size or line count.
- Move an existing XAML/code-behind pair together and preserve its namespace, x:Class, public type and runtime contract unless a separately reviewed compatibility change is necessary.
- Do not move a file until its type, all callers, Ribbon/menu route keys, constructor dependencies, services, repositories, database tables, permissions, localization keys and resource dependencies are mapped.
- Check WPF Page/Compile items, pack URIs, merged dictionaries, icons, templates, StartupUri, reflection and runtime file paths before and after every move.
- Keep the main shell's two-bar structure (menu bar and Ribbon); every visible command must open its intended functional window or be intentionally disabled with an explanation.
- Do not leave placeholder routes where a real window/use case exists. Do not create duplicate windows to satisfy a route.
- Move in bounded business-module waves. Do not relocate the entire Views tree in one commit.

### 13.2 Migration waves

1. Platform shell, company, users, roles, permissions, settings, backup and restore.
2. Inventory cards and operations: materials/items, categories, warehouses, barcode, transfers, counts, adjustments, batches and serials.
3. Sales and purchasing documents: quotations, orders, invoices, returns and delivery/receipt flows.
4. Accounting and treasury: chart of accounts, journals, vouchers, receipts, payments, cash, banks and reconciliation.
5. Manufacturing, HR, CRM, projects and quality.
6. Reports, import/export, maintenance, licensing and diagnostics.

### 13.3 Completion and verification gate

A window is migrated only when its final path, navigation route, permission, localization/resources, service/repository and database dependencies are recorded; its main workflows remain functional; and its navigation/open/close lifecycle has been tested. For data-entry windows test create, read, update, validation, search and business-rule-compliant delete/cancel. For reports test filters, query execution and empty/error states.

After each wave:
- Build the WPF application in Debug and Release on Windows.
- Run unit and integration tests relevant to the wave.
- Run a navigation smoke test for every affected Ribbon/menu route.
- Verify SQLite behavior and PostgreSQL behavior where that feature supports both providers.
- Inspect WPF resource loading at runtime; a successful compile alone is insufficient.
- Record failures, fixes and rerun evidence in the inventory.

The final window-migration gate requires every inventoried candidate to be classified (window, view, helper, base type, view model, resource or historical), every visible route to have a verified destination, and a complete regression report. Do not claim completion from a file move or build-only result.
