using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ShouTech.App;
using ShouTech.App.Configuration;
using ShouTech.App.Services;
using Xunit;

namespace Kern.Tests;

/// <summary>
/// Fix Phase Step 1 — RBAC regression coverage. Pins the three defects
/// from the acceptance sweep (shell-admin-security.md P-1/P-4/P-5):
///   * PermServ.LoadForUserAsync resolved permissions with a CROSS JOIN
///     that ignored role_permissions — every user received every
///     permission. The limited-role facts below FAIL under that query.
///   * The loader was never called, so AppHost.Permissions stayed empty
///     and every guarded window deny-closed. The session-wiring facts
///     pin AuthServ.LoadPermissionsForSessionAsync (the login wiring
///     point) and the logout clear.
///   * PermAdminSvc matched the username PrmWin passes against the
///     user_id columns, loading an all-denied baseline and writing
///     orphaned overrides. The PermAdmin facts load/save BY USERNAME
///     and assert rows are keyed by the resolved user_id.
/// All facts run the REAL services against real migrated SQLite
/// databases. Execution requires Windows (net8.0-windows testhost);
/// the loader SQL is additionally verified headlessly — see
/// docs/FIX_PHASE_STEP1.md.
/// </summary>
public class RbacPermissionTests
{
    private const string JournalView = "ACC.JOURNAL.VIEW";
    private const string JournalPost = "ACC.JOURNAL.POST";
    private const string VoucherView = "VCH.VOUCHER.VIEW";
    private const string PermissionsManage = "SYS.PERMISSIONS.MANAGE";
    private const string BackupRestore = "BACKUP.RESTORE";
    private const string CompanyDelete = "ORG.COMPANY.DELETE";

    private static readonly string[] CatalogCodes =
    {
        JournalView, JournalPost, VoucherView, PermissionsManage, BackupRestore, CompanyDelete
    };

    // ------------------------------------------------------------ loader

    [Fact]
    public async Task LimitedRole_GetsExactlyItsRolePermissions()
    {
        using var db = await SeededDbAsync();
        var perms = new PermServ();
        await perms.LoadForUserAsync("limited");

        Assert.True(perms.Has(JournalView));
        Assert.True(perms.Has(VoucherView));
        // Each of these would be TRUE under the old CROSS JOIN loader.
        Assert.False(perms.Has(JournalPost));
        Assert.False(perms.Has(PermissionsManage));
        Assert.False(perms.Has(BackupRestore));
        Assert.False(perms.Has(CompanyDelete));
        Assert.Equal(2, perms.GetCurrentPermissions().Count);
    }

    [Fact]
    public async Task LimitedRole_LoadsByUserIdToo()
    {
        using var db = await SeededDbAsync();
        var perms = new PermServ();
        await perms.LoadForUserAsync("U-LIMITED");
        Assert.True(perms.Has(JournalView));
        Assert.False(perms.Has(JournalPost));
    }

    [Fact]
    public async Task Administrator_GetsAllPermissions()
    {
        using var db = await SeededDbAsync();
        var perms = new PermServ();
        await perms.LoadForUserAsync("boss");

        foreach (var code in CatalogCodes)
            Assert.True(perms.Has(code), code);
        Assert.False(perms.Has("DOES.NOT.EXIST"));
    }

    [Fact]
    public async Task UserOverrides_AllowAdds_AndDenyWinsOverRoleGrant()
    {
        using var db = await SeededDbAsync();
        var perms = new PermServ();
        await perms.LoadForUserAsync("overridden");

        Assert.True(perms.Has(BackupRestore));   // per-user allow, no role grant
        Assert.False(perms.Has(JournalView));    // per-user deny beats the role grant
        Assert.True(perms.Has(VoucherView));     // untouched role grant survives
    }

    [Fact]
    public async Task UserWhoseRoleGrantsNothing_LoadsWithEmptySet()
    {
        using var db = await SeededDbAsync();
        var perms = new PermServ();
        await perms.LoadForUserAsync("empty");
        Assert.Empty(perms.GetCurrentPermissions());
        Assert.False(perms.Has(JournalView));
    }

