// LicHarness — licensing & protection proof (docs/LICENSING_BUILD.md).
// Legs:
//   1. issue -> verify -> fingerprint match (valid, writable)
//   2. foreign device (>=2 strong components changed) -> demo
//   3. one-component drift -> still the same device
//   4. module lapse -> module view-only (LIC-8002), core still writes
//   5. signed verdict: MISMATCH-CONFIRMED -> demo; replay rejected;
//      tampered verdict rejected; CLEARED -> reactivated
//   6. check-in cycle against the mock server: allowlist-only report
//      shape, request signed by the installation key, verdict from
//      the report response applied, daily marker recorded
//   7. backup upload: initiate -> hashed parts -> complete, resume
//      skips held parts, retention-3 enforced by the server double
//   8. SyncServ retired path: legacy artifacts purged, zero requests
//   9. DevMode fencing: config default off; disabled -> no grant;
//      shipped config carries DevMode=false
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ShouTech.App;
using ShouTech.App.Configuration;
using ShouTech.App.Services;
using ShouTech.Domain;

var pass = 0;
var fail = 0;
void Check(string name, bool condition, string detail = "")
{
    Console.WriteLine($"{(condition ? "PASS" : "FAIL")} - {name}{(condition || detail.Length == 0 ? "" : " | " + detail)}");
    if (condition) pass++; else fail++;
}

