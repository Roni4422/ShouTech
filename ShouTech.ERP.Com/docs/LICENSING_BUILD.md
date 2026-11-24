# Licensing & protection build (feat/licensing)

Status: **ready to merge** into `reduce` (central merge; full gate
suite to be re-run on the integrated tree). Base: reduce tip
`4eafea21`. Migrations used: **none** (client-side licensing needs
no schema; the check-in/backup state lives in local files under
AppPaths, exactly as the telemetry study designed).

Governing documents: `~/workspace/research/dongle-module-licensing-design.md`
and `docs/TELEMETRY_BACKUP_STUDY.md` (feat/telemetry-backup).

## What is REAL today

- **Entitlement core** (`Services/Entitlements.cs`): provider chain
  in config order (default Dongle → LicenseFile → Online → DevMode),
  first non-null snapshot wins; the chain always produces an
  explicit snapshot — worst case `NotFound` (view-only), never a
  silent grant. `AppHost.Entitlements` is the one live gate;
  `InitializeAsync` runs in the splash license step (SplWin).
- **LicenseFile provider — fully real**: RSA-PSS-SHA256 signed
  license envelope (`{payload, signature}`, payload signed
  verbatim), verified against the vendor public key (env override
  `SHOUTECH_LICENSE_PUBLIC_KEY` → embedded release key). Payload:
  license id, customer, edition, modules, max users/companies/
  devices, dongle serial, device fingerprint map, issue/expiry,
  grace days, trial/viewer flags. Failure priority: signature →
  dongle binding (LIC-8005 missing / LIC-8006 wrong) → fingerprint
  (LIC-8006) → clock-rollback suspicion (LIC-8006) → expired past
  grace (LIC-8003) → grace (LIC-8009) → Valid.
