# ShouTech ERP — Enterprise Security Controls (Phase 4)

This document describes the security controls implemented in Phase 4
(branch `feat/enterprise-security-phase4`), how an operator configures them
for production, and the honest remaining backlog. It complements
`docs/usr.md` and `docs/ACCOUNTING_CHART_PERIODS_YEAR_CLOSE.md`.

## 1. Authentication hardening

### Password storage (verified, unchanged)
- `PwdHash` (cod/app/src/Services/Auth.cs): PBKDF2-HMAC-SHA512, 64-byte random
  salt, **600,000 iterations** (above current OWASP guidance for both
  PBKDF2-HMAC-SHA256 at 600k and PBKDF2-HMAC-SHA512 at 210k).
- Hashes are stored versioned (`v1$PBKDF2-SHA512$<iterations>$<hash>`); Verify
  honors the stored iteration count (bounded 100k–2M to prevent tamper-driven
  CPU exhaustion) and compares with `CryptographicOperations.FixedTimeEquals`.
- **Rehash-on-login (new in Phase 4):** the audit found this was claimed but
  not wired in `AuthServ`. After a successful database sign-in, a stored hash
  below the current work factor is transparently rehashed (same transaction
  as the lockout reset). Verified by the runtime smoke test (100k → 600k).

### Login lockout (new, migration 029)
- `users.failed_login_count` + `users.locked_until` (SQLite + PostgreSQL).
- Limits come from configuration: `Security.MaxLoginAttempts` (default 5) and
  `Security.LockoutDurationMinutes` (default 15) in cfg/appset.json.
- Enforced on all database sign-in paths (username and passcode, both
  providers): a locked account is refused **before** password verification;
  failures increment the counter; the failure that reaches the threshold sets
  `locked_until`; success resets the counter.
- The local company-administrator store (CoLocalStore) enforces the same
  limits with state persisted on the company record
  (`AdminFailedLoginCount` / `AdminLockedUntilUtc`).
- Passcode-only sign-in cannot be attributed to an account on failure, so no
  counter is incremented for unmatched passcodes — but a passcode that matches
  a **locked** account is refused, so the passcode path cannot bypass lockout.
- Transitions live in the pure `LoginLockout` state machine (unit-tested).

### Security audit trail (new, migration 029)
- Table `security_audit_log` (company, user, username, event type, details,
  timestamp; indexed by username/event/company + time).
- Events: `LOGIN_SUCCESS`, `LOGIN_FAILURE`, `ACCOUNT_LOCKED`, `LOGIN_LOCKED`,
  `DATA_EXPORT`, `DATA_EXPORT_DENIED`. Lockout events are additionally
  forwarded to `IAuditService.LogSecurityViolationAsync` (file audit).
- Written by `SecurityAuditLog` on a best-effort basis: an audit failure is
  logged and never changes an authentication outcome. Passwords are never
  written to the audit log.

### Password policy
- Company administrator passwords require **at least 3 characters** and allow
  up to 128 characters. The user chooses the composition freely: letters,
  digits, symbols, spaces, or any combination. No uppercase/lowercase/digit/
  symbol mixture is required, and common-password or username-equality checks
  do not override the user's choice.
- Enforced centrally by `PwdPolicy.ValidateConfigured` in
  `CoCreateSvc.CreateAsync` before hashing or persistence. Passwords shorter
  than three characters or longer than the configured maximum are rejected.
- Not applied to the DatWin dataset-package password: that is an export-file
  encryption passphrase, not an account credential (it has its own ≥8 rule in
  the UI). Changing it is an operator decision, noted here deliberately.

## 2. Secrets management

- cfg/appset.json previously shipped placeholder literals
  (`YourTwilioApiSecret`, `YourStripeWebhookSecret`, `YourEncryptionKeyHere`,
  Firebase keys, …). Phase 4 replaced **all** of them with empty strings. No
  code referenced the placeholder literals (verified by repository search).
- **Production rule:** real secrets must come from environment variables or
  the SecEng/DPAPI secure storage — never from files committed to the repo.
  Relevant environment variables:
  | Variable | Purpose |
  |---|---|
  | `SHOUTECH_CONNECTION_STRING` / `SHOUTECH_DB_CONNECTION` | Database connection (contains DB credentials) |
  | `SHOUTECH_DB_PROVIDER` | `SQLite` or `PostgreSQL` |
  | `SHOUTECH_PG_SSLMODE` | PostgreSQL TLS mode (see §4) |
  | `SHOUTECH_WEBHOOK_SECRET` | Webhook HMAC signing secret (see §5) |

## 3. SQL identifier safety

Values are parameterized everywhere in the audited services; identifiers
(table/column names) cannot be parameterized, so Phase 4 centralized them in
**`SqlIdentifier`** (cod/app/src/Services/Security.cs):

- Shape gate: only `[A-Za-z_][A-Za-z0-9_]*` is accepted, then double-quoted.
  Anything else throws before reaching the database.
- Allow-list gate: `RequireTableAsync` / `RequireColumnAsync` verify the
  identifier exists in the live schema (`sqlite_master` /
  `information_schema`) — identifiers must come from the application's own
  schema catalog, never raw input.
