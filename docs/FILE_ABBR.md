# ShouTech ERP — Naming and Abbreviation Reference

This document describes the names and locations that exist on the current repository branch. The generated [File and Folder Index](FILE_INDEX.md) is the authoritative inventory of individual paths; this guide explains the naming conventions and the principal abbreviations.

## Naming rules

- Keep established public type names, database identifiers, migration IDs, resource keys and serialized contract names stable unless a separately reviewed compatibility change is made.
- Use concise, meaningful names for new internal files and folders. Prefer an established abbreviation over inventing another spelling.
- Do not shorten names when doing so makes the business meaning ambiguous.
- Keep WPF XAML and its code-behind together. They are a single UI unit, not duplicate files.
- Keep large cohesive source files intact when splitting them would add indirection without improving ownership or maintainability.
- Do not rename or merge applied SQL migrations. Migration identity and checksums are part of the database upgrade contract.
- User-facing text belongs in localization resources; source code and comments use English.

## Canonical folder names

| Path token | Meaning | Ownership |
|---|---|---|
| `app/` | Desktop application presentation | WPF windows, view models, UI services, navigation, reports, localization and themes |
| `src/Dom/` | Domain | Entities, value types, enums, contracts and business rules |
| `src/App/` | Application | Use cases and orchestration |
| `src/Infra/` | Infrastructure | Persistence, repositories and external adapters |
| `src/Inv/` | Inventory core | Inventory-specific library |
| `dba/` | Database assets | Authoritative DDL, seeds and provider migrations |
| `svc/` | Background service | Independent Windows worker |
| `tools/` | Developer tools | Migration CLI and focused verification harnesses |
| `tests/` | Automated tests | Regression and integration test projects |
| `sec/` | Security | Native C++ security library and tests |
| `cfg/` | Configuration | Non-secret application defaults |
| `docs/` | Documentation | Architecture, engineering, product and user guides |
| `archive/` | Archive | Historical and non-deployable reference material |

The desktop startup is at the repository root: `ShouTech.ERP.csproj`, `App.xaml` and `App.xaml.cs`. There is no second WPF application definition under `app/`.

## Common file-name tokens

| Token | Meaning | Usage |
|---|---|---|
| `Acc` | Accounting / account | Accounting windows and services |
| `App` | Application | Application-level composition and configuration |
| `Bkp` | Backup | Backup operations and UI |
| `Cat` | Category | Category maintenance |
| `Com` | Company or commercial, depending on context | Use the containing namespace and type to disambiguate |
| `Cfg` | Configuration | Configuration types |
| `Coa` | Chart of accounts | Accounting structure |
| `Dom` | Domain | Domain project |
| `Inv` | Inventory or invoice, depending on context | Use the containing module and type to disambiguate |
| `Infra` | Infrastructure | Persistence and external adapters |
| `Jrn` | Journal | Journal entry and journal UI |
| `Loc` | Localization | Language and localized resources |
| `Mat` | Material | Inventory master data |
| `Mod` | Module | Module navigation and workspace |
| `Nav` | Navigation | Navigation services and models |
| `Pos` | Point of sale | POS operations |
| `Prm` | Permission | Access control |
| `Pur` | Purchase | Purchasing workflows |
| `Rep` / `Rpt` | Report / reporting | Reporting features |
| `Sal` | Sales | Sales workflows |
| `Svc` / `Serv` | Service | Service types |
| `Sup` | Supplier | Supplier workflows |
| `Tax` | Tax | Tax setup, calculation and reporting |
| `VM` | ViewModel | Presentation state and commands |
| `Win` | Window | WPF window types |
| `Wrt` | Writer | File or document writer |
| `Xpt` | Export | Export pipeline |

Abbreviations such as `Inv` and `Com` are context-sensitive. Do not infer a type's business meaning from a short token alone.

## Current entry-point and project map

| Path | Purpose |
|---|---|
| `ShouTech.ERP.csproj` | Sole WPF desktop executable project |
| `App.xaml`, `App.xaml.cs` | WPF application definition and lifecycle |
| `ShouTech.sln` | Product and engineering solution |
| `src/Dom/Dom.csproj` | Domain library; preserves the `Domain` assembly identity |
| `src/App/App.csproj` | Application use-case library |
| `src/Infra/Infra.csproj` | Infrastructure library |
| `src/Inv/Inv.csproj` | Inventory core library |
| `docs/FILE_INDEX.md` | Generated complete path inventory |
| `tools/gen_index.py` | File and folder index generator |

## UI file groups

A WPF view and its code-behind are intentionally paired. Examples from the current tree:

```text
app/Views/MWin.xaml       + app/Views/MWin.xaml.cs
app/Views/LogWin.xaml     + app/Views/LogWin.xaml.cs
app/Views/ComWin.xaml     + app/Views/ComWin.xaml.cs
app/Views/BinWin.xaml     + app/Views/BinWin.xaml.cs
app/Views/Modules/ModuleWorkspaceView.xaml
                          + app/Views/Modules/ModuleWorkspaceView.xaml.cs
app/Views/Inventory/MatCard.xaml
                          + app/Views/Inventory/MatCard.xaml.cs
```

## Maintenance

After adding, moving or removing files, regenerate the index from the repository root:

```powershell
python tools/gen_index.py
```

Update this reference only for conventions and abbreviations that are actually used. Do not maintain speculative “original long name” mappings or list files that no longer exist.
