// ========================================================================
// FILE: Services/Auth.cs
// PROJECT: SHOUTECH ERP — Authentication, session, password hashing
// Merged: UsrAccLvl, IAuthSvc, UsrSession, SessionManager, PwdHash, AuthServ
// ========================================================================

#nullable enable

using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.App.Common;
using ShouTech.App.Configuration;

using ShouTech.Domain;
namespace ShouTech.App.Services
{

    // ========== UsrAccLvl.cs ==========
public enum UsrAccLvl
    {
        Viewer = 0,
        User = 1,
        Administrator = 2
    }

    // ========== IAuthSvc.cs ==========
public interface IAuthSvc
    {
        Task<AuthResult> LoginAsync(string username, string password);
        Task LogoutAsync();
        Task<UserInfo?> GetCurrentUserAsync();
        bool IsAuthenticated { get; }
    }

    public class AuthResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public UserInfo? User { get; set; }
        public string? Token { get; set; }

        /// <summary>
        /// Catalog error code for a failed sign-in (AUTH-2xxx). Carried
        /// for coded logging only: the user-facing message deliberately
        /// stays the generic invalid-credentials text for every failure,
        /// so the code never leaks whether an account exists or is locked.
        /// </summary>
        public string? ErrorCode { get; set; }
    }

    public class UserInfo
    {
        public string Id { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
        public string RoleCode { get; set; } = string.Empty;
        public UsrAccLvl AccessLevel { get; set; } = UsrAccLvl.User;
        public bool IsSuperAdmin { get; set; }
        public string[]? Permissions { get; set; }
        public string? CompanyId { get; set; }
        public string? CompanyName { get; set; }
        public bool IsAuthenticated { get; set; }
        public DateTime LastLogin { get; set; }
        public string? AvatarUrl { get; set; }

        public bool IsAdministrator => AccessLevel == UsrAccLvl.Administrator || IsSuperAdmin;
        public bool IsViewer => AccessLevel == UsrAccLvl.Viewer;
        public bool IsReadOnly => IsViewer;
    }

    // ========== UsrSession.cs ==========
/// <summary>
    /// Application-wide authenticated session snapshot for shell UI and services.
    /// </summary>
    public static class UsrSession
    {
        public static UserInfo? Current { get; private set; }

        public static bool IsAuthenticated => Current?.IsAuthenticated == true;
        public static bool IsAdministrator => Current?.IsAdministrator == true;
        public static bool IsViewer => Current?.IsViewer == true;
        public static bool IsReadOnly => Current?.IsReadOnly == true;

        public static void Apply(UserInfo? user) => Current = user;

        public static void Clear() => Current = null;
    }

    // ========== SessionManager.cs ==========
/// <summary>
    /// </summary>
    public class SessionManager : ISessionManager
    {
        public string? CurrentUser { get; set; }
        public bool IsLoggedIn => !string.IsNullOrEmpty(CurrentUser);

        public void Login(string username)
        {
            CurrentUser = username;
            AppHost.Logging.LogInfo("Session", $"✅ User session started: {username}");
        }

        public void Logout()
        {
            if (CurrentUser != null)
            {
                AppHost.Logging.LogInfo("Session", $"🚪 User session ended: {CurrentUser}");
                CurrentUser = null;
            }
        }
    }

    // ========== PwdHash.cs ==========
/// <summary>
    /// Enterprise password hashing service.
    ///
    /// This class is intentionally stateless and thread-safe.
    /// </summary>
    public static class PwdHash
    {
        // --------------------------------------------------------------------
        // Current algorithm
        // --------------------------------------------------------------------

        private const string CurrentAlgorithm = "PBKDF2-SHA512";
        private const string CurrentVersion = "v1";

        // 64 bytes = 512 bits.
        private const int SaltSizeBytes = 64;
        private const int HashSizeBytes = 64;

        // PBKDF2 work factor.
        //
        // This value is deliberately centralized so it can be increased
        // in a future migration without changing the public API.
        private const int DefaultIterations = 600_000;

        // Maximum accepted work factor when reading a stored versioned hash.
        //
        // This prevents a corrupted/tampered database value from forcing the
        // application to perform an absurd amount of CPU work.
        private const int MaximumAcceptedIterations = 2_000_000;

        // --------------------------------------------------------------------
        // Public API
        // --------------------------------------------------------------------

        /// <summary>
        /// Creates a secure password hash and salt.
        ///
        /// The resulting values are intended to be stored separately in:
        ///
        ///     PasswordHash
        ///     PasswordSalt
        ///
        /// The returned hash is versioned so future algorithms can be
        /// introduced without breaking existing accounts.
        /// </summary>
        public static PasswordHashResult Create(
            string password,
            int iterations = DefaultIterations)
        {
            ValidatePassword(password);

            if (iterations < 100_000)
                throw new ArgumentOutOfRangeException(
                    nameof(iterations),
                    "Password hashing iterations are too low.");

            if (iterations > MaximumAcceptedIterations)
                throw new ArgumentOutOfRangeException(
                    nameof(iterations),
                    "Password hashing iterations exceed the allowed maximum.");

            var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);

            var derivedKey = Rfc2898DeriveBytes.Pbkdf2(
                password: password,
                salt: salt,
                iterations: iterations,
                hashAlgorithm: HashAlgorithmName.SHA512,
                outputLength: HashSizeBytes);

            var hashBase64 = Convert.ToBase64String(derivedKey);
            var saltBase64 = Convert.ToBase64String(salt);

            // Store algorithm/version/iterations with the hash.
            //
            // Example:
            //
            // v1$PBKDF2-SHA512$600000$<hash>
            //
            var versionedHash =
                string.Join(
                    "$",
                    CurrentVersion,
                    CurrentAlgorithm,
                    iterations.ToString(CultureInfo.InvariantCulture),
                    hashBase64);

            return new PasswordHashResult(
                Hash: versionedHash,
                Salt: saltBase64,
                Algorithm: CurrentAlgorithm,
                Version: CurrentVersion,
                Iterations: iterations);
        }

        /// <summary>
        /// Verifies a password against the stored hash and salt.
        ///
        /// This method never throws for an invalid credential value.
        /// It returns false instead.
        /// </summary>
        public static bool Verify(
            string password,
            string storedHash,
            string storedSalt)
        {
            if (string.IsNullOrEmpty(password))
                return false;

            if (string.IsNullOrWhiteSpace(storedHash))
                return false;

            if (string.IsNullOrWhiteSpace(storedSalt))
                return false;

            try
            {
                // Current enterprise versioned format.
                if (TryParseVersionedHash(
                        storedHash,
                        out var algorithm,
                        out var iterations,
                        out var expectedHash))
                {
                    if (!string.Equals(
                            algorithm,
                            CurrentAlgorithm,
                            StringComparison.Ordinal))
                    {
                        return false;
                    }

                    if (iterations < 100_000 ||
                        iterations > MaximumAcceptedIterations)
                    {
                        return false;
                    }

                    var salt = Convert.FromBase64String(storedSalt);

                    if (salt.Length < 16)
                        return false;

                    var actualHash = Rfc2898DeriveBytes.Pbkdf2(
                        password: password,
                        salt: salt,
                        iterations: iterations,
                        hashAlgorithm: HashAlgorithmName.SHA512,
                        outputLength: expectedHash.Length);

                    return CryptographicOperations.FixedTimeEquals(
                        actualHash,
                        expectedHash);
                }

                // ----------------------------------------------------------------
                // Legacy compatibility
                // ----------------------------------------------------------------
                //
                // IMPORTANT:
                // We intentionally keep this conservative.
                //
                // The old project did not provide PwdHash.cs, therefore we do
                // not assume the exact legacy algorithm.
                //
                // A legacy database must be migrated only after its actual
                // hash format is identified.
                //
                // We do NOT guess an algorithm here.
                // ----------------------------------------------------------------

                return VerifyKnownLegacyFormat(
                    password,
                    storedHash,
                    storedSalt);
            }
            catch (FormatException exL298)
            {
                AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, exL298, "Auth.Verify");
                return false;
            }
            catch (CryptographicException exL302)
            {
                AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, exL302, "Auth.Verify");
                return false;
            }
            catch (ArgumentException exL306)
            {
                AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, exL306, "Auth.Verify");
                return false;
            }
        }

        /// <summary>
        /// Determines whether the stored password should be upgraded.
        ///
        /// Currently:
        ///     true  = legacy / obsolete / lower work factor
        ///     false = current secure format
        /// </summary>
        public static bool NeedsRehash(
            string storedHash,
            int desiredIterations = DefaultIterations)
        {
            if (string.IsNullOrWhiteSpace(storedHash))
                return true;

            if (!TryParseVersionedHash(
                    storedHash,
                    out var algorithm,
                    out var iterations,
                    out _))
            {
                // Unknown/legacy format.
                return true;
            }

            return !string.Equals(
                       algorithm,
                       CurrentAlgorithm,
                       StringComparison.Ordinal)
                   || iterations < desiredIterations;
        }

        /// <summary>
        /// Generates a new password hash after a successful login when the
        /// existing hash needs upgrading.
        /// </summary>
        public static PasswordHashResult Rehash(
            string password,
            int iterations = DefaultIterations)
        {
            return Create(password, iterations);
        }

        // --------------------------------------------------------------------
        // Validation
        // --------------------------------------------------------------------

        private static void ValidatePassword(string password)
		{
			ArgumentNullException.ThrowIfNull(password);

			if (password.Length == 0)
				throw new ArgumentException(
					"Password cannot be empty.",
					nameof(password));

			// Do not impose an unnecessarily small maximum.
			//
			// 1024 characters is more than enough for normal ERP accounts
			// while preventing pathological input sizes.
			if (password.Length > 1024)
				throw new ArgumentException(
					"Password exceeds the maximum supported length.",
					nameof(password));
		}
        // --------------------------------------------------------------------
        // Versioned hash parsing
        // --------------------------------------------------------------------

        private static bool TryParseVersionedHash(
            string storedHash,
            out string algorithm,
            out int iterations,
            out byte[] expectedHash)
        {
            algorithm = string.Empty;
            iterations = 0;
            expectedHash = Array.Empty<byte>();

            var parts = storedHash.Split('$');

            if (parts.Length != 4)
                return false;

            if (!string.Equals(
                    parts[0],
                    CurrentVersion,
                    StringComparison.Ordinal))
            {
                return false;
            }

            algorithm = parts[1];

            if (!int.TryParse(
                    parts[2],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out iterations))
            {
                return false;
            }

            if (iterations < 100_000 ||
                iterations > MaximumAcceptedIterations)
            {
                return false;
            }

            try
            {
                expectedHash = Convert.FromBase64String(parts[3]);
            }
            catch (FormatException exL424)
            {
                AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, exL424, "Auth.TryParseVersionedHash");
                return false;
            }

            if (expectedHash.Length != HashSizeBytes)
                return false;

            return true;
        }

        // --------------------------------------------------------------------
        // Legacy handling
        // --------------------------------------------------------------------

        private static bool VerifyKnownLegacyFormat(
            string password,
            string storedHash,
            string storedSalt)
        {
            /*
             * SECURITY NOTE
             * -------------
             *
             * The existing AuthServ referenced PwdHash.Verify(), but the actual
             * implementation was not present in the supplied Services files.
             *
             * Therefore we deliberately DO NOT guess whether the old system
             * used:
             *
             *     SHA256
             *     SHA512
             *     PBKDF2
             *     BCrypt
             *     another custom format
             *
             * Guessing here could silently authenticate against an incorrect
             * representation.
             *
             * Once the existing database hash format is confirmed, this method
             * can be implemented as an explicit migration adapter.
             */

            _ = password;
            _ = storedHash;
            _ = storedSalt;

            return false;
        }
    }

    // =========================================================================
    // Result object
    // =========================================================================

    /// <summary>
    /// Complete password hash metadata.
    /// </summary>
    public sealed record PasswordHashResult(
        string Hash,
        string Salt,
        string Algorithm,
        string Version,
        int Iterations);

    // ========== PwdPolicy.cs (Phase 4) ==========
