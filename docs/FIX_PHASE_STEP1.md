<!-- source: docs/FIX_PHASE_STEP1.md -->

# Fix Phase — Step 1: RBAC (permissions actually load, resolve through roles, and gate destruction)

**Branch:** `fix/foundations-1-rbac` (from main @ `0564872`) · **Date:** 2026-11-17
**Evidence base:** `~/workspace/research/sweep/shell-admin-security.md` §2 (P-1…P-7) and
`~/workspace/research/acceptance-sweep-report.md` worst finding #1.
**Scope:** the four assigned defects only. No migration was needed — the schema
(`users`, `roles`, `permissions`, `user_roles`, `role_permissions`, `user_permissions`)
already supports the correct model; the defects were a missing call, a wrong query,
a throwing constructor, and missing gates.

---

## 1. Findings fixed

| # | Sweep finding | Fix |
|---|---|---|
| P-1 (BLOCKER) | `PermServ.LoadForUserAsync` had **zero callers**; `AppHost.Permissions.Has(...)` was false for every user all session, so 35 windows deny-closed on open and 5 more blocked their actions — including for the company administrator. | Wired into the session lifecycle in `AuthServ` (§3 below). Every successful sign-in path (database user, local company administrator, passcode) now loads the user's effective permissions before the shell opens. |
| P-4 (BLOCKER, latent) | The loader **CROSS JOINed** the permission catalog and never read `role_permissions` — wiring it naively would have granted every user every permission except explicit per-user denies. | Loader rewritten (§2): effective set resolves strictly through `user_roles → role_permissions`, plus per-user overrides. Proven against the old query in the harness (§6). |
| P-3 (BLOCKER) | `PrmWin`'s constructor threw `UnauthorizedAccessException` for every user (always true under P-1), surfacing as the global dispatcher dialog. | Constructor no longer throws. The gate moved to `OnLoaded` with the standard deny-close pattern used by the other guarded windows: report `AUTH-2005` via `AppHost.Errors`, show the localized denial (`Permissions.PermissionDenied`, all 4 catalogs), close. Load/save failures now also report through `AppHost.Errors` (`UI-7003`/`UI-7001`) instead of ad-hoc warning logs. |
| P-5 (MAJOR) | `PrmWin` edits keyed by the wrong ID: RibbonNav passes the **username**, `PermAdminSvc` matched it against `user_roles.user_id` / `user_permissions.user_id` (SQLite: all-denied baseline, orphaned override rows on save; PostgreSQL: threw "UserId must be a GUID"). Save was also double-gated by the never-loaded `Has(...)`. | `PermAdminSvc` load **and** save now resolve the supplied identifier (username **or** user_id) to the canonical `user_id` first, on both providers (`ResolveSqliteUserIdAsync` / `ResolvePostgresUserIdAsync`); unknown users raise `ArgumentException` instead of silently loading an all-denied baseline. The double gate now works because P-1 is fixed. Also fixed: `PrmWinVM` language fallback defaulted to Arabic (`?? "ar"`) — now `"en"`, per the English-default rule. |
| P-6 (MAJOR) | Destructive surfaces had **no RBAC**: BkpWin backup/restore (and the live `BkpServ` was constructed without its permission service, so its own check was dead), ComWin permanent company delete, BinWin purge, RestoreCoWin restore. | All gated at the window **and**, for backup, in the service (§4). The live `BkpServ` instance now receives the session `IPermServ` from `AppHost`. |
| P-7 (MINOR) | Logout/company-switch never cleared permissions — a cross-company privilege leak the moment P-1/P-4 were fixed. | `AuthServ.LogoutAsync` now calls `AppHost.Permissions.Clear()`. ComWin's switch path routes through `LogoutAsync`, so a company switch drops the old company's grants before the new sign-in loads its own. |

**Login-flow note (material behavior change, no window developed):** `LogWin` itself is
unchanged visually. Behaviorally, a successful sign-in now performs the permission load
(3 small parameterized queries against the company database) before `LoginSucceeded`
fires, and sign-out/company-switch clears the set. A permission-load failure never
fails the sign-in: it is reported as `AUTH-2006`; non-administrators continue with an
empty set (fail closed), administrators fall back to the full catalog (see §3) so a
loader/seed failure can never lock the administrator out of the tool needed to fix it.

---

## 2. The loader: before / after

**Before (both providers, `Permissions.cs` @ 0564872):**

```sql
SELECT r.role_code, p.permission_code, up.effect
FROM users u
LEFT JOIN user_roles ur ON ur.user_id = u.user_id
LEFT JOIN roles r ON r.role_id = ur.role_id AND r.is_active AND NOT r.is_deleted
CROSS JOIN permissions p                                   -- every permission, for everyone
LEFT JOIN role_permissions rp ON rp.role_id = r.role_id
    AND rp.permission_id = p.permission_id                 -- joined, never read
LEFT JOIN user_permissions up ON up.user_id = u.user_id
    AND up.permission_id = p.permission_id
WHERE u.is_active AND NOT u.is_deleted AND p.is_active
  AND (u.username = @id OR u.user_id = @id)
-- add rule in C#: add unless user_effect is an explicit deny
```

**After (identical logic on SQLite and PostgreSQL; three parameterized steps):**

1. **Resolve + existence** — `SELECT user_id FROM users WHERE is_active AND NOT is_deleted
   AND (username = @id OR user_id = @id)` (PostgreSQL casts `@id` to `uuid` only when it
   matches the GUID shape, as before). No row → `UnauthorizedAccessException`, exactly
   the previous contract. Existence is checked separately so a real user whose roles
   grant *nothing* loads successfully with an empty set (the old row-count check would
   have mistaken an empty grant set for a missing user once the CROSS JOIN was removed).
2. **Roles** — `SELECT r.role_code FROM user_roles ur JOIN roles r … WHERE ur.user_id = @uid`
   (active, non-deleted roles only); records whether the user holds `ROLE_ADMINISTRATOR`.
3. **Effective permissions** — a permission is in the set iff it is active **and**
   (the user is an administrator **or** some active role grants it via `role_permissions`
   **or** a per-user `user_permissions` row allows it) **and** no per-user row denies it:

```sql
SELECT p.permission_code
FROM permissions p
WHERE p.is_active
  AND ( @is_admin
        OR EXISTS (SELECT 1 FROM role_permissions rp
                   JOIN user_roles ur ON ur.role_id = rp.role_id
                   JOIN roles r ON r.role_id = ur.role_id AND r.is_active AND NOT r.is_deleted
                   WHERE ur.user_id = @uid AND rp.permission_id = p.permission_id)
        OR EXISTS (SELECT 1 FROM user_permissions up
                   WHERE up.user_id = @uid AND up.permission_id = p.permission_id AND up.effect) )
  AND NOT EXISTS (SELECT 1 FROM user_permissions up
                  WHERE up.user_id = @uid AND up.permission_id = p.permission_id AND NOT up.effect)
```

Semantics: `effective = (⋃ role grants ∪ user allows) − user denies`. The administrator
rule matches the seeded meaning of `ROLE_ADMINISTRATOR` (company creation and the seed in
`Data.cs` both grant it every permission via `role_permissions`); making it explicit in
the loader additionally covers catalog permissions added after a company's
`role_permissions` rows were seeded. Per-user denies still win over the administrator
rule, consistent with `PermAdminSvc`'s baseline/override model.

---

## 3. Session-lifecycle wiring point

`AuthServ` (`Services/Auth.cs`) is the single choke point every sign-in path funnels
through — all four success returns in `LoginAsync` (local administrator, database user,
and both passcode variants) went through the private `Succeed(...)` helper.

- `Succeed` → `SucceedAsync`: `SyncSession(user)` (unchanged), then
  `LoadPermissionsForSessionAsync(user)`, which calls
  `AppHost.Permissions.LoadForUserAsync(user.Username)` and snapshots the result onto
  `UserInfo.Permissions`.
- `LoadPermissionsForSessionAsync` is `internal static` so Kern.Tests can pin the
  failure policy: on exception → `AppHost.Errors.Report(AUTH-2006, …)`; administrator →
  `IPermServ.GrantAllCatalogPermissions(username)` (new interface method: loads
  `PermCat.All` in-memory, no database); anyone else → `Permissions.Clear()` (fail closed).
  The local company administrator is covered twice over: company creation mirrors the
  admin into the company database with `ROLE_ADMINISTRATOR` (Company.cs), so the normal
  loader path resolves it; the catalog fallback exists for a database whose mirrored
  row is missing (e.g. a restored backup from another build).
- `SetCurrentUser` also kicks off the same load (it previously only synced the session).
- `LogoutAsync` clears the set (P-7). Company switch (ComWin) calls `LogoutAsync` before
  rebinding the database context, so grants can never cross companies.

---

## 4. Destructive-surface gating list

Every denial path is identical in shape: check **before** any confirmation/isolation/
destructive work → `AppHost.Errors.Report(AUTH-2005, null, "<File>.<Handler>")` →
localized `MessageBox` warning (new `PermissionDenied` key in the window's existing
Lang section, all 4 catalogs) → return. No silent failures.

| Surface | Action | Required permission | Where enforced | Who passes (seeded baselines) |
|---|---|---|---|---|
| BkpWin | Create backup | `BACKUP.CREATE` (`PermCat.Backup.Create`) | `BkpWin.Create_Click` + `BkpServ.CreateBackupCoreAsync` | Administrator, User (Viewer lacks it: operation ≠ VIEW) |
| BkpWin | Restore backup | `BACKUP.RESTORE` (`PermCat.Backup.Restore`) | `BkpWin.Restore_Click` + `BkpServ.RestoreBackupAsync` | Administrator, User |
| ComWin (Management) | Delete company **permanently** | `ORG.COMPANY.DELETE` (`PermCat.Company.Delete`) | `ComWin.BtnDeleteFully_Click`, before the confirm dialog | Administrator only (protected list excludes it from ROLE_USER) |
| BinWin | Purge (permanent delete of soft-deleted master data) | `SYS.ADMIN` (`PermCat.System.Admin`) | `BinWin.Purge_Click`, before selection/confirm | Administrator only. Restore (non-destructive) deliberately ungated. See flag F-2. |
| RestoreCoWin | Restore a soft-deleted company | `ORG.COMPANY.CREATE` (`PermCat.Company.Create`) | `RestoreCoWin.BtnRestore_Click` | Administrator only (protected list). See flag F-3. |

Service-layer change behind the first two rows: `BkpServ`'s checks previously tested the
legacy `PermKey.SysBackup` (`"SYS.BACKUP"` — a code that exists in **no** catalog and is
never seeded, so the check could never have passed) and were dead anyway because
`AppHost` used the constructor that leaves `_perm = null`. Now: `AppHost` passes the
session `IPermServ` into `BkpServ`, and the service tests `BACKUP.CREATE` /
`BACKUP.RESTORE`. The window gates give the user a proper denial message; the service
checks are the defense-in-depth for any other caller.

Not gated in this step (out of the assigned defect list, recorded for the owner):
ComWin *Remove from list* (index-only, reversible), ComWin Management edit/save
(`ORG.COMPANY.UPDATE` exists; gating the whole Management mode is a product decision —
flag F-4), DatWin import (`DATA.IMPORT` is referenced by no code — that is the
data-exchange step's D-3 finding, not this step's).

---

## 5. PrmWin comparison table (comparison gate)

PrmWin is the window changed by this step. Baseline: `compare-shell-admin.md` §5
(grade 38% — deep engine, weakest administration surface), extended with this step's
changes. ShouTech column reflects the state **after** this step.

| Capability | ShouTech PrmWin (after Step 1) | Al-Ameen V10 / Phenix v10 | ERPNext | Odoo |
|---|---|---|---|---|
| Opens at all / gate | ✅ Opens for holders of `SYS.PERMISSIONS.MANAGE`; deny-close with localized message + `AUTH-2005` report for everyone else (was: ctor throw for 100% of users) | ✅ group-based tree opens for admins | ✅ Role Permission Manager for System Managers | ✅ Groups on user form |
| Whose permissions are edited | ⚠️ The signed-in user, but now **correctly**: the username from the ribbon call site is resolved to the database `user_id`, so the role baseline and saved overrides are the real ones (was: all-denied baseline + orphaned saves; PG threw). Still no user picker — flag F-1 | User groups: pick the group, edit its tree | Any user/role selectable | Any user/group selectable |
| Permission model edited | Per-user **exceptions** over the role baseline (baseline shown via `BaselineAllowed`, effective via checkbox); role rows themselves stay intact — unchanged design, now reachable | Per-group window tree (module → window → open/add/edit/delete/print) | Per-DocType × role grid (Read/Write/Create/Delete/Submit/…) | Group × model access + record rules |
| Catalog depth | 274 codes in ~20 groups, tabbed by group, sensitive flags — unchanged, now loadable | Window-level tree | DocType-level | Model-level |
| Save integrity | Transactional baseline-vs-override diffing (unchanged); administrator-role accounts protected from editing (`EnsureNotAdministrator*`, unchanged); save keyed by resolved `user_id` (fixed) | ✅ | ✅ version log | ✅ chatter |
| Custom roles / groups CRUD | ❌ still absent (`SYS.ROLES.*` constants remain dead) — not this step's defect list; flag F-1 | ✅ signature pattern | ✅ unlimited roles | ✅ unlimited groups |
| Permission templates | ❌ ribbon key still opens the same PrmWin — advertised-but-absent; flag F-1 | ✅ per job type | Role profiles | Group duplication |
| User management (create/disable/reset) | ❌ no window exists — the sweep's worst admin gap (§5.3 #1); **not** in this step's assigned defects; flag F-1 | ✅ user cards | ✅ User DocType | ✅ Users |
| Ribbon/menu filtering by permission | ❌ buttons still render for all roles; enforcement is at window open (now functional) | Menu is permission-shaped | Desk hides inaccessible items | Menus hidden by group |
| Record rules / amount limits | ❌ absent (engine has no concept) | Branch/warehouse restriction per user | User Permissions + query conditions | Record rules |

**Verdict:** this step moves PrmWin from *NOT USABLE (0% reachable)* to a *working
single-user exception editor on a correct RBAC foundation*. The remaining gaps
(user picker, user management, role CRUD, templates) are feature builds, not defects
in the loader/gates, and are flagged for the owner rather than silently expanded
into this step.

---

## 6. Proof

### 6.1 Headless harness (executed on this Linux VM, 9/9 PASS)

Harness kept outside the product tree at `~/workspace/research/fix-step1/rbac_harness.py`: it extracts
the **verbatim SQLite SQL** from `Services/Permissions.cs` — the new loader from this
branch and the old loader from `git show 0564872:…` — and executes both against a
seeded SQLite database with the same fixtures as the Kern.Tests facts:

```
PASS - OLD loader grants limited user ALL 6 permissions (the P-4 defect)
PASS - NEW limited == exactly {ACC.JOURNAL.VIEW, VCH.VOUCHER.VIEW}
PASS - NEW limited by user_id 'U-LIMITED' resolves identically
PASS - NEW admin has all 6 seeded permissions
PASS - NEW override: allow adds BACKUP.RESTORE, deny removes ACC.JOURNAL.VIEW
PASS - NEW user with grant-less role loads with EMPTY set (no throw)
PASS - NEW unknown user -> not found (UnauthorizedAccessException path)
PASS - PermAdmin OLD baseline by username finds 0 grants (P-5 defect)
PASS - PermAdmin NEW baseline by username finds the role's 2 grants
```

This covers task proofs (a) and (b) at the SQL level, including the case that fails
under the old CROSS JOIN, and the P-5 baseline-resolution contrast.

### 6.2 Kern.Tests facts (written; execution owed on Windows)

`tests/Kern.Tests/RbacPermissionTests.cs` — 13 facts running the **real** services
against real migrated SQLite databases (`SchemaMigrator.ApplyAllAsync` + the
`PointAppAt` pattern from `SteiRoundTripTests`):

- (a) `LimitedRole_GetsExactlyItsRolePermissions`, `…LoadsByUserIdToo`,
  `UserOverrides_AllowAdds_AndDenyWinsOverRoleGrant`,
  `UserWhoseRoleGrantsNothing_LoadsWithEmptySet`, `UnknownUser_ThrowsUnauthorized`
- (b) `Administrator_GetsAllPermissions`
- (c) at the service level: `SessionWiring_LoadsPermissions_AndLogoutClearsThem`
  (login wiring populates `AppHost.Permissions`; logout empties it) and
  `SessionWiring_AdministratorFallsBackToCatalog_WhenDbUserMissing`. The window-level
  half of (c) is code-traced: the 35 deny-close windows consult
  `AppHost.Permissions.Has(<their VIEW code>)` on load — with the set now loaded, a
  permitted user passes and an unpermitted one deny-closes, the same pattern PrmWin
  now follows itself. **WPF execution is owed on Windows.**
- (d) `BackupService_RefusesCreateAndRestore_WithoutPermissions` (limited set →
  `UnauthorizedAccessException` from both `BkpServ` entry points) and
  `BackupService_PermissionGatePasses_ForAdministrator` (gate passes; restore then
  fails on the missing file, proving the refusal was the permission, not the path).
  Window-handler gates for ComWin/BinWin/RestoreCoWin are code-traced (identical
  check-report-return shape at the top of each handler, before any destructive call).
- P-5: `PermAdmin_LoadByUsername_ResolvesRoleBaseline`,
  `PermAdmin_SaveByUsername_WritesOverridesKeyedByUserId` (asserts the override row
  is keyed `U-LIMITED` and **no** row is keyed by the username).
- `GatePermissions_AreRealCatalogCodes` — every gate added in this step references a
  code present in `PermCat.All` (a typo'd code would deny everyone forever).

### 6.3 Owed on Windows (stated plainly)

- Execution of the 13 Kern.Tests facts (testhost is `net8.0-windows`).
- Runtime pass: sign in as a limited user → guarded windows open/deny per role;
  PrmWin opens for an administrator and saves an override that takes effect at the
  target user's next sign-in; the four destructive gates show their denial messages.
- Live PostgreSQL pass of both loaders (SQL written in mirrored pairs and reviewed;
  only the SQLite variant is harness-proven here).

---

## 7. Gate results

| Gate | Result |
|---|---|
| Full solution build `--no-incremental` (`-p:EnableWindowsTargeting=true`) | **0 errors, 0 warnings** |
| `tools/audit_window_context.py` | **exit 0** (71 windows; PrmWin still Window-derived — pre-existing, see F-5) |
| Lang parity (all 4 catalogs) | **0 missing** in every catalog (5 new `PermissionDenied` keys added to En/Ar/Fr/Tr) |
| Error catalogs | 46 codes × 4 languages (added `AUTH-2005`, `AUTH-2006` per `docs/ERROR_CATALOG.md` process: `ErrorCodes.cs` + 4 JSON files + index rows) |
| `tools/verify_english_source.py` | exit 0 — no Arabic added outside `Ar.json` (Lang + Errors); the two Arabic strings touched in code (`PrmWinVM` fallback) went the other way: `"ar"` → `"en"` |
| Migration | **None added** — schema already supports the fix; migrator gate not applicable |

New error codes (catalog process followed, not reused codes — no existing code means
"permission denied" or "permission load failed"; `DATA-4001` is export-specific):
`AUTH-2005 PermissionDenied`, `AUTH-2006 PermissionLoadFailed`.

---

## 8. Flags for the owner (decisions NOT taken)

- **F-1 — Permission administration depth.** PrmWin still edits only the signed-in
  user's exceptions: no user picker, no user-management window, no custom-role CRUD,
  and *Permission Templates* still opens the same window. These are the comparison
  table's CORE gaps (§5) and the sweep's worst admin gap — they are feature builds
  needing their own step, not defects in this step's list.
- **F-2 — BinWin purge permission choice.** No recycle-bin permission exists in the
  catalog, so purge is gated by `SYS.ADMIN` (administrator-only). If the owner wants
  a dedicated `SYS.RECYCLE_BIN.PURGE` code, it needs a catalog constant **plus**
  seed-baseline treatment (it would otherwise be granted to ROLE_USER by the
  everything-except-protected seed rule) — a deliberate product decision.
- **F-3 — RestoreCoWin permission choice.** Gated by `ORG.COMPANY.CREATE`
  (administrator-only in the baselines; semantically "bring a company into the
  active set"). `BACKUP.RESTORE` was the alternative; rejected because RestoreCoWin
  restores soft-deleted companies, not backup packages.
- **F-4 — ComWin Management mode as a whole** remains ungated (any signed-in user
  can open it and use Edit / Remove-from-list / Reindex). Only the irreversible
  permanent delete was in this step's defect list and is now gated.
- **F-5 — PrmWin still derives from `Window`**, not `BaseWin` (no clamp, no geometry
  persistence, no live re-localization, `MinHeight 620` in the §13 screen-fit
  catalogue). Converting it is a window-rebuild task under the comparison gate,
  beyond this step's repair scope.
- **F-6 — Administrator fallback breadth.** `GrantAllCatalogPermissions` grants the
  in-code catalog (`PermCat.All`), which can include codes not present in an older
  company's `permissions` table. It fires only when the database load already failed
  for a session authenticated as administrator; the alternative (empty set) recreates
  the original lockout for exactly the person who must fix it.
- **F-7 — Ribbon buttons are still not permission-filtered** (sweep S-9): users see
  buttons for windows that will deny-close on open. Enforcement is now real at open
  time; shell-level hiding is a separate shell step.

---

## 9. Per-window completeness checklist (owner standard, 2026-11-17 21:59)

Every window has six pillars: **UI / DB / linking / posting / persistence /
downstream**. Each is marked **PROVEN** (with the evidence this step actually
produced) or **GAP** (explicitly listed, not hidden). Per the owner's
instruction, gaps outside this step's RBAC scope are **listed, not closed**.
"Code-traced" means verified by reading the shipped code paths on this
branch; all runtime behavior still owes the Windows pass (§6.3).

### 9.1 PrmWin (permissions administration — the window this step repairs)

| Pillar | Verdict | Evidence / gap |
|---|---|---|
| UI | PROVEN (code-traced) | Gate moved from ctor-throw to Loaded deny-close; tabbed group/checkbox editor and bottom Save/Cancel bar unchanged and now reachable for `SYS.PERMISSIONS.MANAGE` holders. |
| DB | PROVEN | No schema change needed: reads `permission_groups`/`permissions`/`user_roles`/`role_permissions`, writes `user_permissions` — all pre-existing tables; loader + admin SQL harness-proven (§6.1) and fact-covered (§6.2). |
| Linking | PROVEN (code-traced) | RibbonNav `OtherTools.Permissions` reaches it with the session user; shell context arrives via the App-level Loaded handler. |
| Posting | PROVEN (n/a by design) | No GL/stock/ledger effects exist for this window; its real-world effect is the permission set itself, consumed by the loader at sign-in — that consumption path is exactly what §6 proves. |
| Persistence | PROVEN | `PermAdminSvc.SaveForUserAsync` writes overrides transactionally (baseline-vs-override diffing), now keyed by the resolved `user_id`; fact `PermAdmin_SaveByUsername_WritesOverridesKeyedByUserId` asserts the row key and that no username-keyed orphan is written. Administrator accounts remain edit-protected. |
| Downstream | **GAP** | (1) No permission-change audit row is written by `PermAdminSvc` and there is no permission-change history view (sweep §5.2). (2) Saved changes take effect at the target user's **next sign-in** — there is no live-session refresh. (3) No user picker / user management / role CRUD / templates (F-1). (4) Still `Window`-derived, not BaseWin (F-5). None of these are RBAC-loader defects; all are listed for the owner, unclosed. |

### 9.2 BkpWin (backup / restore — gated in this step)

| Pillar | Verdict | Evidence / gap |
|---|---|---|
| UI | PROVEN (code-traced) | Create/restore/list flows unchanged; the new gates fire before any confirmation or shell isolation, with a localized denial. |
| DB | PROVEN | Backup/restore run through the STEI engine (`SteiSvc`) against the company database; round-trip integrity is covered by the pre-existing `SteiRoundTripTests`. The service-layer permission refusal is fact-covered (§6.2). |
| Linking | PROVEN (code-traced) | File-tab Backup/Restore named buttons reach it; restore hands off to a fresh LogWin, whose sign-in now loads permissions (§3). |
| Posting | PROVEN (n/a by design) | No GL/stock posting; restore's effect is wholesale company-database replacement via the STEI import, which is the engine proven above. |
| Persistence | PROVEN (code-traced) | Backups are encrypted `.stb` files on disk; the window rebuilds its index from the folder on every open. |
| Downstream | **GAP** | (1) No backup-history / last-run / next-run verification surface (comparison §8 CORE gap) — pre-existing, unclosed. (2) Tools-tab Backup performs a rotated backup with no window (sweep S-4) — pre-existing. (3) `Window`-derived: no BaseWin clamp/geometry/tint — pre-existing. |

### 9.3 ComWin (company management — permanent delete gated in this step)

| Pillar | Verdict | Evidence / gap |
|---|---|---|
| UI | PROVEN (code-traced) | Management mode (Open/Edit/Delete/Reindex) unchanged; permanent delete now refuses unauthorized users before its confirm dialog. |
| DB | PROVEN (code-traced, sweep) | Profile save writes the local index **and** the DB profile, honestly reporting partial failure (sweep §4). Company open/switch reconfigures the runtime database via `ConfigureRuntimeDatabase` + `AppHost.RebindDatabaseContext`. |
| Linking | PROVEN | Switch flow closes secondary windows, logs out (which now also clears permissions, P-7), rebinds services, and forces a fresh sign-in that loads the new company's permissions. |
| Posting | PROVEN (n/a by design) | No GL/stock posting originates in this window. |
| Persistence | **GAP** | Sweep C-1 (MAJOR, pre-existing, **not** closed by this step): the edit form loads **administrator username, administrator full name, and database provider** but Save silently discards all three while reporting success. |
| Downstream | **GAP** | (1) Sweep C-3: company `CurrencyCode`/`LanguageCode` are saved but never consumed by the shell (shell reads `UsrPref`) — decorative fields, pre-existing, unclosed. (2) Management mode as a whole remains ungated (F-4) — only the irreversible delete was in this step's scope. |

### 9.4 BinWin (recycle bin — purge gated in this step)

| Pillar | Verdict | Evidence / gap |
|---|---|---|
| UI | PROVEN (code-traced) | List/restore/purge flows unchanged; purge now refuses non-administrators before selection/confirm. Restore deliberately stays ungated (non-destructive direction). |
| DB | PROVEN (code-traced, sweep) | `RecycleBinSvc` list/restore/purge operate on real soft-deleted rows per company; shell status bar shows live counts. |
| Linking | PROVEN (code-traced) | Reached from the quick-access bar, status bar, and corner buttons in MWin. |
| Posting | PROVEN (n/a by design) | No GL/stock posting; purge/restore act directly on the master-data rows themselves. |
| Persistence | PROVEN (code-traced) | Restore returns rows to the working sets; purge deletes them permanently — both are direct database effects of the service calls, no UI-only state. |
| Downstream | **GAP** | (1) No dedicated recycle-bin permission exists in the catalog; purge is gated by `SYS.ADMIN` as a documented interim choice (F-2). (2) Root is `<Window>`, not BaseWin, so the central 50% screen-fit cap can never apply (§13 redesign item) — pre-existing, unclosed. |

### 9.5 RestoreCoWin (restore soft-deleted company — gated in this step)

| Pillar | Verdict | Evidence / gap |
|---|---|---|
| UI | PROVEN (code-traced) | Deleted-company list + Restore/Refresh/Close unchanged; restore now refuses unauthorized users first. |
| DB | PROVEN (code-traced, sweep) | `CoRegSvc.GetDeletedCompaniesAsync` / `RestoreCompanyAsync` restore the company into the database **and** the local index. |
| Linking | **GAP** | Sweep S-1 (MAJOR, pre-existing, **not** closed), re-verified on this branch: both ribbon buttons emit key `RestoreCompany.Restore` (MWin.xaml:214 named → routed by reference to **BkpWin**; MWin.xaml:544 unnamed → falls through every router to the placeholder workspace). RibbonNav's `OtherTools.RestoreCompany.Restore` case (RibbonNav.cs:163) would open RestoreCoWin, but **no button emits that key** — so RestoreCoWin currently has no working ribbon entry point at all. The gate added in this step is correct for whenever the window is reached; fixing the key mismatch is a shell-routing fix outside RBAC scope. |
| Posting | PROVEN (n/a by design) | No GL/stock posting originates in this window. |
| Persistence | PROVEN (code-traced) | A successful restore is a database + index effect, verified by the list reload that follows it. |
| Downstream | **GAP** | (1) English-default residue: `ReloadAsync`/`DisplayName` still fall back to `?? "ar"` for the current language (sweep §4) — the same defect class this step fixed in PrmWinVM, left unclosed here as out of RBAC scope. (2) An informational MessageBox pops on **every** reload when the list is empty, including the automatic reload after a successful restore — pre-existing. (3) `Window`-derived (no clamp/geometry/live re-localization) — pre-existing. |

### 9.6 Note on LogWin / Auth (behavior changed, window not developed)

LogWin's UI was not touched and needs no checklist expansion: its one
behavioral change is that a successful sign-in now loads the permission set
before `LoginSucceeded` fires, and sign-out/company-switch clears it (§3).
Its pre-existing pillar status is unchanged from the sweep (auth core
"usable", grade 70%).

---

<!-- source: docs/FIX_PHASE_STEP2.md -->

# FIX PHASE — STEP 2: Unify the Chart of Accounts

**Branch:** `fix/foundations-2-coa` (worktree `~/workspace/projects/ShouTech-fix2`)
**Base:** tip of `fix/foundations-1-rbac` (`fb56fbc`) — the foundations chain is sequential.
**Date:** 2026-11-17 · **Status:** implemented, gated, NOT pushed, NOT merged.
**Evidence base:** `~/workspace/research/sweep/journal-coa.md`,
`~/workspace/research/sweep/compare-journal-coa.md`,
`~/workspace/research/acceptance-sweep-report.md`.

---

## 1. The defect, in one paragraph

The app kept **two charts of accounts with no synchronization**.
`accounts` (SQLite mig 012 / PG mig 014) — integer `account_id`, `company_id` =
company **code** — was written by company-creation seeding, `CoaWin.SeedDefaultChart`
and `AccRepo.Save`, and read by CoaWin/AccCard and the Trial Balance; it carried
cached `balance/debit/credit` columns **nothing maintained** (C-2).
`chart_of_accounts` (SQLite mig 009 / PG mig 011) — Guid `account_id`,
`company_id` = company **Guid** — was read by `JournalRepository.ResolveAccountIdAsync`,
the posting triggers, `ChequeRepository` and `MasterCardRepository`, while its only
writer (`AccountingStructureRepository.CreateAccountAsync`) had **no UI caller**.
On a fresh company the chart existed only in `accounts`, so JrnWin Save threw
*"Account code '131' is missing…"* (J-1/C-1, harness-proven in the sweep): the
journal was unusable with the visible chart. GL reports read the cached counters
and/or joined integer↔Guid keys, producing fiction or empty results (R-1/R-2).
The account lifecycle guard compared journal lines' Guid strings against the
`accounts` integer Id, so an account with posted movements could be deleted (C-3).

## 2. Decision: the canonical store is `chart_of_accounts`

**`chart_of_accounts` (Guid-keyed) is the one canonical chart of accounts.**
The legacy `accounts` table is **dropped** by migration 059 after its data is
merged in. Rationale, weighed from the evidence:

| Factor | `accounts` | `chart_of_accounts` | Verdict |
|---|---|---|---|
| Posted history | No journal line references it | `journal_entry_lines.account_id` **is** its Guid; posting triggers (SQLite mig 009 / PG 011 `shouttech_validate_journal_post`) and fiscal close validate against it | Chart wins decisively — unifying here rewrites **zero** posted history |
| Schema discipline | Free-text `posting_mode`/`status`, cached counters, no type column (type guessed from code prefixes — R-2's root cause) | `account_type` enum (ASSET/LIABILITY/EQUITY/REVENUE/EXPENSE), boolean flags, FK to `companies`, unique (company, code) | Chart |
| UI gravity | CoaWin/AccCard fields (name_ar, risk, tax_rate, reconcile/taxable flags, budget, is_default, description, audit columns) exist only here | Lacks those fields | Tie-breaker: the fields are **carried onto the chart** as extension columns (§3) — gravity moves, history doesn't have to |
| Other readers | Trial Balance (rewritten, §5) | Cheques, master cards, bank rec, fiscal close | Chart |

The alternative (make `accounts` canonical) would have required rewriting every
posted `journal_entry_lines.account_id` from Guid to integer and re-pointing the
posting triggers — i.e. rewriting accounting history to save a UI table. Rejected.

**Why drop, not a deprecated view:** a compatibility view cannot synthesize the
legacy integer `account_id`s honestly (they never had a stable mapping to Guids),
and any surviving writable-looking surface invites a future dual-write. After 059
there is exactly **one writer path** (`AccRepo` + `AccountingStructureRepository.CreateAccountAsync`
+ company-creation seeding, all on `chart_of_accounts`) and **one reader path**
(every former `accounts` reader re-pointed, §5). No code in the tree references
the `accounts` table after this step (verified by repository-wide search; the only
remaining matches are the loyalty table `loyalty_accounts`, migration history
files, and this documentation).

## 3. Migration 059 — `059_coa_unification.sql` (SQLite + PostgreSQL)

One transactional migration per provider, numbered **059** (053 stays reserved).
Idempotent: every statement is guarded (`-- @if-column-missing` directives on
SQLite, `ADD COLUMN IF NOT EXISTS` / catalog checks on PostgreSQL, terminal
`DROP TABLE IF EXISTS`), and the migrator's checksum ledger skips it on re-run —
verified by double-run on both an empty database and a populated pre-unification
fixture (§7e).

### 3.1 Schema changes to `chart_of_accounts`

Extension columns carried over from `accounts` (the UI's field set):
`name_ar`, `risk` (default `'Low'`), `cost_center_mode` (default `'Unspecified'`),
`allow_reconcile`, `taxable`, `foreign_currency`, `cost_required`, `budget`,
`is_default`, `tax_rate`, `description`, `updated_at`, `deleted_at`, `deleted_by`.
The cached `balance`/`debit`/`credit` columns are **deliberately not carried
over** — balances are computed from posted lines everywhere (§5); nothing may
read a stored balance for reporting, and after 059 none exists to read.

### 3.2 Data mapping (explicit)

For every non-deleted `accounts` row:

- **Company resolution (code → Guid):** `accounts.company_id` holds the company
  **code**; the row joins `companies` on `company_code = accounts.company_id`,
  falling back to a direct `company_id` match. Rows whose company cannot be
  resolved are **not migrated** (they belong to no company the registry knows;
  the fixture in §7e proves the skip is silent-safe and loss-free for real rows).
- **Identity (int → Guid):** if a chart row already exists for (company, code),
  it **keeps its existing Guid** — this is what preserves every
  `journal_entry_lines` link, because journal lines only ever referenced chart
  Guids. Accounts-only rows receive a **fresh Guid** (SQLite: the mig-009
  randomblob v4 pattern; PostgreSQL: deterministic
  `md5(company || ':059:' || code)::uuid`, so a re-run converges instead of
  duplicating; `ON CONFLICT DO NOTHING` backs both passes).
- **Field precedence on duplicates:** the chart row keeps its `account_id` and
  `account_type`; the `accounts` row wins for descriptive fields and flags
  (names, risk, tax, reconcile, status) because the UI maintained them there.
- **Derived fields for accounts-only rows:** `account_name` = `name_en`
  (fallback `name_ar`); `is_postable` = `posting_mode <> 'Summary'`;
  `is_active` = `status <> 'Disabled'`; `is_deleted` = OR of both stores;
  `account_type` derived from the code root the seed scheme already uses:
  1→ASSET, 2→LIABILITY, 3→EQUITY, 5→REVENUE, everything else (4 Purchases,
  6 Expenses, custom roots) → EXPENSE. **Owner flag F-2** (§9).
- **Parents:** `parent_code` is resolved to the parent's Guid in a second pass,
  after all rows exist, so ordering never matters.
- Finally `accounts` is dropped (SQLite also drops its indexes first).

### 3.3 Companion change inside 059: fiscal periods backfill

Mandate 2 (journal must save **and post** on a fresh company) exposed a second
fresh-company gap: company creation produced a fiscal **year** but no fiscal
**periods**, and the posting engine requires an open period containing the entry
date. Migration 059 backfills monthly OPEN periods tiling each open fiscal
year's actual start..end dates (≤13 periods, respecting the
`period_number BETWEEN 1 AND 13` check) for every year that has none, and
company creation now seeds the same periods in the creation transaction
(§4). Years that already have periods are untouched.

## 4. Company creation seeds the canonical chart — one place

`Services/Company.cs`: both provider paths now insert the template chart into
`chart_of_accounts` in two passes (rows with NULL parent, then parent Guid
updates), keyed by the new company's **Guid**, with `account_type` from the
shared `AccRepo.AccountTypeForCode` derivation, and seed the fiscal periods via
the shared `BuildFiscalPeriods` helper (SQLite + PostgreSQL). `CoaWin.SeedDefaultChart`
(the empty-chart fallback) already wrote through `AccRepo`, so it lands in the
canonical store unchanged in shape. There is no remaining `INSERT INTO accounts`
anywhere in the tree.

## 5. One writer / one reader path — what moved

| Surface | Before | After |
|---|---|---|
| `AccRepo` (CoaWin/AccCard data layer, `Services/Accounting.cs`) | CRUD on `accounts` (int Id, code-keyed company) | Fully rewritten on `chart_of_accounts`. `AccRec.Id` is now the Guid string. Balances/Debit/Credit are **computed at load** from POSTED lines (status = 1) — C-2 closed at the source. Parent linkage is the Guid FK: renaming a code needs no descendant re-pointing; renumbering rewrites descendant codes + derived types transactionally. Company resolved code→Guid via `companies` per call (shell context honored at use time) |
| `Company.cs` seeding | `accounts` inserts, no periods | Canonical chart + periods, both providers (§4) |
| `RptQuery` GL family | TB read cached counters & ignored dates (R-1); BS/P&L joined `CAST(int AS TEXT)` = Guid (R-2); statement compared code to Guid ids and printed entry Guids as DocNo (R-3); CC trial same broken join (R-4); journal list printed Guids (R-5) | **All** GL queries rewritten onto posted `journal_entry_lines` joined to the chart on Guids, company-scoped via `companies` (the shell passes the code; `journal_entries.company_id` is the Guid). Trial Balance honors [from, to); BS/P&L classify by `account_type`, not code prefixes; statement filters by `account_code` and shows `document_number`; dormant accounts, cash position/flow, account comparison, cash journal, bank GL balances likewise. Window contracts (aliases) unchanged, so report windows needed no edits |
| `CardStore.BankBookBalance` | Cached-column read | Posted-lines sum via chart + companies |
| `RecycleBinSvc` | Two table defs (`accounts` + chart), chart def mis-keyed by code so deleted accounts never listed | Single chart def, Guid-resolved; deleted accounts list/restore correctly |
| `DataExportSvc` (DatWin datasets) | `accounts` dataset listed first, code-scoped | Accounts dataset resolves to `chart_of_accounts` |
| `OperationsRepository` (BankRecWin) | Tried `accounts` first (int ids), then chart | Chart only; new `ResolveChartCompanyAsync` maps the caller's company **code** to the Guid for chart/journal queries (marks stay code-keyed, as stored) |
| `JournalRepository` | — | **Untouched.** Its account resolution already read the chart; unification removes the mismatch instead of moving the journal (per mandate scope) |

## 6. Per-defect closure

| ID | Defect | Closure | Proof |
|---|---|---|---|
| J-1 / C-1 | Fresh company: JrnWin Save throws "Account code '131' is missing…" — journal unusable with the visible chart | Chart unified on the store the journal resolves against; UI writer (`AccRepo`), company seeding and journal reader now share it; fiscal-period gap fixed in 059 + creation | Harness (a)+(b); Kern.Tests `FreshCompany_UiChart_JournalSavesAndPosts` |
| C-2 | Cached `accounts.balance/debit/credit` maintained by nothing; card tiles and reports show fiction | Columns dropped with the table; `AccRec` balances and **all** GL reports compute from POSTED lines | Harness (c); Kern.Tests `AccRecBalances_AreComputedFromPostedLines_NotStored`, `Reports_ComputeFromPostedLines` |
| C-3 | Lifecycle guard compares Guid journal refs to integer Id → posted-to accounts deletable | Guard rewritten on the unified model: live posted totals **by Guid**, any-line reference count, and the generic reference scan now matches Guid-or-code text. Posted-to ⇒ cannot delete/disable | Harness (d); Kern.Tests `LifecycleGuard_BlocksPostedAccount_AllowsUnusedAccount` |
| R-1 | Trial Balance reads cached columns, ignores From/To | Recomputed from posted lines in [from, to) | Harness (c) incl. June-window zero check |
| R-2 | BS/P&L int↔Guid join → always empty | Guid joins + `account_type` classification | Harness (c) |
| R-3 | Account Statement compares code to Guid ids; Guid printed as DocNo | Filter by `account_code`; DocNo = `document_number` | Harness (c) |
| R-4 | Cost Center Trial same broken join | Rewritten on chart Guids + companies scoping | Code + build; runtime proof owed on Windows (report-family pass) |
| R-5 | Journal List shows Guids as DocNo | DocNo = `document_number` (fallback entry id) | Code + build; runtime proof owed on Windows |

Out of scope and untouched, as mandated: J-2 (invoice GL posting, step 3),
J-3 (cost-center validation, step 3), J-5 (base-currency lines, step 7),
J-6/J-8/J-9 (journal workflow).

## 7. Proof

### 7.1 Headless harness — `~/workspace/research/fix-step2/coa_harness.py` — **28/28 PASS**

Runs the **real SqlMigrator** (fix1 build for the pre-059 fixture, fix2 build for
the upgrade) and replays the app's own SQL shapes; GL reports execute with SQL
extracted **verbatim** from the new `RptQuery.cs`, and the lifecycle guard with
SQL extracted verbatim from the new `AccRepo`.

- **(a)** Company created via the app's seeding shapes → 11-account chart present
  in `chart_of_accounts`; no `accounts` table exists.
- **(b)** Codes 131/51/61 resolve through `JournalRepository`'s resolution SQL;
  draft inserts and **posts** (real posting triggers validate period/balance).
- **(c)** Trial Balance Cash = Dr 1000 / Cr 250 / Bal 750 = hand-computed; TB
  totals balance (1250 = 1250); June-only window all zeros (dates honored);
  Balance Sheet Cash 750; P&L Sales 1000 / Rent 250; Account Statement for code
  131 returns both lines with DocNos `JV-H-1`/`JV-H-2`; cash-flow sections
  classify the 750 net movement.
- **(d)** Guard SQL sees posted totals 1000/250 on account 131 → refused;
  unused account 132 has zero totals and zero references → allowed.
- **(e)** Pre-unification fixture (company + rows in **both** tables + a posted
  journal): 059 applies; second migrator run is a clean no-op; `accounts` gone;
  canonical codes = union {13, 131, 132, 51, 61} — nothing lost; the posted
  line still points at the **original Cash Guid**; duplicate 131 kept the chart
  identity and gained the UI fields (tax_rate 5.0, risk Low); accounts-only rows
  typed by code root (132→ASSET, 61→EXPENSE); Disabled→inactive; parent 131→13
  linked; orphan row (unresolvable company) skipped; no `balance` column exists.

### 7.2 Kern.Tests — `tests/Kern.Tests/CoaUnificationTests.cs` (6 facts)

`FreshCompany_UiChart_JournalSavesAndPosts` (real `JournalService` +
`JournalRepository` + `AccRepo`-seeded chart + real fiscal periods) ·
`Reports_ComputeFromPostedLines` (real `RptQuery` TB/BS/P&L/statement vs the same
hand-computed values, incl. the date-range check) ·
`AccRecBalances_AreComputedFromPostedLines_NotStored` ·
`LifecycleGuard_BlocksPostedAccount_AllowsUnusedAccount` (incl. `AccRepo.Delete`
throwing on the posted account and succeeding on the unused one) ·
`AccRepo_RoundTrip_PreservesTypeParentAndFlags` (incl. guarded renumber keeping
children attached) · `Migration_LeavesOneChartStore_AndDoubleRunIsIdempotent`.
**Execution is owed on Windows** (net8.0-windows testhost cannot run on this
Linux VM) — the project compiles at 0 errors / 0 warnings here.
`SteiRoundTripTests` was updated to assert fidelity (Arabic text, decimal, NULL)
on the canonical chart row instead of the dropped table.

### 7.3 What is NOT proven here

WPF runtime behavior (windows opening, clicking through JrnWin on a fresh
company) and **live PostgreSQL** execution of migration 059 and the rewritten
queries are owed on Windows / a live PG instance — no PG server exists in this
VM. The PG migration uses only standard idempotent constructs and mirrors the
SQLite logic statement-for-statement, but it has not been executed. This is
stated plainly per the standing rule: build + SQLite harness success is not
product readiness.

## 8. Gates

| Gate | Result |
|---|---|
| Full solution build `--no-incremental` | **0 errors, 0 warnings** |
| `tools/audit_window_context.py` | **exit 0** |
| Lang parity (En/Ar/Fr/Tr key sets, both catalog families) | **0 missing** (no keys added or removed) |
| Migrator idempotent double-run (SQLite) | PASS — empty DB and populated fixture (§7.1e) |
| Arabic outside Ar.json | None added (migration/test fixtures contain one Arabic sample string inside a **test** seed, matching the pre-existing STEI test's fidelity purpose; product code and migrations contain none) |
| Repository-wide legacy-table sweep | No `accounts` table references remain in product code |

## 9. Owner flags (design decisions surfaced, not silently taken)

- **F-1 — Fiscal periods (scope note, completed):** mandate 2 was impossible
  without periods; 059 backfills them and creation seeds them. The
  profile-edit path (`UpsertFiscalYear*` in `Company.cs`) still creates fiscal
  years **without** periods — left for the owner/step that owns company-profile
  editing.
- **F-2 — `account_type` derivation for accounts-only rows:** code roots
  1/2/3/5 map to ASSET/LIABILITY/EQUITY/REVENUE; **all other roots default to
  EXPENSE** (covers 4 Purchases and 6 Expenses in the seed scheme). If custom
  roots exist in the field with different natures, the owner should supply the
  mapping; affected rows are exactly the accounts-only rows of a pre-059 DB.
- **F-3 — Old STEI packages:** importing a pre-059 `.stei` package containing an
  `accounts` dataset will recreate that table as an orphan nothing reads
  (generic table import). Dataset governance belongs to the data-exchange step
  (sweep D-3); the export side already emits `chart_of_accounts`.
- **F-4 — AccCard serial display:** the card's read-only serial field showed the
  integer Id; it now shows a short fragment of the account Guid. Cosmetic only.
- **F-5 — Untouched sweep MINORs in these windows:** C-4/C-5 (localization and
  `"Header"` context-menu literals), C-7 (no New-Root button in CoaWin), C-8
  (seed writes English into `name_ar`), C-9 (Statement/Ledger open the same
  window), C-10 beyond the serial fragment. They are outside this step's
  mandate and are listed as GAPs in §11 rather than silently fixed or hidden.

## 10. Comparison tables (CoaWin / AccCard / JrnWin vs counterparts)

Condensed from `~/workspace/research/sweep/compare-journal-coa.md` (the full
audit, incl. SAP B1, remains the reference), with the **Step 2** column recording
exactly what this step changed. ⚠ = counterpart behavior asserted from product
knowledge in the sweep, not live-verified.

### 10.1 CoaWin — chart of accounts manager

| Capability | ShouTech before | Al-Ameen V10 ⚠ / Phenix v10 ⚠ | ERPNext | Odoo | **Step 2** |
|---|---|---|---|---|---|
| Tree / hierarchy | ✓ | ✓ | ✓ | ✗ flat + groups | unchanged (now fed from the canonical chart) |
| Search / filter | ✓ | ✓ | ✓ | ✓ | unchanged |
| Add Child / Edit (AccCard) | ✓ | ✓ | ✓ | ✓ | unchanged — but writes now land in the store the journal reads |
| Add Root | ✗ | ✓ | ✓ | ✓ | GAP (C-7, flag F-5) |
| Delete / Disable | ◐ in AccCard, guard blind (C-3) | ✓ blocked once used | ✓ blocked once used | archive | **Guard fixed** — posted-to accounts cannot be deleted/disabled (C-3) |
| Import / Export tree | ✓ CSV/XLSX/`.sta` | ✓ ⚠ | ✓ Importer | ✓ CSV | unchanged; upsert-by-code now targets the canonical store |
| Account Type / Root Type per node | ✗ — no type column; reports guessed from code prefixes | ✓ | ✓ Root Type + Account Type | ✓ Type | **Type now exists and is authoritative** (`account_type` on every row; reports classify by it). Tree still doesn't *display* it — display GAP |
| Balance per node | ✗ | ✓ ⚠ | ✓ | ✓ | **Data now real** (balances computed from posted lines in `AccRepo`); tree column still absent — display GAP |
| Drill to Statement | ◐ report couldn't resolve the account (R-3) | ✓ | ✓ | ✓ | **Fixed** — statement resolves by code (R-3) |
| Move / reparent | ◐ string-parsed parent, desync on rename (C-6) | ✓ ⚠ | ✓ | n/a | **Fixed structurally** — parent is a Guid FK; renumber keeps children attached (proven in tests) |
| Template chart at creation | ◐ hard-coded seed into the wrong table | ✓ ⚠ | ✓ per-country templates | ✓ localization packages | Seed now lands in the canonical chart + periods (§4); template gallery still GAP |

### 10.2 AccCard — account card

| Field / behavior | ShouTech before | Al-Ameen ⚠ | ERPNext `Account` | Odoo `account.account` | **Step 2** |
|---|---|---|---|---|---|
| Code / Name AR / Name EN / Parent / Is Group / Status | ✓ | ✓ | ✓ | ✓ | unchanged, persisted to the canonical chart |
| Root Type / Report Type | ✗ inferred from prefix | ✓ | ✓ Root Type + Report Type | derived from Type | **Root Type is now stored** (`account_type`, derived from code root at save) and drives BS/P&L; a dedicated Report Type field and an editable Type picker remain GAP |
| Account Type (Bank/Cash/Receivable…) | ✗ | ✓ | ✓ ~30 types | ✓ 17 types | GAP — classification spine beyond the 5 root types is a later step |
| Live Balance / Debit / Credit tiles | ✗ cached columns, 0.00 forever (C-2) | ✓ from ledger | ✓ from GL | ✓ from journal items | **Fixed** — computed from POSTED lines at load |
| Delete / Disable guard | ✗ blind to journal (C-3) | ✓ | ✓ | ✓ | **Fixed** (§6) |
| Allow Reconcile / Taxable / Tax Rate / Cost-center mode / Budget / Is Default / Risk | ✓ (ShouTech field set) | ◐ | ◐ | ✓ reconcile | unchanged fields, now on the canonical row; enforcement of cost-center mode at posting is step 3 (J-3) |
| Currency per account | ◐ checkbox only | ✓ | ✓ Account Currency | ✓ Currency | GAP (unchanged) |
| Opening balance / Freeze / Balance Must Be | ✗ | ◐/✓ ⚠ | ✓ | ◐ | GAP (unchanged) |
| Statement / Ledger buttons | ◐ statement broken (R-3) | ✓ | ✓ | ✓ | Statement target **fixed**; both buttons still open the one window (C-9, flag F-5) |

### 10.3 JrnWin — journal entry (unchanged window; unblocked)

JrnWin itself was **not modified** — this step removes the data-layer mismatch
underneath it. The comparison deltas that move:

| Path | Before | Counterparts | **Step 2** |
|---|---|---|---|
| Save draft with a CoaWin-visible account code | ✗ throws "Account code '131' is missing…" on every fresh company (J-1) | ✓ basic function in all | **Works** — draft saves with codes from the visible chart (harness b, Kern.Tests) |
| Post | ✗ unreachable on fresh companies (no resolvable accounts, no periods) | ✓ | **Works** — posting triggers validate against the same chart; periods exist |
| Load a posted entry / account names | ◐ names resolved as a save side-effect (J-7) | ✓ | Resolution path unchanged but now reads the store the UI maintains |
| Reverse / Print / AutoBalance | ✓ (engine sound) | ✓ | unchanged |
| Everything else in the sweep's JrnWin table (party per line, per-line FX = J-5, entry types, templates, approvals = J-4/J-6/J-8/J-9) | ✗/◐ | ✓ | **Out of scope** — steps 3/7 and the journal-workflow step, per mandate |

## 11. Pillars checklists (per touched window)

Legend: PROVEN = evidence in §7; GAP = explicitly not done.

### CoaWin (+ AccCard as its editor)
- **UI:** unchanged this step (tree, context menu, import/export, seed offer) — GAPs: no New-Root (C-7), no type/balance columns in the tree, menu literals (C-5).
- **DB:** PROVEN — all reads/writes on `chart_of_accounts` post-059; legacy table dropped; migrator double-run idempotent.
- **Linking:** PROVEN — parent Guid FK; renumber keeps children attached (Kern.Tests); recycle-bin lists deleted accounts again.
- **Posting:** PROVEN — accounts created here are exactly what `JournalRepository` resolves and the posting triggers validate (harness b).
- **Persistence:** PROVEN — every AccCard field maps to a canonical column (extension set §3.1); balances are computed, never stored.
- **Downstream:** PROVEN for TB/BS/P&L/statement (§7c); GAP: tree balance/type display.

### AccCard
- **UI:** unchanged except the serial field shows a Guid fragment (F-4).
- **DB / Persistence:** PROVEN — full field round-trip incl. flags, tax rate, parent, type derivation (Kern.Tests `AccRepo_RoundTrip…`).
- **Linking:** PROVEN — Statement button's report resolves the account by code (R-3); parent combo writes the Guid FK.
- **Posting:** PROVEN via lifecycle guard seeing real postings (C-3) and resolution (J-1).
- **Downstream:** PROVEN — card balances equal posted-line sums (C-2).

### JrnWin
- **UI:** untouched (by mandate).
- **DB:** PROVEN — drafts/posts land in `journal_entries`/`journal_entry_lines` against canonical Guids.
- **Linking:** PROVEN — CoaWin-visible codes resolve (the J-1 scenario).
- **Posting:** PROVEN — fresh-company save **and** post, trigger-validated (harness b; Kern.Tests).
- **Persistence:** PROVEN — posted entry reloads by document number with codes + amounts (Kern.Tests).
- **Downstream:** PROVEN — the entry's lines are what TB/BS/P&L/statement report (harness c).

### Report windows (TrialBalanceWin / BalSheetWin / IncStmtWin / AccStmtWin + RptBase family)
- **UI:** untouched; aliases preserved so no window code changed.
- **DB / Downstream:** PROVEN for TB/BS/P&L/statement/cash-flow on SQLite (harness c, verbatim SQL); GAP: live WPF rendering + the wider report family (R-4 class) verified by code + build only — Windows pass owed.
- **Posting / Persistence:** n/a (read-only surfaces); they now read only posted lines — drafts never leak into GL reports (status = 1 filter everywhere).

### BankRecWin / BinWin / DatWin (dataset surface)
- **DB / Linking:** re-pointed to the canonical chart (bank GL accounts, recycle bin, accounts dataset) — code + build PROVEN; runtime GAP: Windows pass owed.

## 12. Files changed

- `dba/mig/sqlite/059_coa_unification.sql`, `dba/mig/postgresql/059_coa_unification.sql` (new)
- `cod/app/src/Services/Accounting.cs` (AccRepo rewrite), `Company.cs` (canonical seeding + periods), `RptQuery.cs` (GL family), `CardStore.cs`, `RecycleBinSvc.cs`, `DataExportSvc.cs`
- `cod/app/src/Views/CoaWin.xaml.cs`, `AccCard.xaml.cs` (Guid Id callers)
- `src/ShouTech.Infrastructure/OperationsRepository.cs`, `src/ShouTech.Domain/Interfaces/IOperationsRepository.cs`
- `tests/Kern.Tests/CoaUnificationTests.cs` (new), `SteiRoundTripTests.cs` (canonical-store assertions)
- `docs/FIX_PHASE_STEP2.md` (this file)

---

<!-- source: docs/FIX_PHASE_STEP3.md -->

# FIX PHASE — STEP 3: Real GL Posting for Operational Documents

**Branch:** `fix/foundations-3-glposting` (worktree `~/workspace/projects/ShouTech-fix3`)
**Base:** tip of `fix/foundations-2-coa` (`57de49a`) — the foundations chain is sequential; the canonical chart is `chart_of_accounts` (Guid-keyed, migration 059).
**Date:** 2026-11-17 · **Status:** implemented, gated, NOT pushed, NOT merged.
**Evidence base:** `~/workspace/research/sweep/invoicing-posting.md`, `journal-coa.md` (§1c J-2/J-3, §1d), `cash-payments.md`, `orders-projects-assets.md`, `mfg-hr-crm.md`, `compare-invoicing.md`, `compare-orders-projects-assets.md`, `compare-mfg-hr-crm.md`, `~/workspace/research/acceptance-sweep-report.md`.

---

## 1. The defects, in one paragraph

No operational document created a real journal entry. `InvRepo.Post` fabricated the voucher number (`var journalNo = "JV-" + DocNo`, old `InvRepo.cs:285-286`) and stamped it on the invoice header; repo-wide, only `JournalRepository` (hand-typed journals via JrnWin, cash vouchers via CshWin) ever inserted into `journal_entries`. POS sales, order→invoice conversions, manufacturing completions and asset depreciation likewise never reached the GL, so the Trial Balance / Balance Sheet / P&L that Step 2 made truthful were still *incomplete by construction*. Separately (J-3), cost-center validation in `JournalRepository.ApplyCostCenterAllocationsAsync` had a free-text escape — `continue; // Free-text cost center: no master row, legacy behavior.` — so a cost-center typo posted cleanly and vanished from cost-center reporting, and the account-level `cost_required` flag (carried onto the canonical chart by migration 059) was never consulted on the journal path.

## 2. Architecture: one posting path, inside the document's transaction

**`GlPosting` (`src/ShouTech.Infrastructure/GlPosting.cs`) is the single document→GL posting engine.** It does not invent a parallel writer: it produces entries in exactly the shape `JournalRepository.SaveDraftAsync` + `PostAsync` produce (draft header → lines → cost-center validation/splits → status flip to Posted), so the database posting triggers validate the open fiscal period and postable accounts identically, and `JournalRepository.PostAsync` / `ReverseAsync` now **delegate** their cost-center validation and reversal core to the same code (`GlPosting.ApplyCostCenters`, `GlPosting.ReverseInTransaction`). There is one validation implementation and one reversal implementation in the tree.

`GlPosting.PostInTransaction(connection, transaction, provider, GlDocumentRequest, userId)` runs **inside the caller's existing transaction** — the document and its journal entry commit or roll back together:

1. Lines are aggregated by (account code, cost center, side); zero lines drop out. Unbalanced / <2 lines / zero total fail with the existing catalog codes ACC-5001/5002/5003.
2. **Duplicate guard (ACC-5012):** a live (draft/posted, non-reversal) entry already linked to the same source document fails the posting.
3. The open fiscal period containing the entry date is resolved exactly as `JournalRepository` resolves it; none → the posting (and therefore the document) fails.
4. Every account **code** is resolved against the canonical chart (active, postable, not deleted); unresolvable → **ACC-5011**.
5. Header is inserted as a Draft with the source link (migration 060, §3), `document_number = "JV-" + source doc no` (suffixed with an entry-id fragment only on collision with an unrelated manual entry), `reference_number = source doc no`.
6. Lines are inserted; `ApplyCostCenters` validates and writes `journal_line_cost_splits` (§5).
7. The header flips to Posted (triggers fire), audit events 1 (created) and 2 (posted) are written.

Synchronous ADO is used deliberately: the app-layer document repository (`InvRepo`) is synchronous, and the async infrastructure repositories call the same core inside their async transaction scope without moving their commit boundary. `JournalRepository.PostDocumentAsync` wraps the core in its own transaction for callers that own none.

**Callers (all in the document's own transaction):**

| Document path | Caller | Source type |
|---|---|---|
| Sales/purchase invoice, returns, POS sale, stock in/out | `InvRepo.Post` (app layer) | `SAL` / `PUR` / `SRT` / `PRT` / `POS` / `ENT` / `EXT` (the document's `KindCode`) |
| Sales-chain invoice post | `CommercialDocumentRepository.PostSalesAsync` | `SAL` |
| Order → invoice conversion | `OrderDocumentRepository.ConvertToInvoiceAsync` | `SAL` / `PUR` (the invoice kind) |
| Manufacturing completion | `MfgOrderRepository.PostAsync` | `MFG` |
| Manufacturing cancellation of a posted order | `MfgOrderRepository.CancelAsync` → `GlPosting.ReverseInTransaction` | reverses the `MFG` entry |
| Asset depreciation | `AssetRepository.PostDepreciationAsync` via `AssetService.PostDepreciationAsync`, new **Post Depreciation** button in AssetWin | `AST-DEP` |

## 3. Migration 060 — `060_document_gl_posting.sql` (SQLite + PostgreSQL)

Adds to `journal_entries`: `source_doc_type TEXT`, `source_doc_id TEXT`, `source_doc_no TEXT`, plus index `ix_journal_entries_source_doc (company_id, source_doc_type, source_doc_id)`. Guarded/idempotent on both providers (SQLite `@if-column-missing` directives, PG `ADD COLUMN IF NOT EXISTS`); migrator double-run verified (§7). `source_doc_id` is TEXT because the document stores mix integer `doc_id`s (invoices, chain documents), Guids (manufacturing orders) and composite keys (asset depreciation uses `assetGuid@yyyy-MM-dd`, one entry per asset per posting date — depreciation is posted incrementally, so it is the one source allowed multiple live entries over time). Reversal entries **copy** the source columns, so the GL→document trace survives reversal, and the duplicate guard ignores reversal rows.

## 4. Account mapping (per document kind)

Defaults are the canonical codes present in **every** seeded chart template (`cod/app/src/Resources/COA/*.shc`): Cash `131`, Bank `132`, Receivable `1201`, Inventory `1203`, Payable `2201`, Sales `301`, Sales Returns `302`, COGS `401`, Purchases `402`, Purchase Returns `403`, Depreciation Expense `505`, Accumulated Depreciation `1190`. A material's `acc_*` columns override the revenue / purchase / COGS / inventory legs **when they hold an account code**; the document header's cost center is applied to the revenue/purchase legs of invoice documents.

| Kind | Debit | Credit | Notes |
|---|---|---|---|
| Sales invoice | Receivable `1201` — grand total (minus any amount paid at the document) + settlement account (Cash `131` / Bank `132`) for the paid part | Sales `301` (per-line `acc_sales`) — net of tax when tax is split, tax-inclusive otherwise; Output VAT (below) | COGS pair below |
| POS sale | Settlement by tender: Cash → `131`, Checks/Card → `132`, Credit → Receivable `1201` for the unpaid part | Sales `301` as above | POS posts through `InvRepo.Post` (PosCheckoutWin), so it inherits this mapping; paid-in-full cash POS = Dr Cash / Cr Sales |
| Sales return | Sales Returns `302` (per-line `acc_sales_return`) + Output VAT debit when split | Receivable `1201` — grand total | COGS pair reversed |
| Purchase invoice | Purchases `402` (per-line `acc_purchase`) + Input VAT debit when split | Payable `2201` — grand total (minus paid part) + settlement account for the paid part | No COGS |
| Purchase return | Payable `2201` — grand total | Purchase Returns `403` (per-line `acc_purchase_return`) + Input VAT credit when split | |
| COGS (sales / POS) | COGS `401` (per-line `acc_cogs`) | Inventory `1203` (per-line `acc_inventory`) | At the stock layer's **current** cost figures: balance `average_cost` for the line's warehouse, falling back to the material card `avg_cost`/`cost`. Only for stockable materials (the StkPost service-material rule). Step 6 owns costing-method unification |
| COGS (sales return) | Inventory `1203` | COGS `401` | Mirror of the above |
| Chain sales invoice (`PostSalesAsync`) | Receivable `1201` — grand total | Sales `301` (+ VAT when split) | No COGS: the chain invoice does not move stock (the delivery note did, without amounts) |
| Order conversion (sales) | Receivable `1201` | Sales `301` (+ VAT when split) | Invoice is created already-Posted; GL is written in the conversion transaction |
| Order conversion (purchase) | Purchases `402` (+ Input VAT when split) | Payable `2201` | |
| Manufacturing completion | Inventory — finished item's `acc_inventory` or `1203` — raw lines' total amount | Inventory — each raw item's `acc_inventory` or `1203` — its line amount | Overhead is **not** GL-posted: it stays in the expense accounts where it was incurred (the stock layer values finished goods at total cost incl. overhead — the valuation gap is Step 6's, flagged §9 F-3) |
| Asset depreciation | Depreciation Expense `505` — accumulated-to-date minus already-posted | Accumulated Depreciation `1190` | Incremental: re-running posts only the new increment; a run with nothing new posts nothing |
| Stock in (`ENT`) | Inventory `1203` — line totals | Purchases `402` | |
| Stock out (`EXT`) | COGS `401` — line totals | Inventory `1203` | |
| Quotation, Stock Transfer, Opening Stock, Inventory Count | — no GL entry — | | Quotations are not accounting documents; a transfer moves value between warehouses of the same inventory account; opening/count adjustments are valuation events (Step 6). These kinds no longer receive a fabricated journal number — `journal_doc_no` stays empty |

**Tax rule:** Output VAT `2205` / Input VAT `1205` are split out **only when the company has configured them postable** (or a material's `tax_account` names a postable account). In every seeded template `1205`/`2205` are non-postable group accounts, so by default tax stays inside the revenue/purchase leg and the entry balances on the document totals — the same totals the party ledger has always used. When a postable tax account exists, revenue/purchases post net and the tax leg carries the document's tax amount.

**Amounts dependency (Step 5):** `InvRepo.Post` builds its `InvDoc` from the document's lines and does **not** load the stored header discount/extra (the sweep's stored-`grand_total` BLOCKER). The GL entry therefore posts exactly the totals the document layer hands over today — identical to what `party_ledger` receives today — so GL and party ledger can never disagree with each other. When Step 5 fixes the totals load, both ledgers move together with no change needed here. The chain/conversion paths use their stored header totals, as they already did for `party_ledger`.

**Reversal/cancellation symmetry:** returns post mirroring entries through the same mapping (table above), so an invoice + its return nets to zero per account (proven §7). Manufacturing cancellation reverses the completion entry through the shared Reverse path in the cancellation transaction. A document's GL entry can always be reversed from JrnWin through the existing Reverse button, because it is an ordinary posted journal entry. Pre-Step-3 documents have no GL entry; cancelling one simply has nothing to reverse (manufacturing path handles this explicitly).

## 5. Cost-center validation (J-3) — `GlPosting.ApplyCostCenters`

One implementation, called from `JournalRepository.PostAsync` (so **JrnWin and CshWin inherit it**) and from every document posting:

| Case | Before Step 3 | Now |
|---|---|---|
| Code matches **no** master row | Silently posted, no split (`continue; // Free-text…`) | **Posting fails — ACC-5009 `CostCenterUnknown`** (new) |
| Account flagged `cost_required`, line has no center | Never consulted | **Posting fails — ACC-5010 `CostCenterRequired`** (new) |
| Center is a group or inactive | ACC-5008 (only when the text happened to match) | ACC-5008 — unchanged code, now reached for every matched center |
| Center has an allocation not totalling 100 / targeting a non-leaf | ACC-5007 | ACC-5007 — unchanged |
| Valid leaf / valid allocation | Split rows written, residue to largest target | Unchanged mechanics |

Legacy free-text **drafts** are untouched as drafts; *posting* one now validates and fails on an unknown center — intended, per the mandate, with no silent bypass. **Reversals** keep the historical leniency (`enforceValidation: false`): a reversal mirrors an already-posted entry and is never blocked by master-data changes made after the original posting; unknown centers on a reversal record no split, exactly as before.

New catalog codes ACC-5009…ACC-5012 are registered in `ErrorCodes.cs`, all four error catalogs (En/Ar/Fr/Tr) and `docs/ERROR_CATALOG.md`. ACC-5011 = mapped posting account unresolvable; ACC-5012 = duplicate document posting (§2).

## 6. Per-defect closure

| ID | Defect | Closure | Proof |
|---|---|---|---|
| J-2 | No operational document creates a real journal entry; `InvRepo.Post` fabricates `"JV-" + DocNo` and stamps it on the header | §2/§4: every listed kind posts a real balanced entry in the document's transaction, linked by migration-060 source columns; the stamp is now the real entry's `document_number` returned by the engine. Only `JournalRepository` inserted entries before; `GlPosting` writes in the identical shape and shares its validation/reversal core, so there is still exactly one journal writer design | Harness (per-kind cases); Kern.Tests `GlPostingTests` (12 facts) |
| J-3 | Free-text cost centers escape validation; `cost_required` never consulted | §5: unknown center → ACC-5009, cost-required without center → ACC-5010, on the shared posting path (JrnWin/CshWin + all document postings) | Harness CC cases; Kern.Tests `JournalPost_UnknownCostCenter_FailsWithAcc5009`, `JournalPost_CostRequiredAccountWithoutCenter_FailsWithAcc5010` |
| AST-01 (asset family) | Depreciation is display-only, never posted | §2/§4: depreciation posting API + AssetWin **Post Depreciation** button; incremental, idempotent per date | Harness AST-DEP case; Kern.Tests `AssetDepreciation_PostsIncrementOnly` |
| MFG (mfg-hr-crm) | Manufacturing completion moves stock but never posts value to GL | Completion posts Dr/Cr inventory at raw value in the posting transaction; cancellation reverses it through Reverse | Harness MFG case; Kern.Tests `Manufacturing_PostAndCancel_PostsAndReversesGl` |
| POS (cash-payments) | POS sale never reaches GL; its cash was unrecorded in the books | POS posts through `InvRepo.Post` → Dr Cash/Bank/Receivable by tender, Cr Sales, + COGS | Harness POS case; Kern.Tests `PosSale_Post_DebitsCash_NotReceivable` |

Out of scope and untouched, as mandated: party-master unification / `party_ledger` settlement mechanics (Step 4 — this step's entries debit/credit only the party **control** accounts in the GL), `InvRepo.Post` amount correctness and kind guards incl. the Quotation-receivable bug (Step 5 — see §4 amounts dependency; note the Quotation GL consequence: a quotation posted through `InvRepo.Post` builds **no** GL lines by design in §4, but its `party_ledger` row is Step 5's to fix), valuation/COGS costing unification (Step 6).

## 7. Proof

### 7.1 Headless harness — `~/workspace/research/fix-step3/gl_harness.py` — **28/28 PASS**

Runs the **real SqlMigrator** (fix3 build) and replays the app's own SQL shapes (`GlPosting.PostInTransaction` / `ReverseInTransaction` / `ApplyCostCenters`, with the real posting triggers enforcing period/account validity):

- Migration 060 applies; migrator double-run is a clean no-op; source columns exist.
- Per kind, post → entry exists, is Posted, balanced, carries the source link, `document_number = "JV-" + doc no`, and the per-account amounts equal the §4 mapping table: SAL (incl. COGS 80), PUR, SRT, PRT, POS-cash, order-conversion SAL, MFG (Dr/Cr inventory 20), AST-DEP (300).
- The template VAT account `2205` is non-postable → tax stays unsplit by design (ACC-5011 on direct resolution, which `TryResolveAccount` turns into "no tax leg").
- Duplicate document posting → ACC-5012. Posting with no open period → fails, **no entry written**.
- Unknown cost center → ACC-5009 with the whole posting rolled back; cost-required account without a center → ACC-5010; group center → ACC-5008; valid leaf center posts and its split rows sum exactly to the line amounts.
- Reversal of the SAL entry: original marked Reversed, reversal Posted with mirrored amounts, pair nets to zero per account; aggregate of all live entries balances.
- Source checks: the fabricated stamp is gone from `InvRepo`; every document repository calls `GlPosting`; `JournalRepository` delegates CC validation + reversal to the shared core and the free-text escape comment is gone.

### 7.2 Kern.Tests — `tests/Kern.Tests/GlPostingTests.cs` (12 facts)

Real C# paths against real migrated SQLite databases: sales/purchase/returns/POS postings through the real `InvRepo` (balanced, linked, correct accounts/amounts, real journal number stamped and reloadable); invoice reversal via `ReversePostedInvoice` netting GL to zero; period-less posting failing with the document left Draft and un-stamped; ACC-5009 / ACC-5010 through the real `JournalService`; ACC-5012 + Reverse symmetry through `JournalRepository.PostDocumentAsync`; manufacturing post + cancel through the real `MfgOrderService`; asset depreciation increments through the real `AssetService`. **Execution is owed on Windows** (net8.0-windows testhost cannot run on this Linux VM) — the project compiles at 0 errors / 0 warnings here.

Existing fixtures were updated where Step 3 changed the world they simulate: `ChainTests` and `OrderServiceTests` now seed the GL company (companies row, fiscal year + periods, canonical posting accounts) their documents post against, and the manufacturing `TempDb` gained the journal/fiscal/cost-center tables plus the `1203` account and `acc_inventory`/`cost_required` columns. `Phase3ServiceTests.FakeAssetRepository` implements the new interface member.

### 7.3 What is NOT proven here

WPF runtime behavior (clicking Post in InvoiceWin / PosCheckoutWin, the new AssetWin button) and **live PostgreSQL** execution of migration 060 and the new queries are owed on Windows / a live PG instance — no PG server exists in this VM. The PG migration uses only standard idempotent constructs and mirrors the SQLite logic, but it has not been executed. Build + SQLite harness success is not product readiness, per the standing rule.

## 8. Gates

| Gate | Result |
|---|---|
| Full solution build `--no-incremental` | **0 errors, 0 warnings** |
| `tools/audit_window_context.py` | **exit 0** |
| Lang parity (En/Ar/Fr/Tr, Lang + Errors catalogs) | **0 missing** in every language (3 new `Assets.*` Lang keys + 4 new error codes, all ×4) |
| Migrator idempotent double-run (SQLite) | PASS (harness) |
| Arabic outside Ar.json | None added (Arabic text appears only in the Ar Lang/Errors catalogs) |
| Headless harness | **28/28 PASS** (§7.1) |

## 9. Owner flags

- **F-1 — Period-close UI: ABSENT for periods (verdict: flagged, not built).** A fiscal **year**-close surface exists (`RolloverWin` → `OperationsService.CloseFiscalYearAsync`), but **no window can close a fiscal period**: `AccountingStructureRepository.ClosePeriodAsync` (with its no-drafts / balanced / sequential-close rules) has zero UI or service callers. Building it properly is a window build — period list per year, close action wired through a service, permissions, Lang keys — so per the mandate's stop rule it was **not** ballooned into this step. The posting side is fully period-safe today: every entry (manual or document) fails into a closed/missing period, and year close already requires all periods closed at the engine level.
- **F-2 — Tax split depends on chart configuration.** With the seeded templates, VAT accounts are non-postable groups, so tax is **not** split out of revenue/purchases until the company makes a postable tax account (or sets materials' `tax_account`). If the owner wants tax always split, the chart templates' `1205`/`2205` must become postable (or gain postable children) — a template/seed decision, not a posting-code change.
- **F-3 — Manufacturing overhead and finished-goods value.** GL inventory rises by the raw-material value only; the stock layer values finished goods at total cost **including overhead**. The difference is the Step 6 valuation unification's to close; it is recorded here so it is not discovered later as a discrepancy.
- **F-4 — Stock kinds without GL.** Opening Stock and Inventory Count post no GL entry (valuation events, Step 6); they also no longer carry a fabricated journal number. If the owner wants opening balances in the GL from day one, that is a deliberate opening-entry design (Dr Inventory / Cr Opening Balance Equity) to approve separately.
- **F-5 — Chain (DLV) COGS.** The sales chain's accounting document is the invoice, which posts revenue/receivable only; the delivery note moves stock at zero cost, so chain COGS never posts. Fixing that belongs with the chain's costing (Step 6) — the invoice-side GL here is complete for what the chain currently values.

## 10. Comparison tables (InvoiceWin / POS / AssetWin vs counterparts)

Condensed from `~/workspace/research/sweep/compare-invoicing.md` and `compare-orders-projects-assets.md`, with the **Step 3** column recording exactly what this step changed. ⚠ = counterpart behavior asserted from product knowledge in the sweep, not live-verified.

### 10.1 InvoiceWin — posting behavior (the window itself is unchanged)

| Capability | ShouTech before | Al-Ameen V10 ⚠ / Phenix v10 ⚠ | ERPNext | Odoo | **Step 3** |
|---|---|---|---|---|---|
| Posting creates a real GL entry | ✗ fabricated `"JV-"+DocNo` stamp only | ✓ | ✓ GL Entry per invoice | ✓ journal entry | **✓** — balanced entry in `journal_entries`, linked to the document, in the posting transaction |
| Journal/voucher number on the document | ~ fake string | ✓ opens the real voucher | ✓ | ✓ | **✓** — the real entry's document number; JrnWin can open/reverse it |
| Accounts from material configuration | ✗ `acc_*` columns honored by nothing | ✓ | ✓ item defaults | ✓ category accounts | **✓** — `acc_sales/acc_cogs/acc_inventory/acc_purchase/…` override the canonical defaults per line |
| COGS + inventory relief on sales | ✗ | ✓ | ✓ | ✓ | **✓** — at current stock-layer costs (Step 6 owns the costing method) |
| Tax split to VAT account | ✗ | ✓ | ✓ | ✓ | **◐** — split when a postable tax account is configured; otherwise tax-inclusive leg (flag F-2) |
| Part-payment at posting (paid amount → cash/bank, remainder → control) | ✗ paid amount ignored by posting | ✓ | ✓ | ✓ | **✓** in GL (party_ledger settlement remains Step 4) |
| Reversal of a posted invoice | ◐ return document, no GL effect | ✓ | ✓ credit note with GL | ✓ credit note | **✓** — the return posts a mirroring GL entry; pair nets to zero |
| Posting into a closed period | n/a (nothing posted) | ✗ blocked | ✗ blocked | ✗ blocked | **✓ blocked** — period resolution + DB triggers, document stays Draft |

### 10.2 POS (PosWin + PosCheckoutWin — UI unchanged; posting path is InvRepo)

| Capability | Before | Al-Ameen ⚠ / Phenix ⚠ | ERPNext POS | Odoo POS | **Step 3** |
|---|---|---|---|---|---|
| Sale posts to GL | ✗ | ✓ | ✓ | ✓ (POS session entry) | **✓** — Dr Cash/Bank by tender (Credit → Receivable), Cr Sales, + COGS, in the sale's transaction |
| Cash tender recorded in the books | ✗ (sweep: POS cash unrecorded) | ✓ | ✓ | ✓ | **✓** — cash leg is the `131` debit above |
| Everything else in the sweep's POS table (split tender, hold, receipt print, POS profile/session close) | ✗ | ✓ | ✓ | ✓ | GAP — unchanged; POS **session**-level aggregated posting (Odoo model) is not adopted: ShouTech posts per sale, like ERPNext/Al-Ameen |

### 10.3 AssetWin (one button added: Post Depreciation, bottom bar)

| Capability | Before | Al-Ameen ⚠ | ERPNext | Odoo | **Step 3** |
|---|---|---|---|---|---|
| Depreciation posts to GL | ✗ display-only schedule (AST-01) | ✓ periodic vouchers | ✓ depreciation schedule → journal entries | ✓ depreciation board posts entries | **✓** — Dr `505` / Cr `1190`, accumulated-to-date minus already-posted, per asset per date |
| Re-run safety | n/a | ✓ | ✓ | ✓ | **✓** — second run for the same date posts nothing; later date posts only the increment |
| Disposal with proceeds / gain-loss | ✗ status flip (AST-02) | ✓ | ✓ | ✓ | GAP — unchanged, not in this step's mandate |
| Asset acquisition GL (Dr asset / Cr payable-cash at purchase) | ✗ | ✓ | ✓ | ✓ | GAP — acquisition normally arrives via a purchase invoice, which now posts Dr Purchases; capitalizing it to the asset account is a fixed-asset policy decision for the owner |

### 10.4 OrdWin / MfgWin (windows unchanged; their repositories post)

Order→invoice conversion and manufacturing completion now create the GL entries in §4 in their own transactions; MfgWin cancellation reverses the completion entry. Their remaining sweep gaps (OrdWin header fields, MFG overhead costing) are unchanged and out of this step's scope.

## 11. Pillars checklists (per touched window)

Legend: PROVEN = evidence in §7; GAP = explicitly not done.

### InvoiceWin (SAL/PUR/SRT/PRT/POS kinds)
- **UI:** unchanged (no window code edited).
- **DB:** PROVEN — entries land in `journal_entries`/`journal_entry_lines` with migration-060 source link; migrator double-run idempotent.
- **Linking:** PROVEN — entry ↔ document both ways (source columns on the entry; real `journal_doc_no` on the document, reloadable via `InvRepo.Load`).
- **Posting:** PROVEN — per-kind balanced entries per §4 in the document's transaction (harness + Kern.Tests); closed/missing period blocks the post and leaves the document Draft.
- **Persistence:** PROVEN — every amount on the entry derives from persisted document lines/header fields the loader already reads; no UI-only GL data.
- **Downstream:** PROVEN at data level — entries are ordinary posted journals, so Step 2's TB/BS/P&L/statement (computed from posted lines) now include document postings; GAP: live WPF report rendering owed on Windows.

### PosCheckoutWin / PosWin
- **UI:** unchanged. **DB/Posting:** PROVEN via the `InvRepo` POS path (tender-based settlement leg). **Linking:** PROVEN — source type `POS`, document number stamped. **Persistence:** PROVEN — tender comes from the persisted `payment_method`/`paid_amount`. **Downstream:** as InvoiceWin. GAP: POS session/day-close aggregation does not exist (per-sale posting instead, §10.2).

### OrdWin (order → invoice conversion)
- **UI:** unchanged. **DB/Posting:** PROVEN — conversion writes the invoice **and** its GL entry in one transaction (harness conversion case; fixture-seeded Kern.Tests suite compiles/runs the path). **Linking:** PROVEN — entry source = the created invoice; the pre-existing `document_links` order↔invoice link is unchanged. **Persistence:** PROVEN — totals from the persisted order lines. **Downstream:** invoice GL flows to GL reports as above.

### MfgWin (manufacturing)
- **UI:** unchanged. **DB/Posting:** PROVEN — completion posts inventory value transfer; cancellation reverses it through Reverse in the same transaction (harness + Kern.Tests). **Linking:** PROVEN — source = order Guid + `MO-<doc no>`. **Persistence:** PROVEN — amounts from persisted order lines. **Downstream:** GAP — overhead valuation difference (flag F-3, Step 6).

### AssetWin
- **UI:** PROVEN (code) — **Post Depreciation** button added to the existing bottom command bar, shell-context inherited (BaseWin), Lang keys ×4, gated by `PermCat.Assets.Update`, errors via `AppHost.Errors`. Runtime click-through owed on Windows.
- **DB:** PROVEN — depreciation entries are ordinary journal entries with `AST-DEP` source link.
- **Linking:** PROVEN — entry ↔ asset (source id `assetGuid@date`, source no = asset code); the journal number is shown to the user.
- **Posting:** PROVEN — Dr `505` / Cr `1190`, incremental and re-run-safe (harness + Kern.Tests).
- **Persistence:** PROVEN — amount derives from the persisted register fields via the existing `AssetService` schedule math; nothing stored twice (already-posted is computed from the GL itself).
- **Downstream:** PROVEN at data level — depreciation now reaches TB/BS (accumulated depreciation is a real posted balance). GAP: disposal workflow (AST-02) unchanged.

### JrnWin / CshWin (manual journals + cash vouchers)
- **UI:** unchanged. **Posting:** PROVEN — both post through `JournalRepository.PostAsync`, which now enforces J-3 (ACC-5009/5010) via the shared core; reversals use the shared reversal core with identical semantics. **Downstream:** cost-center splits now exist only for validated centers, so cost-center reporting can no longer silently lose a typo'd center — it fails at posting instead.

## 12. Files changed

- `dba/mig/sqlite/060_document_gl_posting.sql`, `dba/mig/postgresql/060_document_gl_posting.sql` (new)
- `src/ShouTech.Infrastructure/GlPosting.cs` (new — the posting engine: document posting, shared cost-center validation, shared reversal), `src/ShouTech.Domain/Entities/GlPosting.cs` (new — request/result records), `src/ShouTech.Domain/ErrorCodes.cs` (ACC-5009…5012)
- `src/ShouTech.Infrastructure/JournalRepository.cs` (PostAsync → shared CC validation; ReverseAsync → shared reversal core; new `PostDocumentAsync`)
- `cod/app/src/Services/InvRepo.cs` (real GL in `Post`, per-kind line builder, material `acc_*`/cost resolution; fabricated stamp removed)
- `src/ShouTech.Infrastructure/CommercialDocumentRepository.cs` (chain SAL GL), `OrderDocumentRepository.cs` (conversion GL), `MfgOrderRepository.cs` (completion GL + cancellation reversal), `AssetRepository.cs` + `src/ShouTech.Domain/Interfaces/IAssetRepository.cs` + `src/ShouTech.Application/Services/AssetService.cs` (depreciation posting)
- `cod/app/src/Views/AssetWin.cs` (Post Depreciation button), Lang catalogs ×4 (`Assets.PostDepreciation` etc.), Errors catalogs ×4 (ACC-5009…5012), `docs/ERROR_CATALOG.md`
- `tests/Kern.Tests/GlPostingTests.cs` (new, 12 facts); fixture updates in `SalesChainPostingTests.cs`, `OrderServiceTests.cs`, `ChequeMfgServiceTests.cs`, `Phase3ServiceTests.cs`
- `docs/FIX_PHASE_STEP3.md` (this file)

---

<!-- source: docs/FIX_PHASE_STEP4.md -->

# FIX PHASE — STEP 4: Party Master Unification + Settlement Chain

Branch `fix/foundations-4-parties` (from `fix/foundations-3-glposting` @ 2bd8ea1).
Migration **061** (SQLite + PostgreSQL). Evidence:
`~/workspace/research/sweep/parties-ledger.md`, `cash-payments.md`,
`invoicing-posting.md`, `acceptance-sweep-report.md`.

## 1. The defects, in one paragraph

The app kept **two party masters with zero synchronization**: the party
cards (CstCard, PartyManagerWin, PartySearchWin) edit `parties`
(migration 031, Guid id), while operations — the credit guard, invoice
party resolution, balances, aging — ran on the legacy `customers` /
`suppliers` tables (migration 050, integer ids). A customer created in
the card could not be invoiced; the balance lookup bridged the two
stores by *parsing the party code as a number*, so looking up code `"1"`
returned a stranger's 777 debit (harness-proven in the sweep); the
credit guard's legacy fallback had **no company filter** (cross-company
leak). The settlement chain was broken at every cash entry point: cash
vouchers (CshWin) kept the party as memo free-text and never wrote
`party_ledger`; `paid_amount` was never even selected by InvRepo, let
alone maintained; POS cash was recorded nowhere; PayRecWin therefore
showed no real payments — and its payment list even offered sales
**returns** as allocatable payments. `party_ledger.company_id` was
written in two namespaces (company code by some writers, company Guid
by the chain/order repositories), supplier balances were sign-inverted
in two reports, and dead legacy party code in `CardStore` shadowed the
real repository.

## 2. The canonical decision

**`parties` (031) is the one party master.** Rationale, weighed against
the evidence:

- **UI gravity is entirely on `parties`.** Every card, the directory,
  the picker and the search window read and write it; it carries the
  richer field set (contacts, tax number, credit limit/days, price
  level, group, opening balances). Nothing in the UI edits
  `customers`/`suppliers` — they have no writer at all.
- **Operational gravity is real but shallow.** The integer-id consumers
  are the `party_ledger.party_id` history, document `party_id` columns,
  the credit guard and invoice resolution. That gravity is preserved
  without keeping a second master: migration 061 gives every party a
  stable integer identity, **`parties.ledger_id`**, seeded *from* the
  legacy ids wherever the same party exists in both stores, so no
  ledger row and no document ever needs re-pointing.
- The alternative (canonical = legacy tables) would have meant moving
  every card/directory/picker onto tables with a poorer field set and
  per-kind duplicated schemas, for no compensating benefit.

After 061 there is **one writer path** (`MasterCardRepository.SaveParty`,
used by CstCard/PartyManagerWin) and **one reader path** (the same
repository + `PartyLedger.ResolveLedgerId` / `TaxSvc.ResolveParty` for
operational resolution). The legacy tables are **dropped**, not kept as
a compat layer — the same call Step 2 made for the split chart: a
read-only shadow would silently diverge again, and nothing reads them
after this step (verified by grep: zero product references remain).
**No dual-writing anywhere.**

### Data mapping (migration 061, both providers, idempotent)

| Legacy / existing | Canonical result |
|---|---|
| `customers` row whose (company, code) matches a `parties` kind-C row | The `parties` row survives and **keeps the legacy `customer_id` as its `ledger_id`** — its entire `party_ledger`/document history stays attached untouched. |
| `customers` row with no card twin | Inserted into `parties` (new Guid; deterministic `md5(company:061:code)` on PostgreSQL, random on SQLite) with `ledger_id = customer_id`; name = English name falling back to the Arabic name column; credit limit carried over. |
| `suppliers` rows | Same, as kind `S`. |
| Card-only `parties` rows | Fresh `ledger_id` **above** `MAX(existing ledger_ids, legacy ids, party_ledger.party_id)` — a card party can therefore never inherit an orphan or stranger ledger balance (the sweep's code-`"1"` case). |
| `party_ledger` rows | **Not re-pointed at all** — they already reference the integer identity, which is preserved. Rows whose `company_id` holds the company **Guid** (chain/order writers) are normalized to the company **code**, the canonical form every writer now produces. |
| `customers` / `suppliers` tables | Dropped at the end of the migration. |

Supporting schema in 061: `parties.ledger_id` + unique index
`ux_parties_ledger_id`; `party_ledger.journal_entry_id` (links a
settlement row to the journal entry that created it — the reversal
mirror key); new table **`voucher_party_links`** (the structured party
attachment of a cash voucher: journal entry, company, kind, party
ledger id + code snapshot, RCV/PAY, doc no, amount, currency).

Runtime invariants that keep it unified:

- `MasterCardRepository.SaveParty` assigns `ledger_id` on insert
  (max of `parties.ledger_id` and `party_ledger.party_id`, + 1, inside
  the save transaction) — a card-created party is invoiceable the
  moment it is saved.
- `PartyLedger` (Infrastructure) is the single home for company-key
  resolution (code-or-Guid → code), exact party resolution
  (company + kind + code → `ledger_id`; **numeric code parsing is
  deleted everywhere**), ledger writes, and the reversal mirror.
- Every balance/lookup query is company-scoped **and** party-exact:
  `MasterCardRepository.GetPartyBalanceAsync` resolves the party's own
  `ledger_id` and sums only its rows; `PartyCreditGuard` resolves the
  limit and balance against the canonical party in the caller's company
  (the unfiltered legacy fallback is deleted).

## 3. The settlement chain

Settlement entries are always **credit for customers, debit for
suppliers** — the mirror of the invoice entry, matching the existing
reconciliation model.

- **Cash vouchers (CshWin).** The header gains a real Party Code +
  Party Kind (Customer/Supplier) beside the memo text. On save, the
  code is resolved against the canonical master (`PartyLedgerSvc`);
  an unknown code **blocks the save** with catalog error
  `CashVoucher.PartyNotFound` — free-text party vouchers are gone. The
  resolution is stored in `voucher_party_links`; when the voucher's
  journal entry posts, `JournalRepository.PostAsync` writes the
  `party_ledger` settlement **inside the posting transaction**, linked
  by `journal_entry_id`. A Reverse button (the voucher window had none)
  reverses the journal through the Step-3 path, and
  `GlPosting.ReverseInTransaction` mirrors every journal-linked ledger
  row with swapped sides in the same transaction — GL and party ledger
  can never disagree about a reversal.
- **Invoices / POS paid at posting (InvRepo).** `paid_amount` was
  already persisted on the document; now, when a SalesInvoice,
  PurchaseInvoice or PosSale posts with `PaidAmount > 0`, the posting
  transaction also writes the settlement entry (RCV/PAY, capped at the
  grand total) and a pre-allocating `payment_allocations` row, so
  PayRecWin never offers the collected part as an unallocated payment.
  A POS cash sale against a party therefore settles the party side in
  the same transaction that posts its GL (Step 3) — POS cash is no
  longer recorded nowhere. Returns never take this path (a return is
  not a payment — sweep L-10).
- **Allocation (PayRecWin / ReconciliationRepository).** The artificial
  500-row list cap is removed; open documents are pre-filtered to
  genuinely open sales/purchase invoices (`doc_kind` SAL/POS/PUR —
  returns and quotations can no longer appear as settleable documents);
  the payments list is restricted to real settlement kinds
  (RCV/PAY/INS — returns are no longer allocatable as payments), joins
  the canonical party name, and carries the currency; `AllocateAsync`
  re-validates the payment's kind server-side. The payments grid shows
  the new Party and Currency columns.
- **Installment collection** (DefinitionRepository, sweep L-11) now
  resolves the installment's party code through the canonical master
  instead of parsing it as a number; a numeric code is accepted only
  when a live party actually owns that `ledger_id`.

## 4. Per-defect closure

| Sweep ID | Defect | Closure |
|---|---|---|
| C-1 | Two unsynced party masters | Migration 061 unifies on `parties` + `ledger_id`; legacy dropped; one writer/reader path. |
| C-2 / parties T2 | Balance lookup returns another party's balance (code `"1"` → 777) | Numeric-parse bridge deleted; resolution is (company, kind, code) → own `ledger_id`. Harness + tests replay the exact case. |
| G-1 | Credit-guard fallback without company filter | Fallback deleted; guard reads canonical party + company-scoped balance only. |
| B-03 | Cash vouchers never write `party_ledger` | Voucher party link + settlement inside journal posting; reversal mirrors it. |
| B-06 | `party_ledger.company_id` code/Guid split | Writers resolve to the company code (`PartyLedger.ResolveCompanyCode`); 061 normalizes historical Guid rows. |
| L-7 (partial) | `paid_amount` never selected/maintained | InvRepo maintains it at posting for paid-at-posting documents (settlement + allocation); PayRecWin allocation maintains it thereafter. Step 5 still owns InvRepo amount correctness. |
| L-8 | POS cash recorded nowhere | PosSale with a party + paid amount settles in the posting transaction. |
| L-10 | Returns offered/allocatable as payments | Payment list + allocation restricted to RCV/PAY/INS; open docs restricted to SAL/POS/PUR. |
| L-11 | Installment collect parses code as party id | Canonical resolution with ownership check. |
| M-1 | PartyManagerWin mixed customer+supplier totals | Separate per-kind totals in the status bar. |
| RP-1 | Aging resolved parties via legacy tables | `PartyAging` rewritten on `parties`/`ledger_id`, per-kind orientation, both providers' date math. |
| RP-2 / C-3 | Supplier balance sign inverted | Supplier balance = credit − debit in CstCard and `RptQuery.SupplierBalances`. |
| RP-5 | Invoice lists joined legacy tables, Arabic-first names | Joins on `parties.ledger_id`, English-first names. |
| R-3 | Dead legacy party code in `CardStore` | Deleted (zero callers verified). |
| — | DatWin cross-company surface | `customers`/`suppliers` export datasets repointed at canonical `parties`; recycle-bin defs unified to one `parties` def; dashboard customer count reads `parties`. |

Out of scope by mandate and **not** fixed here: InvRepo.Post amount
correctness and kind guards (Step 5); CstCard's Statement button opening
an unfiltered statement (C-4); ReconciliationRepository has no
Unallocate (pre-existing MAJOR — reversing an *allocated* voucher
restores the ledger via the mirror but leaves the allocation row;
see owner flags).

## 5. Proof

### 5.1 Headless harness — `~/workspace/research/fix-step4/party_harness.py` — **26/26 PASS**

Builds a pre-unification DB with the Step-3 migrator (both masters
present), seeds rows in **both** legacy stores plus ledger history
(including the code-`"1"`/777 stranger case, an orphan 777 in a second
company, and a Guid-keyed company row), upgrades with the Step-4
migrator, and replays the production SQL shapes: matched pairs keep
legacy ids; legacy-only rows merge; the second company's code `"1"`
gets a fresh id and a **zero** balance; company normalization lands;
invoice → balance 1000; journal-linked receipt → ledger row, balance
600; PayRecWin source lists the receipt; allocation → paid 400/open
600; reversal mirror → balance 1000; **migrator double-run idempotent**
with stable state. (The harness caught a real bug on its first run —
a rank collision in the SQLite fresh-id backfill, fixed by
materializing ranks in a temp table before assigning.)

### 5.2 Kern.Tests — `tests/Kern.Tests/PartyUnificationTests.cs` (6 facts)

(a) card-saved party resolves via the real `TaxSvc.ResolveParty`;
(b) balance is party-exact and company-scoped (777 stranger + 555
other-company rows ignored) and the credit guard blocks/passes on the
correct company's numbers; (c)+(d) full chain through the real
repositories: invoice → two receipt vouchers posted via
`JournalService` (settlement written by the posting transaction) →
PayRecWin lists the payment with the party name → allocation settles
`paid_amount` → reversing the second voucher restores the balance
exactly; invoice collected at posting and POS cash sale settle via
real `InvRepo.Post`; migration leaves one party master and the app
migrator double-run succeeds. Existing fixtures updated:
`MasterCardServiceTests` (balance fact now asserts the canonical
path), `PostingCompletenessTests` (credit-guard fixtures seed
`parties` with `ledger_id`).

### 5.3 What is NOT proven here

Kern.Tests target `net8.0-windows` and **cannot execute on this Linux
host** — execution is owed on Windows, as with every prior step. The
WPF behavior of the new CshWin party fields and Reverse button is
build-verified only. The PostgreSQL migration was written to the same
semantics (deterministic Guids, `ROW_NUMBER`, `IF EXISTS/NOT EXISTS`
guards) but **live PG execution is owed**. The harness proves the
SQLite migration + SQL shapes headlessly; it does not execute the C#
repositories (the Kern facts do, on Windows).

## 6. Gates

- Full solution build `--no-incremental`: **0 errors, 0 warnings**.
- `tools/audit_window_context.py`: **exit 0**.
- Lang parity En/Ar/Fr/Tr: **0** missing/extra in both Lang (4 new keys
  ×4: `Vouchers.PartyCode`, `Vouchers.PartyKind`,
  `CashVoucher.PartyNotFound`, `PaymentReconciliation.Currency`) and
  Errors catalogs. No Arabic outside `Ar.json` (verified over the diff).
- Migrator idempotent double-run: proven by the harness (SQLite);
  PostgreSQL owed with live execution.

## 7. Owner flags

1. **CshWin link atomicity.** The voucher party link is saved right
   after the draft journal is created; if that insert fails, the save
   surfaces the error but a GL-only draft may remain (it posts no
   settlement until linked). Making draft+link one transaction needs a
   JournalRepository API change — flagged rather than hacked around.
2. **Reversing an allocated voucher/payment** is now handled by the shared
   journal reversal transaction: active allocations from the linked RCV/PAY
   ledger row are copied to `payment_allocation_reversal_audit`, the affected
   document `paid_amount` values are reduced, and the active allocation rows
   are deleted before the mirrored party-ledger row is written. If any audit
   insert or document update fails, the entire reversal rolls back. Covered by
   `PartyUnificationTests.SupplierPayment_IsAllocatedToPostedPurchaseInvoice`.
   block reversal while allocations exist, or build Unallocate.
3. **InvRepo reversal of a paid-at-posting invoice** does not mirror
   the paid settlement (the reversal document carries no PaidAmount);
   Step 5 owns InvRepo reversal semantics — flagged for that step.
4. **DatWin datasets**: the Customers/Suppliers dataset entries now
   export the canonical `parties` table (both entries export all
   parties; the catalog has no kind filter). Same shape as Step 2's
   chart export decision; split by kind if Man wants it.
5. **Installment currency** remains hardcoded `SAR` in the collect
   path — the installments table has no currency column to source it
   from (pre-existing schema gap).
6. **Credit guard vs inactive parties**: `PartyLedgerSvc`/`TaxSvc`
   resolution refuses inactive/deleted parties; the guard itself still
   evaluates a posted party by id even if later deactivated
   (pre-existing behavior, unchanged).

## 8. Comparison tables (materially changed windows)

Sources: `~/workspace/research/sweep/compare-parties.md`,
`compare-cash.md`.

### 8.1 Party cards (CstCard / PartyManagerWin / PartySearchWin — data path only)

| Aspect | Al-Ameen V10 | Phenix v10 | ERPNext | Odoo | ShouTech after Step 4 |
|---|---|---|---|---|---|
| Party master | One customer file + one supplier file, each THE master for its operations | One partner file per kind | One Customer / one Supplier doctype, used by all transactions | One `res.partner` for all party roles | **One `parties` table for both kinds** (kind column), the only store cards and operations read |
| Identity used by transactions | Card code/id | Card id | Name (link field) | Partner id | Stable integer `ledger_id` + code; Guid kept as row id |
| Created-in-card → transacted | Immediate | Immediate | Immediate | Immediate | Immediate (proven: harness (a) + Kern fact) |
| Balance on card | Live from ledger | Live from ledger | Live from GL/ledger | Live | Live from `party_ledger`, party-exact; supplier sign corrected |
| Credit limit enforcement | At posting | At posting | At posting | At posting | `PartyCreditGuard` at posting, correct party + company |

### 8.2 CshWin (cash vouchers)

| Aspect | Al-Ameen V10 | Phenix v10 | ERPNext (Payment Entry) | Odoo (Payments) | ShouTech after Step 4 |
|---|---|---|---|---|---|
| Party on voucher | Picked from card file | Picked from partner file | Link field, required for party payments | Partner field | **Code + kind resolved against the canonical master; unknown code blocks save** (picker parity still owed — code entry only) |
| Party ledger effect | Posts to customer/supplier account + statement | Posts to partner ledger | Ledger + GL on submit | Ledger + GL on post | `party_ledger` settlement **inside** the journal posting transaction + Step-3 GL |
| Reversal | Reverse entry restores statement | Storno restores | Cancel reverses | Reset/reverse | Reverse button added; journal reversal mirrors the ledger row symmetrically |
| Allocation | From statement/payment screens | Allocation screen | Payment Reconciliation tool | Reconcile view | PayRecWin allocation against real payments (see 8.3) |

### 8.3 PayRecWin (payment reconciliation)

| Aspect | Al-Ameen V10 | Phenix v10 | ERPNext | Odoo | ShouTech after Step 4 |
|---|---|---|---|---|---|
| Payment source | Real posted receipts/payments | Real posted payments | Submitted Payment Entries | Posted payments | Real `party_ledger` settlements (vouchers, POS, paid invoices, installments) |
| What can be allocated | Invoices only | Invoices only | Invoices only | Invoices only | Invoices only — returns/quotations excluded on both sides; kind re-validated at allocate |
| Party shown | Yes | Yes | Yes | Yes | **Yes (new Party + Currency columns)** |
| List limits | None artificial | None | None | None | 500-row cap removed |

## 9. Pillars checklists

### CstCard (customer/supplier card)
- UI: unchanged (data path only, per mandate). **PROVEN** (build).
- DB: reads/writes canonical `parties`; save assigns `ledger_id`. **PROVEN** (harness + Kern).
- Linking: card rows are the same rows TaxSvc/InvoiceWin resolve. **PROVEN** (Kern fact (a)).
- Posting: n/a (master card). Persistence: unchanged repository, now single-store. **PROVEN**.
- Downstream: balances on-card are party-exact; supplier sign fixed. **PROVEN** (Kern fact (b)).

### PartyManagerWin / PartySearchWin
- UI: per-kind totals in the manager status bar (M-1). **PROVEN** (build).
- DB/linking/persistence: already on `parties`; balances now oriented per kind. **PROVEN**.
- Posting: n/a. Downstream: directory balances match card balances (same query path). **PROVEN**.

### CshWin
- UI: Party Code + Kind fields and a Reverse button added in the existing layout/language system; buttons remain in the bottom bar. **PROVEN** (build; WPF visual check owed).
- DB: `voucher_party_links` written at save. **PROVEN**. GAP: link insert is not atomic with draft creation (owner flag 1).
- Linking: party resolved from the canonical master; unknown code blocks save with a catalog error. **PROVEN** (code path + error key ×4).
- Posting: settlement inside the journal posting transaction; GL unchanged (Step 3). **PROVEN** (Kern fact (c)).
- Persistence: link reloads with the voucher; fields repopulate on open. **PROVEN** (build).
- Downstream: PayRecWin sees the payment; reversal mirrors the ledger. **PROVEN** (Kern fact (d)).

### PayRecWin
- UI: Party + Currency columns added to the payments grid. **PROVEN** (build).
- DB: reads `party_ledger` + `payment_allocations` + canonical party names. **PROVEN**.
- Linking: allocations reference real documents/payments only. **PROVEN** (Kern fact (c)).
- Posting: allocation updates `paid_amount` transactionally (repository). **PROVEN**.
- Persistence: allocations persist; open amounts derive from `grand_total − paid_amount`. **PROVEN**.
- Downstream: GAP — no Unallocate (owner flag 2, pre-existing).

### InvoiceWin / POS (posting path via InvRepo)
- UI: unchanged. DB: documents unchanged; settlement + allocation rows added at posting. **PROVEN** (Kern facts).
- Linking: party must resolve through the canonical master (TaxSvc) — card-created parties included. **PROVEN**.
- Posting: GL (Step 3) + party ledger + settlement in one transaction. **PROVEN**.
- Persistence: `paid_amount` now truthful for paid-at-posting documents. **PROVEN**.
- Downstream: PayRecWin open/payment lists consistent (collected parts pre-allocated). **PROVEN**. GAP: reversal of a paid invoice (owner flag 3, Step 5).

### JrnWin (journal reversal path)
- UI/DB: unchanged. Posting: reversal now also mirrors journal-linked party settlements (`GlPosting`). **PROVEN** (Kern fact (d) via JournalService.ReverseAsync).
- Downstream: party balances restore symmetrically with the GL. **PROVEN**.

## 10. Files changed

- **Migrations**: `dba/mig/sqlite/061_party_unification.sql`,
  `dba/mig/postgresql/061_party_unification.sql` (new).
- **Infrastructure**: `PartyLedger.cs` (new), `MasterCardRepository.cs`,
  `PartyCreditGuard.cs`, `JournalRepository.cs`, `GlPosting.cs`,
  `ReconciliationRepository.cs`, `DefinitionRepository.cs`,
  `CommercialDocumentRepository.cs`, `OrderDocumentRepository.cs`.
- **Domain**: `Entities/MasterCards.cs` (`Party.LedgerId`),
  `Entities/Reconciliation.cs` (payment row party name/currency).
- **App**: `Services/PartyLedgerSvc.cs` (new), `Services/TaxSvc.cs`,
  `Services/InvRepo.cs`, `Services/RptQuery.cs`, `Services/CardStore.cs`
  (dead legacy code deleted), `Services/Data.cs`,
  `Services/DataExportSvc.cs`, `Services/RecycleBinSvc.cs`,
  `Views/CshWin.cs`, `Views/CstCard.cs`, `Views/PartyManagerWin.cs`,
  `Views/PayRecWin.cs`, `Views/Reports/CustomerDueWin.cs`,
  Lang catalogs ×4 (4 new keys each).
- **Tests**: `PartyUnificationTests.cs` (new, 6 facts),
  `MasterCardServiceTests.cs`, `PostingCompletenessTests.cs` (fixtures).

---

<!-- source: docs/FIX_PHASE_STEP5.md -->

# FIX PHASE — STEP 5: InvRepo.Post Correctness (Stored Totals, Kind Guards, Symmetric Reversals)

**Branch:** `fix/foundations-5-invpost` (worktree `~/workspace/projects/ShouTech-fix5`)
**Base:** tip of `fix/foundations-4-parties` (`1e7984a`) — the foundations chain is sequential; Step 3 added `GlPosting` + migration 060, Step 4 unified parties on `parties.ledger_id` + migration 061 and made `InvRepo.Post` settle collections/POS.
**Date:** 2026-11-17 · **Status:** implemented, gated, NOT pushed, NOT merged.
**Evidence base:** `~/workspace/research/sweep/invoicing-posting.md`, `~/workspace/research/acceptance-sweep-report.md` (worst finding #6, §2 invoicing I-1/I-2), `docs/FIX_PHASE_STEP3.md`, `docs/FIX_PHASE_STEP4.md` (owner flag 3 is this step's reversal item).
**No migration in this step** — every total/field correctness required was already in storage (see §2); migrations 062+ were not needed and 053 remains reserved.

---

## 1. The defects, in one paragraph

`InvRepo.Post` SELECTed the document's stored `subtotal, tax_amount, grand_total` and then **threw them away**: the `InvDoc` it built set none of them and left `LinesDiscountAmount`/`LinesExtraAmount` at their 0 defaults, so the amount it debited to `party_ledger`, fed to the credit guard (ACC-5006) and — since Step 3 — posted to the GL was `InvoiceValue` recomputed from the lines alone. A document whose lines are 1,000 + 150 tax with a 100 header discount **stores 1,050 and debited 1,150**. Worse, after Step 3 the recomputation also broke the GL entry itself: the revenue legs summed to the lines total (1,150) while the control leg carried the grand total, so any discounted/extra-charge invoice was actually **unbalanced and rejected by GlPosting (ACC-5001)**. The posting was also kind-blind: `IsSales()` includes Quotation and the party-ledger block ran for any kind with a party, so **posting a quotation created a real receivable** and stock documents credited a supplier. And reversals were asymmetric in three places: the reversal document dropped the original's header money fields (its stored total therefore never cancelled the original's), a return's GL carried no settlement leg (a paid invoice's cash leg never netted out), and the settlement Step 4 writes at posting time was left dangling on reversal (Step 4 owner flag 3).

## 2. Amount-flow analysis — where the stored total was discarded

The stored total is **correct at save time**: `SaveDraft → BindHeader` writes `$grand = doc.InvoiceValue` (= lines subtotal − `discount_amount` + `extra_amount` + tax, `InvDoc.cs`), alongside stored `subtotal` and `tax_amount`. The loss happened entirely inside `Post`:

| Stage | Before Step 5 | After Step 5 |
|---|---|---|
| `Post` header SELECT | selected `subtotal, tax_amount, grand_total` **and discarded all three**; `discount_amount`/`extra_amount` were not even selected | selects all five and assigns them: `LinesDiscountAmount`, `LinesExtraAmount`, and the new `InvDoc.StoredSubtotal / StoredTaxAmount / StoredGrandTotal` |
| Amount definition | `doc.GrandTotal` = recomputed from the just-loaded lines with discount/extra = 0 | `doc.PostingTotal` (= `StoredGrandTotal ?? GrandTotal`) — the stored figure is the single amount of record; the recomputed value is used only for never-saved in-memory drafts |
| GL (`BuildGlLines`, Step 3) | control leg from the recomputed figure; revenue/purchase legs from raw line sums → unbalanced whenever discount/extra ≠ 0 (ACC-5001) | control legs, revenue/purchase/returns legs and stock-doc legs all derive from `PostingTotal`/`PostingTaxAmount`; the header adjustment is allocated across the revenue/purchase legs (§3.1) so the entry balances **exactly on the stored total** |
| `party_ledger` | debit/credit = recomputed figure | debit/credit = `PostingTotal` |
| Credit guard (ACC-5006) | fed the recomputed figure | fed `PostingTotal` — the figure that will actually post (mandate 4) |
| Settlement cap (Step 4) | `Math.Min(PaidAmount, recomputed)` | `Math.Min(PaidAmount, PostingTotal)` |
| Disagreement handling | silent — the recomputation *became* the posted number | if lines + header adjustments in storage no longer recompute to the stored `grand_total`, the **stored total still wins** for every consumer and the disagreement is reported via the error catalog (**ACC-5013 `DocumentTotalMismatch`**, `AppHost.Errors.Report`, never silently recomputed) |

`InvRepo.Load` now also populates the `Stored*` fields, so any holder of a loaded document (InvoiceWin reload, the reversal flow) sees the same totals posting will use.

### 3.1 GL leg allocation

The header adjustment (−discount + extra, plus any stored-vs-lines residue reported under ACC-5013) is, in accounting terms, a revenue/purchase adjustment. `BuildGlLines` computes each line's base leg (line subtotal, plus line tax when tax is not split to a VAT account), then allocates the adjustment across lines **in proportion to |line subtotal|** with the rounding residue on the largest line. Per-material account overrides (`acc_sales`, `acc_purchase`, …) therefore keep their proportional share, the aggregated legs sum to exactly `grand − taxLeg`, and with the tax leg the entry balances on the stored total to the last 0.0001. COGS pairs are untouched (cost-based, self-balancing; Step 6 owns costing). StockIn/StockOut GL legs now carry the stored grand total directly (previously subtotal+tax recomputed).

## 3. Kind → effects matrix (enforced)

One definition — **`InvPostingPolicy.For(kind)`** in `InvRepo.cs` — enforced inside `InvRepo.Post` at the repository layer, so every caller (InvoiceWin, PosCheckoutWin, any future caller) is safe. `StkPost.ApplyDocument` additionally refuses transfer/count kinds itself (defense in depth for direct callers).

| Kind (InvRepo) | GL | Party ledger | Stock via StkPost | Settlement at posting | Credit guard |
|---|---|---|---|---|---|
| SalesInvoice (SAL) | ✓ Step-3 mapping | ✓ customer **debit** = stored total | out | ✓ (RCV + allocation) | ✓ on the debit |
| PosSale (POS) | ✓ tender legs | ✓ customer debit (if a party is resolved) | out | ✓ | ✓ |
| SalesReturn (SRT) | ✓ mirror, incl. settlement leg (§4) | ✓ customer **credit** | in | ✗ — a return is not a payment | ✗ — reduces exposure |
| PurchaseInvoice (PUR) | ✓ | ✓ supplier **credit** = stored total | in | ✓ (PAY + allocation) | ✓ on the credit (unchanged semantics) |
| PurchaseReturn (PRT) | ✓ mirror, incl. settlement leg | ✓ supplier **debit** | out | ✗ | ✗ |
| StockIn (ENT) | ✓ Dr Inventory / Cr Purchases at stored total | **✗** | in | ✗ | ✗ |
| StockOut (EXT) | ✓ Dr COGS / Cr Inventory at stored total | **✗** | out | ✗ | ✗ |
| OpeningStock (OPN) | ✗ (valuation event — Step 6, Step-3 flag F-4) | ✗ | in | ✗ | ✗ |
| **Quotation (QUO)** | **✗** | **✗ — never a receivable** | **✗** | **✗** | **✗** |
| StockTransfer (TRN) | — **BLOCKED in this path (ACC-5014)** — | | | | |
| InventoryCount (CNT) | — **BLOCKED in this path (ACC-5014)** — | | | | |

Notes on the blocked kinds: through `InvRepo` a transfer posted every line as qty-in at the single header warehouse (**stock from nothing**, sweep I-6 — `InvDoc.TargetWarehouseId` is not even persisted, so a correct two-sided transfer cannot be built from this document) and a count added the **full counted quantity instead of the delta** (I-7). The correct engines exist and are what the warehouse windows use — `StkPost.PostTransfer` (two-sided) and `StkPost.PostCount` (delta-based) — so this path now refuses with a cataloged `AppError` (ACC-5014) that InvoiceWin already renders as a localized catalog message. Wiring InvoiceWin's TRN/CNT modes to those engines is a UI job, flagged in §7 (F-1).

Kinds that do **not** flow through InvRepo, for completeness of the enumeration: the commercial-chain kinds (`CommercialDocKinds`: QUO/SO/DLV/SAL, RFQ/PO/RCV/PUR) live in `CommercialDocumentRepository`, which already guards them the same way (QUO/SO post with no side effects, DLV moves stock only, SAL books ledger+GL from its **stored** header totals); order/proforma kinds live in `OrderDocumentRepository` (orders post with no ledger/GL/stock; conversion creates the invoice and posts its stored totals). Neither needed changes for this step's mandate and neither was touched.

A quotation posted through InvRepo now does exactly one thing: its status flips to Posted inside the transaction. No `party_ledger` row, no GL entry (its `journal_doc_no` stays empty), no stock movement, no settlement, no guard evaluation — proven in §6 (c).

## 4. Reversal symmetry design

`ReversePostedInvoice` remains a return-document reversal (the product's established correction model), made fully symmetric:

1. **Header copy is complete.** The reversal `InvDoc` now copies *all* header money and context fields from the original: `LinesDiscountAmount`, `LinesExtraAmount`, `PaidAmount`, `PaymentMethod`, warehouse name (header and per-line), receipt no, reference, branch, sales rep, cost center, party/goods account names (previously all defaulted — sweep §2 BLOCKER). Its stored `grand_total` therefore equals the original's, and stored totals cancel exactly.
2. **GL mirrors the settlement leg.** Return GL legs (`BuildGlLines` SRT/PRT) now split the control leg exactly as the original did: the paid part through the settlement account (cash/bank by tender), only the unpaid remainder through Receivable/Payable. A paid invoice + its return now net to zero on **every** GL account (revenue vs its returns contra account net together, as designed in the chart). The purchase side's settlement account selection was unified to the same tender rule the sales side already used.
3. **Settlement un-does itself (Step 4 owner flag 3 — closed).** In the reversal's finalise transaction, `UnsettleOriginalOnReversal`:
   - **mirrors** every settlement entry written *at the original's posting* — identified precisely as `party_ledger` rows with `doc_kind IN ('RCV','PAY') AND doc_id = original` (the shape only `InvRepo.Post` writes; cash-voucher settlements carry `doc_id NULL` and installment collections use `INS`, so neither can be mistaken for one) — with debit/credit swapped, pointing at the reversal document. The ledger stays append-only; the settlement's net effect becomes zero;
   - **deletes every `payment_allocations` row against the original**. Allocations of *external* payments (receipt/payment vouchers allocated later in PayRecWin) simply free that payment again — the voucher's own ledger row stands, because the money really was received, and the party keeps the resulting credit balance (a refund owed / credit on account), which is the correct post-reversal position. A full reversal may leave no allocation against the original standing;
   - **returns the original's `paid_amount` to 0** — its settled state is exactly as if it had never been posted.
4. **Stock** already reversed symmetrically through the return's `StkPost` posting (quantities; valuation/avg-cost restatement is Step 6's, unchanged).
5. The pre-existing multi-transaction reservation design (Pending/Failed reconciliation) is unchanged; the un-settlement rides the existing finalise transaction, so it commits or fails with the reversal's completion record.

## 5. Per-defect closure

| Sweep ID | Defect | Closure |
|---|---|---|
| I-1 (§1 BLOCKER 1; report #6) | Post discards stored `grand_total`, debits the recomputed lines-only figure (1,050 stored → 1,150 debited); credit guard fed the same wrong figure | §2: stored totals loaded and consumed by GL + party_ledger + guard + settlement cap via `PostingTotal`; discrepancy reported ACC-5013, stored wins |
| I-1 consequence (found in this step) | After Step 3, a discounted/extra-charge invoice's GL entry was unbalanced (legs = lines total vs control = grand) → ACC-5001 rejection | §3.1 leg allocation: entry balances exactly on the stored total |
| I-2 (§1 BLOCKER 3) | Kind-blind ledger: quotation creates a receivable; stock kinds credit a supplier | §3: `InvPostingPolicy` enforced in `Post`; quotation has zero effects; ENT/EXT/OPN have no party ledger |
| §2 reversal BLOCKER | Reversal doc drops header money fields → stored totals don't cancel | §4.1 full header copy |
| Step 4 owner flag 3 | Reversing a paid invoice leaves its at-posting settlement dangling | §4.3 un-settlement: settlement mirrored, allocations deleted, `paid_amount` → 0 |
| §2 reversal MAJOR (GL part) | Return GL had no settlement leg, so a paid invoice's cash/bank leg never netted | §4.2 return settlement legs (SRT/PRT) |
| I-6 / I-7 (partial) | StockTransfer via InvoiceWin creates stock from nothing; InventoryCount adds the full counted qty | Blocked in this path (ACC-5014) + `StkPost.ApplyDocument` refuses both kinds; the dedicated engines remain the correct path. **The InvoiceWin TRN/CNT windows are not rewired** — flag F-1 |
| I-5 (Step-3/4 parts already closed) | `paid_amount` ignored at posting | Already closed by Steps 3+4 (GL settlement leg + ledger settlement); this step makes both consume the stored total and makes reversal mirror them |

Out of scope and untouched, as mandated: valuation/costing incl. reversal avg-cost restatement and movement `unit_cost` basis (Step 6); GL account mapping (Step 3); the party model (Step 4); the invoice UI and document schema (no schema change was needed — no migration); order→invoice stock (a different repository's defect, not InvRepo); the chain DLV guards (already kind-guarded, §3 note).

## 6. Proof

### 6.1 Headless harness — `~/workspace/research/fix-step5/invpost_harness.py` — **30/30 PASS**

Builds a DB with the fix5 migrator, seeds documents in the exact `SaveDraft`/`BindHeader` storage shape, and replays the new production posting shapes + source-checks the production code:

- (a) the sweep's exact case: lines 1,000 + tax 150 − discount 100 → **stored 1,050**; the old recomputed figure is demonstrated to be 1,150; party_ledger debit = **1,050**; the GL leg allocation sums to exactly 1,050 (entry balances); the credit guard on the stored figure passes a 1,100 limit that the old figure would have breached.
- (b) extra charges: stored 1,075 posts 1,075 to ledger and GL legs.
- (c) kind policy source-verified against `InvRepo.cs`/`StkPost.cs` (stored totals assigned in Post, all consumers on `PostingTotal`, ACC-5013/5014 wired, ledger/stock/GL gated by the policy, quotation = no effects, TRN/CNT blocked and refused by `StkPost`); a seeded quotation carries no ledger row.
- (d) paid invoice → reversal shapes: settlement mirror + allocation delete + `paid_amount` → 0 net the party ledger for the invoice to zero (200 Dr = 200 Cr across invoice, settlement, return, mirror).
- Migrator double-run idempotent (no new migration in this step).

### 6.2 Kern.Tests — `tests/Kern.Tests/InvPostCorrectnessTests.cs` (9 facts)

Real C# paths against real migrated SQLite databases, through the real `InvRepo` / `MasterCardRepository`:
(a) discounted sales invoice posts exactly 1,050 to GL (1201 Dr / 301 Cr) and party_ledger; the same for a purchase invoice to Payable/Purchases; (b) extra charges post the stored 1,075; (c) a posted Quotation has status Posted with **zero** party_ledger rows, zero GL entries, zero stock movements and an unchanged balance quantity; (d) a fully-paid cash invoice (settlement + allocation at posting) reversed through `ReversePostedInvoice` → party debit = credit in total, allocations gone, original `paid_amount` = 0, stock restored, reversal's stored grand = original's, GL nets to zero on cash/receivable/inventory/COGS individually and revenue-vs-returns combined; (e) credit guard: a 1,050-stored doc passes a 1,100 limit (the old 1,150 figure would have blocked), and a further doc that truly exceeds the limit blocks with ACC-5006; StockIn with a party creates no ledger row but moves stock; StockTransfer and InventoryCount throw ACC-5014 and fabricate nothing; the policy matrix itself is pinned by a fact. **Execution is owed on Windows** (net8.0-windows testhost cannot run on this Linux VM) — the project compiles at 0 errors / 0 warnings here.

### 6.3 What is NOT proven here

WPF runtime behavior (clicking Post/Reverse in InvoiceWin, the ACC-5014 message rendering for TRN/CNT modes — the window's existing `AppError` handler renders catalog messages, code-verified) and **live PostgreSQL** execution are owed on Windows / a live PG instance, as with every prior step. No query in this step is provider-specific except the two `is_deleted` literal forms in the un-settlement update, which follow the existing dual-provider pattern in the same file. Build + SQLite harness success is not product readiness, per the standing rule.

## 7. Gates

| Gate | Result |
|---|---|
| Full solution build `--no-incremental` | **0 errors, 0 warnings** |
| `tools/audit_window_context.py` | **exit 0** |
| Lang parity (En/Ar/Fr/Tr) | **0** missing/extra in both Lang and Errors catalogs (2 new error codes ×4 languages; no new Lang keys — no UI text added) |
| Arabic outside Ar.json | None (verified over the diff) |
| Migrator idempotent double-run (SQLite) | PASS (harness; no new migration) |
| Headless harness | **30/30 PASS** (§6.1) |

## 8. Owner flags

- **F-1 — InvoiceWin TRN/CNT modes now refuse to post (visible change, deliberate).** Posting a StockTransfer or InventoryCount from InvoiceWin previously *fabricated stock*; it now surfaces ACC-5014 and posts nothing. The windows themselves still open in those modes. Wiring them to `StkPost.PostTransfer`/`PostCount` (the WhsWin engines) — including persisting a target warehouse for transfers — is a UI build that should be scheduled; until then the warehouse windows are the correct route for transfers and counts.
- **F-2 — Reversal of an invoice allocated against external payments.** Un-allocation frees the external payment (its ledger row and the party's resulting credit balance stand, §4.3). If Man instead wants reversal *blocked* while external allocations exist, that is a one-line policy change in `UnsettleOriginalOnReversal` — the current behavior was chosen because a full reversal must not leave the invoice half-settled.
- **F-3 — Original document's status after reversal** still reads `Posted` (reversal state lives in `document_reversals`, pre-existing sweep §2 MAJOR). This step makes its *settled* state truthful (`paid_amount` 0, no allocations); changing the displayed status is a reporting/UI decision for Man.
- **F-4 — Standalone returns with a PaidAmount** now post a GL settlement leg (refund through cash/bank) mirroring the sales/purchase split. That is the accounting-correct reading of the field on a return; if Man intends return `paid_amount` to mean something else, the leg should be revisited.
- **F-5 — ACC-5013 is report-only by design.** A stored-vs-recomputed disagreement posts the stored total and logs the catalog code. If Man prefers a hard block on mismatch, the report call becomes a throw — flagged because silently *changing* the number was the original defect, and blocking could strand legacy rows whose stored totals are the only correct record.

## 9. Comparison table (InvoiceWin — materially changed posting behavior)

Changes in this step are repository-layer, **except** two behaviors a user can see: a discounted/extra-charge invoice now *posts successfully* (after Step 3 its GL entry was unbalanced and GlPosting rejected it with ACC-5001; before Step 3 it posted the wrong amount with no GL at all), and the TRN/CNT modes of InvoiceWin now refuse to post (F-1). Because behavior changes are visible, the comparison table is included (condensed from `~/workspace/research/sweep/compare-invoicing.md`; ⚠ = counterpart behavior asserted from product knowledge in the sweep, not live-verified).

| Capability | ShouTech before Step 5 | Al-Ameen V10 ⚠ / Phenix v10 ⚠ | ERPNext | Odoo | **Step 5** |
|---|---|---|---|---|---|
| Posted amount = document's stored total (header discount/extra honored) | ✗ stored 1,050 debited as 1,150 | ✓ | ✓ | ✓ | **✓** — one stored figure for GL, party ledger, guard, settlement |
| Discounted invoice posts a balanced GL entry | ✗ unbalanced (ACC-5001) after Step 3 | ✓ | ✓ | ✓ | **✓** — header adjustment allocated across revenue/purchase legs |
| Posting a quotation | ✗ created a receivable | ✓ no accounting effect | ✓ no GL/ledger until conversion | ✓ no journal until invoiced | **✓** — status only; no receivable, GL, stock or settlement |
| Stock documents (in/out) creating party balances | ✗ credited a supplier when a party was typed | ✓ no party effect | ✓ | ✓ | **✓** — no party ledger for ENT/EXT/OPN |
| Stock transfer / count from the invoice window | ✗ fabricated stock (in-from-nothing / full-qty add) | ✓ two-sided transfer / delta count | ✓ stock reconciliation (delta) | ✓ scrap/adjustment (delta) | **◐** — refused in this path (ACC-5014); dedicated warehouse engines do it correctly; window rewiring flagged (F-1) |
| Reversal of a paid invoice | ✗ settlement dangled; original stayed "paid"; GL cash leg never netted | ✓ full storno | ✓ credit note reverses payment allocation | ✓ reversal un-reconciles | **✓** — settlement mirrored, allocations removed, `paid_amount` → 0, GL nets to zero |
| Credit-limit check figure | ✗ recomputed (discount dropped) | ✓ document total | ✓ grand total | ✓ amount total | **✓** — the stored total that actually posts |

## 10. Pillars checklist — invoice/document surface (InvoiceWin / PosCheckoutWin via InvRepo)

- **UI:** GAP (deliberate) — no window code changed; the only visible deltas are correct posting outcomes and the ACC-5014 refusal in TRN/CNT modes (F-1). WPF click-through owed on Windows.
- **DB:** PROVEN — no schema change needed; stored header totals (`subtotal`, `discount_amount`, `extra_amount`, `tax_amount`, `grand_total`) already existed and are now actually consumed; migrator double-run idempotent.
- **Linking:** PROVEN — GL entries keep the Step-3 source link and now balance on the document's stored total; reversal documents link via `original_*` + `document_reversals` as before, with complete header context copied.
- **Posting:** PROVEN — stored-total posting to GL + party_ledger, kind→effects policy enforced at the repository layer, credit guard on the posting figure, settlement mirrored on reversal (harness §6.1 + Kern facts §6.2).
- **Persistence:** PROVEN — posting reads only persisted fields; the reversal persists the full copied header (discount/extra/paid/method/context) so its stored total cancels the original's.
- **Downstream:** PROVEN at data level — party balances, PayRecWin open amounts (allocations/`paid_amount` maintained symmetrically), and GL reports now all derive from the same stored totals; GAP: live report rendering owed on Windows.

## 11. Files changed

- `cod/app/src/Reporting/Invoices/InvDoc.cs` — `StoredSubtotal/StoredTaxAmount/StoredGrandTotal` + `PostingSubtotal/PostingTaxAmount/PostingTotal`.
- `cod/app/src/Services/InvRepo.cs` — stored-total load in `Post`/`Load`; ACC-5013 discrepancy report; `InvPostingPolicy` kind→effects enforcement; GL leg allocation + return settlement legs on stored totals; full reversal header copy; `UnsettleOriginalOnReversal`.
- `cod/app/src/Services/StkPost.cs` — `ApplyDocument` refuses StockTransfer/InventoryCount.
- `src/ShouTech.Domain/ErrorCodes.cs` + Errors catalogs ×4 + `docs/ERROR_CATALOG.md` — ACC-5013, ACC-5014.
- `tests/Kern.Tests/InvPostCorrectnessTests.cs` (new, 9 facts).
- `docs/FIX_PHASE_STEP5.md` (this file).

---

<!-- source: docs/FIX_PHASE_STEP6.md -->

# FIX PHASE — STEP 6: Valuation Unification (Phase B Migration 053) + RptQuery Swallow Removal

Branch: `fix/foundations-6-valuation` (base: `fix/foundations-5-invpost` @ a8e3d15).
Design: `~/workspace/research/phase-b-design.md` §1 (valuation) + §7 decision table.
Evidence: `~/workspace/research/sweep/stock-reports.md` (RC-1…RC-5),
`~/workspace/research/sweep/compare-stock-reports.md`.

## 1. The defects, in one paragraph

Stock valuation had **three writers with three different maths** (sweep RC-1):
`StkPost` valued outbound movements at the **selling price** (so returns
re-entered at sale price), `CommercialDocumentRepository` wrote chain
delivery movements at **`unit_cost = 0`** and never touched the average,
and `MfgOrderRepository` + `CardStore` each carried a **private copy** of
moving-average logic. The same stock was provably worth **70.00** in the
stock-balance report and **40.00** in the end-of-period report. Profit
reports (RC-2) costed from `materials.avg_cost`, which **no posting path
ever updated** — the sweep case reported a margin of **235 on a true
profit of 100**. `materials.cost_method` (WeightedAvg/FIFO/LIFO/Standard/
LastPurchase) was honored by **nothing** (RC-4). And `RptQuery.QueryAsync`
caught **every** exception and returned an empty result set (RC-5), so
any SQL/schema/provider failure rendered as a normal-looking "0 rows"
report.

## 2. Design-vs-built mapping (Phase B §1 + V-decisions)

| Design item | Disposition |
|---|---|
| One `StockValuationEngine`; every writer delegates | **Built.** `src/ShouTech.Infrastructure/StockValuationEngine.cs` + pure `ValuationMath.cs`. Verified mechanically: the only `INSERT INTO material_stock_movements` in the codebase is in the engine. |
| Migration 053: movement valuation columns, balance `stock_value`, `stock_cost_layers`, revaluation jobs, permissions | **Built**, both providers, idempotent; safe on DBs already at 059–061 (see §3). |
| FIFO layer queue (`stock_cost_layers`) | **Built.** FIFO oldest-first, LIFO newest-first, negative (deficit) layers settled by the next receipt (ERPNext queue behaviour). |
| Recalculate Costs job for back-dating | **Built as a service** (`StockRecalculationService`). The window surface is **flagged** (F-1) — it is a window build. |
| `materials.avg_cost` maintained by posting | **Built.** Engine refreshes the card average (company-wide Σvalue/Σqty) and `last_purchase_cost` on purchase receipts with every movement. |
| V1 — LIFO | **Recommendation taken:** engine ships LIFO, kept visible (regional parity; MatCard already offers it). |
| V2 — Back-dated policy | **Built as recommended:** posting-order valuation + manual Recalculate Costs; no automatic chronological reposting at post time. |
| V3 — Standard-cost variance | **Built as recommended:** report-only. Issues value at `standard_cost`; the receipt-vs-standard difference stays visible in the balance value; no variance GL account is posted. |
| V4 — GL COGS journals | The §7 table deferred this to Phase C, but the Fix Phase superseded it: Step 3 built the COGS journal (amounts from the pre-posting balance average) and **Step 6 re-sources those amounts from the engine** (§5). Step 3's account mapping is unchanged. |
| V5 — Valuation granularity | **Built as designed:** per material × warehouse. Per-batch valuation deferred; 053's layers carry no batch columns, and Step 14a (054) extends via movement allocations without changing the engine's value identity. |
| Batch/serial (054) | **Not built** (Step 14a). 053 leaves room: layers are per material×warehouse queues; allocations will reference movements, not rates. |

## 3. Migration 053

`dba/mig/{sqlite,postgresql}/053_valuation_engine.sql`:

- `material_stock_movements` gains `balance_qty_after`, `valuation_rate`,
  `stock_value_after`, `cost_amount` (signed: receipts +, issues −),
  `valuation_method`, `repost_batch_id`.
- `material_stock_balances` gains `stock_value` — the valuation of record.
- New `stock_cost_layers` (FIFO/LIFO queue; index on
  company/warehouse/material/is_open/layer_date/layer_id) and
  `stock_revaluation_jobs` (Recalculate Costs audit trail).
- Backfills: balance `stock_value = available_qty × average_cost` where
  unset; legacy movements stamped `valuation_rate = unit_cost`,
  `cost_amount = (qty_in − qty_out) × unit_cost`,
  `valuation_method = 'Legacy'`; one opening layer per positive
  FIFO/LIFO balance (NOT EXISTS-guarded).
- Permissions `INV.VALUATION.VIEW` (340) and `INV.VALUATION.RECALC`
  (341, sensitive): Admin + User get both, Viewer view-only.
- **Position safety:** the foundations chain already carries 059–061 and
  the migrator is filename-ordered with checksum-skip. 053 depends only
  on 013-era objects + roles/permissions — never on 059–061 objects —
  so it applies cleanly on a database that already ran 059–061. Proven
  by the harness fixture gate (§8). SQLite uses
  `-- @if-column-missing` guards; PostgreSQL uses
  `ADD COLUMN IF NOT EXISTS` / `ON CONFLICT`. Double-run idempotent on
  both paths (SQLite proven here; PG owed on Windows, §8.3).

## 4. Engine architecture

`ValuationMath` (pure, no DB) owns the arithmetic over a
`ValuationState` (quantity, value, layer queue):

- **Receipts** enter at actual cost (exact amount for transfers-in,
  linked rate for returns, document cost otherwise). For layer methods
  a receipt first settles negative layers; the settlement delta
  (deficit rate → actual rate) is part of the movement's `cost_amount`.
- **Issues** are valued **only** from the balance/queue — never from a
  caller-supplied price, so a selling price can never enter valuation
  again. WeightedAvg at the moving average; FIFO/LIFO from the queue;
  Standard at `standard_cost`; LastPurchase at `last_purchase_cost`.
- **Invariants:** a movement's `cost_amount` is exactly the change in
  balance value (Σ movement `cost_amount` ≡ balance `stock_value`); an
  issue that empties stock costs exactly the value before it (rounding
  residue absorbed; value is exactly 0 at quantity 0). Rates round to
  6dp, money to 4dp, AwayFromZero.

`StockValuationEngine.Apply(connection, transaction, provider, request)`
runs inside the **writer's own transaction** (sync ADO, like GlPosting)
and writes the movement (with its valuation record), the balance
(`average_cost` = display rate value/qty), the layer queue, and the
material card. Insufficient stock on a material with
`allow_negative = 0` throws the cataloged **INV-6002**. New error codes:
INV-6002 `StockValuationFailed`, INV-6003 `RecalculationFailed`,
INV-6004 `RecalculationClosedPeriod` (all four Errors catalogs +
`docs/ERROR_CATALOG.md`).

### Writer delegation inventory

| Writer | Before | Now |
|---|---|---|
| `StkPost.ApplyDocument` (InvRepo documents) | moving average, outbound at selling price | engine; SRT resolves the original document's rate via `TryGetDocumentRate` |
| `StkPost.PostCount` | full-qty recount movements | engine, **delta-only** (COUNT_CLOSE); zero-delta lines write no movement (behaviour change, flagged F-4) |
| `StkPost.PostTransfer` | private average copy | engine out + engine in with the out leg's exact `cost_amount` (value conserved across warehouses) |
| `StkPost.MoveMaterialBalances` (card merge admin op) | re-derived value = qty × avg | merges the engine-maintained `stock_value`; layer queue re-pointed to the target material |
| `CommercialDocumentRepository` DLV | `unit_cost = 0`, average untouched | one engine call per line (unit-equivalence kept) |
| `MfgOrderRepository.Post/Cancel` | private copy; GL from entered line amounts | engine for consume/produce/reverse; raw re-enters at the original consumption rate; **GL amounts are the engine's consumption costs** |
| `CardStore.PostOrder` (MfgWin card path) | private `InsertMovement`/`ApplyBalance` copy | engine (`MFG-OUT`/`MFG-IN`); private helpers deleted |

## 5. COGS wiring (Step 3's GlPosting, amounts from the engine)

`InvRepo.Post` now applies stock effects **before** building GL lines,
inside the same transaction. `AddCogs` reads the just-written movements
for the document: COGS = −Σ `cost_amount` (SRT: the re-entry value,
mirrored). This also fixes two latent errors in the old formula: it
multiplied the **raw line quantity** (chosen unit) instead of the base
quantity, and read the average **before** posting. Fallback to the
pre-posting average remains only for a stockable line the engine wrote
no movement for. Step 3's account mapping is untouched.
`MfgOrderRepository`'s completion journal likewise carries engine
consumption costs (Step 3 flag F-3 closed for the raw-material leg;
overhead still enters finished stock value via the order's computed
unit cost while the GL leg carries raw cost only — F-5).

## 6. Recalculate Costs (`StockRecalculationService`)

`RecalculateAsync(companyId, warehouseId?, materialId?, fromDate,
reason, userId)`:

- Writes a `stock_revaluation_jobs` row (Running → Done/Failed, with
  the cataloged error on failure) in its own committed write, so the
  job is auditable even when the replay fails.
- **Closed-period guard (INV-6004):** refuses when any fiscal period
  ending on/after `fromDate` is non-OPEN or its year is closed.
- Replays each (warehouse, material) movement chain in
  `(movement_date, stock_movement_id)` order through `ValuationMath`,
  restating `cost_amount` / `valuation_rate` / `stock_value_after` /
  `unit_cost` for rows ≥ `fromDate` and stamping `repost_batch_id`.
  Returns (sales/purchase) re-enter at their **original document's
  replayed rate** (via `original_doc_no`); transfers-in take the
  matching transfer-out's replayed value; layers and balances are
  rebuilt; card averages refreshed.
- **GL entries are NOT restated** by recalculation (F-2): the journal
  keeps the amounts posted at the time; the delta is reported on the
  job row for a manual adjustment decision.

## 7. RptQuery swallow inventory (RC-5) and replacements

| Site | Before | Now |
|---|---|---|
| `RptQuery.QueryAsync` (the single funnel for ~50 report queries) | catch-all → empty result; missing connection string → empty result | reports **DB-1005** via `AppHost.Errors.Report(...)` then throws cataloged `AppError(DB-1005)`; missing CS throws `AppError(DB-1004)`; cancellation rethrows untouched |
| `RptBase.RefreshAsync` | try covered only the data load | try now also covers definition/filter construction (filter choice loaders query via RptQuery); an `AppError` is not double-reported (source already logged it); failure shows `Report.LoadFailed` |
| `CustomReportsWin.LoadKeysAsync` | `catch { }` silent | reports DB-1005 centrally, window still opens |
| `CustomReportsWin.ResolveCompanyGuidAsync` | `catch → Guid.Empty` silent | reports DB-1005 centrally before the fallback |
| `CustomReportsWin.QueryReportKeysAsync` | empty list on missing CS | throws `AppError(DB-1004)` (reported by its caller) |

Verified non-swallows, left as-is: `RptNav.Show` (reports
NavigationFailed), `ReportRepo` (rollback + throw),
`FinancialAnalysisWin` (surfaces via MessageBox).

### Report valuation rewrites (one consistent valuation)

All read `COALESCE(cost_amount, (qty_in − qty_out) × unit_cost)` for
movement value (legacy rows fall back to their recorded cost) or the
balance `stock_value`:

- `StockBalancesAsync`: Value := `stock_value` (legacy fallback
  qty × avg); service / non-stocked materials excluded.
- `EndOfPeriodAsync`: Value := Σ `cost_amount` to the as-of date;
  AvgCost = Value/Balance; boundary changed to the end-exclusive
  pattern every other report uses (the old `<= date` dropped same-day
  timestamped movements on PostgreSQL — half of the 70-vs-40 split).
- `NegativeStockAsync`: re-sourced from `material_stock_balances`, so
  it agrees with Stock Balances' NEGATIVE filter by construction.
- `MaterialMovesAsync`: MoveValue := `cost_amount`; new
  `ValuationRate` and `BalanceValue` (stock_value_after) columns in
  the result set.
- `MostActiveMaterialsAsync`, `MaterialComparisonAsync`,
  `StagnantMaterialsAsync`, `BranchMaterialsAsync`: values from
  `cost_amount` / `stock_value`.
- `InvoiceProfitsAsync`, `MaterialStatisticsAsync`,
  `ItemProfitabilityAsync`: Cost := −Σ movement `cost_amount` for the
  document/material — per cost_method, in base units, at posting
  time; sales/purchase returns are **netted** (previously ignored).
  `ItemProfitabilityAsync` gains Cost/Profit/MarginPct (its window
  gains the matching columns, §9).

## 8. Proof

### 8.1 Headless harness — `~/workspace/research/fix-step6/valuation_harness.py` — **18/18 PASS**

Migrator gates (real SqlMigrator): full set incl. 053 on a fresh DB;
double-run idempotent; fixture DB migrated with 053 withheld (already
at 059–061) applies 053 cleanly afterwards and stays idempotent; 053
tables/columns/permissions verified. C# scenarios run the **real**
engine, `CommercialDocumentRepository` and `MfgOrderRepository`:

- S1 sweep sequence through the engine: sale valued at cost 10 (never
  the selling price 20); delivery costed −10 (was 0); return re-enters
  at the original 10; balance qty 6 / value **60**, and
  Σ `cost_amount` ≡ `stock_value` (the 70-vs-40 divergence is
  impossible when both reports read the engine's numbers); card
  `avg_cost` maintained by posting.
- S2 real CDR delivery note: movement `cost_amount` = −10; balance
  9 / 90, ledger agrees.
- S3 real MFG post: raw consumed at engine cost (balance 6 / 60),
  finished produced 2 / 40 (value conserved), journal Dr = Cr = 40
  from engine costs.
- S4 FIFO: issue of 12 costs 140 (oldest first); open layer 8 @ 20.
- S5 back-dated purchase + real `StockRecalculationService`: sale
  COGS restated 50 → 35, balance 15 @ 7 = 105, job recorded Done.
- Source checks: RptQuery throws/reports as designed; the engine is
  the only movement writer in the codebase.

### 8.2 Kern.Tests — `tests/Kern.Tests/ValuationEngineTests.cs` (9 facts)

Pure `ValuationMath` facts (weighted average + residue absorption,
FIFO order, LIFO order, deficit-layer settlement) and engine
integration facts on real migrated SQLite DBs (sweep sequence, FIFO
end-to-end, Standard-cost issue valuation, INV-6002 on disallowed
negative stock, back-dated purchase + recalculation). The test
project **compiles** here; execution is owed on Windows (net8.0-windows
testhost cannot run on this Linux VM).

### 8.3 What is NOT proven here

- **WPF runtime**: report windows rendering the new values/columns,
  and the RptBase error path in the live UI — owed on Windows.
- **Live PostgreSQL**: 053 PG migration, engine and recalculation
  against PG (the code is provider-parameterized and the PG SQL uses
  the same shapes as Steps 3–5; execution owed).
- Kern.Tests execution (see 8.2).

## 9. Gates

| Gate | Result |
|---|---|
| Full build `--no-incremental` | **0 errors, 0 warnings** |
| `tools/audit_window_context.py` | exit **0** |
| Lang parity | no Lang files touched; missing-key parity vs En = 0 in all catalogs (pre-existing Ar/Fr/Tr extras unchanged) |
| Errors catalogs | 4 × 194 entries, parity 0; INV-6002/6003/6004 added to all four + ErrorCodes.cs + ERROR_CATALOG.md |
| Arabic outside Ar.json | none in the diff (053 name_ar values are English per the 038 pattern) |
| Migrator | fresh + double-run + 059–061-fixture gates PASS (§8.1) |
| Harness | 18/18 PASS (§8.1) |

## 10. Comparison table (triggered: stock/profit report values and ItemProfitWin columns change)

Condensed from `~/workspace/research/sweep/compare-stock-reports.md`;
scope = only the axes Step 6 changes. (⚠ Al-Ameen/Phenix rows are
pattern-based per that audit — vendors publish no field-level docs.)

| Axis | Before Step 6 | After Step 6 | Al-Ameen V10 / Phenix v10 | ERPNext | Odoo |
|---|---|---|---|---|---|
| Stock value source | balance report: qty × avg; period report: Σ qty × recorded unit_cost — **disagreed (70 vs 40)** | ONE engine value: balance `stock_value` ≡ Σ movement `cost_amount` in every report | single inventory valuation per method | Stock Ledger Entry valuation rate; balance = Σ SLE value | AVCO/FIFO valuation layers, one value across reports |
| Valuation method | `cost_method` stored, honored by nothing | honored per material: WeightedAvg / FIFO / LIFO / Standard / LastPurchase | تقييم by method (weighted avg / FIFO) | per-item FIFO or Moving Average | per-category Standard / AVCO / FIFO |
| Outbound valuation | selling price (StkPost), 0 (chain DLV) | cost only, per method, from balance/queue | cost | valuation rate from ledger | cost from layers |
| Returns | re-enter at selling price | re-enter at the **original outbound cost** (linked rate) | original cost | linked against original SLE rate | original move value |
| Profit reports | line qty (chosen unit) × stale card `avg_cost`; returns ignored | −Σ movement `cost_amount` (base units, posting-time, per method); returns netted | COGS from inventory valuation | Gross Profit report from SLE/bin valuation | margin from move costs |
| Item profitability columns | Qty, Revenue, Tax, Total (no cost at all) | + Cost, Profit, Margin % with totals | material profits with cost + margin | Gross Profit per item with buying amount + margin | product margin on sales analysis |
| Back-dated documents | later movements keep wrong values forever | Recalculate Costs job replays in posting order and restates movements (GL not restated, F-2) | إعادة احتساب التكلفة reposting | Repost Item Valuation (automatic queue) | recompute via valuation layers |
| Report query failure | silent empty report | cataloged error (DB-1005) reported + surfaced in the window | error dialog | error/traceback surfaced | error dialog |

Remaining comparison gaps (not Step 6 scope): drill-down to source
document, group-by/subtotals, as-of balances beyond EndOfPeriod,
batch/serial dimensions (Step 14a).

## 11. Pillars checklists

### Stock valuation engine + migration 053
- UI: **GAP (flagged F-1)** — no new window; ItemProfitWin columns are
  the only UI change (below).
- DB: **PROVEN** — 053 both providers, idempotent, 059–061-safe
  (harness gates); backfills for legacy rows.
- Linking: **PROVEN** — every writer delegates (mechanical grep gate);
  movements carry document numbers; returns link via
  `original_doc_no` / document rate lookup.
- Posting: **PROVEN** — engine runs inside each writer's transaction;
  MFG GL and InvRepo COGS consume engine amounts (harness S3, tests).
- Persistence: **PROVEN** — movements, balances, layers, card averages
  and revaluation jobs all persisted transactionally; parameterized
  SQL throughout.
- Downstream: **PROVEN (code) / GAP (runtime)** — all stock & profit
  reports re-sourced to the engine's numbers (§7); live WPF rendering
  owed on Windows.

### Recalculate Costs (service)
- UI: **GAP (F-1)** — service only; `INV.VALUATION.RECALC` permission
  seeded for the future window.
- DB: **PROVEN** — job rows in `stock_revaluation_jobs`; movements
  restated with `repost_batch_id`.
- Linking: **PROVEN** — original-document rate resolution for returns;
  transfer pairing preserved.
- Posting: **GAP (F-2, by design)** — GL not restated; delta reported
  on the job.
- Persistence: **PROVEN** — replay is one transaction per company
  scope; job outcome recorded even on failure.
- Downstream: **PROVEN** — reports read the restated movements.

### ItemProfitWin (only materially changed window)
- UI: **PROVEN (code)** — Cost/Profit/MarginPct columns + totals added
  with existing Lang keys ×4 (no new keys); buttons remain in the
  standard RptBase bottom bar; shell context unchanged (audit exit 0).
- DB/linking/posting/persistence: **PROVEN** — reads the engine's
  movement costs via RptQuery; no writes.
- Downstream: n/a (terminal report).

### Report layer error surfacing (RptBase family)
- UI: **PROVEN (code) / GAP (runtime)** — failures show
  `Report.LoadFailed` instead of a fake empty grid; live check owed.
- DB/linking/posting/persistence: n/a (read path); errors persisted
  via the central log (LogServ) with catalog codes.

## 12. Owner flags

- **F-1 — Recalculate Costs + Valuation windows not built.** The
  design's Recalculate surface (and an InvValuationWin-style valuation
  viewer) are window builds; per the step boundary they are flagged,
  not built. The service, job table and `INV.VALUATION.*` permissions
  are in place for them.
- **F-2 — Recalculation does not restate GL.** Journal entries keep
  posted amounts; the recalc job reports the value delta. Decide
  whether a follow-up should post a valuation-adjustment journal.
- **F-3 — Chain SAL has no COGS leg.** `CommercialDocumentRepository`
  posts revenue/receivable for chain sales invoices; the delivery
  note's movements now carry true `cost_amount`, but posting chain
  COGS needs a document_links traversal + mapping decision (Step 3's
  mapping covers InvRepo kinds). Flagged, not improvised.
- **F-4 — Count behaviour change.** Zero-delta count lines no longer
  write zero movements, and shortages/surpluses are delta-only
  (previously full-qty). This is the correct stock semantics; noted
  because movement history shape changes.
- **F-5 — MFG finished value vs GL.** Finished stock enters at the
  order's computed unit cost (raw + overhead, Step 5 behaviour), while
  the completion journal carries the engine's raw consumption cost
  only — the overhead portion of finished value has no GL leg (Step 3
  F-3 residue).
- **F-6 — FIFO purchase returns.** A purchase return consumes the
  layer queue normally (FIFO/LIFO order) rather than removing the
  specific purchase layer the design sketches; the value effect is
  identical under WeightedAvg and near-identical under FIFO unless
  rates moved between purchase and return. Linked-layer removal can
  land with 054's allocation work.
- **F-7 — LIFO shipped.** Per V1's recommendation the engine supports
  LIFO and it stays visible, although IAS 2/IFRS prohibits it; the
  owner may still choose to hide it in the MatCard list.

## 13. Files changed

- Migration: `dba/mig/sqlite/053_valuation_engine.sql`,
  `dba/mig/postgresql/053_valuation_engine.sql` (new).
- Engine: `src/ShouTech.Infrastructure/ValuationMath.cs`,
  `StockValuationEngine.cs`, `StockRecalculationService.cs` (new).
- Writers: `src/ShouTech.Infrastructure/CommercialDocumentRepository.cs`,
  `MfgOrderRepository.cs`; `cod/app/src/Services/StkPost.cs`,
  `CardStore.cs`, `InvRepo.cs` (COGS + ordering).
- Reports: `cod/app/src/Services/RptQuery.cs`,
  `cod/app/src/Views/RptBase.cs`, `cod/app/src/Views/CustomReportsWin.cs`,
  `cod/app/src/Views/Reports/ItemProfitWin.cs`.
- Errors: `src/ShouTech.Domain/ErrorCodes.cs`,
  `cod/app/src/Resources/Errors/{En,Ar,Fr,Tr}.json`,
  `docs/ERROR_CATALOG.md`.
- Tests: `tests/Kern.Tests/ValuationEngineTests.cs` (new).
- Proof: `~/workspace/research/fix-step6/valuation_harness.py` +
  `harness/` (C# harness against the real repositories).

---

<!-- source: docs/FIX_PHASE_STEP7.md -->

# FIX PHASE — STEP 7: Base-Currency Amounts on Every Money Line

**Branch:** `fix/step7-base-currency` (worktree `~/workspace/projects/ShouTech-fix7`)
**Base:** tip of `fix/foundations-6-valuation` (`11d82b6`) — the foundations chain is sequential (steps 1–6).
**Date:** 2026-11-17 · **Status:** implemented, gated, NOT pushed, NOT merged.
**Evidence base:** `~/workspace/research/sweep/journal-coa.md` (J-5),
`~/workspace/research/sweep/invoicing-posting.md` (multi-currency MAJOR),
`~/workspace/research/sweep/cash-payments.md` (B1 cash BLOCKER, cheque BLOCKER, PayRec cross-currency MAJOR),
`~/workspace/research/acceptance-sweep-report.md`.

---

## 1. The defect, in one paragraph

Multi-currency was decorative (J-5). Journal headers stored `currency_code` +
`exchange_rate`, but `journal_entry_lines` had **no base-currency amounts**, no
report converted by rate, and the FX master table (`currency_rates`) was never
applied to any money anywhere. A USD entry at rate 3.75 posted and reported as
if 1 unit = 1 base unit — wrong money in every total. Document lines feeding
the GL and `party_ledger` had the same disease: a foreign-currency invoice
debited the party at face value, and `PartyCreditGuard` compared that raw
foreign sum against a base-currency credit limit. Cheques pre-multiplied their
amounts by the rate and posted them under a header claiming rate 1, conflating
transaction and base amounts in the same columns.

## 2. Design: dual amounts + one rate-resolution path

**Every money line that feeds the GL or a party balance now carries BOTH
amounts** (migration 062, §3): the transaction amount (unchanged columns) and
its base-currency counterpart (`base_debit` / `base_credit` = transaction ×
effective rate, rounded to 4dp AwayFromZero), plus the **effective rate
actually applied** stored on the line itself, so the rate source is auditable
per line. Journal headers additionally record `base_currency_code` — the base
currency the amounts are expressed in.

**Rate-source precedence** — enforced by the ONE resolver,
`FxRates.Resolve` / `ResolveByCompanyCode` (`src/ShouTech.Infrastructure/FxRates.cs`):

| Priority | Source | Rule |
|---|---|---|
| 1 | Base currency | Transaction currency == company base currency (`companies.currency_code`) → rate **1** |
| 2 | Document/entry rate | A header/document `exchange_rate` > 0 stored on the document → that rate (explicit) |
| 3 | Master | Stored rate ≤ 0 (absent) → `currency_rates`: latest rate on or before the entry date for the company (the master is keyed by company **code**, the namespace CurCard maintains; the Guid form is also consulted for journal-side callers), latest overall as fallback |
| 4 | — | No rate anywhere → **posting fails with ACC-5015 `ExchangeRateMissing`** (new catalog code, §6), inside the posting transaction, so nothing is written |

Two enabling changes make priority 3 reachable: the document repositories no
longer **fabricate** a rate at insert — `CommercialDocumentRepository` and
`OrderDocumentRepository` stored `draft.ExchangeRate <= 0 ? 1 : rate`, which
converted "no rate entered" into a fake explicit rate of 1 and made the master
table unreachable. They now store the rate as given (0 = absent). The journal
header written by `GlPosting` / `JournalRepository` stores the **effective**
(post-resolution) rate, so header, lines, and reports can never disagree about
which rate was applied.

**Balance rule (mixed-rate definition).** An entry carries ONE effective rate
(the journal model has no per-line rates), so a transaction-balanced entry is
base-balanced after conversion; `FxRates.ConvertBalanced` absorbs the 4dp
rounding residue on the largest line so the base sums are *exactly* equal.
`JournalRepository.PostAsync` additionally **validates the stored base sums**
and refuses a base-imbalanced entry with ACC-5001 — an entry whose lines were
converted at mixed rates that do not net (the only way to construct one is
direct data manipulation or a legacy partial write) cannot post even though it
balances in transaction currency. Legacy drafts whose lines lack base amounts
are healed from the header rate inside the posting transaction (the same
conversion the 062 backfill applies).

**COGS treatment (Step 6 boundary).** Stock values are base-currency already
(the valuation engine's `cost_amount`). `InvRepo.BuildGlLines` therefore
converts the COGS leg **from base to transaction** (÷ effective rate) before
handing it to GlPosting, whose uniform conversion brings its base amount back
to exactly the engine's cost. At rate 1 nothing changes; the revenue /
receivable / payable legs are transaction amounts throughout, as before.
Step 3's account mapping and Step 5's stored-totals logic are untouched —
this step consumes the stored totals + the stored rate, exactly as mandated.

## 3. Migration 062 — `062_base_currency_amounts.sql` (SQLite + PostgreSQL)

- `journal_entry_lines` gains `base_debit`, `base_credit`, `exchange_rate`
  (the effective rate applied to that line).
- `journal_entries` gains `base_currency_code`.
- `party_ledger` gains `base_debit`, `base_credit`, `exchange_rate`.
- **Backfill (idempotent — only rows whose base amounts are still NULL):**
  journal lines convert at their **header rate** (1.0 when the entry currency
  is the company's base currency); header `base_currency_code` comes from
  `companies.currency_code`; party_ledger rows convert at, in order, the rate
  of the journal entry they settle (migration 061 link), their source
  sales/purchase document's rate, the `currency_rates` master rate on or
  before the entry date, else 1.0. Historical reports therefore stay
  consistent with the entries as posted.
- Position: numbered 062 in step order (053 was Step 6's; 059–061 used).
  Guarded on both providers (SQLite `@if-column-missing`, PG
  `ADD COLUMN IF NOT EXISTS`); migrator double-run verified (§7).

## 4. Per-surface treatment

| Surface | Treatment |
|---|---|
| `JournalRepository` (JrnWin / CshWin manual entries + cash vouchers) | SaveDraft/UpdateDraft resolve the rate once and write dual amounts per line; PostAsync validates base balance (ACC-5001) + heals missing base; the voucher settlement written at posting inherits the voucher's currency + header rate via `PartyLedger.WriteEntry(exchangeRate:)` |
| `GlPosting` (Step 3's document→GL engine) | Resolves the effective rate before writing anything (ACC-5015 fails the whole document transaction); header stores effective rate + base currency; lines written with dual amounts (residue-adjusted); reversal mirrors the **stored** base amounts swapped — never a recomputation at today's rate — so a reversed pair nets to zero in base exactly |
| `InvRepo.Post` (InvoiceWin / POS) | Resolves the rate once at the top of the posting transaction; the credit guard is fed the **base** exposure; the party_ledger row and the collected-at-posting settlement carry dual amounts at the same rate; COGS legs converted per §2; the GL request still carries the stored document rate and GlPosting resolves identically |
| `CommercialDocumentRepository.PostSalesAsync` (chain SAL) | Same: FxRates resolution, guard fed base total, ledger row via `PartyLedger.WriteEntry` with dual amounts; stored-rate fabrication removed at draft insert |
| `OrderDocumentRepository.ConvertToInvoiceAsync` | Same treatment for the converted invoice's guard + ledger + GL |
| `PartyLedger.WriteEntry` (the shared writer) | Computes dual amounts from explicit base overrides (reversal mirrors) or by resolving the caller's document rate through FxRates; every caller now passes its rate: InvRepo settlement + unsettle-mirror (stored base swapped), chain, order conversion, journal settlements, DefinitionRepository installment collections (SAR plans resolve their SAR rate) |
| `PartyCreditGuard` | Sums `base_debit/base_credit` (COALESCE fallback to transaction amounts for pre-062 rows); callers pass base totals — a foreign document can no longer slip under a base-currency limit at face value |
| `ChequeService` | A batch whose cheques share one currency + rate posts as a proper transaction-currency entry (header = batch currency + rate, lines = raw amounts, base derived by JournalRepository); single-cheque Clear/Endorse entries likewise. Mixed-currency/rate batches keep the legacy pre-converted shape — see flag F-2 |
| Reports (`RptQuery`) | §5 |

Untouched, as mandated: account mapping (Step 3), document totals logic
(Step 5 — stored totals consumed as-is), valuation (Step 6), pricing and
document entry UX (no window gains or loses a field — see §8 comparison gate).

## 5. Report changes (`cod/app/src/Services/RptQuery.cs`)

All GL reports now total in **base currency** — they sum
`COALESCE(base_debit, debit)` / `COALESCE(base_credit, credit)` (the fallback
keeps any row the backfill could not reach honest instead of invisible):

- **Trial Balance, Balance Sheet, P&L** (the mandated trio), plus the rest of
  the line-based GL family for consistency: Account Statement, Journal List,
  Cost Center Report + Cost Center Trial, Account Movement Chart, Account
  Comparison, Cash Journal, Cash Position, Cash Flow Sections, Dormant
  Accounts, Bank Balances / Bank Accounts GL.
- **Party-balance reports** sum party_ledger base amounts: Customer Balances,
  Supplier Balances, Party Aging, Customer Due, Collection Rates.
- **Exchange Rate Differences** now reads the **stored** base amounts
  (BaseDebit = Σ base_debit; Difference = base − transaction) instead of
  re-deriving them from the header rate at report time; **Average Currency
  Rate** weights by base debit.
- **Transaction-currency views keep working**: document-list reports (sales /
  purchase invoice lists, price history, targets, sales-by-party, POS cash)
  still show document amounts in the document's own currency, and journal
  lines keep their transaction `debit/credit` columns — the FX-differences
  report shows both side by side. Allocation matching in PayRec
  (ReconciliationRepository) stays transaction-currency (same-currency
  matching); see flag F-3.

`journal_line_cost_splits` remain transaction-currency: their amounts are
written by `GlPosting.ApplyCostCenters` from transaction line amounts, and no
report totals splits directly (Cost Center Trial reads the lines, now in
base). Flagged as F-3 for a future conversion if a splits-based report appears.

## 6. New error code

**ACC-5015 `ExchangeRateMissing`** — registered in `ErrorCodes.cs`, all four
error catalogs (En/Ar/Fr/Tr, parity 0), and `docs/ERROR_CATALOG.md`. Thrown by
`FxRates` when a foreign-currency posting has no document rate and no master
rate; the posting transaction rolls back, so the document/entry stays Draft
and no GL or ledger row exists (proven §7d). No new Lang keys: no window's
visible surface changed.

## 7. Proof

### 7.1 Headless harness — `~/workspace/research/fix-step7/fx_harness.py` — **22/22 PASS** (plus 13/13 C# scenario checks)

Runs the **real SqlMigrator** and the **real** `JournalRepository` /
`GlPosting` / `PartyLedger` / `FxRates` (C# harness referencing the fix7
Infrastructure project) against migrated SQLite databases:

- Migration 062 applies on a fresh DB; double-run idempotent; all new columns present.
- **(a) J-5 case:** USD journal @ 3.75 → base total **375** (transaction 100); header `base_currency_code = SAR`; TB-style base sum = 375.
- **(b) Foreign sales invoice** (invoice-shaped GlPosting + shared party-ledger writer): receivable base = 200 × 3.75 = **750**; **COGS base = 80** (the engine's base cost, not 300); party_ledger base_debit = 750 with transaction debit 200.
- **(c) Mixed currencies:** USD @ 3.75 and EUR @ 4.2 entries each balance in base; an entry corrupted to balance in transaction but not in base is **refused at posting (ACC-5001)**.
- **(d) Missing rate:** foreign document, stored rate 0, no master → **ACC-5015, nothing written**; after seeding a `currency_rates` master row, the same document posts at the master rate (base 375, header rate stored as 3.75).
- **(e) Base-currency document:** SAR @ 1 → base == transaction, identical to before.
- **(f) Backfill:** pre-062 fixture (062 withheld, then applied) — foreign journal lines gain 375 @ 3.75, base-currency lines 50 @ 1.0, header base currency filled, party_ledger doc row gains 750 @ document rate, plain SAR row 40 @ 1.0; double-run idempotent and values stable.
- Reversal of the (a) entry mirrors stored base amounts: net base per account = 0.
- Source checks: TB/party reports sum base amounts; the document repositories no longer fabricate rate 1 at insert; InvRepo resolves through FxRates.

### 7.2 Kern.Tests — `tests/Kern.Tests/BaseCurrencyTests.cs` (7 facts)

Real C# paths against real migrated SQLite databases (company base = SAR):
USD journal base amounts + header base currency; foreign sales invoice GL +
party ledger in base with COGS base intact; mixed-currency base balance +
ACC-5001 refusal; missing-rate invoice → ACC-5015 with document left Draft and
zero GL/ledger rows; master-rate-only invoice posts at the master rate;
base-currency invoice identity; legacy draft healed at posting.
**Execution is owed on Windows** (net8.0-windows testhost cannot run on this
Linux VM) — the project compiles at 0 errors / 0 warnings here.

### 7.3 What is NOT proven here

WPF runtime behavior (posting from InvoiceWin/JrnWin/CshWin/ChqWin) and **live
PostgreSQL** execution of migration 062 and the new queries are owed on
Windows / a live PG instance — no PG server exists in this VM. The PG
migration mirrors the SQLite logic with standard idempotent constructs but
has not been executed. Build + SQLite harness success is not product
readiness, per the standing rule.

## 8. Comparison gate

JrnWin / InvoiceWin **visible behavior does not change**: no new fields, no
new columns, no moved buttons — this step is storage/posting/report-basis
only, so their comparison tables are **skipped with justification** per the
mandate's own condition. What a user *sees* change is report **values**:
GL and party-balance reports now total in base currency (§5), which is the
mandated correction itself — a USD 100 receipt @ 3.75 previously added 100
to every total and now adds 375. Document lists still show document-currency
amounts, unchanged.

## 9. Per-defect closure

| ID | Defect | Closure | Proof |
|---|---|---|---|
| J-5 (journal-coa) | Multi-currency decorative: no base amounts on lines, no report converts, master rates never applied | Migration 062 dual amounts + FxRates precedence + GL/party reports in base (§2–§5) | Harness (a)(c)(d)(e)(f); Kern.Tests `UsdJournal_…`, `MixedCurrencyEntries_…` |
| Invoicing multi-currency MAJOR | Currency free-text, `ExchangeRate` stays 1, ledger posts foreign GrandTotal as base, guard compares unconverted | InvRepo resolves the stored rate (or master, or ACC-5015), GL + ledger + guard all consume base at that rate; COGS kept at engine base cost | Harness (b)(d); Kern.Tests `ForeignSalesInvoice_…`, `ForeignInvoice_WithoutAnyRate_…`, `ForeignInvoice_WithMasterRateOnly_…` |
| Cash B1 (cash-payments) | Exchange rate never applied to money on cash vouchers (lines at face value, no base columns) | Vouchers post through JournalRepository, which now writes dual amounts at the header rate; settlements inherit it | Harness (a) path (same repository); code |
| Cheque BLOCKER (cash-payments) | Foreign cheques post at face value; header rate hardcoded 1 while lines pre-multiplied | Uniform batches post raw amounts under the batch's real currency + rate; single-cheque entries likewise (§4) | Code + build; mixed batches flagged F-2 |
| PayRec cross-currency MAJOR (partial) | Party balances sum mixed currencies raw, feeding decisions at face value | Party balances/aging/due/collection reports and the credit guard are base-consistent (§5). Allocation *matching* itself remains transaction-currency — F-3 | Harness (b) ledger base; code |

## 10. Pillars checklists (touched surfaces)

**Journal (JrnWin/CshWin path)** — UI: unchanged (PROVEN by diff: no window
files touched) · DB: 062 columns + backfill (PROVEN harness f) · Linking:
snapshot carries BaseDebit/BaseCredit/ExchangeRate; reversal mirrors stored
base (PROVEN harness) · Posting: dual amounts written at save/update, base
balance enforced at post (PROVEN a/c) · Persistence: base amounts round-trip
through FindByDocumentNumber (PROVEN Kern.Tests fact 1, owed on Windows) ·
Downstream: TB/BS/P&L + GL family in base (PROVEN harness a TB-style sum;
report SQL source-checked).

**Documents (InvoiceWin/chain/orders)** — UI: unchanged · DB: stored rate no
longer fabricated (0 = absent) · Linking: GL entry header stores effective
rate + base currency; source link unchanged (Step 3) · Posting: ACC-5015
gate, COGS base preserved (PROVEN b/d) · Persistence: party_ledger dual
amounts on every writer (PROVEN b) · Downstream: credit guard + party
reports in base (code + harness b).

**Cheques** — UI: unchanged · DB: no schema change (journal lines carry the
dual amounts) · Linking: batch entry currency/rate now truthful for uniform
batches · Posting: raw amounts + real rate (code) · Persistence: GAP —
mixed-currency batches keep the legacy shape (F-2) · Downstream: GL reports
in base include cheque entries at their stored base.

**Reports** — UI: unchanged shape · DB: read-only · Linking: n/a · Posting:
n/a · Persistence: n/a · Downstream: the reports ARE the downstream —
base-basis totals PROVEN by harness sums + source checks; WPF rendering
owed on Windows (§7.3).

## 11. Gates

| Gate | Result |
|---|---|
| Full solution build `--no-incremental` | **0 errors, 0 warnings** |
| `tools/audit_window_context.py` | **exit 0** |
| Lang parity (En/Ar/Fr/Tr, Lang + Errors catalogs) | **0 missing** in every language (1 new error code ×4; no new Lang keys) |
| Migrator idempotent double-run (SQLite) | PASS (harness, fresh + pre-062 fixture) |
| Arabic outside Ar.json | None added (Arabic only in the Ar Errors catalog entry for ACC-5015) |
| Headless harness | **22/22 PASS** + 13/13 C# scenarios (§7.1) |

## 12. Owner flags

- **F-1 — Stored rate 1 is an explicit rate (interpretation, STOP-flag).**
  Precedence rule 2 treats a stored document/entry rate of 1 as *entered*
  (parity), so the master is consulted only when the stored rate is absent
  (≤ 0). Consequence: an invoice typed in a window with **no rate field**
  (InvoiceWin — the sweep noted ExchangeRate "stays 1, ctor default") whose
  stored rate is the legacy default 1 posts at 1 even if a master rate
  exists. Making the master win over a stored 1 for such documents is a
  one-line precedence change in FxRates, but it would silently revalue
  every legacy foreign document whose 1 was deliberate — flagged for the
  owner's decision rather than guessed. The missing-rate failure
  (ACC-5015) applies exactly where no rate exists anywhere: stored ≤ 0
  and no master row.
- **F-2 — Mixed-currency cheque batches.** The journal model carries one
  currency + rate per entry, so a batch mixing currencies/rates cannot be
  represented truthfully in one entry; those batches keep the legacy
  pre-converted shape (lines in base, header rate 1). Splitting mixed
  batches into one entry per currency is a workflow change for ChqWin —
  owner decision.
- **F-3 — Cost splits + allocation matching stay transaction-currency.**
  `journal_line_cost_splits` amounts and PayRec allocation matching are
  transaction-currency by design (allocations match a payment to a document
  in the document's currency). The cash-payments sweep's cross-currency
  allocation defect (a SAR payment settling a USD invoice unit-for-unit)
  needs an allocation-policy decision (require same currency, or allocate
  at the payment's rate with a difference leg) — not made here; balances
  feeding credit decisions are base-consistent regardless.
- **F-4 — Base-currency source divergence (pre-existing).** The base
  currency of record for posting is `companies.currency_code`, but the
  schema default is `'USD'` (migration 001), app config defaults to SYP,
  UsrPref defaults to SAR, and CurCard reads its "base currency" from app
  config. This step does not reconcile those defaults — the owner should
  confirm `companies.currency_code` is maintained per company, since every
  base amount now depends on it.

---

<!-- source: docs/FIX_PHASE_STEP8.md -->

# FIX PHASE — STEP 8: Cheques + Bank Reconciliation

**Branch:** `fix/step8-cheques-bankrec` (worktree `~/workspace/projects/ShouTech-fix8`)
**Base:** tip of `fix/step7-base-currency` (`d6c95db`) — the chain carries Steps 1–7 (RBAC, COA unification, GlPosting, party unification, InvRepo correctness, valuation engine, dual base-currency amounts).
**Date:** 2026-11-17 · **Status:** implemented, gated, NOT pushed, NOT merged.
**Evidence base:** `~/workspace/research/sweep/cheques-bankrec.md`, `~/workspace/research/sweep/compare-cheques-bankrec.md`, `~/workspace/research/acceptance-sweep-report.md` (§7 cheques & bank rec).

---

## 1. The defects, in one paragraph

Issuing a cheque from the ribbon recorded a **receipt**: both cheque keys opened `ChqWin` in its Incoming default, so an outgoing cheque posted Dr Box / Cr Party — the exact inverse GL, silently (CHQ-01). A saved batch shared **one** journal entry, so bouncing or cancelling any single cheque reversed the whole batch's GL and permanently blocked its siblings (CHQ-02), and lifecycle GL/status writes were non-atomic, so a retry could double-post (CHQ-03). Outgoing clearance posted no GL at all (CHQ-04). BankRecWin's "Difference" was identically 0.00 **by construction** — no bank statement existed anywhere in the model to differ from the books (BNK-01). And the bank-account master existed twice: the live `banks` table (031) and the dead `bank_accounts` table (050), written only by caller-less code yet still offered as a DatWin export dataset (BNKC-01).

## 2. Direction inversion (CHQ-01) — routing + GL legs

`RptNav.TryHandle` now routes the keys separately:

| Ribbon key | Before | Now |
|---|---|---|
| `Bonds.ReceiptCheque` ("Incoming check") | `new ChqWin()` = Incoming | `OpenIncomingChequeVoucher` → `ChqWin(ChqKind.Incoming)` |
| `Bonds.PaymentCheque` ("Outgoing check") | `new ChqWin()` = Incoming (**wrong**) | `OpenOutgoingChequeVoucher` → `ChqWin(ChqKind.Outgoing)` |
| `Accounts.ChequeVoucher` / `Vouchers.ChequeTitle` (generic) | Incoming | Incoming (unchanged default) |

The generic `RptNav.Show` dedupes by window **type** only — the router mode-stickiness defect — so a visible Incoming window would have been re-activated for an Outgoing request. Cheques therefore open through a dedicated `ShowCheque(kind, owner)` that dedupes on **(type, kind)**: a visible window of the other kind is left alone and a new window of the requested kind opens.

GL legs per direction (through Step 3's GlPosting, Step 7's FX — §3), proven in the harness:

| Event | Incoming | Outgoing |
|---|---|---|
| Issue / receive | Dr Portfolio/Box, Cr Party (per cheque) | Dr Party, Cr Portfolio/Box (per cheque) |
| Clear | Dr Deposit Bank, Cr Portfolio | Dr Portfolio, **Cr Paying Bank** (new — CHQ-04) |
| Bounce / Cancel | Reverse own issue entry (or compensate, §3) | Reverse own issue entry (or compensate, §3) |

ChqWin's lifecycle buttons are now kind-gated (CHQ-08): Deposit/Endorse/Bounce are disabled for Outgoing, Cancel for Incoming.

## 3. Journal-per-cheque design (CHQ-02, CHQ-03, CHQ-04, CHQ-05, CHQ-07)

**One journal entry per cheque per event.** `ChequeService` no longer builds an aggregated batch entry through `SaveDraftAsync`/`PostAsync`; every event posts through `IJournalRepository.PostDocumentAsync` — Step 3's GlPosting engine — with the migration-060 source link pointing at the cheque itself:

| Source type | Event | Legs (transaction amounts; base derived per Step 7) |
|---|---|---|
| `CHQ-ISSUE` | Batch save, per cheque | IN: Dr Portfolio / Cr Party · OUT: Dr Party / Cr Portfolio |
| `CHQ-CLEAR` | Clear | IN: Dr Deposit Bank / Cr Portfolio · OUT: Dr Portfolio / Cr Paying Bank |
| `CHQ-BOUNCE` | Bounce compensation (shared legacy entries only, below) | Dr Party / Cr Portfolio |
| `CHQ-ENDORSE` | Endorse | Dr Endorsed-To / Cr Portfolio |
| `CHQ-CANCEL` | Cancel compensation (shared legacy entries only) | Dr Portfolio / Cr Party |
| `CHQ-FEE` | Bounce fees > 0 (CHQ-07: fees are now **posted**, not just recorded) | Dr Party / Cr Deposit Bank (or Portfolio when not deposited) |

Each entry carries the cheque's own currency + exchange rate; GlPosting/FxRates resolve and store the dual base amounts (migration 062) exactly as for every other document — no pre-multiplication, no rate-1 fabrication. The GlPosting duplicate guard (one live entry per source type + cheque id) makes every event idempotent per cheque. `cheques.journal_entry_id` now stores **that cheque's own** `CHQ-ISSUE` entry.

**Atomicity (CHQ-03).** A lifecycle transition first *claims* its new status through the repository's compare-and-swap update, then posts its GL; if the GL step fails, the claim is rolled back with a compensating CAS update (status and event fields restored) before the error propagates — a retry can never double-post, and a cheque is never stranded in a terminal status without its GL. If the rollback itself fails, the service throws an explicit "stranded — reconcile manually" error rather than swallowing either failure. Batch save posts each cheque's entry, then inserts the batch; any failure reverses every entry posted so far (the pre-existing compensation pattern, now per-cheque).

**Historical batch entries — honest treatment.** Pre-Step-8 cheques may still share one batch entry. That entry **cannot be split honestly** — its lines are aggregated totals with no per-cheque identity — so it is never fabricated into splits and never reversed for a single cheque. Instead, at bounce/cancel time the service counts the live cheques referencing the entry (`IChequeRepository.CountByJournalEntryAsync`):

- count ≤ 1 → the entry is effectively the cheque's own: reverse it outright;
- count > 1 → post a **per-cheque compensating entry** (`CHQ-BOUNCE` / `CHQ-CANCEL`) with the mirror legs for exactly that cheque's amount, leaving the shared entry posted and every sibling's GL exact. Each sibling later receives its own compensation the same way. The shared entry remains in the GL as the historical batch record; the sum of compensations plus the remaining live cheques always equals it.

**Also closed in this design:** the header voucher number is persisted on every cheque as `batch_doc_no` (migration 063; CHQ-05 — it was displayed but stored nowhere); outgoing cancellation records a stop-payment note (`cancel_reason`, 063) collected in both windows; the register gains the missing **Cancel** action; ChqListWin gains Branch / Currency / Issue-Date columns (CHQ-15) and its due-soon filter excludes terminal cheques (CHQ-16); the 1,000-row caps on the cheque register (CHQ-13) and the bank-rec book lines (BNK-03) are removed (standing unlimited-rows rule); ChqListWin and BankRecWin failures now report through the central mechanism with catalog code APP-2010 (CHQ-18 / BNK-07), following ChqWin's existing pattern.

**Not changed:** Step 3's account-mapping philosophy (the portfolio/box account remains the user-chosen account recorded on the cheque — no new designated under-collection/under-payment accounts were invented) and Step 7's FX conventions (consumed, including its F-1 stored-rate-1 interpretation for window-typed rates). POS/card payments are untouched — they are not cheques.

## 4. Real BankRec statement model (BNK-01, BNK-02)

**Migration 063** (both providers, idempotent) introduces the statement side as data:

- `bank_statements` — one statement per bank account: `account_code`, `statement_date`, `statement_ref`, `period_from/to`, `opening_balance`, **`closing_balance` (the balance as printed on the statement — bank data, independent of the books)**, `status` Open/Closed.
- `bank_statement_lines` — the statement's own movements (date, reference, description, signed amount: + into the account / − out of it), each optionally matched to one book line (`matched_journal_line_key`).
- `bank_reconciliation_marks` gains `statement_id` / `statement_line_id`, so a cleared mark records *which statement line* cleared the book line.

**The Difference is now a real quantity:**

```
Difference = closing_balance − opening_balance − Σ matched book lines
```

It is non-zero whenever the bank statement disagrees with the books, moves by exactly each matched amount as matching proceeds, and is legitimately zero only when the entire statement movement is matched. The old formula (`Book − Cleared − Uncleared ≡ 0`) is deleted, and the unit test that enshrined it now documents why. `OperationsService.ComputeTotals(bookLines, statement, statementLines)` computes both sides, including matched/unmatched statement totals; the window footer shows Book / Cleared / Uncleared / Statement closing / Difference plus unmatched counts on **both** sides.

**Matching rules:** one statement line ↔ one book line, **equal amounts only** — a mismatched or partial match would hide exactly the discrepancy the Difference exists to surface; a genuinely different bank amount stays unmatched until the book side is corrected or the missing entry (charges, interest) is posted and then matched. Matching and unmatching are single-transaction operations (statement-line link + mark upsert/delete together). A statement **closes only at Difference = 0** — otherwise `CloseBankStatementAsync` fails with the new catalog code **ACC-5016**; matching an already-matched line fails with **ACC-5017**. Closed statements are immutable (line add/remove/match/unmatch all refuse). The legacy bare Mark/Unmark (no statement line) remains available for ad-hoc clearing.

**Window (BankRecWin):** statement header fields (account, statement date/ref, opening + closing balances, Save Statement), a statement-lines grid with an entry row (date / ref / description / amount, Add / Remove), Match / Unmatch / Close Statement in the bottom command bar, and both grids visible together so unmatched items show on both sides. Statement lines are **entered** in the window; file import (CSV/OFX/CAMT) is **not** built — see flag F-1.

## 5. Banks / bank_accounts resolution (BNKC-01)

**Decision: `banks` (migration 031) is the one canonical bank-account store.** It is what BnkCard / `MasterCardService` / `MasterCardRepository` read and write, what BankRecWin's account picker reads (`banks.gl_account_code` first), and what migration 063 backfills. Following the Steps 2/4 pattern — one writer, one reader path, no silent dual-write:

- Migration 063 **copies** any live `bank_accounts` rows into `banks` (keyed by company + code, only where the code is not already present; the copy SQL is itself idempotent). In practice the table is empty everywhere: its only writers were `CardStore.LoadBank/SaveBank/SoftDeleteBank`, which had **zero callers** in the entire codebase.
- The CardStore bank path (`BankCardRec`, `LoadBank`, `SaveBank`, `SoftDeleteBank`, `BankBookBalance`) is **deleted**, with a comment recording the decision where it stood.
- DatWin's export catalogue no longer offers the always-empty **"BankAccounts"** dataset; bank accounts export through the canonical **"Banks"** dataset.
- The `bank_accounts` table itself is left in place as a legacy artifact (dropping it would break STEI packages that still carry it); no code reads or writes it.

BNKC-02 (bank balance reading only the canonical chart) was already resolved by Step 2's COA unification — `chart_of_accounts` is the only chart. BnkCard's remaining sweep items (delete reference guard BNKC-03, SWIFT/BIC, bank master split) are **not** in this step's mandate and are listed under flags.

## 6. Migration 063 — `063_cheques_bankrec.sql` (SQLite + PostgreSQL)

- `cheques`: `batch_doc_no`, `cancel_reason` (guarded column adds).
- `bank_statements`, `bank_statement_lines` (new tables + indexes).
- `bank_reconciliation_marks`: `statement_id`, `statement_line_id` (guarded).
- Canonical-banks copy from `bank_accounts` (§5).
- Idempotent on both providers (SQLite `@if-column-missing` directives / PG `ADD COLUMN IF NOT EXISTS`; tables `IF NOT EXISTS`; copy inserts only missing codes). Migrator double-run verified (§8).

## 7. Per-defect closure

| ID | Defect | Closure | Proof |
|---|---|---|---|
| CHQ-01 ★ | "Outgoing check" ribbon button opens an Incoming window; inverse GL posted silently | §2: keys split, outgoing constructs `ChqWin(ChqKind.Outgoing)`, kind-aware dedupe; GL legs per direction verified | Harness (a) source checks + outgoing issue/clear legs; Kern.Tests `Outgoing_IssueThenClear_…` |
| CHQ-02 ★ | Bouncing/cancelling ONE cheque reverses the WHOLE batch journal | §3: per-cheque entries; bounce/cancel reverse only the cheque's own entry; shared legacy entries compensated per-cheque, never reversed | Harness (b) + legacy case; Kern.Tests `BatchOfThree_…`, `LegacySharedBatchEntry_…` |
| CHQ-03 ★ | Lifecycle GL + status not atomic; retry double-posts / strands | §3: status-claim → GL → compensating rollback saga; GlPosting duplicate guard per (event, cheque) | Code + harness happy paths; fault-injection owed on Windows (flag F-4) |
| CHQ-04 | Outgoing clearance has no GL; box credited at issue with no bank leg | OUT Clear posts Dr Portfolio / Cr Paying Bank as the cheque's own `CHQ-CLEAR` entry; both windows collect the paying bank for OUT | Harness (a); Kern.Tests `Outgoing_IssueThenClear_…` |
| CHQ-05 | Header Voucher No is a dead field | Persisted as `cheques.batch_doc_no` (063) on every row of the batch | Migration + repository round-trip (harness schema checks; Kern fixture columns) |
| CHQ-07 | Bounce fees recorded, never posted | Fees > 0 post a `CHQ-FEE` entry Dr Party / Cr Bank-or-Portfolio in the bounce operation | Harness (b) exact balances incl. fee; Kern.Tests `BatchOfThree_…` |
| CHQ-08 | Lifecycle buttons not kind/status gated | ChqWin disables Deposit/Bounce/Endorse for Outgoing and Cancel for Incoming | Code (window diff) |
| CHQ-13 | Register capped at 1,000 cheques; totals over the truncated set | `LIMIT` removed from `ChequeRepository.GetAllAsync` | Code |
| CHQ-15 | Register omits Branch / Currency / Issue Date | Columns added to ChqListWin grid + row wrapper | Code (window diff) |
| CHQ-16 | Due-soon includes terminal cheques | Filter excludes Cleared/Bounced/Endorsed/Cancelled | Code (window diff) |
| CHQ-18 / BNK-07 | Register / BankRec errors bypass the central mechanism | Both windows report via APP-2010 + `AppHost.Logging` (ChqWin pattern) | Code (window diffs) |
| BNK-01 ★ | "Difference" ≡ 0.00 by construction; no statement balance exists | §4: statement model (063) + real Difference formula; close gated at zero (ACC-5016) | Harness (c) full lifecycle; Kern.Tests `BankRec_StatementDifference_…` |
| BNK-02 | No statement-side data, no matching, no partial/adjustment path | Statement lines entered + equal-amount matching/unmatching in-window; adjustment entries are posted as journals and then matched (the harness's 50-interest flow). Partial clearing remains unbuilt — flag F-2 | Harness (c); code |
| BNK-03 | Journal lines capped at 1,000; totals wrong beyond it | `LIMIT` removed from `OperationsRepository.GetBankJournalLinesAsync` | Code |
| BNKC-01 | Two competing bank tables; dead one leaks into exports | §5: `banks` canonical; 063 copy; CardStore path deleted; DatWin dataset removed | Harness banks-copy + source checks |

Out of scope and untouched, as mandated: CHQ-06 (source-voucher/invoice allocation link on cheques — the PayRec seam; a bounce still cannot re-open a settled invoice), CHQ-09/10/11/12 (grid parse behavior, drawer/beneficiary split, rate UI, print pagination), CHQ-17 (register prompt dialog styling), BNK-04/05/06, BNKC-03/04 — recorded as flags where relevant.

## 8. Proof

### 8.1 Headless harness — `~/workspace/research/fix-step8/step8_harness.py` — **18/18 PASS** (incl. 21 C# scenario checks)

Runs the **real SqlMigrator** and the **real** `ChequeService` / `JournalRepository` (GlPosting) / `OperationsService` (C# harness referencing the fix8 Infrastructure + Application projects) against migrated SQLite databases:

- **(e)** Migration 063 applies on a fresh DB; migrator double-run idempotent; statement tables + new columns present. Withholding 063, seeding a legacy `bank_accounts` row, then applying 063 copies it into `banks`; re-running the copy SQL does not duplicate it.
- **(a)** Source: `Bonds.ReceiptCheque` → incoming opener, `Bonds.PaymentCheque` → outgoing opener constructing `ChqWin(ChqKind.Outgoing)` with kind-aware dedupe. Runtime legs: outgoing issue Dr Party 400 / Cr Box 400 (the inverse of incoming); outgoing clear posts `CHQ-CLEAR` and credits the bank 400.
- **(b)** Batch of 3 (100/200/300): three **distinct** per-cheque entries; GL Dr Portfolio 600 / Cr Party 600. Bounce the 200 cheque with a 10 fee → exactly its entry reversed, siblings still `InPortfolio` and posted, fee posted as `CHQ-FEE`, balances exact (Portfolio Dr 390 / Party Cr 390); the second sibling then bounces independently (the old code threw forever). Legacy case: two cheques sharing one posted batch entry — bouncing one leaves the shared entry **posted**, posts a `CHQ-BOUNCE` compensation of exactly 200, sibling untouched.
- **(c)** BankRec: statement (opening −400 framing the period, closing 650; lines 600/400/50) → Difference **1050** unmatched; match 600 → **450**; match 400 → exactly the known **50** discrepancy; close refused with **ACC-5016**; post the missing 50 book entry, match it → Difference **0** legitimately, no unmatched statement lines; statement closes.
- **(d)** After every lifecycle above, the trial balance over all ever-posted entries (posted + reversed, so reversal pairs net to zero — the Step-3 harness's own definition) nets to exactly 0 across all accounts, and every per-account figure asserted in (a)/(b) is the register-consistent GL figure. Cheques settle through the GL party **control** accounts (they write no `party_ledger` rows — that ledger belongs to the Step-4 document/voucher settlement chain); the reconciliation proven here is register ↔ GL.

### 8.2 Kern.Tests — `tests/Kern.Tests/ChequeBankRecStep8Tests.cs` (5 facts)

Real C# paths against real migrated SQLite databases: batch-of-three bounce isolation with fees and exact balances; outgoing issue + clear bank credit; legacy shared-entry compensation; the full BankRec difference lifecycle incl. ACC-5016 and legitimate-zero close; migration-063 double-run idempotence. Existing fixtures were updated where Step 8 changed the world they simulate: `ChequeMfgServiceTests` (fake journal gains document postings; assertions moved to per-cheque entries; fixture gains the 063 columns) and `OperationsServiceTests` (the tautological `Difference == 0` assertion removed with a pointer here). **Execution is owed on Windows** (net8.0-windows testhost cannot run on this Linux VM) — the project compiles at 0 errors / 0 warnings here.

### 8.3 What is NOT proven here

WPF runtime behavior (ribbon clicks opening the correct cheque kind, BankRecWin matching gestures, ChqListWin Cancel prompt) and **live PostgreSQL** execution of migration 063 and the new queries are owed on Windows / a live PG instance — no PG server exists in this VM. The PG migration mirrors the SQLite logic with standard idempotent constructs but has not been executed. Build + SQLite harness success is not product readiness, per the standing rule.

## 9. Gates

| Gate | Result |
|---|---|
| Full solution build `--no-incremental` | **0 errors, 0 warnings** |
| `tools/audit_window_context.py` | **exit 0** |
| Lang parity (En/Ar/Fr/Tr, Lang + Errors catalogs) | **0 missing** in every language (18 new Lang keys + 2 new error codes, all ×4) |
| Migrator idempotent double-run (SQLite) | PASS (harness (e) + Kern.Tests) |
| Arabic outside Ar.json | None added (Arabic only in the Ar Lang/Errors catalog entries) |
| Headless harness | **18/18 PASS** (§8.1) |

## 10. Comparison tables (vs Al-Ameen V10 / Phenix v10 / ERPNext / Odoo)

Condensed from `~/workspace/research/sweep/compare-cheques-bankrec.md`, with the **Step 8** column recording exactly what this step changed. ⚠ = counterpart behavior asserted from the regional pattern in the sweep, not live-verified.

### 10.1 ChqWin (CORE ≈50% before)

| Capability | Before | Al-Ameen ⚠ / Phenix ⚠ | ERPNext / Odoo | **Step 8** |
|---|---|---|---|---|
| Outgoing document reachable from the ribbon | ✗ both keys opened Incoming | ✓ separate documents | ✓ Receive/Pay types | **✓** — PaymentCheque opens Outgoing; kind-aware dedupe |
| Per-cheque GL reversal (bounce/cancel one of a batch) | ✗ whole batch reversed | ✓ per-cheque | ✓ per payment | **✓** — one entry per cheque per event |
| Outgoing clearance GL (bank debited) | ✗ status only | ✓ Dr Under-Payment / Cr Bank | ✓ | **✓** — Dr Portfolio / Cr Paying Bank (portfolio account stands in for the under-payment leg; no new designated accounts invented) |
| Bounce fees posted | ✗ recorded only | ✓ Dr Party / Cr Bank-or-income | n/a core | **✓** — `CHQ-FEE` entry in the bounce operation |
| Saved voucher number | ✗ displayed, never stored | ✓ | ✓ | **✓** — `batch_doc_no` persisted per cheque |
| Stop-payment note on cancel | ✗ no input | ✓ | ✓ (void reason) | **✓** — `cancel_reason` collected in both windows |
| Party master lookup, invoice allocation link, cheque books, cheque-paper print, cost-center column, rate UI | ✗ | ✓ | ◐ | GAP — unchanged (CHQ-06/10/11 and §6 parity list; not in this step's mandate) |

### 10.2 ChqListWin (CORE ≈44% before)

| Capability | Before | Counterparts | **Step 8** |
|---|---|---|---|
| Cancel / stop issued cheque from the register | ✗ button absent | ✓ | **✓** — Cancel action with stop-note prompt |
| Branch / Currency / Issue Date columns | ✗ stored, not shown | ✓ | **✓** |
| Row cap | ✗ 1,000, totals over truncated set | unlimited/paged honestly | **✓** — cap removed |
| Due-soon excludes terminal cheques | ✗ | ✓ | **✓** |
| Errors via central catalog | ✗ raw MessageBox | — | **✓** — APP-2010 pattern |
| Search, date-range filter, batch deposit, per-currency totals, multi-page print, export | ✗ | ✓ | GAP — unchanged |

### 10.3 BankRecWin (CORE ≈20% before)

| Capability | Before | ERPNext / Odoo / SAP B1 | **Step 8** |
|---|---|---|---|
| Statement (closing) balance as data | ✗ no field existed | ✓ | **✓** — `bank_statements` (063) |
| Statement lines as data (manual entry) | ✗ no object existed | ✓ | **✓** — entered in-window; **import (CSV/OFX/CAMT) still GAP (F-1)** |
| Difference that can be non-zero | ✗ ≡ 0.00 by construction | ✓ must be 0 to validate | **✓** — closing − opening − matched; close gated at 0 (ACC-5016) |
| Two-sided manual match | ✗ checkbox on book line only | ✓ core gesture | **✓** — statement line ↔ book line, equal amounts, single transaction |
| Unmatched items on both sides | ✗ | ✓ | **✓** — both grids + unmatched counts/totals |
| Close / lock a statement | ✗ nothing ever closed | ✓ validate/finalize | **✓** — Closed statements immutable |
| Auto-match, split/merge, partial clearing, create-adjustment-from-rec, print/export rec report | ✗ | ✓ | GAP — adjustments are posted as journals then matched (proven flow); the rest unchanged (F-2) |

### 10.4 BnkCard (CORE ≈61% before)

| Capability | Before | Counterparts | **Step 8** |
|---|---|---|---|
| One canonical bank-account store | ✗ `banks` live + `bank_accounts` dead, both exported | ✓ one master | **✓** — `banks` only; legacy rows copied by 063; dead path + dataset deleted |
| Card fields / persistence / live GL balance | ✓ (cleanest card in the sweep) | ✓ | Unchanged |
| SWIFT/BIC, bank (institution) master split, account type, opening balance, default flag, delete reference guard, bank list window | ✗ | ✓ | GAP — unchanged (F-3) |

## 11. Pillars checklists (per touched window)

Legend: PROVEN = evidence in §8; GAP = explicitly not done.

### ChqWin
- **UI:** PROVEN (code) — kind-gated lifecycle buttons; header voucher number now meaningful. Runtime click-through owed on Windows (§8.3).
- **DB:** PROVEN — per-cheque rows carry own `journal_entry_id` + `batch_doc_no` (063); migrator double-run idempotent.
- **Linking:** PROVEN — every GL entry links back to its cheque (060 source columns, source = cheque id); ribbon routing fixed incl. kind-aware dedupe.
- **Posting:** PROVEN — per-direction legs (§2 table) through GlPosting with Step-7 base amounts; harness (a)/(b).
- **Persistence:** PROVEN — batch doc no + cancel reason round-trip through the repository; all prior fields unchanged.
- **Downstream:** PROVEN at data level — cheque entries are ordinary posted journals, so TB/BS/statement include them per-account exactly (harness (d)). GAP: live WPF report rendering owed on Windows.

### ChqListWin
- **UI:** PROVEN (code) — Cancel action added; Branch/Currency/Issue-Date columns; due-soon excludes terminal. **DB/Linking/Posting:** PROVEN — all actions delegate to the same ChequeService (per-cheque GL, §3). **Persistence:** PROVEN — register reads the uncapped repository query. **Downstream:** GAP — print remains the single-page register list; per-currency footer totals and export unchanged.

### BankRecWin
- **UI:** PROVEN (code) — statement header (opening/closing), statement-lines grid + entry row, Match/Unmatch/Close in the bottom bar; unmatched shown on both sides. Runtime gestures owed on Windows.
- **DB:** PROVEN — statements/lines/marks-with-link persist (063); repository round-trips in harness (c) + Kern.Tests.
- **Linking:** PROVEN — mark ↔ statement line ↔ book line chain is stored and reloadable; cheque clearing does **not** auto-mark here ( GAP — the sweep's cheque↔rec tie-in remains unbuilt, F-2).
- **Posting:** n/a (reconciliation posts nothing; adjustment journals are posted through the journal engine and then matched — proven flow).
- **Persistence:** PROVEN — every statement field and line round-trips; closed statements immutable.
- **Downstream:** GAP — no print/export of the reconciliation report (BNK-06, unchanged).

### BnkCard
- **UI:** unchanged. **DB:** PROVEN — the card's store (`banks`) is now the *only* bank store; 063 copies legacy rows in. **Linking:** PROVEN — BankRecWin's picker and the card read the same store. **Posting:** n/a. **Persistence:** PROVEN (pre-existing, re-verified by the copy harness). **Downstream:** PROVEN — DatWin exports bank accounts only via the canonical "Banks" dataset. GAP: delete reference guard (BNKC-03) unchanged.

## 12. Owner flags

- **F-1 — Statement file import NOT built (STOP/flag, per mandate).** The statement model + entered-lines matching are complete, but CSV/OFX/CAMT.053/MT940 import is a separate surface (format parsers, per-bank format settings, import preview/mapping) and was not ballooned into this step. BankRecWin works fully on entered lines today.
- **F-2 — BankRec depth gaps remain:** auto-match proposals, split/merge and partial clearing, create-adjustment-journal *from inside* the window (today: post the journal in JrnWin, then match it — the proven flow), print/export of the reconciliation report, and the cheque↔rec tie-in (clearing a cheque in ChqWin does not mark its line here). None blocks a correct manual reconciliation.
- **F-3 — BnkCard backlog unchanged:** SWIFT/BIC, a separate Bank (institution) master, account type, opening balance/date, Is-Default flag, bank list/search window, delete reference guard (BNKC-03). The store decision (§5) makes these safe to add later on the one canonical table.
- **F-4 — CHQ-03 fault-injection is code-proven, not runtime-proven.** The claim/rollback saga is exercised on happy paths by the harness; a mid-flight GL failure (e.g. period closed between claim and post) returning the cheque to its prior status should be confirmed on Windows, where the real period/fiscal fixtures exist.
- **F-5 — Reversal reporting observation (pre-existing, NOT cheque-specific; discovered while proving (d)).** GL reports (Trial Balance family, `RptQuery`) sum lines of entries with `status = 1` only. A reversal pair therefore contributes **only the reversal entry** to those reports: post-then-reverse nets to *minus* the original per account in the TB, not zero — for **every** reversal in the system (JrnWin, cash vouchers, manufacturing), not just cheques. The Step-3 harness's own net-zero check summed posted + reversed entries (the definition this step's proof (d) uses). Whether reports should include status-2 originals (pairs net to zero) is a reporting-semantics decision for the owner; changing it here would have altered Steps 2/3/7 verified behavior, so it is flagged, not changed.
- **F-6 — CHQ-06 seam remains:** cheques still carry no source-voucher/invoice allocation, so a bounce cannot re-open a settled invoice's outstanding balance. The per-cheque GL is now correct; the allocation link is the PayRec seam's next step.

## 13. Files changed

- `dba/mig/sqlite/063_cheques_bankrec.sql`, `dba/mig/postgresql/063_cheques_bankrec.sql` (new)
- `src/ShouTech.Domain/Entities/Cheque.cs` (BatchDocNo, CancelReason, ChequeSourceTypes), `Entities/Operations.cs` (BankStatement, BankStatementLine, mark statement link, real totals), `ErrorCodes.cs` (ACC-5016/5017), `Interfaces/IJournalRepository.cs` (PostDocumentAsync), `Interfaces/IChequeRepository.cs` (CountByJournalEntryAsync), `Interfaces/IOperationsRepository.cs` (statement contract)
- `src/ShouTech.Application/Services/ChequeService.cs` (per-cheque posting rewrite), `OperationsService.cs` (statement workflow + real ComputeTotals)
- `src/ShouTech.Infrastructure/ChequeRepository.cs` (063 columns, uncapped list, sharing count), `OperationsRepository.cs` (statement CRUD/matching, uncapped book lines)
- `cod/app/src/Services/RptNav.cs` (direction routing + kind-aware dedupe), `Services/CardStore.cs` (dead bank path removed), `Services/DataExportSvc.cs` (BankAccounts dataset removed), `Views/ChqWin.cs` (batch doc no, OUT clear bank, cancel note, kind gating), `Views/ChqListWin.cs` (Cancel action, columns, due-soon, central errors), `Views/BankRecWin.cs` (statement UI + matching)
- Lang catalogs ×4 (18 keys), Errors catalogs ×4 (ACC-5016/5017), `docs/ERROR_CATALOG.md`
- `tests/Kern.Tests/ChequeBankRecStep8Tests.cs` (new, 5 facts); fixture/double updates in `ChequeMfgServiceTests.cs`, `OperationsServiceTests.cs`, `UnitTest1.cs`
- `docs/FIX_PHASE_STEP8.md` (this file)

---

<!-- source: docs/FIX_PHASE_STEP9.md -->

# Fix Phase — Step 9: Order-Chain UI Wiring + DocSearch Open Fix

**Branch:** `fix/step9-order-chain` (worktree `~/workspace/projects/ShouTech-fix9`), based on
`b2b40da` (tip of `fix/step8-cheques-bankrec`; carries steps 1–8). **Not pushed, not merged.**
**Commits:** `774deef` (migration 064 + conversion stock/COGS + dual-key search) ·
`4109309` (InvRepo TRN/CNT routing, dual-key Load/Post/Save, InvoiceWin target field + loud
load failure, DocSearch open-by-id + quotation conversion, OrdWin fixes, Lang ×4) ·
`653b3ca` (credit-guard ordering + tests) · this doc.

**Scope delivered:** DocSearch opens the document it found, for every indexed kind; the
order → invoice conversion posts stock + COGS through the one valuation engine; InvoiceWin's
StockTransfer (TRN) and InventoryCount (CNT) modes post through their dedicated engines
(`StkPost.PostTransfer` / `StkPost.PostCount`) instead of refusing with ACC-5014; the
quotation → sales-order conversion is reachable from DocSearch. No new document kinds, no
posting-math changes (steps 3/5/6 own the math — step 9 only routes to it).

---

## 1. Root cause found beyond the sweep: the company-key split-brain

The sweep documented the symptoms; tracing them exposed a deeper cause the sweep did not
name. `sales_documents.company_id` / `purchase_documents.company_id` hold **two different
keys** depending on which family wrote the row:

| Writer family | `company_id` value |
|---|---|
| InvRepo / RptQuery (InvoiceWin native documents) | company **CODE** (e.g. `ACME`) |
| OrderDocumentRepository / CommercialDocumentRepository (orders, chain documents, converted invoices) | company **Guid string** |

Consequences, all reproduced before fixing:

- DocSearch searches through the Guid-keyed order repository → native InvoiceWin invoices
  were **invisible** to search.
- `InvRepo.Load` filtered by the code only → a Guid-keyed converted invoice loaded as
  "not found" → the window fell back to a blank document (the visible DS-02/I-8 symptom).

**Fix (no schema change):** both readers now match **either of the company's own keys** —
`OrderDocumentRepository.ListAsync` resolves the code from the Guid via `companies`;
InvRepo `Load` / `Post` (header + status update) / `SaveDraft` UPDATE accept either key via
`ResolveCompanyGuidOrNull` (resolution failure is reported through `AppHost.Errors` and
degrades to code-only matching, never silently). This is a bridge, not a cure: unifying the
key namespace is a data-migration project of its own (see Owner flags).

---

## 2. Per-defect closure (vs sweep IDs)

| ID | Defect | Closure | Proof |
|---|---|---|---|
| **ORD-01** (BLOCKER) | Order → invoice conversion produced a Posted invoice + party ledger + link with **zero stock movement and no COGS** | Sales conversion still issues stock through the single `StockValuationEngine` inside the invoice transaction, using the material cost method and posting Dr `401` / Cr `1203`. Purchasing is now deliberately separate: a posted PO must first create one or more RCV documents; each RCV atomically posts stock (stocked items) or expense (non-stock/service items) against GRNI `2203`. Supplier invoice creation is restricted to received, unbilled quantities, matches invoice lines FIFO to receipt lines, clears GRNI at receipt-date base cost, and posts price/FX variance to `402`. Purchase invoice posting never receives stock again. | Kern.Tests `PurchaseOrder_ReceiptThenInvoice_AccruesAndClearsGrniWithoutDoubleReceiving`, `DuplicateMaterialLines_DistributeReceiptAndInvoiceProgressBySourceLine`, `NonStockServiceReceipt_AccruesToExpenseWithoutCreatingInventory`, and `SupplierPaymentVoucher_ReconcilesAgainstGuidScopedPurchaseInvoice`. |
| **ORD-07 / I-8 / DS-02** | DocSearch Open discarded the selected id and opened a **blank** SalesInvoice | `DocSearchWin.OpenSelected` rewritten: orders → `OrdWin` + `LoadOrderAsync(row.DocId)`; SAL/PUR/QUO/SRT/PRT → `new InvoiceWin(row.DocId, kind)`, then the opener **verifies** `LoadedDocumentId == row.DocId` (mismatch → window closed, `NavigationFailed` reported, status shows `DocSearch.OpenUnsupported`). Unmapped kinds refuse honestly instead of falling through to a blank invoice. `InvoiceWin(long, kind)` now **throws** when the document cannot be loaded — a blank masquerading as an open is impossible. | Harness (a): FindById returns the stored order + converted invoice (id + totals); source-chain checks (no `new InvoiceWin()` in DocSearchWin). Kern.Tests `Load_FindsGuidKeyedDocument_SoDocSearchOpensIt`. |
| **DS-02** | Quotation → Sales Order conversion existed service-side with **no UI caller** | New **Convert to Order** button in DocSearchWin (Quotations mode only), gated by `PermCat.Sales.QuotationConvert`, calling `AppHost.Orders.ConvertQuotationToOrderAsync` → creates a DRAFT sales order with the QUO→SO link. Errors reported via `AppHost.Errors` (the window previously swallowed them / showed raw text). Lang keys `DocSearch.ConvertToOrder`, `DocSearch.ConvertedToOrder`, `DocSearch.OpenUnsupported` ×4. | Harness (d): QUO → linked DRAFT SO, total carried. Kern.Tests `Quotation_ConvertToOrder_CreatesLinkedSalesOrderDraft`. |
| **I-6 / Step-5 F-1** | InvoiceWin StockTransfer mode wired to `InvRepo.Post`, which fabricated stock; step 5 made it refuse (ACC-5014) | `InvRepo.Post` routes TRN at the top to `PostDedicatedEngineDocument`: claim (guarded Draft→Posted) → `StkPost.PostTransfer` (two-sided, value-conserving, FIFO layers move) → compensate back to Draft on engine failure (explicit stranded-state error if the revert itself fails — the step-8 cheque saga pattern). Source = `WarehouseId`, target = the new persisted `TargetWarehouseId`, reason = Notes ?? DocNo; line quantities restated to base units via the new public `StkPost.ToBaseUnits`. The engine's `TRF-…` number is written to `journal_doc_no`. `InvPostingPolicy`: TRN → `None` (no generic effects, not blocked). | Kern.Tests `StockTransfer_PostsTwoSided_AndConservesValue` (997/3, Σ stock_value = 40,000, no ledger/GL rows). Harness (c) engine composition. |
| **I-7 / Step-5 F-1** | InventoryCount added the **full counted quantity** instead of the delta | Same routing: CNT → `StkPost.PostCount` (delta-only by construction). Counted-qty-0 lines are included (a real zero count must zero the balance). ACC-5014 no longer exists for either kind (survives only in historical comments). | Kern.Tests `InventoryCount_PostsDeltaOnly_NotFullCountedQty` (count 1003 vs book 1000 → movement +3). Harness (c): count 10 vs book 7 → +3. |
| **ORD-03** | OrdWin Close checked the **sales** cancel permission for purchase orders too | Close permission is now kind-conditional (sales vs purchase family). | Code-traced; OrdWin diff in `4109309`. |
| **ORD-10** | OrdWin drafts carried a warehouse **name** with `WarehouseId: null` on header and lines | `BuildDraft` resolves the warehouse id via `CardStore.ResolveWarehouseId` and applies it to the header and every line. | Code-traced; conversion warehouse fallback chain now starts from a real id. |
| **ORD-13** (partial) | Silent catches in the touched windows | DocSearchWin (`OnLoaded`/`Search`/`SetLost`/`Convert`) and OrdWin (`OnLoaded`/`RefreshProgressAsync`) now report through `AppHost.Errors` with catalog codes. | Diff review; audit exit 0. |
| — | Step-7 convention drift in OrdWin | Hardwired `ExchangeRate = 1m` → `0m` (0 = no document rate, step-7 convention). | Diff review. |

**Deliberately not fixed here:** ORD-02 (see Owner flags), ORD-05 (absent feature, flagged),
DS-01 (raw kind/status codes in the DocSearch grid — display-only, flagged).

---

## 3. Wiring map (entry point → handler → engine)

| Entry | Handler | Engine / repository | Effects |
|---|---|---|---|
| DocSearch **Open** (order row) | `DocSearchWin.OpenSelected` | `OrdWin.LoadOrderAsync(row.DocId)` → `AppHost.Orders` → `OrderDocumentRepository.FindByIdAsync` | Window shows the stored order; shell context via BaseWin/ShellContext; `Owner = this` |
| DocSearch **Open** (SAL/PUR/QUO/SRT/PRT row) | `DocSearchWin.OpenSelected` | `new InvoiceWin(row.DocId, kind)` → `InvRepo.Load` (dual-key) → `ApplyDoc`; opener verifies `LoadedDocumentId` | The stored document, or a loud refusal — never a blank |
| DocSearch **Convert to Order** (quotations) | `DocSearchWin.ConvertToOrder` (perm `Sales.QuotationConvert`) | `AppHost.Orders.ConvertQuotationToOrderAsync` → `SalesChainService` | DRAFT sales order + QUO→SO link in `document_links` |
| OrdWin **Convert to Invoice** | `OrdWin` convert action → `AppHost.Orders.ConvertToInvoiceAsync` | `OrderDocumentRepository.ConvertToInvoiceAsync` (one transaction): remaining-qty invoice (step-5 stored totals) → credit guard ACC-5006 → `StockValuationEngine` movements (CODE-keyed) → `GlPosting` (revenue/purchases + COGS 401/1203) → party ledger → link | Invoice Posted; order billed quantities/status update from links; progress truthful incl. partial |
| InvoiceWin **Post** (TRN) | `InvoiceWin.PostDoc` → `InvRepo.Post` | claim → `StkPost.PostTransfer` → engine out/in at identical value; `TRF-…` no. linked | Stock moves source → target; value conserved; no GL, no party ledger |
| InvoiceWin **Post** (CNT) | `InvoiceWin.PostDoc` → `InvRepo.Post` | claim → `StkPost.PostCount` (delta-only) | Balance adjusted by counted − book only |
| InvoiceWin **Post** (all other kinds) | unchanged | step-5 path (stored totals + step-3 GL + step-6 stock via `StkPost.ApplyDocument`, which still early-returns TRN/CNT as defense in depth) | unchanged |

All opened windows keep the standing conventions: `Owner` set, no Topmost, shell context
(company/language/theme/currency) applied centrally at open.

---

## 4. Migration 064 — justification

`InvDoc.TargetWarehouseId` existed in the model with **no column**: a transfer's
destination could not persist, so TRN could never post honestly. 064 adds
`target_warehouse_id INTEGER` + `target_warehouse_name TEXT` to `sales_documents` and
`purchase_documents` (SQLite + PostgreSQL, guarded/idempotent). That is the only schema
change in this step, and it exists solely because the wiring gap was a missing column.

---

## 5. Comparison tables (condensed from the sweep compare files, with step-9 deltas)

### 5.1 OrdWin — orders (source: `compare-orders-projects-assets.md` §1)

| Capability | Al-Ameen V10 ⚠ | Phenix v10 ⚠ | ERPNext | Odoo | ShouTech after step 9 |
|---|---|---|---|---|---|
| Convert → Invoice | ✓ executed-vs-remaining | ✓ | Create ▸ Invoice | Create Invoice (invoiceable lines) | ✓ **fixed**: remaining-qty only, **now with stock at engine cost + full GL incl. COGS** (was: ledger only) |
| Partial conversion | ✓ editable row qty | ✓ | qty editable on generated doc | invoiceable-lines wizard | ◐ repository bills all *remaining* qty (partial across conversions proven); **no quantity-picker UI** (flag) |
| Convert → Delivery/Receipt | ✓ | ✓ | Create ▸ Delivery Note / Purchase Receipt | picking on confirm | ✗ **absent feature** (ORD-05, flagged — service has no UI caller for DLV) |
| Post permission | ✓ roles | ✓ | Submit (workflow) | Confirm (To Approve option) | ✗ Post still has **no permission check** (ORD-02, flagged — no ORDER.POST code exists in the catalog) |
| Close permission | ✓ | ✓ | ✓ | ✓ | ✓ **fixed**: kind-correct family (ORD-03) |
| Warehouse on lines | ✓ per line | ✓ | ✓ per row | per picking | ✓ **fixed**: real `WarehouseId` resolved onto header + lines (ORD-10); per-line warehouse *column* still not editable in the grid |
| Currency / FX | ✓ | ✓ | ✓ rate | ✓ | ◐ rate follows step-7 convention (0 = none); no FX control on the window (ORD-14, not this step) |
| Fulfilled / Billed progress | ✓ | ✓ | ✓ two axes | ✓ | ✓ Billed axis truthful end-to-end after step 9; Fulfilled axis still dead (depends on ORD-05) |
| Payment terms / price list / doc discount | ✓ | ✓ | ✓ | ✓ | ✗ unchanged (CORE-missing list §1.7 of the compare file; not step-9 scope) |

### 5.2 InvoiceWin — invoicing (source: `compare-invoicing.md` §1)

| Capability | Al-Ameen V10 ⚠ | Phenix v10 ⚠ | ERPNext | Odoo | ShouTech after step 9 |
|---|---|---|---|---|---|
| Post → GL + stock + ledger | ✓ | ✓ | ✓ Submit | ✓ Confirm | ✓ (steps 3/5/6; unchanged here) |
| Stock Transfer document | ✓ two-sided transfer | ✓ | Stock Entry (Transfer) | Internal transfer picking | ✓ **fixed (F-1)**: TRN mode posts via `PostTransfer` — two-sided, value-conserving, destination persisted (064); ACC-5014 refusal gone |
| Inventory Count document | ✓ count + delta adjustment | ✓ | Stock Reconciliation | Inventory adjustment | ✓ **fixed (F-1)**: CNT mode posts via `PostCount` — **delta only** (was: full counted qty fabricated; then refused) |
| Open existing document by id | ✓ | ✓ | ✓ | ✓ | ✓ **fixed**: `(docId, kind)` constructor loads from DB and **throws** if absent; openers verify `LoadedDocumentId` |
| Warehouse per line | ✓ lookup | ✓ | ✓ per row | via moves | ~ name resolved to id at capture (unchanged); TRN target is now a first-class persisted field |
| Due date / terms / price list / batch-serial columns | ✓ | ✓ | ✓ | ✓ | ✗ unchanged (compare file §1 CORE-missing; batch/serial belongs to the Phase B 054 design, not this step) |

### 5.3 DocSearchWin — document finder (source: `compare-orders-projects-assets.md` §6)

| Capability | ERPNext List/Global Search | Odoo List + Search | Al-Ameen / Phenix ⚠ | ShouTech after step 9 |
|---|---|---|---|---|
| Open the found document | ✓ | ✓ | ✓ | ✓ **fixed for every indexed kind** (was: orders only; invoices/quotations opened blank) |
| Convert Quotation → Order | Create ▸ Sales Order | Confirm | ✓ convert | ✓ **fixed**: Convert to Order button (quotations mode, permission-gated, expiry enforced service-side) |
| Families covered | every DocType | every model | per-family browsers | ✓ invoices / quotations / orders, both sides — **and search now sees both company-key families** (native invoices were invisible) |
| Kind/status display | translated pill | translated | localized | ✗ still raw codes (DS-01, flagged) |
| Print / export of results | ✓ | ✓ | ✓ reprint | ✗ unchanged (flagged in compare §6.3; not step-9 scope) |
| Outstanding / progress columns | ✓ | ✓ residual | ✓ remaining | ✗ unchanged (data exists via `GetProgressAsync`; display not wired) |
| Set Lost | Lost Reason captured | — | — | ◐ unchanged: still no lost-reason capture (flagged) |

---

## 6. Pillars checklists (COMPLETE-PILLARS rule: PROVEN or GAP)

### OrdWin
| Pillar | Verdict | Evidence |
|---|---|---|
| UI | PROVEN (existing) + step-9 fixes code-traced | Close permission family, warehouse resolution, rate convention; WPF execution owed on Windows |
| DB | PROVEN | Orders/lines persist via OrderDocumentRepository (steps 1–8); conversion writes invoice + link in one transaction — harness (b) |
| Linking | PROVEN | QUO→SO and SO→SAL links in `document_links`; progress computed from links — harness (b)(d) |
| Posting | PROVEN | Conversion: GL balanced incl. COGS, stock at engine cost, party ledger, credit guard before effects — harness (b) |
| Persistence | PROVEN | Every converted field lands in stored rows (invoice header/lines/totals read back by FindById) — harness (a)(b) |
| Downstream | GAP (flagged) | RptQuery reports filter by company CODE only → Guid-keyed converted invoices are missing from sales/purchase reports (report family's step) |

### InvoiceWin
| Pillar | Verdict | Evidence |
|---|---|---|
| UI | PROVEN (existing) + target-warehouse field code-traced | TRN-only target field (existing Lang keys); loud load failure; WPF execution owed |
| DB | PROVEN | Migration 064 persists the transfer target; SaveDraft/Load round-trip asserted in Kern.Tests TRN fact |
| Linking | PROVEN | Opened by id from DocSearch with `LoadedDocumentId` verification; engine TRF number linked into `journal_doc_no` |
| Posting | PROVEN (repository level) | TRN two-sided + value-conserved, CNT delta-only — Kern.Tests facts + harness (c) composition |
| Persistence | PROVEN | Target warehouse survives save/load; status claim/compensation keeps Draft on engine failure |
| Downstream | GAP (minor, flagged) | CNT movements carry the engine-internal `CNT-…` number, not the CNT document's number (reason text carries the DocNo when Notes is empty) |

### DocSearchWin
| Pillar | Verdict | Evidence |
|---|---|---|
| UI | PROVEN (existing) + Convert button code-traced | Quotations-mode button, permission-gated; WPF execution owed |
| DB | PROVEN | Search reads both company-key families — harness (a) |
| Linking | PROVEN | Open-by-id for all indexed kinds with verification; QUO→SO link written — harness (a)(d) |
| Posting | n/a (finder) — opens/post actions delegate to the document windows' proven paths | — |
| Persistence | PROVEN | No window state; Set Lost writes through the chain repository (unchanged) |
| Downstream | GAP (flagged) | DS-01 raw codes in grid; no print/export; no outstanding/progress columns (compare §6.3) |

---

## 7. Proof

**Headless harness** — `~/workspace/research/fix-step9/step9_harness.py` (+ `harness/Program.cs`),
real SqlMigrator + real repositories/services on SQLite: **20/20 PASS**

- Migrator: fresh-DB apply incl. 064, double-run idempotent, 064 columns on both tables,
  064 recorded in `schema_migrations`.
- (a) DocSearch load path: order by stored id → that order (id + total 75); converted
  invoice by stored id → that invoice (75, Posted); search matches the company-CODE key
  (native row found) and returns both key families.
- (b) Conversion e2e: invoice carries 2 lines / total 160; stock deducted at engine cost
  (96/960, 48/960 — not selling price); GL balanced, Dr 401 COGS = 80 / Cr 1203 = 80,
  Dr 1201 = 160 / Cr 301 = 160; progress 100%; re-conversion refused; movements carry the
  invoice number. Partial: pre-billed 4/10 via the chain path (no stock), conversion bills
  only the remaining 6 (150), stock moves for 6 only, progress 40% → 100%. Purchase:
  receipt at line cost (105/1060), Dr 402 / Cr 2201, no COGS.
- (c) TRN/CNT engine composition exactly as `StkPost.PostTransfer`/`PostCount` perform it:
  count 10 vs book 7 → movement **+3 only**; transfer 3 → source 10→7, destination 0→3,
  total value conserved (100).
- (d) QUO → linked DRAFT sales order via `OrderService` (total 50).
- Source-chain assertions (WPF layer): DocSearch opens by id with no blank fallback and
  verifies `LoadedDocumentId`; `InvRepo.Post` routes TRN/CNT to
  `PostDedicatedEngineDocument` → `StkPost.PostTransfer` / `StkPost.PostCount`; the policy
  no longer blocks either kind.

**Kern.Tests (execution owed on Windows — net8.0-windows testhost cannot run here):**
new `OrderChainStep9Tests` (5 facts mirroring the harness scenarios),
`InvPostCorrectnessTests` (TRN two-sided/value-conserved fact, CNT delta-only fact,
dual-key Load fact, policy matrix updated), `OrderServiceTests` fixture extended with the
stock world; `PostingCompletenessTests` credit-guard fact protected by the guard-before-stock
ordering. Full solution compiles 0/0 with all of them.

**Stated plainly:** WPF window execution (DocSearch/OrdWin/InvoiceWin interaction) is
**owed on Windows**. Everything below the window layer is proven above.

---

## 8. Gates

| Gate | Result |
|---|---|
| Full build `--no-incremental` | **0 errors, 0 warnings** |
| `tools/audit_window_context.py` | **exit 0** (71 windows, 0 window-level direction pins) |
| Lang parity (`tools/verify_english_source.py`) | **exit 0 — missing = 0 in all 4 catalogs**; step 9 added exactly 3 keys ×4 (`DocSearch.ConvertToOrder`, `DocSearch.ConvertedToOrder`, `DocSearch.OpenUnsupported`) |
| Migrator idempotent double-run (incl. 064) | PASS (harness) |
| No Arabic outside Ar.json | PASS — the only Arabic added is inside `Ar.json` (the 3 keys) |
| PostgreSQL | 064 PG counterpart written (`ADD COLUMN IF NOT EXISTS`); live PG run still owed (no PG server on this machine — same standing caveat as steps 5–8) |

Pre-existing Lang *extras* (Ar +114, Fr +9, Tr +9 keys with no En counterpart) exist at the
base commit and are unchanged by this step; the project gate counts them as non-failing.

---

## 9. Owner flags (decisions / follow-ups for Man)

1. **ORD-02 — OrdWin Post has no permission check.** No `SALES.ORDER.POST` /
   `PURCH.ORDER.POST` code exists in the permission catalog; inventing one is step-1 RBAC's
   call, not step 9's. Decide: add the codes (then wiring is one line) or accept unprivileged Post.
2. **ORD-05 — Delivery/Receipt UI is an entirely absent feature.** SO→DLV / PO→RCV has a
   service (`SalesChainService.CreateFromSourceAsync`) with no UI caller for DLV and no
   window. Flagged per mandate, not built. Until it exists, the Fulfilled axis and
   per-order delivery status stay dead, and ShouTech's chain remains
   Quotation → Order → Invoice → Payment (counterparts all have the delivery step).
3. **Partial conversion has no quantity picker.** The repository truthfully bills all
   *remaining* quantities per conversion (proven), but the user cannot choose "bill 3 of
   the remaining 6 now" — counterparts expose editable quantities on the generated invoice.
   A picker belongs with the ORD-05 window work.
4. **QUO→SO from an InvoiceWin-native quotation fails honestly.** A quotation written by
   InvoiceWin is company-CODE-keyed; the chain repository parses `company_id` as a Guid and
   the DocSearch Convert button reports the failure instead of converting. Full fix =
   company-key namespace unification (the §1 bridge made reads tolerant; writes/conversions
   still assume one key per family).
5. **Converted invoices are missing from sales/purchase reports.** RptQuery filters by
   company CODE only; converted (Guid-keyed) invoices don't appear. Same root cause as
   flag 4; the report family's step should adopt the dual-key match.
6. **CNT document number.** Count movements carry the engine-internal `CNT-…` number; the
   CNT document's own number appears only in the reason text (when Notes is empty).
   Cosmetic traceability gap.
7. **DS-01 — DocSearch grid still shows raw kind/status codes** in all four languages.
   Display-only; a localization pass over the grid was out of this step's wiring scope.
8. **Lang extras** (Ar +114 / Fr +9 / Tr +9 vs En) pre-date this step — a catalog cleanup
   decision, untouched here.

---

<!-- source: docs/FIX_PHASE_STEP10.md -->

# Fix Phase — Step 10: Data Exchange (DatWin / STEI / Excel) Safety + Catalog Repair

Branch: `fix/step10-data-exchange` (based on `fix/step9-order-chain` @ `520d78c`).
Evidence: `~/workspace/research/sweep/data-exchange.md` (DX-01…DX-18),
`~/workspace/research/acceptance-sweep-report.md` §11,
`~/workspace/research/sweep/compare-data-exchange.md`.

**Scope honored:** the STEI container format/encryption (`SteiCrypto`,
manifest, signing) is untouched — this step governs its inputs and outputs.
DatWin's window shape is untouched; the only visible behavior change is one
new denial message on import (see "Comparison gate" below).

---

## 1. Per-defect closure (sweep IDs)

| ID | Severity | Status | How |
|---|---|---|---|
| DX-01 | BLOCKER | **CLOSED** | Dataset export is company-scoped end to end (§2). Proven per dataset by harness scenario (a): zero company-B rows in company A's exports, code-keyed and Guid-keyed tables alike. |
| DX-02 | BLOCKER | **CLOSED** | Catalog reconciled against the real post-Step-2/4/8 schema: 61 → 37 datasets, every one resolving to a real company-scoped table (§5). Validity proven programmatically (harness (d) + Kern.Tests fact). |
| DX-03 | MAJOR | **CLOSED** | `DATA.IMPORT` is now enforced in the service layer for STEI package import, STEI dataset import, Excel import and encrypted-workbook import (§3). Denial = catalog error `DATA-4004` + security-audit row. |
| DX-04 | MAJOR | **CLOSED** | Excel imports can no longer overwrite live balances (§4): balance columns are stripped and reported on master datasets; balance-table and document datasets are refused from spreadsheets outright. |
| DX-05 | MAJOR | **CLOSED (by catalog repair)** | The dead datasets behind the always-empty exports were removed; every remaining dataset maps to a live table. |
| DX-06 | MAJOR | **CLOSED** | Every import writes a `DATA_IMPORT` security-audit row (who/when/what/counts, rejected balance columns, skipped legacy tables); denials write `DATA_IMPORT_DENIED`. (Before this step, `DATA_IMPORT` events did not exist; only exports were audited, by Step 1.) |
| DX-07 | MAJOR | Open (owner flag) | Self-referential package signing is a format property of the proven STEI engine; out of the mandated boundary ("format/encryption not redesigned"). |
| DX-08 | MAJOR | Partially mitigated | Full-package export now also scopes child tables through their parents (§2), removing one class of cross-company import failures. The company-remap rule itself (code-vs-Guid fragility, `SourceCompanyCode` carrying a Guid) is engine behavior — flagged, not redesigned. |
| DX-09 | MINOR | Open (owner flag) | `BkpServ` still passes the hardcoded company `"001"` to full export/import. Left untouched: backup/restore is governed by `BACKUP.RESTORE`, and changing the company argument changes backup semantics beyond this step's mandate. |
| DX-10 | MAJOR | **CLOSED** | STEI import now repairs PostgreSQL sequences (`setval` past the max imported id for every imported table with a `nextval` identity default) inside the import transaction; failures are reported through `AppHost.Errors` without rolling the import back. |
| DX-11 | MAJOR | Open (owner flag) | Workbooks carry no installation marker; adding one is a workbook-format change. Mitigated indirectly: imports are permission-gated, company-forced per row, and audited. |
| DX-12 | MINOR | Open | BLOB columns are still skipped by the Excel reader (base64 round trip not implemented). |
| DX-13 | MAJOR | **CLOSED (by catalog repair)** | The non-round-tripping phantom datasets (valuation/batches/serials/HR/vouchers/…) were removed from the catalog; every remaining dataset round-trips through its real table. |
| DX-14 | MINOR | Open | Export of an empty dataset is still blocked by design in the window; unchanged. |
| DX-15 | MINOR | Partially closed | The duplicate SystemInfo column-header logic was left as is; the export model now uses the live UI direction (DX-17) and per-dataset package names are unique (DX-18). |
| DX-16 | MINOR | Partially closed | DatWin's stale comment claiming permission enforcement in the catalog was corrected; the window now pre-checks `DATA.IMPORT` before opening the file dialog, mirroring the existing `DATA.EXPORT` pattern (service layer remains the real gate). |
| DX-17 | MINOR | **CLOSED** | `DataExportSvc` no longer hardcodes `Rtl = true`; the export model uses `LangDirSv.IsRtl(App.CurrentLanguageCode)`. |
| DX-18 | MINOR | **CLOSED** | Dataset package file names are unique per dataset (`stei-dataset-<key>.stei`) instead of one shared name. |
| Step-2 flag F-3 | — | **CLOSED** | Old packages can no longer resurrect dropped legacy tables (§3.3): `accounts`, `customers`, `suppliers`, `bank_accounts` found in an incoming package are skipped — not created, not counted as failures — named in the result message and in the `DATA_IMPORT` audit row. Full-package export no longer offers them either. Proven by harness scenario (e). |

---

## 2. Company-scoping mechanism (mandate 1)

The schema has a dual company-key reality (documented by Steps 2/4/9): most
tables key rows by the company **code** (`A01`), while five FK-declaring
tables key by the company's **internal Guid** (`chart_of_accounts`,
`fiscal_years`, `brands`, `product_categories`, `units_of_measure`).
Scoping therefore always matches **the company's own two keys — code OR
Guid — and never another company's**.

### 2.1 Dataset exports (`DataExportSvc.BuildScopedExportQueryAsync`)

Every dataset export is built as a parameterized query:

- **Company-keyed tables:** `WHERE CAST(company_id AS TEXT) IN (@scopeCode,
  @scopeGuid)` (one parameter when the keys coincide). CAST normalizes Guid
  storage differences between SQLite (TEXT) and PostgreSQL (uuid) — the same
  idiom the STEI engine already uses.
- **Party split:** `Customers`/`Suppliers` read the one canonical `parties`
  table with an added `kind = 'C' / 'S'` predicate; `CustomerGroups`/
  `SupplierGroups` do the same over `party_groups`. A customer export can
  therefore never contain a supplier (or the other company).
- **Document kinds:** `Sales`/`SalesReturns`/`Purchases`/`PurchaseReturns`
  read `sales_documents`/`purchase_documents` with the `doc_kind` sets the
  document layer itself uses (`SAL,POS` / `SRT` / `PUR` / `PRT`).
- **Line tables** (no company column of their own) are scoped through their
  parent: `doc_id IN (SELECT doc_id FROM sales_documents WHERE …)` (and the
  journal equivalent).
- **Companies table:** the `Company` dataset exports exactly the active
  company's row (`code = @scopeCode`).
- **Fail-closed:** a dataset whose table has no company key and no scoped
  parent join throws instead of exporting unfiltered. The old
  export-everything fallback (and the import-side company-filter fallback in
  the STEI engine) were already removed by earlier steps; this step removes
  the last unfiltered read (`SELECT *` in `DataExportSvc`).

### 2.2 STEI full-package exports (`SteiSvc.ReadRowsAsync`)

Tables with a `company_id` keep the engine's existing dual-key filter.
This step adds the missing half: **company-less child tables are scoped
through their parents** (`BuildChildScopeWhereAsync`) — for every
single-column foreign key whose parent table carries `company_id`, a row
travels only when its parent belongs to the active company. Before this,
child tables traveled whole: another company's document/journal lines leaked
into the package and then failed their foreign keys on import (their parents
are company-filtered out), rolling the entire import back. Genuinely global
tables (no company-scoped parent, e.g. `permission_groups`) still travel
whole; identity tables (`users`, `roles`, `user_roles`, …) remain excluded
as before.

### 2.3 Imports

Imports were already company-forcing per row (the importer stamps the target
company key onto every row, in the correct code-vs-Guid shape — the shape map
was corrected for the five Guid-keyed tables, which previously received the
company *code* in a Guid foreign-key column). Rows whose own `doc_kind` /
party `kind` contradicts the chosen dataset are now refused per row instead
of being imported under the wrong dataset.

---

## 3. Import gating + audit (mandate 2)

### 3.1 The gate

New `Services/DataExchangeGuard.cs`, following the Step-1 pattern for
destructive operations:

- `RequireImportPermission(operation)` checks
  `AppHost.Permissions.Has(PermCat.Data.Import)` (`DATA.IMPORT`).
- On denial it reports catalog error **`DATA-4004`** through
  `AppHost.Errors`, writes a **`DATA_IMPORT_DENIED`** security-audit row
  (best-effort, never masking the denial), and throws `AppError(DATA-4004)`.
- The gate lives in the **service layer**, at the entry of every import
  entry point: `SteiSvc.ImportAsync`, `SteiSvc.ImportDatasetAsync`,
  `DataExportSvc.ImportExcelAsync`, `DataExportSvc.ImportEncryptedAsync`.
  DatWin additionally pre-checks before its file dialog (mirroring its
  existing `DATA.EXPORT` pattern) and shows the localized denial.
- Backup **restore** (`BkpServ.RestoreBackupAsync`) deliberately bypasses
  the data-exchange gate via the internal `ImportForRestoreAsync`: it stays
  governed by `BACKUP.RESTORE`, the stricter, purpose-built permission
  Step 1 wired. Simulations (dry runs) are not gated — they write nothing.

### 3.2 The audit

Every completed import writes one **`DATA_IMPORT`** row to
`security_audit_log` through the existing `SecurityAuditLog.WriteAsync`:
acting user, company, timestamp, and a details string carrying the scope
(full package / dataset key), source company, inserted / skipped-duplicate /
failed counts, skipped legacy tables, and rejected balance columns.
Audit-write failures are reported through `AppHost.Errors` and never fail
the import itself.

### 3.3 Legacy-table governance

`DataExchangeGuard.LegacyDroppedTables` = `accounts`, `customers`,
`suppliers`, `bank_accounts` (dropped by migrations 059/061, or retired by
Step 8). During package/dataset import these tables are partitioned out
**before** table creation: their rows are skipped (counted separately, not
as failures), the tables are never recreated, and the skip is named in the
import result message and the audit details. Full-package export skips them
too, so a package produced by the current build never carries them.

---

## 4. Excel balance-import rule (mandate 3)

**Chosen rule: reject — never adjust silently.** Balances and stock
quantities change only through posted movements (the Step-6 valuation
engine). A spreadsheet is master data, not a posting channel. The rule is
enforced per dataset policy (`DatasetImportPolicy` on every catalog entry):

| Policy | Datasets | Excel rule |
|---|---|---|
| **Master** | Products, Accounts, Customers, Suppliers, groups, Warehouses, Currencies, SalesReps, Brands, Categories, Units, Banks, CostCenters, Projects, FiscalYears | Imported (upsert or Replace). **Balance columns are stripped per row, never written**, collected into `ExcelImportResult.RejectedBalanceColumns`, surfaced in the result's skipped-columns list as `(balance - not imported)`, and named in the import audit. Stripped columns: `materials.qty_on_hand`, `materials.avg_cost` (derived caches owned by the valuation engine), `sales_rep_targets.actual_sales`, `sales_rep_targets.commission_earned` (derived actuals). (`chart_of_accounts` needs no entry: migration 059 already dropped its cached balance columns — the sweep's `accounts.balance` case is gone with the table.) |
| **EngineOwned** | OpeningStock (`material_stock_balances`), CostLayers (`stock_cost_layers`) | **Refused** with catalog error **`DATA-4005`**. These tables *are* balances/layers; there is no master part to import. Opening balances enter through the opening-stock posting flow, layers through goods receipts. |
| **PackageOnly** | Company, Sales/SalesReturns/Purchases/PurchaseReturns + their lines, StockMovements, ProductionOrders(+Lines), JournalEntries(+Lines), Cheques | **Refused** with catalog error **`DATA-4006`**. Documents and journals are ledger/stock facts; they exchange only as signed, encrypted STEI dataset packages (which re-validate on import), never as raw spreadsheet rows. Replace mode is likewise Master-only. |

The alternative (applying imported balances as adjustment movements through
the valuation engine) was considered and **not** chosen: a bulk spreadsheet
is not a stock-count document, and fabricating movements from it would post
unverifiable quantities into the ledger. If the owner wants spreadsheet
opening balances, the correct vehicle is the opening-stock/count flow, not
the dataset importer. **Owner flag:** confirm this rule.

---

## 5. Catalog reconciliation (mandate 4)

61 seeds → **37 datasets + SystemInfo**. Every kept dataset resolves to a
real table with a real company key (proven programmatically, §7d).

### Kept unchanged (13)
Company (`companies`), Products (`materials`), Accounts
(`chart_of_accounts`), Warehouses, Currencies, SalesReps, SalesRepTargets,
Banks (`banks`), CostCenters, Projects, FiscalYears, StockMovements
(`material_stock_movements`), Units (`units_of_measure`).

### Fixed mappings (12)
| Dataset | Was | Now |
|---|---|---|
| Customers | `customers` (dropped 061) | `parties` kind `C` |
| Suppliers | `suppliers` (dropped 061) | `parties` kind `S` |
| CustomerGroups | `customer_groups` (never existed) | `party_groups` kind `C` |
| SupplierGroups | `supplier_groups` (never existed) | `party_groups` kind `S` |
| Sales | `commercial_documents` (retired Step 9) | `sales_documents` kinds SAL,POS |
| SalesLines | `commercial_document_lines` (retired) | `sales_document_lines` via parent |
| SalesReturns | `commercial_documents` | `sales_documents` kind SRT |
| Purchases | `commercial_documents` | `purchase_documents` kind PUR |
| PurchaseLines | `commercial_document_lines` | `purchase_document_lines` via parent |
| PurchaseReturns | `commercial_documents` | `purchase_documents` kind PRT |
| OpeningStock | `material_balances` (never existed) | `material_stock_balances` |
| CostLayers | `stock_layers` (never existed) | `stock_cost_layers` |

### Added (3) — closing the acceptance report's CORE gap
JournalEntries (`journal_entries`), JournalEntryLines
(`journal_entry_lines` via parent), Cheques (`cheques`). Journal entries —
the system's core ledger fact — previously had **no** dataset at all.

### Removed (27) — tables that do not exist in the current schema
Branches, AccountTypes, ProductClassAttributes, ProductUnits,
CustomerContacts, CustomerAddresses, SupplierContacts, Employees,
Departments, JobTitles, WarehouseLocations, WarehouseZones, WarehouseTypes,
TaxCodes, StockAdjustments, StockAdjustmentLines, StockReservations,
Batches, SerialNumbers, ValuationMethods, ProductValuation, ValuationHistory,
WorkCenters, ReceiptVouchers, PaymentVouchers, PosSessions. (BankAccounts was
already removed by Step 8.) Each was a seed pointing at a table no migration
creates — the datasets could never appear or work (DX-02/DX-05/DX-13). If
HR/batches/valuation modules land later, their datasets return with real
tables. The Arabic catalog's dataset names were pruned to match.

---

## 6. Pillars checklist (data-exchange surface)

| Pillar | Verdict | Evidence |
|---|---|---|
| UI | PROVEN (unchanged by design) | DatWin shape untouched (owner's rebuild); one new localized denial message (`DataExchange.ImportNotPermitted` ×4 languages). Window-context audit exit 0. |
| DB | PROVEN | No new migration (none needed — catalog/gate/audit reuse existing tables). Migrator idempotent double-run in the harness (3 fresh DBs + re-run). |
| Linking | PROVEN | Catalog datasets resolve to the canonical Step-2/4/8 tables (parties, chart_of_accounts, banks, sales/purchase documents); line datasets link through their parents on export and import. |
| Posting | PROVEN (by refusal) | The importer posts nothing: documents/journals/balances are refused from spreadsheets (DATA-4005/4006) so no unposted or double-posted facts can enter; STEI package import remains insert-only inside one transaction (rollback on any failure — harness-verified). |
| Persistence | PROVEN | Headless harness against the real service code: exports, imports, audit rows, balance strip, legacy skip all asserted in SQLite (§7). |
| Downstream | PROVEN | Import audit rows land in `security_audit_log` (the Activity Log surface); rejected balance columns surface in the import result and audit details; skipped legacy tables surface in the result message. |

## 7. Proof — headless harness (SQLite, real service code)

Harness: `~/workspace/research/fix-step10/` — compiles the **real**
`Backup.cs`, `DataExportSvc.cs`, `DataExchangeGuard.cs`, `XlsxWrt.cs` and the
real Domain project (only platform shims: DPAPI pass-through, a fixed
harness encryption key, SQLite-only runtime). **14/14 checks PASS:**

- **(a)** Two companies (code-keyed + Guid-keyed rows, kind-split parties,
  documents, journals, balances): exporting **every** dataset for company A
  contains **zero** company-B marker rows; per-dataset counts asserted
  (Customers=1/Suppliers=1 kind split, lines scoped via parents, Company=1).
- **(b)** Import without `DATA.IMPORT` → refused with **DATA-4004**, nothing
  written, `DATA_IMPORT_DENIED` audit row present. With permission → import
  succeeds and a `DATA_IMPORT` audit row carries `dataset=Products` and the
  row counts.
- **(c)** Balance workbook (`qty_on_hand=9999`, `avg_cost=999`) → master
  fields imported, live balances **unchanged (50 / 10)**, both columns
  reported rejected and named in the audit. OpeningStock → **DATA-4005**,
  balances untouched. Sales → **DATA-4006**, nothing written.
- **(d)** Programmatic catalog validity: exactly 37 seeds; every seed's
  table exists in the migrated schema and is company-scoped; legacy tables
  absent from schema and catalog.
- **(e)** STEI round trip on the current schema: full package DB1→DB2 lands
  company A's rows (material, Guid-keyed brand, sales doc + line), **zero**
  company-B rows, import audited; dataset package round trip into DB3
  carries A's material only. A hand-built legacy package containing the
  dropped `accounts` table imports successfully, does **not** recreate
  `accounts`, still imports its live table, and names the skip in the
  result message and the audit row.
- **(f)** No migration added; migrator double-run idempotent (verified in
  the harness across three fresh databases).

Kern.Tests: `DataExchangeStep10Tests` (7 facts mirroring the harness at the
service layer) + the pre-existing `SteiRoundTripTests` updated to grant the
session the import permission. **Test execution is owed on Windows**
(net8.0-windows test host, DPAPI) — the tests compile clean; the harness
above is the executed proof on this machine.

## 8. Gates

| Gate | Result |
|---|---|
| Full build `dotnet build ShouTech.sln --no-incremental` | **0 errors, 0 warnings** |
| `tools/audit_window_context.py` | **exit 0** |
| Lang parity | **missing = 0 in all four catalogs**; new key `DataExchange.ImportNotPermitted` present ×4; error catalogs En/Ar/Fr/Tr at 61 entries each. (`verify_english_source.py` exits 1 on pre-existing Ar/Fr/Tr *extra* keys — identical profile on the base branch; this step adds no extras and reduces Ar extras 114 → 90 by pruning dead dataset names.) |
| Migrator idempotent double-run | PASS (harness, 3 DBs) |
| Harness scenarios (a)–(e) | **14/14 PASS** |

## 9. Comparison gate

DatWin is **not** materially redesigned: no layout, shape, or workflow
change. The only visible behavior change is that an import attempted without
`DATA.IMPORT` now shows a localized denial message (and imports that would
previously have silently overwritten balances or failed against phantom
tables now either work against real tables or are refused with a catalog
message). Per the task's condition, the comparison table from
`compare-data-exchange.md` is therefore not reproduced; the CORE-parity gaps
it lists that this step closes are: journal-entries dataset missing (added),
phantom HR/manufacturing/valuation datasets (removed as non-existent).

## 10. Owner flags / owed verification

1. **Balance rule = reject** (§4) — confirm, or direct an opening-stock
   import flow instead.
2. **Documents via STEI packages only** (DATA-4006) — spreadsheet document
   import is refused by design.
3. DX-07 (self-referential signing), DX-09 (`BkpServ` hardcoded `"001"`),
   DX-11 (no installation marker in workbooks), DX-12 (BLOB columns),
   DX-14 (empty-dataset export) remain open — each needs a format or
   product decision beyond this step's boundaries.
4. **Owed:** WPF run of DatWin (denial message + import flows) and the
   Kern.Tests suite on Windows; live PostgreSQL pass of the scoped exports
   and the sequence repair (SQL is provider-shared and CAST-based; the
   SQLite path is harness-proven).
5. Known pre-existing constraint surfaced by the harness: the Excel upsert
   evaluates NOT NULL columns on the proposed row before conflict routing
   (both providers), so hand-made sparse workbooks must include NOT NULL
   columns — real exports always carry all columns, so round trips are
   unaffected.

---

<!-- source: docs/FIX_PHASE_STEP11.md -->

# FIX PHASE — STEP 11: Router mode-stickiness + MFG report crashes + MFG unit equivalents

Branch `fix/step11-router-mfg` on top of `fix/step10-data-exchange`
(`49217ae`). **Not pushed, not merged** — awaiting the owner's call.

Commits:

| Commit | Content |
|---|---|
| `385a29c` | One `UnitEquivalents` converter in Infrastructure; chain/order repositories delegate to it |
| `02fb1d3` | MFG quantities cross into stock in base units (consumption, finished receipt, cancel symmetry) — MFG-2 |
| `f6824f0` | MFG variance/feasibility reports re-sourced to the migration-034 schema; feasibility Required in base units — MFG-1/MFG-4 |
| `5eecd1c` | Mode-aware router activation: `WindowActivator` + `IWindowMode` on all mode-ful windows; DatWin cache and ComWin scan mode-aware — S-2/S-3 |
| `2ec368c` | Harness-caught fix: tuple element shadowing `object.GetType()` in `WindowActivator.Show` |
| `0f86a4a` | Kern.Tests: `RouterMfgStep11Tests` (8 facts) |

No migration was added (none was needed — every fix works against the
schema that exists). No Lang keys were added (no new user-visible
strings; mode keys are internal identifiers), so Lang parity is
unchanged at 0 missing / 0 extra in all four catalogs, and no Lang or
Errors file differs from the base branch.

---

## 1. Router mode-stickiness (sweep S-2, S-3; Step-8 cheque pattern generalized)

### The policy (one, applied everywhere)

**The requested mode wins.** A window's identity for activation is
**(runtime type, ModeKey)**:

- a visible window of the same type **and the same mode** → reactivate
  it (restore from minimized + `Activate()`); the newly constructed
  instance is never shown;
- only a **different-mode** window of that type is open → open a
  **second instance** in the requested mode, side by side. An open
  window is never re-purposed to another mode: it holds per-mode /
  per-document state (a half-edited sales invoice must not become a
  purchase invoice; a cheque book must not flip direction);
- single-mode windows do not implement `IWindowMode` — both keys are
  null, the comparison degrades to the historical type-only behaviour,
  untouched.

This is exactly the rule Step 8 applied locally to cheques
(kind-aware dedupe); Step 11 deletes that local copy and makes the
rule the router's only rule. It also matches the professional ERPs:
ERPNext/Odoo open a second form for a second document type rather
than mutating the open one.

### Mechanism (router layer only — no per-call-site hacks)

- `cod/app/src/Services/WindowActivator.cs` (new):
  `IWindowMode { string ModeKey { get; } }`; `WindowModePolicy` — the
  pure decision core (`ModeKeyOf`, `IsMatch`, `Decide` →
  `ReactivateExisting | OpenNew`), no WPF state, unit-testable;
  `WindowActivator.Show(window, owner)` — scans visible windows,
  decides, activates or shows; failures are reported through
  `AppHost.Errors.Report(ErrorCodes.NavigationFailed, …)` and the
  window is still shown (a scan failure must never swallow a
  navigation request). `WindowActivator.TryActivateExisting<T>(modeKey)`
  for cached-window call sites.
- `RptNav.Show` and `RibbonNav.Show` now delegate to
  `WindowActivator.Show`. `RptNav`'s Step-8 `ShowCheque` kind-aware
  dedupe is deleted — cheques flow through the general policy via
  `ChqWin.ModeKey` with identical behaviour. `RibbonNav.ShowDef` goes
  through the same `Show` (`DefManagerWin.ModeKey == SetKey`, so two
  different definition sets can be open at once; the same set
  reactivates — the behaviour `ShowDef` already had, now central).
- `RibbonNav`'s silent catches are gone (sweep S-7, router part):
  `Show`'s empty `catch {}` is replaced by the activator's reported
  path; `LockWorkStation`'s silent catch now reports
  `ErrorCodes.ShellOperationFailed`. (S-7's non-router silent catches
  — e.g. inside DatWin/Backup — were already handled in Step 10 or
  remain outside this step's mandate; see §5.)
- `MWin.OpenDataWindow(bool exportMode)` (S-3): the cached `_datWindow`
  is reactivated **only when its current `ModeKey` matches the
  requested mode** (`StructuredExport` for export, `Import`
  otherwise); otherwise an already-open same-mode instance is
  activated via `TryActivateExisting<DatWin>`, else a new instance is
  created (the stale-mode cached window stays open in its own mode —
  DatWin's in-window mode cards remain the way to flip a live
  instance deliberately, which is a user action, not a router
  substitution). `MainShVM.OpenCompanyWindow`'s existing-window scan
  now also requires `Mode == ComWinMode.Management`, so a visible
  Startup-mode `ComWin` can no longer satisfy a Management request.

### Mode-ful window inventory (every window the routers open in >1 mode)

| Window | Mode source | ModeKey | Notes |
|---|---|---|---|
| `InvoiceWin` | `InvDocKind` (9 kinds) + optional loaded document | `{Kind}:{docId\|"new"}` | A window showing saved doc 5 ≠ a new-doc window of the same kind |
| `CshWin` | `CshKind` Receipt/Payment | kind | `Kind` accessor added |
| `OrdWin` | `OrdKind` Sales/Purchase | kind | `Kind` accessor added |
| `ChqWin` | `ChqKind` Incoming/Outgoing | kind | Step-8 local dedupe retired into the general policy |
| `MfgVarianceWin` | string mode Total/Raw/Overhead/Finished | mode | |
| `ClosingAccountsWin` | string mode Operations/Trading | mode | |
| `DocSearchWin` | `DocSearchMode` Invoices/Quotations/Orders | mode | |
| `GroupManagerWin` | `GroupKind` Party/SalesRep | kind | `Kind` accessor added |
| `SupportWin` | `SupportWinMode` Support/Suggestion | mode | `Mode` accessor added |
| `DefManagerWin` | string set key | set key | Already mode-correct pre-step; now via the central policy |
| `DatWin` | `ExchangeMode` Import/Export/StructuredExport (ctor bool + live mode cards) | **current** mode (tracks live card switches) | MWin cache fixed per S-3 (above) |
| `ComWin` | `ComWinMode` Startup/Management | mode | MainShVM scan fixed (above) |
| `PrmWin` | target user id (ctor string) | user id | Two users' permission windows can coexist; same user reactivates |

Everything else the routers open is single-mode and untouched.
`WhsWin.OpenStockCount(bool)` is a modal inline dialog, not
router-deduped, so it is not part of this inventory. Windows opened
outside the routers (e.g. `DocSearchWin` opening a document window
directly) construct exactly the document requested and are unaffected.

### Harness-caught defect in this step's own code

The first harness run failed every reactivation scenario while the
pure policy checks passed: in `WindowActivator.Show` the open-window
projection's second tuple element was name-inferred from
`w.GetType()`, so `o.GetType()` in the decision calls invoked
`object.GetType()` **on the tuple** (the `ValueTuple` runtime type)
instead of the window's type — reactivation could never match. Fixed
in `2ec368c` (element named `Type` explicitly); all scenarios pass.
This is recorded because it is precisely the class of bug the
mandate's harness proof exists to catch: the policy was right, the
plumbing was not, and no build gate could see it.

---

## 2. Manufacturing reports (sweep MFG-1, MFG-4)

### Inventory — every MFG report query, crash status, disposition

| Report (window → query) | Before | After |
|---|---|---|
| `MfgCostsWin` → `RptQuery.MfgOrderCostsAsync` | worked | unchanged (regression-pinned by harness + tests) |
| `MfgVarianceWin` Total/Raw/Overhead/Finished → `RptQuery.MfgVarianceAsync` | **crashed on open, all modes** | fixed, re-sourced (below) |
| `MfgFeasibilityWin` → `RptQuery.MfgFeasibilityAsync` | **crashed on open** | fixed, re-sourced; Required restated in base units |
| `RepostWin` (mfg mode) → `MfgRepostAsync` | worked (header totals) | untouched — out of this step's defect set |

### Root cause

Both broken queries were written against a schema that never shipped:
they joined `o.order_id = l.order_id AND o.company_id = l.company_id`
and filtered `l.company_id = $company`. Migration 034 (the real MFG
schema) gives `mfg_orders` the primary key **`id`** and
`mfg_order_lines` **no `company_id` column at all** — company scope
lives only on the header. Every execution raised "no such column"
(SQLite) / the PostgreSQL equivalent. The harness reproduces the
crash from the base commit's exact SQL on a migrated fixture and
shows the fixed SQL returning the seeded rows (§6).

### Re-sourcing decisions (concepts, not just column names)

- **Line-level modes** (Total/Raw/Overhead) now join
  `o.id = l.order_id` and scope `o.company_id = $company`.
- **Mode semantics follow the domain that actually exists.**
  `MfgLineKinds.All` is exactly `RAW` and `COST`; the old filters
  hunted `OVERHEAD/ADDITIONAL/LABOUR/OTHER` and
  `FINISHED/OUTPUT/PRODUCT` line kinds that can never be written
  (MFG-4 — "absent by construction"). New mapping: **Total** = all
  lines; **Raw** = `line_kind='RAW'`; **Overhead** =
  `line_kind='COST'` — the additional-cost lines (services, labour
  charged as lines). The header `overhead_amount` lump has no
  standard counterpart, so it carries no line variance and stays out
  of this report (it is fully visible in `MfgOrderCosts`).
- **Finished** is a header-level concept (a finished item is the
  order header, never a line): the mode now reads `mfg_orders`
  directly — Amount = `total_cost`, Standard =
  `qty × standard_unit_cost`, Variance = the difference, in the same
  row shape as the line modes (`LineKind='FINISHED'`).
- **Feasibility** joins `l.order_id = o.id`, considers RAW lines of
  Draft orders, and keys the on-hand subquery by the header's
  company. Its `Required` column is now restated in **base units**
  through the invoice path's own converter (`StkPost.ToBaseUnits`):
  Required and OnHand were previously compared across units (a line
  asking 4 × 250 g read as "4 required" against a kilogram balance).
  The query also returns the line's material id (company+code join)
  so the conversion resolves the right material; a line whose item
  code resolves to no material keeps its raw quantity (nothing to
  convert against) — visible behaviour, not a silent catch: query
  failures follow the file's established `QueryAsync` pattern
  (catalog report + `AppError`).

---

## 3. Manufacturing unit equivalents (sweep MFG-2)

### The rule (owner's, from MATCARD_FIXES defect 5)

Stock moves in **base units**; a line expressed in unit N moves
`qty × equivalent(N)`, and any per-unit price restates as
`price ÷ equivalent(N)`. The invoice path has applied this since the
MatCard fix; manufacturing ignored it — `MfgOrderRepository` handed
the valuation engine the **display** quantities verbatim.

### One converter (no second implementation)

`src/ShouTech.Infrastructure/UnitEquivalents.cs` (new) is now the
single Infrastructure converter: `ResolveAsync(connection,
transaction, companyId?, materialId, unitName)` (units_json first,
unit1..unit5 column pairs as fallback, first unit = base with factor
1, base/unknown/free-typed/non-positive → 1 — the exact historical
rule) and `ToBase(qty, unitPrice, equivalent)`. The two private
copies in `CommercialDocumentRepository` and
`OrderDocumentRepository` are deleted; both delegate. The app-layer
`StkPost.ToBaseUnits` (used by invoice/card posting and now by
feasibility) already implemented the identical rule and is unchanged.

### Quantity-conversion trace (BOM line → stock)

```
MfgWin grid line (display unit kept on the document, e.g. 4 × "g250")
  → MfgOrder / MfgOrderLine persisted verbatim (display qty + unit)
  → MfgOrderRepository.PostAsync
      RAW line:  equivalent = UnitEquivalents.ResolveAsync(company-scoped)
                 baseQty    = line.Qty × equivalent            (4 × 0.25 = 1)
                 on-hand guard compares baseQty (was: display qty)
                 StockValuationEngine.Apply(QtyOut: baseQty)   → engine cost
      Finished:  finishedBaseQty = order.Qty × equivalent(finished unit)
                 receipt unit cost = TotalCost ÷ finishedBaseQty
                                   (order.UnitCost unchanged when eq = 1)
                 StockValuationEngine.Apply(QtyIn: finishedBaseQty)
  → CancelAsync mirrors both conversions exactly (finished guard +
    MFG_PRODUCE_REV use finishedBaseQty; raw re-entry converts
    qty AND unit cost via ToBase; the LinkedRate original-rate
    mechanism is untouched)
```

Persisted order totals stay display-based (`ComputeTotals`
unchanged): the document reads as the user typed it; only quantities
crossing into stock convert. `CardStore.PostOrder` (the migration-050
`manufacturing_orders` family) moved raw quantities through the
engine the same way and is converted via `StkPost.ToBaseUnits` for
consistency — it currently has **zero callers** (sweep MFG-7), so
this is correctness hygiene on a shipped writer, not a behaviour
change in any live flow; stated plainly so the owner is not misled.

**Worked example (harness-proven, §6):** RM base KG, `g250` = 0.25;
FG base PCS, `DOZEN` = 12. Order: produce 2 DOZEN of FG, BOM line
4 × g250 of RM @ typed 2.5, overhead 14, opening stock 2 KG @ 8.
Post → consumption movement **qty_out = 1** at engine avg cost
(cost_amount **−8**, not the typed price); RM balance 1 KG / 8;
finished receipt **qty_in = 24** at unit cost **1** (total cost
24 ÷ 24 base units); FG balance 24 / 24; GL completion entry
Dr = Cr = 8. Before this step the same order **could not post at
all** (guard compared display 4 > on-hand 2). Cancel restores
2 KG / 16 and 0 / 0. A 400 × g250 line (= 100 KG) is refused.

---

## 4. Comparison tables (gate — from `research/sweep/compare-mfg-hr-crm.md`, MFG parts)

Step 11 changes **correctness**, not features: no new MFG window,
field, or workflow was added (the build-out is a later build-phase
step). The tables below are the sweep's, with the Step-11 effect
marked; grades move only where a crashed/absent-by-construction item
became usable.

### 4.1 MfgWin — Production Order (sweep §1.1/§1.2; CORE 20, was 6 usable → 30%)

Condensed to the rows this step touches; the full 20-row field table
is unchanged by this step and lives in the sweep document.

| Workflow step | ShouTech before Step 11 | ShouTech after Step 11 | ERPNext | Odoo | Al-Ameen / Phenix ⚠ | SAP B1 |
|---|---|---|---|---|---|---|
| Reserve / check components | ❌ Feasibility crashes (MFG-1) | ✅ Feasibility opens; Required in base units | ✅ reserved qty; shortage | ✅ availability check | ✅ capability ⚠ | ✅ MRP / availability |
| Issue materials (planned vs actual) | ⚠ inside Post, **display qty** (MFG-2) | ⚠ inside Post, **base qty at engine cost** | ✅ Stock Entries | ✅ component moves | ✅ disbursement voucher ⚠ | ✅ Issue for Production |
| Receive finished | ⚠ inside Post, display qty | ⚠ inside Post, **base qty; unit cost = total ÷ base qty** | ✅ Manufacture entry, partial | ✅ Done + by-products | ✅ ⚠ | ✅ Receipt, partial |
| Cancel / reverse | ✅ stock reversal (display qty) | ✅ stock reversal, base-symmetric | ✅ reverses SL+GL | ✅ cancel/unbuild | ✅ ⚠ | ✅ reversals |

MfgWin grade itself is **unchanged at 30%** — the window gained no
CORE field or action; what changed is that its posting is now
quantity-correct and its companion reports open. (The sweep's §1.2
"Post to GL ❌ no journal" line is stale against the current code:
`MfgOrderRepository` has posted a balanced GL completion entry via
`GlPosting` since the Step-3/6 era — harness scenario M3 pins
Dr = Cr = engine consumption cost. WIP staging and variance
accounts remain absent; that is MFG-5's real residue and a
build-phase item.)

### 4.2 Manufacturing Reports (sweep §1.6; CORE 12, was 2 usable → 17%)

| Report | ShouTech after Step 11 | Counterpart equivalent | Verdict |
|---|---|---|---|
| Manufactured Material Costs | ✅ works (unchanged, regression-pinned) | ERPNext Work Order summary; Odoo Cost Analysis | Present |
| Finished Goods Cost Variance ("Total") | ✅ **opens**; all lines, actual vs standard | ERPNext planned vs actual; SAP B1 variance | **CORE — now present** |
| Raw Material Cost Variance ("Raw") | ✅ **opens**; RAW lines only | Per-component variance | **CORE — now present** (figures still depend on line standards being entered — MFG-4 residue, §7) |
| Overhead Cost Variance ("Overhead") | ✅ **opens**; COST lines (was: could never return a row) | Operating-cost variance | **CORE — now present** for line-level costs |
| Finished Goods Overhead Variance ("Finished") | ✅ **opens**; header-level finished variance (was: could never match) | — | **CORE — now present** |
| Feasibility / Capability | ✅ **opens**; Required in **base units** vs OnHand | Production Plan shortage; MO availability | **CORE — now present** |
| Repost Manufacturing | ⚠ ½ header totals only (unchanged) | ERPNext Repost Item Valuation | CORE (½) |
| Cost Analysis per order / WIP / Production-Scrap-Downtime / Batch genealogy | ❌ (unchanged — build phase) | ✅ counterparts | still absent |

**Reports grade after Step 11: 6½/12 CORE ≈ 54%** (from 17%) — the
five crashed/impossible CORE reports are usable; the remaining gap
is feature build-out, not correctness.

---

## 5. Per-defect closure vs sweep IDs

| ID | Defect | Closure |
|---|---|---|
| S-2 | Routers reactivate by type only → wrong mode shown | **Closed.** Central `WindowActivator` policy (type, ModeKey); `IWindowMode` on all 13 mode-ful windows; both routers delegate; Step-8 cheque special case generalized and deleted |
| S-3 | `MWin` caches one `DatWin`; mode argument ignored on re-open | **Closed.** Cache reactivates only on ModeKey match; same-mode open instance activated; otherwise a new instance in the requested mode |
| S-7 (router part) | `RibbonNav.Show` empty `catch {}`; `LockWorkStation` silent catch | **Closed for the router layer.** Activator reports `UI-7004` and still shows; lock reports `ShellOperationFailed`. Non-router silent catches belong to their own steps (DatWin/Backup were Step 10) |
| MFG-1 | Variance ×4 + feasibility crash on open (034 schema mismatch) | **Closed.** Both queries re-sourced to migration 034; harness reproduces the old crash and the new success on the same fixture |
| MFG-2 | MFG quantities ignore unit equivalents | **Closed.** Post + Cancel convert via the single `UnitEquivalents` converter; `CardStore.PostOrder` converted via `StkPost.ToBaseUnits`; feasibility Required in base units |
| MFG-4 | Variance modes hunt kinds that can never exist; line StandardUnitCost unenterable | **Modes closed** (Total/Raw/Overhead=COST/Finished=header). The MfgWin grid still offers no StandardUnitCost column — a window build item, out of this step's no-new-features mandate; flagged in §7 |
| MFG-3, MFG-5 (residue), MFG-6…MFG-9 | manual line costs; WIP/variance GL; BOM shell buttons; register caps/order | **Not touched** — build-phase items, explicitly out of mandate |

---

## 6. Proof — headless harness (SQLite), 65/65 PASS

`~/workspace/research/fix-step11/step11_harness.py` (+ C# harness in
`harness/`, Step-10 pattern: real SqlMigrator DLL migrates the
fixtures; the C# harness links the **real** `WindowActivator.cs` and
references the real Domain/Infrastructure/Application projects; the
only stand-ins are the WPF surface types `WindowActivator` touches
and `AppHost.Errors`, disclosed in `harness/Shims.cs`).

- **Migrator:** fresh DBs migrate clean; double-run idempotent
  (no migration in this step — gate verified anyway).
- **(a) Router policy:** two mode-ful families through the real
  activator — invoice kinds (Sales open → Purchase requested ⇒
  Purchase shown as a second instance, Sales **not** reactivated;
  same-kind request reactivates; loaded-doc ≠ new-doc identity) and
  MFG variance modes (Total open → Raw opens beside it; second Raw
  reactivates). Single-mode windows keep type-only reactivation.
  Pure `WindowModePolicy.Decide` cases pinned directly.
  **WPF activation itself (real window handles, focus, minimize
  restore) is owed on Windows** — the harness proves the decision
  and dispatch logic, not the shell's rendering.
- **(b) MFG reports:** the **exact SQL strings extracted from
  `RptQuery.cs`** run against a migrated fixture (posted order:
  lines 10 + 12 + 20, overhead 20, total 62, standard 5; draft order
  for feasibility). The base commit's SQL raises "no such column" on
  the same fixture (crash reproduced); the fixed SQL returns exact
  row sets for Total/Raw/Overhead/Finished + costs + feasibility.
  Hand-verified figures: RM-1 line variance **+2** (10 − 4×2), RM-2
  **−1.5** (12 − 3×4.5), COST line **+2** (20 − 18), finished
  **+12** (62 − 10×5), costs report variance **12**.
- **(c) MFG units end-to-end** (real `MfgOrderService` /
  `MfgOrderRepository` / `StockValuationEngine` / `GlPosting`):
  the §3 worked example — consumption exactly **1** base unit at
  engine cost −8; finished receipt **24** base units at unit cost 1;
  balances conserved; GL Dr = Cr = 8; cancel restores both sides;
  base-unit shortage refused with the order left Draft.
  `UnitEquivalents` resolution pinned directly (0.25 / 1 / 12 /
  unknown→1 / none→1; `ToBase(4, 2.5, 0.25) = (1, 10)`).

**Kern.Tests:** `RouterMfgStep11Tests` — 8 facts mirroring (a) at
the policy core and (b)/(c) end-to-end (feasibility asserted in base
units through the real `StkPost` conversion). Compiles at 0/0;
**execution owed on Windows** with the rest of the suite
(net8.0-windows testhost), as in every prior step.

---

## 7. Pillars checklists (touched windows)

PROVEN = exercised by the harness/tests above or by an unchanged
green gate; GAP = known, named, not hidden.

### MfgWin (posting path changed)
- UI: unchanged this step — GAP (standing): no StandardUnitCost grid
  column (MFG-4 residue), no BOM explosion (MFG-6) — build phase.
- DB: PROVEN — orders/lines persist display qty+unit verbatim
  (harness posts real orders through the real repository).
- Linking: PROVEN — opens via mode-aware router (single-mode);
  shell context untouched (audit exit 0).
- Posting: PROVEN — base-unit consumption/receipt at engine cost,
  GL completion entry balanced (harness M1–M3).
- Persistence: PROVEN — totals display-based and conserved
  (FG stock value = order total cost, harness M2).
- Downstream: PROVEN — costs/variance/feasibility reports read the
  posted result (§2); GAP: no WIP/variance GL accounts (MFG-5
  residue).

### MfgVarianceWin (all four modes)
- UI: PROVEN (unchanged shell; mode now part of router identity —
  two modes can be open side by side). DB: PROVEN — reads migration
  034 tables only. Linking: PROVEN — RptNav → WindowActivator with
  ModeKey. Posting: n/a (read-only report). Persistence: n/a.
- Downstream: PROVEN — figures hand-verified against the fixture
  (§6b); GAP: header `overhead_amount` has no standard, so it
  carries no variance line here (visible in MfgOrderCosts instead)
  — a modelling fact, disclosed in §2.

### MfgFeasibilityWin
- UI/DB/Linking: as above (single-mode window; query fixed).
- Downstream/correctness: PROVEN — Required restated in base units
  via `StkPost.ToBaseUnits` (Kern.Tests fact asserts 4 × g250 →
  Required 1 against OnHand 0.5); GAP: a line whose item code
  resolves to no material keeps its raw quantity (nothing to
  convert against).

### MfgCostsWin
- All pillars: PROVEN unchanged — query untouched and
  regression-pinned by harness + tests.

### Router-touched windows (InvoiceWin, CshWin, OrdWin, ChqWin,
### DocSearchWin, GroupManagerWin, SupportWin, DefManagerWin,
### DatWin, ComWin, PrmWin, ClosingAccountsWin)
- UI: unchanged except activation behaviour. DB/Persistence/
  Posting: untouched by this step (only a `ModeKey` accessor and a
  marker interface were added per window — class-declaration diffs
  are one line each).
- Linking: PROVEN at the policy/dispatch level (harness §6a);
  GAP — real WPF activation/focus on Windows is owed (§8).
- DatWin specifically: mode cards, DATA.EXPORT gating and audit
  from Step 10 untouched; only MWin's cached-instance selection
  changed (S-3).

---

## 8. Gates

| Gate | Result |
|---|---|
| Full build `dotnet build ShouTech.sln --no-incremental -p:EnableWindowsTargeting=true` | **0 errors, 0 warnings** |
| `tools/audit_window_context.py` | **exit 0** |
| Lang parity | **0 missing / 0 extra** in Ar/Fr/Tr vs En; this step changes no Lang/Errors file |
| Migrator idempotent double-run | **PASS** (harness; no migration added) |
| Headless harness | **65/65 PASS** (`~/workspace/research/fix-step11/step11_harness.py`) |

## 9. Owner flags / owed verification

1. **Windows run owed:** Kern.Tests execution (incl. the 8 new
  facts), real WPF router activation (second-instance focus,
  minimize-restore, DatWin cache behaviour in the live shell), and
  a live PostgreSQL pass of the re-sourced MFG queries (SQL is
  provider-agnostic and parameterized; verified headlessly on
  SQLite only).
2. **MFG-4 residue:** line `StandardUnitCost` still cannot be typed
  in the MfgWin grid, so Raw/Total variance figures are only as
  good as standards entered through other paths (the column exists
  end-to-end in DB/entity/repository). Adding the grid column is a
  window change — build phase, needs the owner's go-ahead like any
  feature.
3. **Overhead mode semantics** now mean "additional-cost (COST)
  lines". If the owner instead wants the header `overhead_amount`
  lump reported as its own variance row, that needs a standard
  overhead concept that does not exist yet — build phase.
4. `CardStore.PostOrder` remains a zero-caller writer (MFG-7); it
  was converted for consistency, but the owner may prefer deleting
  the migration-050 family outright in the build phase.
5. Nothing in this step is pushed or merged.

---

<!-- source: docs/FIX_PHASE_STEP12.md -->

# FIX PHASE — STEP 12: Screen fit — minimum-size reductions, clip fixes, BinWin under BaseWin

Branch `fix/step12-screenfit` on top of `fix/step11-router-mfg`
(`80eb1d6`). **Not pushed, not merged** — awaiting the owner's call.

Commits:

| Commit | Content |
|---|---|
| `e09cb73` | App-wide minimum-size reduction to the 50% cap (684×384 @1366×768) — 53 files, minimum values only |
| `4beaa44` | Clip fixes + layout adaptations for the new minimums (InvoiceWin, ProjWin, AccCard, PosWin, AssetWin, CustomReportsWin, CurCard, BarWin, CalculatorWin, CashDrawerOptionsWin, CoaWin) |
| `76ae3ac` | BinWin under BaseWin |
| (this doc + harness) | `tools/verify_screenfit.py` + this document |

No migration was added (none needed — layout step). No Lang keys were
added or changed (no new user-visible strings; every change is sizes,
container structure, or comments), so Lang parity is unchanged and no
Lang/Errors file differs from the base branch (verified by diff —
§8). Both database providers are n/a for this step: no data path,
query, or schema was touched (diff evidence in §7).

---

## 1. The standard and the honest arithmetic

The owner ruled that ribbon windows open at ≤ 50% of the screen (width
and height each ≤ 50% of the work area). The central BaseWin opening
cap that implements this lives on a parallel branch line (already
merged to main by the owner); this chain does **not** re-implement it.
What defeated the cap here — sweep §13 — was window **minimums**: the
cap cannot shrink a window below its declared `MinWidth`/`MinHeight`.

The bar for this step (per the mandate): the 1366×768 profile, cap
**684×384**. Every window's minimum must be ≤ 684×384, paired with the
layout adaptation that makes the window actually usable at that size —
or carry a written flag (§9).

Result up front: **every window minimum in the app is now ≤ 684×384**
(harness-proven, §8). One minimum was *raised* (AccCard MinWidth
400 → 660) because its own layout was dishonest at 400 — see §4.3 and
flag F-2. The 125%-scaling profile (effective cap 546×287) remains
violated by every working window's minimum and is flagged, not faked
(flag F-1).

Why 660×360 is the honest floor for a working window: a BaseWin
working window carries a bottom command bar (~50 DIP incl. divider), a
slim status line (~25), window chrome/margins (~35), and must still
show a usable slice of its schedule (grid header 34 + ≥ 3 rows ≈ 125)
or form. 360 DIP of height meets that with the grid/form scrolling;
anything materially smaller would hide the command bar's context or
reduce the schedule to its header alone. Width 660 fits the widest
fixed need among the standard windows (a 3-pair form grid: 3 × 120-DIP
label columns + 3 usable fields) and the AccCard header strip (§4.3).

---

## 2. Before/after minimums inventory (complete)

Generated by `tools/verify_screenfit.py --base 80eb1d6` (parses every
XAML root and every code constructor/initializer size sequence in
`cod/app/src/Views`); the table below is its verbatim output data.
"Adaptation" names the layout change that makes the new minimum
usable; "grid scrolls" means the window's fill is a DataGrid/ListBox/
TabControl-with-grids in a star/DockPanel-fill region, which already
shrinks and scrolls internally — no structural change was honest to
make there.

| Window | Before | After | Adaptation at the new minimum |
|---|---|---|---|
| PosWin | **1080×680** | 660×360 | §4.4 — star rail + scrollable summary, trimmed cart columns |
| CoaWin | 760×620 | 660×360 | Tree/grid star row shrinks; §4.5 re-widen clamp |
| PrmWin | 900×620 | 660×360 | Tab content already in ScrollViewers (XAML) |
| ProjWin | 980×620 | 660×360 | §4.2 — star rows/columns + task-editor ScrollViewer |
| AssetWin | 980×620 | 660×360 | §5 — schedule row + editor column star-proportioned |
| InstallmentsWin | 980×620 | 660×360 | Editor already in a ScrollViewer; grid scrolls |
| DatWin | 760×600 | 660×360 | Star content row (dataset grid) shrinks |
| DeliveryDeskWin | 980×600 | 660×360 | Tab grids scroll; settle editor is 4 rows (fits) |
| PayRecWin | 980×580 | 660×360 | Two star-column grids scroll |
| CustomReportsWin | 980×580 | 660×360 | §5 — results row + designer star/scroll |
| DefManagerWin | 940×580 | 660×360 | Editor already in a ScrollViewer; grid scrolls |
| InvoiceWin | 960×560 | 660×360 | §4.1 — scrollable star bands |
| ChqListWin | 980×560 | 660×360 | Filter row Auto + grid fill scrolls |
| BankRecWin | 960×560 | 660×360 | 3\*/2\* split rows shrink; grids scroll |
| LoyaltyWin | 900×560 | 660×360 | Grid scrolls; editor is 4 rows (fits) |
| MatCard | 840×540 | 660×360 | Sidebar + star content; tab grids carry scrollbars |
| CatWin | 760×520 | 660×360 | Form region already in a ScrollViewer (XAML) |
| MatGroupWin | 820×520 | 660×360 | Star grid row shrinks |
| MatDirWin | 380×520 | 380×360 | Tree/list star row shrinks (width already compliant) |
| BcdDesWin | 720×500 | 660×360 | Preview already in a ScrollViewer; grid scrolls |
| ActivityLogWin | 900×500 | 660×360 | Filter Auto + tab grids fill |
| SupportWin | 760×500 | 660×360 | Form docked Top + grid fill scrolls |
| CcListWin | 820×480 | 660×360 | Grid fill scrolls |
| BkpWin | 680×480 | 680×360 | Star log/list row shrinks (width already ≤ cap) |
| FinancialAnalysisWin | 860×480 | 660×360 | Filter/note docked Top + grid fill scrolls |
| CstCard | 720×460 | 660×360 | Tab forms are 2–4 rows (fit); metrics/action bars docked |
| OrdWin | 820×460 | 660×360 | Auto header + star lines grid (document pattern) |
| PartyManagerWin | 840×460 | 660×360 | Filter docked Top + grid fill scrolls |
| PosSyncWin | 860×460 | 660×360 | Grid fill scrolls |
| CloseCashWin | 820×520 | 660×360 | Form/computed docked Top + grid fill scrolls |
| JrnWin | 800×450 | 660×360 | Auto header + star lines grid |
| ChqWin | 800×440 | 660×360 | Auto header + star grid |
| CshWin | 760×440 | 660×360 | Auto header + star grid |
| MfgWin | 800×440 | 660×360 | Auto header + star grid |
| RolloverWin | 760×440 | 660×360 | Note docked Top + grid fill scrolls |
| DocSearchWin | 760×420 | 660×360 | Filters + star grid |
| OrderTrackWin | 780×420 | 660×360 | Star grid region scrolls |
| CurCard | 740×420 | 660×360 | §5 — card column star + form ScrollViewer |
| AddonsWin | 700×420 | 660×360 | Note docked Top + grid fill scrolls |
| MaintenanceWin | 700×440 | 660×360 | Results are a ListBox (scrolls) |
| MfgListWin | 900×520 | 660×360 | Grid fill scrolls |
| HelpWin | 860×520 | 660×360 | Section ListBox + RichTextBox reader (both scroll) |
| BarWin | 660×430 | 660×360 | §5 — split star-proportioned + form ScrollViewer |
| BcdGenWin | 660×430 | 660×360 | Grid fill scrolls |
| CalculatorWin | 320×430 | 320×360 | §5 — pad MinHeight 48 → 36 |
| LicenseWin | 600×420 | 600×360 | Info is a ListBox (scrolls); width already compliant |
| RepostWin | 620×420 | 620×360 | Grid/form fill (F2 pattern) |
| GroupManagerWin | 640×400 | 640×360 | Star grid/editor regions shrink |
| ColChsWin | 380×400 | 380×360 | List star row shrinks (width already compliant) |
| WhsWin | 640×420 | 640×360 | Star rows shrink (width already compliant) |
| WhsWin/StockCountDialog | 760×420 | 660×360 | Count grid scrolls (nested BaseWin dialog) |
| RptBase (**57 report windows**) | 760×440 | 660×360 | One change in the base class: filter card Auto + star grid + bottom totals/actions; grids scroll |
| AccCard | 400×500 | **660×360** | §4.3 — MinWidth *raised* + card widened + WrapPanel bar |
| PosCheckoutWin | 500×400 | 500×360 | Star content region shrinks |

**Already compliant — unchanged (minimum ≤ 684×384 on the base):**
BnkCard 620×380 · CcCard 620×360 · ChangePasswordWin 460×380 ·
CompressInvoicesWin 540×360 · PartySearchWin 620×380 · RepCard
640×320 · UpdatesWin 600×380 · SerWin 520×340 · Lang 380×260 ·
RestoreCoWin 420×320 · BinWin 560×360 (size unchanged; base class
changed, §6) · MatMergeWin (nested in WhsWin) 410×190 · PosWin
UnitChooser dialog 420×260.

**No declared minimum — cannot defeat the cap, unchanged:** ComWin
(designed 560×330, NoResize — fits the cap as designed), LogWin
(560×330, NoResize — fits), SplWin (620×360, NoResize splash — fits,
transient), StpWiz (650×600 designed, resizable, no minimum — see
flag F-4). CashDrawerOptionsWin declares no minimum either; it is
NoResize at a designed size, and its sweep MINOR (~9-DIP clip at
270 high) is fixed by Height 270 → 300, which fits the cap (§5).
MWin (600×120 declared) is the main window — maximized by the
owner's rule, exempt.

---

## 3. Layout-adaptation method (why a smaller minimum is honest here)

Three shrink mechanisms, all already the app's own patterns — no new
layout system was introduced, and sizes flow through the central
styles/tokens wherever a token exists (the proportions standard,
`docs/WINDOW_PROPORTIONS.md`, is untouched):

1. **Star/DockPanel-fill schedules.** The fill region is a DataGrid
   (central `VisuDataGridStyle`, scrollbars Auto, row+column
   virtualization on) or a ListBox/RichTextBox. It shrinks to any
   height and scrolls internally; the bottom command bar and status
   line are docked *outside* the fill, so they can never be pushed
   off-screen. This covers the majority of windows, including the
   whole RptBase family.
2. **Docked-Top form + fill grid** (F2 pattern: CloseCashWin,
   SupportWin, FinancialAnalysisWin…): the form takes its Auto height,
   the grid takes what remains and scrolls.
3. **Scrollable form hosts** where a tall form shares space with a
   grid (the ProjWin/AssetWin/InstallmentsWin editor pattern):
   the form sits in a `ScrollViewer (Vertical=Auto)` inside a star
   row/column. Step 12 extended this existing pattern to the four
   places that lacked it (ProjWin task editor, CustomReportsWin
   designer, CurCard card form, BarWin form) and to InvoiceWin's two
   bands and PosWin's summary rail.

Fixed-size regions that survived the pass are only ones that fit the
minimum with room to spare (tab forms of 2–4 rows, 4-row editors,
Auto header bands of document windows) — each is named in §2.

---

## 4. The three sweep clip fixes (+ PosWin and CoaWin)

### 4.1 InvoiceWin — payment column (sweep: true violation, clips at every size)

Root cause: the root grid was three *proportional* bands
(3\*/4\*/3\* ≈ 30%/40%/30%). The header band's payment column stacks
**8 fields** (≈ 320 DIP with the standard 32-DIP control height and
form margins) — more than 30% of any window height the app ever
opens at — and neither band had a ScrollViewer, so the overflow was
simply clipped, at the designed 700, at the 1080p cap (520), and
everywhere else.

Fix (`InvoiceWin.BuildUi`, commit `4beaa44`):

- Root bands re-proportioned to **3\* / 2.5\* / 2.5\*** (header /
  lines grid / bottom) + the unchanged Auto action bar at the bottom.
- The header panel now sits in a `ScrollViewer (Vertical=Auto,
  Horizontal=Disabled)` inside its star band; the bottom band
  (totals + voucher bar + notes) sits in an identical ScrollViewer.
- At a large size all three bands show in full simultaneously; as
  the window shrinks toward 660×360 each band scrolls internally,
  the lines grid keeps its own scrollbars, and the bottom command
  bar (Save/Post/…) is outside every scroll region — always visible.
- No field, column, or handler moved; `BuildHeaderPanel` /
  `BuildFooterPanel` are byte-identical.

### 4.2 ProjWin — task editor (sweep: true violation, clips ~30 DIP at every size)

Root cause: the content grid's task row was a **fixed 170 DIP**
while the task editor stacks 5 fields (≈ 200 DIP), with no
ScrollViewer — the last field(s) clipped at 1080p and everywhere
else. The project editor directly above it already used the correct
pattern (star row + ScrollViewer) — the task editor simply never
got it.

Fix (`ProjWin.BuildUi`):

- Content rows: `1\* / Auto / 170-fixed` → **`1.25\* / Auto / 1\*`** —
  the task band now takes a proportional share and grows/shrinks
  with the window.
- Editor columns (content grid and task panel alike): fixed 330 →
  **`2.5\* / 1\*` with editor `MinWidth = 240`** — the editor keeps
  its designed ~28% share at 1180 wide and a usable 240+ at 660.
- The task editor is wrapped in a `ScrollViewer (Vertical=Auto)`,
  exactly mirroring the project editor's host two dozen lines above.
- At the designed 1180×740 the task band is *taller* than before
  (~290 vs 170), so the editor shows in full with room to spare;
  at the minimum it scrolls. No field or handler changed.

### 4.3 AccCard — 734-DIP bottom bar in a 450-wide card (sweep V3, MAJOR)

Root cause, two halves: (a) the bottom bar was a single-row
DockPanel holding Close + seven `ButtonMinWidth` (88) actions ≈
**734 DIP** inside a card whose client width is ≈ 406 — New/Save/
Edit opened off the card's left edge; (b) the header strip is a
3-pair form grid (3 × `FormLabelColumnWidth` 120 + 3 fields), which
at 450 wide leaves Code/Status/Risk fields ≈ 10 DIP each. The card
was dishonest *at its own designed size* — no minimum reduction
could fix it, so this window is the one place the step changes a
designed size:

- Designed **Width 450 → 700**; **MinWidth 400 → 660** (raised —
  flag F-2), MinHeight 500 → 360. At 660 the header fields get
  ≈ 85 DIP each beside their 120-DIP labels — narrow but honestly
  usable; at the designed 700, ≈ 100.
- Bottom bar: DockPanel + single-row StackPanel → **WrapPanel**
  (right-aligned, same eight buttons, same order, same MinWidths,
  same handlers). One row when there is room; wraps to a second row
  at the minimum. Every action stays reachable at every size.
- The main form already lived in a ScrollViewer inside a star row;
  it now actually gets to use it at the 360 minimum.

### 4.4 PosWin — the worst offender (1080×680, exceeded every profile in both dimensions)

POS is a touch surface and keeps its deliberate proportions
exception from the proportions standard: the 48-DIP scan field,
38–54-DIP touch buttons, and `TouchControlHeight` family are
**untouched**. What changed is only how the surface *composes* at
small sizes:

- Minimum **1080×680 → 660×360** (fits the 1366×768 cap).
- Body split: cart `1\*` + fixed 320 rail → **`3\* / 1\*`
  (rail `MinWidth = 250`)** — the rail keeps its designed ~¼ share
  at any width.
- The summary rail's content now sits in a `ScrollViewer
  (Vertical=Auto)`: at the minimum height the totals/payment block
  scrolls instead of pushing the checkout actions off the bottom.
  At working sizes nothing scrolls and nothing moved.
- Header brand column fixed 250 → `Auto` (sizes to the wordmark),
  so the scan field keeps room; scan `MinWidth` 260 → 180 (its
  48-DIP touch *height* is unchanged).
- Cart columns trimmed 502 → 456 DIP total (Barcode 118→110, Qty
  82→76, Price 100→90, Tax 90→80, Line total 112→100; the Name
  column stays star) so the cart fits beside the rail at the
  minimum without a horizontal scrollbar; the grid keeps its own
  scrollbars regardless.
- Written justification for the record: a POS terminal runs this
  window maximized/full-screen on its own display — that is
  unchanged (designed 1360×820, freely maximizable). The 660×360
  minimum is a *compact mode* (e.g. a back-office laptop at
  1366×768 running the till window at the 50% cap): every control
  remains reachable — cart by its grid scrollbars, rail by its
  scroll viewer, all touch targets at full touch size.

### 4.5 CoaWin — post-clamp re-widening (sweep §13 MINOR, fixed at the call site)

`SizeToLongestAccountName` runs on Loaded — *after* any central
clamp/cap — and set `Width = Clamp(…, MinWidth, 1600)` with no
work-area term, silently defeating the cap on data. The upper bound
is now `Min(1600, max(MinWidth, WorkArea.Width))`: the window still
widens for long account names, never past the screen it is on.

---

## 5. Remaining adaptations (commit `4beaa44`)

- **AssetWin**: depreciation-schedule row fixed 180 → `0.85\*`
  (list row `1.25\*`); editor column fixed 330 → `1\*` of a
  `2.5\*/1\*` split, `MinWidth = 240`. (The editor already scrolled;
  the schedule grid scrolls internally.)
- **CustomReportsWin**: run-results row fixed 190 → `1\*`
  (`MinHeight = 80`, list row `1.4\*`); designer column fixed 330 →
  `1\*` of `2.5\*/1\*`, `MinWidth = 240`; designer form wrapped in a
  ScrollViewer (its ~290 DIP previously clipped in the squeezed
  star row).
- **CurCard**: card column fixed 330 → `1\*` (`MinWidth = 240`)
  against a `1.5\*` rates grid; the 10-row card form wrapped in a
  ScrollViewer inside its card border.
- **BarWin**: form column fixed 380 → `1.1\*` (`MinWidth = 250`)
  against a `1\*` preview (the 240-DIP preview card previously had
  ~210 DIP at the minimum); form wrapped in a ScrollViewer.
- **CalculatorWin**: keypad button `MinHeight` 48 → 36 — at the new
  360 window minimum the 5-row pad now fits (5 × 36 + gaps ≈ 210 of
  ≈ 224 available) instead of clipping its last row.
- **CashDrawerOptionsWin**: Height 270 → 300 (sweep MINOR: ~9 DIP
  of content clipped on every screen). NoResize and designed width
  unchanged; 570×300 fits the cap as designed.

Minimum-only windows needed no structural change for the reasons in
§3 — each is named with its mechanism in the §2 table.

---

## 6. BinWin under BaseWin (commit `76ae3ac`)

BinWin was the last working window rooted at `<Window>`, so no
central policy reached it: no work-area clamp / 50% cap, no geometry
persistence, no live relocalization, no schedule-tint hook (sweep
§11/§13 — "redesign item: bring BinWin under BaseWin"; step 1
flagged the same).

Conversion, behaviour preserved 1:1:

- `BinWin.xaml`: root `<Window>` → `<local:BaseWin>` (the pattern
  every other XAML window uses); `ShowInTaskbar="False"` removed —
  it now shows in the taskbar like every other window, so it is
  recoverable when another program covers it (sweep §14's
  no-taskbar sink class). Designed 780×520 and minimum 560×360
  unchanged (already cap-compliant).
- `BinWin.xaml.cs`: `class BinWin : Window` → `: BaseWin`. The
  constructor's hand-rolled `Win11Chrome.Apply(this)` and
  `LangDirSv.ApplyToWindow(this)` try/catch are deleted — BaseWin's
  constructor/Loaded path applies chrome, shell context
  (language/direction/company/theme via `ShellContext`), geometry
  persistence, and the work-area clamp centrally.
- The existing `ApplyLanguage()` (all texts via `Loc.Get`, unchanged)
  is now also the `ApplyLocalizedText()` override, so BaseWin calls
  it on load **and on every live language switch** — the bin follows
  the shell language like every other window, which it did not
  before.
- **Untouched**: every field, the grid and its columns, all
  `RecycleBinSvc` calls, Refresh/Restore flows, and the step-1
  **SYS.ADMIN gate on `Purge_Click`** (`AppHost.Permissions.Has(
  PermCat.System.Admin)` + `ErrorCodes.PermissionDenied` report) —
  the harness asserts the gate is still present (§8).
- `audit_window_context.py` now lists BinWin with base `BaseWin`
  and the full central context row (direction/company/theme), where
  Window-derived rows show the reduced path.

---

## 7. Comparison notes (small-screen behaviour) + pillars

Brief notes per the mandate (full comparison tables exist in the
sweep's compare files; this step changes no fields, workflows, or
options, so only small-screen behaviour is compared):

- **PosWin** — Al-Ameen V10 / Phenix v10 POS run as dedicated
  full-screen till surfaces on the terminal, exactly as PosWin's
  designed 1360×820 does; ERPNext POS and Odoo POS are web apps that
  reflow and scroll the page/cart on small viewports. ShouTech now
  matches both postures: full-screen by design on the terminal, and
  a scrollable compact mode (rail scrolls, cart scrolls, touch
  targets unshrunk) when windowed small — the web counterparts'
  scroll-the-overflow behaviour, in desktop form.
- **InvoiceWin** — ERPNext/Odoo invoice forms are long web forms:
  the page scrolls, the lines grid sits mid-form, and actions pin
  to a bottom bar. Al-Ameen/Phenix desktop invoices keep a fixed
  header + grid + totals composition and rely on the window being
  large. The fix takes the web counterparts' rule — *bands scroll,
  actions stay pinned* — into the desktop composition: header and
  totals bands scroll in place, the action bar never scrolls away.
- **ProjWin** — ERPNext's Project form and Odoo's project view put
  tasks in a tab/list below the project with the page scrolling as
  one; Phenix/Al-Ameen project windows use the same list-over-tasks
  split ShouTech has. The star-row + scrollable-editor fix keeps
  ShouTech's split but borrows the counterparts' guarantee that the
  task editor is never partially rendered.
- **AccCard** — Counterpart account cards (Al-Ameen/Phenix chart
  cards, ERPNext Account, Odoo chart) are compact cards whose
  action rows fit the card by construction; none ships a card whose
  own button row overflows it. The widen + wrap fix restores
  exactly that invariant, keeping ShouTech's button set and order.
- **BinWin** — ERPNext's Deleted Documents and Odoo's recycle/trash
  views are ordinary list windows under the same shell chrome,
  permissions, and localization as every other window. BinWin
  under BaseWin now has the same property in ShouTech's terms:
  one shell policy (geometry, direction, context, relocalization)
  with no per-window copies.

### Pillars checklists

For every window below: **DB / linking / posting / persistence /
downstream are "unchanged — verified no data-path edits"** — the
step's full diff (58 files) touches only size values, container
structure (ScrollViewer/WrapPanel/star proportions), comments, and
the BinWin base class; `git diff 80eb1d6` contains no repository,
service, SQL, or handler-body change (handler *wiring* lines are
untouched; the only code-logic line changed is CoaWin's width
clamp, §4.5, which writes no data).

| Window | UI | DB | Linking | Posting | Persistence | Downstream |
|---|---|---|---|---|---|---|
| PosWin | CHANGED — §4.4, proven statically (§8); runtime owed | unchanged — no data-path edits | unchanged | unchanged | unchanged (cart/checkout code untouched) | unchanged |
| InvoiceWin | CHANGED — §4.1 | unchanged — no data-path edits | unchanged | unchanged (InvRepo/GlPosting untouched) | unchanged (all fields + save paths identical) | unchanged |
| ProjWin | CHANGED — §4.2 | unchanged | unchanged | unchanged (ProjectService untouched) | unchanged (project + task editors collect the same fields) | unchanged |
| AccCard | CHANGED — §4.3 | unchanged | unchanged | unchanged | unchanged (same controls/handlers) | unchanged |
| BinWin | CHANGED — §6 (base class, taskbar) | unchanged (RecycleBinSvc untouched) | IMPROVED — now under the central router/BaseWin policy like every window | n/a (no posting; purge gate preserved + harness-asserted) | unchanged (restore/purge flows identical); geometry persistence ADDED by BaseWin (central policy) | unchanged |
| AssetWin, CustomReportsWin, CurCard, BarWin, CalculatorWin, CashDrawerOptionsWin, CoaWin | CHANGED — §4.5/§5 layout only | unchanged — no data-path edits | unchanged | unchanged | unchanged | unchanged |
| All minimum-only windows (§2) | Size values only | unchanged | unchanged | unchanged | unchanged | unchanged |

---

## 8. Proof and gates

**Static verification harness** — `tools/verify_screenfit.py`
(new, committed with this doc):

```
python3 tools/verify_screenfit.py --base 80eb1d6   → exit 0
  - 68 window/dialog minimums parsed (XAML roots + code size
    sequences, incl. nested StockCountDialog / MatMergeWin /
    UnitChooser): every one ≤ 684×384, before/after printed
    (the §2 table is its output);
  - 4 windows with no declared minimum listed (ComWin, LogWin,
    SplWin, StpWiz) — cannot defeat the cap;
  - BinWin: XAML root is <local:BaseWin>, code-behind derives from
    BaseWin, SYS.ADMIN purge gate present;
  - Clip regions: InvoiceWin header/bottom ScrollViewers present
    and the fixed 30/40/30 bands absent; ProjWin task-editor
    ScrollViewer present and the fixed 170-DIP row absent; AccCard
    WrapPanel bar + Width 700 present; PosWin 660×360 + scrollable
    rail present.
```

**Gates (all run on this branch):**

| Gate | Result |
|---|---|
| Full solution build `--no-incremental` (`-p:EnableWindowsTargeting=true`) | **0 errors, 0 warnings** |
| `tools/audit_window_context.py` | **Exit 0** — 71 window classes, 0 window-level FlowDirection pins; BinWin's row improved to BaseWin/central (§6) |
| `tools/verify_screenfit.py --base 80eb1d6` | **Exit 0** — all assertions pass (above) |
| Lang parity | **Untouched** — no Lang/catalog file in the branch diff (`git diff 80eb1d6 --name-only` shows none), so parity is exactly the base's 0 |
| Migrations | None added; migrator untouched |
| Silent catches | None added — the only catch removed is BinWin's, whose responsibility moved to BaseWin's central (reported) path |

**Not claimed:** WPF runtime rendering at small sizes. Every
verdict above is static (parsed sizes + code-traced layout
structure) from a Linux VM. Opening each window at 684×384 and at
its minimum on Windows — above all PosWin's compact mode,
InvoiceWin's scrolling bands, ProjWin's task band, and AccCard's
wrapped bar — is **owed on Windows**, as is the merge-time check
that the parallel branch's 50% opening cap and these minimums
compose (cap opens ≤ 684×384; no minimum now resists it).

---

## 9. Owner flags (residuals, by name — nothing faked)

- **F-1 — 125% scaling profile (effective cap 546×287).** At
  1366×768 @125% the 50% cap height is 287 and the width cap 546;
  *every* working window's new minimum (660×360, and the smaller
  cards at 320–640 wide / 320–380 high) still exceeds it — a window
  whose command bar + status + schedule slice need ~360 DIP cannot
  honestly declare 287. Named as a class: all 660×360 windows in §2
  (worst: the full working set), plus BnkCard/CcCard/PartySearchWin/
  ChangePasswordWin/UpdatesWin on height (380). If the owner wants
  the 125% profile honoured literally, the honest route is a
  compact-density pass (smaller control height token at high
  scaling), not smaller declared minimums over the same layout.
  At 100% scaling — the mandated bar — there are **no residual
  violations**.
- **F-2 — AccCard minimum *raised*.** MinWidth 400 → 660 and
  designed width 450 → 700 (§4.3): the card's own 3-pair header
  grid cannot render usable fields below ~660. This is a designed-
  size change beyond a pure minimum reduction, taken because the
  alternative (stacking the header pairs vertically) rewrites the
  card's form layout. Owner to confirm the wider card is acceptable.
- **F-3 — InvoiceWin header band scrolls at cap size.** A
  consequence of the 50% rule meeting an 8-field payment column:
  at the 684×384 opening the header band shows ~5 of 8 rows and
  scrolls for the rest (nothing clips; the action bar is always
  visible). Enlarging/maximizing restores the full header. If the
  owner prefers, the payment column could be rebalanced across the
  three header groups — a field-layout change this step was
  forbidden from making silently.
- **F-4 — StpWiz designed height.** The setup wizard is designed
  650×600 with no declared minimum (resizable). It cannot defeat a
  minimum-based cap, but if the parallel cap does not reach
  Window-derived startup windows it will open at 600 high on a
  1366×768 screen. Same reach question applies to BkpWin and
  PrmWin (both still `Window`-derived; their minimums now fit the
  cap, but the cap itself is the parallel branch's to apply). This
  step's mandate converted BinWin only.
- **F-5 — Windows runtime pass owed** (see §8): visual confirmation
  at the minimum sizes, Mica/RTL behaviour of the newly scrolling
  bands, and PosWin touch use in compact mode all need the owner's
  Windows run before this is called runtime-verified.

---

<!-- source: docs/FIX_PHASE_STEP13.md -->

# FIX PHASE — STEP 13: Visual leftovers (COA localization, accented-primary convention, schedule tints, shell font token)

**Branch:** `fix/step13-visual` (worktree `~/workspace/projects/ShouTech-fix13`), based on `fix/step12-screenfit` @ `08e0498` (chain carries steps 1–12). **NOT pushed / NOT merged** — awaiting owner approval.
**Scope source:** `research/acceptance-sweep-report.md` §summary step 13 + `research/sweep/visual-*.md` (12 files) + `research/sweep/journal-coa.md` C-4/C-5.
**Excluded by scope discipline:** the parallel central branch merged into main separately (central label contract in `VisuId.xaml`/`BaseWin.AddFormField`, the 50% opening cap, ownership/z-order) — checked against main @ `82615e7`; none of it is redone here. This step changes **text sources and style claims only**: no behavior, data, posting, or schema change anywhere (diff evidence in §7).

**Windows visual verification is OWED on Windows** — everything below is code/XAML-traced and programmatically proven; no claim here says the running UI was seen.

---

## 1. Item-by-item closure vs the sweep

| # | Sweep item (source) | Closure |
|---|---|---|
| 13-1 | **C-4/C-5 — CoaWin + AccCard localization-dead** (empty `ApplyLocalizedText()` stubs, 59 placeholder literals rendering as UI text, ~150 hardcoded literals, combo values doubling as display text) — `journal-coa.md`, `visual-journal-coa.md` worst #1/#2 | **CLOSED** — §2. Both windows fully wired into the central localization system (LocProps + `T()`/`Tf()`), live relocalization implemented, placeholders = 0, combo display separated from stored canonical values. |
| 13-2 | **No primary-button distinction anywhere** (Save/Post identical to Close in nearly every window; DatWin's accent convention never generalized) — every `visual-*.md`, e.g. visual-cash #3, visual-cheques #4, visual-parties #2, visual-mfg-hr-crm #5, visual-orders #3 | **CLOSED centrally** — §3. `BaseWin.ApplyPrimaryButtonConvention` accents the one primary action (Post > Save, by localization key) in every BaseWin window; windows that already accent their own primary are respected untouched. Residual non-Save/Post primaries flagged in §8 (F-3). |
| 13-3 | **Missing schedule tints** — named: CloseCashWin (visual-cash #4), ChqListWin (visual-cheques #2), PosWin + DocSearchWin (visual-invoicing #8), MatGroupWin (visual-materials #3), MfgListWin + DefManagerWin (visual-mfg-hr-crm #3), OrderTrackWin + DocSearchWin + DeliveryDeskWin (visual-orders #2/#3), PartyManagerWin + PartySearchWin (visual-parties #1), CustomReportsWin + FinancialAnalysisWin (visual-report-family §2), ActivityLogWin (visual-shell-admin §12 note), BinWin (visual-shell-admin §BinWin), StockCountDialog (visual-stock-reports #3) | **CLOSED** — §4. All 16 named windows claimed a unique tint; the palette was extended (16 → 43 tokens) because uniqueness is arithmetically impossible inside the old palette (15 used + Peach spare). The same standing rule was then completed for the remaining grid windows the audits did not name (12 more claims) so the proof in §7 holds app-wide. |
| 13-4 | **Tahoma shell font token** (`VisuId.xaml:273` `ShellChromeFontFamily` = Tahoma; MWin menu style hardcodes Tahoma at `MWin.xaml:153`) — acceptance report §13/visual-shell-admin | **CLOSED** — §5. Token now resolves to the central Segoe UI family in both dictionaries that define it (`VisuId.xaml`, `ShMenu.xaml`); MWin's literal setter routes through the token. Zero Tahoma remains in the shell/themes. Side effect, intended: BinWin/DatWin, which set their window font from this token, now render in Segoe UI like every other window (the audit's G-2 complaint). |
| 13-5 | Other small/mechanical leftovers in the same class (§5 sweep) | **CLOSED (small):** MWin dead "Option 1/2/3" RadioPanel scaffold + its orphaned style removed (visual-shell-admin); AccCard Close button literal `MinWidth="62"`/`Padding="8,4"` → `ButtonMinWidth` token (visual-journal-coa Axis 2 note); CoaWin password dialog rebuilt beside-field on the central form grid with token sizes (visual-journal-coa Axes 1+3 #6) as part of its localization; `F2WinUi.ParseDecimal` hardcoded `"Invalid number format."` → `Common.InvalidNumberFormat` ×4 (visual-cash, CloseCashWin shared-helper note). **Larger items listed as GAPs in §8, not built.** |

---

## 2. COA localization (C-4/C-5) — what was done

### 2.1 Mechanism (the app's own central system, no parallel path)
- **XAML:** every static caption in `CoaWin.xaml` / `AccCard.xaml` now carries `loc:LocProps.Key` / `loc:LocProps.ToolTipKey` and the windows carry `loc:LocProps.WindowKey` (`CoaWin.Title` / `AccountCard`) — the exact CatWin/MatCard pattern, resolved by `UiLoczr` on open and on every live language switch.
- **Code-behind:** both `ApplyLocalizedText()` stubs are now real implementations: they run the `UiLoczr` walk (errors reported via `AppHost.Errors.Report` — no silent catch), then refresh the dynamic surfaces the walk cannot reach:
  - CoaWin: status readouts (`TxtCount`, `TxtSelected` via `Tf` format keys), the tree (rebind so node display/tooltips re-resolve), and the **popup context menu** — a `ContextMenu` is not in the window's logical tree, so `LocalizeContextMenu()` applies its LocProps keys explicitly (its FlowDirection sync, pre-existing, is kept).
  - AccCard: the four option combos are rebuilt in the new language (see 2.2) and the risk indicator is refreshed.
- **Formatted messages** go through the new central `BaseWin.Tf(key, args)` helper (cached `CompositeFormat` + `CultureInfo.CurrentCulture` — the BinWin/BcdGenWin CA1863 pattern, held centrally so every window formats one way). This was required: plain `string.Format(T(...))` trips CA1305/CA1863 and the gate is 0 warnings.
- **All user-facing literals** in both code-behinds were replaced: load/export/import dialogs (titles + file-filter display text), success/failure messages, the `.sta` import validation exceptions (they surface verbatim in message boxes), the PDF print heading/date/job name, the password dialog, and every AccCard validation/lifecycle message box (16 sites). The dead, caller-less `CoaWin.ShowAction` (last two literals) was removed.
- **AccountNode:** `TypeLabel` stays canonical (`Group`/`Account`) because it is the export file's Type column; the tree now binds the new localized `TypeDisplay`, and `ToolTipText` is composed from `CoaWin.NodeTypeLine` / `NodeChildren` / `NodePostable` keys via `AppHost.Localization`.

### 2.2 Combo values ≠ display text (AccCard)
The four combos (Status, Risk, PostingMode, CostCenter) previously held raw English strings that were simultaneously display text **and** the stored/compared values. They now hold an `AccOption { Value, Display }`: `Value` is the canonical token the schema has always stored (`Active`, `Low`, `Postable`, `Unspecified`, …), `Display` is `T()` of the matching `AccCard.*` key, rendered via `ToString()`. All reads go through `SelectedValue()`, all writes through `SelectValue()` — **stored data is byte-identical to before in every language**; only the rendering localizes. The parent combo still lists `"code - name"` data strings (account data, not UI text) and its code-prefix parsing is unchanged (see flag F-1, defect C-6).

### 2.3 Key inventory
**145 new keys, added to all four catalogs (En/Ar/Fr/Tr), parity 0 missing / 0 new extras:**

| Section | Keys | Covers |
|---|---|---|
| `CoaWin.*` | 87 | title/tree header/search, 13 context-menu items + 13 tooltips, export/import menus + dialogs + filters, success/failure messages, password dialog, print, node type/tooltip lines, 14 `.sta`/import validation messages, selection/count formats |
| `AccCard.*` | 57 | 19 field/balance captions, nav + serial tooltips, Disable/Statement/Ledger, 10 combo option displays, 9 message-box titles, 14 validation/lifecycle/save messages |
| `Common.InvalidNumberFormat` | 1 | shared F2 helper exception (item 13-5) |
| **Total** | **145** | every one is referenced from code/XAML (programmatic check, §7) |

Reused existing keys where they already existed: `Common.Close/Print/Refresh/OK/Cancel/New/Save/Edit/Delete/First/Prev/Next/Last`, top-level `AccountCard` (window title).

### 2.4 Justified remaining literals in the two code-behinds (explicit list)
- Canonical **stored tokens** compared/persisted, never displayed raw: `"Summary"/"Postable"/"Group"/"Account"/"Active"/"Low"/"Unspecified"` etc. (display goes through §2.2 / `TypeDisplay`).
- **File-format internals:** XLSX package XML, the CSV header row (`Code,ParentCode,Name,NameEn,Type` — the import format itself), `.sta` magic/format strings, default export file names.
- **Format/product names** in menus: `Excel`, `CSV`, `PDF` (identical in all four languages' catalogs elsewhere in the app).
- `"ShouTech ERP"` message-box title (brand name, as in the rest of the app).
- **Seed chart account names** (`SeedDefaultChart`) — these are *database seed data*, not UI text; see flag F-2 (defect C-8).

---

## 3. Accented-primary convention (item 13-2)

**Central rule, implemented once in `BaseWin.ApplyPrimaryButtonConvention()`** (called from `OnBaseLoaded`, after localization):
1. Walk the window's logical tree for `Button`s; read each button's localization key (`LocProps.GetKey`, else the `Tag` the code-built `StyleButton` helpers store the key in).
2. If any button's style chain already contains `VisuAccentButtonStyle`/`PrimaryButtonStyle`, the window has chosen its own primary (DatWin Start, WhsWin Post, MatGroupWin Save, PosCheckoutWin Complete, …) — **do nothing**.
3. Otherwise accent the first **Post** button (key's last segment `Post`: `Invoice.Post`, `OrderDoc.Post`, `Vouchers.Post`), else the first **Save** button (`Common.Save`, `Buttons.Save`, `CloseCash.Save`, …): apply `VisuAccentButtonStyle` via `SetResourceReference` and raise `MinWidth` to `VisuId.PrimaryButtonMinWidth` (100) if below it. At most one button per window is accented.
4. All failures are reported through `AppHost.Errors.Report` (no silent catch).

This closes the audit finding for the document/card family the audits sampled (JrnWin, CshWin, OrdWin, InvoiceWin, ChqWin, BankRecWin, PayRecWin via Save, AccCard, ProjWin, AssetWin, MfgWin, the F2 family, the master cards, …) without a single per-window style edit. Windows whose culminating action is neither Save nor Post are **not** force-accented by guesswork — they are flagged (F-3).

---

## 4. Schedule tints (item 13-3)

Palette: `Themes/VisuId.xaml` grew from 16 → **43 `ScheduleTint*Brush` tokens**, all in the same light pastel band as the originals (hover/selection states stay legible). Peach (the old spare) is now used by PosWin.

**New claims this step (28):**

| Window | Tint | Source |
|---|---|---|
| PosWin | Peach | audit-named (visual-invoicing #8) |
| CloseCashWin | Coral | audit-named (visual-cash #4) |
| ChqListWin | Indigo | audit-named (visual-cheques #2) |
| DocSearchWin | Denim | audit-named (visual-invoicing #8, visual-orders #2) |
| MatGroupWin | Honey | audit-named (visual-materials #3) |
| MfgListWin | Fern | audit-named (visual-mfg-hr-crm #3) |
| DefManagerWin | Jade | audit-named (visual-mfg-hr-crm #3) |
| OrderTrackWin | Cyan | audit-named (visual-orders #2) |
| DeliveryDeskWin | Lime | audit-named (visual-orders #2) |
| PartyManagerWin | Blush | audit-named (visual-parties #1) |
| PartySearchWin | Mauve | audit-named (visual-parties #1) |
| CustomReportsWin | Violet | audit-named (visual-report-family §2.1) |
| FinancialAnalysisWin | Plum | audit-named (visual-report-family §2.2) |
| ActivityLogWin | Olive | audit-named (visual-shell-admin §12) |
| BinWin | Bronze | audit-named (visual-shell-admin, BinWin) |
| StockCountDialog (in WhsWin.xaml.cs) | Sage | audit-named (visual-stock-reports #3) |
| AddonsWin | Wheat | rule completion (grid, unnamed by audits) |
| BcdGenWin | Lagoon | rule completion |
| CcListWin | Orchid | rule completion |
| CurCard | Peridot | rule completion |
| GroupManagerWin | Topaz | rule completion |
| InstallmentsWin | Quartz | rule completion |
| LoyaltyWin | Garnet | rule completion |
| PosSyncWin | Cobalt | rule completion |
| RolloverWin | Cedar | rule completion |
| SerWin | Onyx | rule completion |
| SupportWin | Ember | rule completion |
| MatMergeWin (in WhsWin.xaml.cs) | Willow | rule completion |

**Pre-existing claims (15, unchanged):** JrnWin Blue · OrdWin Sky · ProjWin Aqua · CshWin Teal · InvoiceWin Mint · MfgWin Green · MatCard Amber · MatDirWin Sand · CstCard Rose · CoaWin Lavender · PayRecWin Lilac · ChqWin Periwinkle · BankRecWin Slate · AssetWin Stone · RptBase Silver (whole report family — deliberate documented exception, flag F-4) · (Peach was spare).

Uniqueness: every tint name is claimed by exactly one window class (the one apparent exception in raw file grep — "Sage" inside `WhsWin.xaml.cs` — is the nested `StockCountDialog` class span; the WhsWin card itself claims nothing and has no schedule). Three claims are inert-by-design and pre-date this step: CoaWin (tree, no DataGrid), CstCard and MatDirWin (no DataGrid surface for the tint to reach — MatDirWin's is a ListView; converting it is a behavior-adjacent rebuild, flag F-5).

---

## 5. Shell font token + small leftovers (items 13-4 / 13-5)

- `ShellChromeFontFamily`: `Tahoma` → `Segoe UI Variable Text, Segoe UI` (identical value to the central `ApplicationFontFamily` token) in **both** dictionaries defining it: `Themes/VisuId.xaml` and `Themes/ShMenu.xaml`. Consumers (MWin, BinWin, DatWin window fonts; `App.xaml` chrome styles; `ShRibn.xaml` ribbon styles) all pick it up through the token — no consumer was edited.
- `MWin.xaml`: the menu style's literal `Value="Tahoma"` → `{DynamicResource ShellChromeFontFamily}`; dead RadioPanel (`Option 1/2/3`, shown only when minimized, no code references) and its orphaned `RadioPanelVisibility` style removed.
- Remaining `Tahoma` strings in the repo are **not** the shell: `Configuration/AppConfig.cs` `SecondaryFont`/`ArabicFont` defaults (user-configurable report/print font settings — changing a stored-config default is a data decision, flag F-6) and `BcdDesWin`'s font-picker list (Tahoma offered as one selectable font among eight — correct as-is).

---

## 6. Comparison tables — CoaWin / AccCard (comparison gate)

Source: `research/sweep/compare-journal-coa.md` §2/§3 (grades unchanged by this step — it closes the **localization row**, which the comparison file recorded as defect C-4/C-5 in `journal-coa.md`; all functional rows stand exactly as graded there and in steps 1–12).

### 6.1 CoaWin — Chart of Accounts

| Capability | ShouTech CoaWin | Al-Ameen V10 ⚠ / Phenix v10 ⚠ | ERPNext | Odoo | SAP B1 |
|---|---|---|---|---|---|
| Tree / hierarchy display | ✓ tree, code + name | ✓ | ✓ tree | ✗ flat list + groups | ✓ tree by Drawer |
| Search / filter | ✓ code/name with ancestor paths | ✓ | ✓ + company filter | ✓ | ✓ |
| Add Child | ✓ (context menu → AccCard) | ✓ | ✓ | n/a | ✓ |
| Add Root | ✗ (flag F-7 / defect C-7) | ✓ | ✓ | ✓ | ✓ |
| Edit account | ✓ opens AccCard | ✓ | ✓ | ✓ | ✓ |
| Delete / Disable from manager | ◐ only inside AccCard | ✓ | ✓ | ✓ | ✓ |
| Import tree | ✓ CSV/XLSX/`.sta` upsert | ✓ ⚠ | ✓ Importer | ✓ CSV | ✓ DTW |
| Export / Print tree | ✓ XLSX/CSV/PDF/`.sta` | ✓ | ✓ | ✓ | ✓ |
| Account Type / Root Type per node | ✗ (defect family C-1/R-2, steps 1–3 scope) | ✓ ⚠ | ✓ Root Type + Account Type | ✓ Type | ✓ |
| Balance per node | ✗ | ✓ ⚠ | ✓ | ✓ | ✓ |
| Drill to Statement / Ledger | ◐ both open the one statement report | ✓ | ✓ | ✓ | ✓ |
| Move / reparent | ◐ via AccCard parent combo (flag F-1 / C-6) | ✓ ⚠ | ✓ | n/a | ✓ |
| Merge accounts | ✗ | ◐ ⚠ | ✓ | ◐ | ✗ |
| Template charts | ◐ one hard-coded seed (flag F-2 / C-8) | ✓ ⚠ | ✓ per-country | ✓ localization packages | ✓ |
| Status visibility in tree | ◐ Group/Account label yes; status not shown | ✓ | ✓ | ✓ | ✓ |
| Opening balances from CoA | ✗ | ✓ ⚠ | ✓ | ✓ | ✓ |
| Freeze account | ✗ | ◐ | ✓ | ✓ | ✓ |
| **Localization (4 languages, live switch)** | **✓ — CLOSED BY STEP 13** (was ✗: empty stub, 36 placeholder literals, menu items headed "Header") | ✓ | ✓ | ✓ | ✓ |

Grade (unchanged): **9.5 / 18 = 53%** CORE — localization was not a scored CORE row; the scored gaps (types, balances, root creation, merge, freeze, opening balances) are functional builds outside this step's mandate.

### 6.2 AccCard — Account Card

| AccCard field | ShouTech | Al-Ameen ⚠ | ERPNext `Account` | Odoo `account.account` | SAP B1 |
|---|---|---|---|---|---|
| Code / Account Number | ✓ digits-only, prefix = parent | ✓ | ✓ | ✓ Code | ✓ |
| Name (bilingual) | ✓ Name + English Name | ✓ | ◐ single name | ◐ single name | ◐ single name |
| Parent | ✓ combo (flag F-1 / C-6) | ✓ | ✓ Parent Account | ✓ via Account Group | ✓ Father Account |
| Is Group / postable | ✓ Posting Mode | ✓ | ✓ Is Group | ✓ | ✓ |
| Status | ✓ Active/Disabled | ✓ | ✓ Disabled | ✓ Deprecated | ✓ |
| Root Type | ✗ inferred from code prefix | ✓ | ✓ mandatory | ✓ derived | ✓ Drawer |
| Report Type | ✗ | ✓ | ✓ | ✓ derived | ✓ derived |
| Account Type | ✗ | ✓ | ✓ (~30) | ✓ Type (17) | ✓ |
| Currency per account | ◐ Foreign-Currency checkbox only | ✓ | ✓ Account Currency | ✓ Currency | ✓ |
| Opening balance | ✗ | ✓ via opening voucher | ✓ via Opening Entry | ✓ Opening Dr/Cr | ✓ |
| Balance Must Be | ✗ | ◐ | ✓ | ◐ | ◐ |
| Freeze | ✗ (Disable only) | ◐ | ✓ | ✓ Deprecated | ✓ Lock |
| Allow reconciliation | ✓ | ✓ | ✓ | ✓ | ✓ |
| Tax on the account | ◐ flag + rate, unconsumed on journal path | ✓ | ✓ | ✓ default Taxes | ✓ Tax Group |
| Cost-center behaviour | ◐ mode combo + checkbox, unenforced at posting | ✓ | ✓ | ✓ analytic rules | ✓ Distribution Rule |
| Default cost center value | ✗ | ✓ | ◐ | ✓ | ✓ |
| Default account flag | ✓ (ShouTech plus) | ◐ | ◐ company-level | ◐ | ◐ |
| Live Balance / Debit / Credit | ✗ in substance — cached counters (C-2, addressed by step-3 GL work, not this step) | ✓ | ✓ | ✓ | ✓ |
| Budget flag | ◐ checkbox; budget engine is Phase-B | ✓ ⚠ | ✓ separate documents | ✓ | ✓ |
| Risk level | ✓ (ShouTech-only) | ✗ | ✗ | ✗ | ✗ |
| Statement / Ledger buttons | ◐ both open the same statement (C-9) | ✓ distinct | ✓ | ✓ | ✓ |
| **Localization (4 languages, live switch)** | **✓ — CLOSED BY STEP 13** (was ✗: empty stub, 23 placeholder literals incl. 3 bottom-bar buttons captioned "Content", 16 hardcoded message boxes, combo text = stored values) | ✓ | ✓ | ✓ | ✓ |

Grade (unchanged): **11.0 / 22 = 50%** CORE — the missing classification spine (Root Type, Account Type, currency code, opening balance, freeze) is functional scope for a later step, not visual leftovers.

---

## 7. Proof & gates

| Check | Result |
|---|---|
| Full build `dotnet build ShouTech.sln --no-incremental -p:EnableWindowsTargeting=true` | **0 errors, 0 warnings** |
| `tools/audit_window_context.py` | **exit 0** |
| Lang parity (`tools/verify_english_source.py` catalog section) | **missing = 0 in all 4 catalogs**; extras unchanged from base profile (Ar 90 / Fr 9 / Tr 9, all pre-existing); En 2458 → 2603 keys (+145) |
| Placeholder literals in CoaWin/AccCard XAML (`Text="Text"`, `Content="Content"`, `Header="Header"`, `ToolTip="ToolTip"`) | **0** (was 59) |
| `ApplyLocalizedText()` empty stubs in CoaWin/AccCard | **0** — both are real implementations (§2.1) |
| User-facing literals in CoaWin/AccCard code-behind | **0** beyond the justified list in §2.4 (verified by grep over MessageBox/dialog/menu/format sites) |
| Every new catalog key referenced | **145/145 used** (programmatic scan of both windows + F2WinUi + BaseWin) |
| Tint inventory (per-class brace-matched scan) | 43 claims, **every schedule-bearing window class has a tint, every tint unique** (RptBase family Silver = documented exception, F-4); all claimed names resolve to a `ScheduleTint*Brush` token |
| Tahoma in shell (`MWin.xaml[.cs]`, `Themes/`, `App.xaml`) | **0** |
| Behavior/data diff evidence | Diff touches only: XAML text/key attributes, the two windows' text-source code, `BaseWin` (additive: `Tf` + primary convention), theme tokens, one-line tint claims, catalog JSONs. No SQL, no repository, no posting, no schema file in the diff (`git diff --stat` vs `08e0498`) |
| WPF visual verification | **OWED on Windows** — placeholder removal, live relocalization, accent rendering, tint rendering, Segoe shell font all need a Windows run |

## 8. Owner flags / GAPs (not built — by mandate or by size)

- **F-1 (C-6, functional — do not fix here per mandate):** AccCard parent identity is still string-parsed (`SelectedParentCode()` splits the `"code - name"` combo text on `" -"`); CoaWin reparenting has no cycle check. Exposed by the C-4/C-5 work, untouched by it.
- **F-2 (C-8):** `SeedDefaultChart` writes English names into **both** name fields of the seed chart (data, not UI text). Localizing seed data per first-run language is a data decision for the owner.
- **F-3:** Primary actions that are neither Save nor Post get no central accent (e.g. DocSearchWin Search, PayRecWin Allocate — that window has no Save/Post at all, MfgListWin Open, PartySearchWin Select, BkpWin Restore). Accenting them needs a per-window primary declaration — proposed: an opt-in `LocProps`-style attached marker in a follow-up, not guesswork in the central rule.
- **F-4:** Report family still shares RptBase **Silver** (deliberate, documented family exception to the unique-tint rule) — owner confirmation that the exception stands is still open from the sweep.
- **F-5:** MatDirWin's schedule is a ListView, so its Sand tint (and now any tint) cannot render; CoaWin's Lavender is inert on a tree. Converting MatDirWin's list to the central DataGrid is a separate rebuild.
- **F-6:** `AppConfig.SecondaryFont/ArabicFont` default to `"Tahoma"` — user-facing *settings defaults* for report fonts, not the shell token. Changing stored-config defaults is the owner's call.
- **F-7 (C-7):** CoaWin's primary actions remain context-menu-only (Add Child; no root creation; Delete only inside AccCard) — visual-journal-coa Axis 2 FAIL-SCATTERED. Adding a bottom-bar New/Add Child button is a UI-structure change beyond this step's text-source mandate.
- **F-8:** CoaWin's local TreeViewItem template still bypasses `VisuTreeViewStyle`, and its title block still docks Right — identity items larger than this step's localization mandate.
- **F-9:** JrnWin's 17 error-path `InvalidOperationException` literals and the six remaining per-window `"Invalid number format."` copies (AssetWin, CstCard, CurCard, DeliveryDeskWin, ProjWin, RepCard) — exception-text localization beyond the shared F2 helper fixed here; the per-window copies are the known local-helper duplication class.
- **F-10:** Windows whose buttons are built in their own `Loaded` handlers (after BaseWin's) won't receive the central accent until a later pass — the convention applies at BaseWin load; a `OnLanguageChanged`-time re-application is a possible follow-up if a concrete window is found.

## 9. Pillars checklists (per touched window)

**CoaWin** — UI: **PROVEN** (all captions/menus/dialogs/print localized ×4, placeholders 0, live relocalization implemented) · DB: unchanged — text-source edits only (no AccRepo/SQL in diff) · Linking: unchanged (AccCard/statement navigation untouched) · Posting: n/a (no posting in this window; unchanged) · Persistence: unchanged (import/export file formats byte-identical; canonical Type tokens preserved, §2.2/§2.4) · Downstream: unchanged (exported CSV/XLSX/`.sta` payloads identical; only dialog chrome text localized).
**AccCard** — UI: **PROVEN** (as above; Close token fix; Save now centrally accented) · DB: unchanged (stored combo values byte-identical via AccOption.Value) · Linking: unchanged · Posting: n/a unchanged · Persistence: **PROVEN unchanged** — every save path reads `SelectedValue()` canonical tokens; defaults identical · Downstream: unchanged.
**BaseWin** — UI: additive central convention (§3) + `Tf` helper · all other pillars: unchanged (no data path touched).
**Tint-claim windows (28 files, one line each)** — UI: tint claim only · DB/linking/posting/persistence/downstream: unchanged — one-line override diff evidence per file.
**MWin** — UI: font token + dead scaffold removal · DB/linking/posting/persistence/downstream: unchanged (no code-behind change).
**F2WinUi** — UI: one exception message now catalog-sourced · everything else unchanged (same message text in English).
