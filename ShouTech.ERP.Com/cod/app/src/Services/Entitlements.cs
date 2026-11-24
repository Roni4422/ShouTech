// ========================================================================
// FILE: Services/Entitlements.cs
// PROJECT: SHOUTECH ERP — Entitlement core: signed license state,
//         provider chain, module gate, demo (view-only) derivation
//
// The ONE live license gate (dongle/module licensing design §3.1,
// telemetry study §1 layer 1). Providers are tried in config order —
// Dongle → LicenseFile → Online → DevMode — and the first snapshot
// wins. Signature verification is fail-closed for granting and
// fail-open only for read access to existing data: a licensing
// failure NEVER bricks a customer's access to their own records —
// the app opens in view-only (demo) mode with the License window
// reachable (owner decisions D1/D2, settled 2026-11-24).
//
// Merged: EntitlementSnapshot, IEntitlementProvider, VendorKey,
//         LicensePayload, DongleProbe, LastSeenStore,
//         DongleProvider, LicenseFileProvider, OnlineProvider,
//         DevModeProvider, EntitlementService
// ========================================================================

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ShouTech.App.Common;
using ShouTech.App.Configuration;
using ShouTech.App.Navigation;
using ShouTech.Domain;
using ShouTech.Domain.Enums;

namespace ShouTech.App.Services
{
    public enum EntitlementStatus
    {
        Valid,
        GracePeriod,
        Expired,
        NotFound,
        SignatureInvalid,
        BindingMismatch,
        DongleMissing,
        Revoked
    }

    public enum EntitlementSource
    {
        None,
        Dongle,
        LicenseFile,
        Online,
        DevMode
    }

    /// <summary>Immutable entitlement snapshot — the only license
    /// truth the shell and services consult.</summary>
    public sealed record EntitlementSnapshot
    {
        public required EntitlementStatus Status { get; init; }
        public string Edition { get; init; } = string.Empty;
        public bool IsTrial { get; init; }
        public bool IsViewer { get; init; }
        public IReadOnlySet<string> Modules { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public string? LicenseId { get; init; }
        public string? CustomerName { get; init; }
        public string? DongleSerial { get; init; }
        public int? MaxUsers { get; init; }
        public int? MaxCompanies { get; init; }
        public DateTime? ExpiresAt { get; init; }
        public DateTime? GraceEndsAt { get; init; }
        public EntitlementSource Source { get; init; } = EntitlementSource.None;
        public DateTime ValidatedAtUtc { get; init; } = DateTime.UtcNow;

        /// <summary>The LIC catalog code describing the failure when
        /// the snapshot does not grant; null when usable.</summary>
        public string? FailureCode { get; init; }

        public bool IsUsable => Status is EntitlementStatus.Valid or EntitlementStatus.GracePeriod;

        /// <summary>Write access from the license layer alone. The
        /// demo gate (EntitlementService.IsDemoMode) additionally
        /// honors an applied server verdict.</summary>
        public bool CanWrite => IsUsable && !IsViewer;

        public static EntitlementSnapshot NotFound() => new()
        {
            Status = EntitlementStatus.NotFound,
            FailureCode = ErrorCodes.LicenseNotFound,
            ValidatedAtUtc = DateTime.UtcNow
        };
    }

    /// <summary>One entitlement source. Returns null when the source
    /// is not applicable on this machine (no dongle, no file, no
    /// server configured) so the chain falls through.</summary>
    public interface IEntitlementProvider
    {
        string Name { get; }
        Task<EntitlementSnapshot?> TryLoadAsync(CancellationToken ct = default);
    }

    // ========== VendorKey.cs ==========
    /// <summary>
    /// The vendor's license-signing public key. Resolution follows the
    /// codebase's support-grant precedent: the SHOUTECH_LICENSE_PUBLIC_KEY
    /// environment override (PEM) wins — support and tests use it — and
    /// the release-embedded key is the production trust root. The
    /// matching private key is the vendor's offline issuance key; it
    /// never ships and is never read by this program.
    /// </summary>
    public static class VendorKey
    {
        public const string EnvOverrideName = "SHOUTECH_LICENSE_PUBLIC_KEY";

