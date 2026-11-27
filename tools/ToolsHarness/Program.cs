// ToolsHarness — unified Tools window proof (TOOLS_WINDOW_STUDY §8).
// Exercises the REAL stores and engines the ToolsWin Backup & automation
// and Overview sections are wired to:
//   settings: the six AppConfig values round-trip through
//     AppCfgSvc.Load/Save (the store BkpServ and OnlineCoordinator read);
//   consumer (due): BkpServ.IsBackupDue flips correctly across the
//     Enabled / AutoBackup / interval settings against real .stb files;
//   consumer (rotation): CreateBackupAsync + PruneOldBackups keep
//     exactly MaxBackups newest packages; ValidateBackupAsync verifies
//     a created package (the window's Verify-after-backup consumer);
//   health: OperationsRepository schema-version / integrity summary /
//     trial-balance verdict on a real migrated database, and the
//     recycle-bin count source BinWin lists from;
//   errors: TLS-8501..8503 resolve through the real ErrorCatalogSvc in
//     all four languages.
// BackupOnStartup and the background-cycle interval are governed by
// OnlineCoordinator (a shell-lifetime coordinator that cannot be hosted
// headlessly); their settings are proven by the round-trip here and
// their consumer reads are grep-verified in the study.
using ShouTech.App;
using ShouTech.App.Configuration;
using ShouTech.App.Services;
using ShouTech.Application.Services;
using ShouTech.Domain;
using ShouTech.Infrastructure;

const string CompanyCode = "001";

var pass = 0;
var fail = 0;
void Check(string name, bool condition, string detail = "")
{
    Console.WriteLine($"{(condition ? "PASS" : "FAIL")} - {name}{(condition || detail.Length == 0 ? "" : " | " + detail)}");
    if (condition) pass++; else fail++;
}