var root = Path.Combine(Path.GetTempPath(), "lic-harness-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);

// ------------------------------------------------------------------
// Test doubles: log + device info
// ------------------------------------------------------------------
var log = new HarnessLog();
var device = new HarnessDevice();

// ------------------------------------------------------------------
// Test vendor keypair (the release key is NEVER used by tests; the
// product resolves the env override first — VendorKey precedent).
// ------------------------------------------------------------------
using var vendor = RSA.Create(4096);
var vendorPrivatePem = vendor.ExportPkcs8PrivateKeyPem();
var vendorPublicPem = vendor.ExportSubjectPublicKeyInfoPem();
Environment.SetEnvironmentVariable(VendorKey.EnvOverrideName, vendorPublicPem);
Check("vendor key resolves from env override", VendorKey.ResolvePublicKeyPem() == vendorPublicPem);

string IssueLicense(LicensePayload payload)
{
    var payloadJson = JsonSerializer.Serialize(payload, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    });
    using var signer = RSA.Create();
    signer.ImportFromPem(vendorPrivatePem);
    var signature = signer.SignData(
        Encoding.UTF8.GetBytes(payloadJson), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
    return JsonSerializer.Serialize(new LicenseEnvelope
    {
        Payload = payloadJson,
        Signature = Convert.ToBase64String(signature)
    });
}

string IssueVerdict(VerdictPayload payload, RSA key)
{
    var payloadJson = JsonSerializer.Serialize(payload, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    });
    var signature = key.SignData(
        Encoding.UTF8.GetBytes(payloadJson), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
    return JsonSerializer.Serialize(new VerdictEnvelope
    {
        Payload = payloadJson,
        Signature = Convert.ToBase64String(signature)
    });
}

// ------------------------------------------------------------------
// Devices: A (licensed), A' (one component drifted), B (foreign)
// ------------------------------------------------------------------
var rawA = new Dictionary<string, string>
{
    [FpComponent.MachineGuid] = "aaaaaaaa-1111-4222-8333-444444444444",
    [FpComponent.BaseBoardSerial] = "BOARD-A-001",
    [FpComponent.BiosSerial] = "BIOS-A-001",
    [FpComponent.DriveVolumeSerial] = "VOL-A-001",
    [FpComponent.NicMac] = "00-11-22-33-44-55",
    [FpComponent.MachineName] = "WS-A"
};
var rawDrift = new Dictionary<string, string>(rawA) { [FpComponent.NicMac] = "00-11-22-33-44-66" };
var rawB = new Dictionary<string, string>(rawA)
{
    [FpComponent.MachineGuid] = "bbbbbbbb-5555-4666-8777-888888888888",
    [FpComponent.BaseBoardSerial] = "BOARD-B-999",
    [FpComponent.BiosSerial] = "BIOS-B-999"
};

FingerprintSvc Fp(Dictionary<string, string> raw) => new(new RecordedComponentSource(raw));
var fpA = Fp(rawA).Collect();

(EntitlementService Service, string LicensePath) BuildService(Dictionary<string, string> raw, string licenseText)
{
    var dir = Path.Combine(root, Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    var licensePath = Path.Combine(dir, "license.dat");
    File.WriteAllText(licensePath, licenseText);
    var fp = Fp(raw);
    var fileProvider = new LicenseFileProvider(
        fp, new DongleProbe(), new LastSeenStore(Path.Combine(dir, "lastseen.dat")), log,
        new[] { licensePath });
    var service = new EntitlementService(
        new IEntitlementProvider[] { fileProvider }, fileProvider, log);
    return (service, licensePath);
}

var licenseAll = IssueLicense(new LicensePayload
{
    LicenseId = "LIC-HARNESS-1",
    Customer = "Harness Customer",
    Edition = "Core",
    Modules = new List<string> { "ACC", "INV", "SALES", "PURCH", "POS" },
    MaxUsers = 10,
    MaxCompanies = 1,
    Fingerprint = new Dictionary<string, string>(fpA.Components),
    IssuedAtUtc = DateTime.UtcNow.AddDays(-1),
    ExpiresAtUtc = DateTime.UtcNow.AddYears(1),
    GraceDays = 30
});

// ------------------------------------------------------------------
// Leg 1: issue -> verify -> fingerprint match
// ------------------------------------------------------------------
{
    var (service, _) = BuildService(rawA, licenseAll);
    var snapshot = await service.RefreshAsync();
    Check("leg1 license verifies and binds to device A",
        snapshot.Status == EntitlementStatus.Valid, snapshot.Status.ToString());
    Check("leg1 valid license is writable, not demo",
        service.CanWrite && !service.IsDemoMode);
    Check("leg1 entitled module POS usable", service.IsEntitled("POS"));
}

// ------------------------------------------------------------------
// Leg 2: foreign device -> binding mismatch -> demo
// ------------------------------------------------------------------
{
    var (service, _) = BuildService(rawB, licenseAll);
    var snapshot = await service.RefreshAsync();
    Check("leg2 foreign device is a binding mismatch",
        snapshot.Status == EntitlementStatus.BindingMismatch, snapshot.Status.ToString());
    Check("leg2 mismatch converts to demo / view-only",
        service.IsDemoMode && !service.CanWrite);
    var refused = false;
    try { service.EnsureCanWrite(); }
    catch (AppError ex) { refused = ex.Code == ErrorCodes.LicenseBindingMismatch; }
    Check("leg2 writes refused at the service layer with LIC-8006", refused);
}

// ------------------------------------------------------------------
// Leg 3: one-component drift -> same device
// ------------------------------------------------------------------
{
    var (service, _) = BuildService(rawDrift, licenseAll);
    var snapshot = await service.RefreshAsync();
    Check("leg3 single-component drift still matches",
        snapshot.Status == EntitlementStatus.Valid, snapshot.Status.ToString());
}

// ------------------------------------------------------------------
// Leg 4: module lapse -> that module view-only, core unaffected
// ------------------------------------------------------------------
{
    var coreOnly = IssueLicense(new LicensePayload
    {
        LicenseId = "LIC-HARNESS-2",
        Customer = "Harness Customer",
        Edition = "Core",
        Modules = new List<string> { "ACC", "INV" },
        Fingerprint = new Dictionary<string, string>(fpA.Components),
        IssuedAtUtc = DateTime.UtcNow.AddDays(-1),
        ExpiresAtUtc = DateTime.UtcNow.AddYears(1),
        GraceDays = 30
    });
    var (service, _) = BuildService(rawA, coreOnly);
    await service.RefreshAsync();
    Check("leg4 lapsed module POS is not entitled", !service.IsEntitled("POS"));
    var moduleRefused = false;
    try { service.EnsureModuleWrite("POS"); }
    catch (AppError ex) { moduleRefused = ex.Code == ErrorCodes.ModuleNotLicensed; }
    Check("leg4 module write refused with LIC-8002", moduleRefused);
    var coreAllowed = true;
    try { service.EnsureCanWrite(); service.EnsureModuleWrite("ACC"); }
    catch (AppError) { coreAllowed = false; }
    Check("leg4 licensed core still writes (module-level view-only)", coreAllowed);
}

// ------------------------------------------------------------------
// Leg 5: signed verdicts — demo on confirmed mismatch, replay and
// tamper rejected, CLEARED reactivates
// ------------------------------------------------------------------
{
    var (service, _) = BuildService(rawA, licenseAll);
    await service.RefreshAsync();
    var verdictDir = Path.Combine(root, "verdicts");
    Directory.CreateDirectory(verdictDir);
    var store = new VerdictStore(Path.Combine(verdictDir, "verdict-cache.json"));

    VerdictEnvelope Envelope(string json) => JsonSerializer.Deserialize<VerdictEnvelope>(json)!;

    var mismatchJson = IssueVerdict(new VerdictPayload
    {
        InstallationId = "dev-harness-1",
        LicenseId = "LIC-HARNESS-1",
        Verdict = "MISMATCH-CONFIRMED",
        ReasonCategory = "fingerprint",
        Seq = 1,
        IssuedAtUtc = DateTime.UtcNow
    }, vendor);
    var accepted = store.Accept(Envelope(mismatchJson), "dev-harness-1", "LIC-HARNESS-1");
    Check("leg5 signed mismatch verdict accepted", accepted is not null);
    service.ApplyVerdictOverlay(accepted!.ReasonCategory);
    Check("leg5 confirmed mismatch forces demo over a valid license",
        service.IsDemoMode && !service.CanWrite);

    var replay = store.Accept(Envelope(mismatchJson), "dev-harness-1", "LIC-HARNESS-1");
    Check("leg5 replayed verdict (same seq) rejected", replay is null);

    var tampered = Envelope(mismatchJson);
    tampered.Signature = Convert.ToBase64String(new byte[512]);
    Check("leg5 tampered verdict rejected", store.Accept(tampered, "dev-harness-1", "LIC-HARNESS-1") is null);

    var foreign = Envelope(IssueVerdict(new VerdictPayload
    {
        InstallationId = "someone-else",
        LicenseId = "LIC-HARNESS-1",
        Verdict = "MISMATCH-CONFIRMED",
        Seq = 9,
        IssuedAtUtc = DateTime.UtcNow
    }, vendor));
    Check("leg5 verdict for another installation rejected",
        store.Accept(foreign, "dev-harness-1", "LIC-HARNESS-1") is null);

    var clearedJson = IssueVerdict(new VerdictPayload
    {
        InstallationId = "dev-harness-1",
        LicenseId = "LIC-HARNESS-1",
        Verdict = "CLEARED",
        Seq = 2,
        IssuedAtUtc = DateTime.UtcNow
    }, vendor);
    var cleared = store.Accept(Envelope(clearedJson), "dev-harness-1", "LIC-HARNESS-1");
    Check("leg5 CLEARED verdict accepted", cleared is not null);
    service.ApplyVerdictOverlay(null);
    Check("leg5 reactivation: demo lifted, writes restored",
        !service.IsDemoMode && service.CanWrite);
}

// ------------------------------------------------------------------
// Mock server (the §6.1 double) + config pointing at it.
// The double sits at the HttpMessageHandler seam: this sandbox
// denies outbound TCP from agent-spawned processes (other_tcp
// policy), so a socket listener cannot be reached from this
// process. The seam is the standard .NET test position for
// HttpClient clients — the client still builds and signs real
// requests, and the double sees the exact bytes and headers and
// answers the exact §6.1 responses.
// ------------------------------------------------------------------
var mock = new MockHandler(vendorPrivatePem);
var mockHttp = new HttpClient(mock);
var configPath = Path.Combine(root, "appset.json");
File.WriteAllText(configPath, JsonSerializer.Serialize(new
{
    // EnvironmentType matters: AppCfgSvc.ApplyDevelopmentDefaults
    // forcibly empties ActivationServer on Development installs
    // (a dev machine never phones home) — the mock legs therefore
    // run as a Production install, which is the customer shape.
    // The URL is never dialed (the handler intercepts), but the
    // transport requires an allowed (loopback/HTTPS) base.
    Application = new { EnvironmentType = "Production" },
    Licensing = new
    {
        Activation = new { ActivationServer = "http://127.0.0.1:9" },
        DongleInfo = new { SerialNumber = "" }
    }
}));
AppCfgSvc.Load(configPath);
AppState.CurrentCompanyCode = "LICH";
Check("leg6 transport configured for the mock (loopback)", SignedTransport.IsConfigured);

// ------------------------------------------------------------------
// Leg 6: check-in cycle — allowlist report, signed request, verdict
// from the report response applied, daily marker recorded
// ------------------------------------------------------------------
var checkinDir = Path.Combine(root, "checkin");
Directory.CreateDirectory(checkinDir);
var keys = new InstallationKeyStore(Path.Combine(checkinDir, "installation-key.dat"));
var verdictStore = new VerdictStore(Path.Combine(checkinDir, "verdict-cache.json"));
var stateStore = new CheckInStateStore(Path.Combine(checkinDir, "checkin-state.json"));
var checkIn = new CheckInSvc(log, device, keys, verdictStore, stateStore, mockHttp);
await checkIn.RunPostLoginCycleAsync(force: true);
{
    Check("leg6 exactly one report reached the server", mock.ReportCount == 1, $"count={mock.ReportCount}");
    var body = mock.LastReportBody ?? "";
    var forbidden = new[] { "password", "license.local", "user.dat", "userprefs" };
    Check("leg6 report carries no forbidden material",
        forbidden.All(f => !body.Contains(f, StringComparison.OrdinalIgnoreCase)));

    using var doc = JsonDocument.Parse(body);
    var topKeys = doc.RootElement.EnumerateObject().Select(p => p.Name)
        .OrderBy(k => k, StringComparer.Ordinal).ToArray();
    Check("leg6 report top-level shape is the allowlist",
        topKeys.SequenceEqual(new[] { "company", "device", "installation", "license", "reportLocalDate", "sentAtUtc", "users" }),
        string.Join(",", topKeys));

    var licenseKeys = doc.RootElement.GetProperty("license").EnumerateObject().Select(p => p.Name)
        .OrderBy(k => k, StringComparer.Ordinal).ToArray();
    Check("leg6 license section is the allowlist",
        licenseKeys.SequenceEqual(new[]
        {
            "activatedAtUtc", "edition", "expiresAtUtc", "features", "isTrial", "isViewer",
            "lastOnlineCheckUtc", "licenseId", "licenseKey", "maxCompanies", "maxUsers",
            "modules", "source", "status"
        }), string.Join(",", licenseKeys));
    Check("leg6 license identity falls back to LOCAL-DEV",
        doc.RootElement.GetProperty("license").GetProperty("licenseKey").GetString() == "LOCAL-DEV");

    // The request must verify against the installation public key
    // the report itself carries (canonical string, §6.2).
    var headers = mock.LastReportHeaders!;
    var bodyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
    var canonical = $"POST\n/installations/report\n{bodyHash}\n{headers["X-ShouTech-Timestamp"]}\n{headers["X-ShouTech-Nonce"]}";
    using var verifyKey = RSA.Create();
    verifyKey.ImportFromPem(doc.RootElement.GetProperty("device").GetProperty("installationPublicKey").GetString());
    var signatureOk = verifyKey.VerifyData(
        Encoding.UTF8.GetBytes(canonical),
        Convert.FromBase64String(headers["X-ShouTech-Signature"]),
        HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
    Check("leg6 request signature verifies with the installation key", signatureOk);

    var state = stateStore.Read();
    Check("leg6 verdict from the report response was applied",
        state.LastVerdict == "OK" && state.LastVerdictSeq == 7,
        $"verdict={state.LastVerdict} seq={state.LastVerdictSeq}");
    var marker = CheckInStateStore.MarkerFor(state, "LICH");
    Check("leg6 daily report marker recorded after success",
        marker.LastReportLocalDate == DateTime.Now.ToString("yyyy-MM-dd") && state.LastReportUtc is not null);
}

// ------------------------------------------------------------------
// Leg 7: backup upload — parts hashed, resume honored, retention-3
// ------------------------------------------------------------------
{
    var uploads = new BackupUploadSvc(keys,
        new SignedTransport(keys, mockHttp),
        stateStore, log);

    string MakePackage(int n, DateTime exportedUtc)
    {
        // Incompressible payload: the .stb is a ZIP, and a patterned
        // payload would compress into a single 64 KiB part, leaving
        // the multi-part and resume legs nothing to exercise.
        var payload = new byte[150_000];
        new Random(1234 + n).NextBytes(payload);
        var hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var path = Path.Combine(root, $"pkg{n}.stb");
        using var fs = File.Create(path);
        using var zip = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create);
        var manifest = zip.CreateEntry("manifest.json");
        using (var writer = new StreamWriter(manifest.Open()))
            writer.Write(JsonSerializer.Serialize(new
            {
                PackageId = $"PKG-{n}",
                PayloadHash = hash,
                SourceCompanyCode = "LICH",
                ExportedAtUtc = exportedUtc.ToString("o")
            }));
        var entry = zip.CreateEntry("payload.bin");
        using (var stream = entry.Open())
            stream.Write(payload, 0, payload.Length);
        return path;
    }

    var baseDate = new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc);
    var allUploaded = true;
    for (var n = 1; n <= 4; n++)
    {
        var descriptor = BackupUploadSvc.ReadDescriptor(MakePackage(n, baseDate.AddDays(n)));
        allUploaded &= await uploads.UploadPackageAsync(descriptor);
    }
    Check("leg7 four packages upload end-to-end", allUploaded);
    Check("leg7 every part hash verified by the server", mock.HashMismatches == 0,
        $"mismatches={mock.HashMismatches}");

    var state = stateStore.Read();
    Check("leg7 retention-3: server holds exactly the newest three",
        state.ServerCopies.Count == 3 && state.ServerCopies.Any(c => c.PackageId == "PKG-4")
        && state.ServerCopies.All(c => c.PackageId != "PKG-1"),
        string.Join(",", state.ServerCopies.Select(c => c.PackageId)));

    mock.ResumeNextInitiate = true;
    var resumeDescriptor = BackupUploadSvc.ReadDescriptor(MakePackage(5, baseDate.AddDays(5)));
    var resumeOk = await uploads.UploadPackageAsync(resumeDescriptor);
    Check("leg7 resume: held part 0 is not re-sent",
        resumeOk && !mock.LastUploadPutParts.Contains(0) && mock.LastUploadPutParts.Count >= 2,
        $"parts={string.Join(",", mock.LastUploadPutParts)}");
}

// ------------------------------------------------------------------
// Leg 8: SyncServ retired path — purge only, zero network
// ------------------------------------------------------------------
{
    var snapshots = Path.Combine(AppPaths.Sync, "snapshots");
    Directory.CreateDirectory(snapshots);
    var legacy = Path.Combine(snapshots, "users_20200101_000000.json");
    File.WriteAllText(legacy, "{\"files\":{\"license.local.json\":\"{}\"}}");
    var requestsBefore = mock.Requests.Count;

    var sync = new SyncServ(log, device);
    await sync.SnapshotLocalUserDataAsync();
    var drained = await sync.ProcessQueueAsync();

    Check("leg8 legacy snapshot artifacts purged",
        !File.Exists(legacy) && Directory.EnumerateFiles(snapshots).Count() == 0);
    Check("leg8 the retired path made zero network requests",
        mock.Requests.Count == requestsBefore && drained >= 0);
}

// ------------------------------------------------------------------
// Leg 9: DevMode fencing
// ------------------------------------------------------------------
{
    Check("leg9 DevMode defaults to off in a fresh config",
        new AppSetng().Licensing.DevMode == false);
    var provider = new DevModeProvider(log);
    Check("leg9 DevMode provider grants nothing while disabled",
        await provider.TryLoadAsync() is null);

    var shipped = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "cfg", "appset.json"));
    using var doc = JsonDocument.Parse(File.ReadAllText(shipped));
    Check("leg9 shipped cfg/appset.json carries DevMode=false",
        doc.RootElement.GetProperty("Licensing").GetProperty("DevMode").GetBoolean() == false);
}

