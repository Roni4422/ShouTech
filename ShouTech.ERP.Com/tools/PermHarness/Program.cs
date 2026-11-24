// PermHarness — permission templates + Inspector proof
// (PERMISSIONS_STUDY §2.2/§2.3/§2.6). Builds a fresh SQLite database
// through migration 125, seeds two companies, an administrator and a
// minimal-role user, then proves the matrix against the REAL
// services (PermServ loader + ExplainAsync, PermTplSvc):
//   catalog reconciliation (every active DB code is in PermCat.All),
//   role grant / template grant / per-user allow / explicit deny /
//   administrator role — each named by the Inspector, matching the
//   loader; deny wins over template AND over the administrator role;
//   DOC semantics (generic fallback, DOC deny subtracts one type,
//   RequireDoc writes the AccessDenied event); template lifecycle
//   (create/copy/delete, system immutability PRM-8504, duplicate
//   PRM-8503, cross-company assignment PRM-8505, audit rows,
//   company scoping, inactive templates grant nothing).
using Microsoft.Data.Sqlite;
using ShouTech.App;
using ShouTech.App.Configuration;
using ShouTech.App.Services;
using ShouTech.Domain;

const string CompanyCode = "PERMH";
const string Company2Code = "PERMX";

var root = Path.Combine(Path.GetTempPath(), "perm-harness-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var dbPath = Path.Combine(root, "perm.db");
var cs = $"Data Source={dbPath};";
var companyId = Guid.NewGuid();
var company2Id = Guid.NewGuid();
var adminId = Guid.NewGuid();
var minId = Guid.NewGuid();
var otherId = Guid.NewGuid();

var pass = 0;
var fail = 0;
void Check(string name, bool condition, string detail = "")
{
    Console.WriteLine($"{(condition ? "PASS" : "FAIL")} - {name}{(condition || detail.Length == 0 ? "" : " | " + detail)}");
    if (condition) pass++; else fail++;
}

void Exec(string sql)
{
    using var connection = new SqliteConnection(cs);
    connection.Open();
    using var cmd = connection.CreateCommand();
    cmd.CommandText = sql;
    cmd.ExecuteNonQuery();
}

long ScalarLong(string sql)
{
    using var connection = new SqliteConnection(cs);
    connection.Open();
    using var cmd = connection.CreateCommand();
    cmd.CommandText = sql;
    return Convert.ToInt64(cmd.ExecuteScalar());
}

long PermId(string code) => ScalarLong($"SELECT permission_id FROM permissions WHERE permission_code = '{code}'");

async Task<string> CatchCode(Func<Task> action)
{
    try { await action(); return string.Empty; }
    catch (AppError ex) { return ex.Code; }
    catch (UnauthorizedAccessException) { return "UNAUTH"; }
    catch (Exception ex) { return "OTHER:" + ex.GetType().Name; }
}

// ---------------------------------------------------------------- setup
var migration = await SchemaMigrator.ApplyAllAsync("SQLite", dbPath);
Check("schema migrates (through 125)", migration.Success, migration.Message);

var settings = AppCfgSvc.Current;
settings.ConnectionStrings["Default"] = new ConnectionStringSettings { Provider = "SQLite", ConnectionString = dbPath };
settings.Database ??= new DatabaseSettings();
settings.Database.DefaultProvider = "SQLite";
AppState.SetCompany(CompanyCode, "Perm Harness Co", "Perm Harness Co");

Check("catalog: 489 active permissions", ScalarLong("SELECT COUNT(*) FROM permissions WHERE is_active = 1") == 489,
    $"{ScalarLong("SELECT COUNT(*) FROM permissions WHERE is_active = 1")}");
Check("catalog: 104 DOC permissions", ScalarLong("SELECT COUNT(*) FROM permissions WHERE permission_code LIKE 'DOC.%'") == 104);
Check("catalog: 26 permission groups", ScalarLong("SELECT COUNT(*) FROM permission_groups") == 26);
Check("catalog: 3 system templates seeded", ScalarLong("SELECT COUNT(*) FROM permission_templates WHERE is_system = 1") == 3);

// Catalog reconciliation: every active stored code exists in PermCat.
var missing = new List<string>();
using (var connection = new SqliteConnection(cs))
{
    connection.Open();
    using var cmd = connection.CreateCommand();
    cmd.CommandText = "SELECT permission_code FROM permissions WHERE is_active = 1";
    using var reader = cmd.ExecuteReader();
    var catalog = new HashSet<string>(PermCat.All, StringComparer.OrdinalIgnoreCase);
    while (reader.Read())
        if (!catalog.Contains(reader.GetString(0)))
            missing.Add(reader.GetString(0));
}
// Reconciliation boundary (study section 1.1): exactly 58 stored
// codes have no PermCat constant, in three documented families —
// the migration-002 underscore relics, the short PermKey family and
// the purchase-cycle PUR.* / INV.VALUATION.* service literals. They
// are governed where they are used (PermKey, the P2P services, the
// bootstrapper's protected list), not by the catalog. The proof:
// the uncovered set is EXACTLY that documented drift set — every
// other stored code is in PermCat.All, and no new drift appears.
var drift = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "ACC.CREATE",
    "ACC.DELETE",
    "ACC.POST",
    "ACC.VIEW",
    "ACCOUNTING_EDIT",
    "ACCOUNTING_VIEW",
    "AUDIT_VIEW",
    "BACKUP_RESTORE",
    "BACKUP_RUN",
    "COMPANY_CREATE",
    "COMPANY_DELETE",
    "COMPANY_EDIT",
    "COMPANY_VIEW",
    "INV.CREATE",
    "INV.DELETE",
    "INV.MOVE",
    "INV.VALUATION.RECALC",
    "INV.VALUATION.VIEW",
    "INV.VIEW",
    "INVENTORY_EDIT",
    "INVENTORY_VIEW",
    "JOURNAL_POST",
    "PERMISSION_VIEW",
    "PUR.ATTACH.DELETE",
    "PUR.ATTACH.UPLOAD",
    "PUR.ATTACH.VIEW",
    "PUR.LANDEDCOST.EDIT",
    "PUR.LANDEDCOST.POST",
    "PUR.LANDEDCOST.REVERSE",
    "PUR.LANDEDCOST.VIEW",
    "PUR.REQ.APPROVE",
    "PUR.REQ.CANCEL",
    "PUR.REQ.CLOSE",
    "PUR.REQ.CONVERT",
    "PUR.REQ.EDIT",
    "PUR.REQ.VIEW",
    "PURCH.CREATE",
    "PURCH.POST",
    "PURCH.VIEW",
    "PURCHASE_EDIT",
    "PURCHASE_VIEW",
    "REPORT_VIEW",
    "ROLE_EDIT",
    "ROLE_VIEW",
    "SALES.CREATE",
    "SALES.POST",
    "SALES.VIEW",
    "SALES_EDIT",
    "SALES_VIEW",
    "SETTINGS_EDIT",
    "SYS.BACKUP",
    "SYS.SETTINGS",
    "SYS.USERS",
    "SYSTEM_VIEW",
    "USER_CREATE",
    "USER_DELETE",
    "USER_EDIT",
    "USER_VIEW"
};
Check("catalog: uncovered set is exactly the documented 58-code drift",
    missing.Count == drift.Count && missing.All(drift.Contains),
    $"missing={missing.Count} extra={string.Join(",", missing.Where(m => !drift.Contains(m)).Take(5))}");

