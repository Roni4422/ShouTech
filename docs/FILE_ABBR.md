# ShouTech ERP — File Abbreviation Map

A reference that maps the current **abbreviated file names** in the project to their **original long names** (before abbreviation).

> **Naming rule:** roughly 6–9 characters, with a semantic suffix when names are similar:
> `VM` = ViewModel · `Vw` = View · `Svc`/`Serv` = Service · `Cv` = Converter · `Sv` = Service/helper

## Canonical folder abbreviations

| Folder | Full meaning | Responsibility |
|---|---|---|
| `app/` | Application presentation | WPF desktop shell, windows, view models, UI services and resources |
| `src/Dom/` | Domain | Business entities, value objects, enums and contracts |
| `src/App/` | Application | Use cases and application orchestration |
| `src/Infra/` | Infrastructure | Database repositories, persistence and external adapters |
| `src/Inv/` | Inventory core | Inventory-specific core library |
| `dba/` | Database administration | Authoritative database assets |
| `dba/mig/` | Migrations | Ordered provider-specific schema changes |
| `dba/sys/` | Database system assets | System-level database scripts and references |
| `svc/` | Service | Independent Windows background worker |
| `sec/` | Security | Native security library and supporting tests |
| `cfg/` | Configuration | Non-secret deployment defaults |
| `tools/` | Tools | Migration CLI and developer verification scripts |
| `tests/` | Tests | Automated regression and integration tests |
| `docs/` | Documentation | Architecture, engineering, user and research material |
| `archive/` | Archive | Historical and non-deployable reference material |
| `Views/` | Views | WPF windows and view components |
| `ViewModels/` | View models | Presentation state and commands |
| `Services/` | Services | UI-facing application service adapters |
| `Resources/` | Resources | Localization, icons, templates and error catalogs |
| `Themes/` | Themes | Shared WPF visual dictionaries |
| `Reporting/` | Reporting | Report definitions, rendering and invoice templates |

The canonical architecture uses the full layer names in documentation and the compact folder tokens in paths. Do not invent new abbreviations when an existing folder already owns the responsibility.

---

## WPF Application — `app`

### Entry point

| Abbreviated file | Long / original name | Class / Namespace |
|-----------|-----------------|-------------------|
| `App.xaml` | Application entry | `ShouTech.App.App` |
| `App.xaml.cs` | Application code-behind | — |

### ViewModels — `ViewModels/`

| Abbreviated file | Original long name | Class |
|-----------|-----------------|-------|
| `MainShVM.cs` | `MainShellViewModel.cs` | `MainShVM` |
| `MainWnVM.cs` | `MainWindowViewModel.cs` (legacy) | `MainWnVM` |
| `ModWsVM.cs` | `ModuleWorkspaceViewModel.cs` | `ModWsVM` |
| `LogWinVM.cs` | `LogWinViewModel.cs` | `LogWinVM` |
| `BaseVM.cs` | `BaseViewModel.cs` | `BaseVM` |

### Views — `Views/`

| Abbreviated file | Original long name | Class |
|-----------|-----------------|-------|
| `MWin.xaml` (+ `.cs`) | `MainWindow.xaml` | `MWin` |
| `LogWin.xaml` (+ `.cs`) | Login window (abbreviated from the start) | `LogWin` |
| `ComWin.xaml` (+ `.cs`) | Company selection window | `ComWin` |
| `SplWin.xaml` (+ `.cs`) | `SplashWindow.xaml` | `SplWin` |
| `StpWiz.xaml` (+ `.cs`) | `SetupWizardWindow.xaml` | `StpWiz` |
| `Lang.xaml` (+ `.cs`) | Language picker window | `Lang` |
| `LangVM.cs` | Language window ViewModel | `LangVM` |
| `AppState.cs` | Application session state | `AppState` |

### Module Views — `Views/Modules/`

| Abbreviated file | Original long name | Class |
|-----------|-----------------|-------|
| `ModWsVw.xaml` (+ `.cs`) | `ModuleWorkspaceView.xaml` | `ModWsVw` |
| `DashVw.xaml` (+ `.cs`) | `DashboardView.xaml` | `DashVw` |

### Services — `Services/`