/// <summary>
/// Central password policy enforcement. Company administrator passwords
/// must contain at least three characters; users choose the characters and
/// may use letters, digits, symbols, or any combination. A maximum length
/// still protects storage and hashing operations. Validation never throws
/// for a weak password.
/// </summary>
public static class PwdPolicy
{
    public static IReadOnlyList<string> Validate(string? password, PasswordPolicySettings policy, string? username = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var errors = new List<string>();

        if (string.IsNullOrEmpty(password))
        {
            errors.Add("Password is required.");
            return errors;
        }

        // Three characters is the only composition-independent minimum.
        // Do not require character classes or reject a user's chosen format.
        const int minLength = 3;
        var maxLength = Math.Max(minLength, policy.MaxLength);
        if (password.Length < minLength)
            errors.Add($"Password must be at least {minLength} characters long.");
        if (password.Length > maxLength)
            errors.Add($"Password must not exceed {maxLength} characters.");

        return errors;
    }

    /// <summary>Validates against the configured policy (AppCfgSvc Security section).</summary>
    public static IReadOnlyList<string> ValidateConfigured(string? password, string? username = null)
        => Validate(password, AppCfgSvc.Current?.Security?.PasswordPolicy ?? new PasswordPolicySettings(), username);
}

    // ========== LoginLockout.cs (Phase 4) ==========
