# Telemetry, Daily Backup Upload & License Enforcement — Design Study

**Status:** DESIGN PHASE ONLY. No upload or enforcement code is written in
this phase. Build is gated on owner decisions (B) and (C) in §10, and on
the owner's confirmation of the §1 principle.
**Branch:** `feat/telemetry-backup`, base `origin/reduce` @ `07a1e5bc`.
**Owner requirements:** 2026-11-24 20:25 (send user info, company info
and a daily backup to the vendor server when online; server keeps at
most 3 copies), 20:27 (consent model = license condition,
EULA-disclosed, no customer opt-in), 20:27 refinement (the information
is GENERAL program + device information, collected so the vendor can
COMPARE it against its own licensing/installation registry — compliance
verification), 20:28–20:30 (enforcement: on a confirmed mismatch the
copy converts IMMEDIATELY — no warning, no grace period — to a DEMO /
VIEW-ONLY state; sabotage excluded, §7.7), 20:32 (the two-layer
offline-first principle, §1).

---

## 1. Headline principle — two layers, offline-first

**ShouTech is offline-first. The program must never require staying in
contact with the vendor server.** The whole design is two layers:

### Layer 1 — LOCAL (primary enforcement, zero network)

- The license is a **signed license state** bound to the device
  fingerprint (§4.4), verified **locally at every startup**: signature
  valid, fingerprint binding holds, license terms (edition, modules,
  seats, term) as issued. This aligns with the existing
  dongle/licensing design (`~/workspace/research/dongle-module-licensing-design.md`):
  the `SignedLicenseFileProvider` model — a JSON payload (`customer,
  dongleSerial?, hardwareId?, edition, modules[], maxUsers,
  maxCompanies, issuedAt, expiresAt`) plus a detached RSA signature
  verified against the release-embedded vendor public key, surfaced
  through `AppHost.Entitlements`, with its existing coded failures
  (LIC-8006 binding mismatch, LIC-8007 unsigned/tampered).
- A copy moved to an unregistered device **fails locally, with no
  server involved**: the fingerprint on the new machine does not match
  the fingerprint bound in the signed license state, and the copy
  lands in the same demo/view-only state defined in §7.4 (reason
  category: local binding).
- Layer 1 needs no network, no clock-based contact deadline, and no
  server permission to run. A customer who never connects to the
  internet runs the full program for the whole license term.

### Layer 2 — OPPORTUNISTIC (only when the machine happens to be online)

- A **brief check-in** — at launch after login, or once daily in the
  background — performs three things and then disconnects: the
  compliance report (§4), the daily backup upload (§5, §6.4), and
  retrieval of any signed server verdict (§7).
- **No continuous connection. No heartbeat. No time-based lease expiry
  that locks a genuinely offline customer.** Server-layer enforcement
  requires an **affirmative signed verdict received during a voluntary
  contact** (§7.2). Silence from the server — for a day or for a year —
  changes nothing locally.
- Check-in is a background task on the existing coordinator substrate
  (§3). It never blocks, delays, or gates startup or any program
  function, and it skips silently when offline.

### The honest limit (stated for the owner's confirmation)

A permanently-offline pirate copy is **unreachable by layer 2 by
design** — no verdict can ever be delivered to a machine that never
makes contact. What stops casual copying is layer 1: the signed,
fingerprint-bound license state makes "copy the folder to another
machine" fail on the other machine. Layer 1 raises the cost of piracy;
like every client-side scheme in desktop software it is not
unbreakable against a determined attacker patching the binary, and
this study does not claim otherwise. **That trade-off — real
offline-first operation in exchange for layer 2 being advisory-range
only — is the price of the principle, and the owner is asked to
confirm it when this study lands.**

Everything below conforms to this section. Where an earlier draft idea
conflicted with it (a phone-home lease whose expiry locks the program),
it has been removed, not retained.

---

## 2. Purpose (the stated reasons for collection — recorded verbatim in intent)

1. **Compliance comparison.** On launch, when internet is available,
   the program reports general information about the program and the
   device it runs on. The vendor compares the report against its own
   licensing/installation registry to verify that the copy in the
   field is the copy that was licensed, on the device it was licensed
   to.
2. **Off-site backup custody.** Once per day the program uploads one
   backup copy of the company data to the vendor server. The server
   never retains more than **3** copies per company (retention-3,
   §6.5).
3. **Enforcement.** If the confirmed comparison shows the copy does
   not match the registered data, the server returns a signed verdict
   and the copy converts to demo/view-only mode (§7) — delivered and
   applied only under the §1 layer rules.

Anything not serving one of these three purposes is out of scope for
the report payload. The payload is an allowlist, never a serialization
of a store record (§4.5).

---

## 3. Current state — what already exists in the code (audit, base 07a1e5bc)

A dormant online stack already exists. It shapes the build phase: the
substrate is reused and governed, not rebuilt.

