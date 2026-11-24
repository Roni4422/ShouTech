// PeriodHarness — period-close proof (PERIOD_CLOSE_STUDY SS6).
// Seeds three companies on one migrated database and proves, through
// the real services (no SQL shortcuts for the actions under test):
//   schema: migrates through 127; events table + activation column exist;
//   listing: a period's unposted items = seeded journal draft + document
//     draft;
//   close: blocked by unposted items (ACC-5022), then succeeds; persists
//     across a fresh read with closed_by/at; CLOSE event recorded;
//   guard: posting into the closed period is refused with ACC-5021
//     NAMING the period on the GL resolver (the choke point for
//     journals / invoices / vouchers / POS / manufacturing) and on the
//     stock engine's stock-only paths (ApplyDocument + PostTransfer);
//   isolation: another company's same period stays open and postable;
//   permission: PermServ.Require (the window's exact gate) refuses a
//     permission-less user and audits the denial; the catalog carries
//     ACC.PERIOD.CLOSE / ACC.PERIOD.REOPEN;
//   reopen: reason required (ACC-5025); with a reason it succeeds, is
//     recorded in fiscal_period_events + security_audit_log, and
//     posting works again; a direct SQL reopen without an event is
//     refused by the database trigger;
//   order/balance: closing out of order is refused (ACC-5023); a period
//     with an unbalanced posted journal is refused (ACC-5026);
//   reports: the trial-balance integrity rows show the period CLOSED;
//   activation: a fresh company's year starts with period-end
//     activation ON (default); deactivating it rolls forward — old year
//     OFF, new year ON with 12 OPEN periods, audit row written; the
//     pre-existing CreateNextFiscalYear path now creates periods too.
using System.Globalization;
using Microsoft.Data.Sqlite;
using ShouTech.App;
using ShouTech.App.Configuration;
using ShouTech.App.Reporting.Invoices;
using ShouTech.App.Services;
using ShouTech.Application.Services;
using ShouTech.Domain;
using ShouTech.Domain.Entities;
using ShouTech.Infrastructure;

const string Actor = "period-harness";

var root = Path.Combine(Path.GetTempPath(), "period-harness-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var dbPath = Path.Combine(root, "period.db");
var cs = $"Data Source={dbPath};";

var companyA = Guid.NewGuid();
var companyB = Guid.NewGuid();
var companyC = Guid.NewGuid();

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
    return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
}

async Task<AppError?> CatchAsync(Func<Task> act)
{
    try { await act(); return null; }
    catch (AppError ex) { return ex; }
}

AppError? CatchSync(Action act)
{
    try { act(); return null; }
    catch (AppError ex) { return ex; }
}

// ---------------------------------------------------------------- setup
var migration = await SchemaMigrator.ApplyAllAsync("SQLite", dbPath);
Check("schema migrates", migration.Success, migration.Message);
Check("migration 127 applied",
    ScalarLong("SELECT COUNT(*) FROM schema_migrations WHERE migration_id = '127_period_close_events.sql'") == 1);
Check("fiscal_period_events table + period_end_activation column exist",
    ScalarLong("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='fiscal_period_events'") == 1 &&
    ScalarLong("SELECT COUNT(*) FROM pragma_table_info('fiscal_years') WHERE name='period_end_activation'") == 1);

var settings = AppCfgSvc.Current;
settings.ConnectionStrings["Default"] = new ConnectionStringSettings { Provider = "SQLite", ConnectionString = dbPath };
settings.Database ??= new DatabaseSettings();
settings.Database.DefaultProvider = "SQLite";

Exec($"INSERT INTO companies (company_id, company_code, company_name_ar, company_name_en) VALUES ('{companyA:D}', 'PERA', 'Period Co A', 'Period Co A');");
Exec($"INSERT INTO companies (company_id, company_code, company_name_ar, company_name_en) VALUES ('{companyB:D}', 'PERB', 'Period Co B', 'Period Co B');");
Exec($"INSERT INTO companies (company_id, company_code, company_name_ar, company_name_en) VALUES ('{companyC:D}', 'PERC', 'Period Co C', 'Period Co C');");

