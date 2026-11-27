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
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;
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
Exec($"INSERT INTO parties (id, company_id, kind, code, name_en, credit_limit, is_active, is_deleted, created_by, ledger_id) VALUES ('{Guid.NewGuid():D}', '{CompanyCode}', 'C', 'CUS-1', 'Num Customer', 0, 1, 0, '{Actor}', 7);");

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
Check("B1 config prefix/start drive unpadded document numbering",
    configured1 == "INV-100" && configured2 == "INV-101",
    $"{configured1} / {configured2}");
var unconfigured = InvRepo.NextDocNo(InvDocKind.PurchaseInvoice);
Check("B2 unconfigured series start at one without padding",
    unconfigured == "PUR-1", unconfigured);

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

// ------------------------------------------ N2: routed families
var chainRepo = new OrderDocumentRepository("SQLite", cs);
var so1 = await chainRepo.NextDocNoAsync(CommercialDocSide.Sales, companyId, CommercialDocKinds.SalesOrder);
var so2 = await chainRepo.NextDocNoAsync(CommercialDocSide.Sales, companyId, CommercialDocKinds.SalesOrder);
Check("E1 chain documents use the unified format, sequentially",
    so1 == "SO-1" && so2 == "SO-2", $"{so1} / {so2}");
var chainSal = await chainRepo.NextDocNoAsync(CommercialDocSide.Sales, companyId, CommercialDocKinds.SalesInvoice);
Check("E2 chain SAL shares InvRepo's counter AND its config (INV-00102 after B)",
    chainSal == "INV-102", chainSal);

var posRepo = new PosRepository("SQLite", cs);
var ses1 = await posRepo.OpenSessionAsync(CompanyCode, "T1", "cashier", 1, "SAR", 0m, Actor);
var ses2 = await posRepo.OpenSessionAsync(CompanyCode, "T2", "cashier", 1, "SAR", 0m, Actor);
Check("F1 POS sessions allocate sequentially",
    ses1.SessionNo == $"SES-{year}-0001" && ses2.SessionNo == $"SES-{year}-0002",
    $"{ses1.SessionNo} / {ses2.SessionNo}");
Exec($"UPDATE pos_sessions SET is_deleted = 1 WHERE session_no = '{ses1.SessionNo}'");
Exec($"DELETE FROM pos_sessions WHERE session_no = '{ses1.SessionNo}'");
var ses3 = await posRepo.OpenSessionAsync(CompanyCode, "T3", "cashier", 1, "SAR", 0m, Actor);
Check("F2 a purged session's number is never reissued (COUNT-based code failed this)",
    ses3.SessionNo == $"SES-{year}-0003", ses3.SessionNo);

var mfgRepo = new MfgOrderRepository("SQLite", cs);
var mfg1 = await mfgRepo.GetNextDocNoAsync(CompanyCode);
var mfg2 = await mfgRepo.GetNextDocNoAsync(CompanyCode);
Check("G1 manufacturing order numbers are sequential and consumed atomically",
    mfg1 == 1 && mfg2 == 2, $"{mfg1} / {mfg2}");

var mat1 = MatRepo.NextCode("MAT-");
Exec("UPDATE materials SET is_deleted = 1 WHERE material_id = 42");
var mat2 = MatRepo.NextCode("MAT-");
Check("H1 material codes seed above history incl. binned rows, counter monotonic",
    mat1 == "MAT-0043" && mat2 == "MAT-0044", $"{mat1} / {mat2}");

var journalRepo = new JournalRepository("SQLite", cs);
var jv1 = journalRepo.NextDocumentNumber(CompanyCode, "JV", DateOnly.FromDateTime(DateTime.Today));
var jv2 = journalRepo.NextDocumentNumber(CompanyCode, "JV", DateOnly.FromDateTime(DateTime.Today));
var rcv1 = journalRepo.NextDocumentNumber(CompanyCode, "RCV", DateOnly.FromDateTime(DateTime.Today));
Check("I1 journal/voucher numbers are sequential per series (no timestamps)",
    jv1 == "JV-1" && jv2 == "JV-2" && rcv1 == "RCV-1",
    $"{jv1} / {jv2} / {rcv1}");

