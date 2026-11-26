using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ShouTech.App;
using ShouTech.App.Configuration;
using ShouTech.App.Services;
using Xunit;

namespace Kern.Tests;

/// <summary>
/// Defect B regression coverage: the STEI package engine behind DatWin
/// export/import (Services/Backup.cs SteiSvc) must round-trip a real
/// company database losslessly. These tests run the REAL services against
/// real migrated SQLite databases and pin the failure modes found in the
/// 2026-11-17 reproduction:
///   * posted journal entries were uninsertable (posting-guard trigger),
///   * alphabetical table order violated foreign keys (total rollback),
///   * the export company filter leaked other companies' rows,
///   * re-imports silently dropped rows and misreported the counts.
/// Execution requires Windows (the test host is net8.0-windows and the
/// signing key is DPAPI-protected); the same scenarios are verified
/// headlessly on any OS by the STEI harness described in
/// docs/DATWIN_ROUNDTRIP.md.
/// </summary>
public class SteiRoundTripTests
{
    [Fact]
    public void BuildManifest_SeparatesCompanyIdFromCompanyCode()
    {
        var previousUser = AppState.CurrentUser;
        var previousCode = AppState.CurrentCompanyCode;
        var previousName = AppState.CurrentCompanyName;
        var previousNameEn = AppState.CurrentCompanyNameEn;
        try
        {
            AppState.CurrentUser = new UserInfo
            {
                Id = "user-1",
                Username = "admin",
                FullName = "Administrator",
                CompanyId = "company-id-1"
            };
            AppState.SetCompany("company-code-1", "Company Local", "Company English");

            var manifest = SteiSvc.BuildManifest("SQLite");

            Assert.Equal("company-id-1", manifest.SourceCompanyId);
            Assert.Equal("company-code-1", manifest.SourceCompanyCode);
        }
        finally
        {
            AppState.CurrentUser = previousUser;
            AppState.SetCompany(previousCode, previousName, previousNameEn);
        }
    }

    [Fact]
    public void Crypto_RoundTrip_PreservesPayload_AndRejectsWrongPassword()
    {
        var payload = Encoding.UTF8.GetBytes("فاتورة INV-001 — 109.25 — 2026-01-15");
        var (cipher, salt, nonce, tag) = SteiCrypto.Encrypt(payload, "correct horse");

        var plain = SteiCrypto.Decrypt(cipher, "correct horse", salt, nonce, tag);
        Assert.Equal(payload, plain);

        Assert.ThrowsAny<CryptographicException>(() =>
            SteiCrypto.Decrypt(cipher, "wrong password", salt, nonce, tag));
    }