Console.WriteLine($"== LIC HARNESS: {pass} PASS / {fail} FAIL ==");
Environment.ExitCode = fail == 0 ? 0 : 1;

// ------------------------------------------------------------------
// Test doubles
// ------------------------------------------------------------------
sealed class HarnessLog : ILogServ
{
    public string LogFilePath => Path.Combine(Path.GetTempPath(), "lic-harness.log");
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

sealed class HarnessDevice : IDevInfoSv
{
    public DeviceInfoSnapshot Current { get; } = new()
    {
        DeviceId = "dev-harness-1",
        HardwareId = "hw-harness",
        MachineName = "WS-A",
        OsVersion = "TestOS 1.0",
        UserName = "harness",
        AppVersion = "10.6.0",
        InstanceId = "ST-ERP-HARNESS"
    };
    public DeviceInfoSnapshot Collect() => Current;
    public Task RegisterWithServerAsync() => Task.CompletedTask;
}

/// <summary>The §6.1 server double at the HttpMessageHandler
/// seam: report/verdict/initiate/parts/complete with per-part
/// SHA-256 checks and retention-3 pruning. Records every request
/// so legs can prove what did NOT happen (the retired /sync
/// path).</summary>
sealed class MockHandler : HttpMessageHandler
{
    private readonly string _vendorPrivatePem;
    private readonly List<(string PackageId, string Company, DateTime DateUtc, long Size, string Hash)> _held = new();
    private readonly Dictionary<string, (string PackageId, string Company, DateTime DateUtc, string Hash)> _uploads = new();

