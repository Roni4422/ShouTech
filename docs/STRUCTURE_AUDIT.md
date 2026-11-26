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
| `src/Inv/` | Inventory core library |
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
- Move the inventory library from `cod/cor/` to `src/Inv/`.
- Use the compact `src/Dom/Dom.csproj` project path while preserving the public `Domain.dll` assembly identity. Standard .NET project conventions remain in use.
- Keep Domain, App, Infra and Inv as separate bounded technical layers.
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