var masterRepo = new MasterCardRepository("SQLite", cs);
var cus1 = await masterRepo.NextPartyCodeAsync(CompanyCode, "C");
var cus2 = await masterRepo.NextPartyCodeAsync(CompanyCode, "C");
var sup1 = await masterRepo.NextPartyCodeAsync(CompanyCode, "S");
Check("J1 party codes allocate per kind, seeded above existing CUS-1",
    cus1 == "CUS-2" && cus2 == "CUS-3" && sup1 == "SUP-1",
    $"{cus1} / {cus2} / {sup1}");

var hrRepo = new HrRepository("SQLite", cs);
var emp1 = await hrRepo.NextEmployeeCodeAsync(CompanyCode);
var emp2 = await hrRepo.NextEmployeeCodeAsync(CompanyCode);
Check("K1 employee codes allocate sequentially",
    emp1 == "EMP-1" && emp2 == "EMP-2", $"{emp1} / {emp2}");

var seriesRows = SeqSvc.LoadSeries();
Check("L1 SeqSvc lists the series catalog with counters",
    seriesRows.Count >= 20 && seriesRows.Any(r => r.Key == "JV" && r.CurrentValue == 2),
    $"rows={seriesRows.Count}");
var jvRow = seriesRows.First(r => r.Key == "JV");
SeqSvc.SaveSeries(new[] { jvRow with { Prefix = "JX", Padding = 3, StartValue = 50 } }, Actor);
var jx = journalRepo.NextDocumentNumber(CompanyCode, "JV", DateOnly.FromDateTime(DateTime.Today));
Check("L2 saved OptWin config drives the next unpadded allocation (prefix/start)",
    jx == "JX-50", jx);

// ------------------------------------------ P4: POS terminal bindings
var termId = PosTermRepo.Save(new PosTermRepo.PosTermRec(
    string.Empty, "T9", "Terminal 9", string.Empty,
    "WH-01", "131", "133", null, null, null, null, null, true,
    SalesDocKind: "SAL", ClosingAccountCode: "132", CurrencyCode: "KWD",
    CustomerGroupCode: "RETAIL", SaleDesignName: "Thermal A", ReturnDesignName: "Thermal R",
    GiftDesignName: "Gift A"), Actor);
var termBack = PosTermRepo.FindByCode("T9");
Check("M1 terminal P4 fields round-trip through PosTermRepo",
    termBack is not null && termBack.Id == termId
    && termBack.SalesDocKind == "SAL" && termBack.ClosingAccountCode == "132"
    && termBack.CurrencyCode == "KWD" && termBack.CustomerGroupCode == "RETAIL"
    && termBack.SaleDesignName == "Thermal A" && termBack.GiftDesignName == "Gift A",
    termBack?.SalesDocKind ?? "missing");
Exec($"INSERT INTO party_groups (id, company_id, kind, code, name_en, is_active, is_deleted) VALUES ('{Guid.NewGuid():D}', '{CompanyCode}', 'C', 'RETAIL', 'Retail', 1, 0);");
Exec($"INSERT INTO doc_designs (CompanyID, DocKind, DesignName, IsDefault, PayloadJson, IsDeleted) VALUES ('{CompanyCode}', 'SAL', 'Thermal A', 0, '{{}}', 0);");
Check("M2 design + customer-group loaders read real rows",
    PosTermRepo.LoadDesignNames().Contains("Thermal A") && PosTermRepo.LoadCustomerGroups().Contains("RETAIL"));
var sesT9 = await posRepo.OpenSessionAsync(CompanyCode, "T9", "cashier", 1, "KWD", 25m, Actor);
await posRepo.CloseSessionAsync(CompanyCode, sesT9.Id, 25m, Actor);
Check("M3 the close register records the terminal's closing account",
    Scalar("SELECT COUNT(*) FROM pos_cash_closings WHERE company_id='NUMH' AND terminal_code='T9' AND closing_account_code='132'") == 1);

Console.WriteLine($"== NumHarness: {pass} PASS / {fail} FAIL ==");
return fail == 0 ? 0 : 1;