| Piece | Where | State |
|---|---|---|
| Connectivity monitor (`ConnServ`) | `Services/Online.cs` | Live. Probes the configured activation/API URL; states Offline / Online / ServerReachable. |
| Device info (`DevInfoSv`) | `Services/Online.cs` | Live. Collects machine name, OS string, Windows user, domain, CPU count, working set, culture; `DeviceId` persisted in `cfg/device.id`; snapshot cached in `cfg/device.json`. `RegisterWithServerAsync` POSTs the snapshot to `Licensing.Activation.ActivationServer` + `OnlineServices.DeviceRegisterEndpoint` — only when both are configured. |
| License service (`LicenSvc`) | `Services/Online.cs` | Stub-grade (per the dongle design audit: unsigned local JSON, no module concept). Local file `cfg/license.local.json`; online validation POST exists, keyed by license key + HardwareId + DeviceId. Layer 1 (§1) replaces this with the signed license state from the licensing design; `LicenSvc` is the migration source, not the target. |
| Sync queue (`SyncServ`) | `Services/Online.cs` | **Live and problematic — see §3.1.** |
| Online coordinator (`OnlCoord`) | `Services/Online.cs` | Live. Started from `SplWin` (splash, pre-login) via `AppHost.OnlineCoordinator?.Start()`; hourly cycle timer; runs backup + sync + license validation when online. This is the layer-2 check-in substrate. |
| Backup service (`BkpServ`) | `Services/Backup.cs` | Live for local backups. STEI `.stb` packages (§5). Local rotation `PruneOldBackups` keeps `Database.Backup.MaxBackups` (shipped default **3**, interval 24 h). |
| Backup upload | `BkpServ.TryUploadRotatedBackupsAsync` | **Stub — returns `false` unconditionally.** `OnlineServices.BackupUploadEndpoint` (`"/backup/upload"`) is defined in config and referenced nowhere else. No backup bytes leave the machine today. |
| Server address | `cfg/appset.json` → `Licensing.Activation.ActivationServer` | **Empty string in the shipped config.** With no base URL, every online path above no-ops. **No vendor server or endpoint exists in any project record** (decision B, §10). |

### 3.1 Pre-existing conditions the build phase must govern (findings)

1. **A dormant, unauthenticated upload path already exists.**
   `SyncServ.SnapshotLocalUserDataAsync` runs at *every* startup
   (`SplWin` step "Preparing local user data...") and on every
   coordinator cycle. It writes a JSON snapshot containing the device
   snapshot **plus the raw contents of whichever of these files
   exist**: `cfg/user.dat`, `userprefs.json`,
   `cfg/license.local.json`, `cfg/device.json` — i.e. the license file
   (license key, support grant) is bundled verbatim.
   `ProcessQueueAsync` POSTs queued snapshots to the sync endpoint
   with **no authentication of any kind**. Today this is inert only
   because no server URL is configured. The moment any deployment sets
   a server URL, this path activates as-is. **Ruling:** the build
   phase must retire or rebuild this path in the same change that
   activates any upload; the §4 report payload replaces the file-dump
   snapshot entirely.
2. **Unbounded local accumulation.** Snapshots are only removed from
   `sync/queue.json` after a successful upload. With no server
   configured, every launch adds a `sync/snapshots/users_<stamp>.json`
   file and a queue entry that are never cleaned. The build phase must
   cap/expire the local queue regardless of the telemetry feature.