        // RSA-4096 release verification key (public half only).
        // Custody: the matching private key is vendor-held offline
        // material (generated with tools/LicenseIssuer genkey); it
        // is never in this repository and never ships. This embedded
        // key was rotated once during development (2026-11-24)
        // before any license had been issued against the first pair,
        // so no issued license is affected; see
        // docs/LICENSING_BUILD.md.
        public const string ReleasePublicKeyPem = """
            -----BEGIN PUBLIC KEY-----
            MIICIjANBgkqhkiG9w0BAQEFAAOCAg8AMIICCgKCAgEAygrWsQfjPh/wJgE0V6bF
            xHIT4x4kzmQ4WOdI5Rqnl6W28/f2x9p0qJe+BCB0exFzaT3q+I9JcdAAYEtnF0Aq
            Kdh6creSjGjxNphnhJDiPBFShSGSQmmBNi+qGIlnozzRCBmOfAi7I/cIIdxLNZ3s
            u1V8kBBxCCNLIe2mtRHuPsCVvnkDwU9+QDbm311q0J88HJu2JKezE8AeZE1wAeCO
            LkaOVN4zCCmfKDdgqrHoCLPtI3Dvd6i2ssaJBovneTiLAejIj5wO3RQLpPv6FqsE
            yYZMdWnafqbnlaLMwy6KAi6se7PJ6EGRVNK/wPFt+clqwyp0AanJdCqCOQkCOEJU
            0erhV5VEK1Ckt+c1+BbzayzCsD6eMrGvP9aeAUORqYkT7zyBvHA39evZ7HEHYxI5
            BGwEb3lawwrV3iywgGZOIctTe+OhKB+2XWL5sMXZia5/S7NrOn+ayh2xhKaBYrBE
            XFcjpsEo7rc89ZLeSH+H3ZrC2WaUTTTWCHy8753AVEWOZGIy8KxBf29jyP4hfQtN
            hjgHifoC9SCmOBaqPh7hilFW8drWfCD/dCBDHJZiTHVh2+xD89psDFVQlW+cBiPp
            rV5fwSRpepbmmWaciYo6mnJGPisuu8ihzedQFi59EZO99EhmNmbkBktOsIiwta/2
            Yajroc65qRmwP74AS6tOm58CAwEAAQ==
            -----END PUBLIC KEY-----
            """;

        public static string ResolvePublicKeyPem()
        {
            var fromEnv = Environment.GetEnvironmentVariable(EnvOverrideName);
            return string.IsNullOrWhiteSpace(fromEnv) ? ReleasePublicKeyPem : fromEnv.Trim();
        }

        /// <summary>RSA-PSS-SHA256 verification — the same scheme the
        /// STEI packages and support grants already use.</summary>
        public static bool Verify(byte[] data, byte[] signature)
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(ResolvePublicKeyPem());
            return rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        }
    }

    // ========== LicensePayload.cs ==========
    /// <summary>The signed license payload (design §4.2.2). The
    /// payload JSON is signed verbatim — the envelope carries the
    /// exact string that was signed, so verification never depends on
    /// re-serialization.</summary>
    public sealed class LicensePayload
    {
        public string LicenseId { get; set; } = string.Empty;
        public string Customer { get; set; } = string.Empty;
        public string Edition { get; set; } = string.Empty;
        public List<string> Modules { get; set; } = new();
        public int? MaxUsers { get; set; }
        public int? MaxCompanies { get; set; }
        public int? MaxDevices { get; set; }
        public string? DongleSerial { get; set; }
        /// <summary>Component type → component hash (study §4.4).
        /// Null/empty = the license is not fingerprint-bound.</summary>
        public Dictionary<string, string>? Fingerprint { get; set; }
        public DateTime IssuedAtUtc { get; set; }
        public DateTime? ExpiresAtUtc { get; set; }
        public int GraceDays { get; set; }
        public bool IsTrial { get; set; }
        public bool IsViewer { get; set; }
    }

    /// <summary>On-disk / on-wire envelope: the payload JSON exactly
    /// as signed, plus the base64 RSA-PSS signature over its UTF-8
    /// bytes.</summary>
    public sealed class LicenseEnvelope
    {
        public string Payload { get; set; } = string.Empty;
        public string Signature { get; set; } = string.Empty;
    }

    /// <summary>Verifies an envelope and evaluates the payload into a
    /// snapshot. Shared by the file and online providers so a license
    /// means the same thing however it arrived.</summary>
    public static class LicenseEnvelopeEvaluator
    {
        /// <summary>Returns null when the envelope cannot be parsed
        /// at all (caller decides whether that is a fall-through or a
        /// signature-invalid snapshot); a parsed-but-unverifiable or
        /// mismatched envelope yields a non-granting snapshot.</summary>
        public static EntitlementSnapshot? Evaluate(
            string envelopeJson,
            EntitlementSource source,
            DeviceFingerprint fingerprint,
            string? dongleSerialEvidence,
            bool clockSuspicious,
            DateTime nowUtc)
        {
            LicenseEnvelope? envelope;
            try
            {
                envelope = JsonSerializer.Deserialize<LicenseEnvelope>(envelopeJson, JsonDefs.AppSetngRead);
            }
            catch (JsonException)
            {
                return null;
            }

            if (envelope is null || string.IsNullOrWhiteSpace(envelope.Payload) || string.IsNullOrWhiteSpace(envelope.Signature))
                return null;

            byte[] signature;
            try
            {
                signature = Convert.FromBase64String(envelope.Signature);
            }
            catch (FormatException)
            {
                return Invalid(source, nowUtc);
            }

            bool verified;
            try
            {
                verified = VendorKey.Verify(Encoding.UTF8.GetBytes(envelope.Payload), signature);
            }
            catch (Exception ex)
            {
                AppHost.Errors.Report(ErrorCodes.LicenseSignatureInvalid, ex, "Entitlements.Verify");
                return Invalid(source, nowUtc);
            }

            if (!verified)
                return Invalid(source, nowUtc);

            LicensePayload? payload;
            try
            {
                payload = JsonSerializer.Deserialize<LicensePayload>(envelope.Payload, JsonDefs.AppSetngRead);
            }
            catch (JsonException)
            {
                return Invalid(source, nowUtc);
            }

            if (payload is null)
                return Invalid(source, nowUtc);

            return EvaluatePayload(payload, source, fingerprint, dongleSerialEvidence, clockSuspicious, nowUtc);
        }