| Abbreviated file | Original long name | Class / Interface |
|-----------|-----------------|-------------------|
| `AppHost.cs` | `AppServiceHost.cs` | `AppHost` |
| `AuthServ.cs` | `AuthService.cs` | `AuthServ` |
| `IAuthSvc.cs` | `IAuthService.cs` | `IAuthSvc` |
| `NavServ.cs` | `NavigationService.cs` | `NavServ` |
| `LogServ.cs` | `LoggingService.cs` | `LogServ` |
| `PermServ.cs` | `PermissionService.cs` | `PermServ` |
| `ThmServ.cs` | `ThemeService.cs` | `ThmServ` |
| `BkpServ.cs` | `BackupService.cs` | `BkpServ` |
| `IBkpSvc.cs` | `IBackupService.cs` | `IBkpSvc` |
| `DataServ.cs` | `DataService.cs` | `DataServ` |
| `CoRegSvc.cs` | `CompanyRegistryService.cs` | `CoRegSvc` (implements `ICoRegSvc`) |
| `LangDirSv.cs` | `LanguageDirectionService.cs` | `LangDirSv` |
| `ShlChrHlpr.cs` | `ShellChromeHelper.cs` | `ShlChrHlpr` |
| `UsrSession.cs` | `UserSession.cs` | `UsrSession` |
| `UsrAccLvl.cs` | `UserAccessLevel.cs` | enum `UsrAccLvl` |
| `UsrPrefSvc.cs` | User preferences service | `UsrPrefSvc` |
| `ILocSvc.cs` | `ILocalizationService.cs` + `LocalizationService` | `ILocSvc` / `LocSvc` |
| `IServices.cs` | Core service interfaces | `IDataServ`, `INavSvc`, `IPermSvc`, … |

### Online Services — `Services/Online/`

| Abbreviated file | Original long name | Class / Interface |
|-----------|-----------------|-------------------|
| `OnlCoord.cs` | `OnlineServicesCoordinator.cs` | `OnlCoord` |
| `IOnlCoord.cs` | `IOnlineServicesCoordinator.cs` | `IOnlCoord` |
| `ConnServ.cs` | `ConnectivityService.cs` | `ConnServ` |
| `IConnSvc.cs` | `IConnectivityService.cs` | `IConnSvc` |
| `SyncServ.cs` | `SyncService.cs` | `SyncServ` |
| `ISyncSvc.cs` | `ISyncService.cs` | `ISyncSvc` |
| `LicenSvc.cs` | `LicenseService.cs` | `LicenSvc` |
| `ILicenSvc.cs` | `ILicenseService.cs` | `ILicenSvc` |
| `DevInfoSv.cs` | `DeviceInfoService.cs` | `DevInfoSv` |
| `IDevInfoSv.cs` | `IDeviceInfoService.cs` | `IDevInfoSv` |

### Configuration — `Configuration/`

| Abbreviated file | Original long name | Class |
|-----------|-----------------|-------|
| `AppCfgSvc.cs` | `AppConfigService.cs` | `AppCfgSvc` |
| `AppSetng.cs` | `AppSettings.cs` | `AppSetng` |
| `AppPrefs.cs` | `AppPreferences.cs` + `AppPreferencesService` | `AppPrefs` / `AppPrefsSvc` |

### Common — `Common/`

| Abbreviated file | Original long name | Class |
|-----------|-----------------|-------|
| `AppFormat.cs` | `AppFormats.cs` | `AppFormat` |
| `JsonDefs.cs` | `JsonDefaults.cs` | `JsonDefs` |
| `StatBarEn.cs` | Status bar English texts (`StatBar`) | `StatBarEn` |

### Localization — `Localization/`

| Abbreviated file | Original long name | Class |
|-----------|-----------------|-------|
| `Loc.cs` | Localization helper | `Loc` |
| `LocProps.cs` | `LocalizationProperties.cs` | `LocProps` |
| `UiLoczr.cs` | `UiLocalizer.cs` | `UiLoczr` |

### Navigation — `Navigation/`

| Abbreviated file | Original long name | Class |
|-----------|-----------------|-------|
| `RibModMap.cs` | `RibbonModuleMapper.cs` | `RibModMap` |

### Converters — `Converters/`

| Abbreviated file | Class inside the file | Original long name |
|-----------|------------------|-----------------|
| `BoolVis.cs` | `BoolVis` | `BoolToVisibilityConverter` |
| `BoolVis.cs` | `InvBVis` | `InverseBoolToVisibilityConverter` |
| `BoolVis.cs` | `InvBool` | `InverseBooleanConverter` |
| `BoolVis.cs` | `NullVis` | `NullToVisibilityConverter` |
| `LangFlwCv.cs` | `LangFlwCv` | `LanguageToFlowDirectionConverter` |

### Themes — `Themes/`

| Abbreviated file | Original long name | Type |
|-----------|-----------------|-----|
| `DkTheme.xaml` | `DarkTheme.xaml` | ResourceDictionary |
| `LtTheme.xaml` | `LightTheme.xaml` | ResourceDictionary |
| `ShMenu.xaml` | `ShellMenuStyles.xaml` | ResourceDictionary |
| `ShRibn.xaml` | `ShellRibbonStyles.xaml` | ResourceDictionary |

---

## Core / Inventory — `src/Inv/`

| Abbreviated file | Long name / meaning | Class |
|-----------|-------------------|-------|
| `navrep.cs` | Navigation repository | `NavigationRepository` |
| `invrep.cs` | Inventory repository | — |
| `invsvc.cs` | Inventory service | — |
| `invuow.cs` | Inventory unit of work | — |
| `invmod.cs` | Inventory models | — |
| `Models.cs` | Shared models | — |
| `Security.cs` | Security helpers | — |

