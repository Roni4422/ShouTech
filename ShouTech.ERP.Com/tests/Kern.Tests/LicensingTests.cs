using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ShouTech.App.Configuration;
using ShouTech.App.Services;
using ShouTech.Domain;
using Xunit;

namespace Kern.Tests;

/// <summary>
/// Licensing &amp; protection build (docs/LICENSING_BUILD.md). Pins,
/// against the REAL injectable seams (no host composition):
///   * the fingerprint matcher: identical → Match, one substantive
///     component changed → Drift, two or more (or MachineGuid +
///     BaseBoardSerial) → Mismatch, nothing comparable → Mismatch
///     (fail-closed), placeholder serials treated as absent;
///   * the signed-license evaluation: valid → writable; foreign
///     device → BindingMismatch + demo + LIC-8006 at the gate;
///     tampered payload → SignatureInvalid; expired past grace →
///     Expired; expired inside grace → GracePeriod; module lapse →
///     LIC-8002 for that module while core writes stay allowed;
///   * the provider chain: first non-null snapshot wins, null
///     providers fall through;
///   * the verdict store: accept → replay rejected → tamper
///     rejected → foreign rejected; CLEARED + overlay clears demo
///     (reactivation);
///   * DevMode fencing at the config layer: default off, and the
///     provider grants nothing while the flag is off.
/// A TEST vendor keypair is generated for the whole class and the
/// public half is published through the documented env override
/// (VendorKey reads it on every call — no caching).
/// Execution is Windows-owed like the rest of this suite; the same
/// legs run headlessly today in tools/LicHarness (36/36).
/// </summary>
public sealed class LicensingTests : IDisposable
{
    private static readonly RSA Vendor = CreateVendor();
    private static readonly string VendorPrivatePem = Vendor.ExportPkcs8PrivateKeyPem();
    private static readonly string VendorPublicPem = Vendor.ExportSubjectPublicKeyInfoPem();

    private readonly string _root;
    private readonly HarnessLog _log = new();

    private static RSA CreateVendor()
    {
        var rsa = RSA.Create(4096);
        Environment.SetEnvironmentVariable(VendorKey.EnvOverrideName, rsa.ExportSubjectPublicKeyInfoPem());
        return rsa;
    }

