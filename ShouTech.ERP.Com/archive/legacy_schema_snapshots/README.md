# Superseded schema snapshots

The old monolithic SQLite and PostgreSQL schema snapshots are retained only for reference. They duplicated the migration-managed schema and contained outdated column definitions. Do not execute these snapshots. Active database changes must be implemented as ordered migrations under `dba/mig/sqlite/` and `dba/mig/postgresql/`.