3. **Hardcoded company `"001"`.** `BkpServ.PerformRotatedBackup`,
   `IsBackupDue` and `RunOnlineBackupCycleAsync` operate on company
   `"001"` literally, while `CreateBackupAsync(companyCode)` is
   general. The coordinator also starts before any company is open
   (it logs "Backup skipped because no company database is currently
   selected"). Daily-upload semantics (§6.3) must key off the company
   actually opened, per company, not `"001"`.
4. **Config anticipates the wrong retention number.**
   `OnlineServicesSettings.MaxServerBackupCopies` defaults to **2**
   (comment: "1 or 2"). The owner's requirement is **3**. Retention is
   server-authoritative in this design (§6.5); the client knob becomes
   informational and must be corrected to 3 so the two never disagree
   in print.
5. **The EULA template currently promises the opposite.** §7 of
   `docs/legal/EULA_TEMPLATE.md` states the Licensor "does not receive
   the Licensee's business data except data the Licensee affirmatively
   sends for support or activation". A license-condition upload cannot
   ship under that text. §9 drafts the replacement clause; the
   template is not amended by this study — adoption is a build-phase
   step with the owner's lawyer review, as the template itself
   requires.
6. **The local company master store holds password hashes.**
   `data/companies.json` (`StoredCoRec`) contains `AdminPasswordHash`
   + `AdminPasswordSalt` and local paths (`DbPath`, `BackupPath`).
   Any outward payload built by serializing store records would leak
   them. This is why §4 is an allowlist built field-by-field.

---

## 4. Data inventory — what exists, what is sent, what is never sent

### 4.1 Program information (purpose 1: comparison)

Source stores: `AppCfgSvc` (`cfg/appset.json`), the license state
(§1 layer 1; today `LicenSvc` / `cfg/license.local.json`), assembly
metadata, the STEI signing identity.

| Field | Source today | Sent? |
|---|---|---|
| App version | `Application.Version` (default `"10.6.0"`) / assembly version | Yes |
| Instance ID | `Application.InstanceId` (default `"ST-ERP-001"`) | Yes |
| Device ID | `cfg/device.id` (persistent per installation) | Yes — the installation's primary key in the registry |
| License key / dongle serial | `Licensing.DongleInfo.SerialNumber`, fallback literal `"LOCAL-DEV"` | Yes — it *is* the license identity being verified. (It is a bearer secret today; §6.2 replaces bearer-only auth.) |
| License status / expiry / activated-at / last online check | `LicenseInfo` today; the signed license state after the licensing build | Yes |
| Licensed limits | `Licensing.MaxUsers / MaxCompanies / MaxWarehouses / MaxProducts`, `Features[]` | Yes — comparison is meaningless if the client doesn't state what it believes it holds |
| STEI signing-key fingerprint | Per-installation RSA key (`Security/stei-signing.key`) | Yes — binds the installation's backup packages to the installation |
| Support grant | `LicenseInfo.SupportGrant` | **No** — a capability token, not identity. Its *existence* may be reported as a boolean if support entitlement comparison is ever required; the grant and its signature never travel in the routine report. |

### 4.2 User information (purpose 1: comparison against licensed seats)

Source: `users` table (SQLite `001_sqlite_core.sql`, PG
`002_security.sql`): `user_id, company_id, username, password_hash,
password_salt, full_name, full_name_ar, full_name_en, email,
must_change_password, is_active, is_deleted, created_at`; roles via
`user_roles`/`roles`. The session object `UserInfo` additionally has
`LastLogin`, but **no last-login is persisted in the schema today** —
the report cannot include it until a build-phase migration adds it
(flagged, not assumed).

**Plausible meaning of the owner's "user information":** the vendor
needs to verify the installation's users against the licensed seats —
who the users are and how many. **Sent subset (per user):** `user_id`,
`username`, `full_name`, `email`, `is_active`, role codes,
`created_at`; plus the aggregate active-user count for the seat
comparison.

**Never sent:** `password_hash`, `password_salt` (credential material —
no comparison purpose can ever need it; the vendor authenticates the
*installation*, not the customer's users), `must_change_password`
(security-posture state), `UsrPref` contents wholesale (per-user UI
preferences; includes a remembered username — not registry data).

### 4.3 Company information (purpose 1: the licensed company)

Sources: `companies` table (per-company DB) and the installation
master store `StoredCoRec` (`data/companies.json`): code,
`company_name_ar/en`, tax number, commercial registration, email,
phone, mobile, address_line1, currency code, language code,
`is_active`, `created_at`, plus the record's `License` field.

**Sent subset:** company code, legal names (ar/en), tax number,
commercial registration, email, phone, currency, language, license
reference, active flag. This is the identity the license was issued
to; the server compares it against the License Certificate data.

**Never sent:** `AdminPasswordHash`, `AdminPasswordSalt`,
`AdminFailedLoginCount`, `AdminLockedUntilUtc` (credential + lockout
state from the master store), `DbPath` / `AltDbPath` / `BackupPath`
(local filesystem layout — a security disclosure with no comparison
value), the setup request's plaintext `AdminPassword` (transient in
`CompanySetupRequest`; must never persist or transmit).

### 4.4 Device information — a comparison fingerprint, not a hardware dump

**What exists today:** `DevInfoSv` reads only
`Environment.MachineName`, `Environment.OSVersion.VersionString`,
`Environment.UserName`, user-domain name, processor count, working
set, UI culture. Its `HardwareId = SHA256(machine | osVersion |
domain)` is **not a device fingerprint**: it changes on an OS
reinstall or a machine rename, and two cloned machines with the same
name collide. No WMI/CIM, registry MachineGuid, or serial reading
exists anywhere in the codebase today (verified by search). The build
phase adds a dedicated collector, shared by layer 1 binding (§1) and
the layer 2 report.

**Design — component hashes, matched hash-to-hash:**

- Components (each read once per check, normalized — trimmed,
  upper-cased, placeholder-filtered — then hashed individually):
  1. Windows `MachineGuid` (registry `HKLM\SOFTWARE\Microsoft\Cryptography`) — strong, survives most hardware swaps, changes on OS reinstall.
  2. Motherboard serial (`Win32_BaseBoard.SerialNumber`) — strong.
  3. BIOS serial (`Win32_BIOS.SerialNumber`) — medium (often duplicates the board serial; counts once if identical).
  4. System-drive volume serial — medium (changes on reformat/replace).
  5. Primary physical NIC MAC — weak-medium (changes with NIC/dock swaps).
  6. Machine name — weak (user-editable; corroborating only).
- Each component is sent (layer 2) and bound (layer 1) as
  `SHA-256("SHOUTECH-FP-v1" | type | normalized value)`. The vendor
  registry stores the same component hashes captured at
  registration/activation, so comparison is hash-to-hash: **raw
  hardware serials never leave the machine and are never stored by
  the vendor.** The domain separator keeps the hashes from being
  matched against generic serial-hash databases.
- **Where raw values would be needed:** nowhere in routine operation.
  The single exception is a disputed mismatch handled by a human, and
  even then the channel is the existing affirmative support flow
  (time-boxed, signed `SupportGrant`, scope `diagnostics`) — the
  customer shows the vendor a component on screen; the routine report
  never carries it. Stated plainly: if a future requirement claims to
  need raw serials in the routine report, that claim should be treated
  as a design smell, not a feature.
- **Absent/placeholder handling (VMs and cheap hardware):** well-known
  placeholder serials (`"To be filled by O.E.M."`, all-zeros, empty,
  default VM UUID patterns) are treated as **component absent**, never
  as a value — otherwise every VM on one hypervisor template would
  share a "serial". If fewer than 3 of the 5 substantive components
  are present, identity falls back to `MachineGuid` (if present) +
  the persisted `DeviceId`, and the report marks the fingerprint
  `quality: reduced` so the server applies wider tolerance (§7.3).
- **Stability / tolerance (feeds §7.3 directly):** a report is scored
  against the registered component set:
  - *Match:* all present strong components equal.
  - *Drift (same device):* exactly one substantive component changed
    (drive replaced, NIC changed, OS reinstall moving `MachineGuid`
    alone). The server records the new component hash as the
    component's current value (history retained) — hardware evolves,
    the registry follows it, no escalation.
  - *Mismatch candidate:* two or more strong components differ, or
    `MachineGuid` **and** board serial both differ. Only a candidate —
    §7.3 defines what confirms it.
- **Clone caveat, stated plainly:** a bit-identical VM clone inherits
  every component. The fingerprint is one signal; the binding that
  catches clones is license identity + `DeviceId` + seat/device
  counts in the registry (the same license key reporting from two
  distinct confirmed devices is a §7.3 confirmation input,
  independent of fingerprint score).

### 4.5 Consolidated NEVER-SEND list

Password hashes and salts (users and company admin); the plaintext
setup admin password; support-grant tokens/signatures; the STEI
package password and the backup key material (`bkpkey.dat` contents);
the local license file verbatim; local filesystem paths; lockout
counters; UI preferences wholesale; **and any customer business data
other than the daily backup package itself** (the report carries
identity and counts — never documents, journals, or party data).

---

## 5. Backup content — what "a backup copy" is here

The payload is the product's existing backup artifact, unchanged: a
**STEI package** (`.stb`) produced by `BkpServ.CreateBackupAsync` via
`SteiSvc.ExportAsync` for the company database (SQLite or PostgreSQL).

- **Contents:** every company-data table for the active company,
  company-scoped in the exporter (company-less child tables are
  scoped through their parents). **Excluded by design:** `companies`,
  `users`, `roles`, `permissions`, `user_roles`, `role_permissions`,
  `permission_groups`, `user_permissions`, `journal_audit_events`,
  `schema_migrations`, `sqlite_sequence` — identity/security tables
  are installation-local and never travel, including to the vendor
  server. A restored backup therefore never restores credentials;
  account re-provisioning on restore is an existing product
  behaviour, not a telemetry concern.
- **Protection already in the format:** AES-256-GCM payload
  encryption (key from PBKDF2-SHA256, 310,000 iterations, over a
  per-installation random 256-bit package password held in a
  DPAPI-protected store, `CommonApplicationData\ShouTech\sec\bkpkey.dat`);
  a signed manifest (RSA-PSS-SHA256, per-installation signing key)
  carrying `PayloadHash` (SHA-256 of the plaintext payload),
  table/row counts, source company code/name, database provider, app
  version, export timestamps and the exporting user. The server can
  verify hash + signature and read the manifest **without being able
  to read the data** (see decision C, §10).
- **Size expectations (no invented numbers):** the payload is the
  company's rows serialized as JSON, then encrypted; ciphertext does
  not compress, so package size ≈ the company's dataset as JSON —
  small companies are in the low megabytes, and size grows linearly
  with documents/journals. The manifest's table/row counts let the
  server sanity-check expectations per company. Server storage per
  company ≈ **3 × one daily package** (retention-3). There is **no
  incremental/differential engine** — every daily upload is a full
  package; §6.4's resumable transfer exists precisely because of that.
- **Cadence mismatch to fix in build:** local auto-backup is
  interval-driven (24 h default) and coordinator-driven (hourly
  cycles); the requirement is **one uploaded copy per day**. The
  build keys the upload to the daily marker (§6.3), reusing the
  newest local package of the day when one exists instead of forcing
  a second export.

---

## 6. Protocol design (layer 2 — opportunistic check-in)

All endpoints hang off the already-configured base
`Licensing.Activation.ActivationServer`. New paths extend
`OnlineServicesSettings` alongside the existing ones (`SyncEndpoint`,
`DeviceRegisterEndpoint`, `LicenseValidateEndpoint`,
`BackupUploadEndpoint`). TLS 1.2+ is mandatory; a plain-HTTP base URL
is refused by the client (the current `ConnServ` probe accepts any
scheme — the build tightens this for the check-in endpoints).

### 6.1 Endpoints (server contract we would need)

| Endpoint | Purpose |
|---|---|
| `POST /installations/report` | The §4 compliance report (program, license, company, user subset, fingerprint component hashes). Response carries the current signed verdict (§7.2) and the server's backup inventory for this installation. |
| `POST /backup/initiate` | Opens an upload session for one package: client sends manifest summary (PackageId, size, PayloadHash, company code, backup date). Server returns an upload id + the part size + which parts it already holds (resume). |
| `PUT /backup/{uploadId}/parts/{n}` | One part, with its SHA-256. Idempotent per part. |
| `POST /backup/{uploadId}/complete` | Server assembles, verifies whole-package hash against the manifest `PayloadHash`, verifies the STEI signature, records the copy, applies retention-3 (§6.5), and returns the authoritative inventory. |
| `GET /installations/verdict` | Lightweight verdict fetch, used by the banner's "check now" action (§7.6) outside the normal check-in. Response is the same signed verdict object. |

There is no separate "telemetry socket", no push channel, and no
server-initiated contact of any kind — the server can only answer
when the client voluntarily checks in (§1).

### 6.2 Per-installation authentication

- The license key alone is a bearer secret and is **not** sufficient
  as the only credential for uploads that also drive enforcement.
- At activation/registration the client generates a per-installation
  API keypair (same local-protection pattern as the existing STEI
  signing key). The public key is registered against the license
  record; every check-in request is signed (method, path, body hash,
  timestamp, nonce) and the server verifies against the registered
  public key. The license key/dongle serial remains the *identity*;
  the keypair is the *credential*. This ties directly into the §1
  layer-1 identity: the same installation record holds the license
  state binding, the fingerprint component hashes, and the API public
  key.
- A lost/rotated keypair is a vendor-side registry operation (support
  flow), never a silent client-side regeneration — a silent regen
  would let a cloned copy mint itself a fresh identity.

### 6.3 Once-daily semantics

- **Report:** due on the first check-in of each local day. **Backup:**
  due once per local day per company.
- **Trigger point:** the coordinator starts at splash, before login
  and before any company is open. The build therefore splits duties:
  connectivity monitoring may start at splash as today, but the
  report and the backup upload run **after login and company open**
  (the report needs the company and its users; the backup needs the
  company database). This also conforms to the startup-performance
  workstream's rulings: no new synchronous work enters the splash
  chain; everything here is background work after the shell is up.
- **Local marker:** per company, the date of the last *successful*
  report and the last *successful* backup upload, stored in the local
  store (under `AppPaths.Cfg`, keyed by company code). The marker is
  a scheduling convenience only — the server's own record is
  authoritative, and a wiped marker causes at most one duplicate
  report/upload, which the server deduplicates by the idempotency
  keys below.
- **Offline:** silent skip. Nothing is queued visibly, nothing nags.
  The next launch (or the next daily background cycle while running)
  retries. A customer offline for a month simply has a month-old
  marker; per §1 that is a normal state, not a violation.
- **Idempotency:** report keyed by (installation, local date);
  upload keyed by STEI `PackageId` — re-sending the same package is a
  no-op the server acknowledges, never a second copy.

### 6.4 Transfer mechanics

- Chunked upload with per-part SHA-256 and server-side resume
  (§6.1): an interrupted upload continues from the parts the server
  already holds, across launches if needed.
- Bounded retries with backoff inside a check-in (3 attempts per
  part), then the attempt ends and the next launch/cycle continues
  it. No retry loop may keep the machine awake or the app open.
- Metered-connection courtesy: on a connection Windows reports as
  metered, the backup upload defers unless the last successful upload
  is more than 3 days old; the (small) report still goes. This is a
  courtesy rule, not a compliance gate.

### 6.5 Retention-3 (server-authoritative)

- After each completed upload, the **server** prunes that
  (installation, company) set to the newest **3** copies by backup
  date. The client never instructs deletions and never deletes server
  copies by its own decision.
- The `complete` response returns the authoritative inventory (copy
  ids, dates, sizes, hashes) including what was pruned, and the
  report response carries the same inventory, so the client's status
  surface (§8) shows the server's truth rather than the client's
  guess.
- Object naming on the server: `{licenseId}/{companyCode}/{yyyy-MM-dd}/{packageId}.stb`.
  The date is the backup's manifest date (UTC), not the upload date,
  so a resumed upload of yesterday's package still lands in
  yesterday's slot and cannot push out a newer copy.

---

## 7. Enforcement — signed verdicts and demo conversion

Owner decisions 20:28–20:30, conformed to §1: the endpoint of
enforcement is **conversion to a demo/view-only state** — never
destruction, never data hostage-taking. There is **no warning and no
grace period** before conversion; the safeguard that replaces them is
the rigour of the CONFIRMED threshold (§7.3), and the advance
disclosure is the EULA clause itself (§9).

### 7.1 Two enforcement sources, one endpoint state

- **Layer 1 (local, §1):** the signed license state fails local
  verification — signature invalid/tampered (LIC-8007), or the device
  fingerprint does not bind (LIC-8006), or the license term has
  expired as issued. No server is involved and none is needed.
- **Layer 2 (server):** an affirmative signed verdict of
  MISMATCH-CONFIRMED (§7.2), received during a voluntary check-in.
- Both produce the same demo state (§7.4) with a different recorded
  reason category. Neither deletes, corrupts, encrypts-away, or
  otherwise touches the customer's data (§7.7).

### 7.2 The signed verdict (the only server→client control channel)

- Verdict object: `{ installationId, licenseId, verdict, reasonCategory, seq, issuedAtUtc, detailRef }`
  where `verdict ∈ { OK, MISMATCH-CONFIRMED, EXEMPT, CLEARED }` and
  `reasonCategory ∈ { fingerprint, duplicate-device, license-unknown, license-revoked, local-binding, term }`
  (categories are customer-presentable; `detailRef` points at the
  vendor-side case record, not at raw evidence).
- **Signature:** RSA-PSS-SHA256 over the canonical verdict, verified
  by the client against the release-embedded vendor public key —
  the exact verification pattern the codebase already uses for
  support grants (`LicenSvc.ValidateSupportAccess`: env-override key
  for support, embedded key in release). **An unsigned or
  bad-signature verdict is discarded and logged, never acted on** —
  an unsigned kill channel would itself be a critical vulnerability:
  anyone able to spoof a response could demo-lock every customer.
- **Anti-replay:** `seq` is a per-installation monotonic counter
  maintained by the server. The client stores the highest `seq` it
  has applied (inside the signed local license-state store, not a
  plain flag file) and ignores any verdict with `seq` ≤ stored. A
  replayed old OK cannot resurrect a converted copy; a replayed old
  MISMATCH cannot re-lock a cleared one.
- The verdict cache is stored with its signature and re-verified at
  every startup in the login chain. A missing or corrupt cache means
  "no verdict" — **not** demo: corruption must fail toward the
  customer's availability, with layer 1 still fully in force. (A
  fail-closed cache would let disk corruption alone lock every
  honest customer.)