- **Device fingerprint** (`Services/Fingerprint.cs`): per-component
  SHA-256, domain-separated (`SHOUTECH-FP-v1|<type>|<normalized>`);
  raw serials never leave the machine. Placeholder serials ("To be
  filled by O.E.M.", all-zeros, the Phoenix default UUID, …) count
  as absent. Matcher (study §4.4): 0 substantive changes = Match,
  1 = Drift (same device), ≥2 (or both strong components) =
  Mismatch. Windows collector reads MachineGuid (registry), base
  board/BIOS serials and system-drive volume serial (WMI), first
  physical NIC MAC, machine name; every component is individually
  guarded and collection failures report ONL-8504.
- **Dongle evidence**: `DongleProbe` calls the native
  ST_IsDonglePresent through the existing SecEng interop when the
  engine ships; payload dongle-serial binding consumes config
  serial evidence today and probe-detected serial automatically
  when HID lands.
- **Nav guard**: `RibbonNav.TryHandle` consults
  `Navigation/ModuleMap` (prefix defaults POS/MFG/CRM/HR + the
  design's exceptions; Help/Exit/Menu = PLATFORM, never gated). A
  gated module the license lacks opens **view-only** with a
  once-per-module-per-session LIC-8002 surfacing; the window still
  opens. `MainShVM.HandleRibbonAction` carries the same check as
  defense in depth. MWin surfaces the snapshot in the status bar
  (edition · source · grace countdown · DEMO state) with a
  persistent demo banner; quick-access buttons route through the
  same click path and are covered.
- **Demo gate**: `IsDemoMode = !CanWrite`, derived at login from
  signed artifacts only (license snapshot + verified server-verdict
  overlay). State-changing entry points refuse with LIC codes at
  the service layer: journal/invoice posting engines, POS posting,
  invoice repository save/post, backup **restore** (backup
  *creation* deliberately stays allowed — study §7.4), and the
  re-pointed AccServ/InvServ facades. UI disabling is presentation
  only.
- **LicenseWin** is the real status/activation surface: snapshot
  (status, edition, source, modules, dongle serial, fingerprint
  ShortId, expiry/grace), **Import signed license file**
  (verified before it may overwrite; a bad file is refused with
  LIC-8007 and the old license stays), online validation, and the
  check-in/backup status section with manual triggers.
- **Check-in + daily backup client** (`Services/CheckIn.cs`):
  allowlist report only (study §4 subsets; a harness test asserts
  the serialized report contains no forbidden key), requests signed
  with a per-installation RSA keypair (private key DPAPI-protected
  on Windows; on non-Windows dev machines only, a plain file —
  Windows DPAPI failure is a hard failure), verdicts RSA-PSS
  verified against the vendor key with per-installation monotonic
  `seq` anti-replay; a MISMATCH-CONFIRMED verdict applies the demo
  overlay via `EntitlementService.ApplyVerdictOverlay`, CLEARED /
  EXEMPT remove it; a missing or corrupt verdict cache means "no
  verdict", never demo. Backup upload: descriptor read from the
  STEI `.stb` manifest, initiate → SHA-256-hashed resumable parts →
  complete, idempotent by PackageId; daily marker per company set
  only after login/company-open; the cycle is triggered from the
  post-login path, **never** the splash/startup critical path.
  Inert when no server URL is configured.
- **SyncServ governed**: the dormant path that snapshotted raw
  local files (including `license.local.json`) for unauthenticated
  POST is retired — snapshot creation now purges legacy artifacts
  and creates nothing; queue processing performs no HTTP and drains
  legacy items. Proven dead by LicHarness (no snapshot files
  created; zero requests reach the mock's /sync endpoint).
- **Vendor tooling** (`tools/LicenseIssuer`): offline
  genkey / issue / verify. The release keypair was generated
  2026-11-24; the public half is embedded in
  `VendorKey.ReleasePublicKeyPem`. **The private half is held by
  the owner outside the repository**
  (`~/workspace/vendor-keys/shoutech-vendor-license-private.pem`,
  mode 600 — hand to the owner for offline custody; it is the
  issuance key for every customer license and for verdict signing).
  Note: the embedded key was rotated once during development,
  before any license had been issued against the first pair, so no
  issued license is affected.
- **Errors**: LIC-8002..8009 and ONL-8501..8506 added to
  `ErrorCodes.cs`, all four `Errors/*.json` catalogs, and
  `docs/ERROR_CATALOG.md` (catalog total 185 codes ×4).
- **EULA**: clause 7A (license-condition telemetry/backup +
  demo-conversion enforcement) adopted into
  `docs/legal/EULA_TEMPLATE.md`, replacing the contradictory §7
  sentence; marked pending lawyer review.

## Plug-in points (built ready, awaiting the owner's assets)

- **Dongle as an entitlement source**: `DongleProvider` probes
  presence but always falls through today — prot.cpp's
  validateLicense is an explicit stub that accepts any blob, and
  granting from a stub would be fake. When SecEng ships real HID
  I/O + signed-blob verification (the D5 follow-on native project),
  the provider builds its snapshot from ST_ValidateSession and
  takes the head of the chain with no other change.
- **Receiving server**: none exists. The client speaks study §6.1
  and is proven against the in-process mock in LicHarness
  (including retention-3 on the double). Verdict signing uses the
  vendor key seam; CLEARED/EXEMPT handling is live client-side.
- **Online provider grants** only from a *signed* entitlements
  claim in the validation response; unsigned responses refresh
  LicenSvc status but never grant.

## DevMode fencing — proof

- Config default: `Licensing.DevMode` has no enabling initializer;
  shipped `cfg/appset.json` carries `"DevMode": false`.
- The grant exists only inside `#if DEBUG` in `DevModeProvider`;
  a Release build logs a coded warning if a field-edited config
  asks for it and grants nothing.
- `tools/verify_devmode_fence.py` (run at this landing): **OK** —
  fails on a DevMode-enabled shipped config, an unfenced grant, or
  a default-true initializer.

## SyncServ disposition

Retired as a data path (see above). `ISyncServ` surface kept for
composition compatibility; snapshot/queue methods are governed
no-ops with purge semantics. The one behavior change: installations
that had a server URL configured stop uploading raw local files —
that upload was unauthenticated and included the license file; the
study (§3.3) required it gone before any server ships.

## Retirements in this build

- `Services/License.cs` (the dead fake gate: LicServ granted
  Enterprise + all modules whenever SecEng validated, "development
  mode" Core otherwise; nothing constructed it) — deleted.
  AccServ/InvServ re-pointed at `IEntitlementService`;
  ModMenuServ derives the visible-module set from the snapshot;
  BkpServ's unused ILicServ constructor removed.
- Design deviation, recorded: entitlement types live in the **app
  layer** (the design placed them in Domain) because src/ LicSvc /
  ILicProv are unconstructible dead code and AppHost is the live
  composition root; the Domain `ModKey` vocabulary is used as-is.
- Telemetry status surface lives in **LicenseWin**, not the OptWin
  Backup section the study suggested, to avoid colliding with the
  OptWin workstream's active edits on reduce.

## Gates (this branch, per landing)

- Solution build `--no-incremental`: 0 errors / 0 warnings.
- SqlMigrator fresh-apply through 126 + idempotent second run
  (no migrations added by this build).
- 4 audits (window context, screenfit, english source, schema):
  exit 0.
- Errors parity 185 ×4 exact-set; Lang parity ×4.
- Harnesses: P2p 39/39, W1 36/36, Bin 22/22, Num 27/27,
  Info 35/35, LicHarness (see its section in this doc).
- Kern.Tests additions (provider matrix, chain order, gate matrix,
  matcher recorded sets) compile; execution is Windows-owed like
  the rest of Kern.Tests.

## Conflict-watch files for the central merge

`src/Domain/ErrorCodes.cs`, `cod/app/src/Resources/Errors/{En,Ar,Fr,Tr}.json`,
`cod/app/src/Resources/Lang/{En,Ar,Fr,Tr}.json`,
`cod/app/src/Services/{AppHost,Online,Backup,Accounting,Inventory,Shell,Auth,SplWin-related Views/SplWin.xaml.cs}.cs`,
`cod/app/src/Views/{MWin.xaml,MWin.xaml.cs,LicenseWin.cs,AddonsWin.cs}`,
`cod/app/src/Services/RibbonNav.cs`, `cod/app/src/ViewModels/MainShVM.cs`,
`cod/app/src/Configuration/AppConfig.cs`, `cfg/appset.json`,
`docs/ERROR_CATALOG.md`, `docs/legal/EULA_TEMPLATE.md`,
`cod/app/src/MainApp.csproj` (System.Management reference).
No migration numbers were taken; nothing to renumber.