/// <summary>
/// Pure lockout state machine (Phase 4). AuthServ persists
/// <see cref="State"/> on the users row (failed_login_count / locked_until)
/// and on the local company-admin record; this type owns the transitions so
/// they are unit-testable without a database:
///   failure below threshold  → count + 1, no lock
///   failure at threshold     → locked until now + lockout minutes
///   success                  → clean state
///   locked and not expired   → login is refused before password verification
/// </summary>
public static class LoginLockout
{
    public readonly record struct State(int FailedCount, DateTime? LockedUntilUtc);

    public static readonly State Clean = new(0, null);

    public static bool IsLocked(State state, DateTime nowUtc)
        => state.LockedUntilUtc is { } until && until > nowUtc;

    public static State RegisterFailure(State state, DateTime nowUtc, int maxAttempts, int lockoutMinutes)
    {
        maxAttempts = Math.Max(1, maxAttempts);
        lockoutMinutes = Math.Max(1, lockoutMinutes);
        var count = state.FailedCount + 1;
        return count >= maxAttempts
            ? new State(count, nowUtc.AddMinutes(lockoutMinutes))
            : new State(count, null);
    }

    public static State RegisterSuccess() => Clean;

    /// <summary>Configured limits (SecuritySettings; defaults 5 attempts / 15 minutes).</summary>
    public static (int MaxAttempts, int LockoutMinutes) GetConfiguredLimits()
    {
        var security = AppCfgSvc.Current?.Security;
        return (Math.Max(1, security?.MaxLoginAttempts ?? 5),
                Math.Max(1, security?.LockoutDurationMinutes ?? 15));
    }
}

    // ========== SecurityAuditLog.cs (Phase 4) ==========
/// <summary>
/// Durable security audit writer (Phase 4): inserts into the
/// security_audit_log table (migration 029) in the active company database.
/// Best-effort by design — an audit failure must never change an
/// authentication outcome — but failures are logged. Passwords are never
/// written. Also used by DatWin for data-export governance events.
/// </summary>
public static class SecurityAuditLog
{
    public const string LoginSuccess = "LOGIN_SUCCESS";
    public const string LoginFailure = "LOGIN_FAILURE";
    public const string AccountLocked = "ACCOUNT_LOCKED";
    public const string LoginLocked = "LOGIN_LOCKED";
    public const string DataExport = "DATA_EXPORT";
    public const string DataExportDenied = "DATA_EXPORT_DENIED";
    public const string DataImport = "DATA_IMPORT";
    public const string DataImportDenied = "DATA_IMPORT_DENIED";
    public const string TaxSettingsEnabled = "TAX_SETTINGS_ENABLED";
    public const string TaxSettingsDisabled = "TAX_SETTINGS_DISABLED";
    public const string TaxRuleSaved = "TAX_RULE_SAVED";