        private static EntitlementSnapshot Invalid(EntitlementSource source, DateTime nowUtc) => new()
        {
            Status = EntitlementStatus.SignatureInvalid,
            Source = source,
            FailureCode = ErrorCodes.LicenseSignatureInvalid,
            ValidatedAtUtc = nowUtc
        };

        public static EntitlementSnapshot EvaluatePayload(
            LicensePayload payload,
            EntitlementSource source,
            DeviceFingerprint fingerprint,
            string? dongleSerialEvidence,
            bool clockSuspicious,
            DateTime nowUtc)
        {
            var snapshot = new EntitlementSnapshot
            {
                Status = EntitlementStatus.Valid,
                Edition = payload.Edition,
                IsTrial = payload.IsTrial,
                IsViewer = payload.IsViewer,
                Modules = new HashSet<string>(payload.Modules ?? new List<string>(), StringComparer.OrdinalIgnoreCase),
                LicenseId = payload.LicenseId,
                CustomerName = payload.Customer,
                DongleSerial = payload.DongleSerial,
                MaxUsers = payload.MaxUsers,
                MaxCompanies = payload.MaxCompanies,
                ExpiresAt = payload.ExpiresAtUtc,
                GraceEndsAt = payload.ExpiresAtUtc is { } expiry ? expiry.AddDays(Math.Max(0, payload.GraceDays)) : null,
                Source = source,
                ValidatedAtUtc = nowUtc
            };

            // Failure priority: dongle binding → fingerprint binding →
            // clock suspicion → term. A foreign or tampered license
            // reports its binding failure, not its dates.
            if (!string.IsNullOrWhiteSpace(payload.DongleSerial))
            {
                if (string.IsNullOrWhiteSpace(dongleSerialEvidence))
                {
                    return snapshot with
                    {
                        Status = EntitlementStatus.DongleMissing,
                        FailureCode = ErrorCodes.DongleNotFound
                    };
                }

                if (!string.Equals(dongleSerialEvidence.Trim(), payload.DongleSerial.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return snapshot with
                    {
                        Status = EntitlementStatus.BindingMismatch,
                        FailureCode = ErrorCodes.LicenseBindingMismatch
                    };
                }
            }

            if (payload.Fingerprint is { Count: > 0 })
            {
                var match = FingerprintMatcher.Evaluate(payload.Fingerprint, fingerprint.Components);
                if (match.Kind == FpMatchKind.Mismatch)
                {
                    return snapshot with
                    {
                        Status = EntitlementStatus.BindingMismatch,
                        FailureCode = ErrorCodes.LicenseBindingMismatch
                    };
                }
            }

            if (clockSuspicious)
            {
                return snapshot with
                {
                    Status = EntitlementStatus.BindingMismatch,
                    FailureCode = ErrorCodes.LicenseBindingMismatch
                };
            }

            if (snapshot.ExpiresAt is { } expiresAt)
            {
                if (nowUtc > (snapshot.GraceEndsAt ?? expiresAt))
                {
                    return snapshot with
                    {
                        Status = EntitlementStatus.Expired,
                        FailureCode = ErrorCodes.LicenseExpired
                    };
                }

                if (nowUtc > expiresAt)
                {
                    return snapshot with
                    {
                        Status = EntitlementStatus.GracePeriod,
                        FailureCode = ErrorCodes.LicenseGracePeriod
                    };
                }
            }

            return snapshot;
        }
    }

    // ========== DongleProbe.cs ==========
    /// <summary>
    /// Shared dongle evidence for the provider chain. Native detection
    /// runs through SecEngInterop; while SecEng is not shipped the
    /// probe honestly reports "native unavailable". The serial
    /// evidence used for payload binding is, until HID productization
    /// (D5 follow-on), the serial recorded in configuration at sale
    /// time — the design §4.2.2 interim binding. When the native
    /// provider is productized it fills DetectedSerial and takes
    /// precedence automatically.
    /// </summary>
    public sealed class DongleProbe
    {
        private bool _probed;

        public bool NativeAvailable { get; private set; }
        public bool DonglePresent { get; private set; }
        public string? DetectedSerial { get; private set; }

        public void Probe()
        {
            if (_probed)
                return;
            _probed = true;

            try
            {
                DonglePresent = SecEngInterop.ST_IsDonglePresent();
                NativeAvailable = true;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                // SecEng.dll is not part of the build yet (design
                // §1.1): absence is a fact to record, not an error.
                NativeAvailable = false;
                DonglePresent = false;
            }
            catch (Exception ex)
            {
                NativeAvailable = false;
                DonglePresent = false;
                AppHost.Errors.Report(ErrorCodes.LicenseCheckFailed, ex, "Entitlements.DongleProbe");
            }
        }

