// NumHarness — automatic sequential numbering proof (NUMBERING_STUDY §4).
//   A: the central allocator is sequential, seed-aware and monotonic
//      across separate transactions (no duplicates, no going back).
//   B: number_series_config drives InvRepo: prefix, padding and the
//      start floor apply to future allocations only.
//   C: bin interaction — a posted document's number is NEVER reissued
//      after soft-delete + permanent purge (the counter outlives the
//      record; study §3.3).
using System.Globalization;
using Microsoft.Data.Sqlite;
using ShouTech.App;
using ShouTech.App.Configuration;
using ShouTech.App.Reporting.Invoices;
using ShouTech.App.Services;
using ShouTech.Infra;
using ShouTech.Infrastructure;

const string Actor = "num-harness";
const string CompanyCode = "NUMH";
var year = DateTime.Today.Year;

var root = Path.Combine(Path.GetTempPath(), "num-harness-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var dbPath = Path.Combine(root, "num.db");
var cs = $"Data Source={dbPath};";
var companyId = Guid.NewGuid();

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

decimal Scalar(string sql)
{
    using var connection = new SqliteConnection(cs);
    connection.Open();
    using var cmd = connection.CreateCommand();
    cmd.CommandText = sql;
    var value = cmd.ExecuteScalar();
    return value is null or DBNull ? 0m : Convert.ToDecimal(value, CultureInfo.InvariantCulture);
}

// ---------------------------------------------------------------- setup
var migration = await SchemaMigrator.ApplyAllAsync("SQLite", dbPath);
Check("schema migrates (through 123)", migration.Success, migration.Message);
Check("migration 123 config table exists",
    Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='number_series_config'") == 1);

var settings = AppCfgSvc.Current;
settings.ConnectionStrings["Default"] = new ConnectionStringSettings { Provider = "SQLite", ConnectionString = dbPath };
settings.Database ??= new DatabaseSettings();
settings.Database.DefaultProvider = "SQLite";
AppState.SetCompany(CompanyCode, "Num Harness Co", "Num Harness Co");

Exec($"INSERT INTO companies (company_id, company_code, company_name_ar, company_name_en) VALUES ('{companyId:D}', '{CompanyCode}', 'Num Co', 'Num Co');");
var structure = new AccountingStructureRepository("SQLite", cs);
await structure.CreateFiscalYearAsync(companyId, 2026, Actor);
Exec($"INSERT INTO warehouses (warehouse_id, company_id, warehouse_code, warehouse_name_ar, warehouse_name_en, is_active, is_deleted) VALUES (1, '{CompanyCode}', 'WH-01', 'Main', 'Main', 1, 0);");
Exec($"INSERT INTO materials (material_id, company_id, code, name_ar, name_en, is_stocked, is_service, cost_method, allow_negative, is_deleted) VALUES (42, '{CompanyCode}', 'MAT-42', 'Item 42', 'Item 42', 1, 0, 'WeightedAvg', 0, 0);");
Exec($"INSERT INTO material_stock_balances (company_id, warehouse_id, material_id, available_qty, reserved_qty, average_cost, last_cost, stock_value, is_deleted) VALUES ('{CompanyCode}', 1, 42, 100, 0, 10, 10, 1000, 0);");

// ------------------------------------------------- A: allocator basics
long Alloc(string series, int segment, long seed = 0, long startFloor = 0)
{
    using var connection = new SqliteConnection(cs);
    connection.Open();
    using var tx = connection.BeginTransaction(deferred: false);
    var value = SequenceAllocator.Allocate(connection, tx, false, CompanyCode, series, segment, seed, startFloor);
    tx.Commit();
    return value;
}

var a1 = Alloc("TST", year);
var a2 = Alloc("TST", year);
var a3 = Alloc("TST", year);
Check("A1 allocator is sequential from 1", a1 == 1 && a2 == 2 && a3 == 3, $"{a1},{a2},{a3}");
var seeded = Alloc("TST", year, seed: 41);
Check("A2 seed raises the counter above history", seeded == 42, seeded.ToString(CultureInfo.InvariantCulture));
var afterSeed = Alloc("TST", year);
Check("A3 counter never goes back after seeding", afterSeed == 43, afterSeed.ToString(CultureInfo.InvariantCulture));
var otherSegment = Alloc("TST", year + 1);
Check("A4 year segments are independent counters", otherSegment == 1, otherSegment.ToString(CultureInfo.InvariantCulture));
var floored = Alloc("FLR", 0, startFloor: 99);
Check("A5 start floor applies on first allocation", floored == 100, floored.ToString(CultureInfo.InvariantCulture));

// ------------------------------------------------- B: config via InvRepo
Exec($"INSERT INTO number_series_config (company_id, series_key, prefix, padding, yearly_segment, start_value, updated_by, updated_at) VALUES ('{CompanyCode}', 'SAL', 'INV', 5, 1, 100, '{Actor}', '2026-11-24');");
var configured1 = InvRepo.NextDocNo(InvDocKind.SalesInvoice);
var configured2 = InvRepo.NextDocNo(InvDocKind.SalesInvoice);
Check("B1 config prefix/padding/start drive the document number",
    configured1 == $"INV-{year}-00100" && configured2 == $"INV-{year}-00101",
    $"{configured1} / {configured2}");
var unconfigured = InvRepo.NextDocNo(InvDocKind.PurchaseInvoice);
Check("B2 unconfigured series keep the defaults",
    unconfigured == $"PUR-{year}-0001", unconfigured);

// ------------------------------------------ C: bin interaction (purge)
var stockIn = new InvDoc
{
    Kind = InvDocKind.StockIn,
    DocNo = InvRepo.NextDocNo(InvDocKind.StockIn),
    DocDate = new DateTime(2026, 11, 1),
    WarehouseId = 1,
    CurrencyCode = "SAR",
    ExchangeRate = 1m,
    Lines =
    {
        new InvLine { LineNo = 1, MaterialId = 42, MaterialCode = "MAT-42", Qty = 5m, UnitPrice = 10m, WarehouseId = 1 }
    }
};
InvRepo.SaveDraft(stockIn);
InvRepo.Post(stockIn.DocId, stockIn.Kind, Actor);
var purgedSuffix = long.Parse(stockIn.DocNo.Split('-')[^1], CultureInfo.InvariantCulture);
Check("C0 stock-in posted (stock 105)",
    Scalar("SELECT COALESCE(SUM(available_qty),0) FROM material_stock_balances WHERE company_id='NUMH' AND material_id=42 AND is_deleted=0") == 105m);
DocBinSvc.SoftDeleteDocument(stockIn.DocId, stockIn.Kind, Actor);
Check("C1 binned stock-in leaves stock clean (100)",
    Scalar("SELECT COALESCE(SUM(available_qty),0) FROM material_stock_balances WHERE company_id='NUMH' AND material_id=42 AND is_deleted=0") == 100m);
DocBinSvc.PurgeDocument(stockIn.DocId, stockIn.Kind, Actor);
Check("C2 purged stock-in record is gone",
    Scalar($"SELECT COUNT(*) FROM purchase_documents WHERE doc_id={stockIn.DocId}") == 0);
var nextStockIn = InvRepo.NextDocNo(InvDocKind.StockIn);
var nextSuffix = long.Parse(nextStockIn.Split('-')[^1], CultureInfo.InvariantCulture);
Check("C3 purged number is NEVER reissued", nextSuffix > purgedSuffix, $"{stockIn.DocNo} -> {nextStockIn}");

Console.WriteLine($"== NumHarness: {pass} PASS / {fail} FAIL ==");
return fail == 0 ? 0 : 1;