    /// <summary>
    /// Synchronous best-effort audit entry for synchronous business services.
    /// Call only after the business transaction commits; audit failure must not
    /// roll back a completed business operation.
    /// </summary>
    public static void Write(string? companyCode, object? userId, string username, string eventType, string? details = null)
    {
        try
        {
            var connectionString = DatabaseRuntime.GetConnectionString();
            if (string.IsNullOrWhiteSpace(connectionString))
                return;

            var isPostgreSql = DatabaseRuntime.IsPostgreSql();
            using DbConnection connection = isPostgreSql
                ? new NpgsqlConnection(connectionString)
                : new SqliteConnection(connectionString);
            connection.Open();

            using (var exists = connection.CreateCommand())
            {
                exists.CommandText = isPostgreSql
                    ? "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'security_audit_log'"
                    : "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'security_audit_log'";
                if (Convert.ToInt64(exists.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
                    return;
            }

            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO security_audit_log (company_id, user_id, username, event_type, details)
                VALUES ((SELECT company_id FROM companies WHERE company_code = @code), @uid, @user, @event, @details)
                """;
            Add(command, "@code", (object?)companyCode ?? DBNull.Value);
            Add(command, "@uid", ConvertUserId(userId, isPostgreSql));
            Add(command, "@user", string.IsNullOrWhiteSpace(username) ? "(unknown)" : username.Trim());
            Add(command, "@event", eventType);
            Add(command, "@details", (object?)details ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            try { AppHost.Logging.LogWarning("SecurityAudit", $"Security audit write failed ({eventType}): {ex.Message}"); }
            catch { /* audit logging must never break a committed business operation */ }
        }
    }

    public static async Task WriteAsync(string? companyCode, object? userId, string username, string eventType, string? details = null)
    {
        try
        {
            var connectionString = DatabaseRuntime.GetConnectionString();
            if (string.IsNullOrWhiteSpace(connectionString))
                return;

            var isPostgreSql = DatabaseRuntime.IsPostgreSql();
            await using var connection = isPostgreSql
                ? (DbConnection)new NpgsqlConnection(connectionString)
                : new SqliteConnection(connectionString);
            await connection.OpenAsync();

            if (!await TableExistsAsync(connection, isPostgreSql))
                return;

            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO security_audit_log (company_id, user_id, username, event_type, details)
                VALUES ((SELECT company_id FROM companies WHERE company_code = @code), @uid, @user, @event, @details)
                """;
            Add(command, "@code", (object?)companyCode ?? DBNull.Value);
            Add(command, "@uid", ConvertUserId(userId, isPostgreSql));
            Add(command, "@user", string.IsNullOrWhiteSpace(username) ? "(unknown)" : username.Trim());
            Add(command, "@event", eventType);
            Add(command, "@details", (object?)details ?? DBNull.Value);
            await command.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            try { AppHost.Logging.LogWarning("SecurityAudit", $"Security audit write failed ({eventType}): {ex.Message}"); }
            catch { /* logging must never throw into an auth path */ }
        }
    }

    private static object ConvertUserId(object? userId, bool isPostgreSql)
    {
        if (userId is null)
            return DBNull.Value;
        if (!isPostgreSql)
            return userId.ToString() ?? (object)DBNull.Value;
        return Guid.TryParse(userId.ToString(), out var guid) ? guid : DBNull.Value;
    }

    private static async Task<bool> TableExistsAsync(DbConnection connection, bool isPostgreSql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = isPostgreSql
            ? "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'security_audit_log'"
            : "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'security_audit_log'";
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result, CultureInfo.InvariantCulture) > 0;
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

    // ========== AuthServ.cs ==========
public sealed class AuthServ : IAuthSvc
{
    private sealed record DbUserCredential(
        string UserId,
        string Username,
        string PasswordHash,
        string PasswordSalt,
        string FullName,
        string Email,
        string CompanyCode,
        string CompanyName,
        string RoleCode,
        string RoleName,
        int FailedLoginCount,
        DateTime? LockedUntilUtc);

    /// <summary>Outcome of a database credential check (Phase 4 lockout/audit).</summary>
    private enum DbLoginKind
    {
        Success,
        BadCredentials,
        NotFound,
        /// <summary>Account was already locked; the password was not evaluated.</summary>
        Locked,
        /// <summary>This failure reached the threshold and locked the account.</summary>
        LockedNow
    }

    private sealed record DbLoginAttempt(UserInfo? User, DbLoginKind Kind, string? UserId)
    {
        public static readonly DbLoginAttempt NotFound = new(null, DbLoginKind.NotFound, null);
        public static DbLoginAttempt BadCredentials(string? userId) => new(null, DbLoginKind.BadCredentials, userId);
    }

    private UserInfo? _currentUser;

    public bool IsAuthenticated => _currentUser?.IsAuthenticated == true;

    public async Task<AuthResult> LoginAsync(string username, string password)
    {
        var normalizedUser = username?.Trim() ?? string.Empty;
        var hasUsername = !string.IsNullOrWhiteSpace(normalizedUser);

        if (string.IsNullOrWhiteSpace(password))
            return Failed();

        var companyCode = AppState.CurrentCompanyCode ?? string.Empty;
        if (string.IsNullOrWhiteSpace(companyCode))
            return Failed();

        if (hasUsername)
        {
            var localCompany = await CoLocalStore.FindUserCompanyAsync(companyCode, normalizedUser, password);
            if (localCompany != null)
            {
                await SecurityAuditLog.WriteAsync(companyCode, null, normalizedUser,
                    SecurityAuditLog.LoginSuccess, "Local company administrator sign-in.");
                return await SucceedAsync(BuildCompanyAdminUser(normalizedUser, localCompany));
            }

            var attempt = await TryLoginFromDatabaseAsync(companyCode, normalizedUser, password);
            switch (attempt.Kind)
            {
                case DbLoginKind.Success:
                    await SecurityAuditLog.WriteAsync(companyCode, attempt.UserId, normalizedUser,
                        SecurityAuditLog.LoginSuccess, "Database sign-in.");
                    return await SucceedAsync(attempt.User!);
                case DbLoginKind.Locked:
                    await SecurityAuditLog.WriteAsync(companyCode, attempt.UserId, normalizedUser,
                        SecurityAuditLog.LoginLocked, "Sign-in refused: account is locked.");
                    AuditSecurityViolation("LoginLocked", $"Locked account sign-in attempt | user={normalizedUser} | company={companyCode}", normalizedUser);
                    return Failed(ErrorCodes.AccountLocked);
                case DbLoginKind.LockedNow:
                    await SecurityAuditLog.WriteAsync(companyCode, attempt.UserId, normalizedUser,
                        SecurityAuditLog.AccountLocked, "Account locked after repeated failed sign-ins.");
                    AuditSecurityViolation("AccountLocked", $"Account locked after repeated failed sign-ins | user={normalizedUser} | company={companyCode}", normalizedUser);
                    return Failed(ErrorCodes.AccountLockedNow);
                default:
                    await SecurityAuditLog.WriteAsync(companyCode, attempt.UserId, normalizedUser,
                        SecurityAuditLog.LoginFailure,
                        attempt.Kind == DbLoginKind.BadCredentials ? "Invalid password." : "User not found or inactive.");
                    return Failed();
            }
        }
        else
        {
            var localCompany = await CoLocalStore.FindCompanyAdminByPasswordAsync(companyCode, password);
            if (localCompany != null && !string.IsNullOrWhiteSpace(localCompany.AdminUsername))
            {
                await SecurityAuditLog.WriteAsync(companyCode, null, localCompany.AdminUsername.Trim(),
                    SecurityAuditLog.LoginSuccess, "Local company administrator passcode sign-in.");
                return await SucceedAsync(BuildCompanyAdminUser(localCompany.AdminUsername.Trim(), localCompany));
            }

            var attempt = await TryLoginFromDatabaseByPasscodeAsync(companyCode, password);
            switch (attempt.Kind)
            {
                case DbLoginKind.Success:
                    await SecurityAuditLog.WriteAsync(companyCode, attempt.UserId, attempt.User!.Username,
                        SecurityAuditLog.LoginSuccess, "Database passcode sign-in.");
                    return await SucceedAsync(attempt.User!);
                case DbLoginKind.Locked:
                    await SecurityAuditLog.WriteAsync(companyCode, attempt.UserId, "(passcode)",
                        SecurityAuditLog.LoginLocked, "Passcode sign-in refused: matched account is locked.");
                    return Failed(ErrorCodes.AccountLocked);
                default:
                    // Passcode failures cannot be attributed to one account, so no
                    // per-account counter is incremented on this path; the event
                    // is still audited. A matched-but-locked account is refused
                    // above, so this path cannot be used to bypass a lockout.
                    await SecurityAuditLog.WriteAsync(companyCode, null, "(passcode)",
                        SecurityAuditLog.LoginFailure, "Passcode did not match exactly one active account.");
                    return Failed();
            }
        }
    }

    public Task LogoutAsync()
    {
        _currentUser = null;
        UsrSession.Clear();
        AppState.CurrentUser = null;
        // Permissions are per company database: leaving a session must
        // drop the loaded set, or the next company session would inherit
        // the previous company's grants (company switch routes through
        // this method via ComWin).
        AppHost.Permissions.Clear();
        return Task.CompletedTask;
    }

    public Task<UserInfo?> GetCurrentUserAsync() => Task.FromResult(_currentUser);

    public void SetCurrentUser(UserInfo user)
    {
        _currentUser = user ?? throw new ArgumentNullException(nameof(user));
        SyncSession(user);
        _ = LoadPermissionsForSessionAsync(user);
    }

    private static void AuditSecurityViolation(string eventType, string details, string? username)
    {
        try
        {
            if (AppHost.Audit is { } audit)
                _ = audit.LogSecurityViolationAsync(eventType, details, username);
        }
        catch (Exception exL813)
        {
            AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, exL813, "Auth.AuditSecurityViolation");
        }
    }

    private static async Task<AuthResult> SucceedAsync(UserInfo user)
    {
        SyncSession(user);
        await LoadPermissionsForSessionAsync(user).ConfigureAwait(false);
        return new AuthResult { Success = true, Message = "Login succeeded", User = user };
    }

    /// <summary>
    /// Session-lifecycle wiring for RBAC: after a successful sign-in the
    /// user's effective permissions are loaded into
    /// <see cref="AppHost.Permissions"/> so every window/action guard
    /// consults the real set for the whole session. (Before this wiring
    /// existed, PermServ.LoadForUserAsync had no callers and every
    /// guarded window deny-closed for every user.)
    ///
    /// Failure policy: a load failure is reported with AUTH-2006 and
    /// never fails the sign-in itself. A non-administrator continues
    /// with an empty set (fail closed — guarded windows deny). An
    /// administrator falls back to the full catalog
    /// (<see cref="IPermServ.GrantAllCatalogPermissions"/>) so a loader
    /// or seed failure can never lock the company administrator out of
    /// the permission administration needed to repair it.
    /// Internal (not private) so Kern.Tests can pin this policy.
    /// </summary>
    internal static async Task LoadPermissionsForSessionAsync(UserInfo user)
    {
        try
        {
            await AppHost.Permissions.LoadForUserAsync(user.Username).ConfigureAwait(false);
            user.Permissions = AppHost.Permissions.GetCurrentPermissions().ToArray();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.PermissionLoadFailed, ex,
                $"Auth.LoadPermissionsForSessionAsync user={user.Username}");
            if (user.IsAdministrator)
            {
                AppHost.Permissions.GrantAllCatalogPermissions(user.Username);
                user.Permissions = AppHost.Permissions.GetCurrentPermissions().ToArray();
                AppHost.Logging?.LogWarning("AuthServ",
                    $"Permission load failed for administrator '{user.Username}'; full catalog granted as fallback.");
            }
            else
            {
                AppHost.Permissions.Clear();
                user.Permissions = Array.Empty<string>();
            }
        }
    }

    private static AuthResult Failed(string errorCode = ErrorCodes.InvalidCredentials) => new()
    {
        Success = false,
        Message = "The username or password is incorrect",
        ErrorCode = errorCode
    };

    private static UserInfo BuildCompanyAdminUser(string username, StoredCoRec company) => new()
    {
        Id = company.CompanyId.ToString(),
        Username = username,
        FullName = company.AdminFullName,
        Email = company.Email,
        Role = "Administrator",
        RoleCode = "ROLE_ADMINISTRATOR",
        AccessLevel = UsrAccLvl.Administrator,
        CompanyId = company.Code,
        CompanyName = company.Name,
        IsAuthenticated = true,
        LastLogin = DateTime.Now
    };

    private static DateTime? ParseLockedUntil(object? value) => value switch
    {
        null => null,
        DBNull => null,
        DateTime dt => dt.ToUniversalTime(),
        string text when DateTime.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) => parsed,
        _ => null
    };