Exec($"INSERT INTO companies (company_id, company_code, company_name_ar, company_name_en) VALUES ('{companyId:D}', '{CompanyCode}', 'Perm Co', 'Perm Co');");
Exec($"INSERT INTO companies (company_id, company_code, company_name_ar, company_name_en) VALUES ('{company2Id:D}', '{Company2Code}', 'Other Co', 'Other Co');");
Exec($"INSERT INTO roles (role_id, company_id, role_code, role_name) VALUES (1, '{companyId:D}', 'ROLE_ADMINISTRATOR', 'Administrator');");
Exec($"INSERT INTO roles (role_id, company_id, role_code, role_name) VALUES (2, '{companyId:D}', 'ROLE_MIN', 'Minimal');");
Exec($"INSERT INTO users (user_id, company_id, username, password_hash, password_salt, full_name, full_name_ar, full_name_en) VALUES ('{adminId:D}', '{companyId:D}', 'padmin', 'x', 'x', 'Admin', 'Admin', 'Admin');");
Exec($"INSERT INTO users (user_id, company_id, username, password_hash, password_salt, full_name, full_name_ar, full_name_en) VALUES ('{minId:D}', '{companyId:D}', 'pmin', 'x', 'x', 'Minimal', 'Minimal', 'Minimal');");
Exec($"INSERT INTO users (user_id, company_id, username, password_hash, password_salt, full_name, full_name_ar, full_name_en) VALUES ('{otherId:D}', '{company2Id:D}', 'pother', 'x', 'x', 'Other', 'Other', 'Other');");
Exec($"INSERT INTO user_roles (user_id, role_id) VALUES ('{adminId:D}', 1);");
Exec($"INSERT INTO user_roles (user_id, role_id) VALUES ('{minId:D}', 2);");