### 7.3 CONFIRMED — the threshold that replaces the grace period

Because conversion is immediate (§7.5), a mismatch may be confirmed
**only** when all of the following hold, evaluated server-side over
the registry and the report history:

1. **Fingerprint evidence is strong:** the §4.4 score is
   *mismatch candidate* or worse — two or more strong components
   differ, or `MachineGuid` **and** board serial both differ,
   placeholders excluded. A single raw field difference, a single
   changed component (drift), a reduced-quality fingerprint, a clock
   anomaly, or a missing report can **never** confirm.
2. **Persistence across independent contacts:** the candidate state
   is present in reports from at least **two different local days**.
   One report — however wrong it looks — confirms nothing. Immediacy
   applies to the *response* once confirmed, not to thin evidence.
3. **Identity inconsistency:** in addition to (1)+(2), at least one
   of: the same license key is confirmed active from two distinct
   devices beyond its licensed device/seat count; the reported
   license key is unknown to the registry; the license is revoked in
   the registry for cause (non-payment, fraud) by a human decision.
   Pure hardware confusion without an identity problem resolves as
   drift/re-registration (§7.6), not conversion.

A vendor-side human review queue sits between "candidate" and
"confirmed" for the first enforcement release: the system proposes,
a person confirms. Automating that last step is a later, separate
decision once false-positive rates are measured.

