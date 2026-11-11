# Authentication and Authorization Security

## Scope

The WPF client uses an offline-first, company-scoped desktop sign-in flow. It is not an HTTP authentication server and does not issue bearer tokens. `AuthResult.Token` remains null by design; callers must not interpret a successful desktop session as a JWT or API credential.

## Sign-in sequence

1. The company selector sets `AppState.CurrentCompanyCode`.
2. `LogWinVM` validates the visible fields and calls `IAuthSvc.LoginAsync`.
3. `AuthServ` verifies either the local company administrator record or a database user.
4. Database users have their effective permissions loaded for the exact user ID and selected company before the session is published.
5. Only after permission loading succeeds are `UsrSession` and `AppState.CurrentUser` updated.
6. Logout clears the authenticated user and the permission snapshot.

A permission-load failure is a sign-in failure. The application must fail closed rather than opening a partially authorized session.

## Password storage

Passwords are not encrypted or stored in plaintext. `PwdHash` uses PBKDF2-HMAC-SHA512 with a random 64-byte salt and a versioned work factor. Verification uses a constant-time comparison. Unknown legacy formats are deliberately rejected: do not add guessed hash algorithms. Identify the actual legacy format and migrate it explicitly before enabling compatibility.

## Authorization isolation

Runtime permission queries must match the authenticated database user ID and the currently selected company code. Username-only matching is prohibited because usernames may repeat across companies. Role grants are additive; an explicit user override takes precedence over the combined role grants.

Local company administrator credentials are stored as a salted password hash in the per-user application data directory. Protect that directory with Windows account ACLs and do not copy its contents into shared or source-controlled locations.

## Failed sign-in controls

Failed and successful sign-in attempts are sent to the application authentication log. The desktop process applies a temporary lockout after the configured number of failures (`Security.MaxLoginAttempts`) for `Security.LockoutDurationMinutes`.

**Limitation:** this lockout is process-local and resets when the application exits. It is a local guessing-resistance control, not a durable account lockout for a multi-user server. A shared deployment must move attempt counters and lockout state into a transactional database or identity provider before it is exposed to a network.

## Token and API boundary

No JWT, refresh-token store, token signature validation, token revocation, or bearer middleware is implemented in this WPF client. Do not invent a token or store a long-lived token in `AuthResult`. If a future React or remote client needs authentication, implement a separate server-side identity boundary with short-lived signed access tokens, rotating refresh sessions, revocation, TLS, and company-scoped authorization. Keep desktop local sign-in independent so offline operation remains possible.

## Verification checklist

- [ ] Build the WPF application and test project on Windows with .NET 8.
- [ ] Run the SQLite migration and authorization tests against a fresh database.
- [ ] Verify a user cannot load permissions from another company even when usernames match.
- [ ] Verify role grants are unioned and explicit user denies override them.
- [ ] Verify failed permission loading prevents session creation.
- [ ] Verify logout clears both user and permission state.
- [ ] Verify lockout settings are read from application configuration.