    // ------------------------------------------------------------------
    // Username + password paths
    // ------------------------------------------------------------------

    private static async Task<DbLoginAttempt> TryLoginFromDatabaseAsync(string companyCode, string username, string password)
    {
        var provider = DatabaseRuntime.GetProvider();
        if (DatabaseRuntime.IsSqlite(provider))
            return await TryLoginFromSqliteAsync(companyCode, username, password);

        try
        {
            var connectionString = DatabaseRuntime.GetConnectionString();
            if (string.IsNullOrWhiteSpace(connectionString)) return DbLoginAttempt.NotFound;

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();

            const string sql = """
                SELECT
                    u.user_id, u.username, u.password_hash, u.password_salt,
                    u.full_name_ar, u.email, c.company_code, c.company_name_ar,
                    u.failed_login_count, u.locked_until,
                    COALESCE(r.role_code, 'ROLE_USER') AS role_code,
                    COALESCE(r.role_name, 'User') AS role_name
                FROM public.users u
                INNER JOIN public.companies c ON c.company_id = u.company_id
                LEFT JOIN LATERAL (
                    SELECT r.role_code, r.role_name
                    FROM public.user_roles ur
                    INNER JOIN public.roles r ON r.role_id = ur.role_id
                    WHERE ur.user_id = u.user_id AND r.is_active = TRUE AND r.is_deleted = FALSE
                    ORDER BY CASE r.role_code
                        WHEN 'ROLE_ADMINISTRATOR' THEN 0
                        WHEN 'ROLE_USER' THEN 1
                        WHEN 'ROLE_VIEWER' THEN 2
                        ELSE 9 END
                    LIMIT 1
                ) r ON TRUE
                WHERE c.company_code = @CompanyCode
                  AND u.username = @Username
                  AND u.is_active = TRUE AND u.is_deleted = FALSE
                  AND c.is_active = TRUE AND c.is_deleted = FALSE
                LIMIT 1;
                """;

            string userId, hash, salt, fullName, email, companyName, roleCode, roleName;
            int failedCount;
            DateTime? lockedUntil;
            await using (var command = new NpgsqlCommand(sql, connection))
            {
                command.Parameters.AddWithValue("@CompanyCode", companyCode);
                command.Parameters.AddWithValue("@Username", username);
                await using var reader = await command.ExecuteReaderAsync();
                if (!await reader.ReadAsync()) return DbLoginAttempt.NotFound;

                userId = reader["user_id"]?.ToString() ?? string.Empty;
                hash = reader["password_hash"]?.ToString() ?? string.Empty;
                salt = reader["password_salt"]?.ToString() ?? string.Empty;
                fullName = reader["full_name_ar"]?.ToString() ?? string.Empty;
                email = reader["email"]?.ToString() ?? string.Empty;
                companyName = reader["company_name_ar"]?.ToString() ?? string.Empty;
                roleCode = reader["role_code"]?.ToString() ?? "ROLE_USER";
                roleName = reader["role_name"]?.ToString() ?? "User";
                failedCount = Convert.ToInt32(reader["failed_login_count"], CultureInfo.InvariantCulture);
                lockedUntil = ParseLockedUntil(reader["locked_until"]);
            }

            if (LoginLockout.IsLocked(new LoginLockout.State(failedCount, lockedUntil), DateTime.UtcNow))
                return new DbLoginAttempt(null, DbLoginKind.Locked, userId);

            if (!PwdHash.Verify(password, hash, salt))
            {
                var kind = await RegisterPostgresFailureAsync(connection, userId, failedCount);
                return new DbLoginAttempt(null, kind, userId);
            }

            await RegisterPostgresSuccessAsync(connection, userId, failedCount, lockedUntil, password, hash);
            return new DbLoginAttempt(
                BuildDatabaseUser(userId, username, fullName, email, companyCode, companyName, roleCode, roleName),
                DbLoginKind.Success, userId);
        }
        catch (Exception ex)
        {
            AppHost.Logging.LogWarning("AuthServ", $"Database authentication failed: {ex.Message}");
            return DbLoginAttempt.NotFound;
        }
    }

