// ========================================================================
// FILE: Services/Auth.cs
// PROJECT: SHOUTECH ERP — Authentication, session, password hashing
// Merged: UsrAccLvl, IAuthSvc, UsrSession, SessionManager, PwdHash, AuthServ
// ========================================================================

#nullable enable

using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.App.Common;

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
            AppHost.Logging.LogInfo("Session", $"✅ بدء جلسة المستخدم: {username}");
        }

        public void Logout()
        {
            if (CurrentUser != null)
            {
                AppHost.Logging.LogInfo("Session", $"🚪 إنهاء جلسة المستخدم: {CurrentUser}");
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
            catch (FormatException)
            {
                return false;
            }
            catch (CryptographicException)
            {
                return false;
            }
            catch (ArgumentException)
            {
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
            catch (FormatException)
            {
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
        string RoleName);

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
                return Succeed(BuildCompanyAdminUser(normalizedUser, localCompany));

            var dbUser = await TryLoginFromDatabaseAsync(companyCode, normalizedUser, password);
            if (dbUser != null)
                return Succeed(dbUser);
        }
        else
        {
            var localCompany = await CoLocalStore.FindCompanyAdminByPasswordAsync(companyCode, password);
            if (localCompany != null && !string.IsNullOrWhiteSpace(localCompany.AdminUsername))
                return Succeed(BuildCompanyAdminUser(localCompany.AdminUsername.Trim(), localCompany));

            var dbUser = await TryLoginFromDatabaseByPasscodeAsync(companyCode, password);
            if (dbUser != null)
                return Succeed(dbUser);
        }

        return Failed();
    }

    public Task LogoutAsync()
    {
        _currentUser = null;
        UsrSession.Clear();
        AppState.CurrentUser = null;
        return Task.CompletedTask;
    }

    public Task<UserInfo?> GetCurrentUserAsync() => Task.FromResult(_currentUser);

    public void SetCurrentUser(UserInfo user)
    {
        _currentUser = user ?? throw new ArgumentNullException(nameof(user));
        SyncSession(user);
    }

    private static AuthResult Succeed(UserInfo user)
    {
        SyncSession(user);
        return new AuthResult { Success = true, Message = "تم تسجيل الدخول بنجاح", User = user };
    }

    private static AuthResult Failed() => new()
    {
        Success = false,
        Message = "اسم المستخدم أو كلمة المرور غير صحيحة"
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

    private static async Task<UserInfo?> TryLoginFromDatabaseAsync(string companyCode, string username, string password)
    {
        var provider = DatabaseRuntime.GetProvider();
        if (DatabaseRuntime.IsSqlite(provider))
            return await TryLoginFromSqliteAsync(companyCode, username, password);

        try
        {
            var connectionString = DatabaseRuntime.GetConnectionString();
            if (string.IsNullOrWhiteSpace(connectionString)) return null;

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();

            const string sql = """
                SELECT
                    u.user_id, u.username, u.password_hash, u.password_salt,
                    u.full_name_ar, u.email, c.company_code, c.company_name_ar,
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

            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("@CompanyCode", companyCode);
            command.Parameters.AddWithValue("@Username", username);
            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return null;

            var hash = reader["password_hash"]?.ToString() ?? string.Empty;
            var salt = reader["password_salt"]?.ToString() ?? string.Empty;
            if (!PwdHash.Verify(password, hash, salt)) return null;

            return BuildDatabaseUser(
                reader["user_id"], reader["username"], reader["full_name_ar"], reader["email"],
                reader["company_code"], reader["company_name_ar"], reader["role_code"], reader["role_name"]);
        }
        catch (Exception ex)
        {
            AppHost.Logging.LogWarning("AuthServ", $"Database authentication failed: {ex.Message}");
            return null;
        }
    }

    private static async Task<UserInfo?> TryLoginFromDatabaseByPasscodeAsync(string companyCode, string password)
    {
        var provider = DatabaseRuntime.GetProvider();
        if (DatabaseRuntime.IsSqlite(provider))
            return await TryLoginFromSqliteByPasscodeAsync(companyCode, password);

        try
        {
            var connectionString = DatabaseRuntime.GetConnectionString();
            if (string.IsNullOrWhiteSpace(connectionString)) return null;

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();

            const string sql = """
                SELECT
                    u.user_id, u.username, u.password_hash, u.password_salt,
                    u.full_name_ar, u.email, c.company_code, c.company_name_ar,
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

            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("@CompanyCode", companyCode);
            await using var reader = await command.ExecuteReaderAsync();
            var matches = new List<DbUserCredential>();

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
                    reader["role_name"]?.ToString() ?? "User"));
            }

            if (matches.Count != 1) return null;
            var matched = matches[0];
            return BuildDatabaseUser(
                matched.UserId, matched.Username, matched.FullName, matched.Email,
                matched.CompanyCode, matched.CompanyName, matched.RoleCode, matched.RoleName);
        }
        catch (Exception ex)
        {
            AppHost.Logging.LogWarning("AuthServ", $"Database authentication failed: {ex.Message}");
            return null;
        }
    }

    private static async Task<UserInfo?> TryLoginFromSqliteAsync(string companyCode, string username, string password)
    {
        var cs = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(cs)) return null;

        await using var db = new SqliteConnection(cs);
        await db.OpenAsync();
        await using var cmd = new SqliteCommand("""
            SELECT u.user_id, u.username, u.password_hash, u.password_salt, u.full_name_ar, u.email,
                   c.company_code, c.company_name_ar,
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
            """, db);
        cmd.Parameters.AddWithValue("@company", companyCode);
        cmd.Parameters.AddWithValue("@username", username);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        if (!PwdHash.Verify(password, reader["password_hash"]?.ToString() ?? "", reader["password_salt"]?.ToString() ?? ""))
            return null;
        return BuildDatabaseUser(reader["user_id"], reader["username"], reader["full_name_ar"], reader["email"],
            reader["company_code"], reader["company_name_ar"], reader["role_code"], reader["role_name"]);
    }

    private static async Task<UserInfo?> TryLoginFromSqliteByPasscodeAsync(string companyCode, string password)
    {
        var cs = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(cs)) return null;

        await using var db = new SqliteConnection(cs);
        await db.OpenAsync();
        await using var cmd = new SqliteCommand("""
            SELECT u.user_id, u.username, u.password_hash, u.password_salt, u.full_name_ar, u.email,
                   c.company_code, c.company_name_ar,
                   COALESCE((SELECT r.role_code FROM user_roles ur INNER JOIN roles r ON r.role_id=ur.role_id
                             WHERE ur.user_id=u.user_id AND r.is_active=1 AND r.is_deleted=0
                             ORDER BY CASE r.role_code WHEN 'ROLE_ADMINISTRATOR' THEN 0 WHEN 'ROLE_USER' THEN 1 WHEN 'ROLE_VIEWER' THEN 2 ELSE 9 END LIMIT 1), 'ROLE_USER') AS role_code,
                   COALESCE((SELECT r.role_name FROM user_roles ur INNER JOIN roles r ON r.role_id=ur.role_id
                             WHERE ur.user_id=u.user_id AND r.is_active=1 AND r.is_deleted=0
                             ORDER BY CASE r.role_code WHEN 'ROLE_ADMINISTRATOR' THEN 0 WHEN 'ROLE_USER' THEN 1 WHEN 'ROLE_VIEWER' THEN 2 ELSE 9 END LIMIT 1), 'User') AS role_name
            FROM users u INNER JOIN companies c ON c.company_id=u.company_id
            WHERE c.company_code=@company AND u.is_active=1 AND u.is_deleted=0
              AND c.is_active=1 AND c.is_deleted=0
            """, db);
        cmd.Parameters.AddWithValue("@company", companyCode);
        await using var reader = await cmd.ExecuteReaderAsync();
        UserInfo? match = null;
        while (await reader.ReadAsync())
        {
            if (!PwdHash.Verify(password, reader["password_hash"]?.ToString() ?? "", reader["password_salt"]?.ToString() ?? ""))
                continue;
            if (match != null) return null;
            match = BuildDatabaseUser(reader["user_id"], reader["username"], reader["full_name_ar"], reader["email"],
                reader["company_code"], reader["company_name_ar"], reader["role_code"], reader["role_name"]);
        }
        return match;
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

        return "الشركة الرئيسية";
    }
}

}