    public MockHandler(string vendorPrivatePem) => _vendorPrivatePem = vendorPrivatePem;

    public List<(string Method, string Path)> Requests { get; } = new();
    public int ReportCount { get; private set; }
    public string? LastReportBody { get; private set; }
    public IReadOnlyDictionary<string, string>? LastReportHeaders { get; private set; }
    public int HashMismatches { get; private set; }
    public bool ResumeNextInitiate { get; set; }
    public IReadOnlyList<int> LastUploadPutParts { get; private set; } = Array.Empty<int>();
    private readonly List<int> _currentPutParts = new();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        Requests.Add((request.Method.Method, path));
        var body = request.Content is null
            ? Array.Empty<byte>()
            : await request.Content.ReadAsByteArrayAsync(cancellationToken);

        if (request.Method == HttpMethod.Post && path == "/installations/report")
        {
            ReportCount++;
            LastReportBody = Encoding.UTF8.GetString(body);
            LastReportHeaders = request.Headers
                .ToDictionary(h => h.Key, h => string.Join(",", h.Value));
            return Respond(200, new { verdictEnvelope = OkVerdictEnvelope() });
        }

        if (request.Method == HttpMethod.Get && path == "/installations/verdict")
            return new HttpResponseMessage(HttpStatusCode.NotFound);

