// InfoHarness — global Information Window proof (INFO_WINDOW_STUDY §7).
// Seeds one company with a stocked material in two warehouses, posted
// sale/purchase documents, party ledger rows, a posted journal pair, a
// sales rep with a monthly target and commission entries; then asserts
// the sheets InfoSvc itself resolves:
//   material: total/per-warehouse balances, weighted-average cost,
//     stock value, projected balance (context pending-out), last
//     purchase price/date/supplier, last sale price to THIS customer
//     (and its absence for another party), month sales;
//   party: balance (both sign conventions), open amount, credit used,
//     last price for the context material;
//   account: posted sums, balance, movement count, last movement;
//   rep: month sales, target, achievement, pending commission;
//   document: totals + open amount + line count;
//   config: visible-keys round-trip through WinPref;
//   errors: unknown entity maps to INF-8401.
using System.Globalization;
using Microsoft.Data.Sqlite;
using ShouTech.App;
using ShouTech.App.Configuration;
using ShouTech.App.Services;
using ShouTech.Application.Services;
using ShouTech.Domain;
using ShouTech.Domain.Entities;
using ShouTech.Infrastructure;

const string Actor = "info-harness";
const string CompanyCode = "INFOH";

var root = Path.Combine(Path.GetTempPath(), "info-harness-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var dbPath = Path.Combine(root, "info.db");
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

InfoProperty? Prop(InfoSheet sheet, string key)
    => sheet.Properties.FirstOrDefault(p => p.Key == key);

decimal? Num(InfoSheet sheet, string key) => Prop(sheet, key)?.Number;

// ---------------------------------------------------------------- setup
var migration = await SchemaMigrator.ApplyAllAsync("SQLite", dbPath);
Check("schema migrates (through 124)", migration.Success, migration.Message);

var settings = AppCfgSvc.Current;
settings.ConnectionStrings["Default"] = new ConnectionStringSettings { Provider = "SQLite", ConnectionString = dbPath };
settings.Database ??= new DatabaseSettings();
settings.Database.DefaultProvider = "SQLite";
AppState.SetCompany(CompanyCode, "Info Harness Co", "Info Harness Co");

Exec($"INSERT INTO companies (company_id, company_code, company_name_ar, company_name_en) VALUES ('{companyId:D}', '{CompanyCode}', 'Info Co', 'Info Co');");

// Material 42 with card prices, stocked in two warehouses.
Exec($"INSERT INTO materials (material_id, company_id, code, name_ar, name_en, is_stocked, is_service, avg_cost, cost, price_retail, price_wholesale, is_deleted) VALUES (42, '{CompanyCode}', 'MAT-42', 'Item', 'Item', 1, 0, 40, 38, 25, 22, 0);");
Exec($"INSERT INTO warehouses (warehouse_id, company_id, warehouse_code, warehouse_name_ar, warehouse_name_en, is_active, is_deleted) VALUES (1, '{CompanyCode}', 1, 'Main', 'Main', 1, 0);");
Exec($"INSERT INTO warehouses (warehouse_id, company_id, warehouse_code, warehouse_name_ar, warehouse_name_en, is_active, is_deleted) VALUES (2, '{CompanyCode}', 2, 'Second', 'Second', 1, 0);");
Exec($"INSERT INTO material_stock_balances (company_id, warehouse_id, material_id, available_qty, reserved_qty, average_cost, last_cost, stock_value, is_deleted) VALUES ('{CompanyCode}', 1, 42, 100, 5, 40, 40, 4000, 0);");
Exec($"INSERT INTO material_stock_balances (company_id, warehouse_id, material_id, available_qty, reserved_qty, average_cost, last_cost, stock_value, is_deleted) VALUES ('{CompanyCode}', 2, 42, 50, 0, 60, 60, 3000, 0);");

// Parties through the real card repository (assigns ledger ids).
var repo = new MasterCardRepository("SQLite", cs);
var customer = new Party { Kind = "C", Code = "CUS-I", NameEn = "Info Customer", CreditLimit = 10000m };
await repo.SavePartyAsync(CompanyCode, customer, Actor);
var supplier = new Party { Kind = "S", Code = "SUP-I", NameEn = "Info Supplier", CreditLimit = 0m };
await repo.SavePartyAsync(CompanyCode, supplier, Actor);
Check("parties saved with ledger ids", customer.LedgerId > 0 && supplier.LedgerId > 0,
    $"customer={customer.LedgerId} supplier={supplier.LedgerId}");

// Documents (posted): an older sale @18, a newer sale @20 in two lines,
// a purchase @45 partially paid.
Exec($"INSERT INTO sales_documents (doc_id, company_id, doc_kind, doc_no, doc_date, party_id, party_name, warehouse_id, grand_total, paid_amount, status, is_deleted) VALUES (600, '{CompanyCode}', 'SAL', 'SAL-600', '2026-01-05', {customer.LedgerId}, 'Info Customer', 1, 1800, 1800, 'Posted', 0);");
Exec($"INSERT INTO sales_document_lines (doc_id, line_no, material_id, material_code, description, qty, unit_price, line_total) VALUES (600, 1, 42, 'MAT-42', 'Item', 100, 18, 1800);");
Exec($"INSERT INTO sales_documents (doc_id, company_id, doc_kind, doc_no, doc_date, party_id, party_name, warehouse_id, grand_total, paid_amount, status, is_deleted) VALUES (601, '{CompanyCode}', 'SAL', 'SAL-601', '2026-02-01', {customer.LedgerId}, 'Info Customer', 1, 8000, 3000, 'Posted', 0);");
Exec($"INSERT INTO sales_document_lines (doc_id, line_no, material_id, material_code, description, qty, unit_price, line_total) VALUES (601, 1, 42, 'MAT-42', 'Item', 3, 20, 60);");
Exec($"INSERT INTO sales_document_lines (doc_id, line_no, material_id, material_code, description, qty, unit_price, line_total) VALUES (601, 2, 42, 'MAT-42', 'Item', 2, 20, 40);");
Exec($"INSERT INTO purchase_documents (doc_id, company_id, doc_kind, doc_no, doc_date, party_id, party_name, warehouse_id, grand_total, paid_amount, status, is_deleted) VALUES (501, '{CompanyCode}', 'PUR', 'PUR-501', '2026-01-20', {supplier.LedgerId}, 'Info Supplier', 1, 5000, 2000, 'Posted', 0);");
Exec($"INSERT INTO purchase_document_lines (doc_id, line_no, material_id, material_code, description, qty, unit_price, line_total) VALUES (501, 1, 42, 'MAT-42', 'Item', 10, 45, 450);");

// Party ledger: customer owes 5,000 net; supplier is owed 3,000 net.
Exec($"INSERT INTO party_ledger (company_id, party_kind, party_id, doc_kind, doc_id, doc_no, entry_date, debit, credit, currency_code) VALUES ('{CompanyCode}', 'C', {customer.LedgerId}, 'SAL', 601, 'SAL-601', '2026-02-01', 8000, 0, 'SAR');");
Exec($"INSERT INTO party_ledger (company_id, party_kind, party_id, doc_kind, doc_id, doc_no, entry_date, debit, credit, currency_code) VALUES ('{CompanyCode}', 'C', {customer.LedgerId}, 'RCV', NULL, 'RCV-1', '2026-02-03', 0, 3000, 'SAR');");
Exec($"INSERT INTO party_ledger (company_id, party_kind, party_id, doc_kind, doc_id, doc_no, entry_date, debit, credit, currency_code) VALUES ('{CompanyCode}', 'S', {supplier.LedgerId}, 'PUR', 501, 'PUR-501', '2026-01-20', 0, 5000, 'SAR');");
Exec($"INSERT INTO party_ledger (company_id, party_kind, party_id, doc_kind, doc_id, doc_no, entry_date, debit, credit, currency_code) VALUES ('{CompanyCode}', 'S', {supplier.LedgerId}, 'PAY', NULL, 'PAY-1', '2026-01-25', 2000, 0, 'SAR');");

// Accounts + a posted journal pair through the real journal service.
foreach (var (code, name, type) in new[] { ("1201", "Receivable", "ASSET"), ("131", "Cash", "ASSET") })
    Exec($"INSERT INTO chart_of_accounts (account_id, company_id, account_code, account_name, account_type, is_postable, is_active, is_deleted) VALUES ('{Guid.NewGuid():D}', '{companyId:D}', '{code}', '{name}', '{type}', 1, 1, 0);");
var structure = new AccountingStructureRepository("SQLite", cs);
await structure.CreateFiscalYearAsync(companyId, 2026, Actor);
var journalRepo = new JournalRepository("SQLite", cs);
var journalSvc = new JournalService(journalRepo);
var recvAccount = await journalRepo.ResolveAccountIdAsync(companyId, "1201");
var cashAccount = await journalRepo.ResolveAccountIdAsync(companyId, "131");
var j1 = await journalSvc.CreateDraftAsync(companyId, new DateOnly(2026, 3, 1), "Info entry 1", Actor,
    new[] { new JournalLineRequest(recvAccount, 7000m, 0m, "Dr"), new JournalLineRequest(cashAccount, 0m, 7000m, "Cr") },
    currencyCode: "SAR", exchangeRate: 1m, documentNumber: "JV-I1");
await journalSvc.PostAsync(j1, Actor);
var j2 = await journalSvc.CreateDraftAsync(companyId, new DateOnly(2026, 3, 5), "Info entry 2", Actor,
    new[] { new JournalLineRequest(cashAccount, 2000m, 0m, "Dr"), new JournalLineRequest(recvAccount, 0m, 2000m, "Cr") },
    currencyCode: "SAR", exchangeRate: 1m, documentNumber: "JV-I2");
await journalSvc.PostAsync(j2, Actor);

// Sales rep: monthly target 100,000 for the current month; commission
// entries: this month Sales basis 25,000 (commission 1,250 Pending)
// and a Settled 500 from last month (must not count as pending).
var today = DateTime.Today;
var monthStart = new DateTime(today.Year, today.Month, 1);
string Fmt(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
Exec($"INSERT INTO sales_reps (id, company_id, code, name_en, name_ar, commission_pct, target_amount, is_active, is_deleted) VALUES ('{Guid.NewGuid():D}', '{CompanyCode}', 'REP1', 'Rep One', 'Rep One', 5, 0, 1, 0);");
Exec($"INSERT INTO targets (id, company_id, owner_kind, owner_code, period_kind, period_start, target_amount) VALUES ('{Guid.NewGuid():D}', '{CompanyCode}', 'REP', 'REP1', 'M', '{Fmt(monthStart)}', 100000);");
Exec($"INSERT INTO sco_commission_policies (policy_id, company_id, policy_name, basis, rate_pct) VALUES ('POL-I', '{CompanyCode}', 'Info policy', 'Sales', 5);");
Exec($"INSERT INTO sco_commission_entries (entry_id, company_id, policy_id, rep_code, source_doc_kind, source_doc_id, basis, basis_amount, rate_pct, commission_amount, status, period_date, is_deleted) VALUES ('{Guid.NewGuid():D}', '{CompanyCode}', 'POL-I', 'REP1', 'SAL', 601, 'Sales', 25000, 5, 1250, 'Pending', '{Fmt(today)}', 0);");
Exec($"INSERT INTO sco_commission_entries (entry_id, company_id, policy_id, rep_code, source_doc_kind, source_doc_id, basis, basis_amount, rate_pct, commission_amount, status, period_date, is_deleted) VALUES ('{Guid.NewGuid():D}', '{CompanyCode}', 'POL-I', 'REP1', 'SAL', 600, 'Sales', 10000, 5, 500, 'Settled', '{Fmt(monthStart.AddMonths(-1))}', 0);");

// ------------------------------------------------------------ material
var material = InfoSvc.Resolve(new InfoRef(InfoKind.Material, Id: 42));
Check("material: total balance 150", Num(material, "TotalBalance") == 150m, $"{Num(material, "TotalBalance")}");
Check("material: reserved 5", Num(material, "ReservedQty") == 5m);
Check("material: stock value 7,000", Num(material, "StockValue") == 7000m);
Check("material: weighted-average cost 46.667", Num(material, "AvgCost") is decimal avg && Math.Abs(avg - 7000m / 150m) < 0.001m,
    $"{Num(material, "AvgCost")}");
var warehouseBalances = Prop(material, "WarehouseBalances");
Check("material: two warehouse rows (100 / 50)",
    warehouseBalances?.Children is { Count: 2 } rows && rows[0].Number == 100m && rows[1].Number == 50m);
Check("material: no projected balance without context", Prop(material, "ProjectedBalance") is null);
Check("material: sale price from card 25", Num(material, "SalePrice") == 25m);
Check("material: last purchase 45 on 2026-01-20 from the supplier",
    Num(material, "LastPurchasePrice") == 45m
    && Prop(material, "LastPurchaseDate")?.Text == "2026-01-20"
    && Prop(material, "LastPurchaseSupplier")?.Text == "Info Supplier");
Check("material: last sale price 20 (newest doc wins over 18)", Num(material, "LastSalePrice") == 20m);
Check("material: sale price stats min 18 / max 20",
    Num(material, "MinSalePrice") == 18m && Num(material, "MaxSalePrice") == 20m);
Check("material: no last-sale-to-customer without context", Prop(material, "LastSalePriceToCustomer") is null);

var projected = InfoSvc.Resolve(new InfoRef(InfoKind.Material, Id: 42),
    new InfoContext(PartyLedgerId: customer.LedgerId, PartyKind: "C", PendingOutQty: 30m));
Check("material: projected balance 150 - 30 = 120", Num(projected, "ProjectedBalance") == 120m,
    $"{Num(projected, "ProjectedBalance")}");
Check("material: last sale price to THIS customer 20", Num(projected, "LastSalePriceToCustomer") == 20m);

var otherParty = InfoSvc.Resolve(new InfoRef(InfoKind.Material, Id: 42),
    new InfoContext(PartyLedgerId: supplier.LedgerId, PartyKind: "C"));
Check("material: no last-sale price for a party that never bought", Prop(otherParty, "LastSalePriceToCustomer") is null);

// ------------------------------------------------------------ parties
var customerSheet = InfoSvc.Resolve(new InfoRef(InfoKind.Customer, Id: customer.LedgerId),
    new InfoContext(MaterialId: 42));
Check("customer: balance 5,000 (debit-positive)", Num(customerSheet, "Balance") == 5000m,
    $"{Num(customerSheet, "Balance")}");
Check("customer: open amount 5,000", Num(customerSheet, "OpenAmount") == 5000m);
Check("customer: credit used 50%", Num(customerSheet, "CreditUsedPct") == 50m);
Check("customer: last price for the context material 20", Num(customerSheet, "LastPriceForMaterial") == 20m);
Check("customer: oldest open document 2026-02-01", Prop(customerSheet, "OldestOpenDate")?.Text == "2026-02-01");

var supplierSheet = InfoSvc.Resolve(new InfoRef(InfoKind.Supplier, Id: supplier.LedgerId),
    new InfoContext(MaterialId: 42));
Check("supplier: balance -3,000 signed (credit side = owed to them)", Num(supplierSheet, "Balance") == -3000m,
    $"{Num(supplierSheet, "Balance")}");
Check("supplier: open amount 3,000", Num(supplierSheet, "OpenAmount") == 3000m);
Check("supplier: last price for the context material 45", Num(supplierSheet, "LastPriceForMaterial") == 45m);

// ------------------------------------------------------------ account
var accountSheet = InfoSvc.Resolve(new InfoRef(InfoKind.Account, Code: "1201"));
Check("account: debit 7,000 / credit 2,000 / balance 5,000",
    Num(accountSheet, "DebitTotal") == 7000m && Num(accountSheet, "CreditTotal") == 2000m
    && Num(accountSheet, "Balance") == 5000m, $"{Num(accountSheet, "Balance")}");
Check("account: 2 movements, last on 2026-03-05",
    Num(accountSheet, "MovementsCount") == 2m && Prop(accountSheet, "LastMovementDate")?.Text == "2026-03-05",
    Prop(accountSheet, "LastMovementDate")?.Text ?? "");

// ------------------------------------------------------------ rep
var repSheet = InfoSvc.Resolve(new InfoRef(InfoKind.SalesRep, Code: "REP1"));
Check("rep: month sales 25,000 (commission basis)", Num(repSheet, "MonthSales") == 25000m,
    $"{Num(repSheet, "MonthSales")}");
Check("rep: target 100,000 from the targets table", Num(repSheet, "Target") == 100000m);
Check("rep: achievement 25%", Num(repSheet, "AchievementPct") == 25m);
Check("rep: month commission 1,250; pending 1,250 (settled excluded)",
    Num(repSheet, "MonthCommission") == 1250m && Num(repSheet, "PendingCommission") == 1250m);

// ------------------------------------------------------------ warehouse + document
var warehouseSheet = InfoSvc.Resolve(new InfoRef(InfoKind.Warehouse, Id: 1));
Check("warehouse: 1 material / qty 100 / value 4,000",
    Num(warehouseSheet, "MaterialsCount") == 1m && Num(warehouseSheet, "TotalQty") == 100m
    && Num(warehouseSheet, "StockValue") == 4000m);

var docSheet = InfoSvc.Resolve(new InfoRef(InfoKind.Document, Id: 601, DocKind: "SAL"));
Check("document: grand total 8,000 / open 5,000 / 2 lines",
    Num(docSheet, "GrandTotal") == 8000m && Num(docSheet, "OpenAmount") == 5000m
    && Num(docSheet, "LinesCount") == 2m);

// ------------------------------------------------------------ config + errors
InfoSvc.SaveVisibleKeys(InfoKind.Material, new[] { "TotalBalance", "SalePrice" });
var savedKeys = InfoSvc.LoadVisibleKeys(InfoKind.Material);
Check("config: visible keys round-trip", savedKeys.SequenceEqual(new[] { "TotalBalance", "SalePrice" }),
    string.Join(",", savedKeys));
InfoSvc.SaveVisibleKeys(InfoKind.Material, Array.Empty<string>());
var defaultKeys = InfoSvc.LoadVisibleKeys(InfoKind.Material);
Check("config: empty set falls back to defaults", defaultKeys.Contains("TotalBalance") && defaultKeys.Contains("AvgCost"));

var notFoundCode = string.Empty;
try { InfoSvc.Resolve(new InfoRef(InfoKind.Material, Id: 999999)); }
catch (AppError ex) { notFoundCode = ex.Code; }
Check("errors: unknown material maps to INF-8401", notFoundCode == ErrorCodes.InfoEntityNotFound, notFoundCode);

Console.WriteLine($"INFO HARNESS: {pass} PASS / {fail} FAIL");
return fail == 0 ? 0 : 1;