        /// <summary>The serial a payload's dongle binding is checked
        /// against: hardware-detected when available, else the
        /// configured serial, else null (no evidence).</summary>
        public string? SerialEvidence
        {
            get
            {
                Probe();
                if (!string.IsNullOrWhiteSpace(DetectedSerial))
                    return DetectedSerial;
                var configured = AppCfgSvc.Current.Licensing?.DongleInfo?.SerialNumber;
                return string.IsNullOrWhiteSpace(configured) ? null : configured.Trim();
            }
        }
    }

    // ========== LastSeenStore.cs ==========
    /// <summary>
    /// Clock-rollback guard (design §4.3): the last UTC instant this
    /// installation validated at, kept across runs. The value is a
    /// timestamp, not a secret; it is DPAPI-protected on Windows via
    /// the codebase pattern, and stored plain where DPAPI does not
    /// exist (non-Windows test hosts) — integrity there rests on the
    /// same file-access boundary as the license file itself.
    /// </summary>
    public sealed class LastSeenStore
    {
        private readonly string _path;

        public LastSeenStore(string? path = null)
            => _path = path ?? Path.Combine(AppPaths.Cfg, "lastseen.dat");

        public DateTime? Read()
        {
            try
            {
                if (!File.Exists(_path))
                    return null;
                var raw = File.ReadAllBytes(_path);
                var text = TryUnprotect(raw) ?? Encoding.UTF8.GetString(raw);
                return DateTime.TryParse(text.Trim(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var parsed)
                    ? parsed
                    : null;
            }
            catch (Exception ex)
            {
                AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, ex, "Entitlements.LastSeenStore.Read");
                return null;
            }
        }

        public void Write(DateTime utc)
        {
            try
            {
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                var bytes = Encoding.UTF8.GetBytes(utc.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
                File.WriteAllBytes(_path, TryProtect(bytes) ?? bytes);
            }
            catch (Exception ex)
            {
                AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, ex, "Entitlements.LastSeenStore.Write");
            }
        }

        /// <summary>True when the system clock is materially earlier
        /// than the last validated instant (10-minute tolerance for
        /// ordinary clock corrections).</summary>
        public bool IsClockSuspicious(DateTime nowUtc)
        {
            var last = Read();
            return last is { } seen && nowUtc < seen.AddMinutes(-10);
        }

