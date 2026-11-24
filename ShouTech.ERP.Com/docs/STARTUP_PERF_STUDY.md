# Startup Performance Study — feat/startup-perf

Owner directive (2026-11-24 20:23): «البرنامج بطيئ في الاقلاع هل يمكن
تسريعه» — the program is slow to launch; make cold startup to an
interactive main window measurably faster, **without changing any
behavior**.

Base: `origin/reduce` @ `07a1e5bcb0d6e13b01c53bc2f9cbbb71ff3a7d18`
("feat(optwin): Landing 1 — Font + Numbers/date/time sections govern
the program"). Worktree `~/workspace/projects/ShouTech-perf`, branch
`feat/startup-perf`. No migrations are added by this workstream.

Method: full code trace of the startup chain in execution order
(App.xaml.cs → SplWin → ComWin → LogWin → MWin), plus segment timings
from `tools/PerfHarness` (headless, on this Linux VM — segment timings
only, **not** app-startup times; the real before/after is measured on
the owner's Windows machine from the log lines §4 defines).

---

## 1. The critical path, in code order

### Phase 0 — Runtime and framework (before any ShouTech code)
Process start, .NET 8 runtime + WPF framework load, JIT of every
method on the chain the first time it runs. `MainApp.csproj` sets
`PublishReadyToRun=false`. Cost class: **high on cold start**,
inherent to a framework-dependent WPF app.
**Ruling: keep** (code scope). ReadyToRun is a *packaging* decision —
it changes published artifacts, cannot be verified from this VM, and
is recorded in §6 as an owner decision, not silently flipped.

### Phase 1 — App construction (before OnStartup)
1. `App.xaml` resources: merged dictionaries `Themes/VisuId.xaml`
   (**220 KB**) + `Themes/ShRibn.xaml` are parsed and ~30 application
   styles are built. Cost: medium-high, one-time, must exist before
   any window. **Ruling: keep.**
2. `App.Localization` property initializer constructs **LocSvc #1**.
   Its constructor (a) `BuildLabelIndex()` — enumerates and fully
   parses **all four** Lang catalogs (Ar/En/Fr/Tr ≈ 838 KB of JSON)
   with a recursive flatten that re-serializes and re-parses every
   nested object, and (b) `LoadResources()` — parses En.json **again**.
   Cost: **high** (≈5 full catalog parses before OnStartup begins).
   **Ruling: lazy** — §3/L3.
3. The first touch of any `AppHost` static runs its static
   initializers, including `_fallbackLocalization = new LocSvc()` —
   **LocSvc #2**, a second full repetition of the same ≈5 parses.
   Its catalogs are only ever used when `Application.Current` is not
   an `App` (tools/harnesses); in the real app every call returns
   `App.Localization` instead. Cost: **high, wasted** in the app.
   **Ruling: lazy** (`Lazy<LocSvc>`) — §3/L3.

### Phase 2 — `App.OnStartup`, synchronous, splash not yet shown
Everything here delays the *first visible pixel*.

| # | Operation | Cost | Ruling |
|---|-----------|------|--------|
| 2.1 | Class-handler registrations (digits, window logging, F1) | negligible | keep |
| 2.2 | `UsrPref.Load()` — small JSON read+parse (also re-read at SplWin step 4 and by WinPref) | low | keep |
| 2.3 | `SetApplicationLanguage(pref.Lang)` → `LocSvc.SetLanguage` → for a non-English language, `LoadResources()` parses En base **+** active overlay (2 more parses); `ApplyToAllWindows` is a no-op (no windows yet) | medium | cheapen via L3 (this becomes the single load point) |
| 2.4 | `NumFmt.Current = pref.ToNumFmtSettings()` | negligible | keep |
| 2.5 | DI container build (6 registrations) | low | keep |
| 2.6 | `SchemaBootstrapper.EnsureSchema()` — the embedded resources `ShouTech.App.dba.lite.sql` / `dba.pg.sql` **do not exist** in the build (`EnableDefaultEmbeddedResourceItems=false`, no `EmbeddedResource` item anywhere in the solution — verified by csproj search and by PerfHarness listing the built assembly's manifest resources). With the shipped `cfg/appset.json` the Default connection string is also empty, so this returns at the first check. Either way it performs **no schema work at startup today**; real provisioning is `SchemaMigrator` (company creation) + `SqliteSchemaBootstrapper.EnsureDatabase` (validation/seed). | low (dead path) | keep — do **not** "fix" by embedding the scripts; that would change behavior and is out of scope. Noted so nobody mistakes it for the migration path. |
| 2.7 | `RecycleBinSvc.InitializeAuditInfrastructureAsync().GetAwaiter().GetResult()` — sync-over-async; with no Default connection configured it throws, is caught, and logs the "deferred" warning. With one configured it opens that DB and ensures the deletion-audit table (idempotent DDL). | low–medium | keep on the path: the code's stated invariant is "audit infrastructure before any business screen can mutate data", and the measured segment (§5) does not justify weakening it. |
| 2.8 | `AppHost.Initialize()` — `LogServ` ctor (creates log dir, headers; cheap), `AppCfgSvc.Load()` (appset.json parse), then construction of ~30 module services. Each `CreatePhase3Service` only resolves config strings and constructs a repository object — **no DB I/O in any constructor on this path** (verified by reading the factories). Mostly assembly load + JIT surface. | low–medium | keep |
| 2.9 | `Audit.LogSystemStartAsync()` — fire-and-forget (`_ =`), off the path | — | keep |

### Phase 3 — Splash (`SplWin`), shown only after all of Phase 2
`SplWin.OnLoaded` runs an 8-step pipeline, then the company check,
then hands off to ComWin.

| # | Operation | Cost | Ruling |
|---|-----------|------|--------|
| 3.1 | **Artificial floor: `MaxSplashSeconds = 4.0`.** After all real work, `OnLoaded` awaits `4.0s − elapsed` before proceeding. **Every launch waits at least ≈4.0 s at the splash, on any machine, however fast.** | **HIGH — dominant, artificial** | **remove** — §3/L2 |
| 3.2 | **Budget skip:** `RunStepAsync` silently *skips a step's action entirely* once elapsed ≥ 3.85 s (it only sets the progress number). On a slow machine the license check / background-services start can be skipped *because the machine is slow*. | correctness quirk | **remove with the floor** — every step always runs; splash duration becomes the real work time |
| 3.3 | **Pacing delays:** every `RunStepAsync` ends with `SafeDelay(40)` — 8 steps ≈ **320 ms** of pure delay on the happy path (plus 80 ms per warning step) | medium, artificial | **remove** — §3/L2 |
| 3.4 | `VerifyIntegrityAsync` — `File.ReadAllBytes(entryAssembly)` + SHA-256 of the whole entry assembly, **whose result is discarded** (`_ =`); the only behavioral effect is throwing when the file is < 1024 bytes | low–medium (whole-file read+hash) | **cheapen**: `FileInfo.Length` gives the identical pass/fail — §3/L2 |
| 3.5 | `VerifyApplicationBinariesAsync` (4 × FileInfo), `ScanUnauthorizedModificationsAsync` (6 directory pattern scans, top-level only) | low | keep |
| 3.6 | `ValidateConfigAndUserFilesAsync` — re-loads UsrPref (2nd read) | negligible | keep |
| 3.7 | `CheckDeviceAndDongleAsync` → `DevInfoSv.Collect()` — env values + HWID hash + one small JSON cache write; **no WMI** | low | keep |
| 3.8 | `CheckLicenseAsync` → `LicenSvc.InitializeAsync()` — local license file load + offline validation + save | low | keep |
| 3.9 | `PrepareUserDataAsync` → `SyncServ.SnapshotLocalUserDataAsync()` — reads 3 named user files, writes one snapshot JSON, saves the sync queue | low | keep |
| 3.10 | `StartBackgroundServicesAsync` → `OnlineCoordinator.Start()` — starts background loops, returns immediately | low | keep |
| 3.11 | `CoRegSvc.HasCompaniesAsync()` → `GetCompaniesAsync()` → local store JSON + (only when a Default connection is configured) `LoadFromDatabaseAsync`. The PostgreSQL variant calls `SqliteSchemaBootstrapper.EnsureDatabase` only in its SQLite sibling `LoadFromSqliteAsync` — see §2 note below. | low as shipped | keep |

**Note on `SqliteSchemaBootstrapper.EnsureDatabase`** (measured in §5):
it validates core tables, then re-seeds the enterprise permission
schema **on every call**: 21 `permission_groups` inserts, one
`INSERT … ON CONFLICT DO UPDATE` **per `PermCat` code**, full
`DELETE` + re-`INSERT` rebuilds of the `ROLE_USER` and `ROLE_VIEWER`
role-permission sets, and `MatRepo.EnsureWarehouseSchema`. Its call
sites are company create/save/delete/restore/list operations — the
list path (`LoadFromSqliteAsync`) reaches it only when a Default
connection string is configured, which the shipped config does not
do. The re-seed is *self-healing by design* (it is how catalog
changes reach existing databases), so **this workstream does not
skip or gate it**; §5 records its measured cost so the owner can
judge, and §6 records the candidate (a seed-version probe) as a
future decision, since a probe removes the self-heal property and
is therefore a behavior change.

### Phase 4 — Company window (`ComWin`)
Constructor + XAML, `Loaded → LoadCompaniesAsync` (the same
`GetCompaniesAsync` as 3.11 + local store). Window construction is
ordinary WPF cost. On **Open**: `CoLocalStore.LoadAllAsync` (JSON
re-read), `ConfigureRuntimeDatabase` (in-memory config rewrite —
cheap), `AppHost.RebindDatabaseContext()` (reconstructs the ~25
connection-bound services — same object-construction cost class as
2.8, no DB I/O), then LogWin. **Ruling: keep** — nothing here is
repeated wastefully except the double company-list load (splash
3.11 then ComWin), which is one small JSON/registry read and keeps
the two windows independent; noted, not changed.

### Phase 5 — Login (`LogWin` → `MWin`)
- LogWin construction is light; `UiLoczr.ApplyToWindow` walks its
  small tree on Loaded.
- Login itself (after the user's action): user lookup +
  **`PwdHash` PBKDF2 at 600,000 iterations** — a deliberate security
  cost (by design, ~hundreds of ms on a slow CPU, once per login).
  **Ruling: keep, explicitly not a defect.**
- `PermServ.LoadForUserAsync` — a handful of SQL queries. Low.
- `App.ShowMainApp` → `SchemaBootstrapper.EnsureSchema()` again —
  the dead path of 2.6, returns immediately. Low.

### Phase 6 — Main window (`MWin`) construction → interactive
| # | Operation | Cost | Ruling |
|---|-----------|------|--------|
| 6.1 | `MainShVM` ctor — commands, localized strings, `ShowDashboard()`, fire-and-forget `LoadSessionAsync` | low–medium | keep |
| 6.2 | `InitializeComponent()` — `MWin.xaml` is 1,000 lines and contains the **full retained ribbon**: every tab/group/~266 `RibbonButton`s as real controls (the ribbon is the nav state source by design) | **high** — largest single XAML parse on the chain | keep — rebuilding the ribbon lazily would redesign the shell's state model; recorded in §6 as a future candidate, out of this workstream's safe scope |
| 6.3 | `BuildNavShell()` — creates one nav `Button` **per ribbon button (~266)** plus group labels, each with 2 bindings, for **all** tabs, synchronously in the constructor | medium–high | **defer**: build the initially shown section's body in the constructor; build every other section body on first demand (header click / `ExpandNavSection`) or at `Loaded`-priority right after first render, whichever comes first — identical controls, bindings and behavior, constructed later — §3/L4 |
| 6.4 | `UiLoczr.ApplyToWindow(MWin)` — full visual-tree localization walk (ribbon + nav + chrome); plus `GlobalWindowLoaded`'s `ShellContext.ApplyToWindow` (sync + one deferred re-apply) | medium | keep — deferring it would flash unlocalized text on first paint for ar/fr/tr; the phase marks (§4) will show its share on Windows |
| 6.5 | `RefreshRecycleBinBadgeAsync()` — fire-and-forget DB count | off-path | keep |
| 6.6 | First render of the maximized shell | inherent | keep |

### What is NOT on the path (verified, so nobody optimizes ghosts)
- `SchemaMigrator.ApplyAllAsync` (reads + SHA-256-hashes all 115
  SQLite migration files, ~860 KB, then compares checksums) runs
  **only** at company creation and in the standalone SqlMigrator
  tool — never at launch or company open. Its all-skipped cost is
  measured in §5 purely to document this.
- No network access occurs anywhere on the startup path
  (device registration / sync are background-coordinator work,
  started at 3.10 and operating asynchronously).
- The error catalog loads lazily (`Lazy<>` English index, ~39 KB)
  on first use. Keep.

---

## 2. Summary of rulings

| Landing | Change | Expected effect |
|---------|--------|-----------------|
| L1 | `StartupTiming` phase marks through LogServ (§4) | measurement itself; ~zero cost |
| L2 | SplWin: remove the 4.0 s floor, the budget-skip, and the 40 ms/step pacing; `VerifyIntegrityAsync` via `FileInfo.Length` | **−≈4.3 s of artificial delay on every launch** + whole-file read/hash removed |
| L3 | LocSvc: label index and resource load become lazy (first use); `AppHost._fallbackLocalization` becomes `Lazy<LocSvc>` | catalog parses at startup drop from ≈11 full parses (two instances × index+loads + SetLanguage reloads) to **1–2** (En base + active overlay, once) |
| L4 | MWin: nav section bodies for non-initial tabs built deferred/on-demand | MWin constructor sheds ~250 button constructions + ~500 bindings from the show path |

Behavior preservation: every change is timing/construction-order
only. Error-catalog reporting coverage on the startup chain is
untouched (all existing `AppHost.Errors.Report` sites stay; the
removed budget-skip *strengthens* it — security steps can no longer
be skipped on slow machines). Shell live context, language/RTL
adoption order (language is still adopted before any window), and
all functional gates are unchanged.

## 3. Landings

- **L1 — Instrumentation.** `StartupTiming` (in `App.xaml.cs`, next
  to `SchemaBootstrapper`): a process-start `Stopwatch`, buffered
  marks, flushed through `LogServ.LogLifecycle` as soon as logging
  exists, direct writes afterwards. Marks: `OnStartup.Begin`,
  `PrefsLoaded`, `LanguageAdopted`, `ContainerBuilt`,
  `SchemaEnsured`, `AuditInfraEnsured`, `AppHostInitialized`,
  `SplashShown`, `SplashPipelineDone`, `CompaniesChecked`,
  `ComWinShown`, `LogWinShown`, `LoginCompleted`, `MWinConstructed`,
  `MWinShown`, `MWinInteractive` (first `ApplicationIdle` after
  MWin shows).
- **L2 — Splash honesty.** As tabled in §1 Phase 3.
- **L3 — Lazy localization.** As tabled in §1 Phase 1/2.3.
- **L4 — Deferred nav bodies.** As tabled in §1 Phase 6.3.

Each landing is gated (§5 gate set) and pushed before the next.

## 4. How the owner measures on Windows

Every launch writes lines like this to `log.log` (LIFE/Lifecycle):

```
[2026-.. ..:..:ss.fff] [LIFE] [Lifecycle] [Company=-] [User=-] STARTUP [MWinShown] t=+1234ms d=56ms
```

`t=` is milliseconds since process start; `d=` is the delta from
the previous phase. Compare the same lines before/after (the
pre-change build writes only the phases that exist — for a true
before/after, the L1 build *is* the baseline: it adds marks and
changes nothing else). The phases that matter for the owner's
complaint: `SplashShown` (time to first feedback),
`ComWinShown`, and `MWinInteractive` (total cold-start time).

## 5. Segment timings (headless, this VM — NOT app timings)

Measured by `tools/PerfHarness` against the real code on a real
SchemaMigrator-built SQLite database. Filled in as landings proceed:

| Segment | Before | After | Note |
|---------|--------|-------|------|
| LocSvc construct + SetLanguage("en") | _pending_ | _pending_ | L3 |
| LocSvc construct + SetLanguage("ar") | _pending_ | _pending_ | L3 |
| LocSvc label-index build (all 4 catalogs) | _pending_ | deferred to first use | L3 |
| `EnsureDatabase` on a migrated DB (re-seed) | _pending_ | unchanged (by ruling) | §1 note |
| `SchemaMigrator.ApplyAllAsync`, all applied | _pending_ | unchanged (not on path) | §1 |
| Entry-assembly full read + SHA-256 (harness file equivalent) | _pending_ | `FileInfo.Length` only | L2 |
| Manifest resources of built MainApp assembly | `dba.lite.sql` **absent** (proof of §1/2.6) | — | — |

Gate set per landing (branch standard): solution build
`--no-incremental` = 0 errors / 0 warnings; SqlMigrator fresh-apply
+ idempotent through migration 124; `tools/audit_window_context.py`,
`verify_screenfit.py`, `verify_english_source.py`, `schema_audit.py`
exit 0; errors + Lang parity ×4; P2pHarness 39/39, W1Harness 36/36,
BinHarness 22/22, NumHarness 27/27, InfoHarness 35/35.

## 6. Deferred / owner-decision items (not done here, with reasons)

1. **PublishReadyToRun** — packaging decision; changes published
   artifacts; unverifiable from this VM. Expected to cut Phase 0
   JIT cost meaningfully on the owner's laptop.
2. **Permission seed probe** (`EnsureDatabase` re-seed skip when a
   stored seed version matches) — removes the self-heal property;
   a behavior change, needs the owner's call.
3. **Lazy ribbon construction in MWin.xaml** — the retained ribbon
   is the nav state source; virtualizing it is a shell redesign,
   not a timing tweak.
4. **Splash-before-init restructure** (showing SplWin before
   `AppHost.Initialize`) — L2 removes the artificial floor, after
   which the pre-splash Phase 2 work is small (and L3 shrinks it
   further); the remaining gain is first-feedback time only, and
   reordering OnStartup around the language-adoption invariant is
   the riskiest change in this file. Revisit only if the Windows
   phase lines show `SplashShown` still dominating.
