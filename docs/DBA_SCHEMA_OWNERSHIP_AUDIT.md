# Database Schema Ownership Audit (main)

## Goal

The canonical definitions of application tables must live under `dba/` (preferably provider-specific, ordered SQL migrations). C# files under `cod/app/src/Services/` should contain runtime operations and migration orchestration, not embedded `CREATE TABLE` definitions. SQL in the legacy SQL Server archive is reference material and is not a deployable SQLite/PostgreSQL schema.

## Audit result

The historical `docs/SCHEMA_AUDIT_PHASE1.md` report records an earlier baseline (540 SQL table-definition occurrences, 120 repeated names, and 55 runtime C# `CREATE TABLE` statements); those counts are not a current measurement. Run `python tools/schema_audit.py` to produce a fresh inventory across table, column, index, trigger, function, view, and drop DDL. The current review found that most service table bootstrap code has already been removed, while provider-specific delete-audit trigger installation still remains in `RecycleBinSvc.cs` and is a known outstanding migration task.

### Confirmed service files containing table/schema DDL

| File | Findings |
|---|---|
| `cod/app/src/Services/Accounting.cs` | `accounts` table defined twice (SQLite/PostgreSQL variants); creates indexes; adds columns dynamically via `ALTER TABLE`. |
| `cod/app/src/Services/Data.cs` | Creates core tables including `companies`, `users`, `fiscal_years`, `roles`, `permissions`, `products`, journal tables, stock tables, and permission tables; also creates indexes and adds columns dynamically. Several tables already exist in migrations or legacy schema snapshots. |
| `cod/app/src/Services/DocSchema.cs` | Embeds separate SQLite and PostgreSQL DDL strings for `sales_documents`, `sales_document_lines`, `purchase_documents`, `purchase_document_lines`, `party_ledger`, and `stock_movements`, plus indexes. |
| `cod/app/src/Services/InvRepo.cs` | Defines `document_reversals` separately for the two providers. |
| `cod/app/src/Services/Inventory.cs` | Creates `materials`, material child tables, stock tables, `material_groups`, `material_group_sequences`, and `warehouses`; several table definitions are repeated for provider variants; also performs dynamic column additions and creates indexes. |
| `cod/app/src/Services/RecycleBinSvc.cs` | Table creation was removed; provider-specific audit function/trigger DDL and trigger discovery/installation still run at application runtime. Move this to provider-specific DBA migrations before claiming schema ownership is complete. |
| `tools/SqlMigrator/Program.cs` | The migrator now bootstraps `schema_migrations` from `dba/mig/{provider}/000_history.sql`; it no longer contains the table definition inline. The bootstrap SQL is also processed and recorded by the normal migration loop. |

A repository-wide search also found schema snapshots in `dba/lite.sql` and `dba/pg.sql`, older scripts under `dba/`, and active migrations under `dba/mig/sqlite/` and `dba/mig/postgresql/`. The archived SQL Server scripts under `archive/legacy_sqlserver/` must remain clearly separated from deployable migrations.

## Implementation progress (2026-10-15)

The first centralization pass has been committed directly to `main`:

- `DocSchema.cs`: embedded SQLite/PostgreSQL table DDL removed; now validates the DBA-managed commercial-document schema.
- `InvRepo.cs`: inline creation of `document_reversals` removed; now validates its presence.
- `RecycleBinSvc.cs`: inline creation of `deletion_audit_log` removed; table presence is validated before audit operations. Provider-specific audit-trigger installation remains in the service and is a separate schema-ownership follow-up.
- `Accounting.cs`: inline account-table DDL and its runtime column bootstrap removed.
- `Inventory.cs`: inline DDL for materials, child tables, material groups, group sequences, and warehouses removed. The old warehouse compatibility path that could drop a legacy `warehouses` table was removed to avoid data loss. The service now validates required tables.
- `Data.cs`: inline core-table bootstrap and development-only table-drop reset removed; permission-table creation and runtime permission-column additions were moved to DBA migrations.
- Added provider-specific migrations for commercial documents/reversals, audit storage, accounts, inventory, groups/warehouses, and the missing SQLite category/brand tables. SQLite migrations now support the explicit `-- @if-column-missing table.column` directive for safe conditional column additions to existing databases.

This is a significant first pass, not a claim that the entire repository has been audited and validated end-to-end. The PostgreSQL migration numbering/content issue documented below remains unresolved. GitHub Actions builds triggered during this pass initially reported compile errors; fixes were committed, and the latest CI run must complete before treating the code as build-verified. Fresh SQLite/PostgreSQL migration execution and representative existing-database upgrade tests are still required.

## Important migration defect found

In `dba/mig/postgresql/`, `003_modules.sql` and `004_modules.sql` have identical Git blob hashes and identical content. Also, `003_rbac.sql` shares the `003` filename prefix with `003_modules.sql`. The current migrator derives each migration ID from the path relative to the provider migration directory and sorts those IDs; duplicate content and unclear ordering require correction.

**Do not simply delete or rename already-released migration files.** Existing databases record migration IDs and checksums. Before repairing the migration sequence, the migrator needs a compatibility plan for already-recorded IDs and tests for both fresh and previously migrated databases.

## Required refactor plan

1. Treat the current main-branch code as the baseline and preserve it in Git history.
2. Consolidate every supported SQLite/PostgreSQL table definition into ordered SQL files under `dba/mig/sqlite/` and `dba/mig/postgresql/`. Keep provider-specific syntax separate.
3. Preserve data and behavior: translate each runtime `CREATE TABLE`, index, and column-addition path into appropriate migrations; do not merely remove runtime SQL.
4. Refactor services to execute parameterized CRUD/business operations only. They must not own table definitions. Schema upgrades should be run through one migration path.
5. Resolve existing duplicate schema sources (`dba/lite.sql`, `dba/pg.sql`, older root-level SQL scripts, and migrations) by documenting which are active, legacy, or generated. Do not delete compatibility files until references and deployment workflows are checked.
6. Repair migration identity/order compatibly. Existing applied migration files remain immutable; introduce forward migrations or a deliberate compatibility mechanism instead of rewriting checksummed migrations.
7. Add a static audit/test that scans non-archive C# source for table DDL and fails when new schema definitions are added outside `dba/`.
8. Validate: clean SQLite creation, clean PostgreSQL creation, upgrade of representative existing databases, application startup, company initialization, inventory/document flows, permissions, and accounting/reversal behavior.

## Completion criteria

The refactor is complete only when all supported table-creation DDL is under `dba/`, service C# contains no table-creation definitions (except any explicitly documented bootstrap exception that is also moved into DBA scripts), migration IDs are unique and compatible, and the build plus database integration tests pass for both supported providers.

This document records the audit findings and safe execution order; it does not claim that the full refactor or integration tests have already been completed.


## Follow-up remediation (2026-10-15)

### Completed in this pass

- Added `dba/mig/sqlite/000_history.sql` and `dba/mig/postgresql/000_history.sql` as the canonical definitions for the migration-history table.
- Changed `tools/SqlMigrator/Program.cs` to load the provider's `000_history.sql` before reading the history ledger. The migration is then processed by the normal ordered migration loop and recorded with its checksum.
- This is compatible with an existing database that already has `schema_migrations`: `CREATE TABLE IF NOT EXISTS` leaves it intact, and the new migration ID is recorded without rewriting existing migration IDs or checksums.
- Expanded `tools/schema_audit.py` to inventory CREATE TABLE, ALTER TABLE, CREATE INDEX, CREATE TRIGGER, CREATE FUNCTION, CREATE VIEW, and DROP TABLE statements; it separates application-service DDL from non-archive SQL outside `dba/`, and supports `--strict` to fail when service DDL remains.

### Policy decision: where schema belongs

- **Application/business tables, columns, constraints, indexes, views, triggers, and database functions:** canonical definitions belong in provider-specific DBA migrations under `dba/mig/sqlite/` and `dba/mig/postgresql/`.
- **Services/repositories:** may perform CRUD, business validation, metadata discovery for safe queries, and explicit schema-presence checks. They must not create or alter persistent schema objects.
- **Migration runner:** may orchestrate migrations and bootstrap the migration ledger, but the ledger's DDL must itself come from the DBA-owned bootstrap SQL.
- **Tests:** may create isolated temporary/in-memory tables to test behavior. Such fixtures are not production schema.
- **Archive snapshots and legacy SQL Server scripts:** remain reference-only and must not be part of the active migration path.
- **Seed/reference data:** may be managed by DBA migrations when it is versioned and required for application operation; user/company-specific operational data belongs in application services.

### Outstanding item — audit triggers

`RecycleBinSvc.cs` still discovers tables and installs provider-specific audit triggers/functions dynamically. This is not compliant with the policy above. It has deliberately not been removed in this pass because deleting it without a complete replacement would silently weaken hard-delete and soft-delete auditing. The next safe change must:
1. define an explicit, reviewed set of auditable tables and keys per provider;
2. add the SQLite and PostgreSQL trigger/function definitions as new forward-only migrations (do not edit already-released migrations);
3. preserve the existing deletion-audit table contract and snapshot/actor semantics, or explicitly version and test any intentional change;
4. remove runtime DDL and auto-discovery from `RecycleBinSvc.cs`;
5. test fresh databases and upgrades from databases where the old service already installed triggers.

Do not mark schema ownership complete until that migration and both-provider integration tests pass.