var ops = new OperationsService(new OperationsRepository("SQLite", cs));
var engine = new AccountingStructureRepository("SQLite", cs);

// Fiscal years + periods through the real engine (calendar 2026).
await engine.CreateFiscalYearAsync(companyA, 2026, Actor);
await engine.CreateFiscalYearAsync(companyB, 2026, Actor);
await engine.CreateFiscalYearAsync(companyC, 2026, Actor);

var yearsA = await ops.GetFiscalYearsAsync("PERA");
var periodsA = await ops.GetFiscalPeriodsAsync("PERA", yearsA[0].Id);
Check("company A has 12 OPEN periods for 2026", periodsA.Count == 12 && periodsA.All(p => p.Status == "OPEN"),
    $"count={periodsA.Count}");
var jan = periodsA[0];
var feb = periodsA[1];
var mar = periodsA[2];
Check("period names are yyyy-MM", jan.PeriodName == "2026-01" && feb.PeriodName == "2026-02",
    $"{jan.PeriodName}/{feb.PeriodName}");

// ------------------------------------------------------- seeds (company A)
// Two real postable accounts (the journal post trigger validates lines
// against the chart). Fixed ids keep the line seeds readable.
var acctCash = Guid.Parse("11111111-1111-1111-1111-111111111111");
var acctSales = Guid.Parse("22222222-2222-2222-2222-222222222222");
Exec($"INSERT INTO chart_of_accounts (account_id, company_id, account_code, account_name, account_type, is_postable, is_active, is_deleted, created_at, risk, cost_center_mode, allow_reconcile, taxable, foreign_currency, cost_required, budget, is_default, tax_rate, description, warning_side, account_side, estimated_budget, exchange_parity) VALUES ('{acctCash:D}', '{companyA:D}', '131', 'Cash', 'ASSET', 1, 1, 0, '2026-01-01T00:00:00', 'NONE', 'NONE', 0, 0, 0, 0, 0, 0, 0, '', 'NONE', 'DEBIT', 0, 1);");
Exec($"INSERT INTO chart_of_accounts (account_id, company_id, account_code, account_name, account_type, is_postable, is_active, is_deleted, created_at, risk, cost_center_mode, allow_reconcile, taxable, foreign_currency, cost_required, budget, is_default, tax_rate, description, warning_side, account_side, estimated_budget, exchange_parity) VALUES ('{acctSales:D}', '{companyA:D}', '411', 'Sales', 'REVENUE', 1, 1, 0, '2026-01-01T00:00:00', 'NONE', 'NONE', 0, 0, 0, 0, 0, 0, 0, '', 'NONE', 'CREDIT', 0, 1);");

