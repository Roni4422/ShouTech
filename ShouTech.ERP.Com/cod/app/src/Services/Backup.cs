// ========================================================================
// FILE: Services/Backup.cs
// PROJECT: SHOUTECH ERP — Backup service + STEI encrypted packages
// Merged: BkpFileInfo, IBkpSvc, SteiModels, SteiCrypto, SteiSvc, BkpServ
// ========================================================================

#nullable enable

using System.Collections.Generic;
using System.Data.Common;
using System.Data;
using System.IO.Compression;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.App.Configuration;
using System.Globalization;

using ShouTech.Domain;
namespace ShouTech.App.Services
{

    // ========== BkpFileInfo.cs ==========
/// <summary>
/// Presentation-neutral information about a local ShouTech backup package.
/// </summary>
public sealed class BkpFileInfo
{
    public string FilePath { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public DateTime LastWriteUtc { get; init; }
    public long SizeBytes { get; init; }
    public bool IsValid { get; set; }
    public string Status { get; set; } = string.Empty;
}

    // ========== IBkpSvc.cs ==========
public class BackupResult
    {
        public bool Success { get; set; }
        public string? LocalPath { get; set; }
        public bool Uploaded { get; set; }
        public string? Message { get; set; }
    }

        public class BackupValidationDetail
    {
        public bool IsValid { get; set; }
        public string? FailureReason { get; set; }
    }

    public interface IBkpServ
    {
        Task<string> CreateBackupAsync(string companyCode, CancellationToken ct = default);
	Task<string> CreateBackupAsync(string companyCode, string notes);
        Task RestoreBackupAsync(string backupFilePath, CancellationToken ct = default);
        Task<bool> ValidateBackupAsync(string backupFilePath, CancellationToken ct = default);
        void PerformBackup();
        BackupResult PerformRotatedBackup();
        bool IsBackupDue();
        Task<BackupResult> RunOnlineBackupCycleAsync(bool force = false, CancellationToken ct = default);
        Task<bool> TryUploadRotatedBackupsAsync(CancellationToken ct = default);

        List<BkpFileInfo> ListBackups(string companyCode);
        Task<BackupValidationDetail> ValidateBackupDetailedAsync(string backupFilePath, CancellationToken ct = default);
        string GetBackupDirectory(string companyCode);
    }

    // ========== SteiModels.cs ==========
internal sealed class SteiManifest
{
    public string Format { get; set; } = "SHOUTECH-STEI";
    public int FormatVersion { get; set; } = 1;
    public int SchemaVersion { get; set; } = 1;
    public string PackageId { get; set; } = Guid.NewGuid().ToString("D");
    public string ExportedAtUtc { get; set; } = DateTimeOffset.UtcNow.ToString("O");
    public string ExportedLocal { get; set; } = DateTimeOffset.Now.ToString("O");
    public string ExportedByUserId { get; set; } = "";
    public string ExportedByUserName { get; set; } = "";
    public string SourceCompanyId { get; set; } = "";
    public string SourceCompanyCode { get; set; } = "";
    public string SourceCompanyName { get; set; } = "";
    public string SourceDatabaseName { get; set; } = "";
    public string SourceDatabaseProvider { get; set; } = "";
    public string TargetDatabaseName { get; set; } = "";
    public string TargetDatabaseProvider { get; set; } = "";
    public string ApplicationVersion { get; set; } = "";
    public string DataScope { get; set; } = "CompanyData";
    public int TableCount { get; set; }
    public long RowCount { get; set; }
    public string PayloadHash { get; set; } = "";
    public string SignatureAlgorithm { get; set; } = "RSA-PSS-SHA256";
    public string Signature { get; set; } = "";
    public string SigningKeyFingerprint { get; set; } = "";
    public string EncryptionAlgorithm { get; set; } = "AES-256-GCM";
    public string KeyDerivation { get; set; } = "PBKDF2-SHA256";
    public string Salt { get; set; } = "";
    public int Iterations { get; set; } = 310_000;
    public string Nonce { get; set; } = "";
    public List<string> ExcludedTables { get; set; } = new();
}

internal sealed class SteiPackage
{
    public SteiManifest Manifest { get; set; } = new();
    public Dictionary<string, List<Dictionary<string, JsonElement>>> Tables { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed record SteiResult(bool Success, bool Partial, long Rows, long FailedRows, int Tables, int FailedTables, IReadOnlyList<string> Errors, string Message, long SkippedDuplicates = 0);

    // ========== SteiCrypto.cs ==========
internal static class SteiCrypto
{
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int SaltSize = 16;
    private const int Iterations = 310_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public static (byte[] Cipher, byte[] Salt, byte[] Nonce, byte[] Tag) Encrypt(byte[] plain, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var key = DeriveKey(password, salt, Iterations);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plain, cipher, tag);
        CryptographicOperations.ZeroMemory(key);
        return (cipher, salt, nonce, tag);
    }

    public static byte[] Decrypt(byte[] cipher, string password, byte[] salt, byte[] nonce, byte[] tag)
    {
        var key = DeriveKey(password, salt, Iterations);
        var plain = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, cipher, tag, plain);
            return plain;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public static byte[] DeriveKey(string password, byte[] salt, int iterations)
        => Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, KeySize);

    public static RSA LoadSigningKey()
    {
        var dir = AppPaths.Security;
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "stei-signing.key");
        var rsa = RSA.Create(3072);
        if (File.Exists(path))
        {
            var protectedBlob = File.ReadAllBytes(path);
            var blob = ProtectedData.Unprotect(protectedBlob, null, DataProtectionScope.CurrentUser);
            rsa.ImportPkcs8PrivateKey(blob, out _);
            CryptographicOperations.ZeroMemory(blob);
            return rsa;
        }

        var pkcs8 = rsa.ExportPkcs8PrivateKey();
        var protectedKey = ProtectedData.Protect(pkcs8, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(path, protectedKey);
        CryptographicOperations.ZeroMemory(pkcs8);
        return rsa;
    }

    public static byte[] ExportPublicKey(RSA rsa) => rsa.ExportSubjectPublicKeyInfo();

    public static byte[] Sign(RSA rsa, byte[] data)
        => rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);

    public static bool Verify(byte[] publicKey, byte[] data, byte[] signature)
    {
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(publicKey, out _);
        return rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
    }

    public static string Fingerprint(byte[] publicKey)
        => Convert.ToHexString(SHA256.HashData(publicKey)).ToLowerInvariant();

    public static byte[] Serialize(object value) => JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
}

    // ========== SteiSvc.cs ==========
internal static class SteiSvc
{
    private const string Magic = "SHOUTECH-STEI\n";
    private static readonly JsonSerializerOptions ManifestJsonOptions = new() { WriteIndented = true };
    // Never exported: identity/security tables (companies, users, roles and
    // their permission bindings are installation-local and must never travel
    // between databases), the journal audit trail (regenerated by posting),
    // and engine bookkeeping (sqlite_sequence, schema_migrations — the
    // target's own migration state is a local fact, not company data).
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    {
        "sqlite_sequence", "schema_migrations", "companies", "users", "roles", "permissions", "user_roles", "role_permissions",
        "permission_groups", "user_permissions", "journal_audit_events"
    };