        if (request.Method == HttpMethod.Post && path == "/backup/initiate")
        {
            using var doc = JsonDocument.Parse(body);
            var rootEl = doc.RootElement;
            var uploadId = "upl-" + Guid.NewGuid().ToString("N");
            _uploads[uploadId] = (
                rootEl.GetProperty("packageId").GetString()!,
                rootEl.GetProperty("companyCode").GetString()!,
                DateTime.Parse(rootEl.GetProperty("backupDateUtc").GetString()!, null, System.Globalization.DateTimeStyles.RoundtripKind),
                rootEl.GetProperty("payloadHash").GetString()!);
            _currentPutParts.Clear();
            int[] received = Array.Empty<int>();
            if (ResumeNextInitiate)
            {
                received = new[] { 0 };
                ResumeNextInitiate = false;
            }
            return Respond(200, new { uploadId, partSize = 65536, receivedParts = received });
        }

        if (request.Method == HttpMethod.Put && path.StartsWith("/backup/", StringComparison.Ordinal) && path.Contains("/parts/"))
        {
            var segments = path.Split('/');
            var part = int.Parse(segments[4], System.Globalization.CultureInfo.InvariantCulture);
            var expected = request.Headers.TryGetValues("X-Part-Sha256", out var values)
                ? values.FirstOrDefault()
                : null;
            var actual = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                HashMismatches++;
                return new HttpResponseMessage(HttpStatusCode.BadRequest);
            }
            _currentPutParts.Add(part);
            LastUploadPutParts = _currentPutParts.ToArray();
            return Respond(200, new { ok = true });
        }

