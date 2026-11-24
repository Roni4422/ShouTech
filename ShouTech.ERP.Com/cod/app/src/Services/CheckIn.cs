// ========================================================================
// FILE: Services/CheckIn.cs
// PROJECT: SHOUTECH ERP — Layer-2 check-in client: allowlist compliance
//         report, signed verdicts, daily STEI backup upload
//
// Implements the telemetry study (docs/TELEMETRY_BACKUP_STUDY.md)
// §6 protocol and §7 enforcement client-side, under the owner's
// settled decisions: disclosure is a EULA license condition (no
// opt-in, no customer-side off switch); the client is INERT until a
// vendor server URL is configured; check-in runs only after login +
// company open, in the background — never in the splash/startup
// critical path; a permanently-offline installation is a normal
// state, never a violation (§1, §6.3).
//
// Merged: InstallationKeyStore, CheckInReportBuilder, VerdictStore,
//         CheckInStateStore, SignedTransport, CheckInSvc,
//         BackupUploadSvc
// ========================================================================

#nullable enable

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.App.Common;
using ShouTech.App.Configuration;
using ShouTech.Domain;

namespace ShouTech.App.Services
{
    // ========== InstallationKeyStore.cs ==========
    /// <summary>
    /// The per-installation API keypair (study §6.2): the license key
    /// is the installation's IDENTITY; this keypair is its CREDENTIAL.
    /// Generated once, at first use (the registration act), and never
    /// silently regenerated — a lost or corrupt key is a vendor-side
    /// registry operation, so a corrupt store is a hard failure, not
    /// a quiet re-mint. The private key is DPAPI-protected on Windows
    /// (the STEI signing-key pattern); on a Windows host a DPAPI
    /// failure is a hard failure too. Plain storage exists only on
    /// non-Windows development/test hosts.
    /// </summary>
    public sealed class InstallationKeyStore
    {
        private readonly string _path;
        private RSA? _rsa;

        public InstallationKeyStore(string? path = null)
            => _path = path ?? Path.Combine(AppPaths.Security, "installation-key.dat");

        public RSA Key
        {
            get
            {
                if (_rsa is not null)
                    return _rsa;

                if (File.Exists(_path))
                {
                    var stored = File.ReadAllBytes(_path);
                    var plain = Unprotect(stored)
                        ?? throw new InvalidOperationException(
                            "The installation key exists but cannot be unprotected. Key rotation is a vendor registry operation; the key is never silently regenerated.");
                    var rsa = RSA.Create();
                    rsa.ImportPkcs8PrivateKey(plain, out _);
                    CryptographicOperations.ZeroMemory(plain);
                    return _rsa = rsa;
                }

                var created = RSA.Create(3072);
                var pkcs8 = created.ExportPkcs8PrivateKey();
                try
                {
                    var dir = Path.GetDirectoryName(_path);
                    if (!string.IsNullOrEmpty(dir))
                        Directory.CreateDirectory(dir);
                    File.WriteAllBytes(_path, Protect(pkcs8));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(pkcs8);
                }

                AppHost.Logging.LogInfo("CheckIn", "Installation API keypair generated (first registration).");
                return _rsa = created;
            }
        }

        public string PublicKeyPem => Key.ExportSubjectPublicKeyInfoPem();

        /// <summary>SHA-256 hex of the SubjectPublicKeyInfo — the
        /// registry-facing fingerprint of this credential.</summary>
        public string PublicKeyFingerprint
            => Convert.ToHexString(SHA256.HashData(Key.ExportSubjectPublicKeyInfo())).ToLowerInvariant();

        public byte[] Sign(byte[] data)
            => Key.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);

        private static byte[] Protect(byte[] plain)
        {
            if (OperatingSystem.IsWindows())
                return ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            return plain; // non-Windows dev/test hosts only
        }

