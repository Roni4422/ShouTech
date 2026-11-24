// RecalcHarness — Recalculate Costs WITH GL restatement proof
// (feat/recalc-costs-gl). Seeds company RECALC where two purchase
// prices were corrected after posting, so the hand-computed outcome is:
//
//   MAT-A: bought 100 @ 10, sold 40  -> 60 on hand valued 600.
//          Price corrected to 13     -> value 780, delta +180,
//          adjustment Dr 1203 180 / Cr 401 180.
//   MAT-B: bought 50 @ 20            -> value 1000.
//          Price corrected to 18     -> value 900, delta -100,
//          adjustment Dr 401 100 / Cr 1203 100.
//   MAT-S: a SERVICE material (never stock) with a bogus balance of
//          990 — excluded from the preview and untouched by the run.
//
// Opening GL: Dr 1203 1600 / Cr 32 1600, so after approval the GL
// inventory balance (1600 + 180 - 100 = 1680) must equal the restated
// stock value (780 + 900 = 1680).
//
// Asserted, in order: preview shows exactly the hand-computed changes
// and balanced adjustments and writes NOTHING (GL and balances
// untouched); approving a STALE preview (data changed after it) is
// refused with INV-6017 and still posts nothing; approving a fresh
// preview posts the two entries through GlPosting with the run link,
// persists the run record + lines, and makes stock value and GL
// agree; a second preview over the same range is empty (idempotent);
// a range ending before a later movement is refused (INV-6018); a
// closed period is refused (INV-6004); the default range is the open
// period containing today.
using System.Globalization;
using Microsoft.Data.Sqlite;
using ShouTech.Domain;
using ShouTech.Domain.Entities;
using ShouTech.Infrastructure;

const string Actor = "recalc-harness";
const string CompanyCode = "RECALC";
const string ClosedCompanyCode = "RECLC";