        private static byte[]? TryProtect(byte[] data)
        {
            try
            {
                return System.Security.Cryptography.ProtectedData.Protect(
                    data, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            }
            catch (Exception ex) when (ex is PlatformNotSupportedException or CryptographicException)
            {
                return null;
            }
        }

        private static string? TryUnprotect(byte[] data)
        {
            try
            {
                var plain = System.Security.Cryptography.ProtectedData.Unprotect(
                    data, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch (Exception ex) when (ex is PlatformNotSupportedException or CryptographicException or FormatException)
            {
                return null;
            }
        }
    }

    // ========== DongleProvider.cs ==========
    /// <summary>
    /// The owner's target state (design §4.2.1). It performs the
    /// native detection probe so dongle evidence is shared with the
    /// chain, but as an entitlement SOURCE it reports not-applicable
    /// until the native stubs are productized: prot.cpp's
    /// validateLicense is an explicit stub that accepts any blob, and
    /// granting modules from a stub would be a fake. When SecEng
    /// ships with real signed-blob verification and HID I/O (D5
    /// follow-on project), this provider builds its snapshot from
    /// ST_ValidateSession and takes its place at the head of the
    /// chain with no other code changing.
    /// </summary>
    public sealed class DongleProvider : IEntitlementProvider
    {
        private readonly DongleProbe _probe;
        private readonly ILogServ _log;

        public DongleProvider(DongleProbe probe, ILogServ log)
        {
            _probe = probe;
            _log = log;
        }

        public string Name => "Dongle";

        public Task<EntitlementSnapshot?> TryLoadAsync(CancellationToken ct = default)
        {
            _probe.Probe();
            _log.LogInfo("Entitlements",
                _probe.NativeAvailable
                    ? $"Dongle provider: native engine present, dongle {(_probe.DonglePresent ? "detected" : "absent")}; entitlement from dongle awaits native productization — falling through."
                    : "Dongle provider: SecEng native engine not shipped — not applicable, falling through.");
            return Task.FromResult<EntitlementSnapshot?>(null);
        }
    }

    // ========== LicenseFileProvider.cs ==========
    /// <summary>
    /// The shippable v1 (design §4.2.2): reads the signed license
    /// file (cfg/license.dat next to the executable, the path the
    /// retired LicServ already probed). A file that exists but does
    /// not verify yields a SignatureInvalid snapshot — never a silent
    /// fall-through, never a grant.
    /// </summary>
    public sealed class LicenseFileProvider : IEntitlementProvider
    {
        private readonly FingerprintSvc _fingerprint;
        private readonly DongleProbe _probe;
        private readonly LastSeenStore _lastSeen;
        private readonly ILogServ _log;
        private readonly IReadOnlyList<string> _candidatePaths;

        public LicenseFileProvider(
            FingerprintSvc fingerprint,
            DongleProbe probe,
            LastSeenStore lastSeen,
            ILogServ log,
            IReadOnlyList<string>? candidatePaths = null)
        {
            _fingerprint = fingerprint;
            _probe = probe;
            _lastSeen = lastSeen;
            _log = log;
            _candidatePaths = candidatePaths ?? DefaultCandidatePaths();
        }

        public string Name => "LicenseFile";

        public static IReadOnlyList<string> DefaultCandidatePaths() => new[]
        {
            Path.Combine(AppContext.BaseDirectory, "cfg", "license.dat"),
            Path.Combine(AppContext.BaseDirectory, "license.dat")
        };

        /// <summary>Where an imported license file is installed.</summary>
        public static string PrimaryPath => DefaultCandidatePaths()[0];

        public async Task<EntitlementSnapshot?> TryLoadAsync(CancellationToken ct = default)
        {
            var path = _candidatePaths.FirstOrDefault(File.Exists);
            if (path is null)
                return null;

            string json;
            try
            {
                json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppHost.Errors.Report(ErrorCodes.LicenseCheckFailed, ex, "Entitlements.LicenseFile.Read");
                return new EntitlementSnapshot
                {
                    Status = EntitlementStatus.SignatureInvalid,
                    Source = EntitlementSource.LicenseFile,
                    FailureCode = ErrorCodes.LicenseSignatureInvalid,
                    ValidatedAtUtc = DateTime.UtcNow
                };
            }

            return Evaluate(json);
        }

        /// <summary>Evaluates license-file content without touching
        /// the disk — the import path uses it to refuse installing a
        /// file that does not verify.</summary>
        public EntitlementSnapshot? Evaluate(string envelopeJson)
        {
            var now = DateTime.UtcNow;
            var clockSuspicious = _lastSeen.IsClockSuspicious(now);
            var snapshot = LicenseEnvelopeEvaluator.Evaluate(
                envelopeJson,
                EntitlementSource.LicenseFile,
                _fingerprint.Collect(),
                _probe.SerialEvidence,
                clockSuspicious,
                now);

            if (snapshot is null)
            {
                // A present but unparseable license file is a
                // signature failure, not an absence.
                return new EntitlementSnapshot
                {
                    Status = EntitlementStatus.SignatureInvalid,
                    Source = EntitlementSource.LicenseFile,
                    FailureCode = ErrorCodes.LicenseSignatureInvalid,
                    ValidatedAtUtc = now
                };
            }

            if (snapshot.IsUsable)
                _lastSeen.Write(now);

            if (clockSuspicious && snapshot.IsUsable)
                _log.LogWarning("Entitlements", "Clock rollback suspected at license validation (LIC-8006 evaluation applied).");

            return snapshot;
        }
    }

    // ========== OnlineProvider.cs ==========
    /// <summary>
    /// Online activation (design §4.2.3): extends the license
    /// validation exchange — the server response may carry a signed
    /// <c>entitlements</c> envelope, which is verified exactly like a
    /// license file. An unsigned response can refresh status in
    /// LicenSvc, but it NEVER grants entitlements here. Inert until
    /// an activation server is configured.
    /// </summary>
    public sealed class OnlineProvider : IEntitlementProvider
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

        private readonly IDevInfoSv _deviceInfo;
        private readonly FingerprintSvc _fingerprint;
        private readonly DongleProbe _probe;
        private readonly ILogServ _log;
        private readonly Func<string?> _licenseKey;
        private readonly Func<string?> _baseUrl;
        private readonly HttpClient _http;

        public OnlineProvider(
            IDevInfoSv deviceInfo,
            FingerprintSvc fingerprint,
            DongleProbe probe,
            ILogServ log,
            Func<string?>? licenseKey = null,
            Func<string?>? baseUrl = null,
            HttpClient? http = null)
        {
            _deviceInfo = deviceInfo;
            _fingerprint = fingerprint;
            _probe = probe;
            _log = log;
            _licenseKey = licenseKey ?? DefaultLicenseKey;
            _baseUrl = baseUrl ?? (() => AppCfgSvc.Current.Licensing?.Activation?.ActivationServer);
            _http = http ?? Http;
        }

        public string Name => "Online";

        private static string? DefaultLicenseKey()
        {
            var fromService = AppHost.License?.Current?.LicenseKey;
            if (!string.IsNullOrWhiteSpace(fromService))
                return fromService;
            var fromConfig = AppCfgSvc.Current.Licensing?.DongleInfo?.SerialNumber;
            return string.IsNullOrWhiteSpace(fromConfig) ? null : fromConfig;
        }

        public async Task<EntitlementSnapshot?> TryLoadAsync(CancellationToken ct = default)
        {
            var baseUrl = _baseUrl();
            var key = _licenseKey();
            if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(key))
                return null;

            var endpoint = AppCfgSvc.Current.OnlineServices?.LicenseValidateEndpoint ?? "/license/validate";
            var url = $"{baseUrl.TrimEnd('/')}/{endpoint.TrimStart('/')}";
            var device = _deviceInfo.Current;
            var payload = JsonSerializer.Serialize(new
            {
                licenseKey = key,
                hardwareId = device.HardwareId,
                deviceId = device.DeviceId,
                appVersion = device.AppVersion
            }, JsonDefs.Indented);

            try
            {
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var response = await _http.PostAsync(url, content, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    _log.LogInfo("Entitlements", $"Online provider: validation returned {(int)response.StatusCode} — falling through.");
                    return null;
                }

                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("entitlements", out var claim) || claim.ValueKind != JsonValueKind.Object)
                {
                    _log.LogInfo("Entitlements", "Online provider: response carried no signed entitlements claim — not granting.");
                    return null;
                }

                var snapshot = LicenseEnvelopeEvaluator.Evaluate(
                    claim.GetRawText(),
                    EntitlementSource.Online,
                    _fingerprint.Collect(),
                    _probe.SerialEvidence,
                    clockSuspicious: false,
                    DateTime.UtcNow);

                if (snapshot is null || snapshot.Status == EntitlementStatus.SignatureInvalid)
                {
                    AppHost.Errors.Report(
                        ErrorCodes.LicenseSignatureInvalid,
                        new InvalidOperationException("Online entitlements claim failed signature verification."),
                        "Entitlements.OnlineProvider");
                    return null;
                }

                return snapshot;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                _log.LogInfo("Entitlements", $"Online provider unavailable ({ex.GetType().Name}) — falling through.");
                return null;
            }
            catch (Exception ex)
            {
                AppHost.Errors.Report(ErrorCodes.LicenseCheckFailed, ex, "Entitlements.OnlineProvider");
                return null;
            }
        }
    }