- Applied at: `DataExportSvc` (its `QuoteIdentifier` now delegates; export
  additionally requires the dataset to be in the static catalog *and* the
  table to exist live), `RecycleBinSvc` (all former private regex checks and
  its `Quote` now delegate; its table names come from a static catalog), and
  `SchMig` (the previously unquoted `PRAGMA table_info(...)` now quotes via
  the helper; its conditional-directive identifiers were already
  regex-validated during migration-file parsing).
- Spots proven safe and left as-is: `BuildFilter` in DataExportSvc (static
  fragments selected by an internal enum), import upserts (column list is
  derived from the target table's real columns; values parameterized).

## 4. Database hardening

- **Foreign keys (SQLite):** `PRAGMA foreign_keys` is per-connection and was
  previously set only by the App.xaml.cs schema bootstrapper. Centrally
  prepared connection strings now carry `Foreign Keys=True`
  (`DatabaseRuntime.NormalizeSqliteConnectionString` /
  `PrepareConnectionString`, also applied by `AppHost.ResolveDatabaseConnection`
  and the svc worker), so every connection enforces FK constraints.
- **Indexes (migration 029, both providers):** sales/purchase documents by
  (company_id, doc_date) and (company_id, status); journal lines by
  account_id; document reversals by (company_id, original kind/id); the new
  audit table's indexes. Earlier migrations already covered products, stock,
  journals, party ledger, allocations, projects and the audit tables.
- **Uniqueness:** `UNIQUE(company_id, username)` on `users` already existed
  on both providers (verified in 001/002) — no migration needed.
- **PostgreSQL transport security:** `SHOUTECH_PG_SSLMODE` =
  `Disable | Prefer | Require` (default `Prefer`; unknown values fall back to
  `Prefer`). Applied centrally when connection strings are built:
  `DatabaseRuntime.PrepareConnectionString` (app + AppHost factories),
  `tools/SqlMigrator`, and the svc worker. The environment value overrides
  any SSL Mode in the configured string. **Production: set
  `SHOUTECH_PG_SSLMODE=Require`** (add certificate validation parameters to
  the connection string as your CA requires).

## 5. Webhook signing (svc)

- When `SHOUTECH_WEBHOOK_SECRET` is set, the outbound worker adds
  `X-ShouTech-Signature: sha256=<hex>` — HMAC-SHA256 over the exact body
  bytes transmitted, keyed by the secret (`WebhookSigner`, RFC 4231
  test-vector verified).
- When not set, webhooks are sent unsigned and the worker logs a warning
  once. At-least-once delivery semantics are unchanged; receivers should
  verify the signature and dedupe on their side.

## 6. Export governance

- DatWin previously ran full-package and structured exports with **no
  permission check** (file-log only). Now, in `StartButton_Click`, every
  export mode requires `PermCat.Data.Export` (`DATA.EXPORT`, already seeded
  in migration 004); a denied attempt shows a warning and is audited
  (`DATA_EXPORT_DENIED` + IAuditService security violation).
- Every completed export writes a `DATA_EXPORT` audit event: format, dataset
  key + table (structured) or file name + rows + tables (packages), user,
  company.

## 7. Operator production checklist

1. Set `SHOUTECH_PG_SSLMODE=Require` for PostgreSQL deployments.
2. Set `SHOUTECH_WEBHOOK_SECRET` (long random value) and configure receivers
   to verify `X-ShouTech-Signature`.
3. Provide DB credentials via environment, not cfg files; keep
   cfg/appset.json free of real secrets (it ships with empty secret fields).
4. Review `Security` settings: MaxLoginAttempts 5, LockoutDurationMinutes 15,
   password policy: minimum 3 characters, maximum 128, with no character-class requirements.
5. Apply migrations with tools/SqlMigrator (029 is required for lockout and
   the audit table; the app degrades safely — no lockout, no DB audit — on an
   unmigrated database, so migrate before relying on these controls).
6. Periodically review `security_audit_log` for ACCOUNT_LOCKED /
   DATA_EXPORT_DENIED spikes.

## 8. Backlog (honest, ranked)

1. **SQLite table-rebuild constraints:** adding CHECK constraints (e.g.
   `failed_login_count >= 0`) to existing SQLite tables requires a table
   rebuild; deliberately not attempted on live data. PostgreSQL can take
   `ADD CONSTRAINT` migrations safely in a follow-up.
2. **SecEng/DPAPI secret vault:** configuration secrets still rely on
   environment variables; wiring AppConfig secret fields to the native SecEng
   vault (Security.cs interop exists) is the next step.
3. **Passcode sign-in design:** password-only sign-in is inherently weaker
   (shared-secret identification). Consider removing it or restricting it to
   local single-user installs; lockout-bypass is prevented, but unattributed
   brute force is only audited, not throttled per-account.
4. **Password history/expiry:** `PreventReuseCount` (5) and `ExpirationDays`
   (90) exist in config but are not enforced — needs a password-history table
   and a change-password flow (no user-facing change-password window exists).
5. **Connection-string builders outside central paths:** ComWin's
   test-connection builder and CoCreateSvc's migration connection resolve
   strings themselves; route them through `DatabaseRuntime.PrepareConnectionString`
   in a follow-up (DatWin's builder only reads the database name).
6. **Two-factor authentication:** settings exist (`TwoFactorAuth`) with no
   implementation.
7. **PostgreSQL verification:** lockout/audit code paths for PostgreSQL are
   compile-verified and mirror the SQLite logic that passed the runtime
   smoke test, but no live PostgreSQL was available in the build environment.