### 7.4 The demo / view-only state — precise definition

**Allowed (the customer's records stay fully theirs):** open and view
every window and record; search and filter; run reports; **print and
export existing documents and data** (including the product's data
export facilities and local backup creation — a customer in demo mode
can still take their books out, in full, at any time).

**Blocked:** creating, editing, posting, or deleting any transaction
or card; period closing; any state-changing operation, including
settings changes that alter data (pure view preferences remain
allowed).

**Enforcement point:** the demo state is a property of the session,
derived in the startup chain at login from (layer-1 license
verification) + (cached signed verdict, §7.2). It is enforced at the
**service layer** — the same central gate the posting/save paths
already pass through (session/permission context), so no window can
forget to check — with UI disabling as presentation only. It is
**tamper-evident, never a plain editable flag**: the state derives
from signed artifacts (license state file + verdict cache, both
signature-verified at startup); editing local JSON cannot clear it,
because there is no local JSON boolean to edit.

### 7.5 Immediacy, and the banner's role

On the check-in that delivers a MISMATCH-CONFIRMED verdict, the
current session converts at that check (open windows keep their data
on screen; the next state-changing action is refused and the banner
appears). There is no countdown and no "days remaining" — by the
owner's reasoning, a warning does not deter a hacker, and the honest
customer's protection is §7.3's threshold plus §7.6's fast
regularization, not a delay.

The persistent demo banner shows: the reason category, the
installation and license identifiers to quote, the vendor contact /
regularization path, and a **"check now"** action (re-fetches the
verdict immediately, §6.1). The banner is **not a warning** — it
appears *after* conversion and is the resolution channel, and the
study records that distinction in one line as instructed.

### 7.6 Reactivation — designed for one support interaction

Because there is no grace period, an honest false positive must be
resolvable in **one** support interaction:

1. Customer reads the identifiers + reason category off the banner
   and contacts the vendor.
2. Vendor verifies against the registry (e.g. a legitimate hardware
   change: board replaced → two strong components changed → candidate
   → wrongly confirmed). The vendor either issues a **CLEARED**
   verdict (signed, higher `seq`) or performs **re-registration**:
   the registry's component hashes are updated to the device's
   current fingerprint, a fresh signed license state is issued, and a
   CLEARED verdict accompanies it.
3. The customer's "check now" (or the next launch's check-in) applies
   the clearance and **full function returns in the same session**
   where the session is still open, otherwise at next login — no
   reinstall, no data migration, nothing was ever taken away but the
   ability to write.