    private static async Task<DbLoginAttempt> TryLoginFromSqliteAsync(string companyCode, string username, string password)
    {
        var cs = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(cs)) return DbLoginAttempt.NotFound;

        await using var db = new SqliteConnection(cs);
        await db.OpenAsync();

        string userId, hash, salt, fullName, email, companyName, roleCode, roleName;
        int failedCount;
        DateTime? lockedUntil;
        await using (var cmd = new SqliteCommand("""
            SELECT u.user_id, u.username, u.password_hash, u.password_salt, u.full_name_ar, u.email,
                   c.company_code, c.company_name_ar, u.failed_login_count, u.locked_until,
                   COALESCE((SELECT r.role_code FROM user_roles ur
                             INNER JOIN roles r ON r.role_id=ur.role_id
                             WHERE ur.user_id=u.user_id AND r.is_active=1 AND r.is_deleted=0
                             ORDER BY CASE r.role_code
                                 WHEN 'ROLE_ADMINISTRATOR' THEN 0
                                 WHEN 'ROLE_USER' THEN 1
                                 WHEN 'ROLE_VIEWER' THEN 2
                                 ELSE 9 END LIMIT 1), 'ROLE_USER') AS role_code,
                   COALESCE((SELECT r.role_name FROM user_roles ur
                             INNER JOIN roles r ON r.role_id=ur.role_id
                             WHERE ur.user_id=u.user_id AND r.is_active=1 AND r.is_deleted=0
                             ORDER BY CASE r.role_code
                                 WHEN 'ROLE_ADMINISTRATOR' THEN 0
                                 WHEN 'ROLE_USER' THEN 1
                                 WHEN 'ROLE_VIEWER' THEN 2
                                 ELSE 9 END LIMIT 1), 'User') AS role_name
            FROM users u INNER JOIN companies c ON c.company_id=u.company_id
            WHERE c.company_code=@company AND u.username=@username
              AND u.is_active=1 AND u.is_deleted=0
              AND c.is_active=1 AND c.is_deleted=0 LIMIT 1
            """, db))
        {
            cmd.Parameters.AddWithValue("@company", companyCode);
            cmd.Parameters.AddWithValue("@username", username);
            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return DbLoginAttempt.NotFound;

            userId = reader["user_id"]?.ToString() ?? string.Empty;
            hash = reader["password_hash"]?.ToString() ?? string.Empty;
            salt = reader["password_salt"]?.ToString() ?? string.Empty;
            fullName = reader["full_name_ar"]?.ToString() ?? string.Empty;
            email = reader["email"]?.ToString() ?? string.Empty;
            companyName = reader["company_name_ar"]?.ToString() ?? string.Empty;
            roleCode = reader["role_code"]?.ToString() ?? "ROLE_USER";
            roleName = reader["role_name"]?.ToString() ?? "User";
            failedCount = Convert.ToInt32(reader["failed_login_count"], CultureInfo.InvariantCulture);
            lockedUntil = ParseLockedUntil(reader["locked_until"]);
        }

        if (LoginLockout.IsLocked(new LoginLockout.State(failedCount, lockedUntil), DateTime.UtcNow))
            return new DbLoginAttempt(null, DbLoginKind.Locked, userId);

        if (!PwdHash.Verify(password, hash, salt))
        {
            var kind = await RegisterSqliteFailureAsync(db, userId, failedCount);
            return new DbLoginAttempt(null, kind, userId);
        }