// One draft journal assigned to January + one draft sales document
// dated inside January: the two unposted items.
var draftEntry = Guid.NewGuid();
Exec($"INSERT INTO journal_entries (journal_entry_id, company_id, entry_date, description, status, created_by, created_at, fiscal_period_id, document_number, currency_code, exchange_rate, is_deleted) VALUES ('{draftEntry:D}', '{companyA:D}', '2026-01-10', 'Draft January journal', 0, '{Actor}', '2026-01-10T00:00:00', '{jan.Id:D}', 'JV-DRAFT-1', 'KWD', 1, 0);");
Exec($"INSERT INTO journal_entry_lines (journal_entry_id, line_number, account_id, debit, credit) VALUES ('{draftEntry:D}', 1, '{acctCash:D}', 50, 0);");
Exec($"INSERT INTO sales_documents (doc_id, company_id, doc_kind, doc_no, doc_date, party_id, party_name, warehouse_id, grand_total, paid_amount, status, is_deleted) VALUES (700, '{companyA:D}', 'SAL', 'SAL-DRAFT-1', '2026-01-15', 1, 'Seed Customer', 1, 250, 0, 'Draft', 0);");
// One posted BALANCED journal in February (trial-balance status proof).
var febEntry = Guid.NewGuid();
Exec($"INSERT INTO journal_entries (journal_entry_id, company_id, entry_date, description, status, created_by, created_at, fiscal_period_id, document_number, currency_code, exchange_rate, is_deleted) VALUES ('{febEntry:D}', '{companyA:D}', '2026-02-10', 'Posted February journal', 0, '{Actor}', '2026-02-10T00:00:00', '{feb.Id:D}', 'JV-FEB-1', 'KWD', 1, 0);");
Exec($"INSERT INTO journal_entry_lines (journal_entry_id, line_number, account_id, debit, credit) VALUES ('{febEntry:D}', 1, '{acctCash:D}', 100, 0);");
Exec($"INSERT INTO journal_entry_lines (journal_entry_id, line_number, account_id, debit, credit) VALUES ('{febEntry:D}', 2, '{acctSales:D}', 0, 100);");
Exec($"UPDATE journal_entries SET status = 1 WHERE journal_entry_id = '{febEntry:D}';");
// One posted UNBALANCED journal in March (ACC-5026 proof).
var marEntry = Guid.NewGuid();
Exec($"INSERT INTO journal_entries (journal_entry_id, company_id, entry_date, description, status, created_by, created_at, fiscal_period_id, document_number, currency_code, exchange_rate, is_deleted) VALUES ('{marEntry:D}', '{companyA:D}', '2026-03-10', 'Unbalanced March journal', 0, '{Actor}', '2026-03-10T00:00:00', '{mar.Id:D}', 'JV-MAR-1', 'KWD', 1, 0);");
Exec($"INSERT INTO journal_entry_lines (journal_entry_id, line_number, account_id, debit, credit) VALUES ('{marEntry:D}', 1, '{acctCash:D}', 100, 0);");
Exec($"INSERT INTO journal_entry_lines (journal_entry_id, line_number, account_id, debit, credit) VALUES ('{marEntry:D}', 2, '{acctSales:D}', 0, 40);");
Exec($"UPDATE journal_entries SET status = 1 WHERE journal_entry_id = '{marEntry:D}';");
// Stock seeds for the stock-engine guard proofs (keyed by company code).
Exec("INSERT INTO materials (material_id, company_id, code, name_ar, name_en, is_stocked, is_service, avg_cost, cost, price_retail, price_wholesale, is_deleted) VALUES (42, 'PERA', 'MAT-42', 'Item', 'Item', 1, 0, 10, 10, 20, 18, 0);");
Exec("INSERT INTO warehouses (warehouse_id, company_id, warehouse_code, warehouse_name_ar, warehouse_name_en, is_active, is_deleted) VALUES (1, 'PERA', 1, 'Main', 'Main', 1, 0);");
Exec("INSERT INTO warehouses (warehouse_id, company_id, warehouse_code, warehouse_name_ar, warehouse_name_en, is_active, is_deleted) VALUES (2, 'PERA', 2, 'Second', 'Second', 1, 0);");

// ------------------------------------------------------------- listing
var unposted = await ops.GetPeriodUnpostedItemsAsync("PERA", jan.Id);
Check("unposted listing matches seeded drafts (1 journal + 1 document)",
    unposted.Count == 2 && unposted.Any(u => u.Kind == "JOURNAL" && u.DocNo == "JV-DRAFT-1") &&
    unposted.Any(u => u.Kind == "DOCUMENT" && u.DocNo == "SAL-DRAFT-1"),
    $"count={unposted.Count}");

