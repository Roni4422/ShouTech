# Repository Structure Audit

Branch: `reduce-2-upgrade`

## Canonical layout

| Path | Responsibility |
|---|---|
| `App.xaml`, `App.xaml.cs`, `ShouTech.ERP.csproj` | Sole Windows desktop startup and lifecycle |
| `ShouTech.sln` | Single solution for the product and supported engineering projects |
| `app/` | WPF presentation: views, view models, services, navigation, reporting, localization, themes and resources |
| `src/Dom/` | Domain entities, value types, contracts and business rules |
| `src/App/` | Application use cases and orchestration |
| `src/Infra/` | SQLite/PostgreSQL persistence and infrastructure adapters |
| `archive/legacy_inventory/` | Retained source for a retired SQL Server-only inventory adapter; excluded from the active solution |
| `dba/` | Authoritative database definitions and immutable provider migrations |
| `svc/` | Independent Windows background worker |
| `tools/` | SQL migration CLI and developer verification harnesses |
| `tests/` | Automated regression and integration tests |
| `sec/` | Native C++ security library and native tests |
| `cfg/` | Non-secret application defaults |
| `docs/` | Architecture, product, engineering and user documentation |
| `archive/` | Historical schema snapshots and legacy reference material |

## Structural corrections

- Remove the redundant `ShouTech.ERP.Com/` wrapper.
- Flatten `cod/app/src/` to `app/`.
- Preserve the old SQL Server-only inventory adapter under `archive/legacy_inventory/`; it is excluded from the active solution and desktop build.
- Use the compact `src/Dom/Dom.csproj` project path while preserving the public `Domain.dll` assembly identity. Standard .NET project conventions remain in use.
- Keep Domain, App and Infra as separate bounded technical layers. Inventory workflows use the current application services and repositories.
- Place the solution, service, database assets, docs, tools, tests, security code and configuration directly under the repository root.
- Preserve migration filenames, IDs, checksums and SQL contents.
- Remove the obsolete nested company README; the root README remains the single repository landing page.
- Keep the service, migration CLI, test projects and focused harnesses because each has a distinct operational or verification role.
- Move the old free-form kernel notes into the archive as Markdown rather than retaining a misleading C# source extension.

## Index maintenance

`docs/FILE_INDEX.md` lists all repository folders and files. Regenerate it after every structural change:

```powershell
python tools/gen_index.py
```

The index is generated from the working tree and excludes build output, package caches and IDE state.

## Current inventory and consolidation disposition

The canonical index now records **76 directories and 842 files** (excluding generated build/cache folders). The index now assigns responsibilities by the most specific architectural path, so nested folders no longer inherit an overly broad top-level description.

### Duplicate-content review

An exact Git-blob comparison found nine groups of byte-identical files. Every group belongs to provider migration history:

- PostgreSQL `003_modules.sql` and `004_modules.sql`.
- Provider-parity copies for accounting indexes, deletion audit, journal document-number uniqueness, supplier invoice reference uniqueness, invoice-line notes, UOM names and barcode templates.
- The language-neutral material-name migration appears at both IDs `070` and `127` in both providers.

These files are **not deleted or renamed**. Migration IDs and checksums are durable upgrade contracts, and the repository cannot prove from source control alone which IDs have already been applied to customer databases. The repeated `070`/`127` operation and PostgreSQL `003`/`004` pair are recorded as migration-history cleanup candidates, not safe file-removal candidates. Any correction must be forward-only and preserve existing IDs and checksums.

### Small-file review

Small files were reviewed by role rather than size. Project files, solution metadata, service configuration, test assembly setup, native source units, and paired WPF code-behind files remain separate because they are build, deployment, test, language/linkage, or source-unit boundaries. No non-migration exact duplicate blob group was found in the current tree.

### Consolidation rule

Do not merge the four .NET layer projects or the independent service, migration runner, native security library, test project, or WPF XAML/code-behind pairs solely to reduce file count. Their assembly, deployment, testing, and runtime boundaries are intentional. Further consolidation requires a call-site/build/runtime proof and a measurable reduction in complexity, not just fewer paths.