    public static async Task<SteiResult> ExportAsync(string outputPath, string password, IProgress<(long done, long total)>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("A package password is required.", nameof(password));
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("An output path is required.", nameof(outputPath));

        var provider = DatabaseRuntime.GetProvider();
        var cs = DatabaseRuntime.GetConnectionString() ?? throw new InvalidOperationException("The current database connection is not configured.");
        await using var db = CreateConnection(provider, cs);
        await db.OpenAsync(ct);

        var manifest = BuildManifest(provider);
        var package = new SteiPackage { Manifest = manifest };
        var tables = await GetTablesAsync(db, provider, ct);
        var total = 0L;
        var errors = new List<string>();

        foreach (var table in tables)
        {
            ct.ThrowIfCancellationRequested();
            if (Excluded.Contains(table))
                continue;
            // Step 10: a database that still carries a dropped legacy
            // table (pre-059/061/063, not yet migrated) must not export
            // it — the rows belong to a store the current schema
            // replaced, and re-importing them elsewhere would recreate
            // an orphan legacy table (Step 2 flag F-3).
            if (DataExchangeGuard.IsLegacyDroppedTable(table))
                continue;
            var columns = await GetColumnsAsync(db, provider, table, ct);
            if (columns.Count == 0)
                continue;
            var rows = await ReadRowsAsync(db, provider, table, columns, progress, total, ct);
            if (rows.Count == 0)
                continue;
            package.Tables[table] = rows;
            total += rows.Count;
        }

        manifest.TableCount = package.Tables.Count;
        manifest.RowCount = total;
        manifest.ExcludedTables = Excluded.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var payload = SteiCrypto.Serialize(package.Tables);
        manifest.PayloadHash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

        using var signingKey = SteiCrypto.LoadSigningKey();
        var publicKey = SteiCrypto.ExportPublicKey(signingKey);
        manifest.SigningKeyFingerprint = SteiCrypto.Fingerprint(publicKey);

        // Encryption metadata is part of the signed manifest. Populate it first.
        var encrypted = SteiCrypto.Encrypt(payload, password);
        manifest.Salt = Convert.ToBase64String(encrypted.Salt);
        manifest.Nonce = Convert.ToBase64String(encrypted.Nonce);
        manifest.Iterations = 310_000;

        var signatureData = BuildSignatureData(manifest, payload);
        manifest.Signature = Convert.ToBase64String(SteiCrypto.Sign(signingKey, signatureData));

        if (package.Tables.Count == 0)
        {
            errors.Add("No table rows were exported. Check that the database contains data and that the active company matches company_id values.");
            return new SteiResult(false, false, 0, 0, 0, 0, errors, "Export produced an empty package.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        await WriteContainerAsync(outputPath, manifest, encrypted.Cipher, encrypted.Tag, publicKey, ct);
        progress?.Report((total, total));
        return new SteiResult(true, false, total, 0, package.Tables.Count, 0, errors, "Export completed successfully.");
    }

    public static async Task<SteiResult> ExportDatasetAsync(
        string outputPath,
        string password,
        string datasetKey,
        string tableName,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        IReadOnlyList<string> fields,
        IProgress<(long done, long total)>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("A package password is required.", nameof(password));
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("An output path is required.", nameof(outputPath));
        if (string.IsNullOrWhiteSpace(datasetKey) || string.IsNullOrWhiteSpace(tableName))
            throw new ArgumentException("A dataset key and table name are required.");
        if (rows.Count == 0 || fields.Count == 0)
            throw new InvalidOperationException("The selected dataset has no exportable records.");

        var provider = DatabaseRuntime.GetProvider();
        var manifest = BuildManifest(provider);
        manifest.DataScope = $"Dataset:{datasetKey}";
        var tableRows = rows.Select(row => fields.ToDictionary(
            field => field,
            field => ToJsonElement(row.TryGetValue(field, out var value) ? value : null),
            StringComparer.OrdinalIgnoreCase)).ToList();
        var tables = new Dictionary<string, List<Dictionary<string, JsonElement>>>(StringComparer.OrdinalIgnoreCase)
        {
            [tableName] = tableRows
        };
        manifest.TableCount = 1;
        manifest.RowCount = tableRows.Count;
        var payload = SteiCrypto.Serialize(tables);
        manifest.PayloadHash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

        using var signingKey = SteiCrypto.LoadSigningKey();
        var publicKey = SteiCrypto.ExportPublicKey(signingKey);
        manifest.SigningKeyFingerprint = SteiCrypto.Fingerprint(publicKey);
        var encrypted = SteiCrypto.Encrypt(payload, password);
        manifest.Salt = Convert.ToBase64String(encrypted.Salt);
        manifest.Nonce = Convert.ToBase64String(encrypted.Nonce);
        manifest.Iterations = 310_000;
        manifest.Signature = Convert.ToBase64String(
            SteiCrypto.Sign(signingKey, BuildSignatureData(manifest, payload)));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        await WriteContainerAsync(outputPath, manifest, encrypted.Cipher, encrypted.Tag, publicKey, ct);
        progress?.Report((tableRows.Count, tableRows.Count));
        return new SteiResult(true, false, tableRows.Count, 0, 1, 0, Array.Empty<string>(), "Dataset export completed successfully.");
    }

    public static Task<SteiResult> ImportDatasetAsync(
        string inputPath,
        string password,
        string datasetKey,
        string tableName,
        bool simulation = false,
        IProgress<(long done, long total)>? progress = null,
        CancellationToken ct = default)
        => ImportCoreAsync(inputPath, password, simulation, datasetKey, tableName, enforceImportGate: true, progress, ct);

    public static Task<SteiResult> ImportAsync(
        string inputPath,
        string password,
        bool simulation,
        IProgress<(long done, long total)>? progress = null,
        CancellationToken ct = default)
        => ImportCoreAsync(inputPath, password, simulation, null, null, enforceImportGate: true, progress, ct);

    /// <summary>
    /// Backup-restore entry point (Step 10): identical engine, but the
    /// governing permission is BACKUP.RESTORE, enforced by BkpServ
    /// before it calls here — NOT DATA.IMPORT. A simulation run never
    /// writes, so it is ungated on every entry point.
    /// </summary>
    internal static Task<SteiResult> ImportForRestoreAsync(
        string inputPath,
        string password,
        IProgress<(long done, long total)>? progress = null,
        CancellationToken ct = default)
        => ImportCoreAsync(inputPath, password, simulation: false, null, null, enforceImportGate: false, progress, ct);

    private static async Task<SteiResult> ImportCoreAsync(
        string inputPath,
        string password,
        bool simulation,
        string? expectedDatasetKey,
        string? expectedTableName,
        bool enforceImportGate,
        IProgress<(long done, long total)>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("A package password is required.", nameof(password));
        // Step 10 (DX-03): service-layer DATA.IMPORT gate — runs before
        // the container is even opened, so a denied import writes
        // nothing and reads nothing.
        if (!simulation && enforceImportGate)
            DataExchangeGuard.RequireImportPermission(
                expectedDatasetKey == null ? "STEI full package" : $"STEI dataset package '{expectedDatasetKey}'");
        var container = await ReadContainerAsync(inputPath, ct);
        var manifest = container.Manifest;
        ValidateManifest(manifest);

        var publicKey = container.PublicKey;
        var fingerprint = SteiCrypto.Fingerprint(publicKey);
        if (!string.Equals(fingerprint, manifest.SigningKeyFingerprint, StringComparison.OrdinalIgnoreCase))
            throw new CryptographicException("The STEI signing key fingerprint does not match the manifest.");

        var payload = SteiCrypto.Decrypt(container.Cipher, password, Convert.FromBase64String(manifest.Salt), Convert.FromBase64String(manifest.Nonce), container.Tag);
        var hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(hash), Encoding.UTF8.GetBytes(manifest.PayloadHash)))
            throw new CryptographicException("The STEI payload hash does not match the manifest.");
        var signatureData = BuildSignatureData(manifest, payload);
        if (!SteiCrypto.Verify(publicKey, signatureData, Convert.FromBase64String(manifest.Signature)))
            throw new CryptographicException("The STEI digital signature is invalid.");

        var tables = JsonSerializer.Deserialize<Dictionary<string, List<Dictionary<string, JsonElement>>>>(payload)
                     ?? throw new InvalidDataException("The STEI payload is empty or invalid.");
        if (expectedDatasetKey == null)
        {
            if (manifest.DataScope.StartsWith("Dataset:", StringComparison.Ordinal))
                throw new InvalidDataException("This is a dataset-specific package. Select its matching dataset type before importing.");
        }
        else if (!string.Equals(manifest.DataScope, $"Dataset:{expectedDatasetKey}", StringComparison.Ordinal) ||
                 string.IsNullOrWhiteSpace(expectedTableName) ||
                 tables.Count != 1 ||
                 !tables.ContainsKey(expectedTableName))
        {
            throw new InvalidDataException("The encrypted package does not match the selected dataset type.");
        }

        // Step 10 (Step 2 flag F-3): govern the package against the
        // current schema. Tables dropped by the unification migrations
        // (accounts / customers / suppliers / bank_accounts) are SKIPPED
        // — never recreated, never counted as failures — and named in
        // the result message and the import audit row. Any other table
        // the target does not have keeps the honest failure behavior
        // below (the whole import rolls back).
        var skippedLegacyTables = new List<string>();
        foreach (var legacyName in tables.Keys.Where(DataExchangeGuard.IsLegacyDroppedTable).ToList())
        {
            skippedLegacyTables.Add($"{legacyName} ({tables[legacyName].Count} rows)");
            tables.Remove(legacyName);
        }

        var provider = DatabaseRuntime.GetProvider();
        var cs = DatabaseRuntime.GetConnectionString() ?? throw new InvalidOperationException("The target database connection is not configured.");
        await using var db = CreateConnection(provider, cs);
        await db.OpenAsync(ct);

        var targetTables = new HashSet<string>(await GetTablesAsync(db, provider, ct), StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        var done = 0L;
        var total = 0L;
        foreach (var rows in tables.Values)
            total += rows.Count;
        var failedRows = 0L;
        var failedTables = 0;

        if (simulation)
        {
            foreach (var pair in tables)
            {
                ct.ThrowIfCancellationRequested();
                if (!targetTables.Contains(pair.Key))
                {
                    failedTables++;
                    errors.Add($"Table not found in target database: {pair.Key}");
                    continue;
                }
                var columns = await GetColumnsAsync(db, provider, pair.Key, ct);
                foreach (var row in pair.Value)
                {
                    if (row.Keys.Any(k => !columns.Contains(k, StringComparer.OrdinalIgnoreCase)))
                    {
                        failedRows++;
                        errors.Add($"Column mismatch in table {pair.Key}.");
                    }
                    done++;
                    progress?.Report((done, total));
                }
            }
            var partialSim = failedRows > 0 || failedTables > 0;
            var simMessage = partialSim ? "Simulation found issues." : "Simulation completed successfully.";
            if (skippedLegacyTables.Count > 0)
                simMessage += $" Legacy tables skipped (dropped by the current schema): {string.Join(", ", skippedLegacyTables)}.";
            return new SteiResult(!partialSim, partialSim, total, failedRows, tables.Count, failedTables, errors, simMessage);
        }

        // FK-safe insertion order: parents before children, derived from the
        // TARGET database's foreign-key graph. (The package's alphabetical
        // table order is NOT insertable: e.g. fiscal_periods sorts before
        // fiscal_years, sales_document_lines before sales_documents.)
        var orderedTables = await OrderTablesForImportAsync(db, provider, tables.Keys, ct);
        var insertedRows = 0L;
        var skippedDuplicates = 0L;

        await using var tx = await db.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            // The journal posting triggers are workflow guards for
            // interactive posting (an entry must be created as a draft and
            // posted through the application so its lines, accounts and
            // period are validated at post time). A package restore
            // re-inserts history that was already validated when it was
            // originally posted; leaving the guards active makes every
            // posted entry uninsertable and therefore makes restore
            // impossible. They are suspended for the duration of THIS
            // transaction only and restored before commit — DDL is
            // transactional on both providers, so a rolled-back import
            // leaves the target schema untouched.
            var suspendedTriggers = await SuspendJournalPostingTriggersAsync(db, tx, provider, ct);
            try
            {
                var pending = new List<PendingRow>();
                foreach (var tableName in orderedTables)
                {
                    ct.ThrowIfCancellationRequested();
                    var rows = tables[tableName];
                    if (!targetTables.Contains(tableName))
                    {
                        failedTables++;
                        errors.Add($"Table not found in target database: {tableName}");
                        continue;
                    }
                    var columns = await GetColumnsAsync(db, provider, tableName, ct);
                    if (columns.Count == 0)
                    {
                        failedTables++;
                        errors.Add($"No compatible columns found: {tableName}");
                        continue;
                    }

                    foreach (var row in rows)
                    {
                        try
                        {
                            var valid = row.Keys.All(k => columns.Contains(k, StringComparer.OrdinalIgnoreCase));
                            if (!valid) throw new InvalidDataException($"Column mismatch in {tableName}.");
                            RemapCompanyId(row, manifest.SourceCompanyId, AppState.CurrentUser?.CompanyId);
                            var affected = await InsertIgnoreAsync(db, tx, provider, tableName, row, columns, ct);
                            if (affected > 0) insertedRows++; else skippedDuplicates++;
                        }
                        catch (Exception ex) when (IsForeignKeyViolation(ex))
                        {
                            // Parent row may land later (self-referencing
                            // tables, residual ordering edges): retry after
                            // the first pass instead of failing the row.
                            pending.Add(new PendingRow(tableName, row, columns, ex.Message));
                        }
                        catch (Exception ex)
                        {
                            failedRows++;
                            errors.Add($"{tableName}: {ex.Message}");
                        }
                        done++;
                        progress?.Report((done, total));
                    }
                }

                // Retry passes: each pass inserts the rows whose parents have
                // since landed. Ends when a full pass resolves nothing.
                while (pending.Count > 0)
                {
                    var remaining = new List<PendingRow>();
                    var resolved = 0;
                    foreach (var item in pending)
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            var affected = await InsertIgnoreAsync(db, tx, provider, item.Table, item.Row, item.Columns, ct);
                            if (affected > 0) insertedRows++; else skippedDuplicates++;
                            resolved++;
                        }
                        catch (Exception ex) when (IsForeignKeyViolation(ex))
                        {
                            remaining.Add(item with { LastError = ex.Message });
                        }
                        catch (Exception ex)
                        {
                            failedRows++;
                            errors.Add($"{item.Table}: {ex.Message}");
                            resolved++;
                        }
                    }
                    pending = remaining;
                    if (resolved == 0)
                        break;
                }

                foreach (var item in pending)
                {
                    failedRows++;
                    errors.Add($"{item.Table}: {item.LastError}");
                }
            }
            finally
            {
                await RestoreJournalPostingTriggersAsync(db, tx, provider, suspendedTriggers, ct);
            }