        if (request.Method == HttpMethod.Post && path.StartsWith("/backup/", StringComparison.Ordinal) && path.EndsWith("/complete", StringComparison.Ordinal))
        {
            var uploadId = path.Split('/')[2];
            var meta = _uploads[uploadId];
            _held.RemoveAll(h => h.PackageId == meta.PackageId);
            _held.Add((meta.PackageId, meta.Company, meta.DateUtc, 150_000, meta.Hash));
            var pruned = new List<string>();
            foreach (var companyGroup in _held.GroupBy(h => h.Company))
            {
                foreach (var old in companyGroup.OrderByDescending(h => h.DateUtc).Skip(3).ToList())
                {
                    _held.Remove(old);
                    pruned.Add(old.PackageId);
                }
            }
            var inventory = _held
                .OrderByDescending(h => h.DateUtc)
                .Select(h => new
                {
                    packageId = h.PackageId,
                    backupDateUtc = h.DateUtc.ToString("o"),
                    sizeBytes = h.Size,
                    payloadHash = h.Hash
                }).ToArray();
            return Respond(200, new { inventory, pruned });
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private object OkVerdictEnvelope()
    {
        var payloadJson = JsonSerializer.Serialize(new VerdictPayload
        {
            InstallationId = "dev-harness-1",
            LicenseId = "LOCAL-DEV",
            Verdict = "OK",
            Seq = 7,
            IssuedAtUtc = DateTime.UtcNow
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        using var signer = RSA.Create();
        signer.ImportFromPem(_vendorPrivatePem);
        var signature = signer.SignData(
            Encoding.UTF8.GetBytes(payloadJson), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        return new VerdictEnvelope
        {
            Payload = payloadJson,
            Signature = Convert.ToBase64String(signature)
        };
    }

    private static HttpResponseMessage Respond(int status, object payload)
        => new((HttpStatusCode)status)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
}
