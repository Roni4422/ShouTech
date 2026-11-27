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
- Company administrator passwords require **at least 8 characters** (OWASP
  Authentication Cheat Sheet baseline) and allow up to 128 characters. The
  user chooses the composition freely: letters, digits, symbols, spaces, or
  any combination. No uppercase/lowercase/digit/symbol mixture is required,
  and common-password or username-equality checks do not override the user's
  choice.
- Enforced centrally by `PwdPolicy.ValidateConfigured` in
  `CoCreateSvc.CreateAsync` before hashing or persistence. Passwords shorter
  than eight characters or longer than the configured maximum are rejected.
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
**`SqlIdentifier`** (see Security services):

- Shape gate: only `[A-Za-z_][A-Za-z0-9_]*` is accepted, then double-quoted.
  Anything else throws before reaching the database.
- Allow-list gate: `RequireTableAsync` / `RequireColumnAsync` verify the
  identifier exists in the live schema.

## 4. PostgreSQL TLS

Prefer `SHOUTECH_PG_SSLMODE=Require` (or VerifyFull) in production. Defaults
are documented in cfg/appset.json comments; never disable TLS on a network
path that carries credentials or financial data.

## 5. Webhook signing

Outbound webhooks are HMAC-signed with `SHOUTECH_WEBHOOK_SECRET`. The signer
is covered by unit tests in Kern.Tests.

## 6. Remaining backlog (honest)

- Formal ISO 27001 / SOC 2 programs are not claimed.
- Password policy: minimum 8 characters, maximum 128, with no character-class requirements.
- Continuous security review of new UI surfaces that accept file uploads or
  free-form SQL report definitions.
- Periodic dependency vulnerability review for NuGet packages.

---

*Aligned with docs/INTERNATIONAL_STANDARDS_COMMITMENT.md (2026-11-27).*