4. **Audit trail, both sides:** the client logs every verdict
   receipt, conversion, clearance and re-registration with catalog
   codes (§8); the server logs the determination, the confirming
   evidence, the human confirmer, and the resolution. The whole
   episode is reconstructable from the two logs.

**Vendor-side per-license disable:** the registry can mark an
installation EXEMPT (support cases, disputes, strategic customers) —
an EXEMPT verdict instructs the client to skip enforcement for that
installation until further notice. This is the vendor's switch, held
server-side; per decision (A) there is **no customer-side off
switch**, and none is designed.

### 7.7 Explicitly out of scope

Any destruction or corruption of the customer's installation or data
("تخريب") is **excluded by design decision**: it would destroy the
customer's own business records, create data-destruction legal
liability, and make a single false positive catastrophic; demo
conversion achieves the enforcement goal without it. No sabotage
mechanism is designed, here or elsewhere.

### 7.8 One mechanism, two uses

The demo state is also exactly what a **trial edition** needs: a
signed license state issued with a short term and/or a reduced module
set produces a genuine time/feature-limited trial for sales demos,
and a trial that is later licensed converts by replacing the signed
state — no separate trial codebase, no demo-data fakery.

---

## 8. Visibility & control

**Customer-visible status surface** (extends the existing OptWin
Backup section, whose last-backup readout is already derived from
`ListBackups` with a >7-day staleness warning; the Tools hub links to
the same surface rather than duplicating it):