---

## Configuration outside the project — `cfg/`

| Abbreviated file | Original long name | Class |
|-----------|-----------------|-------|
| `UsrPref.cs` | User preferences | `UsrPref` |
| `appset.json` | Application settings (deploy) | — |

---

## Domain — `src/Dom/`

| File / path | Note |
|------------|--------|
| `Pref.cs` | User preference model (encrypted local storage) |
| `Entities/Navigation/NavigationModels.cs` | Navigation DTOs (not yet abbreviated) |
| `Interfaces/INavigationRepository.cs` | Navigation repo contract |

---

## Maintenance tools — `tools/`

| Script | Purpose |
|-------|--------|
| `rename_short.ps1` | The initial abbreviation of the long names |
| `rename_distinct.ps1` | Disambiguating similar names (+1–2 characters) |
| `generate_nav_seed.ps1` | Generating `dba/sds/sds_nav.sql` from XAML + Lang JSON |

---

## Related file groups (XAML + Code-behind + VM)

```
LogWin.xaml  ←→  LogWin.xaml.cs  ←→  LogWinVM.cs
MWin.xaml    ←→  MWin.xaml.cs    ←→  MainShVM.cs
ModWsVw.xaml ←→  ModWsVw.xaml.cs ←→  ModWsVM.cs
SplWin.xaml  ←→  SplWin.xaml.cs
ComWin.xaml  ←→  ComWin.xaml.cs
```

---

## Notes for developers

1. **Do not change `Application.MainWindow`** — a WPF property; the abbreviated window name is only the `MWin` class.
2. **The status bar** uses `StatBarEn` (fixed English) — it does not go through `LocSvc`.
3. **Online services** (sync, licensing, connectivity) run in the background and their details are not shown in the UI.
4. When adding a new file: follow the same pattern (`LogWin`, `NavServ`, `ModWsVM`) and update this file.

---


---

## Batch 1 Foundation — Reports / Windows / Export (2026-10-05)

### Views — `app/Views/`

| Abbreviated file | Long name / meaning | Class |
|-----------|-------------------|-------|
| `BaseWin.cs` | Base window (mandatory for secondary windows) | `BaseWin` |
| `RptBase.cs` | Report base window | `RptBase` |
| `ColChsWin.xaml` (+ `.cs`) | Column chooser window | `ColChsWin` |

### Reporting — `app/Reporting/`

| Abbreviated file | Long name / meaning | Class |
|-----------|-------------------|-------|
| `RptDef.cs` | Report definitions (columns, filters, data models) | `RptDef`, `RptCol`, `RptFlt`, `RptData` |
| `RptRend.cs` | Report renderer (print + PDF page composition) | `RptRend`, `RptPaginator` |
| `InvTpl.cs` | Invoice design templates (encrypted) | `InvTpl` |

### Services — `app/Services/` (merged into existing folder)

| Abbreviated file | Long name / meaning | Class |
|-----------|-------------------|-------|
| `WinPref.cs` | Window geometry + report column preferences (per user/company) | `WinPref` |
| `XptCry.cs` | Export crypto container AES-256-GCM (.shx) | `XptCry` |
| `XptServ.cs` | Unified export service (Excel / PDF / encrypted / print) | `XptServ` |
| `XlsxWrt.cs` | Professional XLSX writer (no external deps) | `XlsxWrt` |
| `PdfWrt.cs` | Embedded PDF writer (JPEG pages) | `PdfWrt` |

### Localization keys added

- Sections: `Report.*`, `Export.*`
- Common: `Columns`, `Refresh`, `Print`, `Export`, `OK`, `Cancel`, `MoveUp`, `MoveDown`, `SelectAll`, `ClearAll`, `Info`, `Warning`, `Error`, `Confirm`

*Last updated: 2026-10-05 · ShouTech ERP v0.4 — Batch 1 Foundation merged*


---

## Batch 2 — Reports & Invoices (2026-10-05)

| File | Meaning |
|------|---------|
| `RptQuery.cs` | Shared report queries |
| `DocSchema.cs` | Commercial document DDL bootstrap |
| `InvRepo.cs` | Invoice persistence + post |
| `InvPrn.cs` | Invoice print/PDF |
| `RptNav.cs` | Report/document launcher |
| `InvDoc.cs` | Invoice domain model |
| `TrialBalanceWin.cs` | Trial balance |
| `AccStmtWin.cs` | Account statement |
| `JrnListWin.cs` | Journal list |
| `StkBalWin.cs` | Stock balances |
| `MatMoveWin.cs` | Material moves |
| `CustBalWin.cs` / `SupBalWin.cs` | Party balances |
| `SalListWin.cs` / `PurListWin.cs` | Document registers |
| `SalInvWin.cs` / `PurInvWin.cs` | Invoice entry |