// ------------------------------------------------- permission gate proof
AppState.SetCompany("PERA", "Period Co A", "Period Co A");
var denied = new PermServ();
Check("permission-less user lacks ACC.PERIOD.CLOSE", !denied.Has(PermCat.System.PeriodClose));
Check("PermServ.Require refuses the permission-less user (window's gate)",
    !denied.Require(PermCat.System.PeriodClose, docKind: "PERIOD", docNo: "2026-01"));
Check("the refusal is audited (AccessDenied event naming the permission)",
    ScalarLong("SELECT COUNT(*) FROM sau_document_events WHERE event_type = 'AccessDenied' AND details LIKE '%ACC.PERIOD.CLOSE%'") >= 1);
var granted = new PermServ();
granted.GrantAllCatalogPermissions(Actor);
Check("catalog carries ACC.PERIOD.CLOSE + ACC.PERIOD.REOPEN",
    granted.Has(PermCat.System.PeriodClose) && granted.Has(PermCat.System.PeriodReopen));
Check("granted user passes PermServ.Require",
    granted.Require(PermCat.System.PeriodClose, docKind: "PERIOD", docNo: "2026-01"));

// ------------------------------------------------------------ close flow
var blocked = await CatchAsync(() => ops.ClosePeriodAsync("PERA", jan.Id, Actor));
Check("close blocked by unposted items (ACC-5022)",
    blocked is { Code: ErrorCodes.PeriodCloseUnpostedItems } && blocked.Args.Count >= 2 &&
    Convert.ToInt64(blocked.Args[1], CultureInfo.InvariantCulture) == 2,
    blocked is null ? "no error" : $"{blocked.Code} args=[{string.Join(", ", blocked.Args)}]");

Exec($"DELETE FROM journal_entry_lines WHERE journal_entry_id = '{draftEntry:D}';");
Exec($"DELETE FROM journal_entries WHERE journal_entry_id = '{draftEntry:D}';");
Exec("DELETE FROM sales_documents WHERE doc_id = 700;");
var remaining = await ops.GetPeriodUnpostedItemsAsync("PERA", jan.Id);
Check("unposted listing empties after the drafts are removed", remaining.Count == 0, $"count={remaining.Count}");

await ops.ClosePeriodAsync("PERA", jan.Id, Actor);
// Fresh read through NEW service instances: the close persisted.
var opsFresh = new OperationsService(new OperationsRepository("SQLite", cs));
var periodsFresh = await opsFresh.GetFiscalPeriodsAsync("PERA", yearsA[0].Id);
var janFresh = periodsFresh[0];
Check("close persists across a fresh read (CLOSED + closed_by/at)",
    janFresh.Status == "CLOSED" && janFresh.ClosedBy == Actor && !string.IsNullOrWhiteSpace(janFresh.ClosedAt),
    $"status={janFresh.Status} by={janFresh.ClosedBy}");
Check("CLOSE event recorded in fiscal_period_events",
    ScalarLong($"SELECT COUNT(*) FROM fiscal_period_events WHERE fiscal_period_id = '{jan.Id:D}' AND event_type = 'CLOSE' AND actor = '{Actor}'") == 1);

// --------------------------------------------------------- posting guard
string Resolve(Guid company, DateOnly date)
{
    using var connection = new SqliteConnection(cs);
    connection.Open();
    using var tx = connection.BeginTransaction();
    return GlPosting.ResolveOpenPeriod(connection, tx, "SQLite", company, date);
}

var glRefusal = CatchSync(() => Resolve(companyA, new DateOnly(2026, 1, 15)));
Check("GL resolver refuses posting into the closed period (ACC-5021 naming it)",
    glRefusal is { Code: ErrorCodes.PeriodClosedPosting } && glRefusal.Args.Count >= 1 &&
    Convert.ToString(glRefusal.Args[0], CultureInfo.InvariantCulture) == "2026-01",
    glRefusal is null ? "no error" : $"{glRefusal.Code} args=[{string.Join(", ", glRefusal.Args)}]");