    // ========== DevModeProvider.cs ==========
    /// <summary>
    /// The formalized, fenced LOCAL-DEV grant (design §4.2.4). Active
    /// ONLY in Debug builds AND with Licensing.DevMode = true in
    /// config (default false, absent from the shipped config's
    /// semantics — the shipped file carries it explicitly false).
    /// A Release build compiles the grant away entirely: editing JSON
    /// in the field cannot switch it on. Every activation is logged
    /// with a coded warning and the snapshot is stamped Source =
    /// DevMode so the shell can show a persistent DEV marker.
    /// </summary>
    public sealed class DevModeProvider : IEntitlementProvider
    {
        private readonly ILogServ _log;

        public DevModeProvider(ILogServ log) => _log = log;

        public string Name => "DevMode";

        public static IReadOnlySet<string> AllModules { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ModKey.Acc, ModKey.Inv, ModKey.Sales, ModKey.Purch,
            ModKey.Crm, ModKey.Hr, ModKey.Mfg, ModKey.Pos,
            ModKey.Ai, ModKey.AdvRpt, ModKey.MultiCur, ModKey.WhiteLabel
        };

        public Task<EntitlementSnapshot?> TryLoadAsync(CancellationToken ct = default)
        {
            var requested = AppCfgSvc.Current.Licensing?.DevMode == true;
#if DEBUG
            if (requested)
            {
                _log.LogWarning("Entitlements",
                    $"[{ErrorCodes.LicenseGracePeriod}] DevMode license grant ACTIVE (Debug build, config-fenced). This grant is impossible in Release builds.");
                return Task.FromResult<EntitlementSnapshot?>(new EntitlementSnapshot
                {
                    Status = EntitlementStatus.Valid,
                    Edition = "Developer",
                    Modules = AllModules,
                    LicenseId = "LOCAL-DEV",
                    CustomerName = "Development",
                    Source = EntitlementSource.DevMode,
                    ValidatedAtUtc = DateTime.UtcNow
                });
            }
#else
            if (requested)
            {
                // The fence, made visible: a field-edited config that
                // asks for a dev grant on a Release build is refused
                // and the refusal is on the record.
                _log.LogWarning("Entitlements",
                    "Licensing.DevMode is true in configuration but is IGNORED in Release builds (fenced grant).");
            }
#endif
            return Task.FromResult<EntitlementSnapshot?>(null);
        }
    }

    // ========== EntitlementService.cs ==========
    public interface IEntitlementService
    {
        EntitlementSnapshot Current { get; }
        event EventHandler? EntitlementsChanged;

        Task<EntitlementSnapshot> InitializeAsync(CancellationToken ct = default);
        Task<EntitlementSnapshot> RefreshAsync(CancellationToken ct = default);

        /// <summary>True when the module may be used at all (core and
        /// platform modules are always entitled; add-ons need the
        /// snapshot to carry them while usable).</summary>
        bool IsEntitled(string moduleKey);

        /// <summary>True when state-changing work is allowed: the
        /// license layer grants writes AND no demo verdict applies.</summary>
        bool CanWrite { get; }

        /// <summary>The demo / view-only state of study §7.4, derived
        /// from signed artifacts only (license snapshot + applied
        /// server verdict) — never from an editable flag.</summary>
        bool IsDemoMode { get; }

        /// <summary>Reason category of the current demo state
        /// ("local-binding", "term", "fingerprint", …) for the
        /// banner; null when not in demo mode.</summary>
        string? DemoReasonCategory { get; }

        void EnsureModule(string moduleKey);
        void EnsureCanWrite();
        void EnsureModuleWrite(string moduleKey);

