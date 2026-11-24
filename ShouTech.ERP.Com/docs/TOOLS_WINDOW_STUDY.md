# Unified Tools Window — Study (feat/tools-window)

Status: STUDY — written before any build code, per the workstream brief.
Owner directives behind this work: one unified Tools window as the home of
the program's tools (2026-11-24 20:02); the window is dense with checkboxes
by nature ("والتشيك بوكس فيها متعددة جدا"); form is ShouTech's own (IP rule —
functionality modelled on the research shortlist, never a copied layout);
every checkbox binds to a real stored setting AND names its real consumer
(control rule — a toggle without a consumer is a forbidden fake field and
goes to the gap list in §6 instead).

Research basis: `research_notes/phenix-tools-tab-inventory-20261124-1335/`
(screenshot transcriptions of the Phenix أدوات / أدوات أخرى menus,
`tools-window-web-research-20261124.md`,
`other-programs-windows-research-20261124.md`). Shortlist adopted: SAP
Business One's single Administration home + Dynamics GP's Tools content
model for the hub; Frappe's jobs/logs-as-visible-data for status content;
QuickBooks' audit-as-report is noted as the document-audit model (gap, §6).

---

## 1. Inventory — where every tool lives today

The tools exist and work; they are scattered across two ribbon tabs, three
other tabs, shell buttons and one dashboard. Nothing below is rebuilt — the
hub (§2) gives them one home.

| Tool surface | Lives today | Entry point(s) today |
|---|---|---|
| Options (OptWin) | own window, control center | ribbon Tools.Options (special-cased in MWin.xaml.cs), OptWin is a sibling center — the hub links to it, never absorbs it |
| Maintenance wizard (MaintenanceWin) | own window: DB health check, integrity summary, Backup Now, SQLite VACUUM/ANALYZE, Recalculate Costs, TB integrity audit; gated SYS.MAINTENANCE | ribbon Tools.MaintenanceWizard |
| Backup (BkpWin) | own window: create / restore / validate / delete *.stb; freezes shell, closes secondary windows on restore | ribbon Tools.Backup; OptWin Backup section (last-backup readout + stale > 7 days warning + open button) |
| Backup automation engine | `OnlineCoordinator` background cycle + `BkpServ.IsBackupDue()` + `PruneOldBackups()` rotation — **settings exist only as hand-edited `cfg/appset.json`; no UI anywhere** | none — the gap this workstream fills (§3) |
| Recycle bin (BinWin) | own window over RecycleBinSvc + DocBinSvc (documents, journals, cheques, master data); purge gated SYS.ADMIN | MWin shell button |
| Data exchange (DatWin) | own window, export/import modes; import gated DATA.IMPORT in the service layer | ribbon View.ExportData; MWin mode-aware activation |
| Restore deleted company (RestoreCoWin) | own window | ribbon OtherTools.RestoreCompany.Restore |
| Fiscal-year rollover (RolloverWin) | own window | ribbon OtherTools.Rotation |
| Compress invoices (CompressInvoicesWin) | own window; hard-deletes soft-deleted DRAFT documents in one transaction + audit | ribbon Invoices.CompressInvoices |
| Repost documents (RepostWin) | own window; recomputes totals from lines, writes back + audit | ribbon Invoices.RepostInvoices / Manufacturing.RepostManufacturing |
| POS outbox / sync viewer (PosSyncWin) | own window over `outbound_messages`; Retry requeues FAILED rows | ribbon POS.SyncPOS |
| Activity log (ActivityLogWin) | own window over security_audit_log | ribbon Tools.ActivityLog |
| Permissions (PrmWin) | own window; templates building on feat/permissions-templates — hub links, never duplicates | ribbon OtherTools.Permissions / PermissionTemplates (both → PrmWin today) |
| Report designer (CustomReportsWin) | own window over ReportRepo / ReportDesignerVM; .strd design files | ribbon Reports.CustomizeReports |
| Invoice designer (InvDesWin) | own window; .stid design files | ribbon Invoices.InvoiceDesigner |
| Barcode designer (BcdDesWin) / generator (BcdGenWin) | own windows; .stld label designs | RptNav.OpenBarcodeDesigner / OpenBarcodeGenerator |
| Definitions (DefManagerWin sets) | units, payment methods, custom fields, material specs, drivers, delivery prices (+ DeliveryDeskWin flows) | ribbon OtherTools.* keys |
| Calculator (CalculatorWin), on-screen keyboard | own window / OS launch | ribbon Tools.Calculator / Tools.Keyboard |
| Change password (ChangePasswordWin) | own window | ribbon Tools.ChangePassword |
| Add-ons (AddonsWin) | own window, read-only by design — no runtime module toggle API exists and the window says so | ribbon Tools.Addons |
| Updates (UpdatesWin) | own window; honest "not configured" state when no update source is set | ribbon Help.CheckUpdates / Help.UpdateFromFile |
| Licence (LicenseWin) | own window | ribbon Help.RenewLicense |
| Help (HelpWin, F1), Support/Suggestions (SupportWin) | own windows | F1 / ribbon Help.* |
| Language switch ×4 | App.SetApplicationLanguage | ribbon Tools.{Arabic,English,French,Turkish} |