var stockIn = new InvDoc
{
    Kind = InvDocKind.StockIn,
    DocNo = "ENT-HARNESS-1",
    DocDate = new DateTime(2026, 1, 12),
    WarehouseId = 1,
    Lines = new List<InvLine> { new() { LineNo = 1, MaterialId = 42, MaterialCode = "MAT-42", Qty = 5, UnitPrice = 10 } }
};
AppError? stockRefusal;
using (var connection = new SqliteConnection(cs))
{
    connection.Open();
    using var tx = connection.BeginTransaction();
    stockRefusal = CatchSync(() => StkPost.ApplyDocument(connection, tx, stockIn));
}
Check("stock-only document (stock entry, no GL) refused into the closed period (ACC-5021)",
    stockRefusal is { Code: ErrorCodes.PeriodClosedPosting }, stockRefusal?.Code ?? "no error");

var transferRefusal = CatchSync(() => StkPost.PostTransfer(1, 2, new DateTime(2026, 1, 20), "harness",
    new List<StkPost.StockTransferLine> { new(42, "MAT-42", 1m) }));
Check("stock transfer refused into the closed period (ACC-5021)",
    transferRefusal is { Code: ErrorCodes.PeriodClosedPosting }, transferRefusal?.Code ?? "no error");

// -------------------------------------------------------------- isolation
var yearsB = await ops.GetFiscalYearsAsync("PERB");
var periodsB = await ops.GetFiscalPeriodsAsync("PERB", yearsB[0].Id);
Check("another company's same period stays OPEN", periodsB[0].Status == "OPEN", periodsB[0].Status);
var bResolved = CatchSync(() => Resolve(companyB, new DateOnly(2026, 1, 15)));
Check("posting into company B's January still resolves a period", bResolved is null && Resolve(companyB, new DateOnly(2026, 1, 15)).Length > 0);

// --------------------------------------------------------------- reopen
var noReason = await CatchAsync(() => ops.ReopenPeriodAsync("PERA", jan.Id, Actor, "  "));
Check("reopen without a reason is refused (ACC-5025)",
    noReason is { Code: ErrorCodes.PeriodReopenReasonRequired }, noReason?.Code ?? "no error");
await ops.ReopenPeriodAsync("PERA", jan.Id, Actor, "Correcting a late supplier invoice");
var janReopened = (await ops.GetFiscalPeriodsAsync("PERA", yearsA[0].Id))[0];
Check("reopen with a reason succeeds (OPEN, close stamp cleared)",
    janReopened.Status == "OPEN" && janReopened.ClosedBy is null, janReopened.Status);
Check("REOPEN event recorded with the reason",
    ScalarLong($"SELECT COUNT(*) FROM fiscal_period_events WHERE fiscal_period_id = '{jan.Id:D}' AND event_type = 'REOPEN' AND reason LIKE '%late supplier invoice%'") == 1);
Check("reopen written to security_audit_log (PERIOD_REOPEN)",
    ScalarLong("SELECT COUNT(*) FROM security_audit_log WHERE event_type = 'PERIOD_REOPEN'") == 1);
var postReopen = CatchSync(() => Resolve(companyA, new DateOnly(2026, 1, 15)));
Check("posting into the reopened period works again", postReopen is null, postReopen?.Code ?? "");

// ------------------------------------------------- order + trigger proof
var outOfOrder = await CatchAsync(() => ops.ClosePeriodAsync("PERA", feb.Id, Actor));
Check("closing February while January is open is refused (ACC-5023)",
    outOfOrder is { Code: ErrorCodes.PeriodCloseOutOfOrder }, outOfOrder?.Code ?? "no error");