        /// <summary>Validates and installs a signed license file,
        /// then refreshes. Refuses (LIC-8007) a file that does not
        /// verify — the previous license is left untouched.</summary>
        Task<EntitlementSnapshot> ImportLicenseFileAsync(string sourcePath, CancellationToken ct = default);
    }

    public sealed class EntitlementService : IEntitlementService
    {
        private readonly IReadOnlyList<IEntitlementProvider> _providers;
        private readonly LicenseFileProvider _fileProvider;
        private readonly ILogServ _log;
        private EntitlementSnapshot _current = EntitlementSnapshot.NotFound();

        // Layer-2 verdict overlay (telemetry study §7): set only by
        // the check-in client after signature verification + seq
        // anti-replay. A confirmed mismatch forces demo mode even
        // under an otherwise-valid local license; a CLEARED verdict
        // (or EXEMPT) removes the overlay.
        private string? _verdictDemoReason;

        public EntitlementService(
            IReadOnlyList<IEntitlementProvider> providers,
            LicenseFileProvider fileProvider,
            ILogServ log)
        {
            _providers = providers;
            _fileProvider = fileProvider;
            _log = log;
        }

        public EntitlementSnapshot Current => _current;

        public event EventHandler? EntitlementsChanged;

        public Task<EntitlementSnapshot> InitializeAsync(CancellationToken ct = default) => RefreshAsync(ct);

        public async Task<EntitlementSnapshot> RefreshAsync(CancellationToken ct = default)
        {
            EntitlementSnapshot? snapshot = null;
            foreach (var provider in _providers)
            {
                try
                {
                    snapshot = await provider.TryLoadAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    AppHost.Errors.Report(ErrorCodes.LicenseCheckFailed, ex, $"Entitlements.Provider.{provider.Name}");
                    snapshot = null;
                }

                if (snapshot is not null)
                    break;

                _log.LogInfo("Entitlements", $"Provider {provider.Name}: not applicable — falling through.");
                Audit(SecurityAuditLog.LicenseProviderFallthrough, $"provider={provider.Name}");
            }

            snapshot ??= EntitlementSnapshot.NotFound();

            var changed = !SnapshotEquals(_current, snapshot) || _verdictDemoReason is not null;
            _current = snapshot;

            _log.LogInfo("Entitlements",
                $"Snapshot: status={snapshot.Status} source={snapshot.Source} edition={snapshot.Edition} modules=[{string.Join(",", snapshot.Modules.OrderBy(m => m, StringComparer.Ordinal))}] failure={snapshot.FailureCode ?? "-"}");
            Audit(
                snapshot.IsUsable ? SecurityAuditLog.LicenseValidated : SecurityAuditLog.LicenseBlocked,
                $"status={snapshot.Status};source={snapshot.Source};license={snapshot.LicenseId ?? "-"};code={snapshot.FailureCode ?? "-"}");

            if (changed)
                EntitlementsChanged?.Invoke(this, EventArgs.Empty);

            return snapshot;
        }

        public bool IsEntitled(string moduleKey)
        {
            if (string.IsNullOrWhiteSpace(moduleKey))
                return false;
            if (!ModuleMap.IsGatedModule(moduleKey))
                return true; // CORE / platform surface — §2.2
            return _current.IsUsable && _current.Modules.Contains(moduleKey);
        }

        public bool CanWrite => _current.CanWrite && _verdictDemoReason is null;

        public bool IsDemoMode => !CanWrite;

        public string? DemoReasonCategory
        {
            get
            {
                if (!IsDemoMode)
                    return null;
                if (_verdictDemoReason is not null)
                    return _verdictDemoReason;
                return _current.Status switch
                {
                    EntitlementStatus.BindingMismatch => "local-binding",
                    EntitlementStatus.SignatureInvalid => "local-binding",
                    EntitlementStatus.DongleMissing => "local-binding",
                    EntitlementStatus.Expired => "term",
                    EntitlementStatus.NotFound => "license-unknown",
                    _ => "term"
                };
            }
        }

        public void EnsureModule(string moduleKey)
        {
            if (IsEntitled(moduleKey))
                return;

            var code = _current.FailureCode ?? ErrorCodes.ModuleNotLicensed;
            if (_current.IsUsable)
                code = ErrorCodes.ModuleNotLicensed;

            Audit(SecurityAuditLog.LicenseBlocked, $"module={moduleKey};code={code}");
            throw new AppError(code, ResolveEnglishMessage(code, moduleKey), moduleKey);
        }

        public void EnsureCanWrite()
        {
            if (CanWrite)
                return;

            var code = _verdictDemoReason is not null
                ? ErrorCodes.LicenseReadOnly
                : _current.FailureCode ?? ErrorCodes.LicenseReadOnly;
            if (_current.IsUsable && _current.IsViewer)
                code = ErrorCodes.LicenseReadOnly;

            Audit(SecurityAuditLog.LicenseBlocked, $"write;code={code};reason={DemoReasonCategory ?? "-"}");
            throw new AppError(code, ResolveEnglishMessage(code, null));
        }

        public void EnsureModuleWrite(string moduleKey)
        {
            EnsureCanWrite();
            EnsureModule(moduleKey);
        }

