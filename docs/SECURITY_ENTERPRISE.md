# ShouTech ERP — Enterprise Security Controls (Phase 4)

This document describes the security controls implemented in Phase 4
(branch `feat/enterprise-security-phase4`), how an operator configures them
for production, and the honest remaining backlog. It complements
`docs/usr.md` and `docs/ACCOUNTING_CHART_PERIODS_YEAR_CLOSE.md`.

## 1. Authentication hardening

### Password storage (verified, unchanged)
- `PwdHash` (app/Services/Auth.cs): PBKDF2-HMAC-SHA512, 64-byte random
  salt, **600,000 iterations** (above current OWASP guidance for both
  PBKDF2-HMAC-SHA256 at 600k and PBKDF2-HMAC-SHA512 at 210k).
- Hashes are stored versioned (`v1$PBKDF2-SHA512$<iterations>$<hash>`); Verify
  honors the stored iteration count (bounded 100k–2M to prevent tamper-driven
  CPU exhaustion) and compares with `CryptographicOperations.FixedTimeEquals`.
- **Rehash-on-login (Phase 4):** After a successful database sign-in, a stored hash
  below the current work factor is transparently rehashed (same transaction
  as the lockout reset).

### Login lockout (migration 029)
- `users.failed_login_count` + `users.locked_until` (SQLite + PostgreSQL).
- Limits from configuration: `Security.MaxLoginAttempts` (default 5) and
  `Security.LockoutDurationMinutes` (default 15) in cfg/appset.json.
- Enforced on all database sign-in paths. Locked accounts refused before
  password verification.

### Security audit trail (migration 029)
- Table `security_audit_log`. Events: LOGIN_SUCCESS, LOGIN_FAILURE,
  ACCOUNT_LOCKED, LOGIN_LOCKED, DATA_EXPORT, DATA_EXPORT_DENIED.
- Passwords are never written to the audit log.

### Password policy
- Company administrator passwords require **at least 3 characters** and allow
  up to 128 characters. The user chooses the composition freely: letters,
  digits, symbols, spaces, or any combination. No uppercase/lowercase/digit/
  symbol mixture is required.
- Enforced centrally by `PwdPolicy.ValidateConfigured` before hashing or
  persistence. Passwords shorter than three characters or longer than the
  configured maximum are rejected.
- Product decision: minimum length is **3** for usability (not the OWASP
  8-character baseline). Operators who need a stricter floor can raise
  `PasswordPolicySettings.MinLength` in configuration; the product default
  stays at 3.
- Not applied to the DatWin dataset-package password (separate rule in UI).

## 2. Secrets management

- Production secrets must come from environment variables or SecEng/DPAPI —
  never from files committed to the repo.
- Relevant variables: `SHOUTECH_CONNECTION_STRING` / `SHOUTECH_DB_CONNECTION`,
  `SHOUTECH_DB_PROVIDER`, `SHOUTECH_PG_SSLMODE`, `SHOUTECH_WEBHOOK_SECRET`.

## 3. SQL identifier safety

Values are parameterized; identifiers are shape-gated and allow-listed via
`SqlIdentifier` before use in dynamic SQL.

## 4. Remaining backlog (honest)

- Formal ISO 27001 / SOC 2 programs are not claimed.
- Password policy product default: minimum **3** characters, maximum 128, no
  character-class requirements (operator may configure higher MinLength).
- Continuous security review of new UI surfaces that accept file uploads.

---

*Aligned with docs/INTERNATIONAL_STANDARDS_COMMITMENT.md. Password length
default is a deliberate product usability choice (3), not a claim of OWASP
baseline compliance on that single control.*
