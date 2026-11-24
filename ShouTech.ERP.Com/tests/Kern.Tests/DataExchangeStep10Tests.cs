using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ShouTech.App;
using ShouTech.App.Configuration;
using ShouTech.App.Services;
using ShouTech.Domain;
using Xunit;

namespace Kern.Tests;

/// <summary>
/// Fix Phase Step 10 — data-exchange safety facts (sweep DX-01..DX-06):
///   * the dataset catalog resolves ONLY to real, company-scoped tables;
///   * imports are gated on DATA.IMPORT in the service layer (DATA-4004)
///     and leave a durable security-audit row;
///   * an Excel import never overwrites live balances (balance columns
///     are stripped and reported), balance datasets refuse spreadsheet
///     import (DATA-4005) and document datasets refuse it too (DATA-4006);
///   * a pre-unification STEI package carrying a dropped legacy table
///     (accounts) imports WITHOUT recreating that table.
/// Execution requires Windows (net8.0-windows test host, DPAPI signing
/// key); the same scenarios are verified headlessly on any OS by the
/// Step 10 harness described in docs/FIX_PHASE_STEP1.md.
/// </summary>
public class DataExchangeStep10Tests
{
    private const string CompanyCode = "C1";
    private const string CompanyGuid = "11111111-1111-1111-1111-111111111111";

    // ------------------------------------------------------------ catalog

    [Fact]
    public async Task Catalog_EverySeed_ResolvesToARealCompanyScopedTable()
    {
        using var db = await MigratedDbAsync();
        PointAppAt(db.Path);
        try
        {
            var catalogField = typeof(DataExportSvc).GetField("Catalog", BindingFlags.Static | BindingFlags.NonPublic)!;
            var seeds = ((IEnumerable)catalogField.GetValue(null)!).Cast<object>().ToList();
            Assert.Equal(37, seeds.Count);

            foreach (var seed in seeds)
            {
                var key = (string)seed.GetType().GetProperty("Key")!.GetValue(seed)!;
                var candidates = (string[])seed.GetType().GetProperty("TableCandidates")!.GetValue(seed)!;
                var table = candidates.FirstOrDefault(c => db.TableExists(c));
                Assert.True(table != null, $"Dataset '{key}' resolves to no real table.");
                var columns = db.Columns(table!);
                var scoped = columns.Contains("company_id")
                    || table == "companies"
                    || new[] { "sales_document_lines", "purchase_document_lines", "journal_entry_lines", "mfg_order_lines" }.Contains(table);
                Assert.True(scoped, $"Dataset '{key}' -> '{table}' has no company scope.");
            }
        }
        finally { AppHost.Permissions.Clear(); }
    }

    // ------------------------------------------------------------ gating

    [Fact]
    public async Task Import_WithoutDataImportPermission_IsRefusedWithData4004_AndWritesNothing()
    {
        using var db = await MigratedDbAsync();
        PointAppAt(db.Path);
        SeedCompanyAndMaterial(db);
        var xlsx = WriteWorkbook(db.Root,
            ["material_id", "company_id", "code", "name_ar", "name_en"],
            [[1L, CompanyCode, "MAT-1", "Widget", "Renamed"]]);
        var option = new DataExportOption("Products", "Product cards and directory", "materials");
        try
        {
            AppHost.Permissions.Clear();
            AppState.CurrentUser = new UserInfo { Id = "u1", Username = "limited", CompanyId = CompanyCode };
            AppState.SetCompany(CompanyCode, "Step10 Co", "Step10 Co");

            var ex = await Assert.ThrowsAsync<AppError>(() =>
                DataExportSvc.ImportExcelAsync(xlsx, option, CompanyCode));
            Assert.Equal(ErrorCodes.ImportPermissionDenied, ex.Code);
            Assert.Equal("Widget", db.Scalar("SELECT name_en FROM materials WHERE material_id = 1"));
            Assert.True(db.ScalarLong("SELECT COUNT(*) FROM security_audit_log WHERE event_type = 'DATA_IMPORT_DENIED'") >= 1);
        }
        finally { AppHost.Permissions.Clear(); }
    }

