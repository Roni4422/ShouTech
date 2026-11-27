# Archived legacy inventory adapter

This folder preserves the former `ShouTech.Core.Inv` project for historical reference only.

It is intentionally excluded from `ShouTech.sln` and the production WPF project because its implementation is tied to SQL Server-specific objects (`dbo.*` and stored procedures) and is not compatible with the supported SQLite/PostgreSQL runtime.

The active inventory implementation lives in the application and infrastructure layers. Do not restore this project to the build or route production operations through it. Any required behavior must be migrated into the active provider-neutral services with tests for SQLite and PostgreSQL first.