    [Fact]
    public async Task UnknownUser_ThrowsUnauthorized()
    {
        using var db = await SeededDbAsync();
        var perms = new PermServ();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => perms.LoadForUserAsync("no-such-user"));
    }

    // ------------------------------------------------------ session wiring

    [Fact]
    public async Task SessionWiring_LoadsPermissions_AndLogoutClearsThem()
    {
        using var db = await SeededDbAsync();
        try
        {
            var user = new UserInfo
            {
                Id = "U-LIMITED",
                Username = "limited",
                AccessLevel = UsrAccLvl.User,
                IsAuthenticated = true
            };
            await AuthServ.LoadPermissionsForSessionAsync(user);

            Assert.True(AppHost.Permissions.Has(JournalView));
            Assert.False(AppHost.Permissions.Has(JournalPost));
            Assert.Contains(JournalView, user.Permissions ?? Array.Empty<string>());

            await new AuthServ().LogoutAsync();
            Assert.False(AppHost.Permissions.Has(JournalView));
            Assert.Empty(AppHost.Permissions.GetCurrentPermissions());
        }
        finally
        {
            AppHost.Permissions.Clear();
            UsrSession.Clear();
        }
    }

    [Fact]
    public async Task SessionWiring_AdministratorFallsBackToCatalog_WhenDbUserMissing()
    {
        using var db = await SeededDbAsync();
        try
        {
            var admin = new UserInfo
            {
                Username = "ghost-admin",
                AccessLevel = UsrAccLvl.Administrator,
                IsAuthenticated = true
            };
            await AuthServ.LoadPermissionsForSessionAsync(admin);
            Assert.True(AppHost.Permissions.Has(PermissionsManage));

            var plain = new UserInfo
            {
                Username = "ghost-user",
                AccessLevel = UsrAccLvl.User,
                IsAuthenticated = true
            };
            await AuthServ.LoadPermissionsForSessionAsync(plain);
            Assert.False(AppHost.Permissions.Has(PermissionsManage));
            Assert.Empty(AppHost.Permissions.GetCurrentPermissions());
        }
        finally
        {
            AppHost.Permissions.Clear();
            UsrSession.Clear();
        }
    }

    // ---------------------------------------------------------- PermAdmin

    [Fact]
    public async Task PermAdmin_LoadByUsername_ResolvesRoleBaseline()
    {
        using var db = await SeededDbAsync();
        var items = await PermAdminSvc.LoadForUserAsync("limited");
        var view = items.Single(i => i.Code == JournalView);
        var post = items.Single(i => i.Code == JournalPost);

        // Pre-fix these were all false: the username was matched
        // against user_roles.user_id, which holds 'U-LIMITED'.
        Assert.True(view.BaselineAllowed);
        Assert.True(view.EffectiveAllowed);
        Assert.False(post.BaselineAllowed);
        Assert.False(post.EffectiveAllowed);
    }

    [Fact]
    public async Task PermAdmin_SaveByUsername_WritesOverridesKeyedByUserId()
    {
        using var db = await SeededDbAsync();
        try
        {
            var boss = new UserInfo
            {
                Id = "U-ADMIN",
                Username = "boss",
                AccessLevel = UsrAccLvl.Administrator,
                IsAuthenticated = true
            };
            UsrSession.Apply(boss);
            await AppHost.Permissions.LoadForUserAsync("boss");
            Assert.True(AppHost.Permissions.Has(PermissionsManage));

            var postId = db.ScalarLong(
                $"SELECT permission_id FROM permissions WHERE permission_code='{JournalPost}'");
            await PermAdminSvc.SaveForUserAsync("limited",
                new System.Collections.Generic.Dictionary<long, bool> { [postId] = true });

            Assert.Equal(1, db.ScalarLong(
                "SELECT COUNT(*) FROM user_permissions WHERE user_id='U-LIMITED' AND effect=1"));
            Assert.Equal(0, db.ScalarLong(
                "SELECT COUNT(*) FROM user_permissions WHERE user_id='limited'"));
        }
        finally
        {
            AppHost.Permissions.Clear();
            UsrSession.Clear();
        }
    }

    // ------------------------------------------------- destructive gating

    [Fact]
    public async Task BackupService_RefusesCreateAndRestore_WithoutPermissions()
    {
        using var db = await SeededDbAsync();
        var limited = new PermServ();
        await limited.LoadForUserAsync("limited");
        var backup = new BkpServ(null!, null!, null!, limited);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => backup.CreateBackupAsync("C1", "test"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => backup.RestoreBackupAsync(Path.Combine(db.Root, "missing.stb")));
    }

    [Fact]
    public async Task BackupService_PermissionGatePasses_ForAdministrator()
    {
        using var db = await SeededDbAsync();
        var admin = new PermServ();
        await admin.LoadForUserAsync("boss");
        var backup = new BkpServ(null!, null!, null!, admin);

        // The gate passes, so restore reaches the file check and fails
        // there instead of with UnauthorizedAccessException.
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => backup.RestoreBackupAsync(Path.Combine(db.Root, "missing.stb")));
    }

    [Fact]
    public void GatePermissions_AreRealCatalogCodes()
    {
        // Every window/service gate added in Step 1 must reference a
        // permission that exists in the catalog (and is therefore
        // seeded into role_permissions); a typo'd code would deny
        // everyone forever.
        Assert.Contains(PermCat.Backup.Create, PermCat.All);
        Assert.Contains(PermCat.Backup.Restore, PermCat.All);
        Assert.Contains(PermCat.Company.Delete, PermCat.All);
        Assert.Contains(PermCat.Company.Create, PermCat.All);
        Assert.Contains(PermCat.System.Admin, PermCat.All);
        Assert.Contains(PermCat.System.PermissionsManage, PermCat.All);
    }

    // -------------------------------------------------------------- seed

    private sealed class TestDb : IDisposable
    {
        public required string Root { get; init; }
        public required string DbPath { get; init; }

        public long ScalarLong(string sql)
        {
            using var c = Open(DbPath);
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (Exception ex) { Console.WriteLine($"Temp cleanup failed: {ex.Message}"); }
        }
    }

    private static async Task<TestDb> SeededDbAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "rbac-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var dbPath = Path.Combine(root, "rbac.db");

        Assert.True((await SchemaMigrator.ApplyAllAsync("SQLite", dbPath)).Success);
        PointAppAt(dbPath);

        using (var c = Open(dbPath))
        {
            Exec(c, "INSERT INTO companies (company_id, company_code, company_name_ar, company_name_en, currency_code, language_code, is_active, is_deleted, created_at) " +
                    "VALUES ('C1','C1','Test Co','Test Co','SAR','en',1,0,'2026-01-01T00:00:00')");
            foreach (var code in CatalogCodes)
                Exec(c, $"INSERT OR IGNORE INTO permissions (permission_code, permission_name) VALUES ('{code}', '{code}')");
            Exec(c, "INSERT INTO roles (company_id, role_code, role_name) VALUES ('C1','ROLE_LIMITED','Limited'),('C1','ROLE_ADMINISTRATOR','Administrator'),('C1','ROLE_EMPTY','Empty')");
            Exec(c, "INSERT INTO users (user_id, company_id, username, password_hash, password_salt, full_name, full_name_ar, full_name_en, is_active, is_deleted) VALUES " +
                    "('U-LIMITED','C1','limited','h','s','Limited User','Limited User','Limited User',1,0)," +
                    "('U-ADMIN','C1','boss','h','s','Boss','Boss','Boss',1,0)," +
                    "('U-OVERRIDE','C1','overridden','h','s','Override User','Override User','Override User',1,0)," +
                    "('U-EMPTY','C1','empty','h','s','Empty User','Empty User','Empty User',1,0)");
            Exec(c, "INSERT INTO user_roles (user_id, role_id) SELECT 'U-LIMITED', role_id FROM roles WHERE role_code='ROLE_LIMITED'");
            Exec(c, "INSERT INTO user_roles (user_id, role_id) SELECT 'U-ADMIN', role_id FROM roles WHERE role_code='ROLE_ADMINISTRATOR'");
            Exec(c, "INSERT INTO user_roles (user_id, role_id) SELECT 'U-OVERRIDE', role_id FROM roles WHERE role_code='ROLE_LIMITED'");
            Exec(c, "INSERT INTO user_roles (user_id, role_id) SELECT 'U-EMPTY', role_id FROM roles WHERE role_code='ROLE_EMPTY'");
            // Limited role: exactly two grants.
            Exec(c, $"INSERT INTO role_permissions (role_id, permission_id) SELECT r.role_id, p.permission_id FROM roles r, permissions p WHERE r.role_code='ROLE_LIMITED' AND p.permission_code IN ('{JournalView}','{VoucherView}')");
            // Administrator role: every catalog code seeded above.
            Exec(c, "INSERT INTO role_permissions (role_id, permission_id) SELECT r.role_id, p.permission_id FROM roles r, permissions p WHERE r.role_code='ROLE_ADMINISTRATOR'");
            // Per-user overrides for 'overridden': allow restore, deny journal view.
            Exec(c, $"INSERT INTO user_permissions (user_id, permission_id, effect) SELECT 'U-OVERRIDE', permission_id, 1 FROM permissions WHERE permission_code='{BackupRestore}'");
            Exec(c, $"INSERT INTO user_permissions (user_id, permission_id, effect) SELECT 'U-OVERRIDE', permission_id, 0 FROM permissions WHERE permission_code='{JournalView}'");
        }

        return new TestDb { Root = root, DbPath = dbPath };
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
}