    [Fact]
    public async Task Import_WithPermission_WritesAuditRowWithCounts()
    {
        using var db = await MigratedDbAsync();
        PointAppAt(db.Path);
        SeedCompanyAndMaterial(db);
        var xlsx = WriteWorkbook(db.Root,
            ["material_id", "company_id", "code", "name_ar", "name_en"],
            [[1L, CompanyCode, "MAT-1", "Widget", "Renamed"]]);
        var option = new DataExportOption("Products", "Product cards and directory", "materials");
        try
        {
            AppHost.Permissions.GrantAllCatalogPermissions("u1");
            AppState.CurrentUser = new UserInfo { Id = "u1", Username = "admin", CompanyId = CompanyCode };
            AppState.SetCompany(CompanyCode, "Step10 Co", "Step10 Co");

            var result = await DataExportSvc.ImportExcelAsync(xlsx, option, CompanyCode);
            Assert.Equal(1, result.UpsertedRows);
            Assert.Equal("Renamed", db.Scalar("SELECT name_en FROM materials WHERE material_id = 1"));
            var details = db.Scalar("SELECT details FROM security_audit_log WHERE event_type = 'DATA_IMPORT' ORDER BY security_audit_id DESC LIMIT 1");
            Assert.Contains("dataset=Products", details);
            Assert.Contains("upserted=1", details);
        }
        finally { AppHost.Permissions.Clear(); }
    }

    // ------------------------------------------------------------ balances

    [Fact]
    public async Task ExcelImport_StripsBalanceColumns_LiveBalancesUnchanged()
    {
        using var db = await MigratedDbAsync();
        PointAppAt(db.Path);
        SeedCompanyAndMaterial(db);
        var xlsx = WriteWorkbook(db.Root,
            ["material_id", "company_id", "code", "name_ar", "name_en", "qty_on_hand", "avg_cost"],
            [[1L, CompanyCode, "MAT-1", "Widget", "Renamed", 9999L, 999L]]);
        var option = new DataExportOption("Products", "Product cards and directory", "materials");
        try
        {
            AppHost.Permissions.GrantAllCatalogPermissions("u1");
            AppState.CurrentUser = new UserInfo { Id = "u1", Username = "admin", CompanyId = CompanyCode };
            AppState.SetCompany(CompanyCode, "Step10 Co", "Step10 Co");

            var result = await DataExportSvc.ImportExcelAsync(xlsx, option, CompanyCode);
            Assert.Contains("qty_on_hand", result.RejectedBalanceColumns);
            Assert.Contains("avg_cost", result.RejectedBalanceColumns);
            Assert.Equal("Renamed", db.Scalar("SELECT name_en FROM materials WHERE material_id = 1"));
            Assert.Equal("50", db.Scalar("SELECT CAST(qty_on_hand AS TEXT) FROM materials WHERE material_id = 1"));
            Assert.Equal("10", db.Scalar("SELECT CAST(avg_cost AS TEXT) FROM materials WHERE material_id = 1"));
        }
        finally { AppHost.Permissions.Clear(); }
    }

    [Fact]
    public async Task ExcelImport_BalanceDataset_RefusedWithData4005()
    {
        using var db = await MigratedDbAsync();
        PointAppAt(db.Path);
        SeedCompanyAndMaterial(db);
        var xlsx = WriteWorkbook(db.Root,
            ["stock_balance_id", "company_id", "material_id", "warehouse_id", "available_qty"],
            [[1L, CompanyCode, 1L, 1L, 5L]]);
        var option = new DataExportOption("OpeningStock", "Opening stock and balances", "material_stock_balances",
            Policy: DatasetImportPolicy.EngineOwned);
        try
        {
            AppHost.Permissions.GrantAllCatalogPermissions("u1");
            AppState.CurrentUser = new UserInfo { Id = "u1", Username = "admin", CompanyId = CompanyCode };
            AppState.SetCompany(CompanyCode, "Step10 Co", "Step10 Co");

            var ex = await Assert.ThrowsAsync<AppError>(() =>
                DataExportSvc.ImportExcelAsync(xlsx, option, CompanyCode));
            Assert.Equal(ErrorCodes.BalanceImportNotAllowed, ex.Code);
        }
        finally { AppHost.Permissions.Clear(); }
    }