await ops.ClosePeriodAsync("PERA", jan.Id, Actor);
var triggerBlocked = false;
try
{
    Exec($"UPDATE fiscal_periods SET status = 'OPEN' WHERE fiscal_period_id = '{jan.Id:D}';");
}
catch (SqliteException)
{
    triggerBlocked = true;
}
Check("direct SQL reopen without a REOPEN event is refused by the DB trigger", triggerBlocked);
var janStillClosed = (await ops.GetFiscalPeriodsAsync("PERA", yearsA[0].Id))[0];
Check("period is still CLOSED after the blocked SQL reopen", janStillClosed.Status == "CLOSED", janStillClosed.Status);

// ------------------------------------------- balance guard + report mark
await ops.ClosePeriodAsync("PERA", feb.Id, Actor);
var tb = await ops.GetTrialBalanceIntegrityAsync("PERA");
var febRow = tb.Periods.FirstOrDefault(p => p.Period == "2026-02");
Check("trial-balance integrity shows February as CLOSED",
    febRow is { Status: "CLOSED" }, febRow?.Status ?? "row missing");
var unbalanced = await CatchAsync(() => ops.ClosePeriodAsync("PERA", mar.Id, Actor));
Check("closing a period with an unbalanced posted journal is refused (ACC-5026)",
    unbalanced is { Code: ErrorCodes.PeriodCloseUnbalancedJournals }, unbalanced?.Code ?? "no error");
Exec($"DELETE FROM journal_entry_lines WHERE journal_entry_id = '{marEntry:D}';");
Exec($"DELETE FROM journal_entries WHERE journal_entry_id = '{marEntry:D}';");
await ops.ClosePeriodAsync("PERA", mar.Id, Actor);
var marFresh = (await ops.GetFiscalPeriodsAsync("PERA", yearsA[0].Id))[2];
Check("March closes once the unbalanced journal is removed", marFresh.Status == "CLOSED", marFresh.Status);

// ------------------------------------------------------------- activation
var yearsC = await ops.GetFiscalYearsAsync("PERC");
Check("fresh company's fiscal year starts with period-end activation ON (default)",
    yearsC.Count == 1 && yearsC[0].PeriodEndActivation, $"count={yearsC.Count}");
await ops.DeactivatePeriodEndAndRollForwardAsync("PERC", Actor);
var yearsCAfter = await ops.GetFiscalYearsAsync("PERC");
Check("deactivation rolls forward: old year OFF, new year ON",
    yearsCAfter.Count == 2 && !yearsCAfter[0].PeriodEndActivation && yearsCAfter[1].PeriodEndActivation,
    $"count={yearsCAfter.Count}");
var periodsC2 = await ops.GetFiscalPeriodsAsync("PERC", yearsCAfter[1].Id);
Check("the rolled-forward year carries 12 OPEN periods",
    periodsC2.Count == 12 && periodsC2.All(p => p.Status == "OPEN"), $"count={periodsC2.Count}");
Check("roll-forward is audited (FISCAL_YEAR_ROLL_FORWARD)",
    ScalarLong("SELECT COUNT(*) FROM security_audit_log WHERE event_type = 'FISCAL_YEAR_ROLL_FORWARD'") == 1);
var cResolved = CatchSync(() => Resolve(companyC, new DateOnly(2027, 3, 15)));
Check("posting resolves inside the rolled-forward year", cResolved is null, cResolved?.Code ?? "");

// RolloverWin's own path (CreateNextFiscalYearAsync) now creates periods.
await ops.CreateNextFiscalYearAsync("PERB", Actor);
var yearsBAfter = await ops.GetFiscalYearsAsync("PERB");
var periodsB2 = await ops.GetFiscalPeriodsAsync("PERB", yearsBAfter[1].Id);
Check("CreateNextFiscalYearAsync (RolloverWin) now creates the 12 periods too",
    yearsBAfter.Count == 2 && periodsB2.Count == 12, $"years={yearsBAfter.Count} periods={periodsB2.Count}");

Console.WriteLine($"== PeriodHarness: {pass} PASS, {fail} FAIL ==");
return fail == 0 ? 0 : 1;