- Configured server address (or "not configured").
- Layer-1 license state: edition, binding status, term.
- Last report: date/time, result, fingerprint quality.
- Last backup upload: date, package date, size, short hash; server
  copies currently held (from the last inventory response, §6.5).
- Enforcement state: full function / demo, with reason category and
  the banner's identifiers when in demo.
- Manual actions: "send report now", "upload today's backup now",
  "check now" (verdict). These are *triggers*, not switches — per
  decision (A) there is no customer-side disable to put here, and the
  surface must not pretend otherwise.

**Local audit/logging:** every check-in outcome, upload, verdict and
state change is logged through the existing conventions —
`ILogServ` categories (`Telemetry`, `Backup`, `Verdict`) into
`log.log`, and failures through the error catalog. The build phase
adds an **ONL-85xx** error family (ranges in use: LIC-80xx, DSG-81xx,
BIN-82xx, NUM-83xx, INF-84xx) with entries in all four language
catalogs, e.g. verdict-signature-invalid, verdict-replay-ignored,
upload-hash-mismatch, fingerprint-collector-failed — no silent
catches, per the standing error-reporting rule.

**Kill-switches, stated symmetrically:** the *vendor* holds the
per-license EXEMPT/disable (§7.6) and the registry itself. The
*customer* holds physical control of their network and their data —
an offline customer is simply never contacted (§1) — but holds no
in-program off switch for the check-in, because the check-in is a
license condition (§9). Both halves of that sentence are the design;
neither is hidden.

---

## 9. EULA — drafted disclosure clause (for the owner's lawyer review)

**Why this clause is load-bearing:** immediate, no-warning demo
conversion (§7.5) is defensible as a license term only because it is
disclosed to every licensee in advance. **The enforcement design
depends on this clause (or its lawyer-approved successor) shipping
with the license.** The clause replaces the current §7 sentence
quoted in §3.1(5), which contradicts the requirement. Drafted in the
template's style; bracketed items follow the template's existing
convention. The Arabic text is a summary partner, per the template's
own structure (the English text governs).

### Draft — new §7A (insert after §7; §7's final sentence is deleted)