var perms = AppHost.Permissions;

// ------------------------------------------------------- admin baseline
UsrSession.Apply(new UserInfo { Id = adminId.ToString(), Username = "padmin", AccessLevel = UsrAccLvl.Administrator, IsAuthenticated = true });
await perms.LoadForUserAsync("padmin");
Check("admin: loader grants the catalog", perms.Has(PermCat.Sales.InvoiceCreate) && perms.Has(PermCat.System.PermissionTemplatesManage));
var explainAdmin = await perms.ExplainAsync("padmin", PermCat.Sales.InvoiceCreate);
Check("inspector: administrator role named", explainAdmin.Allowed && explainAdmin.Source == PermSourceKind.AdministratorRole,
    $"{explainAdmin.Source}");

// ------------------------------------------------------- minimal user
await perms.LoadForUserAsync("pmin");
Check("minimal: nothing granted yet", !perms.Has(PermCat.Sales.InvoiceCreate));
var explainNone = await perms.ExplainAsync("pmin", PermCat.Sales.InvoiceCreate);
Check("inspector: granted nowhere", !explainNone.Allowed && explainNone.Source == PermSourceKind.None, $"{explainNone.Source}");

// ------------------------------------------------------- role grant
Exec($"INSERT INTO role_permissions (role_id, permission_id) VALUES (2, {PermId(PermCat.Sales.InvoiceCreate)});");
await perms.LoadForUserAsync("pmin");
Check("role grant: loader honors it", perms.Has(PermCat.Sales.InvoiceCreate));
var explainRole = await perms.ExplainAsync("pmin", PermCat.Sales.InvoiceCreate);
Check("inspector: role named", explainRole.Allowed && explainRole.Source == PermSourceKind.Role && explainRole.SourceName == "ROLE_MIN",
    $"{explainRole.Source}/{explainRole.SourceName}");

// ------------------------------------------------------- template grant
await perms.LoadForUserAsync("padmin");
var tplGrants = new[] { PermId(PermCat.Sales.InvoiceUpdate), PermId(PermCat.Documents.SalPost) };
var tplId = await PermTplSvc.SaveTemplateAsync(0, "TST_CASHIER", "Test Cashier", "Test Cashier", "harness", true,
    tplGrants, new[] { "pmin" });
Check("template: created with id", tplId > 0, $"{tplId}");

await perms.LoadForUserAsync("pmin");
Check("template grant: loader honors it", perms.Has(PermCat.Sales.InvoiceUpdate));
var explainTpl = await perms.ExplainAsync("pmin", PermCat.Sales.InvoiceUpdate);
Check("inspector: template named", explainTpl.Allowed && explainTpl.Source == PermSourceKind.Template && explainTpl.SourceName == "TST_CASHIER",
    $"{explainTpl.Source}/{explainTpl.SourceName}");
Check("template DOC grant: HasDoc true without the generic key",
    perms.HasDoc(PermCat.Documents.SalPost, PermCat.Sales.InvoicePost) && !perms.Has(PermCat.Sales.InvoicePost));

// ------------------------------------------------------- deny wins
Exec($"INSERT INTO user_permissions (user_id, permission_id, effect) VALUES ('{minId:D}', {PermId(PermCat.Sales.InvoiceUpdate)}, 0);");
await perms.LoadForUserAsync("pmin");
Check("deny wins over template grant", !perms.Has(PermCat.Sales.InvoiceUpdate) && perms.IsExplicitlyDenied(PermCat.Sales.InvoiceUpdate));
var explainDeny = await perms.ExplainAsync("pmin", PermCat.Sales.InvoiceUpdate);
Check("inspector: explicit deny named", !explainDeny.Allowed && explainDeny.Source == PermSourceKind.UserDeny, $"{explainDeny.Source}");