var root = Path.Combine(Path.GetTempPath(), "tools-harness-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var dbPath = Path.Combine(root, "tools.db");
var cs = $"Data Source={dbPath};";

// ---------------------------------------------------------------- setup
var migration = await SchemaMigrator.ApplyAllAsync("SQLite", dbPath);
Check("schema migrates (through 124)", migration.Success, migration.Message);

// A real configuration file the window's Save can persist to.
var cfgPath = Path.Combine(root, "appset.json");
File.WriteAllText(cfgPath, "{}");
AppCfgSvc.Load(cfgPath);
var settings = AppCfgSvc.Current;
settings.ConnectionStrings["Default"] = new ConnectionStringSettings { Provider = "SQLite", ConnectionString = dbPath };
settings.Database ??= new DatabaseSettings();
settings.Database.DefaultProvider = "SQLite";
AppState.SetCompany(CompanyCode, "Tools Harness Co", "Tools Harness Co");

// ------------------------------------------------------- settings store
settings.Database.Backup.Enabled = false;
settings.Database.Backup.AutoBackup = false;
settings.OnlineServices.BackupOnStartup = false;
settings.Database.Backup.BackupIntervalHours = 7;
settings.Database.Backup.MaxBackups = 9;
settings.OnlineServices.BackgroundSyncIntervalSeconds = 4321;
AppCfgSvc.Save();
AppCfgSvc.Load(cfgPath);
var reloaded = AppCfgSvc.Current;
Check("settings round-trip: Enabled", reloaded.Database.Backup.Enabled == false);
Check("settings round-trip: AutoBackup", reloaded.Database.Backup.AutoBackup == false);
Check("settings round-trip: BackupOnStartup", reloaded.OnlineServices.BackupOnStartup == false);
Check("settings round-trip: BackupIntervalHours", reloaded.Database.Backup.BackupIntervalHours == 7);
Check("settings round-trip: MaxBackups", reloaded.Database.Backup.MaxBackups == 9);
Check("settings round-trip: BackgroundSyncIntervalSeconds", reloaded.OnlineServices.BackgroundSyncIntervalSeconds == 4321);
Check("settings persist only with a loaded config file (TLS-8501 precondition)",
    !string.IsNullOrWhiteSpace(AppCfgSvc.LoadedPath));

// ------------------------------------------------------------- due rule
var log = new LogServ();
var devInfo = new DevInfoSv(log);
var bkp = new BkpServ(log, devInfo, new SyncServ(log, devInfo));
var backupDir = AppPaths.CompanyBackup(CompanyCode);
Directory.CreateDirectory(backupDir);
foreach (var stale in Directory.EnumerateFiles(backupDir, "*.stb"))
    File.Delete(stale);

var cfg = AppCfgSvc.Current.Database.Backup;
cfg.Enabled = false; cfg.AutoBackup = true; cfg.BackupIntervalHours = 24;
Check("due: disabled system is never due", !bkp.IsBackupDue());
cfg.Enabled = true; cfg.AutoBackup = false;
Check("due: automatic backup off is never due", !bkp.IsBackupDue());
cfg.AutoBackup = true;
Check("due: no backups at all is due", bkp.IsBackupDue());

// ------------------------------------------- due rule vs real .stb files
// IsBackupDue reads only existence + LastWriteTimeUtc of *.stb files,
// so its interval leg is proven with real files in the real folder.
var probe = Path.Combine(backupDir, "shouteck_backup_probe.stb");
File.WriteAllBytes(probe, new byte[] { 1, 2, 3 });
cfg.BackupIntervalHours = 24;
Check("due: fresh backup within interval is not due", !bkp.IsBackupDue());
File.SetLastWriteTimeUtc(probe, DateTime.UtcNow.AddHours(-2));
cfg.BackupIntervalHours = 1;
Check("due: backup older than the interval is due", bkp.IsBackupDue());
cfg.BackupIntervalHours = 24;
Check("due: same backup inside a longer interval is not due", !bkp.IsBackupDue());
File.Delete(probe);

// ------------------------------------------------- create/rotate/verify
// Package creation protects the STEI package password with Windows
// DPAPI (System.Security.Cryptography.ProtectedData): on a platform
// without that assembly the create/rotate/verify legs are reported as
// a named SKIP — never as a pass — and they run for real on Windows.
try
{
    cfg.MaxBackups = 2;
    var first = await bkp.CreateBackupAsync(CompanyCode, "harness-a");
    Check("backup created through the real engine", File.Exists(first), first);
    var second = await bkp.CreateBackupAsync(CompanyCode, "harness-b");
    var third = await bkp.CreateBackupAsync(CompanyCode, "harness-c");
    var kept = Directory.EnumerateFiles(backupDir, "*.stb").ToList();
    Check("rotation keeps exactly MaxBackups packages", kept.Count == 2, string.Join(",", kept));
    Check("rotation prunes the oldest package", !File.Exists(first) && File.Exists(second) && File.Exists(third));
    Check("created package validates (verify-after-backup consumer)", await bkp.ValidateBackupAsync(third));
}
catch (Exception ex) when (ex is FileNotFoundException or PlatformNotSupportedException)
{
    Console.WriteLine($"SKIP - backup creation/rotation/verification: Windows-only DPAPI assembly unavailable on this platform ({ex.GetType().Name})");
}

// ---------------------------------------------------------------- health
var ops = new OperationsService(new OperationsRepository("SQLite", cs));
var schema = await ops.GetSchemaVersionAsync();
Check("health: schema version reports applied migrations",
    // The prefix tracks the newest migration on the merged tree
    // (124 on the tools branch; 126 after the reduce merge added
    // the OptWin print/barcode migrations).
    schema.AppliedCount > 0 && schema.HighestMigrationId.StartsWith("126", StringComparison.Ordinal),
    $"{schema.AppliedCount}/{schema.HighestMigrationId}");
var integrity = await ops.GetIntegritySummaryAsync();
Check("health: integrity summary returns the SQLite verdict",
    integrity.Contains("ok", StringComparison.OrdinalIgnoreCase), integrity);
var tb = await ops.GetTrialBalanceIntegrityAsync(CompanyCode);
Check("health: trial-balance verdict on empty books is clean", tb.IsClean);
var binCount = await RecycleBinSvc.CountAsync(CompanyCode);
Check("health: recycle-bin count source answers", binCount == 0, binCount.ToString());

// ---------------------------------------------------------------- errors
var errorsDir = FindUp(AppContext.BaseDirectory, Path.Combine("app", "Resources", "Errors"))
    ?? FindUp(Directory.GetCurrentDirectory(), Path.Combine("app", "Resources", "Errors"));
foreach (var lang in new[] { "en", "ar", "fr", "tr" })
{
    var catalog = new ErrorCatalogSvc(() => lang, errorsDir);
    foreach (var (code, name) in new[]
             {
                 (ErrorCodes.ToolsSettingsNotPersisted, "ToolsSettingsNotPersisted"),
                 (ErrorCodes.ToolsHealthCheckFailed, "ToolsHealthCheckFailed"),
                 (ErrorCodes.ToolsBackupActionFailed, "ToolsBackupActionFailed")
             })
    {
        var info = catalog.Resolve(code);
        Check($"errors: {code} resolves in {lang}",
            info.Code == code && info.Name == name && !string.IsNullOrWhiteSpace(info.Message),
            $"{info.Code}/{info.Name}");
    }
}

// -------------------------------------------------------------- cleanup
foreach (var f in Directory.EnumerateFiles(backupDir, "*.stb"))
    File.Delete(f);
try { Directory.Delete(root, recursive: true); } catch { /* temp residue is harmless */ }

Console.WriteLine($"TOOLS HARNESS: {pass} PASS / {fail} FAIL");
return fail == 0 ? 0 : 1;

static string? FindUp(string start, string relative)
{
    var dir = new DirectoryInfo(start);
    for (var i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
    {
        var candidate = Path.Combine(dir.FullName, relative);
        if (Directory.Exists(candidate))
            return candidate;
    }
    return null;
}