        await RegisterSqliteSuccessAsync(db, userId, failedCount, lockedUntil, password, hash);
        return new DbLoginAttempt(
            BuildDatabaseUser(userId, username, fullName, email, companyCode, companyName, roleCode, roleName),
            DbLoginKind.Success, userId);
    }

    // ------------------------------------------------------------------
    // Passcode-only paths (password identifies the account on its own)
    // ------------------------------------------------------------------

    private static async Task<DbLoginAttempt> TryLoginFromDatabaseByPasscodeAsync(string companyCode, string password)
    {
        var provider = DatabaseRuntime.GetProvider();
        if (DatabaseRuntime.IsSqlite(provider))
            return await TryLoginFromSqliteByPasscodeAsync(companyCode, password);

        try
        {
            var connectionString = DatabaseRuntime.GetConnectionString();
            if (string.IsNullOrWhiteSpace(connectionString)) return DbLoginAttempt.NotFound;

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();

            const string sql = """
                SELECT
                    u.user_id, u.username, u.password_hash, u.password_salt,
                    u.full_name_ar, u.email, c.company_code, c.company_name_ar,
                    u.failed_login_count, u.locked_until,
                    COALESCE(r.role_code, 'ROLE_USER') AS role_code,
                    COALESCE(r.role_name, 'User') AS role_name
                FROM public.users u
                INNER JOIN public.companies c ON c.company_id = u.company_id
                LEFT JOIN LATERAL (
                    SELECT r.role_code, r.role_name
                    FROM public.user_roles ur
                    INNER JOIN public.roles r ON r.role_id = ur.role_id
                    WHERE ur.user_id = u.user_id AND r.is_active = TRUE AND r.is_deleted = FALSE
                    ORDER BY CASE r.role_code
                        WHEN 'ROLE_ADMINISTRATOR' THEN 0
                        WHEN 'ROLE_USER' THEN 1
                        WHEN 'ROLE_VIEWER' THEN 2
                        ELSE 9 END
                    LIMIT 1
                ) r ON TRUE
                WHERE c.company_code = @CompanyCode
                  AND u.is_active = TRUE AND u.is_deleted = FALSE
                  AND c.is_active = TRUE AND c.is_deleted = FALSE;
                """;

            var matches = new List<DbUserCredential>();
            await using (var command = new NpgsqlCommand(sql, connection))
            {
                command.Parameters.AddWithValue("@CompanyCode", companyCode);
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var hash = reader["password_hash"]?.ToString() ?? string.Empty;
                    var salt = reader["password_salt"]?.ToString() ?? string.Empty;
                    if (!PwdHash.Verify(password, hash, salt)) continue;

                    matches.Add(new DbUserCredential(
                        reader["user_id"]?.ToString() ?? string.Empty,
                        reader["username"]?.ToString() ?? string.Empty,
                        hash, salt,
                        reader["full_name_ar"]?.ToString() ?? string.Empty,
                        reader["email"]?.ToString() ?? string.Empty,
                        reader["company_code"]?.ToString() ?? companyCode,
                        reader["company_name_ar"]?.ToString() ?? GetCompanyName(),
                        reader["role_code"]?.ToString() ?? "ROLE_USER",
                        reader["role_name"]?.ToString() ?? "User",
                        Convert.ToInt32(reader["failed_login_count"], CultureInfo.InvariantCulture),
                        ParseLockedUntil(reader["locked_until"])));
                }
            }

            if (matches.Count != 1) return DbLoginAttempt.BadCredentials(null);
            var matched = matches[0];

            if (LoginLockout.IsLocked(new LoginLockout.State(matched.FailedLoginCount, matched.LockedUntilUtc), DateTime.UtcNow))
                return new DbLoginAttempt(null, DbLoginKind.Locked, matched.UserId);

            await RegisterPostgresSuccessAsync(connection, matched.UserId, matched.FailedLoginCount, matched.LockedUntilUtc, password, matched.PasswordHash);
            return new DbLoginAttempt(
                BuildDatabaseUser(matched.UserId, matched.Username, matched.FullName, matched.Email,
                    matched.CompanyCode, matched.CompanyName, matched.RoleCode, matched.RoleName),
                DbLoginKind.Success, matched.UserId);
        }
        catch (Exception ex)
        {
            AppHost.Logging.LogWarning("AuthServ", $"Database authentication failed: {ex.Message}");
            return DbLoginAttempt.NotFound;
        }
    }

    private static async Task<DbLoginAttempt> TryLoginFromSqliteByPasscodeAsync(string companyCode, string password)
    {
        var cs = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(cs)) return DbLoginAttempt.NotFound;

        await using var db = new SqliteConnection(cs);
        await db.OpenAsync();

        var matches = new List<DbUserCredential>();
        await using (var cmd = new SqliteCommand("""
            SELECT u.user_id, u.username, u.password_hash, u.password_salt, u.full_name_ar, u.email,
                   c.company_code, c.company_name_ar, u.failed_login_count, u.locked_until,
                   COALESCE((SELECT r.role_code FROM user_roles ur INNER JOIN roles r ON r.role_id=ur.role_id
                             WHERE ur.user_id=u.user_id AND r.is_active=1 AND r.is_deleted=0
                             ORDER BY CASE r.role_code WHEN 'ROLE_ADMINISTRATOR' THEN 0 WHEN 'ROLE_USER' THEN 1 WHEN 'ROLE_VIEWER' THEN 2 ELSE 9 END LIMIT 1), 'ROLE_USER') AS role_code,
                   COALESCE((SELECT r.role_name FROM user_roles ur INNER JOIN roles r ON r.role_id=ur.role_id
                             WHERE ur.user_id=u.user_id AND r.is_active=1 AND r.is_deleted=0
                             ORDER BY CASE r.role_code WHEN 'ROLE_ADMINISTRATOR' THEN 0 WHEN 'ROLE_USER' THEN 1 WHEN 'ROLE_VIEWER' THEN 2 ELSE 9 END LIMIT 1), 'User') AS role_name
            FROM users u INNER JOIN companies c ON c.company_id=u.company_id
            WHERE c.company_code=@company AND u.is_active=1 AND u.is_deleted=0
              AND c.is_active=1 AND c.is_deleted=0
            """, db))
        {
            cmd.Parameters.AddWithValue("@company", companyCode);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var hash = reader["password_hash"]?.ToString() ?? string.Empty;
                var salt = reader["password_salt"]?.ToString() ?? string.Empty;
                if (!PwdHash.Verify(password, hash, salt))
                    continue;

                matches.Add(new DbUserCredential(
                    reader["user_id"]?.ToString() ?? string.Empty,
                    reader["username"]?.ToString() ?? string.Empty,
                    hash, salt,
                    reader["full_name_ar"]?.ToString() ?? string.Empty,
                    reader["email"]?.ToString() ?? string.Empty,
                    reader["company_code"]?.ToString() ?? companyCode,
                    reader["company_name_ar"]?.ToString() ?? GetCompanyName(),
                    reader["role_code"]?.ToString() ?? "ROLE_USER",
                    reader["role_name"]?.ToString() ?? "User",
                    Convert.ToInt32(reader["failed_login_count"], CultureInfo.InvariantCulture),
                    ParseLockedUntil(reader["locked_until"])));
            }
        }

        if (matches.Count != 1) return DbLoginAttempt.BadCredentials(null);
        var matched = matches[0];

        if (LoginLockout.IsLocked(new LoginLockout.State(matched.FailedLoginCount, matched.LockedUntilUtc), DateTime.UtcNow))
            return new DbLoginAttempt(null, DbLoginKind.Locked, matched.UserId);

        await RegisterSqliteSuccessAsync(db, matched.UserId, matched.FailedLoginCount, matched.LockedUntilUtc, password, matched.PasswordHash);
        return new DbLoginAttempt(
            BuildDatabaseUser(matched.UserId, matched.Username, matched.FullName, matched.Email,
                matched.CompanyCode, matched.CompanyName, matched.RoleCode, matched.RoleName),
            DbLoginKind.Success, matched.UserId);
    }

    // ------------------------------------------------------------------
    // Lockout bookkeeping + transparent hash upgrade (Phase 4)
    // ------------------------------------------------------------------

    private static async Task<DbLoginKind> RegisterSqliteFailureAsync(SqliteConnection db, string userId, int currentFailedCount)
    {
        var (maxAttempts, lockoutMinutes) = LoginLockout.GetConfiguredLimits();
        var state = LoginLockout.RegisterFailure(
            new LoginLockout.State(currentFailedCount, null), DateTime.UtcNow, maxAttempts, lockoutMinutes);

        await using var cmd = new SqliteCommand(
            "UPDATE users SET failed_login_count = @count, locked_until = @locked WHERE user_id = @id", db);
        cmd.Parameters.AddWithValue("@count", state.FailedCount);
        cmd.Parameters.AddWithValue("@locked",
            state.LockedUntilUtc is { } until ? until.ToString("o", CultureInfo.InvariantCulture) : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@id", userId);
        await cmd.ExecuteNonQueryAsync();
        return state.LockedUntilUtc is not null ? DbLoginKind.LockedNow : DbLoginKind.BadCredentials;
    }

    private static async Task RegisterSqliteSuccessAsync(
        SqliteConnection db, string userId, int failedCount, DateTime? lockedUntil, string password, string storedHash)
    {
        var needsReset = failedCount > 0 || lockedUntil is not null;
        var needsRehash = PwdHash.NeedsRehash(storedHash);
        if (!needsReset && !needsRehash)
            return;

        await using var transaction = await db.BeginTransactionAsync();
        if (needsReset)
        {
            await using var cmd = new SqliteCommand(
                "UPDATE users SET failed_login_count = 0, locked_until = NULL WHERE user_id = @id", db, (SqliteTransaction)transaction);
            cmd.Parameters.AddWithValue("@id", userId);
            await cmd.ExecuteNonQueryAsync();
        }
        if (needsRehash)
        {
            // Transparent upgrade: stored iteration metadata is honored by
            // Verify; a successful sign-in rehashes at the current work factor.
            var rehash = PwdHash.Rehash(password);
            await using var cmd = new SqliteCommand(
                "UPDATE users SET password_hash = @hash, password_salt = @salt WHERE user_id = @id", db, (SqliteTransaction)transaction);
            cmd.Parameters.AddWithValue("@hash", rehash.Hash);
            cmd.Parameters.AddWithValue("@salt", rehash.Salt);
            cmd.Parameters.AddWithValue("@id", userId);
            await cmd.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    private static async Task<DbLoginKind> RegisterPostgresFailureAsync(NpgsqlConnection connection, string userId, int currentFailedCount)
    {
        var (maxAttempts, lockoutMinutes) = LoginLockout.GetConfiguredLimits();
        var state = LoginLockout.RegisterFailure(
            new LoginLockout.State(currentFailedCount, null), DateTime.UtcNow, maxAttempts, lockoutMinutes);

        await using var cmd = new NpgsqlCommand(
            "UPDATE public.users SET failed_login_count = @count, locked_until = @locked WHERE user_id = @id", connection);
        cmd.Parameters.AddWithValue("@count", state.FailedCount);
        cmd.Parameters.AddWithValue("@locked", state.LockedUntilUtc is { } until ? until : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@id", Guid.Parse(userId));
        await cmd.ExecuteNonQueryAsync();
        return state.LockedUntilUtc is not null ? DbLoginKind.LockedNow : DbLoginKind.BadCredentials;
    }

    private static async Task RegisterPostgresSuccessAsync(
        NpgsqlConnection connection, string userId, int failedCount, DateTime? lockedUntil, string password, string storedHash)
    {
        var needsReset = failedCount > 0 || lockedUntil is not null;
        var needsRehash = PwdHash.NeedsRehash(storedHash);
        if (!needsReset && !needsRehash)
            return;

        await using var transaction = await connection.BeginTransactionAsync();
        if (needsReset)
        {
            await using var cmd = new NpgsqlCommand(
                "UPDATE public.users SET failed_login_count = 0, locked_until = NULL WHERE user_id = @id", connection, transaction);
            cmd.Parameters.AddWithValue("@id", Guid.Parse(userId));
            await cmd.ExecuteNonQueryAsync();
        }
        if (needsRehash)
        {
            var rehash = PwdHash.Rehash(password);
            await using var cmd = new NpgsqlCommand(
                "UPDATE public.users SET password_hash = @hash, password_salt = @salt WHERE user_id = @id", connection, transaction);
            cmd.Parameters.AddWithValue("@hash", rehash.Hash);
            cmd.Parameters.AddWithValue("@salt", rehash.Salt);
            cmd.Parameters.AddWithValue("@id", Guid.Parse(userId));
            await cmd.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    private static UserInfo BuildDatabaseUser(object id, object username, object fullName, object email,
        object companyCode, object companyName, object roleCode, object roleName)
    {
        var code = roleCode?.ToString() ?? "ROLE_USER";
        var level = code switch
        {
            "ROLE_ADMINISTRATOR" => UsrAccLvl.Administrator,
            "ROLE_VIEWER" => UsrAccLvl.Viewer,
            _ => UsrAccLvl.User
        };

        return new UserInfo
        {
            Id = id?.ToString() ?? string.Empty,
            Username = username?.ToString() ?? string.Empty,
            FullName = fullName?.ToString() ?? string.Empty,
            Email = email?.ToString() ?? string.Empty,
            Role = roleName?.ToString() ?? code,
            RoleCode = code,
            AccessLevel = level,
            CompanyId = companyCode?.ToString() ?? string.Empty,
            CompanyName = companyName?.ToString() ?? string.Empty,
            IsAuthenticated = true,
            LastLogin = DateTime.Now
        };
    }

    private static void SyncSession(UserInfo user)
    {
        UsrSession.Apply(user);
        AppState.CurrentUser = user;
    }

    private static string GetCompanyName()
    {
        if (!string.IsNullOrWhiteSpace(AppState.CurrentCompanyName))
            return AppState.CurrentCompanyName!;

        if (System.Windows.Application.Current?.Properties["SelectedCompany"] is ShouTech.App.Views.CompanyItem company
            && !string.IsNullOrWhiteSpace(company.Name))
            return company.Name;

        return "Main Company";
    }
}

}