> ### 7A. Compliance reporting, backup upload and license enforcement
>
> (a) *Reporting.* As a condition of this license, the Software, when
> the Licensee's machine is connected to the internet, transmits to
> the Licensor's server, on launch or once daily: (i) information
> about the Software installation (version, instance and device
> identifiers, license key or dongle serial, license status, term and
> licensed limits); (ii) information about the licensed company and
> its users as recorded in the Software (company identifiers, legal
> names, tax and commercial registration numbers, contact details;
> user names, user identifiers, roles and active status); and (iii) a
> device fingerprint consisting of one-way hashes of hardware and
> operating-system components. Raw hardware serial numbers and user
> passwords or password hashes are never transmitted.
>
> (b) *Purpose.* Transmitted information is used solely to compare
> the installation against the Licensor's licensing records, to
> verify license compliance, and to provide the backup custody in
> clause (c).
>
> (c) *Backup upload.* Once daily, when connected, the Software
> uploads one encrypted backup copy of the licensed company's data
> (produced by the Software's own backup facility) to the Licensor's
> server. The Licensor retains at most three (3) such copies per
> company and deletes older copies. Backup copies are stored
> encrypted; [the encryption key is held by the Licensee / the
> Licensor — per decision (C)] and the Licensor does not access the
> content of backup copies except as required to provide restore
> assistance at the Licensee's request.
>
> (d) *Enforcement.* If the comparison under clause (b) confirms that
> an installation does not correspond to the Licensor's licensing
> records, or the license is revoked for breach, the Licensor may
> cause that copy of the Software to convert, immediately upon the
> check that confirms the mismatch and without prior notice, to a
> view-only demonstration state in which data can be viewed, printed
> and exported but not created, altered, posted or deleted.
> Conversion does not delete, alter or withhold the Licensee's data,
> which remains the Licensee's property and remains fully exportable
> in that state. A converted copy is restored to full function upon
> the Licensor's clearance or re-registration of the installation.
>
> (e) *Offline use.* The Software does not require an internet
> connection to operate. Reporting and upload occur only when a
> connection is available, and the absence of a connection does not
> by itself affect the Licensee's use of the Software within the
> license term.

### Arabic summary addition (for the template's Arabic summary section)

> كما تشترط الاتفاقية أن يرسل البرنامج عند توفر الاتصال بالإنترنت،
> عند التشغيل أو مرة يومياً، معلومات عامة عن البرنامج والجهاز
> (النسخة ومعرّفات التثبيت والترخيص وبصمة جهاز مكوّنة من تجزئات
> أحادية الاتجاه لمكوّنات العتاد، دون الأرقام التسلسلية الخام ودون
> أي كلمات مرور أو تجزئاتها) ومعلومات الشركة المرخّصة ومستخدميها،
> وذلك لمقارنتها بسجلات الترخيص لدى المرخِّص والتحقق من الالتزام؛
> وأن يرفع نسخة احتياطية مشفّرة واحدة يومياً من بيانات الشركة
> يحتفظ المرخِّص بثلاث نسخ منها كحد أقصى. وإذا أكّدت المقارنة عدم
> تطابق التثبيت مع سجلات الترخيص جاز للمرخِّص تحويل تلك النسخة
> فوراً ودون إشعار مسبق إلى وضع العرض التجريبي (اطّلاع وطباعة
> وتصدير فقط، دون إنشاء أو تعديل أو ترحيل)، دون أن يمسّ ذلك بيانات
> المرخَّص له التي تبقى ملكاً له وقابلة للتصدير كاملة، وتعود النسخة
> إلى كامل وظائفها عند تسوية الوضع أو إعادة التسجيل. ولا يتطلب
> البرنامج اتصالاً بالإنترنت للعمل.

---

## 10. Open decisions for the owner

**(A) Consent model — DECIDED (2026-11-24 20:27, refined 20:29–20:32).**
License-condition disclosure in the EULA; no customer opt-in gate; no
customer-side off switch; transparency (status surface + local audit)
is mandatory; vendor-side per-license EXEMPT retained; enforcement is
immediate demo conversion on a confirmed mismatch, under the §1
two-layer principle. The §1 honest-limit trade-off (permanently
offline copies are layer-2-unreachable) is **presented for the
owner's confirmation with this study**.

**(B) Server side — OPEN.** No vendor server, endpoint, or receiving
component exists in any project record, and the shipped configuration
carries an empty server address. Choices:
  1. *Build a receiving server component* as a later workstream
     (registry + report intake + backup store with retention-3 +
     verdict issuance + review queue). This is real scope: hosting,
     TLS, backup-of-the-backups, registry administration.
  2. *Adopt an existing vendor system* — none is recorded; if the
     vendor operates infrastructure this study doesn't know about,
     the §6.1 contract is the integration surface.
  **Recommendation:** (1), scoped as its own workstream, with a
  localhost mock implementing §6.1 built first so the client can be
  proven end-to-end before any hosting decision. The client build
  cannot be honestly called complete without something answering on
  the other end.

**(C) Backup encryption key holder — OPEN.** The STEI package is
already encrypted with a per-installation key the vendor does not
hold. Choices:
  1. *Customer-installation key (status quo).* The vendor stores
     ciphertext it cannot read. Privacy and vendor liability are
     minimal; the consequence is symmetric: the vendor cannot restore
     a backup *for* a customer who has lost their installation — the
     server copy protects against disk loss at the site only while
     the site's key survives, and a total site loss (machine + key)
     makes the server copies unrecoverable. Stated plainly because it
     decides how much "backup custody" is actually worth.
  2. *Vendor-held key (re-wrap on upload).* The vendor can restore on
     the customer's behalf — and can read every customer's complete
     books. That is a large trust, security and legal-liability
     surface for the vendor, and it must be staffed and secured like
     one.
  3. *Customer-supplied passphrase per company* (entered at setup,
     key derived from it). Recoverable by the customer anywhere;
     forgotten passphrase = unrecoverable copies; support burden
     lands on password-reset theatre that cannot actually reset
     anything.
  **Recommendation:** (1) for launch, with the limitation disclosed in
  the EULA clause (§9(c) bracket), and (3) as a later opt-in upgrade
  for customers who want vendor-independent recovery. (2) is not
  recommended at this scale.

---

## 11. What the build phase needs

1. **Licensing build first (dependency):** layer 1 (§1) is the
   dongle/licensing design's SignedLicenseFileProvider +
   `AppHost.Entitlements`; its open decisions (D1/D2/D5) gate the
   license-state format this study binds to. Telemetry does not
   design a parallel license.
2. **Govern the dormant sync path** (§3.1(1)(2)): retire/rebuild
   `SyncServ`'s file-dump snapshots and unauthenticated POST; cap the
   local queue. This lands in the same change as any upload
   activation — not before it needs to, never after.
3. **Fingerprint collector** (§4.4) shared by layer 1 and layer 2,
   with the placeholder/drift rules unit-tested against recorded
   component sets (VM templates, OS reinstall, drive/NIC swaps).
4. **Check-in client** on the `OnlCoord` substrate: post-login
   trigger, daily markers, §6 endpoints, signed-request auth (§6.2),
   chunked resumable upload replacing the `TryUploadRotatedBackupsAsync`
   stub, verdict fetch/verify/cache (§7.2), company code from the
   opened company (killing the `"001"` literals, §3.1(3)).
5. **Demo gate:** session-level demo state derived at login;
   service-layer write refusal; banner + status surface (§8);
   "check now" action.
6. **Server contract proof:** the §10(B) mock server + a harness in
   the established style exercising report → verdict → conversion →
   clearance end-to-end, plus retention-3 against the mock's store.
   Kern.Tests + live-server verification remain Windows/owner-side
   steps, as with every phase.
7. **Error catalog:** ONL-85xx family ×4 languages (§8); no new DB
   migration is expected client-side for v1 (markers and caches are
   local-store state) — if the build finds it needs one, it takes the
   next free number on this branch and flags the renumber-at-merge,
   per branch precedent.
8. **EULA adoption:** §9's clause (lawyer-approved) replaces §7's
   contradictory sentence in the template and in issued License
   Certificates. Enforcement (§7) does not ship before this lands.

**Skeleton ruling:** no protocol client skeleton is included in this
phase. The request-auth identity depends on the licensing build (item
1) and decisions (B)/(C) are open; a skeleton written now would either
duplicate the dormant `SyncServ` surface or hard-code assumptions the
owner hasn't made. Study only, as briefed.
