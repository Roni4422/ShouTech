# Repository Structure Audit

Branch: `reduce-2-upgrade`

## Canonical layout

| Path | Responsibility |
|---|---|
| `App.xaml`, `App.xaml.cs` | Sole WPF application definition and lifecycle at repository root |
| `ShouTech.ERP.csproj` | Sole Windows desktop startup project and default root-level .NET entry point |
| `build/ShouTech.sln` | Full developer/IDE solution |
| `app/` | WPF presentation, views, view models, localization, themes, resources and UI services |
| `src/Dom/` | Domain entities, value types, contracts and business rules |
| `src/App/` | Application use cases and orchestration |
| `src/Infra/` | SQLite/PostgreSQL persistence and infrastructure adapters |
| `dba/` | Authoritative database definitions and provider-specific migration bundles |
| `svc/` | Independent Windows background worker |
| `tools/` | SQL migration CLI, compact migration builder and developer verification harnesses |
| `tests/` | Automated regression and integration tests |
| `sec/` | Native C++ security library and native tests |
| `cfg/` | Non-secret application defaults |
| `docs/` | Architecture, product, engineering and user documentation |

## Structural corrections

- Removed the redundant legacy `ShouTech.ERP.Com/` wrapper and flattened the active application source into `app/`.
- Kept one startup project at the repository root; the full solution remains under `build/`.
- Removed the retired SQL Server-only inventory adapter and obsolete archive tree from the active repository. The supported product providers are SQLite and PostgreSQL only.
- Consolidated 247 individual provider migration files into ten functional bundles per provider (twenty SQL files total). Original migration IDs, SQL segments and historical checksums are retained inside the bundles; the runtime and CLI read the same format.
- Kept Domain, App and Infra as separate bounded technical layers. Their assembly and runtime boundaries are intentional.
- Kept WPF XAML and code-behind pairs together. They are generated/build boundaries, not arbitrary file fragmentation.
- Moved isolated one-purpose UI files and small view groups to their owning parent folders where namespace and build behavior remain unchanged.
- Removed obsolete nested company README material; the root README remains the single repository landing page.

## Current inventory

The generated `docs/FILE_INDEX.md` is the authoritative live inventory. At this checkpoint it records 614 files and 49 directories, excluding build output, package caches and IDE state. Regenerate after every structural change:

```powershell
python tools/gen_index.py
```

CI verifies that the committed index matches the working tree.

## Consolidation rules

- Do not create a new file for a small helper when an existing owning file can safely contain it.
- Do not split large repositories/services into many small files merely to satisfy class-per-file preferences.
- Do not combine independent .NET projects, the native security library, service, migration runner or test projects solely to reduce path count; preserve real assembly, deployment and test boundaries.
- Consolidate only after checking all call sites, XAML/resource paths, project includes and build/test results.
- Never delete an applied migration ID or rewrite its SQL history. Add a new forward-only migration through the bundle tool.
