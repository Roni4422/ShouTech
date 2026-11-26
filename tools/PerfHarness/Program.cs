using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using ShouTech.App.Services;

// PerfHarness — segment timings for docs/STARTUP_PERF_STUDY.md.
// Every line is prefixed PERF. These are headless segment timings on
// this machine, NOT application startup times.

var baseDir = AppContext.BaseDirectory;

void Report(string segment, double ms, string note)
    => Console.WriteLine($"PERF {segment} {ms:F1}ms {note}");

double Time(Action action)
{
    var sw = Stopwatch.StartNew();
    action();
    sw.Stop();
    return sw.Elapsed.TotalMilliseconds;
}

async Task<double> TimeAsync(Func<Task> action)
{
    var sw = Stopwatch.StartNew();
    await action();
    sw.Stop();
    return sw.Elapsed.TotalMilliseconds;
}

// ------------------------------------------------------------------
// 1. Embedded-resource proof: the startup "SchemaBootstrapper" looks
//    for ShouTech.App.dba.lite.sql inside the app assembly.
// ------------------------------------------------------------------
var appDll = Path.Combine(baseDir, "ShouTech.ERP.dll");
var resources = Assembly.LoadFrom(appDll).GetManifestResourceNames();
Report("manifest-resources", 0,
    $"count={resources.Length} dba.lite.sql={(resources.Any(r => r.Contains("dba.lite.sql")) ? "PRESENT" : "ABSENT")}");

// ------------------------------------------------------------------
// 2. Splash integrity check: whole-file read + SHA-256 (today) vs
//    FileInfo.Length (the only value the check actually uses).
// ------------------------------------------------------------------
Report("integrity-readhash", Time(() =>
{
    var bytes = File.ReadAllBytes(appDll);
    _ = Convert.ToHexString(SHA256.HashData(bytes));
}), $"file={new FileInfo(appDll).Length} bytes");
Report("integrity-length", Time(() =>
{
    _ = new FileInfo(appDll).Length;
}), "FileInfo.Length only");

// ------------------------------------------------------------------
// 3. Localization: construction + language adoption, per language.
//    The warm-up instance runs a FULL cycle (construct, adopt,
//    lookup, label resolve) so one-time JIT / System.Text.Json
//    metadata costs are paid before timing in every code version —
//    in the real process those costs exist exactly once either way.
// ------------------------------------------------------------------
var warm = new LocSvc();
warm.SetLanguage("en");
_ = warm.GetString("Application.Title");
_ = warm.TryResolveKeyFromLabel("Save", out _);
Report("locsvc-construct-only", Time(() =>
{
    _ = new LocSvc();
}), "constructor alone (index + resources deferred after L3)");
Report("locsvc-construct+en", Time(() =>
{
    var svc = new LocSvc();
    svc.SetLanguage("en");
    _ = svc.GetString("Application.Title");
}), "ctor + SetLanguage(en) + first lookup");
Report("locsvc-construct+ar", Time(() =>
{
    var svc = new LocSvc();
    svc.SetLanguage("ar");
    _ = svc.GetString("Application.Title");
}), "ctor + SetLanguage(ar) + first lookup");
Report("locsvc-labelindex", Time(() =>
{
    var svc = new LocSvc();
    // The label->key index over all four catalogs: built eagerly by
    // the constructor before the lazy change, on first use after it.
    _ = svc.TryResolveKeyFromLabel("Save", out _);
}), "first TryResolveKeyFromLabel on a fresh instance");

// ------------------------------------------------------------------
// 4. Database segments on a real migrated SQLite database.
// ------------------------------------------------------------------
var dir = Path.Combine(Path.GetTempPath(), "perfharness-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);
var dbPath = Path.Combine(dir, "perf.db");
var cs = $"Data Source={dbPath}";
await SchemaMigrator.ApplyAllAsync("SQLite", dbPath); // provisioning pass (untimed)
Report("migrator-all-applied", await TimeAsync(() => SchemaMigrator.ApplyAllAsync("SQLite", dbPath)),
    "second pass: every migration already applied (checksum compare path)");
Report("ensuredb-reseed", Time(() => SqliteSchemaBootstrapper.EnsureDatabase(cs)),
    "SqliteSchemaBootstrapper.EnsureDatabase on a migrated DB (permission re-seed)");

Console.WriteLine("PERF-HARNESS DONE");
return 0;