var root = Path.Combine(Path.GetTempPath(), "recalc-harness-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var dbPath = Path.Combine(root, "recalc.db");
var cs = $"Data Source={dbPath};";
var companyId = Guid.NewGuid();
var closedCompanyId = Guid.NewGuid();

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

object? Scalar(string sql)
{
    using var connection = new SqliteConnection(cs);
    connection.Open();
    using var cmd = connection.CreateCommand();
    cmd.CommandText = sql;
    return cmd.ExecuteScalar();
}

decimal Dec(object? value) => value is null or DBNull ? 0m : Convert.ToDecimal(value, CultureInfo.InvariantCulture);
long Lng(object? value) => value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
string Txt(object? value) => value is null or DBNull ? "" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";

decimal GlAccountBalance(string accountCode, Guid company) =>
    Dec(Scalar($"SELECT COALESCE(SUM(l.debit - l.credit), 0) FROM journal_entry_lines l " +
               $"JOIN journal_entries e ON e.journal_entry_id = l.journal_entry_id " +
               $"WHERE e.company_id = '{company:D}' AND e.status = 'Posted' AND e.is_deleted = 0 " +
               $"AND l.account_code = '{accountCode}' AND l.is_deleted = 0"));

decimal StockBalance(long materialId) =>
    Dec(Scalar($"SELECT COALESCE(stock_value, 0) FROM material_stock_balances " +
               $"WHERE company_id = '{CompanyCode}' AND warehouse_id = 1 AND material_id = {materialId} AND is_deleted = 0"));

long StkRevalEntryCount() =>
    Lng(Scalar($"SELECT COUNT(*) FROM journal_entries WHERE company_id = '{companyId:D}' " +
               $"AND source_doc_type = 'STKREVAL' AND status = 'Posted' AND is_deleted = 0"));

// ---------------------------------------------------------------- setup
var migration = await SchemaMigrator.ApplyAllAsync("SQLite", dbPath);
Check("schema migrates (through 127)", migration.Success, migration.Message);

Exec($"INSERT INTO companies (company_id, company_code, company_name_ar, company_name_en) VALUES ('{companyId:D}', '{CompanyCode}', 'Recalc Co', 'Recalc Co');");
Exec($"INSERT INTO companies (company_id, company_code, company_name_ar, company_name_en) VALUES ('{closedCompanyId:D}', '{ClosedCompanyCode}', 'Closed Co', 'Closed Co');");

foreach (var (code, name, type) in new[]
         {
             (GlAccounts.Inventory, "Inventory", "ASSET"),
             (GlAccounts.Cogs, "Cost of Goods Sold", "EXPENSE"),
             (GlAccounts.OpeningBalanceEquity, "Retained Earnings", "EQUITY")
         })
    Exec($"INSERT INTO chart_of_accounts (account_id, company_id, account_code, account_name, account_type, is_postable, is_active, is_deleted) VALUES ('{Guid.NewGuid():D}', '{companyId:D}', '{code}', '{name}', '{type}', 1, 1, 0);");

var structure = new AccountingStructureRepository("SQLite", cs);
await structure.CreateFiscalYearAsync(companyId, 2026, Actor);
await structure.CreateFiscalYearAsync(closedCompanyId, 2026, Actor);

// Opening entry through the real engine: Dr 1203 1600 / Cr 32 1600.
{
    using var connection = new SqliteConnection(cs);
    connection.Open();
    using var tx = connection.BeginTransaction();
    var opening = new GlDocumentRequest(companyId, new DateOnly(2026, 1, 1),
        "Opening stock value", "OPENING", "OPEN-1", "OPN-1", "SAR", 1m,
        new[]
        {
            new GlLineRequest(GlAccounts.Inventory, 1600m, 0m),
            new GlLineRequest(GlAccounts.OpeningBalanceEquity, 0m, 1600m)
        });
    GlPosting.PostInTransaction(connection, tx, "SQLite", opening, Actor);
    tx.Commit();
}
Check("opening entry posts (GL inventory = 1600)", GlAccountBalance(GlAccounts.Inventory, companyId) == 1600m,
    GlAccountBalance(GlAccounts.Inventory, companyId).ToString(CultureInfo.InvariantCulture));

// Materials, movements, balances — the state AFTER the original
// postings at the ORIGINAL prices.
Exec($"INSERT INTO warehouses (warehouse_id, company_id, warehouse_code, warehouse_name_ar, warehouse_name_en, is_active, is_deleted) VALUES (1, '{CompanyCode}', 1, 'Main', 'Main', 1, 0);");
Exec($"INSERT INTO materials (material_id, company_id, code, name_ar, name_en, is_stocked, is_service, is_deleted) VALUES (101, '{CompanyCode}', 'MAT-A', 'Material A', 'Material A', 1, 0, 0);");
Exec($"INSERT INTO materials (material_id, company_id, code, name_ar, name_en, is_stocked, is_service, is_deleted) VALUES (102, '{CompanyCode}', 'MAT-B', 'Material B', 'Material B', 1, 0, 0);");
Exec($"INSERT INTO materials (material_id, company_id, code, name_ar, name_en, is_stocked, is_service, is_deleted) VALUES (103, '{CompanyCode}', 'MAT-S', 'Service S', 'Service S', 0, 1, 0);");

Exec($"INSERT INTO material_stock_movements (company_id, warehouse_id, material_id, movement_date, movement_type, document_no, qty_in, qty_out, unit_cost, is_deleted) VALUES ('{CompanyCode}', 1, 101, '2026-01-10', 'PUR', 'PUR-1', 100, 0, 10, 0);");
Exec($"INSERT INTO material_stock_movements (company_id, warehouse_id, material_id, movement_date, movement_type, document_no, qty_in, qty_out, unit_cost, is_deleted) VALUES ('{CompanyCode}', 1, 101, '2026-01-15', 'SAL', 'SAL-1', 0, 40, 10, 0);");
Exec($"INSERT INTO material_stock_movements (company_id, warehouse_id, material_id, movement_date, movement_type, document_no, qty_in, qty_out, unit_cost, is_deleted) VALUES ('{CompanyCode}', 1, 102, '2026-01-11', 'PUR', 'PUR-2', 50, 0, 20, 0);");
Exec($"INSERT INTO material_stock_movements (company_id, warehouse_id, material_id, movement_date, movement_type, document_no, qty_in, qty_out, unit_cost, is_deleted) VALUES ('{CompanyCode}', 1, 103, '2026-01-12', 'PUR', 'PUR-3', 10, 0, 99, 0);");

Exec($"INSERT INTO material_stock_balances (company_id, warehouse_id, material_id, available_qty, reserved_qty, average_cost, last_cost, stock_value, is_deleted) VALUES ('{CompanyCode}', 1, 101, 60, 0, 10, 10, 600, 0);");
Exec($"INSERT INTO material_stock_balances (company_id, warehouse_id, material_id, available_qty, reserved_qty, average_cost, last_cost, stock_value, is_deleted) VALUES ('{CompanyCode}', 1, 102, 50, 0, 20, 20, 1000, 0);");
Exec($"INSERT INTO material_stock_balances (company_id, warehouse_id, material_id, available_qty, reserved_qty, average_cost, last_cost, stock_value, is_deleted) VALUES ('{CompanyCode}', 1, 103, 10, 0, 99, 99, 990, 0);");

// The purchase-price corrections the recalculation must pick up.
Exec("UPDATE material_stock_movements SET unit_cost = 13 WHERE company_id = '" + CompanyCode + "' AND document_no = 'PUR-1';");
Exec("UPDATE material_stock_movements SET unit_cost = 18 WHERE company_id = '" + CompanyCode + "' AND document_no = 'PUR-2';");

var svc = new StockRecalculationService("SQLite", cs);
var from = new DateOnly(2026, 1, 1);
var to = new DateOnly(2026, 12, 31);

// ---------------------------------------------------------------- preview
var preview = await svc.PreviewAsync(CompanyCode, null, null, from, to);
Check("preview replays 3 movements (service material skipped)", preview.MovementsReplayed == 3,
    preview.MovementsReplayed.ToString(CultureInfo.InvariantCulture));
Check("preview lists exactly 2 cost changes", preview.Changes.Count == 2,
    preview.Changes.Count.ToString(CultureInfo.InvariantCulture));

var changeA = preview.Changes.FirstOrDefault(c => c.MaterialId == 101);
Check("MAT-A change: 60 on hand, 10 -> 13, 600 -> 780, delta +180",
    changeA is not null && changeA.QtyOnHand == 60m && changeA.OldAvgCost == 10m && changeA.NewAvgCost == 13m
    && changeA.OldValue == 600m && changeA.NewValue == 780m && changeA.ValueDelta == 180m,
    changeA?.ToString() ?? "missing");
var changeB = preview.Changes.FirstOrDefault(c => c.MaterialId == 102);
Check("MAT-B change: 50 on hand, 20 -> 18, 1000 -> 900, delta -100",
    changeB is not null && changeB.QtyOnHand == 50m && changeB.OldAvgCost == 20m && changeB.NewAvgCost == 18m
    && changeB.OldValue == 1000m && changeB.NewValue == 900m && changeB.ValueDelta == -100m,
    changeB?.ToString() ?? "missing");
Check("service material absent from preview",
    preview.Changes.All(c => c.MaterialId != 103) && preview.Adjustments.All(a => a.MaterialId != 103));
Check("preview total delta = +80", preview.TotalDelta == 80m,
    preview.TotalDelta.ToString(CultureInfo.InvariantCulture));

Check("preview lists exactly 2 adjustments", preview.Adjustments.Count == 2,
    preview.Adjustments.Count.ToString(CultureInfo.InvariantCulture));
var adjA = preview.Adjustments.FirstOrDefault(a => a.MaterialId == 101);
var adjB = preview.Adjustments.FirstOrDefault(a => a.MaterialId == 102);
Check("MAT-A adjustment: +180 against 1203/401",
    adjA is not null && adjA.Amount == 180m && adjA.InventoryAccountCode == "1203" && adjA.OffsetAccountCode == "401",
    adjA?.ToString() ?? "missing");
Check("MAT-B adjustment: -100 against 1203/401",
    adjB is not null && adjB.Amount == -100m && adjB.InventoryAccountCode == "1203" && adjB.OffsetAccountCode == "401",
    adjB?.ToString() ?? "missing");

// The preview must write NOTHING.
Check("preview leaves GL untouched (no STKREVAL entries, inventory still 1600)",
    StkRevalEntryCount() == 0 && GlAccountBalance(GlAccounts.Inventory, companyId) == 1600m);
Check("preview leaves stock balances untouched (A 600, B 1000, S 990)",
    StockBalance(101) == 600m && StockBalance(102) == 1000m && StockBalance(103) == 990m);
Check("preview writes no job rows",
    Lng(Scalar($"SELECT COUNT(*) FROM stock_revaluation_jobs WHERE company_id = '{CompanyCode}'")) == 0);

// ------------------------------------------------------- stale approval
Exec("UPDATE material_stock_movements SET unit_cost = 19 WHERE company_id = '" + CompanyCode + "' AND document_no = 'PUR-2';");
string? staleCode = null;
try
{
    await svc.ApplyAsync(CompanyCode, null, null, from, to, "stale approval probe", Actor,
        new DateOnly(2026, 6, 15), preview);
}
catch (AppError ex) { staleCode = ex.Code; }
Check("stale preview approval refused with INV-6017", staleCode == ErrorCodes.RecalculationChangedSincePreview,
    staleCode ?? "no error");
Check("stale approval posted nothing and rolled the replay back",
    StkRevalEntryCount() == 0 && StockBalance(101) == 600m && StockBalance(102) == 1000m);
Check("stale approval leaves a Failed run record naming INV-6017",
    Txt(Scalar($"SELECT error FROM stock_revaluation_jobs WHERE company_id = '{CompanyCode}' AND status = 'Failed' ORDER BY created_at DESC LIMIT 1"))
        .Contains(ErrorCodes.RecalculationChangedSincePreview));
Exec("UPDATE material_stock_movements SET unit_cost = 18 WHERE company_id = '" + CompanyCode + "' AND document_no = 'PUR-2';");

// ---------------------------------------------------------------- apply
var freshPreview = await svc.PreviewAsync(CompanyCode, null, null, from, to);
var applied = await svc.ApplyAsync(CompanyCode, null, null, from, to, "price corrections Jan", Actor,
    new DateOnly(2026, 6, 15), freshPreview);
Check("apply posts exactly 2 adjustment entries", applied.PostedEntries.Count == 2,
    applied.PostedEntries.Count.ToString(CultureInfo.InvariantCulture));
Check("apply reports the run delta +80", applied.ValueDelta == 80m,
    applied.ValueDelta.ToString(CultureInfo.InvariantCulture));
Check("ledger holds 2 posted STKREVAL entries", StkRevalEntryCount() == 2);
Check("every STKREVAL entry is balanced",
    Lng(Scalar($"SELECT COUNT(*) FROM (SELECT l.journal_entry_id FROM journal_entry_lines l " +
               $"JOIN journal_entries e ON e.journal_entry_id = l.journal_entry_id " +
               $"WHERE e.company_id = '{companyId:D}' AND e.source_doc_type = 'STKREVAL' AND l.is_deleted = 0 " +
               $"GROUP BY l.journal_entry_id HAVING SUM(l.debit) <> SUM(l.credit))")) == 0);
Check("every STKREVAL entry carries the run link (source id = run id + material)",
    applied.PostedEntries.All(p => Txt(Scalar(
        $"SELECT source_doc_id FROM journal_entries WHERE journal_entry_id = '{p.JournalEntryId:D}'"))
        == $"{applied.JobId}:{p.MaterialId}"));
Check("GL inventory balance = 1680 after approval", GlAccountBalance(GlAccounts.Inventory, companyId) == 1680m,
    GlAccountBalance(GlAccounts.Inventory, companyId).ToString(CultureInfo.InvariantCulture));
Check("GL COGS balance = -80 after approval (180 credit - 100 debit)", GlAccountBalance(GlAccounts.Cogs, companyId) == -80m,
    GlAccountBalance(GlAccounts.Cogs, companyId).ToString(CultureInfo.InvariantCulture));
Check("stock value and GL inventory agree (780 + 900 = 1680)",
    StockBalance(101) == 780m && StockBalance(102) == 900m
    && StockBalance(101) + StockBalance(102) == GlAccountBalance(GlAccounts.Inventory, companyId));
Check("service material balance untouched by the run", StockBalance(103) == 990m);

// Run record + lines.
Check("run record persisted Done with range end and delta",
    Txt(Scalar($"SELECT status FROM stock_revaluation_jobs WHERE job_id = '{applied.JobId}'")) == "Done"
    && Txt(Scalar($"SELECT to_date FROM stock_revaluation_jobs WHERE job_id = '{applied.JobId}'")) == "2026-12-31"
    && Dec(Scalar($"SELECT value_delta FROM stock_revaluation_jobs WHERE job_id = '{applied.JobId}'")) == 80m);
var recordedIds = Txt(Scalar($"SELECT gl_entry_ids FROM stock_revaluation_jobs WHERE job_id = '{applied.JobId}'"));
Check("run record lists both posted entry ids",
    applied.PostedEntries.All(p => recordedIds.Contains(p.JournalEntryId.ToString("D"))), recordedIds);
Check("run has exactly 2 job lines (one per changed material)",
    Lng(Scalar($"SELECT COUNT(*) FROM stock_revaluation_job_lines WHERE job_id = '{applied.JobId}'")) == 2);
Check("each job line carries its material's posted entry id",
    applied.PostedEntries.All(p => Txt(Scalar(
        $"SELECT journal_entry_id FROM stock_revaluation_job_lines WHERE job_id = '{applied.JobId}' AND material_id = {p.MaterialId}"))
        == p.JournalEntryId.ToString("D")));
Check("no job line for the service material",
    Lng(Scalar($"SELECT COUNT(*) FROM stock_revaluation_job_lines WHERE job_id = '{applied.JobId}' AND material_id = 103")) == 0);

// ------------------------------------------------------------ idempotent
var second = await svc.PreviewAsync(CompanyCode, null, null, from, to);
Check("second preview over the same range shows zero changes",
    second.Changes.Count == 0 && second.Adjustments.Count == 0 && second.TotalDelta == 0m,
    $"changes={second.Changes.Count} delta={second.TotalDelta}");

// ------------------------------------------------------------- INV-6018
string? afterRangeCode = null;
try
{
    await svc.PreviewAsync(CompanyCode, null, null, from, new DateOnly(2026, 1, 12));
}
catch (AppError ex) { afterRangeCode = ex.Code; }
Check("range ending before a later movement refused with INV-6018",
    afterRangeCode == ErrorCodes.RecalculationMovementsAfterRange, afterRangeCode ?? "no error");

// ------------------------------------------------------------- INV-6004
Exec($"INSERT INTO materials (material_id, company_id, code, name_ar, name_en, is_stocked, is_service, is_deleted) VALUES (201, '{ClosedCompanyCode}', 'MAT-C', 'Material C', 'Material C', 1, 0, 0);");
Exec($"INSERT INTO material_stock_movements (company_id, warehouse_id, material_id, movement_date, movement_type, document_no, qty_in, qty_out, unit_cost, is_deleted) VALUES ('{ClosedCompanyCode}', 1, 201, '2026-01-10', 'PUR', 'PUR-C1', 10, 0, 5, 0);");
Exec($"UPDATE fiscal_periods SET status = 'CLOSED' WHERE company_id = '{closedCompanyId:D}' AND period_number = 1;");
string? closedCode = null;
try
{
    await svc.PreviewAsync(ClosedCompanyCode, null, null, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
}
catch (AppError ex) { closedCode = ex.Code; }
Check("closed period refused with INV-6004", closedCode == ErrorCodes.RecalculationClosedPeriod,
    closedCode ?? "no error");

// --------------------------------------------------------- default range
var (rangeFrom, rangeTo) = await svc.GetOpenPeriodRangeAsync(CompanyCode);
var today = DateOnly.FromDateTime(DateTime.Today);
Check("default range is the open period containing today",
    rangeFrom <= today && rangeTo >= today, $"{rangeFrom}..{rangeTo} vs {today}");

Console.WriteLine($"RecalcHarness: {pass} PASS, {fail} FAIL");
return fail == 0 ? 0 : 1;