Settings stores that already exist (the hub writes only to these):
- **AppConfig** (`cfg/appset.json`, `AppCfgSvc.Load/Save`) — machine-level
  application settings. Backup + online-services sections live here.
- **UsrPref** — per-user JSON preferences (Options window's store).
- **WinPref** — per company+user window/layout preferences (geometry,
  named values; the hub's own per-user prefs live here).
- Company DB tables for company-wide behaviour (precedents: pos_settings,
  company_tax_settings, document_number_sequences) — not needed here.

## 2. Hub design — ToolsWin

One window, our idiom (the OptWin pattern the program already has):
category rail at the start side + one section body per category + bottom
action bar + slim status bar, built on BaseWin (live shell context,
whole-window RTL mirroring, ≤ 50 % first-open, window-state persistence,
F1 topic = `ToolsWin`). Schedule tint: **Hickory** — the one palette tint
not claimed by any of the 97 censused windows (census becomes 98).

Ribbon entry: `Tools.Home`, first button of the Tools tab's Tools group;
routed by RibbonNav like every other Tools key.

### 2.1 In-window vs launched — the ruling

A tool becomes an in-window SECTION only when the hub adds something the
tool's own window does not have: (a) settings that today have no surface at
all, or (b) a derived status the user otherwise hunts across five windows.
Everything else stays a window LAUNCHED from the hub — same window, same
services, zero duplicated logic (complete-pillars rule; a re-implemented
copy would drift). This is the SAP B1 lesson (one Administration home)
applied without the monolith cost.

| Section | Kind | Content |
|---|---|---|
| Overview | in-window | Health & status (§4), tool search, quick-launch cards |
| Backup & automation | in-window | The AppConfig backup/cycle settings (§3) + backup actions + last-backup readout |
| Maintenance | launch | MaintenanceWin, CompressInvoicesWin, RepostWin, RolloverWin |
| Data | launch | DatWin (export / import), BinWin, RestoreCoWin, PosSyncWin |
| Design & printing | launch | CustomReportsWin, InvDesWin, BcdDesWin, BcdGenWin |
| Security & activity | launch | PrmWin, ActivityLogWin, ChangePasswordWin, LicenseWin, AddonsWin, UpdatesWin |
| Definitions | launch | DefManagerWin sets + delivery definitions via their existing ribbon keys |
| Utilities | launch + actions | CalculatorWin, on-screen keyboard, language switch (App.SetApplicationLanguage), OptWin, HelpWin, SupportWin |

Launch mechanism: `RibbonNav.TryHandle(existingKey, this)` wherever a
ribbon key exists — byte-identical behaviour to clicking the ribbon —
and direct construction with `Owner = this` exactly where the shell itself
does that today (BinWin, BkpWin, OptWin, DatWin modes). Permission gates
stay where they already live (target window / service layer); the hub
adds one gate of its own: the Backup & automation section is editable
only with SYS.ADMIN (it rewrites machine-level configuration), read-only
with an explanatory note otherwise — the SYS.ADMIN precedent is BinWin's
purge gate.

Search: one filter box on Overview filters a single tool catalog (name +
section + keywords, localized names) that also drives the launch cards —
type, click, the tool opens. The catalog is data in the window, defined
once; sections and search read the same list, so they cannot disagree.

## 3. The checkbox inventory — every toggle, its store, its consumer

Store A = AppConfig (`AppCfgSvc.Current`, saved by `AppCfgSvc.Save()`).
All six Store-A settings already have live consumers in the backup engine;
they simply had no UI. The hub is their first surface.

| # | Control | Stored setting | Consumer (named) | Wiring point |
|---|---|---|---|---|
| 1 | ☑ Enable scheduled backups | `Database.Backup.Enabled` | `BkpServ.IsBackupDue()` returns false when disabled → `OnlineCoordinator` background cycle performs no backup | engine reads AppCfgSvc live; hub writes + saves |
| 2 | ☑ Back up automatically when due | `Database.Backup.AutoBackup` | same `IsBackupDue()` gate | same |
| 3 | ☑ Back up on startup | `OnlineServices.BackupOnStartup` | `OnlineCoordinator` startup cycle: forces a backup when a startup is pending and this is on | same |
| 4 | Number: backup interval (hours) | `Database.Backup.BackupIntervalHours` | `IsBackupDue()` — due = now − newest *.stb in the company backup folder ≥ interval | same |
| 5 | Number: keep newest N backups | `Database.Backup.MaxBackups` (engine clamps 1..1000) | `BkpServ.PruneOldBackups()`, called at the end of every `CreateBackupAsync` | same |
| 6 | Number: background cycle interval (seconds) | `OnlineServices.BackgroundSyncIntervalSeconds` (engine clamps ≥ 15 s; sync sub-cycle ≥ 300 s) | `OnlineCoordinator` timers (connectivity check + background sync/backup cycle) | same |

Store B = WinPref (`ToolsWin` scope, per company+user) — the hub's own
per-user behaviour; the consumer is the hub itself, named as such.

| # | Control | Stored setting | Consumer (named) | Wiring point |
|---|---|---|---|---|
| 7 | ☑ Run health checks when this window opens | WinPref `AutoHealth` (default on) | ToolsWin load path — runs the §4 checks automatically vs on demand | ToolsWin |
| 8 | ☑ Verify each backup right after creating it (from this window) | WinPref `VerifyAfterBackup` (default on) | ToolsWin "Back up now" action → `AppHost.Backup.ValidateBackupAsync` on the created file, verdict shown in the status bar | ToolsWin |

Actions in the section (not settings): **Back up now** —
`AppHost.Backup.CreateBackupAsync(currentCompany)` (the real API
MaintenanceWin uses; the legacy `PerformRotatedBackup()` hardcodes company
"001" and is NOT used); **Open backup window** — BkpWin (restore/validate/
delete live there). Readouts: newest backup + age + stale warning (§4),
automatic-backup due state.

Due-state honesty: `BkpServ.IsBackupDue()` inspects the legacy company
"001" folder only. The hub computes the due state for the CURRENT company
from `ListBackups(company)` + settings 1/2/4 with the same rule, and the
study records the engine's "001" scope as a pre-existing limitation of the
engine, not something the hub's readout papers over.

Save semantics: `AppCfgSvc.Save()` writes only when a configuration file
was actually loaded (`LoadedPath`); on a defaults-only run the hub reports
TLS-8501 and leaves the in-memory values untouched-by-claim — it never
pretends a save happened.

## 4. Health & status content — derived from real stores only

Overview shows, each from its named source (no stored copies, no
estimates):
- Company code (AppState), provider (DatabaseRuntime), database file size
  for SQLite (the Data Source path of the live connection string;
  PostgreSQL shows the server form instead — no fake size).
- Schema: applied-migration count + highest applied id from
  `schema_migrations` (new read-only `OperationsRepository` query, both
  dialects — the table both migrators maintain).
- Connectivity: `AppHost.DatabaseHealth.CheckAsync()` verdict + latency.
- Integrity: `Operations.GetIntegritySummaryAsync()` (SQLite
  integrity_check / PostgreSQL reachability + table counts).
- Trial-balance integrity: `Operations.GetTrialBalanceIntegrityAsync
  (company)` verdict line (balanced periods; issue counts when not).
- Backups: `AppHost.Backup.ListBackups(company)` — count, newest file,
  its age, and the stale-backup warning when the newest is older than
  7 days (the same derivation OptWin's Backup section uses).
- Messages/outbox: `Operations.GetOutboundMessagesAsync()` counts by
  status (queued / failed / sent) — Frappe's jobs-as-data idea at the
  scale our substrate supports; the deep view is PosSyncWin (launch).
- Recycle bin: `RecycleBinSvc.CountAsync(company)` — the same source
  BinWin lists from.

Checks run on open when setting 7 is on, else from a "Run checks" button;
failures report through the error catalog (TLS-8502), never a silent
blank panel.

## 5. Errors & language

- New catalog family **TLS-85xx** (85xx verified free; INF ends at 8499):
  TLS-8501 settings could not be persisted (no configuration file
  loaded); TLS-8502 a health check failed; TLS-8503 a backup action
  failed. Constants in `src/Domain/ErrorCodes.cs` + entries ×4 in
  `Resources/Errors/{En,Ar,Fr,Tr}.json` (parity gate is exact-set).
- Lang: ribbon key `Tools.Home` ×4 + a `ToolsWin` section ×4 (section
  names, setting labels, health labels, card titles/descriptions, status
  lines). Key sets stay identical across the four Lang files.
- No migration: this branch adds no schema (AppConfig JSON + WinPref are
  the proper existing stores). Migration numbers on this branch therefore
  stay unused; §7 records the merge note.

## 6. Gap list — evidenced options NOT built, with reasons

| Item | Reason |
|---|---|
| `OnlineServices.Enabled` master switch | Stored in AppConfig but read by nothing — `OnlineCoordinator` never consults it. Surfacing it would be a fake toggle; wiring a new master gate into the coordinator is an engine change outside this window's scope. |
| `Backup.CompressBackup` / `EncryptBackup` toggles | No consumer exists; the STEI backup package is AES-256-GCM by format design, so an "encrypt" checkbox would misdescribe the format. |
| Migration settings (`MigrationSettings.Enabled/AutoMigrate/BackupBeforeMigration`) | No consumer anywhere in code; migrations run through SqlMigrator / the app bootstrapper, which do not read AppConfig. |
| Application toggles (EnableAutoUpdate, CheckFileIntegrity, EnableTelemetry, AllowMultipleInstances, AutoUpdateCheckIntervalHours) | No consumers found in code. UpdatesWin already states the honest manual model. |
| Security policy fields (session timeout, max login attempts, lockout, password policy numbers) | Consumers not verified in the login flow during this inventory; not surfaced unwired. Candidate for a dedicated security-settings pass that wires LogWin/AppHost.Auth first. |
| Company-profile AutoBackup flag (companies row, edited in ComWin/StpWiz) | A second, storage-only switch parallel to the engine's real AppConfig switch; the engine never reads it. Recorded as a pre-existing two-sources-of-truth wart — consolidating it is a company-store decision for the owner, not a hub feature. |
| Document audit (تدقيق المستندات) | No engine exists (REDUCE_PLAN commercial backlog). QuickBooks' audit-as-report + Tally's resurfacing rule are the recorded build models when it is built. |
| General task scheduling (جدولة المهام) | No general scheduler exists; the backup cycle in §3 is the one real scheduled job and its own knobs are what §3 surfaces. A Frappe-style job-type/job-log substrate is the OptWin study's G11 automation family — one scheduler, designed once, not a hub-local imitation. |
| Automated report options (خيارات التقارير الآلية) | Needs the email transport first (OptWin G2 — also blocked on the owner's open quotation-email decision). |
| Add-on runtime toggles | No runtime module enable/disable API exists; AddonsWin already says so honestly. |
| Phenix maintenance-wizard internals, external tools, helper-phrase list | Publicly undocumented (research files say so explicitly); internals are not invented here. Al-Ameen's explaining auto-fix wizard remains the maintenance model on record. |
| DatWin selective per-type export options | Belongs to the DatWin/OptWin export work (OptWin G10), not the hub. |

## 7. Build plan & gates

Landings (each: full gate suite green, then commit + push on
feat/tools-window; never leave the branch red):
1. This study.
2. ToolsWin + shell wiring (ribbon `Tools.Home`, RibbonNav case) +
   Lang ×4 + TLS errors ×4 + OperationsRepository schema-version query.
3. ToolsHarness (headless proof, §8) + gate suite + census/tint record.

Gates per landing: solution build 0 errors / 0 warnings
(`--no-incremental`), SqlMigrator fresh-apply + idempotent through 124
(this branch adds no migration), 4 audit scripts exit 0, errors parity
×4 exact-set, Lang parity ×4, harnesses P2p 39/39, W1 36/36, Bin 22/22,
Num 27/27, Info 35/35, ToolsHarness all-pass.

Merge note: no schema change ⇒ no migration renumber expected from this
branch. Sibling state at merge time: permissions branch owns a migration
125 on its branch; the reduce line's OptWin landings plan their own
125–130 — those renumber between themselves; ToolsWin touches
ErrorCodes.cs, the four Errors JSONs, the four Lang JSONs, MWin.xaml and
RibbonNav.cs, which are the files to watch for textual merge conflicts.

## 8. Headless proof plan (ToolsHarness)

Modelled on tools/InfoHarness (real migrated temp SQLite DB + real
services):
- Settings round-trip: `AppCfgSvc.Load(tempAppset)` → change the six
  Store-A values through the same facade the window uses → `Save()` →
  reload → values identical.
- Consumer proof (as run, 30 PASS / 0 FAIL): the due rule flips
  correctly across Enabled / AutoBackup / interval combinations against
  real *.stb files in the real company backup folder, through the real
  `BkpServ.IsBackupDue()`. The create / rotate / verify legs call the
  real `CreateBackupAsync` / `PruneOldBackups` / `ValidateBackupAsync`;
  on this Linux VM they are reported as a named SKIP — never a pass —
  because the STEI package password is protected with Windows DPAPI
  (`System.Security.Cryptography.ProtectedData` is a Windows-only
  assembly). They execute for real on Windows, where they belong to the
  Windows-owed list below.
- Health derivation: seeded temp DB → schema-version query returns the
  applied count/highest id; integrity summary non-empty; TB integrity
  verdict object returned.
- TLS-8501 mapping: the no-config-loaded save path resolves to the
  catalog entry in all four languages (catalog load, not string compare).

Windows-owed (cannot be proven on this Linux VM): WPF click-through of
ToolsWin (rail, sections, launches, RTL mirroring, ≤ 50 % first-open),
Kern.Tests execution, live PostgreSQL run of the schema-version query.