// ------------------------------------------------------- DOC semantics
Exec($"INSERT INTO user_permissions (user_id, permission_id, effect) VALUES ('{minId:D}', {PermId(PermCat.Purchasing.InvoicePost)}, 1);");
await perms.LoadForUserAsync("pmin");
Check("DOC: generic fallback allows", perms.HasDoc(PermCat.Documents.PurPost, PermCat.Purchasing.InvoicePost));
Exec($"INSERT INTO user_permissions (user_id, permission_id, effect) VALUES ('{minId:D}', {PermId(PermCat.Documents.PurPost)}, 0);");
await perms.LoadForUserAsync("pmin");
Check("DOC: DOC deny subtracts the one type, generic intact",
    !perms.HasDoc(PermCat.Documents.PurPost, PermCat.Purchasing.InvoicePost) && perms.Has(PermCat.Purchasing.InvoicePost));

var deniedBefore = ScalarLong("SELECT COUNT(*) FROM sau_document_events WHERE event_type = 'AccessDenied'");
var requireResult = perms.RequireDoc(PermCat.Documents.EntCreate, PermCat.Inventory.StockAdjust, "PURCHASE", "ENT");
var deniedAfter = ScalarLong("SELECT COUNT(*) FROM sau_document_events WHERE event_type = 'AccessDenied'");
Check("DOC: RequireDoc denies and writes the AccessDenied event", !requireResult && deniedAfter == deniedBefore + 1,
    $"{deniedBefore} -> {deniedAfter}");

// ------------------------------------------------------- per-user allow
Exec($"INSERT INTO user_permissions (user_id, permission_id, effect) VALUES ('{minId:D}', {PermId(PermCat.Sales.QuotationCreate)}, 1);");
var explainAllow = await perms.ExplainAsync("pmin", PermCat.Sales.QuotationCreate);
Check("inspector: per-user allow named", explainAllow.Allowed && explainAllow.Source == PermSourceKind.UserAllow, $"{explainAllow.Source}");

// ------------------------------------------------------- admin deny
Exec($"INSERT INTO user_permissions (user_id, permission_id, effect) VALUES ('{adminId:D}', {PermId("ACC.JOURNAL.VIEW")}, 0);");
await perms.LoadForUserAsync("padmin");
Check("deny wins over the administrator role", !perms.Has("ACC.JOURNAL.VIEW"));
var explainAdminDeny = await perms.ExplainAsync("padmin", "ACC.JOURNAL.VIEW");
Check("inspector: deny named for admin too", !explainAdminDeny.Allowed && explainAdminDeny.Source == PermSourceKind.UserDeny, $"{explainAdminDeny.Source}");

// ------------------------------------------------------- inspector edges
var explainUnknown = await perms.ExplainAsync("pmin", "NOPE.KEY.X");
Check("inspector: unknown key is granted nowhere", !explainUnknown.Allowed && explainUnknown.Source == PermSourceKind.None);
var ghostCode = await CatchCode(() => perms.ExplainAsync("ghost-user", PermCat.Sales.InvoiceCreate));
Check("inspector: unknown user rejected", ghostCode == "UNAUTH", ghostCode);

// ------------------------------------------------------- template lifecycle
var sysId = ScalarLong("SELECT template_id FROM permission_templates WHERE template_code = 'SYS_READ_ONLY'");
var immCode = await CatchCode(() => PermTplSvc.SaveTemplateAsync(sysId, "SYS_READ_ONLY", "X", "X", null, true, Array.Empty<long>(), Array.Empty<string>()));
Check("system template: save refused PRM-8504", immCode == ErrorCodes.PermissionTemplateSystemImmutable, immCode);
var immDelCode = await CatchCode(() => PermTplSvc.DeleteTemplateAsync(sysId));
Check("system template: delete refused PRM-8504", immDelCode == ErrorCodes.PermissionTemplateSystemImmutable, immDelCode);
var dupCode = await CatchCode(() => PermTplSvc.SaveTemplateAsync(0, "TST_CASHIER", "Dup", "Dup", null, true, Array.Empty<long>(), Array.Empty<string>()));
Check("duplicate code refused PRM-8503", dupCode == ErrorCodes.PermissionTemplateCodeDuplicate, dupCode);
var crossCode = await CatchCode(() => PermTplSvc.SaveTemplateAsync(0, "TST_X", "X", "X", null, true, Array.Empty<long>(), new[] { "pother" }));
Check("cross-company assignment refused PRM-8505", crossCode == ErrorCodes.PermissionTemplateCompanyMismatch, crossCode);

