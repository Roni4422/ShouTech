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