    [Fact]
    public async Task ExcelImport_DocumentDataset_RefusedWithData4006()
    {
        using var db = await MigratedDbAsync();
        PointAppAt(db.Path);
        SeedCompanyAndMaterial(db);
        var xlsx = WriteWorkbook(db.Root,
            ["doc_id", "company_id", "doc_kind", "doc_no"],
            [[7L, CompanyCode, "SAL", "SAL-7"]]);
        var option = new DataExportOption("Sales", "Sales invoices", "sales_documents",
            DataExportFilter.SalesDocuments, Policy: DatasetImportPolicy.PackageOnly);
        try
        {
            AppHost.Permissions.GrantAllCatalogPermissions("u1");
            AppState.CurrentUser = new UserInfo { Id = "u1", Username = "admin", CompanyId = CompanyCode };
            AppState.SetCompany(CompanyCode, "Step10 Co", "Step10 Co");

            var ex = await Assert.ThrowsAsync<AppError>(() =>
                DataExportSvc.ImportExcelAsync(xlsx, option, CompanyCode));
            Assert.Equal(ErrorCodes.DocumentImportRequiresPackage, ex.Code);
            Assert.Equal("0", db.Scalar("SELECT CAST(COUNT(*) AS TEXT) FROM sales_documents"));
        }
        finally { AppHost.Permissions.Clear(); }
    }

    // ------------------------------------------------------------ legacy package

    [Fact]
    public async Task SteiImport_LegacyTableInPackage_IsSkipped_NotRecreated()
    {
        using var db = await MigratedDbAsync();
        PointAppAt(db.Path);
        SeedCompanyAndMaterial(db);
        var pkg = Path.Combine(db.Root, "legacy.stei");
        WriteLegacyPackage(pkg);
        try
        {
            AppHost.Permissions.GrantAllCatalogPermissions("u1");
            AppState.CurrentUser = new UserInfo { Id = "u1", Username = "admin", CompanyId = CompanyCode };
            AppState.SetCompany(CompanyCode, "Step10 Co", "Step10 Co");

            var result = await SteiSvc.ImportAsync(pkg, "password123", simulation: false);
            Assert.True(result.Success, result.Message + " | " + string.Join(" ; ", result.Errors));
            Assert.False(db.TableExists("accounts"));
            Assert.Contains("accounts", result.Message);
            Assert.Equal("B1", db.Scalar("SELECT brand_code FROM brands WHERE brand_code = 'B1'"));
            var details = db.Scalar("SELECT details FROM security_audit_log WHERE event_type = 'DATA_IMPORT' ORDER BY security_audit_id DESC LIMIT 1");
            Assert.Contains("legacySkipped", details);
        }
        finally { AppHost.Permissions.Clear(); }
    }