    [Fact]
    public async Task ExportImport_RoundTrip_IsLossless_AndHonestOnReimport()
    {
        var root = Path.Combine(Path.GetTempPath(), "stei-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var srcPath = Path.Combine(root, "src.db");
        var dstPath = Path.Combine(root, "dst.db");
        var pkgPath = Path.Combine(root, "pkg.stei");

        var previousUser = AppState.CurrentUser;
        var previousCompany = AppState.CurrentCompanyCode;
        try
        {
            Assert.True((await SchemaMigrator.ApplyAllAsync("SQLite", srcPath)).Success);
            Assert.True((await SchemaMigrator.ApplyAllAsync("SQLite", dstPath)).Success);

            SeedSource(srcPath);
            using (var dst = Open(dstPath))
                Exec(dst, "INSERT INTO companies (company_id, company_code, company_name_ar, company_name_en, currency_code, language_code, is_active, is_deleted, created_at) " +
                          "VALUES ('C1','C1','شركة الشام التجارية','Sham Trading Co','SAR','en',1,0,'2026-01-01T00:00:00')");

            AppState.CurrentUser = new UserInfo { Id = "u1", Username = "admin", FullName = "Admin", CompanyId = "C1" };
            AppState.SetCompany("C1", "Sham Trading Co", "Sham Trading Co");
            // Step 10: STEI imports are gated on DATA.IMPORT in the
            // service layer — the test session holds the full catalog,
            // like a signed-in administrator.
            AppHost.Permissions.GrantAllCatalogPermissions("u1");

            PointAppAt(srcPath);
            var export = await SteiSvc.ExportAsync(pkgPath, "password123");
            Assert.True(export.Success, export.Message);

            PointAppAt(dstPath);
            var import = await SteiSvc.ImportAsync(pkgPath, "password123", simulation: false);
            Assert.True(import.Success, import.Message + " | " + string.Join(" ; ", import.Errors));
            Assert.Equal(0, import.FailedRows);
            Assert.Equal(export.Rows, import.Rows);

            using (var dst = Open(dstPath))
            {
                // Arabic text, decimal and NULL survived exactly. (Step 2:
                // the sample master row lives in the ONE canonical chart,
                // chart_of_accounts — the legacy accounts table was merged
                // into it and dropped by migration 059.)
                Assert.Equal("الحساب الرئيسي", Scalar(dst, "SELECT name_ar FROM chart_of_accounts WHERE account_id='A110'"));
                Assert.Equal("15.5", Scalar(dst, "SELECT CAST(tax_rate AS TEXT) FROM chart_of_accounts WHERE account_id='A110'"));
                Assert.Equal("", Scalar(dst, "SELECT IFNULL(updated_at,'') FROM chart_of_accounts WHERE account_id='A110'"));

                // The posted journal entry landed AS POSTED (status=1) —
                // impossible before the trigger-suspension fix.
                Assert.Equal("1", Scalar(dst, "SELECT CAST(status AS TEXT) FROM journal_entries WHERE journal_entry_id='JE-1'"));
                Assert.Equal("2", Scalar(dst, "SELECT CAST(COUNT(*) AS TEXT) FROM journal_entry_lines WHERE journal_entry_id='JE-1'"));

                // Posting-guard triggers were restored after the import.
                Assert.Equal("1", Scalar(dst, "SELECT CAST(COUNT(*) AS TEXT) FROM sqlite_master WHERE type='trigger' AND name='trg_journal_post_insert_requires_open_period'"));

                // FK-ordered tables landed (child rows whose parents sort later).
                Assert.Equal("1", Scalar(dst, "SELECT CAST(COUNT(*) AS TEXT) FROM fiscal_periods"));
                Assert.Equal("1", Scalar(dst, "SELECT CAST(COUNT(*) AS TEXT) FROM sales_document_lines"));

                // No cross-company leak: the C2-only brand stayed out.
                Assert.Equal("0", Scalar(dst, "SELECT CAST(COUNT(*) AS TEXT) FROM brands"));
            }

            // Re-import: succeeds, inserts nothing, and SAYS so.
            var reimport = await SteiSvc.ImportAsync(pkgPath, "password123", simulation: false);
            Assert.True(reimport.Success, reimport.Message);
            Assert.Equal(0, reimport.Rows);
            Assert.Equal(export.Rows, reimport.SkippedDuplicates);
        }
        finally
        {
            AppHost.Permissions.Clear();
            AppState.CurrentUser = previousUser;
            AppState.CurrentCompanyCode = previousCompany;
            try { Directory.Delete(root, recursive: true); } catch (Exception cleanupEx) { Console.WriteLine($"Temp cleanup failed: {cleanupEx.Message}"); }
        }
    }

    // ------------------------------------------------------------------

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

    private static SqliteConnection Open(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            ForeignKeys = true
        }.ToString());
        db.Open();
        return db;
    }

    private static void Exec(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string Scalar(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToString(cmd.ExecuteScalar()) ?? "";
    }

    private static void SeedSource(string srcPath)
    {
        using var db = Open(srcPath);
        Exec(db, "INSERT INTO companies (company_id, company_code, company_name_ar, company_name_en, currency_code, language_code, is_active, is_deleted, created_at) " +
                 "VALUES ('C1','C1','شركة الشام التجارية','Sham Trading Co','SAR','en',1,0,'2026-01-01T00:00:00')");
        Exec(db, "INSERT INTO fiscal_years (fiscal_year_id, company_id, fiscal_year, database_name, start_date, end_date, is_current, is_closed) " +
                 "VALUES (1,'C1',2026,'src','2026-01-01','2026-12-31',1,0)");
        Exec(db, "INSERT INTO fiscal_periods (fiscal_period_id, company_id, fiscal_year_id, period_number, period_name, start_date, end_date, status) " +
                 "VALUES ('P2026-01','C1',1,1,'January','2026-01-01','2026-01-31','OPEN')");
        Exec(db, "INSERT INTO chart_of_accounts (account_id, company_id, account_code, account_name, name_ar, account_type, parent_account_id, is_postable, is_active, is_deleted, created_at, tax_rate, description, updated_at) " +
                 "VALUES ('A110','C1','110','Cash','الحساب الرئيسي','ASSET',NULL,1,1,0,'2026-01-01T00:00:00',15.5,'desc',NULL)," +
                 "       ('A400','C1','400','Sales Revenue','Sales Revenue','REVENUE',NULL,1,1,0,'2026-01-01T00:00:00',0,'',NULL)");
        // A table holding ONLY another company's rows: must not be exported.
        // The C2 company itself is seeded so the brands.company_id foreign key
        // (migration 014) holds with ForeignKeys=true; the scenario under test
        // is the export company filter, not an orphaned row.
        Exec(db, "INSERT INTO companies (company_id, company_code, company_name_ar, company_name_en, currency_code, language_code, is_active, is_deleted, created_at) " +
                 "VALUES ('C2','C2','شركة أخرى','Other Co','SAR','en',1,0,'2026-01-01T00:00:00')");
        Exec(db, "INSERT INTO brands (brand_id, company_id, brand_code, brand_name_ar, brand_name_en) " +
                 "VALUES (1,'C2','BR-C2','علامة شركة أخرى','OtherCo Brand')");
        Exec(db, "INSERT INTO warehouses (warehouse_id, company_id, card_no, warehouse_code, warehouse_name_ar, warehouse_name_en, is_default, is_active, is_deleted) " +
                 "VALUES (1,'C1',1,100,'المستودع الرئيسي','Main Warehouse',1,1,0)");
        // Child table that sorts BEFORE its parent alphabetically
        // (sales_document_lines < sales_documents): the FK-order case.
        Exec(db, "INSERT INTO sales_documents (doc_id, company_id, doc_kind, doc_no, doc_date, currency_code, exchange_rate, subtotal, discount_amount, tax_amount, grand_total, paid_amount, status, created_at, is_deleted, payment_method) " +
                 "VALUES (1,'C1','INV','INV-001','2026-01-15','SAR',1.0,100.00,5.00,14.25,109.25,109.25,'Posted','2026-01-15T10:30:00',0,'Cash')");
        Exec(db, "INSERT INTO sales_document_lines (line_id, doc_id, line_no, description, qty, unit_price, discount_pct, tax_rate, line_subtotal, line_tax, line_total) " +
                 "VALUES (1,1,1,'وصف عربي للبند الأول',2.0,50.00,0.0,15.0,100.00,15.00,115.00)");
        // Posted journal entry, created the only way the triggers allow:
        // draft -> lines -> post.
        Exec(db, "INSERT INTO journal_entries (journal_entry_id, company_id, entry_date, description, status, created_by, created_at, currency_code, exchange_rate, fiscal_period_id) " +
                 "VALUES ('JE-1','C1','2026-01-15','قيد مبيعات افتتاحي',0,'admin','2026-01-15T11:00:00','SAR',1.0,'P2026-01')");
        Exec(db, "INSERT INTO journal_entry_lines (journal_entry_line_id, journal_entry_id, line_number, account_id, debit, credit, note) " +
                 "VALUES (1,'JE-1',1,'A110',500.25,0,'مدين'),(2,'JE-1',2,'A400',0,500.25,'دائن')");
        Exec(db, "UPDATE journal_entries SET status=1, posted_by='admin', posted_at='2026-01-15T11:05:00' WHERE journal_entry_id='JE-1'");
    }
}