        private static byte[]? Unprotect(byte[] stored)
        {
            try
            {
                return OperatingSystem.IsWindows()
                    ? ProtectedData.Unprotect(stored, null, DataProtectionScope.CurrentUser)
                    : stored;
            }
            catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException)
            {
                return null;
            }
        }
    }

    // ========== CheckInReportBuilder.cs ==========
    /// <summary>
    /// Builds the compliance report from the system's own stores —
    /// the study §4 allowlist ONLY: program identity, license
    /// identity + the signed snapshot's claims, the licensed company
    /// subset, the user subset for seat comparison (never credential
    /// material), and the device fingerprint as component HASHES
    /// (raw serials never leave the machine). The harness asserts
    /// the serialized key set, so a future field cannot slip in
    /// unreviewed.
    /// </summary>
    public static class CheckInReportBuilder
    {
        public sealed record UserRow(
            string UserId, string Username, string FullName, string? Email,
            bool IsActive, IReadOnlyList<string> Roles, string? CreatedAtUtc);

        public static async Task<object> BuildAsync(
            IDevInfoSv deviceInfo,
            FingerprintSvc fingerprint,
            InstallationKeyStore keyStore,
            CancellationToken ct = default)
        {
            var device = deviceInfo.Current;
            var snapshot = AppHost.Entitlements?.Current;
            var license = AppHost.License?.Current;
            var fp = fingerprint.Collect();

            string? steiFingerprint = null;
            try
            {
                using var stei = SteiCrypto.LoadSigningKey();
                steiFingerprint = SteiCrypto.Fingerprint(stei.ExportSubjectPublicKeyInfo());
            }
            catch (Exception ex)
            {
                AppHost.Errors.Report(ErrorCodes.CheckInFailed, ex, "CheckIn.Report.SteiFingerprint");
            }

            var company = await LoadCompanySubsetAsync(ct).ConfigureAwait(false);
            var users = await LoadUserSubsetAsync(ct).ConfigureAwait(false);

            return new
            {
                installation = new
                {
                    deviceId = device.DeviceId,
                    instanceId = AppCfgSvc.Current.Application?.InstanceId,
                    appVersion = device.AppVersion
                },
                license = new
                {
                    licenseKey = LicenseIdentity(license),
                    licenseId = snapshot?.LicenseId,
                    status = snapshot?.Status.ToString(),
                    edition = snapshot?.Edition,
                    source = snapshot?.Source.ToString(),
                    modules = snapshot?.Modules.OrderBy(m => m, StringComparer.Ordinal).ToArray() ?? Array.Empty<string>(),
                    maxUsers = snapshot?.MaxUsers ?? AppCfgSvc.Current.Licensing?.MaxUsers,
                    maxCompanies = snapshot?.MaxCompanies ?? AppCfgSvc.Current.Licensing?.MaxCompanies,
                    features = AppCfgSvc.Current.Licensing?.Features?.ToArray() ?? Array.Empty<string>(),
                    expiresAtUtc = snapshot?.ExpiresAt,
                    isTrial = snapshot?.IsTrial ?? false,
                    isViewer = snapshot?.IsViewer ?? false,
                    activatedAtUtc = license?.ActivatedAt,
                    lastOnlineCheckUtc = license?.LastOnlineCheck
                },
                company,
                users = new
                {
                    activeCount = users.Count(u => u.IsActive),
                    list = users
                },
                device = new
                {
                    machineName = device.MachineName,
                    osVersion = device.OsVersion,
                    fingerprint = new
                    {
                        components = fp.Components,
                        quality = fp.ReducedQuality ? "reduced" : "full",
                        substantiveCount = fp.SubstantiveCount
                    },
                    steiSigningKeyFingerprint = steiFingerprint,
                    installationPublicKey = keyStore.PublicKeyPem,
                    installationPublicKeyFingerprint = keyStore.PublicKeyFingerprint
                },
                reportLocalDate = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                sentAtUtc = DateTime.UtcNow
            };
        }

        /// <summary>The license identity being verified (§4.1): the
        /// activation service's key, else the configured dongle
        /// serial, else the development literal.</summary>
        public static string LicenseIdentity(LicenseInfo? license)
        {
            if (!string.IsNullOrWhiteSpace(license?.LicenseKey))
                return license.LicenseKey.Trim();
            var serial = AppCfgSvc.Current.Licensing?.DongleInfo?.SerialNumber;
            return string.IsNullOrWhiteSpace(serial) ? "LOCAL-DEV" : serial.Trim();
        }

        private static async Task<object?> LoadCompanySubsetAsync(CancellationToken ct)
        {
            var code = AppState.CurrentCompanyCode;
            if (string.IsNullOrWhiteSpace(code))
                return null;

            try
            {
                var companies = await CoLocalStore.LoadAllAsync().ConfigureAwait(false);
                var record = companies.FirstOrDefault(c => string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase));
                if (record is null)
                    return null;

                // §4.3 sent subset ONLY — never AdminPasswordHash/
                // Salt, lockout state, or local paths.
                return new
                {
                    code = record.Code,
                    nameAr = record.Name,
                    nameEn = record.NameEn,
                    taxNumber = record.TaxNumber,
                    email = record.Email,
                    phone = record.Phone,
                    mobile = record.Mobile,
                    currencyCode = record.CurrencyCode,
                    languageCode = record.LanguageCode,
                    licenseRef = record.License,
                    createdAtUtc = record.CreatedAt
                };
            }
            catch (Exception ex)
            {
                AppHost.Errors.Report(ErrorCodes.CheckInFailed, ex, "CheckIn.Report.Company");
                return null;
            }
        }

        private static async Task<IReadOnlyList<UserRow>> LoadUserSubsetAsync(CancellationToken ct)
        {
            var rows = new List<UserRow>();
            var connectionString = DatabaseRuntime.GetConnectionString();
            var companyCode = AppState.CurrentCompanyCode;
            if (string.IsNullOrWhiteSpace(connectionString) || string.IsNullOrWhiteSpace(companyCode))
                return rows;

            try
            {
                var isPostgreSql = DatabaseRuntime.IsPostgreSql();
                using DbConnection connection = isPostgreSql
                    ? new NpgsqlConnection(connectionString)
                    : new SqliteConnection(connectionString);
                await connection.OpenAsync(ct).ConfigureAwait(false);

                // §4.2 sent subset ONLY — password_hash / password_salt
                // / must_change_password are never selected.
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT u.user_id, u.username, u.full_name, u.email, u.is_active, u.created_at,
                           COALESCE((SELECT GROUP_CONCAT(r.role_code, ',') FROM user_roles ur
                                     JOIN roles r ON r.role_id = ur.role_id
                                     WHERE ur.user_id = u.user_id), '') AS roles
                    FROM users u
                    WHERE u.is_deleted = 0
                      AND u.company_id = (SELECT company_id FROM companies WHERE company_code = @code)
                    ORDER BY u.username
                    """;
                // GROUP_CONCAT is SQLite spelling; PostgreSQL uses
                // STRING_AGG — pick per provider.
                if (isPostgreSql)
                    command.CommandText = command.CommandText.Replace(
                        "GROUP_CONCAT(r.role_code, ',')", "STRING_AGG(r.role_code, ',')");
                var codeParam = command.CreateParameter();
                codeParam.ParameterName = "@code";
                codeParam.Value = companyCode;
                command.Parameters.Add(codeParam);

                using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var roles = (reader.IsDBNull(6) ? string.Empty : reader.GetString(6))
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    rows.Add(new UserRow(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3),
                        ToBool(reader.GetValue(4)),
                        roles,
                        reader.IsDBNull(5) ? null : reader.GetValue(5)?.ToString()));
                }
            }
            catch (Exception ex)
            {
                AppHost.Errors.Report(ErrorCodes.CheckInFailed, ex, "CheckIn.Report.Users");
            }

            return rows;
        }

        /// <summary>is_active arrives as long (SQLite), int or bool
        /// (PostgreSQL) depending on provider and column typing.</summary>
        private static bool ToBool(object value) => value switch
        {
            bool b => b,
            long l => l != 0,
            int i => i != 0,
            _ => Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture) != 0
        };
    }

    // ========== VerdictStore.cs ==========
    /// <summary>The signed verdict object (study §7.2) as carried in
    /// the envelope pattern the license file already uses: the
    /// payload JSON is signed verbatim.</summary>
    public sealed class VerdictPayload
    {
        public string InstallationId { get; set; } = string.Empty;
        public string LicenseId { get; set; } = string.Empty;
        public string Verdict { get; set; } = string.Empty;
        public string? ReasonCategory { get; set; }
        public long Seq { get; set; }
        public DateTime IssuedAtUtc { get; set; }
        public string? DetailRef { get; set; }
    }

    public sealed class VerdictEnvelope
    {
        public string Payload { get; set; } = string.Empty;
        public string Signature { get; set; } = string.Empty;
    }

    /// <summary>
    /// The verdict cache (study §7.2): the last verified verdict
    /// envelope, stored with its signature and re-verified at every
    /// login. A missing or corrupt cache means NO verdict — never
    /// demo (corruption fails toward the customer's availability;
    /// layer 1 remains fully in force). The highest applied seq is
    /// stored beside the envelope; a verdict at or below it is a
    /// replay and is ignored (ONL-8502).
    /// </summary>
    public sealed class VerdictStore
    {
        private sealed class CacheFile
        {
            public VerdictEnvelope? Envelope { get; set; }
            public long HighestAppliedSeq { get; set; }
        }

        private readonly string _path;

        public VerdictStore(string? path = null)
            => _path = path ?? Path.Combine(AppPaths.Cfg, "verdict-cache.json");

        /// <summary>Verifies and records a verdict envelope received
        /// from the server. Returns the payload when it was applied
        /// (or was already current), null when it was discarded
        /// (bad signature — ONL-8501; replay/foreign — ONL-8502).</summary>
        public VerdictPayload? Accept(VerdictEnvelope envelope, string expectedDeviceId, string expectedLicenseIdentity)
        {
            var payload = Verify(envelope);
            if (payload is null)
            {
                AppHost.Logging.LogWarning("CheckIn",
                    $"[{ErrorCodes.VerdictSignatureInvalid}] Verdict discarded: signature verification failed.");
                return null;
            }

            if (!string.Equals(payload.InstallationId, expectedDeviceId, StringComparison.Ordinal)
                || !string.Equals(payload.LicenseId, expectedLicenseIdentity, StringComparison.Ordinal))
            {
                AppHost.Logging.LogWarning("CheckIn",
                    $"[{ErrorCodes.VerdictReplayIgnored}] Verdict discarded: addressed to a different installation/license.");
                return null;
            }

            var cache = Read();
            if (payload.Seq <= cache.HighestAppliedSeq)
            {
                AppHost.Logging.LogWarning("CheckIn",
                    $"[{ErrorCodes.VerdictReplayIgnored}] Verdict seq {payload.Seq} ignored (highest applied {cache.HighestAppliedSeq}).");
                return null;
            }

            Write(new CacheFile { Envelope = envelope, HighestAppliedSeq = payload.Seq });
            return payload;
        }

        /// <summary>The cached verdict, re-verified. Null when there
        /// is no cache or it does not verify — never an error.</summary>
        public VerdictPayload? LoadVerified()
        {
            var cache = Read();
            return cache.Envelope is null ? null : Verify(cache.Envelope);
        }

        public static VerdictPayload? Verify(VerdictEnvelope envelope)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(envelope.Payload) || string.IsNullOrWhiteSpace(envelope.Signature))
                    return null;
                var signature = Convert.FromBase64String(envelope.Signature);
                if (!VendorKey.Verify(Encoding.UTF8.GetBytes(envelope.Payload), signature))
                    return null;
                return JsonSerializer.Deserialize<VerdictPayload>(envelope.Payload, JsonDefs.AppSetngRead);
            }
            catch (Exception ex) when (ex is FormatException or JsonException or CryptographicException)
            {
                return null;
            }
        }

        private CacheFile Read()
        {
            try
            {
                if (!File.Exists(_path))
                    return new CacheFile();
                return JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(_path), JsonDefs.AppSetngRead) ?? new CacheFile();
            }
            catch (Exception ex)
            {
                AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, ex, "CheckIn.VerdictStore.Read");
                return new CacheFile();
            }
        }

        private void Write(CacheFile cache)
        {
            try
            {
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(_path, JsonSerializer.Serialize(cache, JsonDefs.Indented));
            }
            catch (Exception ex)
            {
                AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, ex, "CheckIn.VerdictStore.Write");
            }
        }
    }

    // ========== CheckInStateStore.cs ==========
    /// <summary>
    /// Local check-in state (study §6.3 markers + §8 status): per-
    /// company daily markers (scheduling convenience only — the
    /// server's record is authoritative) and the last-known status
    /// the License window renders. Plain local JSON; it carries no
    /// authority — enforcement derives from signed artifacts only.
    /// </summary>
    public sealed class CheckInStateStore
    {
        public sealed class CompanyMarker
        {
            public string? LastReportLocalDate { get; set; }
            public string? LastUploadLocalDate { get; set; }
            public string? LastUploadedPackageId { get; set; }
        }

        public sealed class BackupCopyInfo
        {
            public string PackageId { get; set; } = string.Empty;
            public string? BackupDateUtc { get; set; }
            public long SizeBytes { get; set; }
            public string? PayloadHash { get; set; }
        }

        public sealed class State
        {
            public Dictionary<string, CompanyMarker> Companies { get; set; } = new(StringComparer.OrdinalIgnoreCase);
            public DateTime? LastReportUtc { get; set; }
            public DateTime? LastUploadUtc { get; set; }
            public string? LastVerdict { get; set; }
            public string? LastVerdictReason { get; set; }
            public long LastVerdictSeq { get; set; }
            public List<BackupCopyInfo> ServerCopies { get; set; } = new();
            public string? LastErrorCode { get; set; }
            public DateTime UpdatedUtc { get; set; }
        }

        private readonly string _path;
        private readonly object _lock = new();

        public CheckInStateStore(string? path = null)
            => _path = path ?? Path.Combine(AppPaths.Cfg, "checkin-state.json");

        public State Read()
        {
            lock (_lock)
            {
                try
                {
                    if (!File.Exists(_path))
                        return new State();
                    return JsonSerializer.Deserialize<State>(File.ReadAllText(_path), JsonDefs.AppSetngRead) ?? new State();
                }
                catch (Exception ex)
                {
                    AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, ex, "CheckIn.StateStore.Read");
                    return new State();
                }
            }
        }

        public void Update(Action<State> mutate)
        {
            lock (_lock)
            {
                var state = Read();
                mutate(state);
                state.UpdatedUtc = DateTime.UtcNow;
                try
                {
                    var dir = Path.GetDirectoryName(_path);
                    if (!string.IsNullOrEmpty(dir))
                        Directory.CreateDirectory(dir);
                    File.WriteAllText(_path, JsonSerializer.Serialize(state, JsonDefs.Indented));
                }
                catch (Exception ex)
                {
                    AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, ex, "CheckIn.StateStore.Write");
                }
            }
        }

        public static CompanyMarker MarkerFor(State state, string companyCode)
        {
            if (!state.Companies.TryGetValue(companyCode, out var marker))
            {
                marker = new CompanyMarker();
                state.Companies[companyCode] = marker;
            }
            return marker;
        }
    }

    // ========== SignedTransport.cs ==========
    /// <summary>
    /// The signed-request transport (study §6.2): every check-in
    /// request is signed with the installation keypair over
    /// method | path | body SHA-256 | timestamp | nonce, and sent
    /// with the signature headers. The base URL is the configured
    /// activation server; TLS is mandatory — a plain-HTTP base is
    /// refused except loopback (development/mock servers only).
    /// </summary>
    public sealed class SignedTransport
    {
        private readonly InstallationKeyStore _keys;
        private readonly HttpClient _http;

        public SignedTransport(InstallationKeyStore keys, HttpClient? http = null)
        {
            _keys = keys;
            _http = http ?? SharedHttp;
        }

        private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(120) };

        public static string? BaseUrl => AppCfgSvc.Current.Licensing?.Activation?.ActivationServer?.Trim();

        public static bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl) && IsUrlAllowed(BaseUrl!);

        public static bool IsUrlAllowed(string baseUrl)
        {
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
                return false;
            return uri.Scheme == Uri.UriSchemeHttps || uri.IsLoopback;
        }

        public async Task<HttpResponseMessage> SendAsync(
            HttpMethod method, string endpoint, byte[]? body, string contentType = "application/json",
            IReadOnlyDictionary<string, string>? extraHeaders = null, CancellationToken ct = default)
        {
            var baseUrl = BaseUrl ?? throw new InvalidOperationException("No activation server is configured.");
            if (!IsUrlAllowed(baseUrl))
                throw new InvalidOperationException("The activation server URL must use HTTPS (loopback excepted for development).");

            var path = "/" + endpoint.TrimStart('/');
            var url = baseUrl.TrimEnd('/') + path;
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
            var nonce = Guid.NewGuid().ToString("N");
            var bodyHash = Convert.ToHexString(SHA256.HashData(body ?? Array.Empty<byte>())).ToLowerInvariant();
            var canonical = $"{method.Method}\n{path}\n{bodyHash}\n{timestamp}\n{nonce}";
            var signature = Convert.ToBase64String(_keys.Sign(Encoding.UTF8.GetBytes(canonical)));

            using var request = new HttpRequestMessage(method, url);
            request.Headers.Add("X-ShouTech-DeviceId", AppHost.DeviceInfo?.Current.DeviceId ?? string.Empty);
            request.Headers.Add("X-ShouTech-Timestamp", timestamp);
            request.Headers.Add("X-ShouTech-Nonce", nonce);
            request.Headers.Add("X-ShouTech-Signature", signature);
            if (extraHeaders is not null)
                foreach (var (name, value) in extraHeaders)
                    request.Headers.Add(name, value);
            if (body is not null)
                request.Content = new ByteArrayContent(body) { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType) } };

            return await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
    }

    // ========== CheckInSvc.cs ==========
    /// <summary>
    /// The layer-2 cycle (study §6.3): after login + company open,
    /// once per local day per company — send the allowlist report,
    /// apply any signed verdict it carries, and upload today's
    /// backup. Offline or unconfigured = silent skip. The verdict
    /// cache is applied separately at login (ApplyCachedVerdict) so
    /// demo state is derived before the shell opens, with zero
    /// network involvement.
    /// </summary>
    public sealed class CheckInSvc
    {
        private static readonly Lazy<CheckInSvc> SharedInstance = new(() => new CheckInSvc(
            AppHost.Logging, AppHost.DeviceInfo, new InstallationKeyStore(), new VerdictStore(), new CheckInStateStore()));

        public static CheckInSvc Shared => SharedInstance.Value;

        private readonly ILogServ _log;
        private readonly IDevInfoSv _deviceInfo;
        private readonly InstallationKeyStore _keys;
        private readonly VerdictStore _verdicts;
        private readonly CheckInStateStore _state;
        private readonly SignedTransport _transport;
        private readonly BackupUploadSvc _uploads;
        private int _cycleRunning;

        public CheckInSvc(
            ILogServ log, IDevInfoSv deviceInfo, InstallationKeyStore keys,
            VerdictStore verdicts, CheckInStateStore state, HttpClient? http = null)
        {
            _log = log;
            _deviceInfo = deviceInfo;
            _keys = keys;
            _verdicts = verdicts;
            _state = state;
            _transport = new SignedTransport(keys, http);
            _uploads = new BackupUploadSvc(keys, _transport, state, log);
        }

        public BackupUploadSvc Uploads => _uploads;

        /// <summary>Re-verifies the cached verdict and applies it to
        /// the entitlement gate. Called from the login path before
        /// the shell opens; purely local. A missing/corrupt cache
        /// applies nothing.</summary>
        public void ApplyCachedVerdict()
        {
            try
            {
                var payload = _verdicts.LoadVerified();
                if (payload is null)
                    return;
                ApplyVerdict(payload, fromCache: true);
            }
            catch (Exception ex)
            {
                AppHost.Errors.Report(ErrorCodes.CheckInFailed, ex, "CheckIn.ApplyCachedVerdict");
            }
        }

        /// <summary>The post-login background cycle. Fire-and-forget
        /// safe: all failures are catalog-reported and recorded in
        /// the state store; nothing throws to the caller.</summary>
        public async Task RunPostLoginCycleAsync(bool force = false, CancellationToken ct = default)
        {
            if (Interlocked.Exchange(ref _cycleRunning, 1) == 1)
                return;
            try
            {
                ApplyCachedVerdict();

                if (!SignedTransport.IsConfigured)
                {
                    _log.LogInfo("CheckIn", "No activation server configured — check-in inert.");
                    return;
                }

                var companyCode = AppState.CurrentCompanyCode;
                if (string.IsNullOrWhiteSpace(companyCode))
                    return;

                var today = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                var state = _state.Read();
                var marker = CheckInStateStore.MarkerFor(state, companyCode);

                if (force || marker.LastReportLocalDate != today)
                    await SendReportAsync(companyCode, today, ct).ConfigureAwait(false);

                state = _state.Read();
                marker = CheckInStateStore.MarkerFor(state, companyCode);
                if (force || marker.LastUploadLocalDate != today)
                    await _uploads.UploadTodaysBackupAsync(companyCode, today, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppHost.Errors.Report(ErrorCodes.CheckInFailed, ex, "CheckIn.RunPostLoginCycle");
                _state.Update(s => s.LastErrorCode = ErrorCodes.CheckInFailed);
            }
            finally
            {
                Interlocked.Exchange(ref _cycleRunning, 0);
            }
        }

        /// <summary>The banner's "check now" (study §7.6): fetch the
        /// current verdict immediately and apply it in this session.</summary>
        public async Task<bool> FetchVerdictNowAsync(CancellationToken ct = default)
        {
            if (!SignedTransport.IsConfigured)
                return false;
            try
            {
                var endpoint = AppCfgSvc.Current.OnlineServices?.VerdictEndpoint ?? "/installations/verdict";
                using var response = await _transport.SendAsync(HttpMethod.Get, endpoint, null, ct: ct).ConfigureAwait(false);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    return false;
                response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return HandleVerdictResponse(body);
            }
            catch (Exception ex)
            {
                AppHost.Errors.Report(ErrorCodes.CheckInFailed, ex, "CheckIn.FetchVerdictNow");
                return false;
            }
        }

        private async Task SendReportAsync(string companyCode, string today, CancellationToken ct)
        {
            var report = await CheckInReportBuilder.BuildAsync(_deviceInfo, new FingerprintSvc(), _keys, ct).ConfigureAwait(false);
            var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(report, JsonDefs.Indented));
            var endpoint = AppCfgSvc.Current.OnlineServices?.ReportEndpoint ?? "/installations/report";

            using var response = await _transport.SendAsync(HttpMethod.Post, endpoint, body, ct: ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                AppHost.Errors.Report(
                    ErrorCodes.CheckInFailed,
                    new HttpRequestException($"Report rejected with HTTP {(int)response.StatusCode}."),
                    "CheckIn.SendReport");
                _state.Update(s => s.LastErrorCode = ErrorCodes.CheckInFailed);
                return;
            }

            var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            HandleVerdictResponse(responseBody);
            HandleInventoryResponse(responseBody);

            _state.Update(s =>
            {
                s.LastReportUtc = DateTime.UtcNow;
                s.LastErrorCode = null;
                CheckInStateStore.MarkerFor(s, companyCode).LastReportLocalDate = today;
            });
            _log.LogInfo("CheckIn", $"Daily report sent for company {companyCode}.");
        }

        private bool HandleVerdictResponse(string responseBody)
        {
            try
            {
                using var doc = JsonDocument.Parse(responseBody);
                if (!doc.RootElement.TryGetProperty("verdictEnvelope", out var envelopeElement)
                    || envelopeElement.ValueKind != JsonValueKind.Object)
                    return false;

                var envelope = JsonSerializer.Deserialize<VerdictEnvelope>(envelopeElement.GetRawText(), JsonDefs.AppSetngRead);
                if (envelope is null)
                    return false;

                var payload = _verdicts.Accept(
                    envelope,
                    _deviceInfo.Current.DeviceId,
                    CheckInReportBuilder.LicenseIdentity(AppHost.License?.Current));
                if (payload is null)
                    return false;

                ApplyVerdict(payload, fromCache: false);
                return true;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                AppHost.Errors.Report(ErrorCodes.CheckInFailed, ex, "CheckIn.HandleVerdictResponse");
                return false;
            }
        }

        private void HandleInventoryResponse(string responseBody)
        {
            try
            {
                using var doc = JsonDocument.Parse(responseBody);
                if (!doc.RootElement.TryGetProperty("backupInventory", out var inventory)
                    || inventory.ValueKind != JsonValueKind.Array)
                    return;

                var copies = JsonSerializer.Deserialize<List<CheckInStateStore.BackupCopyInfo>>(
                    inventory.GetRawText(), JsonDefs.AppSetngRead);
                if (copies is not null)
                    _state.Update(s => s.ServerCopies = copies);
            }
            catch (Exception ex) when (ex is JsonException)
            {
                AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, ex, "CheckIn.HandleInventoryResponse");
            }
        }

        private void ApplyVerdict(VerdictPayload payload, bool fromCache)
        {
            var entitlements = AppHost.Entitlements;
            _state.Update(s =>
            {
                s.LastVerdict = payload.Verdict;
                s.LastVerdictReason = payload.ReasonCategory;
                s.LastVerdictSeq = payload.Seq;
            });

            switch (payload.Verdict.Trim().ToUpperInvariant())
            {
                case "MISMATCH-CONFIRMED":
                    entitlements?.ApplyVerdictOverlay(payload.ReasonCategory ?? "fingerprint");
                    _log.LogWarning("CheckIn",
                        $"Signed verdict applied{(fromCache ? " from cache" : string.Empty)}: MISMATCH-CONFIRMED (reason {payload.ReasonCategory}, seq {payload.Seq}) — installation is now view-only.");
                    break;
                case "OK":
                case "CLEARED":
                case "EXEMPT":
                    entitlements?.ApplyVerdictOverlay(null);
                    _log.LogInfo("CheckIn",
                        $"Signed verdict applied{(fromCache ? " from cache" : string.Empty)}: {payload.Verdict} (seq {payload.Seq}).");
                    break;
                default:
                    _log.LogWarning("CheckIn", $"Unknown verdict value '{payload.Verdict}' ignored.");
                    break;
            }
        }
    }

    // ========== BackupUploadSvc.cs ==========
    /// <summary>
    /// The daily STEI backup upload (study §6.1/§6.4/§6.5):
    /// descriptor read from the package's own manifest (the .stb is
    /// a ZIP; the manifest is readable without decryption), then
    /// initiate → per-part SHA-256 resumable parts → complete.
    /// Idempotent by PackageId; retention-3 is the SERVER's decision
    /// — the client only records the authoritative inventory the
    /// complete response returns. The package is encrypted with the
    /// per-installation key (owner decision C): the vendor stores
    /// ciphertext it cannot read.
    /// </summary>
    public sealed class BackupUploadSvc
    {
        private const int DefaultPartSize = 1024 * 1024;
        private const int MaxAttemptsPerPart = 3;

        private readonly SignedTransport _transport;
        private readonly CheckInStateStore _state;
        private readonly ILogServ _log;

        public BackupUploadSvc(InstallationKeyStore keys, SignedTransport transport, CheckInStateStore state, ILogServ log)
        {
            _ = keys; // signing lives in the transport; the key store is accepted for symmetry/future per-upload proofs
            _transport = transport;
            _state = state;
            _log = log;
        }

        public sealed record PackageDescriptor(
            string PackageId, string PayloadHash, long SizeBytes,
            string CompanyCode, string BackupDateUtc, string FilePath);

        /// <summary>Reads the descriptor from a .stb package's
        /// manifest without decrypting the payload.</summary>
        public static PackageDescriptor ReadDescriptor(string stbPath)
        {
            using var archive = ZipFile.OpenRead(stbPath);
            var entry = archive.GetEntry("manifest.json")
                ?? throw new InvalidDataException($"STEI manifest missing in {stbPath}.");
            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var manifest = JsonSerializer.Deserialize<SteiManifest>(reader.ReadToEnd(), JsonDefs.AppSetngRead)
                ?? throw new InvalidDataException($"STEI manifest unreadable in {stbPath}.");
            var info = new FileInfo(stbPath);
            return new PackageDescriptor(
                manifest.PackageId, manifest.PayloadHash, info.Length,
                manifest.SourceCompanyCode, manifest.ExportedAtUtc, stbPath);
        }

        /// <summary>Ensures today's backup for the company exists
        /// locally (creating it through BkpServ when needed — backup
        /// creation is allowed even in demo mode, study §7.4) and
        /// uploads it. Returns true when the server holds the
        /// package (uploaded now or already current).</summary>
        public async Task<bool> UploadTodaysBackupAsync(string companyCode, string todayLocalDate, CancellationToken ct = default)
        {
            try
            {
                var backups = AppHost.Backup.ListBackups(companyCode)
                    .OrderByDescending(b => b.LastWriteUtc)
                    .ToList();
                var todays = backups.FirstOrDefault(b => b.LastWriteUtc.ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) == todayLocalDate);

                string path;
                if (todays is not null)
                {
                    path = todays.FilePath;
                }
                else
                {
                    path = await AppHost.Backup.CreateBackupAsync(companyCode, ct).ConfigureAwait(false);
                }

                var descriptor = ReadDescriptor(path);
                var state = _state.Read();
                var marker = CheckInStateStore.MarkerFor(state, companyCode);
                if (marker.LastUploadedPackageId == descriptor.PackageId && marker.LastUploadLocalDate == todayLocalDate)
                {
                    _log.LogInfo("CheckIn", $"Backup {descriptor.PackageId} already uploaded today — idempotent skip.");
                    return true;
                }

                var uploaded = await UploadPackageAsync(descriptor, ct).ConfigureAwait(false);
                if (uploaded)
                {
                    _state.Update(s =>
                    {
                        var m = CheckInStateStore.MarkerFor(s, companyCode);
                        m.LastUploadLocalDate = todayLocalDate;
                        m.LastUploadedPackageId = descriptor.PackageId;
                        s.LastUploadUtc = DateTime.UtcNow;
                    });
                }
                return uploaded;
            }
            catch (Exception ex)
            {
                AppHost.Errors.Report(ErrorCodes.BackupUploadFailed, ex, "CheckIn.UploadTodaysBackup");
                _state.Update(s => s.LastErrorCode = ErrorCodes.BackupUploadFailed);
                return false;
            }
        }

        /// <summary>The coordinator's rotated-backup path: upload
        /// the newest EXISTING local package for the company when
        /// the server does not hold it yet. Never creates a backup
        /// (creation is the daily cycle's own decision).</summary>
        public async Task<bool> UploadNewestExistingAsync(string companyCode, CancellationToken ct = default)
        {
            try
            {
                var newest = AppHost.Backup.ListBackups(companyCode)
                    .OrderByDescending(b => b.LastWriteUtc)
                    .FirstOrDefault();
                if (newest is null)
                    return false;

                var descriptor = ReadDescriptor(newest.FilePath);
                var state = _state.Read();
                if (CheckInStateStore.MarkerFor(state, companyCode).LastUploadedPackageId == descriptor.PackageId)
                    return true; // already held by the server

                var uploaded = await UploadPackageAsync(descriptor, ct).ConfigureAwait(false);
                if (uploaded)
                {
                    _state.Update(s =>
                    {
                        CheckInStateStore.MarkerFor(s, companyCode).LastUploadedPackageId = descriptor.PackageId;
                        s.LastUploadUtc = DateTime.UtcNow;
                    });
                }
                return uploaded;
            }
            catch (Exception ex)
            {
                AppHost.Errors.Report(ErrorCodes.BackupUploadFailed, ex, "CheckIn.UploadNewestExisting");
                return false;
            }
        }

        /// <summary>The initiate → parts → complete exchange for one
        /// package. Resumable: parts the server already holds are
        /// skipped.</summary>
        public async Task<bool> UploadPackageAsync(PackageDescriptor descriptor, CancellationToken ct = default)
        {
            var settings = AppCfgSvc.Current.OnlineServices;
            var initiateEndpoint = settings?.BackupInitiateEndpoint ?? "/backup/initiate";

            var initiateBody = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                packageId = descriptor.PackageId,
                payloadHash = descriptor.PayloadHash,
                sizeBytes = descriptor.SizeBytes,
                companyCode = descriptor.CompanyCode,
                backupDateUtc = descriptor.BackupDateUtc
            }, JsonDefs.Indented));

            using var initiateResponse = await _transport.SendAsync(HttpMethod.Post, initiateEndpoint, initiateBody, ct: ct).ConfigureAwait(false);
            if (!initiateResponse.IsSuccessStatusCode)
            {
                AppHost.Errors.Report(ErrorCodes.BackupUploadFailed,
                    new HttpRequestException($"Backup initiate rejected with HTTP {(int)initiateResponse.StatusCode}."),
                    "CheckIn.Upload.Initiate");
                return false;
            }

            string uploadId;
            int partSize;
            var received = new HashSet<int>();
            {
                var json = await initiateResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                uploadId = doc.RootElement.GetProperty("uploadId").GetString()
                    ?? throw new InvalidDataException("Initiate response carried no uploadId.");
                partSize = doc.RootElement.TryGetProperty("partSize", out var ps) && ps.TryGetInt32(out var size)
                    ? Math.Clamp(size, 64 * 1024, 16 * 1024 * 1024)
                    : DefaultPartSize;
                if (doc.RootElement.TryGetProperty("receivedParts", out var parts) && parts.ValueKind == JsonValueKind.Array)
                    foreach (var part in parts.EnumerateArray())
                        if (part.TryGetInt32(out var n))
                            received.Add(n);
            }

            var bytes = await File.ReadAllBytesAsync(descriptor.FilePath, ct).ConfigureAwait(false);
            var totalParts = (int)Math.Ceiling(bytes.Length / (double)partSize);
            var partEndpointTemplate = settings?.BackupPartEndpoint ?? "/backup/{0}/parts/{1}";

            for (var part = 0; part < totalParts; part++)
            {
                if (received.Contains(part))
                    continue;

                var offset = part * partSize;
                var length = Math.Min(partSize, bytes.Length - offset);
                var slice = new byte[length];
                Array.Copy(bytes, offset, slice, 0, length);
                var partHash = Convert.ToHexString(SHA256.HashData(slice)).ToLowerInvariant();
                var endpoint = string.Format(System.Globalization.CultureInfo.InvariantCulture, partEndpointTemplate, uploadId, part);

                var sent = false;
                for (var attempt = 1; attempt <= MaxAttemptsPerPart && !sent; attempt++)
                {
                    try
                    {
                        using var partResponse = await _transport.SendAsync(
                            HttpMethod.Put, endpoint, slice, "application/octet-stream",
                            new Dictionary<string, string> { ["X-Part-Sha256"] = partHash }, ct).ConfigureAwait(false);
                        if (partResponse.StatusCode == System.Net.HttpStatusCode.BadRequest)
                        {
                            AppHost.Errors.Report(ErrorCodes.UploadHashMismatch,
                                new InvalidDataException($"Server rejected part {part} of {descriptor.PackageId} on hash mismatch."),
                                "CheckIn.Upload.Part");
                            return false;
                        }
                        partResponse.EnsureSuccessStatusCode();
                        sent = true;
                    }
                    catch (HttpRequestException ex) when (attempt < MaxAttemptsPerPart)
                    {
                        _log.LogWarning("CheckIn", $"Part {part} attempt {attempt} failed ({ex.Message}); retrying.");
                        await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), ct).ConfigureAwait(false);
                    }
                }

                if (!sent)
                {
                    AppHost.Errors.Report(ErrorCodes.BackupUploadFailed,
                        new HttpRequestException($"Part {part} of {descriptor.PackageId} could not be delivered."),
                        "CheckIn.Upload.Part");
                    return false;
                }
            }

            var completeEndpoint = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                settings?.BackupCompleteEndpoint ?? "/backup/{0}/complete", uploadId);
            var completeBody = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                packageId = descriptor.PackageId,
                payloadHash = descriptor.PayloadHash,
                totalParts
            }, JsonDefs.Indented));

            using var completeResponse = await _transport.SendAsync(HttpMethod.Post, completeEndpoint, completeBody, ct: ct).ConfigureAwait(false);
            if (!completeResponse.IsSuccessStatusCode)
            {
                AppHost.Errors.Report(ErrorCodes.BackupUploadFailed,
                    new HttpRequestException($"Backup complete rejected with HTTP {(int)completeResponse.StatusCode}."),
                    "CheckIn.Upload.Complete");
                return false;
            }

            var completeJson = await completeResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using (var doc = JsonDocument.Parse(completeJson))
            {
                if (doc.RootElement.TryGetProperty("inventory", out var inventory) && inventory.ValueKind == JsonValueKind.Array)
                {
                    var copies = JsonSerializer.Deserialize<List<CheckInStateStore.BackupCopyInfo>>(
                        inventory.GetRawText(), JsonDefs.AppSetngRead);
                    if (copies is not null)
                        _state.Update(s => s.ServerCopies = copies);
                }
            }

            _log.LogInfo("CheckIn", $"Backup {descriptor.PackageId} uploaded ({totalParts} parts, {descriptor.SizeBytes} bytes).");
            return true;
        }
    }
}