var copyId = await PermTplSvc.CopyTemplateAsync(tplId, "TST_COPY");
var (copyGrants, copyUsers) = await PermTplSvc.LoadTemplateDetailAsync(copyId);
Check("copy: grants copied, assignments not", copyGrants.Count == tplGrants.Length && copyUsers.Count == 0,
    $"grants={copyGrants.Count} users={copyUsers.Count}");

// Inactive templates grant nothing: re-point the copy at a DOC key
// nothing else grants (DOC.CNT.CREATE; pmin holds neither it nor its
// generic fallback INV.STOCK.ADJUST), assign it, then deactivate it.
await PermTplSvc.SaveTemplateAsync(copyId, "TST_COPY", "Test Cashier", "Test Cashier", "harness", true,
    new[] { PermId(PermCat.Documents.CntCreate) }, new[] { "pmin" });
await perms.LoadForUserAsync("pmin");
Check("copy assigned: its DOC grant flows", perms.HasDoc(PermCat.Documents.CntCreate, PermCat.Inventory.StockAdjust));
Exec($"UPDATE permission_templates SET is_active = 0 WHERE template_id = {copyId};");
await perms.LoadForUserAsync("pmin");
Check("inactive template grants nothing", !perms.HasDoc(PermCat.Documents.CntCreate, PermCat.Inventory.StockAdjust));

await perms.LoadForUserAsync("padmin");
await PermTplSvc.DeleteTemplateAsync(copyId);
var visibleCodes = await PermTplSvc.ListTemplatesAsync();
Check("delete: template leaves the visible list", visibleCodes.All(t => t.Code != "TST_COPY"));
Check("audit: template lifecycle rows written",
    ScalarLong("SELECT COUNT(*) FROM security_audit_log WHERE event_type IN ('PERM_TEMPLATE_CREATED','PERM_TEMPLATE_COPIED','PERM_TEMPLATE_DELETED')") >= 3,
    $"{ScalarLong("SELECT COUNT(*) FROM security_audit_log WHERE event_type LIKE 'PERM_TEMPLATE%'")}");

// ------------------------------------------------------- company scoping
Exec($"INSERT INTO permission_templates (company_id, template_code, name_ar, name_en, is_system, is_active) VALUES ('{company2Id:D}', 'FOREIGN', 'F', 'F', 0, 1);");
var foreignId = ScalarLong("SELECT template_id FROM permission_templates WHERE template_code = 'FOREIGN'");
Exec($"INSERT INTO permission_template_grants (template_id, permission_id) VALUES ({foreignId}, {PermId("ACC.JOURNAL.CREATE")});");
Exec($"INSERT INTO user_permission_templates (user_id, template_id, assigned_by) VALUES ('{minId:D}', {foreignId}, 'harness');");
await perms.LoadForUserAsync("pmin");
Check("company scoping: another company's template never leaks", !perms.Has("ACC.JOURNAL.CREATE"));

// ------------------------------------------------------- mutation guard
UsrSession.Apply(new UserInfo { Id = minId.ToString(), Username = "pmin", AccessLevel = UsrAccLvl.User, IsAuthenticated = true });
var guardCode = await CatchCode(() => PermTplSvc.SaveTemplateAsync(0, "TST_GUARD", "G", "G", null, true, Array.Empty<long>(), Array.Empty<string>()));
Check("guard: non-manager cannot save templates", guardCode == "UNAUTH", guardCode);
UsrSession.Apply(new UserInfo { Id = adminId.ToString(), Username = "padmin", AccessLevel = UsrAccLvl.Administrator, IsAuthenticated = true });

Console.WriteLine($"PERM HARNESS: {pass} PASS / {fail} FAIL");
return fail == 0 ? 0 : 1;
