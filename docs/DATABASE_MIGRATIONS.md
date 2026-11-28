# Database Migrations

ShouTech supports SQLite and PostgreSQL. A company selects exactly one provider; migrations for the other provider are never executed against that company database.

## Canonical layout

- SQLite bundles: `dba/mig/sqlite/`
- PostgreSQL bundles: `dba/mig/postgresql/`
- Each provider has the same ten functional bundle names: `sys`, `card`, `inv`, `acc`, `vch`, `pur`, `sal_crm`, `mfg`, `hr`, and `pos_tax`.
- The repository therefore contains twenty active SQL files in total. Provider SQL remains separate because SQLite and PostgreSQL have different dialects and capabilities.

A bundle is a compact container, not one indivisible migration. Every embedded migration starts with a `-- @shoutech-migration` marker carrying its original SQL filename (the immutable migration ID), legacy checksum variants, and a checksum of the SQL segment. The reader extracts and applies each segment separately in case-insensitive ID order. Duplicate IDs are rejected before a database is opened.

## Checksums and existing databases

- Applied migrations are recorded individually in `schema_migrations`; each migration runs in its own transaction.
- Checksum validation accepts the historical Windows CRLF checksum, Linux LF checksum, and exact original-byte checksum. This preserves compatibility with databases that were migrated from either operating system before bundling.
- New migrations are append-only. Never edit an already-applied migration or rewrite a bundle by hand.
- Existing databases without migration history are not modified automatically. Take a backup, inspect the schema and migration list, then use `--baseline-existing` only when the existing schema is verified to match the baseline.

## Add a migration

Create the provider-specific SQL source outside the active bundle directory, then append it to the appropriate functional bundle:

```powershell
python tools/bundle_migrations.py append --provider sqlite --bundle inv --id 120_inventory_rule.sql --source .\new-migrations\120_inventory_rule.sqlite.sql
python tools/bundle_migrations.py append --provider postgresql --bundle inv --id 120_inventory_rule.sql --source .\new-migrations\120_inventory_rule.postgresql.sql
python tools/bundle_migrations.py verify
```

Choose the bundle by business domain. Use the same migration ID for the equivalent SQLite and PostgreSQL change. Keep provider-specific SQL in the respective source files.

## Run migrations

```powershell
dotnet run --project tools/SqlMigrator -- --provider sqlite --connection "Data Source=./shoutech_erp.sqlite3" --dry-run
dotnet run --project tools/SqlMigrator -- --provider sqlite --connection "Data Source=./shoutech_erp.sqlite3"
dotnet run --project tools/SqlMigrator -- --provider postgresql --connection "Host=localhost;Database=shoutech;Username=...;Password=..." --dry-run
```

Connection strings can be passed through `SHOUTECH_CONNECTION_STRING` or `SHOUTECH_DB_CONNECTION`; select the provider with `SHOUTECH_DB_PROVIDER`.

The desktop startup migrator and administrative `SqlMigrator` use the same bundle reader and migration identity/checksum rules. CI verifies the twenty bundles and runs fresh-database plus repeat-run smoke tests for both providers.

## Background outbox

The migration-managed `outbound_messages` table is available on both providers. The Windows service reads `SHOUTECH_DB_CONNECTION` (or `SHOUTECH_CONNECTION_STRING`) and `SHOUTECH_DB_PROVIDER` (`SQLite` by default or `PostgreSQL`). Only HTTP/HTTPS webhook dispatch is implemented; email/SMS require explicit connectors. Queue rows should be inserted by application code through a parameterized outbox API and should not be edited manually in production.
