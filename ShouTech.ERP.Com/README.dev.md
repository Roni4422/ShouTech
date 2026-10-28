# ShouTech ERP Development Guide

## Supported databases

ShouTech supports SQLite and PostgreSQL only. SQL Server-specific scripts and implementations are not part of the active runtime.

- Install the .NET 8 SDK.
- SQLite is suitable for local development and single-user deployments.
- PostgreSQL is suitable for server deployments and concurrent multi-user workloads.

## Configure and run

Use `cfg/appset.json` for non-secret provider defaults. Keep credentials out of source control and provide connection strings through `SHOUTECH_CONNECTION_STRING` or `SHOUTECH_DB_CONNECTION`. Select the provider with `SHOUTECH_DB_PROVIDER=sqlite` or `SHOUTECH_DB_PROVIDER=postgresql`.

Run `dotnet run --project tools/SqlMigrator -- --provider sqlite --connection "Data Source=./shoutech_erp.sqlite3" --dry-run` to inspect migrations before applying them. Review `docs/DATABASE_MIGRATIONS.md` before migrating an existing database.

The Windows background service currently runs the provider-neutral outbound webhook queue worker. Configure `SHOUTECH_DB_CONNECTION` and optionally `SHOUTECH_DB_PROVIDER=SQLite|PostgreSQL`. POS synchronization is intentionally not advertised as active until a portable, idempotent synchronization contract is defined. See `docs/ALAMEEN_LOGIC_ADAPTATION.md` for the business rules extracted from the Al-Ameen reference archive and the remaining accounting reversal caveat.

## Accounting chart and fiscal close controls

The provider-neutral `AccountingStructureRepository` is exposed through `AppHost.AccountingStructure`. It creates company-specific chart accounts and calendar fiscal years with monthly periods, closes periods in chronological order, and closes years only after all periods are closed and revenue/expense balances have been cleared by a posted closing entry. See `docs/ACCOUNTING_CHART_PERIODS_YEAR_CLOSE.md` before using these controls. Existing databases should be backed up and their chart of accounts and fiscal-year dates reviewed before new postings are enabled.