    public LicensingTests()
    {
        Environment.SetEnvironmentVariable(VendorKey.EnvOverrideName, VendorPublicPem);
        _root = Path.Combine(Path.GetTempPath(), "lic-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ---------------------------------------------------------------
    // Devices
    // ---------------------------------------------------------------
    private static readonly Dictionary<string, string> RawA = new()
    {
        [FpComponent.MachineGuid] = "aaaaaaaa-1111-4222-8333-444444444444",
        [FpComponent.BaseBoardSerial] = "BOARD-A-001",
        [FpComponent.BiosSerial] = "BIOS-A-001",
        [FpComponent.DriveVolumeSerial] = "VOL-A-001",
        [FpComponent.NicMac] = "00-11-22-33-44-55",
        [FpComponent.MachineName] = "WS-A"
    };

    private static FingerprintSvc Fp(IReadOnlyDictionary<string, string> raw)
        => new(new RecordedComponentSource(new Dictionary<string, string>(raw)));

    private static Dictionary<string, string> Hashes(IReadOnlyDictionary<string, string> raw)
        => new(Fp(raw).Collect().Components);

    // ---------------------------------------------------------------
    // Fingerprint matcher
    // ---------------------------------------------------------------
    [Fact]
    public void Matcher_IdenticalSets_Match()
    {
        var bound = Hashes(RawA);
        var result = FingerprintMatcher.Evaluate(bound, bound);
        Assert.Equal(FpMatchKind.Match, result.Kind);
        Assert.Equal(5, result.ComparableCount);
    }

    [Fact]
    public void Matcher_OneComponentChanged_Drift()
    {
        var current = new Dictionary<string, string>(RawA) { [FpComponent.NicMac] = "00-11-22-33-44-66" };
        var result = FingerprintMatcher.Evaluate(Hashes(RawA), Hashes(current));
        Assert.Equal(FpMatchKind.Drift, result.Kind);
        Assert.Equal(new[] { FpComponent.NicMac }, result.ChangedComponents);
    }

    [Fact]
    public void Matcher_TwoStrongComponentsChanged_Mismatch()
    {
        var current = new Dictionary<string, string>(RawA)
        {
            [FpComponent.MachineGuid] = "bbbbbbbb-5555-4666-8777-888888888888",
            [FpComponent.BaseBoardSerial] = "BOARD-B-999"
        };
        var result = FingerprintMatcher.Evaluate(Hashes(RawA), Hashes(current));
        Assert.Equal(FpMatchKind.Mismatch, result.Kind);
    }

    [Fact]
    public void Matcher_NothingComparable_FailsClosed()
    {
        var current = new Dictionary<string, string> { [FpComponent.MachineName] = "WS-A" };
        var result = FingerprintMatcher.Evaluate(Hashes(RawA), Hashes(current));
        Assert.Equal(FpMatchKind.Mismatch, result.Kind);
        Assert.Equal(0, result.ComparableCount);
    }

    [Fact]
    public void Matcher_PlaceholderSerials_AreAbsent()
    {
        var raw = new Dictionary<string, string>(RawA)
        {
            [FpComponent.BaseBoardSerial] = "To be filled by O.E.M."
        };
        var fp = Fp(raw).Collect();
        Assert.False(fp.Components.ContainsKey(FpComponent.BaseBoardSerial));
        Assert.Equal(4, fp.SubstantiveCount);
    }

    // ---------------------------------------------------------------
    // Signed license evaluation through the real provider chain
    // ---------------------------------------------------------------
    private string IssueLicense(LicensePayload payload)
    {
        var payloadJson = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        using var signer = RSA.Create();
        signer.ImportFromPem(VendorPrivatePem);
        var signature = signer.SignData(
            Encoding.UTF8.GetBytes(payloadJson), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        return JsonSerializer.Serialize(new LicenseEnvelope
        {
            Payload = payloadJson,
            Signature = Convert.ToBase64String(signature)
        });
    }

    private LicensePayload BasePayload() => new()
    {
        LicenseId = "LIC-TEST-1",
        Customer = "Test Customer",
        Edition = "Core",
        Modules = new List<string> { "ACC", "INV", "SALES", "PURCH", "POS" },
        MaxUsers = 10,
        MaxCompanies = 1,
        Fingerprint = Hashes(RawA),
        IssuedAtUtc = DateTime.UtcNow.AddDays(-1),
        ExpiresAtUtc = DateTime.UtcNow.AddYears(1),
        GraceDays = 30
    };

    private EntitlementService BuildService(IReadOnlyDictionary<string, string> raw, string licenseText)
    {
        var dir = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var licensePath = Path.Combine(dir, "license.dat");
        File.WriteAllText(licensePath, licenseText);
        var fileProvider = new LicenseFileProvider(
            Fp(raw), new DongleProbe(), new LastSeenStore(Path.Combine(dir, "lastseen.dat")), _log,
            new[] { licensePath });
        return new EntitlementService(
            new IEntitlementProvider[] { fileProvider }, fileProvider, _log);
    }

    [Fact]
    public async Task License_ValidOnBoundDevice_Writable()
    {
        var service = BuildService(RawA, IssueLicense(BasePayload()));
        var snapshot = await service.RefreshAsync();
        Assert.Equal(EntitlementStatus.Valid, snapshot.Status);
        Assert.True(service.CanWrite);
        Assert.False(service.IsDemoMode);
        Assert.True(service.IsEntitled("POS"));
    }

    [Fact]
    public async Task License_ForeignDevice_BindingMismatchDemo()
    {
        var foreign = new Dictionary<string, string>(RawA)
        {
            [FpComponent.MachineGuid] = "bbbbbbbb-5555-4666-8777-888888888888",
            [FpComponent.BaseBoardSerial] = "BOARD-B-999",
            [FpComponent.BiosSerial] = "BIOS-B-999"
        };
        var service = BuildService(foreign, IssueLicense(BasePayload()));
        var snapshot = await service.RefreshAsync();
        Assert.Equal(EntitlementStatus.BindingMismatch, snapshot.Status);
        Assert.True(service.IsDemoMode);
        var ex = Assert.Throws<AppError>(() => service.EnsureCanWrite());
        Assert.Equal(ErrorCodes.LicenseBindingMismatch, ex.Code);
    }

    [Fact]
    public async Task License_TamperedPayload_SignatureInvalid()
    {
        var envelope = JsonSerializer.Deserialize<LicenseEnvelope>(IssueLicense(BasePayload()))!;
        envelope.Payload = envelope.Payload.Replace("LIC-TEST-1", "LIC-TEST-9");
        var service = BuildService(RawA, JsonSerializer.Serialize(envelope));
        var snapshot = await service.RefreshAsync();
        Assert.Equal(EntitlementStatus.SignatureInvalid, snapshot.Status);
        Assert.True(service.IsDemoMode);
    }

    [Fact]
    public async Task License_ExpiredPastGrace_Expired()
    {
        var payload = BasePayload();
        payload.ExpiresAtUtc = DateTime.UtcNow.AddDays(-60);
        payload.GraceDays = 30;
        var service = BuildService(RawA, IssueLicense(payload));
        var snapshot = await service.RefreshAsync();
        Assert.Equal(EntitlementStatus.Expired, snapshot.Status);
        Assert.False(service.CanWrite);
    }

    [Fact]
    public async Task License_ExpiredInsideGrace_GracePeriodWritable()
    {
        var payload = BasePayload();
        payload.ExpiresAtUtc = DateTime.UtcNow.AddDays(-5);
        payload.GraceDays = 30;
        var service = BuildService(RawA, IssueLicense(payload));
        var snapshot = await service.RefreshAsync();
        Assert.Equal(EntitlementStatus.GracePeriod, snapshot.Status);
        Assert.True(service.CanWrite);
    }

    [Fact]
    public async Task License_ModuleLapse_ModuleViewOnlyCoreWrites()
    {
        var payload = BasePayload();
        payload.Modules = new List<string> { "ACC", "INV" };
        var service = BuildService(RawA, IssueLicense(payload));
        await service.RefreshAsync();
        Assert.False(service.IsEntitled("POS"));
        var ex = Assert.Throws<AppError>(() => service.EnsureModuleWrite("POS"));
        Assert.Equal(ErrorCodes.ModuleNotLicensed, ex.Code);
        service.EnsureCanWrite();
        service.EnsureModuleWrite("ACC");
    }

    [Fact]
    public async Task License_NoFile_NotFoundDemo()
    {
        var dir = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var missing = Path.Combine(dir, "license.dat");
        var fileProvider = new LicenseFileProvider(
            Fp(RawA), new DongleProbe(), new LastSeenStore(Path.Combine(dir, "lastseen.dat")), _log,
            new[] { missing });
        var service = new EntitlementService(
            new IEntitlementProvider[] { fileProvider }, fileProvider, _log);
        var snapshot = await service.RefreshAsync();
        Assert.Equal(EntitlementStatus.NotFound, snapshot.Status);
        Assert.True(service.IsDemoMode);
    }

    // ---------------------------------------------------------------
    // Provider chain order
    // ---------------------------------------------------------------
    private sealed class StubProvider : IEntitlementProvider
    {
        private readonly EntitlementSnapshot? _snapshot;
        public StubProvider(string name, EntitlementSnapshot? snapshot)
        {
            Name = name;
            _snapshot = snapshot;
        }
        public string Name { get; }
        public Task<EntitlementSnapshot?> TryLoadAsync(CancellationToken ct = default)
            => Task.FromResult(_snapshot);
    }

    [Fact]
    public async Task Chain_FirstNonNullWins_NullsFallThrough()
    {
        var dir = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var licensePath = Path.Combine(dir, "license.dat");
        File.WriteAllText(licensePath, IssueLicense(BasePayload()));
        var fileProvider = new LicenseFileProvider(
            Fp(RawA), new DongleProbe(), new LastSeenStore(Path.Combine(dir, "lastseen.dat")), _log,
            new[] { licensePath });

        var canned = new EntitlementSnapshot
        {
            Status = EntitlementStatus.Valid,
            Source = EntitlementSource.Online,
            Edition = "Core"
        };
        var service = new EntitlementService(
            new IEntitlementProvider[]
            {
                new StubProvider("Null", null),
                new StubProvider("Canned", canned),
                fileProvider
            },
            fileProvider, _log);
        var snapshot = await service.RefreshAsync();
        Assert.Equal(EntitlementSource.Online, snapshot.Source);
    }

    // ---------------------------------------------------------------
    // Verdict store + demo overlay (reactivation)
    // ---------------------------------------------------------------
    private string IssueVerdict(VerdictPayload payload)
    {
        var payloadJson = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        var signature = Vendor.SignData(
            Encoding.UTF8.GetBytes(payloadJson), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        return JsonSerializer.Serialize(new VerdictEnvelope
        {
            Payload = payloadJson,
            Signature = Convert.ToBase64String(signature)
        });
    }

    [Fact]
    public async Task Verdict_MismatchDemo_ReplayTamperForeignRejected_ClearedReactivates()
    {
        var service = BuildService(RawA, IssueLicense(BasePayload()));
        await service.RefreshAsync();
        var store = new VerdictStore(Path.Combine(_root, "verdict-cache.json"), _log);
        VerdictEnvelope Env(string json) => JsonSerializer.Deserialize<VerdictEnvelope>(json)!;

        var mismatch = IssueVerdict(new VerdictPayload
        {
            InstallationId = "dev-test-1",
            LicenseId = "LIC-TEST-1",
            Verdict = "MISMATCH-CONFIRMED",
            ReasonCategory = "fingerprint",
            Seq = 1,
            IssuedAtUtc = DateTime.UtcNow
        });
        var accepted = store.Accept(Env(mismatch), "dev-test-1", "LIC-TEST-1");
        Assert.NotNull(accepted);
        service.ApplyVerdictOverlay(accepted!.ReasonCategory);
        Assert.True(service.IsDemoMode);
        Assert.False(service.CanWrite);

        Assert.Null(store.Accept(Env(mismatch), "dev-test-1", "LIC-TEST-1")); // replay

        var tampered = Env(mismatch);
        tampered.Signature = Convert.ToBase64String(new byte[512]);
        Assert.Null(store.Accept(tampered, "dev-test-1", "LIC-TEST-1"));

        var foreign = IssueVerdict(new VerdictPayload
        {
            InstallationId = "someone-else",
            LicenseId = "LIC-TEST-1",
            Verdict = "MISMATCH-CONFIRMED",
            Seq = 9,
            IssuedAtUtc = DateTime.UtcNow
        });
        Assert.Null(store.Accept(Env(foreign), "dev-test-1", "LIC-TEST-1"));

        var cleared = IssueVerdict(new VerdictPayload
        {
            InstallationId = "dev-test-1",
            LicenseId = "LIC-TEST-1",
            Verdict = "CLEARED",
            Seq = 2,
            IssuedAtUtc = DateTime.UtcNow
        });
        Assert.NotNull(store.Accept(Env(cleared), "dev-test-1", "LIC-TEST-1"));
        service.ApplyVerdictOverlay(null);
        Assert.False(service.IsDemoMode);
        Assert.True(service.CanWrite);
    }

    // ---------------------------------------------------------------
    // DevMode fencing (config layer)
    // ---------------------------------------------------------------
    [Fact]
    public void DevMode_DefaultsOff()
        => Assert.False(new AppSetng().Licensing.DevMode);

    [Fact]
    public async Task DevMode_ProviderGrantsNothingWhileDisabled()
    {
        var provider = new DevModeProvider(_log);
        Assert.Null(await provider.TryLoadAsync());
    }

    private sealed class HarnessLog : ILogServ
    {
        public string LogFilePath => Path.Combine(Path.GetTempPath(), "lic-tests.log");
        public void LogInfo(string category, string message) { }
        public void LogWarning(string category, string message) { }
        public void LogError(string category, string message, Exception? ex = null) { }
        public void LogError(string category, string code, string message, Exception? ex = null) { }
        public void LogDebug(string category, string message) { }
        public void LogLifecycle(string eventName, string message = "") { }
        public void LogOperation(string category, string action, string message) { }
        public void LogAuthAttempt(string username, bool success, string? reason = null) { }
        public void LogFileAccess(string filePath, string purpose, bool success = true, string? reason = null) { }
        public void LogSecurity(string eventType, string message, string? username = null) { }
    }
}
