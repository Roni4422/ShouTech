# Repository Structure and Bootstrap Audit

Branch: `reduce-2-upgrade`
Audited HEAD: `e155ad05db01bd0c8432939ed17698348d7720e7`
Scope: first-pass structural inventory, exact-blob duplication, migration safety, and startup entry points.

## Inventory

- Recursive Git tree is complete (not truncated): 840 tracked files.
- Exact duplicate blob groups: 8.
- No files were deleted or merged in this pass. Identical migration contents are not sufficient evidence for safe deletion because migration filenames/order/checksums are part of the database history.

## Exact duplicate blobs

| Paths | Assessment |
|---|---|
| `dba/mig/postgresql/003_modules.sql` and `004_modules.sql` | Exact same blob, but both are migration IDs. Preserve both; deleting/renaming either can invalidate deployed migration ledgers or alter replay order. |
| `dba/mig/postgresql/009_accounting_indexes.sql` and `dba/mig/sqlite/007_accounting_indexes.sql` | Cross-provider exact bytes; retain as provider-specific migration assets. |
| `dba/mig/postgresql/013_deletion_audit.sql` and `dba/mig/sqlite/011_deletion_audit.sql` | Cross-provider exact bytes; retain. |
| `dba/mig/postgresql/015_journal_document_number_unique.sql` and `dba/mig/sqlite/013_journal_document_number_unique.sql` | Cross-provider exact bytes; retain. |
| `dba/mig/postgresql/084_supplier_invoice_reference_unique.sql` and `dba/mig/sqlite/084_supplier_invoice_reference_unique.sql` | Cross-provider exact bytes; retain. |
| `dba/mig/postgresql/120_invoice_line_notes.sql` and `dba/mig/sqlite/120_invoice_line_notes.sql` | Cross-provider exact bytes; retain. |
| `dba/mig/postgresql/121_uom_names.sql` and `dba/mig/sqlite/121_uom_names.sql` | Cross-provider exact bytes; retain. |
| `dba/mig/postgresql/126_barcode_template.sql` and `dba/mig/sqlite/126_barcode_template.sql` | Cross-provider exact bytes; retain. |

## Startup and host entry points

- Desktop WPF entry point: `cod/app/src/App.xaml` is explicitly included as the `ApplicationDefinition` in `cod/app/src/MainApp.csproj`; `App.xaml.cs` contains application lifecycle/bootstrap code.
- Desktop composition root: `cod/app/src/Services/AppHost.cs` owns application-wide service construction and initialization. It is a composition root/service locator, not a second executable entry point.
- Separate Windows background service: `svc/Program.cs` builds and runs a Generic Host with the Windows Service integration. This is a distinct deployable process and must not be merged into the desktop WPF startup.
- Developer/test harnesses each have their own `Program.cs`; these are isolated executable tools and are not desktop launch paths.
- `src/App/App.csproj` is a class library (no executable output type); it is not an application entry point.
- The desktop project currently compiles source files by wildcard and explicitly includes XAML. Any file merge/removal requires reference, reflection, XAML, resource, and project-item checks.

## Migration finding fixed in this pass

The failed PostgreSQL smoke run reached migration `104_pos_remainder.sql` and failed creating `ix_sales_documents_exchange_ref`: `sales_documents.is_deleted` is declared as `INT NOT NULL DEFAULT 0` in migration `012_commercial_documents.sql`, but migration 104 compared it with PostgreSQL boolean `FALSE`. The predicate has been corrected to `is_deleted = 0` on this branch. This is a pre-release repair of a migration that fails on a clean PostgreSQL database before its history row can be committed; verify against deployed database histories before distributing the migration.

## Next safe structural steps

1. Trace all calls to `AppHost.Initialize`, `SchemaBootstrapper.EnsureSchema`, and `SchemaMigrator.ApplyAllAsync` before changing startup.
2. Compare embedded schema bootstrap resources with `dba/mig`; remove no bootstrap path until database creation, upgrade, baseline, and recovery flows are proven equivalent.
3. Add a migration consistency test that validates provider-specific predicates against declared column types, and keep SQLite/PostgreSQL smoke tests as release gates.
4. Only merge source files with cohesive ownership after checking all symbol references, XAML/resource names, reflection/dynamic loading, and project wildcard inclusion.

## Additional startup-flow finding

The desktop launch path is more complex than a single call into the composition root:

1. WPF invokes `App.OnStartup` from `App.xaml`.
2. Startup loads user preferences and language, builds a local `ServiceCollection`, and builds `_serviceProvider`.
3. It calls `SchemaBootstrapper.EnsureSchema()` **before** `AppHost.Initialize()`.
4. It initializes recycle-bin audit infrastructure, then calls `AppHost.Initialize()`.
5. Login and main-window transitions call `SchemaBootstrapper.EnsureSchema()` again; the helper caches database keys within the process.
6. `AppHost.Initialize()` is also exposed through a compatibility facade in `App.xaml.cs`, so call-site/reference analysis is required before changing its visibility or name.

This is a confirmed dual bootstrap concern: desktop startup schema provisioning is implemented by an embedded-resource `SchemaBootstrapper`, while `SchemaMigrator` separately applies ordered `dba/mig` scripts. The next structural phase must compare the embedded `ShouTech.App.dba.lite.sql` / `ShouTech.App.dba.pg.sql` resources and their execution call sites against the migration/baseline flows. Do not simply replace one with the other until fresh install, existing database, upgrade, company switching, and recovery behavior are covered by tests.

## Embedded schema resource verification

A recursive tree check found no tracked SQL source outside `dba` except explicitly archived legacy snapshots. In addition, `MainApp.csproj` sets `EnableDefaultEmbeddedResourceItems=false` and contains no explicit `EmbeddedResource` item for `ShouTech.App.dba.lite.sql` or `ShouTech.App.dba.pg.sql`; it copies the provider migration files as `None` items instead. Therefore the resource names requested by `SchemaBootstrapper.LoadSql` have no corresponding tracked source/build inclusion in the current tree. Because `EnsureSchema` logs and returns when the resource is missing, this bootstrap path can silently do no schema work. Confirm this from an actual Windows build's manifest/resource list before retiring or replacing it; the source-level evidence is sufficient to prioritize it as a defect candidate, not to assert runtime behavior without a build artifact.