    /// <summary>
    /// Builds a genuine STEI container (real SteiCrypto, real signing
    /// key) whose payload carries the dropped legacy `accounts` table
    /// plus one live table — the pre-059 package shape of Step 2 flag F-3.
    /// </summary>
    private static void WriteLegacyPackage(string path)
    {
        var tables = new Dictionary<string, List<Dictionary<string, JsonElement>>>(StringComparer.OrdinalIgnoreCase)
        {
            ["accounts"] = [new Dictionary<string, JsonElement>
            {
                ["account_id"] = JsonSerializer.SerializeToElement(99),
                ["company_id"] = JsonSerializer.SerializeToElement(CompanyCode),
                ["account_code"] = JsonSerializer.SerializeToElement("9999")
            }],
            ["brands"] = [new Dictionary<string, JsonElement>
            {
                ["brand_id"] = JsonSerializer.SerializeToElement("BR-1"),
                ["company_id"] = JsonSerializer.SerializeToElement(CompanyGuid),
                ["brand_code"] = JsonSerializer.SerializeToElement("B1"),
                ["brand_name_ar"] = JsonSerializer.SerializeToElement("Brand One"),
                ["brand_name_en"] = JsonSerializer.SerializeToElement("Brand One")
            }]
        };
        var payload = SteiCrypto.Serialize(tables);
        var manifest = new SteiManifest
        {
            DataScope = "CompanyData",
            TableCount = tables.Count,
            RowCount = 2,
            PayloadHash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
            SourceCompanyId = CompanyCode,
            SourceCompanyCode = CompanyCode
        };
        using var signingKey = SteiCrypto.LoadSigningKey();
        var publicKey = SteiCrypto.ExportPublicKey(signingKey);
        manifest.SigningKeyFingerprint = SteiCrypto.Fingerprint(publicKey);
        var encrypted = SteiCrypto.Encrypt(payload, "password123");
        manifest.Salt = Convert.ToBase64String(encrypted.Salt);
        manifest.Nonce = Convert.ToBase64String(encrypted.Nonce);
        manifest.Iterations = 310_000;
        // Signature data mirrors SteiSvc.BuildSignatureData exactly.
        var original = manifest.Signature;
        manifest.Signature = "";
        var manifestJson = SteiCrypto.Serialize(manifest);
        manifest.Signature = original;
        var signatureData = SHA256.HashData(manifestJson.Concat(SHA256.HashData(payload)).ToArray());
        manifest.Signature = Convert.ToBase64String(SteiCrypto.Sign(signingKey, signatureData));

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        void Entry(string name, byte[] data)
        {
            var e = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var s = e.Open();
            s.Write(data, 0, data.Length);
        }
        Entry("manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions { WriteIndented = true }));
        Entry("payload.bin", encrypted.Cipher);
        Entry("payload.tag", encrypted.Tag);
        Entry("signing-public.key", publicKey);
        Entry("format.txt", Encoding.UTF8.GetBytes("SHOUTECH-STEI\n"));
    }

    // ------------------------------------------------------------ fixture

    private static void SeedCompanyAndMaterial(TestDb db)
    {
        db.Exec($"INSERT INTO companies (company_id, company_code, company_name_ar, company_name_en, currency_code, language_code, is_active, is_deleted, created_at) VALUES ('{CompanyGuid}', '{CompanyCode}', 'Step10 Co', 'Step10 Co', 'SAR', 'en', 1, 0, '2026-01-01T00:00:00')");
        db.Exec($"INSERT INTO materials (material_id, company_id, code, name_ar, name_en, qty_on_hand, avg_cost, is_deleted) VALUES (1, '{CompanyCode}', 'MAT-1', 'Widget', 'Widget', 50, 10, 0)");
    }

    private static string WriteWorkbook(string root, string[] headers, object?[][] rows)
    {
        var path = Path.Combine(root, "import-" + Guid.NewGuid().ToString("N") + ".xlsx");
        var cols = headers.Select(h => new XlsxWrt.Col { Header = h, Width = 14 }).ToList();
        var data = rows.Select(r => (IReadOnlyList<object?>)r.ToList()).ToList();
        XlsxWrt.Write(path, "Sheet1", cols, data, rtl: false);
        return path;
    }

    private static void PointAppAt(string sqlitePath)
    {
        var settings = AppCfgSvc.Current;
        settings.ConnectionStrings["Default"] = new ConnectionStringSettings
        {
            Provider = "SQLite",
            ConnectionString = sqlitePath
        };
        settings.Database ??= new DatabaseSettings();
        settings.Database.DefaultProvider = "SQLite";
    }

    private static async Task<TestDb> MigratedDbAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "step10-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "test.db");
        Assert.True((await SchemaMigrator.ApplyAllAsync("SQLite", path)).Success);
        return new TestDb(root, path);
    }

    private sealed class TestDb(string root, string path) : IDisposable
    {
        public string Root { get; } = root;
        public string Path { get; } = path;

        private SqliteConnection Open()
        {
            var db = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path,
                Mode = SqliteOpenMode.ReadWrite,
                ForeignKeys = true
            }.ToString());
            db.Open();
            return db;
        }

        public void Exec(string sql)
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        public string Scalar(string sql)
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = sql;
            return cmd.ExecuteScalar()?.ToString() ?? "";
        }

        public long ScalarLong(string sql) => long.Parse(Scalar(sql));

        public bool TableExists(string table)
            => ScalarLong($"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{table}'") > 0;

        public HashSet<string> Columns(string table)
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info(\"{table}\")";
            using var reader = cmd.ExecuteReader();
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (reader.Read())
                set.Add(reader.GetString(1));
            return set;
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { /* temp cleanup is best-effort */ }
        }
    }
}
