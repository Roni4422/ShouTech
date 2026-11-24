// ========================================================================
// LicenseIssuer — the VENDOR-side offline license tool.
//
//   genkey  <private.pem> <public.pem>
//       Generates the vendor RSA-4096 signing keypair. The PRIVATE
//       key never enters the repository and never ships; the public
//       key is embedded in the product (Services/Entitlements.cs,
//       VendorKey) and may also be supplied per-installation through
//       the SHOUTECH_LICENSE_PUBLIC_KEY environment override.
//
//   issue   --key <private.pem> --out <license.dat> [options]
//       Builds the signed license envelope the product verifies:
//       { "payload": "<payload JSON, signed verbatim>",
//         "signature": "<base64 RSA-PSS-SHA256>" }
//       Payload fields match Services/Entitlements.cs LicensePayload.
//       --fingerprint <fp.json> takes a JSON object mapping component
//       type names (MachineGuid, BaseBoardSerial, BiosSerial,
//       DriveVolumeSerial, NicMac, MachineName) to their SHA-256
//       hashes — exactly what FingerprintSvc produces on the target
//       machine. Raw serials are never part of a license.
//
//   verify  --pub <public.pem> --file <license.dat>
//       Verifies an envelope's signature and prints its payload.
//
// This tool is vendor tooling: it is not part of the product build
// and is never distributed to customers.
// ========================================================================

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// 'args' is the compiler-provided top-level parameter.
if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: LicenseIssuer genkey|issue|verify ...");
    return 2;
}

try
{
    return args[0] switch
    {
        "genkey" => GenKey(args),
        "issue" => Issue(args),
        "verify" => Verify(args),
        _ => Fail($"Unknown command '{args[0]}'.")
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ERROR: {ex.Message}");
    return 1;
}

static int Fail(string message)
{
    Console.Error.WriteLine($"ERROR: {message}");
    return 2;
}

static Dictionary<string, string> Options(string[] argv)
{
    var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 1; i < argv.Length; i++)
    {
        if (!argv[i].StartsWith("--", StringComparison.Ordinal))
            throw new InvalidOperationException($"Expected an option at position {i}: '{argv[i]}'.");
        var name = argv[i][2..];
        if (name is "trial" or "viewer")
        {
            map[name] = "true";
            continue;
        }
        if (i + 1 >= argv.Length)
            throw new InvalidOperationException($"Option --{name} needs a value.");
        map[name] = argv[++i];
    }
    return map;
}

static string Need(Dictionary<string, string> options, string name)
    => options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value
        : throw new InvalidOperationException($"--{name} is required.");

static int GenKey(string[] argv)
{
    if (argv.Length != 3)
        return Fail("Usage: LicenseIssuer genkey <private.pem> <public.pem>");

    using var rsa = RSA.Create(4096);
    File.WriteAllText(argv[1], rsa.ExportPkcs8PrivateKeyPem());
    File.WriteAllText(argv[2], rsa.ExportSubjectPublicKeyInfoPem());
    Console.WriteLine($"Private key: {argv[1]} (KEEP OFFLINE — never commit, never ship)");
    Console.WriteLine($"Public key:  {argv[2]} (embed in VendorKey.ReleasePublicKeyPem)");
    return 0;
}

static int Issue(string[] argv)
{
    var options = Options(argv);
    var keyPath = Need(options, "key");
    var outPath = Need(options, "out");

    var modules = (options.GetValueOrDefault("modules") ?? string.Empty)
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    Dictionary<string, string>? fingerprint = null;
    if (options.TryGetValue("fingerprint", out var fpPath))
    {
        fingerprint = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(fpPath))
            ?? throw new InvalidOperationException("Fingerprint file did not contain a component map.");
    }

    DateTime? expires = options.TryGetValue("expires", out var exp)
        ? DateTime.Parse(exp, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal)
        : null;

    var payload = new
    {
        licenseId = options.GetValueOrDefault("license-id") ?? $"LIC-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
        customer = Need(options, "customer"),
        edition = options.GetValueOrDefault("edition") ?? "Core",
        modules,
        maxUsers = IntOr(options, "max-users", 0),
        maxCompanies = IntOr(options, "max-companies", 0),
        maxDevices = IntOr(options, "max-devices", 0),
        dongleSerial = options.GetValueOrDefault("dongle"),
        fingerprint,
        issuedAtUtc = DateTime.UtcNow,
        expiresAtUtc = expires,
        graceDays = IntOr(options, "grace-days", 14),
        isTrial = options.ContainsKey("trial"),
        isViewer = options.ContainsKey("viewer")
    };

    var payloadJson = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });

    using var rsa = RSA.Create();
    rsa.ImportFromPem(File.ReadAllText(keyPath));
    var signature = rsa.SignData(
        Encoding.UTF8.GetBytes(payloadJson),
        HashAlgorithmName.SHA256,
        RSASignaturePadding.Pss);

    var envelope = JsonSerializer.Serialize(
        new { payload = payloadJson, signature = Convert.ToBase64String(signature) },
        new JsonSerializerOptions { WriteIndented = true });

    File.WriteAllText(outPath, envelope);
    Console.WriteLine($"License written: {outPath}");
    Console.WriteLine(payloadJson);
    return 0;
}

static int IntOr(Dictionary<string, string> options, string name, int fallback)
    => options.TryGetValue(name, out var raw) && int.TryParse(raw, out var value) ? value : fallback;

static int Verify(string[] argv)
{
    var options = Options(argv);
    using var rsa = RSA.Create();
    rsa.ImportFromPem(File.ReadAllText(Need(options, "pub")));

    using var doc = JsonDocument.Parse(File.ReadAllText(Need(options, "file")));
    var payload = doc.RootElement.GetProperty("payload").GetString()
        ?? throw new InvalidOperationException("Envelope has no payload.");
    var signature = Convert.FromBase64String(doc.RootElement.GetProperty("signature").GetString()
        ?? throw new InvalidOperationException("Envelope has no signature."));

    var ok = rsa.VerifyData(
        Encoding.UTF8.GetBytes(payload),
        signature,
        HashAlgorithmName.SHA256,
        RSASignaturePadding.Pss);

    Console.WriteLine(ok ? "SIGNATURE VALID" : "SIGNATURE INVALID");
    Console.WriteLine(payload);
    return ok ? 0 : 1;
}