        public async Task<EntitlementSnapshot> ImportLicenseFileAsync(string sourcePath, CancellationToken ct = default)
        {
            string json;
            try
            {
                json = await File.ReadAllTextAsync(sourcePath, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppHost.Errors.Report(ErrorCodes.LicenseCheckFailed, ex, "Entitlements.ImportLicenseFile.Read");
                throw new AppError(ErrorCodes.LicenseCheckFailed, "The license could not be verified.", ex);
            }

            var probe = _fileProvider.Evaluate(json);
            if (probe is null || probe.Status == EntitlementStatus.SignatureInvalid)
            {
                throw new AppError(
                    ErrorCodes.LicenseSignatureInvalid,
                    "The license file signature is invalid.");
            }

            var target = LicenseFileProvider.PrimaryPath;
            try
            {
                var dir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                await File.WriteAllTextAsync(target, json, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppHost.Errors.Report(ErrorCodes.LicenseCheckFailed, ex, "Entitlements.ImportLicenseFile.Write");
                throw new AppError(ErrorCodes.LicenseCheckFailed, "The license could not be verified.", ex);
            }

            Audit(SecurityAuditLog.LicenseImported, $"license={probe.LicenseId ?? "-"};status={probe.Status}");
            return await RefreshAsync(ct).ConfigureAwait(false);
        }

        /// <summary>Layer-2 seam (telemetry study §7.2/§7.6): the
        /// check-in client calls this with the reason category of a
        /// verified MISMATCH-CONFIRMED verdict, or null for OK /
        /// CLEARED / EXEMPT. The overlay lives only in memory and in
        /// the signed verdict cache the client re-applies at login —
        /// it is never an editable flag.</summary>
        public void ApplyVerdictOverlay(string? demoReasonCategory)
        {
            if (string.Equals(_verdictDemoReason, demoReasonCategory, StringComparison.Ordinal))
                return;
            _verdictDemoReason = demoReasonCategory;
            Audit(
                demoReasonCategory is null ? SecurityAuditLog.DemoModeCleared : SecurityAuditLog.DemoModeEntered,
                $"reason={demoReasonCategory ?? "-"}");
            EntitlementsChanged?.Invoke(this, EventArgs.Empty);
        }

        private static bool SnapshotEquals(EntitlementSnapshot a, EntitlementSnapshot b)
            => a.Status == b.Status
               && a.Source == b.Source
               && string.Equals(a.LicenseId, b.LicenseId, StringComparison.Ordinal)
               && a.Modules.SetEquals(b.Modules);

        private static string ResolveEnglishMessage(string code, string? moduleKey) => code switch
        {
            ErrorCodes.ModuleNotLicensed => $"Module '{moduleKey}' is not included in this license.",
            ErrorCodes.LicenseExpired => "The license has expired and the grace period has ended.",
            ErrorCodes.LicenseNotFound => "No license was found for this installation.",
            ErrorCodes.DongleNotFound => "The license is bound to a dongle that was not detected.",
            ErrorCodes.LicenseBindingMismatch => "The license does not match this device.",
            ErrorCodes.LicenseSignatureInvalid => "The license file signature is invalid.",
            _ => "This operation is not allowed while the program is in view-only mode."
        };

        private static void Audit(string eventType, string details)
        {
            try
            {
                SecurityAuditLog.Write(
                    AppState.CurrentCompanyCode,
                    AppState.CurrentUser?.Id,
                    AppState.CurrentUser?.Username ?? "system",
                    eventType,
                    details);
            }
            catch (Exception ex)
            {
                // Audit is best-effort by design (SecurityAuditLog's
                // own contract); a failure here must never change a
                // licensing outcome — but it is reported, not silent.
                AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, ex, "Entitlements.Audit");
            }
        }

        /// <summary>The default chain, in the config's provider
        /// order. Unknown names are ignored with a log line; the
        /// canonical order Dongle → LicenseFile → Online → DevMode is
        /// the fallback when config names nothing usable.</summary>
        public static EntitlementService CreateDefault(ILogServ log, IDevInfoSv deviceInfo)
        {
            var fingerprint = new FingerprintSvc();
            var probe = new DongleProbe();
            var fileProvider = new LicenseFileProvider(fingerprint, probe, new LastSeenStore(), log);

            var byName = new Dictionary<string, IEntitlementProvider>(StringComparer.OrdinalIgnoreCase)
            {
                ["Dongle"] = new DongleProvider(probe, log),
                ["LicenseFile"] = fileProvider,
                ["Online"] = new OnlineProvider(deviceInfo, fingerprint, probe, log),
                ["DevMode"] = new DevModeProvider(log)
            };

            var ordered = new List<IEntitlementProvider>();
            foreach (var name in AppCfgSvc.Current.Licensing?.Providers ?? new List<string>())
            {
                if (byName.TryGetValue(name.Trim(), out var provider) && !ordered.Contains(provider))
                    ordered.Add(provider);
            }

            if (ordered.Count == 0)
                ordered.AddRange(new[] { byName["Dongle"], byName["LicenseFile"], byName["Online"], byName["DevMode"] });

            return new EntitlementService(ordered, fileProvider, log);
        }
    }
}
