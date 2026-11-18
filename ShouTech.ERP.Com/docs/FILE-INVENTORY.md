# Repository File Inventory and Missing-File Audit

**Repository:** `Roni4422/ShouTech`  
**Audited branch:** `work/full-app-stabilization-2026-11-18`  
**Audit commit:** `60df7663e42e88315f4c77b9df63bd6b287551df`  
**Compared with:** `main` at `31ebc1c936d8f9de94b0c91751859e2a9feb3458`  
**Audit date:** 2026-11-18

## 1. Tracked-file totals

| Measure | Count |
|---|---:|
| Tracked files on development branch | 751 |
| Tracked files on main | 749 |
| Net difference | +2 |
| Directories containing tracked files | 62 |
| C# source files (`.cs`) | 407 |
| SQL migration/schema files (`.sql`) | 159 |
| Markdown documents (`.md`) | 65 |
| XAML files in repository | 27 |
| WPF view XAML files under `cod/app/src/Views` | 23 |
| WPF view code-behind files (`.xaml.cs`) | 23 |
| Matched WPF XAML/code-behind pairs | 23 |
| WPF view XAML without same-basename code-behind | 0 |
| WPF view code-behind without same-basename XAML | 0 |
| C# project files (`.csproj`) | 8 |

Other tracked extensions: 20 JSON, 18 C++, 16 SHC, 6 H, 5 YML, 5 SYS, 4 PY, 3 PNG, and one each of GITIGNORE, PROPS, TXT (2), SLN, CONFIG, HYBRID and VCXPROJ.

## 2. Comparison with main: missing or renamed files

The path comparison accounts for all 127 paths that differ on main:

- Domain project folder renamed from `src/ShouTech.Domain` to `src/Domain`.
- Application project folder renamed from `src/ShouTech.Application` to `src/App`.
- Infrastructure project folder renamed from `src/ShouTech.Infrastructure` to `src/Infra`.
- The three project files were renamed consistently to `Domain.csproj`, `App.csproj` and `Infra.csproj`.
- `Services/RptQuery.cs` was renamed to `Services/Rpt.cs`.
- `Views/MatCard.xaml` and `Views/MatCard.xaml.cs` were moved together to `Views/Inventory/`.

**Comparison result:** 0 main-branch tracked files are unaccounted for after applying those explicit path mappings. The branch has two additional tracked files, both audit/refactoring documents: `docs/REFACTOR.md` and `docs/WINDOW-INVENTORY.md`. This is why the branch has 751 files versus 749 on main; it is not evidence of two missing source files.

## 3. Project and solution reference audit

- All 8 `.csproj` files were inspected.
- 13 explicit file/project references were resolved against the branch tree.
- Missing explicit references after cleanup: **0**.
- Solution project paths were checked against the repository tree.
- The WPF project uses recursive XAML and C# globs, so the new `Views/Inventory` folder is included by the existing project rules.

### Stale optional includes found and removed

`MainApp.csproj` contained two conditional source includes pointing to files that do not exist:

- `../../../cfg/AppSettings.cs`
- `../../../cfg/CompanyInfo.cs`

These were stale references, not missing implementation files: the settings model `AppSetng` and `CompanyInfo` type are already defined in `cod/app/src/Configuration/AppConfig.cs`, which is compiled by the project's recursive C# include. The only tracked file under `cfg` is `appset.json`.

The two obsolete conditional Compile entries were removed from `MainApp.csproj`. The remaining explicit project/file references all resolve.

## 4. WPF view-pair audit

Under `cod/app/src/Views`, all 23 XAML files have matching `.xaml.cs` code-behind files, and all 23 code-behind files have matching XAML files. No unmatched XAML/code-behind pair was found.

This check verifies file pairing only. It does not prove that every window is reachable from the Ribbon, has correct permissions, loads all resources, or works against the database. Those require route-level and runtime tests and remain part of the window migration gate.

## 5. Remaining verification

- [x] Compare complete tracked-file trees on the development branch and main.
- [x] Account for all renamed/moved paths.
- [x] Count files by extension and directory.
- [x] Check XAML/code-behind pairing.
- [x] Check explicit project/file references.
- [x] Remove the two stale optional source includes.
- [ ] Run Windows Debug and Release build after this audit commit.
- [ ] Complete runtime window navigation and data-persistence tests.
- [ ] Complete SQLite/PostgreSQL integration regression.

**Conclusion:** No tracked source file from `main` is currently unaccounted for after the documented renames and moves. The static project-reference audit now reports zero missing explicit references. Runtime completeness is not yet established; the outstanding window and database tests must still pass.