            if (failedRows > 0 || failedTables > 0)
            {
                await tx.RollbackAsync(ct);
            }
            else
            {
                // Step 10 (DX-10): PostgreSQL identity sequences are not
                // advanced by explicit-id inserts, so the next ordinary
                // insert into an imported table could collide with an
                // imported id. Advance every imported table's sequence
                // past its max id, inside the same transaction. A repair
                // failure is reported, never silently swallowed, and does
                // not roll back an otherwise-valid import.
                if (DatabaseRuntime.IsPostgreSql(provider))
                {
                    try { await RepairPostgresSequencesAsync(db, tx, orderedTables, ct); }
                    catch (Exception exSeq)
                    {
                        AppHost.Errors.Report(ErrorCodes.ImportFailed, exSeq, "SteiSvc.RepairPostgresSequencesAsync");
                        errors.Add($"Sequence repair failed: {exSeq.Message}");
                    }
                }
                await tx.CommitAsync(ct);
            }
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }

        var partial = failedRows > 0 || failedTables > 0;
        var message = partial
            ? "Import completed with issues; no database changes were committed."
            : skippedDuplicates > 0
                ? $"Import completed successfully. {skippedDuplicates} row(s) already existed and were skipped — import never overwrites existing rows."
                : "Import completed successfully.";
        if (skippedLegacyTables.Count > 0)
            message += $" Legacy tables skipped (dropped by the current schema): {string.Join(", ", skippedLegacyTables)}.";
        // Step 10 (DX-06): durable security-audit row for every import —
        // who, which package scope, honest counts, legacy skips.
        await DataExchangeGuard.WriteImportAuditAsync(
            $"operation={(expectedDatasetKey == null ? "STEI full package" : $"STEI dataset package '{expectedDatasetKey}'")} | sourceCompany={manifest.SourceCompanyCode} | tables={tables.Count} | inserted={(partial ? 0 : insertedRows)} | skippedDuplicates={skippedDuplicates} | failedRows={failedRows} | failedTables={failedTables} | legacySkipped={(skippedLegacyTables.Count == 0 ? "-" : string.Join(", ", skippedLegacyTables))} | partial={partial}");
        // Rows reports rows actually inserted by THIS import (0 when the
        // transaction rolled back) — never the package's row count.
        return new SteiResult(!partial, partial, partial ? 0 : insertedRows, failedRows, tables.Count, failedTables, errors, message, skippedDuplicates);
    }

    /// <summary>A row whose insert is deferred because a foreign-key parent has not landed yet.</summary>
    private sealed record PendingRow(string Table, Dictionary<string, JsonElement> Row, List<string> Columns, string LastError);

    private static bool IsForeignKeyViolation(Exception ex) =>
        ex is PostgresException pg && pg.SqlState == "23503"
        || ex.Message.Contains("FOREIGN KEY constraint failed", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("violates foreign key constraint", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Orders package tables so that referenced (parent) tables are inserted
    /// before the tables that reference them, using the target database's
    /// own foreign-key metadata. Self-references are ignored here — the
    /// multi-pass inserter resolves them. Alphabetical tie-breaks keep the
    /// order deterministic; a dependency cycle falls back to alphabetical
    /// order for the remaining tables.
    /// </summary>
    private static async Task<List<string>> OrderTablesForImportAsync(DbConnection db, string provider, IEnumerable<string> packageTables, CancellationToken ct)
    {
        var tables = packageTables.ToList();
        var inPackage = new HashSet<string>(tables, StringComparer.OrdinalIgnoreCase);
        var parents = tables.ToDictionary(
            t => t,
            _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

        if (DatabaseRuntime.IsSqlite(provider))
        {
            foreach (var table in tables)
            {
                await using var cmd = db.CreateCommand();
                cmd.CommandText = $"PRAGMA foreign_key_list({Quote(table)});";
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    // foreign_key_list columns: id, seq, table, from, to, ...
                    var parent = reader.GetString(2);
                    if (inPackage.Contains(parent) && !string.Equals(parent, table, StringComparison.OrdinalIgnoreCase))
                        parents[table].Add(parent);
                }
            }
        }
        else if (DatabaseRuntime.IsPostgreSql(provider))
        {
            await using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT tc.table_name, ccu.table_name
                FROM information_schema.table_constraints tc
                JOIN information_schema.constraint_column_usage ccu
                  ON ccu.constraint_schema = tc.constraint_schema
                 AND ccu.constraint_name = tc.constraint_name
                WHERE tc.constraint_type = 'FOREIGN KEY' AND tc.table_schema = 'public';
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var child = reader.GetString(0);
                var parent = reader.GetString(1);
                if (parents.TryGetValue(child, out var set) && inPackage.Contains(parent) &&
                    !string.Equals(parent, child, StringComparison.OrdinalIgnoreCase))
                    set.Add(parent);
            }
        }

        var ordered = new List<string>();
        var remaining = new HashSet<string>(tables, StringComparer.OrdinalIgnoreCase);
        while (remaining.Count > 0)
        {
            var ready = remaining
                .Where(t => parents[t].All(p => !remaining.Contains(p)))
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (ready.Count == 0)
            {
                // Dependency cycle between distinct tables (not present in
                // this schema): emit one table alphabetically and continue;
                // the multi-pass inserter resolves what it can.
                ready = remaining.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).Take(1).ToList();
            }
            foreach (var t in ready)
            {
                ordered.Add(t);
                remaining.Remove(t);
            }
        }
        return ordered;
    }

    /// <summary>
    /// Suspends the journal posting-guard triggers for the import
    /// transaction (see the call site for the rationale). SQLite: captures
    /// each trigger's definition and drops it; the definitions are returned
    /// for restoration. PostgreSQL: DISABLE TRIGGER USER on journal_entries.
    /// </summary>
    private static async Task<List<(string Name, string Sql)>> SuspendJournalPostingTriggersAsync(DbConnection db, DbTransaction tx, string provider, CancellationToken ct)
    {
        var suspended = new List<(string Name, string Sql)>();
        if (DatabaseRuntime.IsSqlite(provider))
        {
            await using (var cmd = db.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT name, sql FROM sqlite_master WHERE type='trigger' AND tbl_name='journal_entries' AND sql IS NOT NULL;";
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    suspended.Add((reader.GetString(0), reader.GetString(1)));
            }
            foreach (var (name, _) in suspended)
            {
                await using var drop = db.CreateCommand();
                drop.Transaction = tx;
                drop.CommandText = $"DROP TRIGGER IF EXISTS {Quote(name)};";
                await drop.ExecuteNonQueryAsync(ct);
            }
        }
        else if (DatabaseRuntime.IsPostgreSql(provider))
        {
            await using var cmd = db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "ALTER TABLE \"journal_entries\" DISABLE TRIGGER USER;";
            await cmd.ExecuteNonQueryAsync(ct);
        }
        return suspended;
    }

    /// <summary>Restores the triggers suspended by <see cref="SuspendJournalPostingTriggersAsync"/>.</summary>
    private static async Task RestoreJournalPostingTriggersAsync(DbConnection db, DbTransaction tx, string provider, List<(string Name, string Sql)> suspended, CancellationToken ct)
    {
        if (DatabaseRuntime.IsSqlite(provider))
        {
            foreach (var (_, sql) in suspended)
            {
                await using var cmd = db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }
        else if (DatabaseRuntime.IsPostgreSql(provider))
        {
            await using var cmd = db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "ALTER TABLE \"journal_entries\" ENABLE TRIGGER USER;";
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static SteiManifest BuildManifest(string provider)
    {
        var now = DateTimeOffset.Now;
        var user = AppState.CurrentUser;
        return new SteiManifest
        {
            ExportedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            ExportedLocal = now.ToString("O"),
            ExportedByUserId = user?.Id ?? "",
            ExportedByUserName = !string.IsNullOrWhiteSpace(user?.FullName) ? user!.FullName : user?.Username ?? Environment.UserName,
            SourceCompanyId = user?.CompanyId ?? "",
            SourceCompanyCode = AppState.CurrentUser?.CompanyId ?? "",
            SourceCompanyName = AppState.CurrentCompanyDisplayName,
            SourceDatabaseName = GetDatabaseName(provider),
            SourceDatabaseProvider = provider,
            TargetDatabaseName = GetDatabaseName(provider),
            TargetDatabaseProvider = provider,
            ApplicationVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "",
            DataScope = "CompanyData"
        };
    }

    private static byte[] BuildSignatureData(SteiManifest manifest, byte[] payload)
    {
        var original = manifest.Signature;
        manifest.Signature = "";
        var json = SteiCrypto.Serialize(manifest);
        manifest.Signature = original;
        return SHA256.HashData(json.Concat(SHA256.HashData(payload)).ToArray());
    }

    private static async Task WriteContainerAsync(string path, SteiManifest manifest, byte[] cipher, byte[] tag, byte[] publicKey, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: true);
        await WriteTextAsync(zip, "manifest.json", JsonSerializer.Serialize(manifest, ManifestJsonOptions), ct);
        await WriteBytesAsync(zip, "payload.bin", cipher, ct);
        await WriteBytesAsync(zip, "payload.tag", tag, ct);
        await WriteBytesAsync(zip, "signing-public.key", publicKey, ct);
        await WriteTextAsync(zip, "format.txt", Magic, ct);
    }

    private static async Task<(SteiManifest Manifest, byte[] Cipher, byte[] Tag, byte[] PublicKey)> ReadContainerAsync(string path, CancellationToken ct)
	{
		var ext = Path.GetExtension(path);
		if (!string.Equals(ext, ".stei", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(ext, ".stb", StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException(
            "Only .stei or .stb files are accepted by the ShouTech data exchange engine.");

    using var zip = ZipFile.OpenRead(path);
        var format = await ReadTextAsync(zip, "format.txt", ct);
        if (!string.Equals(format, Magic, StringComparison.Ordinal))
            throw new InvalidDataException("The selected file is not a valid ShouTech STEI package.");
        var manifest = JsonSerializer.Deserialize<SteiManifest>(await ReadTextAsync(zip, "manifest.json", ct)) ?? throw new InvalidDataException("Missing STEI manifest.");
        return (manifest, await ReadBytesAsync(zip, "payload.bin", ct), await ReadBytesAsync(zip, "payload.tag", ct), await ReadBytesAsync(zip, "signing-public.key", ct));
    }

    private static void ValidateManifest(SteiManifest m)
    {
        if (!string.Equals(m.Format, "SHOUTECH-STEI", StringComparison.Ordinal)) throw new InvalidDataException("Invalid STEI format.");
        if (m.FormatVersion != 1) throw new InvalidDataException($"Unsupported STEI format version: {m.FormatVersion}.");
        if (m.SchemaVersion != 1) throw new InvalidDataException($"Unsupported STEI schema version: {m.SchemaVersion}.");
        if (!string.Equals(m.EncryptionAlgorithm, "AES-256-GCM", StringComparison.Ordinal)) throw new CryptographicException("Unsupported STEI encryption.");
        if (!string.Equals(m.SignatureAlgorithm, "RSA-PSS-SHA256", StringComparison.Ordinal)) throw new CryptographicException("Unsupported STEI signature algorithm.");
    }

    private static DbConnection CreateConnection(string provider, string connectionString)
        => DatabaseRuntime.IsSqlite(provider) ? new SqliteConnection(DatabaseRuntime.NormalizeSqliteConnectionString(connectionString)) : new NpgsqlConnection(connectionString);

    private static string GetDatabaseName(string provider)
    {
        try
        {
            var cs = DatabaseRuntime.GetConnectionString();
            if (DatabaseRuntime.IsSqlite(provider)) return Path.GetFileName(DatabaseRuntime.GetSqliteDataSource(cs) ?? "-");
            if (DatabaseRuntime.IsPostgreSql(provider)) return new NpgsqlConnectionStringBuilder(cs).Database ?? "-";
        }
        catch (Exception ex) { AppHost.Logging?.LogError("SteiSvc", "Database name resolution failed", ex); }
        return "-";
    }

    private static async Task<List<string>> GetTablesAsync(DbConnection db, string provider, CancellationToken ct)
    {
        var sql = DatabaseRuntime.IsSqlite(provider)
            ? "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;"
            : "SELECT table_name FROM information_schema.tables WHERE table_schema='public' AND table_type='BASE TABLE' ORDER BY table_name;";
        return await ReadSingleColumnAsync(db, sql, ct);
    }

    private static async Task<List<string>> GetColumnsAsync(DbConnection db, string provider, string table, CancellationToken ct)
    {
        var sql = DatabaseRuntime.IsSqlite(provider)
            ? "PRAGMA table_info(\"" + table.Replace("\"", "\"\"") + "\");"
            : "SELECT column_name FROM information_schema.columns WHERE table_schema='public' AND table_name=@table ORDER BY ordinal_position;";
        await using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        if (!DatabaseRuntime.IsSqlite(provider)) AddParameter(cmd, "@table", table);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var result = new List<string>();
        while (await reader.ReadAsync(ct)) result.Add(reader.GetString(DatabaseRuntime.IsSqlite(provider) ? 1 : 0));
        return result;
    }

    private static async Task<List<Dictionary<string, JsonElement>>> ReadRowsAsync(DbConnection db, string provider, string table, List<string> columns, IProgress<(long done, long total)>? progress, long done, CancellationToken ct)
    {
        var qtable = Quote(table);
        var hasCompanyId = columns.Contains("company_id", StringComparer.OrdinalIgnoreCase);

        // Prefer company code from shell state; CompanyId on the user is often the same code.
        var codeValue = AppState.CurrentCompanyCode?.Trim();
        if (string.IsNullOrWhiteSpace(codeValue))
            codeValue = AppState.CurrentUser?.CompanyId?.Trim();
        var guidValue = AppState.CurrentUser?.CompanyId?.Trim();

        string where = "";
        if (hasCompanyId)
        {
            // CAST to text so INTEGER/TEXT company_id both match the active company code.
            if (!string.IsNullOrWhiteSpace(codeValue) && !string.IsNullOrWhiteSpace(guidValue) &&
                !string.Equals(codeValue, guidValue, StringComparison.OrdinalIgnoreCase))
            {
                where = " WHERE (CAST(company_id AS TEXT)=@companyCode OR CAST(company_id AS TEXT)=@companyGuid)";
            }
            else if (!string.IsNullOrWhiteSpace(codeValue))
            {
                where = " WHERE CAST(company_id AS TEXT)=@companyCode";
            }
            else if (!string.IsNullOrWhiteSpace(guidValue))
            {
                where = " WHERE CAST(company_id AS TEXT)=@companyGuid";
            }
        }

        // Deliberately NO fallback to an unfiltered read: when the company
        // filter matches zero rows, the active company simply has no rows in
        // this table. Re-reading without the filter (the previous behavior)
        // silently exported OTHER companies' rows into this company's
        // package whenever a table contained only foreign-company data —
        // a cross-company data leak that also made the package unimportable
        // (the leaked rows reference a company that does not exist in the
        // target database and fail its foreign keys).
        if (!hasCompanyId)
        {
            // Step 10: child tables without a company_id of their own
            // (document lines, journal lines, order lines, project
            // tasks, ...) are scoped THROUGH their parent: a line row
            // travels only when its parent document belongs to the
            // active company. Before, these tables traveled whole — the
            // other companies' lines leaked into the package and then
            // failed their foreign keys on import (their parents are
            // company-filtered out), rolling the whole import back.
            // Tables with no company-scoped parent are global reference
            // data and still travel whole.
            var childWhere = await BuildChildScopeWhereAsync(db, provider, table, guidValue, codeValue, ct);
            if (childWhere != null)
                where = childWhere;
        }
        return await ExecuteSelectRowsAsync(db, qtable, where, guidValue, codeValue, progress, done, ct);
    }

    /// <summary>
    /// Builds a WHERE clause scoping a company-less child table through
    /// every single-column foreign key whose parent table carries a
    /// company_id. Returns null when the table has no such parent (a
    /// genuinely global table). The parameter names match
    /// <see cref="ExecuteSelectRowsAsync"/>'s binding convention.
    /// </summary>
    private static async Task<string?> BuildChildScopeWhereAsync(DbConnection db, string provider, string table, string? guidValue, string? codeValue, CancellationToken ct)
    {
        var foreignKeys = new List<(string From, string ParentTable, string ParentKey)>();
        if (DatabaseRuntime.IsSqlite(provider))
        {
            await using var cmd = db.CreateCommand();
            cmd.CommandText = $"PRAGMA foreign_key_list({Quote(table)});";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var byId = new Dictionary<long, List<(string From, string ParentTable, string ParentKey)>>();
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetInt64(0);
                if (!byId.TryGetValue(id, out var group))
                    byId[id] = group = new List<(string, string, string)>();
                group.Add((reader.GetString(3), reader.GetString(2), reader.IsDBNull(4) ? "" : reader.GetString(4)));
            }
            foreach (var group in byId.Values)
                if (group.Count == 1 && !string.IsNullOrWhiteSpace(group[0].ParentKey))
                    foreignKeys.Add(group[0]);
        }
        else if (DatabaseRuntime.IsPostgreSql(provider))
        {
            await using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT kcu.column_name, ccu.table_name, ccu.column_name
                FROM information_schema.table_constraints tc
                JOIN information_schema.key_column_usage kcu
                  ON tc.constraint_name = kcu.constraint_name AND tc.table_schema = kcu.table_schema
                JOIN information_schema.constraint_column_usage ccu
                  ON ccu.constraint_schema = tc.constraint_schema AND ccu.constraint_name = tc.constraint_name
                WHERE tc.constraint_type = 'FOREIGN KEY' AND tc.table_schema = 'public' AND tc.table_name = @table;
                """;
            AddParameter(cmd, "@table", table);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                foreignKeys.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        var predicates = new List<string>();
        foreach (var (from, parentTable, parentKey) in foreignKeys)
        {
            if (string.Equals(parentTable, table, StringComparison.OrdinalIgnoreCase))
                continue;
            var parentColumns = await GetColumnsAsync(db, provider, parentTable, ct);
            if (!parentColumns.Contains("company_id", StringComparer.OrdinalIgnoreCase))
                continue;
            var parentPredicate = "";
            if (!string.IsNullOrWhiteSpace(codeValue) && !string.IsNullOrWhiteSpace(guidValue) &&
                !string.Equals(codeValue, guidValue, StringComparison.OrdinalIgnoreCase))
                parentPredicate = "(CAST(company_id AS TEXT)=@companyCode OR CAST(company_id AS TEXT)=@companyGuid)";
            else if (!string.IsNullOrWhiteSpace(codeValue))
                parentPredicate = "CAST(company_id AS TEXT)=@companyCode";
            else if (!string.IsNullOrWhiteSpace(guidValue))
                parentPredicate = "CAST(company_id AS TEXT)=@companyGuid";
            if (parentPredicate.Length == 0)
                continue;
            predicates.Add($"CAST({Quote(from)} AS TEXT) IN (SELECT CAST({Quote(parentKey)} AS TEXT) FROM {Quote(parentTable)} WHERE {parentPredicate})");
        }
        return predicates.Count == 0 ? null : " WHERE " + string.Join(" AND ", predicates);
    }

    private static async Task<List<Dictionary<string, JsonElement>>> ExecuteSelectRowsAsync(
        DbConnection db,
        string qtable,
        string where,
        string? guidValue,
        string? codeValue,
        IProgress<(long done, long total)>? progress,
        long done,
        CancellationToken ct)
    {
        await using var cmd = db.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {qtable}{where};";
        if (where.Contains("@companyGuid", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(guidValue))
            AddParameter(cmd, "@companyGuid", guidValue);
        if (where.Contains("@companyCode", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(codeValue))
            AddParameter(cmd, "@companyCode", codeValue);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<Dictionary<string, JsonElement>>();
        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = ToJsonElement(reader.IsDBNull(i) ? null : reader.GetValue(i));
            rows.Add(row);
            progress?.Report((done + rows.Count, 0));
        }
        return rows;
    }


    private static void RemapCompanyId(Dictionary<string, JsonElement> row, string sourceCompanyId, string? targetCompanyId)
    {
        if (string.IsNullOrWhiteSpace(targetCompanyId) || string.IsNullOrWhiteSpace(sourceCompanyId))
            return;
        var key = row.Keys.FirstOrDefault(k => string.Equals(k, "company_id", StringComparison.OrdinalIgnoreCase));
        if (key is null || row[key].ValueKind != JsonValueKind.String)
            return;
        if (string.Equals(row[key].GetString(), sourceCompanyId, StringComparison.OrdinalIgnoreCase))
            row[key] = JsonSerializer.SerializeToElement(targetCompanyId);
    }

    /// <summary>
    /// Advances PostgreSQL identity sequences past the ids an import just
    /// inserted (DX-10). For every imported table, each column whose
    /// default is a sequence (identity / serial) is set to
    /// GREATEST(current sequence value, max(id), 1). Tables without a
    /// sequence-backed column are untouched. PostgreSQL only.
    /// </summary>
    private static async Task RepairPostgresSequencesAsync(DbConnection db, DbTransaction tx, List<string> importedTables, CancellationToken ct)
    {
        foreach (var table in importedTables)
        {
            ct.ThrowIfCancellationRequested();
            var sequenceColumns = new List<string>();
            await using (var probe = db.CreateCommand())
            {
                probe.Transaction = tx;
                probe.CommandText = """
                    SELECT column_name FROM information_schema.columns
                    WHERE table_schema = 'public' AND table_name = @table
                      AND column_default LIKE 'nextval(%'
                    """;
                AddParameter(probe, "@table", table);
                await using var reader = await probe.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    sequenceColumns.Add(reader.GetString(0));
            }
            foreach (var column in sequenceColumns)
            {
                await using var fix = db.CreateCommand();
                fix.Transaction = tx;
                fix.CommandText = $"""
                    SELECT setval(pg_get_serial_sequence(@table, @column),
                                  GREATEST((SELECT COALESCE(MAX({Quote(column)}), 1) FROM {Quote(table)}), 1))
                    """;
                AddParameter(fix, "@table", table);
                AddParameter(fix, "@column", column);
                await fix.ExecuteScalarAsync(ct);
            }
        }
    }

    /// <summary>
    /// Inserts one row, ignoring primary/unique-key conflicts (import never
    /// overwrites existing rows). Returns the affected-row count so callers
    /// can distinguish an inserted row (1) from a skipped duplicate (0) and
    /// report both honestly.
    /// </summary>
    private static async Task<int> InsertIgnoreAsync(DbConnection db, DbTransaction tx, string provider, string table, Dictionary<string, JsonElement> row, List<string> columns, CancellationToken ct)
    {
        var selected = columns.Where(c => row.ContainsKey(c)).ToList();
        if (selected.Count == 0) return 0;
        var names = string.Join(",", selected.Select(Quote));
        var pars = string.Join(",", selected.Select((_, i) => "@p" + i));
        var prefix = DatabaseRuntime.IsSqlite(provider) ? "INSERT OR IGNORE" : "INSERT";
        var suffix = DatabaseRuntime.IsPostgreSql(provider) ? " ON CONFLICT DO NOTHING" : "";
        await using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"{prefix} INTO {Quote(table)} ({names}) VALUES ({pars}){suffix};";
        for (var i = 0; i < selected.Count; i++) AddParameter(cmd, "@p" + i, FromJson(row[selected[i]]));
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static JsonElement ToJsonElement(object? value)
    {
        if (value is byte[] bytes) return JsonSerializer.SerializeToElement(new { __binary = Convert.ToBase64String(bytes) });
        return JsonSerializer.SerializeToElement(value);
    }

    private static object? FromJson(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Null) return DBNull.Value;
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("__binary", out var b)) return Convert.FromBase64String(b.GetString() ?? "");
        return e.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when e.TryGetInt64(out var l) => l,
            JsonValueKind.Number when e.TryGetDecimal(out var d) => d,
            JsonValueKind.String => e.GetString(),
            _ => e.GetRawText()
        };
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
    private static void AddParameter(DbCommand cmd, string name, object? value) { var p = cmd.CreateParameter(); p.ParameterName = name; p.Value = value ?? DBNull.Value; cmd.Parameters.Add(p); }

    private static async Task<List<string>> ReadSingleColumnAsync(DbConnection db, string sql, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(); cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<string>();
        while (await reader.ReadAsync(ct)) list.Add(reader.GetString(0));
        return list;
    }

    private static async Task WriteTextAsync(ZipArchive zip, string name, string text, CancellationToken ct) => await WriteBytesAsync(zip, name, Encoding.UTF8.GetBytes(text), ct);
    private static async Task WriteBytesAsync(ZipArchive zip, string name, byte[] data, CancellationToken ct)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        await using var s = entry.Open();
        await s.WriteAsync(data, ct);
    }
    private static async Task<string> ReadTextAsync(ZipArchive zip, string name, CancellationToken ct) => Encoding.UTF8.GetString(await ReadBytesAsync(zip, name, ct));
    private static async Task<byte[]> ReadBytesAsync(ZipArchive zip, string name, CancellationToken ct)
    {
        var entry = zip.GetEntry(name) ?? throw new InvalidDataException($"Missing STEI entry: {name}");
        await using var s = entry.Open();
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms, ct);
        return ms.ToArray();
    }
}

    // ========== BackupKeyProvider.cs ==========

    internal static class BackupKeyProvider
    {
        private static readonly object Sync = new();
        private static string? _cachedPassword;

        private static string KeyFilePath
        {
            get
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "ShouTech", "sec");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "bkpkey.dat");
            }
        }

        public static string GetPackagePassword()
        {
            if (_cachedPassword is not null)
                return _cachedPassword;

            lock (Sync)
            {
                if (_cachedPassword is not null)
                    return _cachedPassword;

                var path = KeyFilePath;
                byte[] rawKey;

                if (File.Exists(path))
                {
                    try
                    {
                        var protectedBlob = Convert.FromBase64String(File.ReadAllText(path).Trim());
                        rawKey = ProtectedData.Unprotect(protectedBlob, null, DataProtectionScope.CurrentUser);
                        if (rawKey.Length != 32)
                            throw new InvalidDataException("Corrupted backup key material.");
                    }
                    catch (Exception ex) when (ex is CryptographicException or FormatException or InvalidDataException)
                    {
                        // Stored key material is unreadable/corrupt: report
                        // it, then recover by generating a fresh key.
                        AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, ex, "Backup.GetPackagePassword");
                        rawKey = GenerateAndStore(path);
                    }
                }
                else
                {
                    rawKey = GenerateAndStore(path);
                }

                _cachedPassword = Convert.ToBase64String(rawKey);
                CryptographicOperations.ZeroMemory(rawKey);
                return _cachedPassword;
            }
        }

        private static byte[] GenerateAndStore(string path)
        {
            var rawKey = RandomNumberGenerator.GetBytes(32);
            var protectedBlob = ProtectedData.Protect(rawKey, null, DataProtectionScope.CurrentUser);

          
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, Convert.ToBase64String(protectedBlob));
            File.Move(tmp, path, overwrite: true);

            try { File.SetAttributes(path, FileAttributes.Hidden); } catch (Exception ex) { AppHost.Logging?.LogError("BackupKeyProvider", "Failed to mark the backup key file hidden", ex); }
            return rawKey;
        }
    }

    // ========== BkpServ.cs ==========

    public sealed class BkpServ : IBkpServ
    {
        private readonly IPermServ? _perm;
        private readonly ILogServ _log;
        private readonly IDevInfoSv? _devInfo;
        private readonly ISyncServ? _sync;

        public BkpServ(ILogServ log, IDevInfoSv devInfo, ISyncServ sync, IPermServ? perm = null)
        {
            _log = log;
            _devInfo = devInfo;
            _sync = sync;
            // The live AppHost instance passes the session permission
            // service here; without it the permission checks below were
            // dead in production (the old 3-argument construction left
            // _perm null, so any signed-in user could restore a backup
            // over the company database).
            _perm = perm;
        }

        public Task<string> CreateBackupAsync(string companyCode, string notes)
            => CreateBackupCoreAsync(companyCode, notes, CancellationToken.None);

        public Task<string> CreateBackupAsync(string companyCode, CancellationToken ct = default)
            => CreateBackupCoreAsync(companyCode, string.Empty, ct);

        // ═══════════════════════════════════════════════════════════
        //   shouteck_backup_dd-MM-yyyy_HH-mm_<CompanyCode>_<Note>.stb
        // ═══════════════════════════════════════════════════════════
        private async Task<string> CreateBackupCoreAsync(
            string companyCode,
            string notes,
            CancellationToken ct)
        {
            // Licensing build (telemetry study §7.4): creating a
            // local backup stays ALLOWED in view-only/demo mode —
            // the customer's data is always theirs to protect. The
            // retired fake gate (LicServ) is gone; restore is the
            // state-changing operation and is gated below.
            if (_perm != null && !_perm.Has(PermCat.Backup.Create))
                throw new UnauthorizedAccessException("You do not have permission to create backups (BACKUP.CREATE).");

            if (string.IsNullOrWhiteSpace(companyCode))
                throw new ArgumentException("Company code is required.");

            var root = AppPaths.CompanyBackup(companyCode);
            Directory.CreateDirectory(root);

           
            var fileName = BuildBackupFileName(companyCode, notes);
            var fullPath = Path.Combine(root, fileName);

            var password = BackupKeyProvider.GetPackagePassword();
            var tmpPath = fullPath + ".tmp";

            try
            {
                var result = await SteiSvc.ExportAsync(tmpPath, password, progress: null, ct);
                if (!result.Success)
                {
                    _log.LogError("Backup", $"Export failed: {result.Message}");
                    throw new InvalidOperationException($"Backup export failed: {result.Message}");
                }

               
                File.Move(tmpPath, fullPath, overwrite: true);
                PruneOldBackups(root);

                _log.LogInfo("Backup",
                    $"Encrypted backup created: {fullPath} " +
                    $"({result.Tables} tables, {result.Rows} rows, AES-256-GCM, signed)");
                return fullPath;
            }
            finally
            {
               
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch (Exception ex) { _log.LogError("Backup", "Temporary backup file cleanup failed", ex); }
            }
        }

        private static string BuildBackupFileName(string companyCode, string notes)
        {
            var now = DateTime.Now;

            
            var stamp = now.ToString("dd-MM-yyyy_HH-mm", CultureInfo.InvariantCulture);

            var safeCompany = SanitizeFileNameSegment(companyCode, maxLength: 20);
            var safeNotes   = SanitizeFileNameSegment(notes,        maxLength: 40);

            if (string.IsNullOrWhiteSpace(safeCompany))
                safeCompany = "company";

            if (string.IsNullOrWhiteSpace(safeNotes))
                safeNotes = "manual";

            return $"shouteck_backup_{stamp}_{safeCompany}_{safeNotes}.stb";
        }

   
        private static string SanitizeFileNameSegment(string? input, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(input))
                return string.Empty;

            var invalid = Path.GetInvalidFileNameChars();

            var sb = new StringBuilder(input.Length);
            foreach (var c in input.Trim())
            {
                if (Array.IndexOf(invalid, c) >= 0) continue;
                if (c == ' ' || c == '\t') continue;     
                if (c == '.' || c == '"') continue;      
                sb.Append(c);
            }

            var cleaned = sb.ToString();
            if (cleaned.Length > maxLength)
                cleaned = cleaned.Substring(0, maxLength);

            return cleaned;
        }

        public async Task RestoreBackupAsync(string backupFilePath, CancellationToken ct = default)
        {
            // Restore replaces the entire database — a state-
            // changing operation, refused in view-only/demo mode by
            // the one entitlement gate (LIC-8008/LIC-8003 family).
            AppHost.Entitlements?.EnsureCanWrite();

            if (_perm != null && !_perm.Has(PermCat.Backup.Restore))
                throw new UnauthorizedAccessException("You do not have permission to restore backups (BACKUP.RESTORE).");

            if (!File.Exists(backupFilePath))
                throw new FileNotFoundException("Backup file not found.", backupFilePath);

          
            var password = BackupKeyProvider.GetPackagePassword();

            // Step 10: restore is governed by BACKUP.RESTORE (checked
            // above), not DATA.IMPORT — use the engine's restore entry.
            var result = await SteiSvc.ImportForRestoreAsync(backupFilePath, password, progress: null, ct);
            if (!result.Success)
            {
                _log.LogError("Backup",
                    $"Restore failed: {result.Message} ({result.FailedTables} tables, {result.FailedRows} rows)");
                throw new InvalidOperationException($"Backup restore failed: {result.Message}");
            }

            _log.LogInfo("Backup",
                $"Backup restored: {backupFilePath} ({result.Tables} tables, {result.Rows} rows)");
        }

        public async Task<bool> ValidateBackupAsync(string backupFilePath, CancellationToken ct = default)
        {
        
            if (!File.Exists(backupFilePath))
                return false;

            try
            {
                var password = BackupKeyProvider.GetPackagePassword();
                var result = await SteiSvc.ImportAsync(backupFilePath, password, simulation: true, progress: null, ct);
                return result.Success;
            }
            catch (Exception ex)
            {
                _log.LogError("Backup", $"Backup validation failed: {backupFilePath}", ex);
                return false;
            }
        }

        public void PerformBackup()
        {
            _ = CreateBackupAsync("001");
        }

        public BackupResult PerformRotatedBackup()
        {
            try
            {
                var task = CreateBackupAsync("001");
                var path = task.GetAwaiter().GetResult();
                return new BackupResult { Success = true, LocalPath = path };
            }
            catch (Exception ex)
            {
                _log.LogError("Backup", "PerformRotatedBackup failed", ex);
                return new BackupResult { Success = false, Message = ex.Message };
            }
        }

        public bool IsBackupDue()
        {
            try
            {
                var settings = AppCfgSvc.Current.Database.Backup;
                if (!settings.Enabled || !settings.AutoBackup)
                    return false;

                var root = AppPaths.CompanyBackup("001");
                if (!Directory.Exists(root))
                    return true;

                var latest = Directory.EnumerateFiles(root, "*.stb", SearchOption.TopDirectoryOnly)
                    .Select(path => new FileInfo(path))
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .FirstOrDefault();

                if (latest == null)
                    return true;

                var intervalHours = Math.Max(1, settings.BackupIntervalHours);
                return DateTime.UtcNow - latest.LastWriteTimeUtc >= TimeSpan.FromHours(intervalHours);
            }
            catch (Exception ex)
            {
                _log.LogError("Backup", "Unable to determine whether backup is due", ex);
                return false;
            }
        }

        private void PruneOldBackups(string directory)
        {
            try
            {
                var keep = Math.Clamp(AppCfgSvc.Current.Database.Backup.MaxBackups, 1, 1000);
                var files = Directory.EnumerateFiles(directory, "*.stb", SearchOption.TopDirectoryOnly)
                    .Select(path => new FileInfo(path))
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .ToList();

                foreach (var obsolete in files.Skip(keep))
                {
                    try { obsolete.Delete(); }
                    catch (Exception ex)
                    {
                        _log.LogError("Backup", $"Could not remove obsolete backup: {obsolete.FullName}", ex);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogError("Backup", "Backup retention rotation failed", ex);
            }
        }

        public async Task<BackupResult> RunOnlineBackupCycleAsync(bool force = false, CancellationToken ct = default)
        {
            try
            {
                var path = await CreateBackupAsync("001", ct);
                return new BackupResult { Success = true, LocalPath = path, Uploaded = false };
            }
            catch (Exception ex)
            {
                _log.LogError("Backup", "RunOnlineBackupCycleAsync failed", ex);
                return new BackupResult { Success = false, Message = ex.Message };
            }
        }

        public Task<bool> TryUploadRotatedBackupsAsync(CancellationToken ct = default)
        {
            return Task.FromResult(false);
        }

        // ═══════════════════════════════════════════════════════════
        // v0.2.0 additions — required by BkpWin (v0.1.7 UI)
        // ═══════════════════════════════════════════════════════════

        public System.Collections.Generic.List<BkpFileInfo> ListBackups(string companyCode)
        {
            var result = new System.Collections.Generic.List<BkpFileInfo>();
            try
            {
                var dir = GetBackupDirectory(companyCode);
                if (!System.IO.Directory.Exists(dir))
                    return result;

                foreach (var file in System.IO.Directory.GetFiles(dir, "*.stb"))
                {
                    var fi = new System.IO.FileInfo(file);
                    result.Add(new BkpFileInfo
                    {
                        FilePath = fi.FullName,
                        FileName = fi.Name,
                        SizeBytes = fi.Length,
                        LastWriteUtc = fi.LastWriteTimeUtc,
                        IsValid = false,
                        Status = "NotValidated"
                    });
                }
            }
            catch (Exception ex) { _log.LogError("Backup", "Listing backups failed", ex); }
            return result;
        }

        public async System.Threading.Tasks.Task<BackupValidationDetail> ValidateBackupDetailedAsync(
            string backupFilePath,
            System.Threading.CancellationToken ct = default)
        {
            var detail = new BackupValidationDetail();
            try
            {
                if (string.IsNullOrWhiteSpace(backupFilePath) || !System.IO.File.Exists(backupFilePath))
                {
                    detail.IsValid = false;
                    detail.FailureReason = "Backup file not found.";
                    return detail;
                }

                detail.IsValid = await ValidateBackupAsync(backupFilePath, ct);
                if (!detail.IsValid)
                    detail.FailureReason = "Backup integrity validation failed.";
            }
            catch (System.Exception ex)
            {
                AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, ex, "Backup.ValidateBackupDetailedAsync");
                detail.IsValid = false;
                detail.FailureReason = ex.Message;
            }
            return detail;
        }

        public string GetBackupDirectory(string companyCode)
        {
            try
            {
                var dir = AppPaths.CompanyBackup(companyCode);

                System.IO.Directory.CreateDirectory(dir);
                return dir;
            }
            catch (Exception exL1145)
            {
                AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, exL1145, "Backup.GetBackupDirectory");
                return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "backups");
            }
        }
		
		
	}

}
