// W1 withholding tax / TDS — executed headless harness (SQLite).
// Scenarios (task proof a–g):
//   (a) manual invoice base 10,000 + 15% input VAT, withholding 5%:
//       GL balanced, AP credited GROSS 11,500, deduction leg Dr AP 500,
//       withholding payable 2215 Cr 500, supplier party balance NET 11,000;
//   (b) allocation of the NET amount fully settles; allocation above
//       net is rejected and writes nothing;
//   (c) reversal of the withheld invoice restores balances and writes
//       the negative register mirror row;
//   (d) below-threshold invoice: evaluated, no withholding;
//   (e) cumulative threshold: first invoice below, second crosses and
//       withholds only on the excess;
//   (f) withholding register total for the period == GL 2215 movement;
//   (g) chain-path invoice withholds identically to the manual path
//       (supplier-default determination).
using System.Globalization;
using Microsoft.Data.Sqlite;
using ShouTech.App;
using ShouTech.App.Configuration;
using ShouTech.App.Reporting.Invoices;
using ShouTech.App.Services;
using ShouTech.Application.Services;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;
using ShouTech.Infrastructure;

const string Actor = "w1-harness";
const string CompanyCode = "W1H";
const string ChainCode = "W1C";

var root = Path.Combine(Path.GetTempPath(), "w1-harness-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var dbPath = Path.Combine(root, "w1.db");
var cs = $"Data Source={dbPath};";
var companyId = Guid.NewGuid();
var chainCompanyId = Guid.NewGuid();

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

string ScalarText(string sql)
{
    using var connection = new SqliteConnection(cs);
    connection.Open();
    using var cmd = connection.CreateCommand();
    cmd.CommandText = sql;
    return Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
}

(decimal Debit, decimal Credit) Gl(string sourceType, long sourceId, string account, Guid company)
    => (Scalar($"SELECT COALESCE(SUM(l.debit),0) FROM journal_entry_lines l JOIN chart_of_accounts a ON a.account_id=l.account_id JOIN journal_entries e ON e.journal_entry_id=l.journal_entry_id WHERE e.source_doc_type='{sourceType}' AND e.source_doc_id='{sourceId}' AND e.company_id='{company:D}' AND a.account_code='{account}' AND e.status=1"),
        Scalar($"SELECT COALESCE(SUM(l.credit),0) FROM journal_entry_lines l JOIN chart_of_accounts a ON a.account_id=l.account_id JOIN journal_entries e ON e.journal_entry_id=l.journal_entry_id WHERE e.source_doc_type='{sourceType}' AND e.source_doc_id='{sourceId}' AND e.company_id='{company:D}' AND a.account_code='{account}' AND e.status=1"));

decimal PartyBalance(long partyId, string company)
    => Scalar($"SELECT COALESCE(SUM(credit)-SUM(debit),0) FROM party_ledger WHERE company_id='{company}' AND party_id={partyId}");

// ---------------------------------------------------------------- setup
var migration = await SchemaMigrator.ApplyAllAsync("SQLite", dbPath);
Check("schema migrates (through 114)", migration.Success, migration.Message);
Check("migration 114 columns exist",
    Scalar("SELECT COUNT(*) FROM pragma_table_info('company_tax_rules') WHERE name IN ('is_withholding','withholding_account_code','withholding_threshold_amount','withholding_threshold_mode')") == 4
    && Scalar("SELECT COUNT(*) FROM pragma_table_info('purchase_documents') WHERE name LIKE 'withholding%'") == 5
    && Scalar("SELECT COUNT(*) FROM pragma_table_info('parties') WHERE name='withholding_tax_code'") == 1);

var settings = AppCfgSvc.Current;
settings.ConnectionStrings["Default"] = new ConnectionStringSettings { Provider = "SQLite", ConnectionString = dbPath };
settings.Database ??= new DatabaseSettings();
settings.Database.DefaultProvider = "SQLite";
AppState.SetCompany(CompanyCode, "W1 Harness Co", "W1 Harness Co");

Exec($"INSERT INTO companies (company_id, company_code, company_name_ar, company_name_en) VALUES ('{companyId:D}', '{CompanyCode}', 'W1 Co', 'W1 Co');");
Exec($"INSERT INTO companies (company_id, company_code, company_name_ar, company_name_en) VALUES ('{chainCompanyId:D}', '{ChainCode}', 'W1 Chain Co', 'W1 Chain Co');");

// Tax setup for both companies: VAT 15% recoverable + withholding rules.
foreach (var code in new[] { CompanyCode, ChainCode })
{
    Exec($"""
        INSERT INTO company_tax_settings(company_code, country_code, tax_enabled, tax_scheme) VALUES ('{code}', 'SA', 1, 'VAT');
        INSERT INTO company_tax_rules (company_code, country_code, tax_code, tax_name, tax_type, tax_treatment, rate, calculation_mode, is_recoverable, output_account_code, input_account_code, effective_from, is_default, is_active)
          VALUES ('{code}', 'SA', '{code}-VAT15', 'Standard VAT', 'VAT', 'Standard', 15, 'Exclusive', 1, '2205', '1205', '2026-01-01', 1, 1);
        INSERT INTO company_tax_rules (company_code, country_code, tax_code, tax_name, tax_type, tax_treatment, rate, calculation_mode, is_recoverable, output_account_code, input_account_code, effective_from, is_default, is_active, is_withholding, withholding_account_code, withholding_threshold_amount, withholding_threshold_mode)
          VALUES ('{code}', 'SA', 'WHT5', 'Withholding 5%', 'WITHHOLDING', 'Withholding', 5, 'Exclusive', 0, '', '2215', '2026-01-01', 0, 1, 1, '2215', NULL, 'PerDocument');
        INSERT INTO company_tax_rules (company_code, country_code, tax_code, tax_name, tax_type, tax_treatment, rate, calculation_mode, is_recoverable, output_account_code, input_account_code, effective_from, is_default, is_active, is_withholding, withholding_account_code, withholding_threshold_amount, withholding_threshold_mode)
          VALUES ('{code}', 'SA', 'WHT-T', 'Withholding threshold', 'WITHHOLDING', 'Withholding', 5, 'Exclusive', 0, '', '2215', '2026-01-01', 0, 1, 1, '2215', 20000, 'PerDocument');
        INSERT INTO company_tax_rules (company_code, country_code, tax_code, tax_name, tax_type, tax_treatment, rate, calculation_mode, is_recoverable, output_account_code, input_account_code, effective_from, is_default, is_active, is_withholding, withholding_account_code, withholding_threshold_amount, withholding_threshold_mode)
          VALUES ('{code}', 'SA', 'WHT-C', 'Withholding cumulative', 'WITHHOLDING', 'Withholding', 5, 'Exclusive', 0, '', '2215', '2026-01-01', 0, 1, 1, '2215', 10000, 'Cumulative');
        """);
}

var structure = new AccountingStructureRepository("SQLite", cs);
await structure.CreateFiscalYearAsync(companyId, 2026, Actor);
// Chain company fiscal year/periods by SQL (same shape as Kern.Tests Step9 fixture).
Exec($"INSERT INTO fiscal_years (company_id, fiscal_year, database_name, start_date, end_date, is_current, is_closed) VALUES ('{chainCompanyId:D}', 2026, '', '2026-01-01', '2026-12-31', 1, 0);");
var chainYearId = (long)Scalar("SELECT last_insert_rowid()");
for (var m = 1; m <= 12; m++)
    Exec($"INSERT INTO fiscal_periods (fiscal_period_id, company_id, fiscal_year_id, period_number, period_name, start_date, end_date, status) VALUES ('{Guid.NewGuid():D}', '{chainCompanyId:D}', {chainYearId}, {m}, '2026-{m:D2}', '2026-{m:D2}-01', '{new DateTime(2026, m, 1).AddMonths(1).AddDays(-1):yyyy-MM-dd}', 'OPEN');");

// Accounts for the manual-path company (company-Guid-keyed chart,
// inserted directly like the Kern.Tests Step9 fixture).
foreach (var (code, name, type) in new[]
{
    ("131", "Cash", "ASSET"), ("132", "Bank", "ASSET"), ("1201", "Receivable", "ASSET"), ("1203", "Inventory", "ASSET"),
    ("1205", "Input VAT", "ASSET"), ("2201", "Payable", "LIABILITY"), ("2203", "GRNI", "LIABILITY"), ("2205", "Output VAT", "LIABILITY"),
    ("2215", "Withholding Tax Payable", "LIABILITY"), ("301", "Sales", "REVENUE"), ("302", "Sales Returns", "REVENUE"),
    ("401", "COGS", "EXPENSE"), ("402", "Purchases", "EXPENSE"), ("403", "Purchase Returns", "EXPENSE"), ("415", "PPV", "EXPENSE")
})
    Exec($"INSERT INTO chart_of_accounts (account_id, company_id, account_code, account_name, account_type, is_postable, is_active, is_deleted) VALUES ('{Guid.NewGuid():D}', '{companyId:D}', '{code}', '{name}', '{type}', 1, 1, 0);");
// Chain company accounts by SQL (company-keyed chart).
foreach (var (code, name, type) in new[]
{
    ("1203", "Inventory", "ASSET"), ("1205", "Input VAT", "ASSET"), ("2201", "Payable", "LIABILITY"),
    ("2203", "GRNI", "LIABILITY"), ("2215", "Withholding Tax Payable", "LIABILITY"),
    ("402", "Purchases", "EXPENSE"), ("415", "PPV", "EXPENSE"), ("131", "Cash", "ASSET")
})
    Exec($"INSERT INTO chart_of_accounts (account_id, company_id, account_code, account_name, account_type, is_postable, is_active, is_deleted) VALUES ('{Guid.NewGuid():D}', '{chainCompanyId:D}', '{code}', '{name}', '{type}', 1, 1, 0);");

Exec($"INSERT INTO materials (material_id, company_id, code, name_ar, name_en, is_stocked, avg_cost, is_deleted) VALUES (42, '{CompanyCode}', 'MAT-42', 'Item', 'Item', 1, 40, 0);");
Exec($"INSERT INTO materials (material_id, company_id, code, name_ar, name_en, is_stocked, is_service, cost_method, allow_negative, is_deleted) VALUES (142, '{ChainCode}', 'MAT-42', 'Item', 'Item', 1, 0, 'WeightedAvg', 0, 0);");
Exec($"INSERT INTO warehouses (warehouse_id, company_id, warehouse_code, warehouse_name_ar, warehouse_name_en, is_deleted) VALUES (1, '{CompanyCode}', 1, 'Main', 'Main', 0);");
Exec($"INSERT INTO warehouses (warehouse_id, company_id, warehouse_code, warehouse_name_ar, warehouse_name_en, is_active, is_deleted) VALUES (2, '{ChainCode}', 'WH-01', 'Main', 'Main', 1, 0);");
Exec($"INSERT INTO material_stock_balances (company_id, warehouse_id, material_id, available_qty, average_cost, last_cost) VALUES ('{CompanyCode}', 1, 42, 1000, 40, 40);");
Exec($"INSERT INTO material_stock_balances (company_id, warehouse_id, material_id, available_qty, reserved_qty, average_cost, last_cost, stock_value, is_deleted) VALUES ('{ChainCode}', 2, 142, 100, 0, 10, 10, 1000, 0);");

async Task<Party> SavePartyAsync(string kind, string code)
{
    var repo = new MasterCardRepository("SQLite", cs);
    var party = new Party { Kind = kind, Code = code, NameEn = "Harness Party", CreditLimit = 1000000m };
    await repo.SavePartyAsync(CompanyCode, party, Actor);
    return party;
}

var supplier = await SavePartyAsync("S", "SUP-W1");
var supplier2 = await SavePartyAsync("S", "SUP-W2");
// Chain supplier (ledger_id 9) with a supplier-level default withholding code.
Exec($"INSERT INTO parties (id, company_id, kind, code, name_en, credit_limit, is_active, is_deleted, created_by, ledger_id, withholding_tax_code) VALUES ('{Guid.NewGuid():D}', '{ChainCode}', 'S', 'SUP-1', 'Chain Supplier', 0, 1, 0, '{Actor}', 9, 'WHT5');");

InvDoc BuildDoc(long partyId, string partyCode, decimal price, decimal taxRate, string taxCode)
    => new()
    {
        Kind = InvDocKind.PurchaseInvoice,
        DocNo = InvRepo.NextDocNo(InvDocKind.PurchaseInvoice),
        DocDate = new DateTime(2026, 3, 10),
        PartyId = partyId,
        PartyCode = partyCode,
        PartyName = "Harness Party",
        WarehouseId = 1,
        CurrencyCode = "SAR",
        Lines = { new InvLine { LineNo = 1, MaterialId = 42, MaterialCode = "MAT-42", Qty = 1m, UnitPrice = price, TaxCode = taxCode, TaxRate = taxRate, WarehouseId = 1 } }
    };

long PostManual(long partyId, string partyCode, decimal price, decimal taxRate, string taxCode, string withholdingSelection)
{
    var doc = BuildDoc(partyId, partyCode, price, taxRate, taxCode);
    InvRepo.SaveDraft(doc);
    if (withholdingSelection.Length > 0)
        Exec($"UPDATE purchase_documents SET withholding_tax_code = '{withholdingSelection}' WHERE doc_id = {doc.DocId};");
    InvRepo.Post(doc.DocId, InvDocKind.PurchaseInvoice, Actor);
    return doc.DocId;
}

// ------------------------------------------------------------ (a) manual
var invA = PostManual(supplier.LedgerId, supplier.Code, 10000m, 15m, CompanyCode + "-VAT15", "WHT5");
var a402 = Gl("PUR", invA, "402", companyId);
var aVat = Gl("PUR", invA, "1205", companyId);
var aAp = Gl("PUR", invA, "2201", companyId);
var aWht = Gl("PUR", invA, "2215", companyId);
Check("(a) purchases debit 10,000", a402.Debit == 10000m, $"{a402}");
Check("(a) input VAT debit 1,500", aVat.Debit == 1500m, $"{aVat}");
Check("(a) AP credited GROSS 11,500", aAp.Credit == 11500m, $"{aAp}");
Check("(a) deduction leg Dr AP 500", aAp.Debit == 500m, $"{aAp}");
Check("(a) withholding payable Cr 500", aWht.Credit == 500m && aWht.Debit == 0m, $"{aWht}");
Check("(a) GL balanced", Scalar($"SELECT COALESCE(SUM(l.debit),0)-COALESCE(SUM(l.credit),0) FROM journal_entry_lines l JOIN journal_entries e ON e.journal_entry_id=l.journal_entry_id WHERE e.source_doc_type='PUR' AND e.source_doc_id='{invA}' AND e.status=1") == 0m);
Check("(a) supplier party balance NET 11,000", PartyBalance(supplier.LedgerId, CompanyCode) == 11000m,
    PartyBalance(supplier.LedgerId, CompanyCode).ToString(CultureInfo.InvariantCulture));
Check("(a) header stores withholding 500 / base 10,000 / rate 5",
    Scalar($"SELECT withholding_amount FROM purchase_documents WHERE doc_id={invA}") == 500m
    && Scalar($"SELECT withholding_base FROM purchase_documents WHERE doc_id={invA}") == 10000m
    && Scalar($"SELECT withholding_rate FROM purchase_documents WHERE doc_id={invA}") == 5m);
Check("(a) register has WITHHOLDING row 500 on base 10,000",
    Scalar($"SELECT COUNT(*) FROM tax_transactions WHERE company_code='{CompanyCode}' AND source_doc_id={invA} AND tax_type='WITHHOLDING' AND tax_amount=500 AND taxable_amount=10000 AND line_no=0") == 1);
Check("(a) register also has the INPUT VAT row (distinguishable)",
    Scalar($"SELECT COUNT(*) FROM tax_transactions WHERE company_code='{CompanyCode}' AND source_doc_id={invA} AND tax_type='VAT' AND tax_amount=1500") == 1);

// ------------------------------------------------------------ (b) allocation
var recon = new ReconciliationRepository("SQLite", cs);
long payLedgerId;
using (var connection = new SqliteConnection(cs))
{
    connection.Open();
    using var tx = connection.BeginTransaction();
    payLedgerId = PartyLedger.WriteEntry(connection, tx, "SQLite", CompanyCode, "S", supplier.LedgerId,
        "PAY", null, "PAY-W1-1", "2026-03-11", 11500m, 0m, "SAR", "Supplier payment", exchangeRate: 1m);
    tx.Commit();
}
var openRows = await recon.GetOpenDocumentsAsync(CompanyCode, "S");
var openA = openRows.Single(r => r.DocId == invA);
Check("(b) open amount is NET 11,000", openA.OpenAmount == 11000m, openA.OpenAmount.ToString(CultureInfo.InvariantCulture));
var overRejected = false;
try { await recon.AllocateAsync(CompanyCode, "S", payLedgerId, "PURCHASE", invA, 11500m, Actor); }
catch (InvalidOperationException) { overRejected = true; }
Check("(b) allocation above net (11,500) rejected", overRejected);
Check("(b) rejected allocation wrote nothing",
    Scalar($"SELECT COUNT(*) FROM payment_allocations WHERE company_id='{CompanyCode}' AND doc_id={invA}") == 0
    && Scalar($"SELECT paid_amount FROM purchase_documents WHERE doc_id={invA}") == 0m);
await recon.AllocateAsync(CompanyCode, "S", payLedgerId, "PURCHASE", invA, 11000m, Actor);
Check("(b) allocating the NET fully settles (no residual)",
    Scalar($"SELECT paid_amount FROM purchase_documents WHERE doc_id={invA}") == 11000m
    && (await recon.GetOpenDocumentsAsync(CompanyCode, "S")).All(r => r.DocId != invA));

// ------------------------------------------------------------ (d) threshold
var invD = PostManual(supplier.LedgerId, supplier.Code, 10000m, 0m, string.Empty, "WHT-T");
Check("(d) below-threshold: withholding amount 0, code persisted (evaluated)",
    Scalar($"SELECT withholding_amount FROM purchase_documents WHERE doc_id={invD}") == 0m
    && ScalarText($"SELECT withholding_tax_code FROM purchase_documents WHERE doc_id={invD}") == "WHT-T");
Check("(d) below-threshold: supplier credited in full",
    Scalar($"SELECT credit FROM party_ledger WHERE company_id='{CompanyCode}' AND doc_id={invD} AND doc_kind='PUR'") == 10000m);
Check("(d) below-threshold: register evaluation row with amount 0",
    Scalar($"SELECT COUNT(*) FROM tax_transactions WHERE company_code='{CompanyCode}' AND source_doc_id={invD} AND tax_type='WITHHOLDING' AND tax_amount=0") == 1);
Check("(d) below-threshold: no GL withholding leg",
    Gl("PUR", invD, "2215", companyId).Credit == 0m);

// ------------------------------------------------------------ (e) cumulative
var invE1 = PostManual(supplier2.LedgerId, supplier2.Code, 6000m, 0m, string.Empty, "WHT-C");
var invE2 = PostManual(supplier2.LedgerId, supplier2.Code, 6000m, 0m, string.Empty, "WHT-C");
Check("(e) cumulative: first invoice (6,000 of 10,000) withholds 0",
    Scalar($"SELECT withholding_amount FROM purchase_documents WHERE doc_id={invE1}") == 0m);
Check("(e) cumulative: crossing invoice withholds 5% of the 2,000 excess = 100",
    Scalar($"SELECT withholding_amount FROM purchase_documents WHERE doc_id={invE2}") == 100m,
    Scalar($"SELECT withholding_amount FROM purchase_documents WHERE doc_id={invE2}").ToString(CultureInfo.InvariantCulture));
Check("(e) cumulative: second supplier balance = 6,000 + 5,900",
    PartyBalance(supplier2.LedgerId, CompanyCode) == 11900m);

// ------------------------------------------------------------ (g) chain path
AppState.SetCompany(ChainCode, "W1 Chain Co", "W1 Chain Co");
var orders = new OrderDocumentRepository("SQLite", cs);
var chain = new CommercialDocumentRepository("SQLite", cs);
var service = new OrderService(orders, chain);
var poId = await service.CreateOrderAsync(
    new CommercialDocumentDraft(chainCompanyId, CommercialDocKinds.PurchaseOrder, "PO-W1-1",
        new DateOnly(2026, 3, 10), 9, "Chain Supplier", 2, "SAR", 1m, null,
        new[] { new CommercialLineDraft(142, "MAT-42", "Item 42", "PCS", 1m, 10000m, 0m, 15m, 2, null, TaxCode: ChainCode + "-VAT15") },
        "SUP-1"), Actor);
await service.PostOrderAsync(CommercialDocSide.Purchase, poId, Actor);
var (receiptId, _) = await service.CreatePurchaseReceiptAsync(chainCompanyId, poId, new DateOnly(2026, 3, 10),
    new[] { new CommercialLineDraft(142, "MAT-42", "Item 42", "PCS", 1m, 10000m, 0m, 15m, 2, 1, TaxCode: ChainCode + "-VAT15") }, Actor);
var (chainInvId, _) = await service.CreatePurchaseInvoiceAsync(chainCompanyId, poId, new DateOnly(2026, 3, 10),
    new[] { new CommercialLineDraft(142, "MAT-42", "Item 42", "PCS", 1m, 10000m, 0m, 15m, 2, 1, TaxCode: ChainCode + "-VAT15") }, Actor, "BILL-W1-1", new DateOnly(2026, 3, 10));
var gAp = Gl("PUR", chainInvId, "2201", chainCompanyId);
var gWht = Gl("PUR", chainInvId, "2215", chainCompanyId);
var gVat = Gl("PUR", chainInvId, "1205", chainCompanyId);
Check("(g) chain: AP credited GROSS 11,500 with Dr AP deduction 500", gAp.Credit == 11500m && gAp.Debit == 500m, $"{gAp}");
Check("(g) chain: withholding payable Cr 500, input VAT Dr 1,500", gWht.Credit == 500m && gVat.Debit == 1500m, $"{gWht} / {gVat}");
Check("(g) chain: supplier party balance NET 11,000", PartyBalance(9, ChainCode) == 11000m,
    PartyBalance(9, ChainCode).ToString(CultureInfo.InvariantCulture));
Check("(g) chain: header + register withholding identical to manual",
    Scalar($"SELECT withholding_amount FROM purchase_documents WHERE doc_id={chainInvId}") == 500m
    && Scalar($"SELECT COUNT(*) FROM tax_transactions WHERE company_code='{ChainCode}' AND source_doc_id={chainInvId} AND tax_type='WITHHOLDING' AND tax_amount=500") == 1);
var chainOpen = (await recon.GetOpenDocumentsAsync(ChainCode, "S")).Single(r => r.DocId == chainInvId);
Check("(g) chain: allocatable open amount is NET 11,000", chainOpen.OpenAmount == 11000m);

// ------------------------------------------- advances interplay (task 3)
// An advance against the chain PO, consumed against the withheld chain
// invoice: clearing Dr 2201 / Cr 1215 unchanged, and the advance can
// never consume the withheld portion (net open bound applies to it too).
Exec($"INSERT INTO chart_of_accounts (account_id, company_id, account_code, account_name, account_type, is_postable, is_active, is_deleted) VALUES ('{Guid.NewGuid():D}', '{chainCompanyId:D}', '1215', 'Advances to Suppliers', 'ASSET', 1, 1, 0);");
var journalRepo = new JournalRepository("SQLite", cs);
var journalSvc = new JournalService(journalRepo);
var advAccount = await journalRepo.ResolveAccountIdAsync(chainCompanyId, "1215");
var cashAccount = await journalRepo.ResolveAccountIdAsync(chainCompanyId, "131");
var advanceId = await journalSvc.CreateDraftAsync(chainCompanyId, new DateOnly(2026, 3, 11), "Advance for PO-W1-1", Actor,
    new[]
    {
        new JournalLineRequest(advAccount, 2000m, 0m, "Advance"),
        new JournalLineRequest(cashAccount, 0m, 2000m, "Cash")
    }, currencyCode: "SAR", exchangeRate: 1m, documentNumber: "PAY-ADV-W1");
Exec($"INSERT INTO voucher_party_links (journal_entry_id, company_id, party_kind, party_id, party_code, doc_kind, doc_no, amount, currency_code, created_by, po_doc_id, is_advance) VALUES ('{advanceId:D}', '{ChainCode}', 'S', 9, 'SUP-1', 'PAY', 'PAY-ADV-W1', 2000, 'SAR', '{Actor}', {poId}, 1);");
await journalSvc.PostAsync(advanceId, Actor);
var advancePayment = (await recon.GetPaymentsAsync(ChainCode, "S")).Single(r => r.DocNo == "PAY-ADV-W1");
await recon.AllocateAsync(ChainCode, "S", advancePayment.LedgerId, "PURCHASE", chainInvId, 2000m, Actor);
var clearingId = ScalarText($"SELECT clearing_journal_id FROM payment_allocations WHERE company_id='{ChainCode}' AND doc_id={chainInvId} AND source_kind='ADVANCE'");
Check("(adv) advance consumed 2,000 against withheld invoice; clearing Dr 2201 / Cr 1215",
    clearingId.Length > 0
    && Scalar($"SELECT COALESCE(SUM(debit),0) FROM journal_entry_lines l JOIN chart_of_accounts a ON a.account_id=l.account_id WHERE l.journal_entry_id='{clearingId}' AND a.account_code='2201'") == 2000m
    && Scalar($"SELECT COALESCE(SUM(credit),0) FROM journal_entry_lines l JOIN chart_of_accounts a ON a.account_id=l.account_id WHERE l.journal_entry_id='{clearingId}' AND a.account_code='1215'") == 2000m);
Check("(adv) open amount after advance = net 11,000 − 2,000 = 9,000",
    (await recon.GetOpenDocumentsAsync(ChainCode, "S")).Single(r => r.DocId == chainInvId).OpenAmount == 9000m);

// ------------------------------------------------------------ (c) reversal
AppState.SetCompany(CompanyCode, "W1 Harness Co", "W1 Harness Co");
InvRepo.ReversePostedInvoice(invA, InvDocKind.PurchaseInvoice, "W1 harness reversal", Actor);
// Supplier balance after reversal = invoice D credit 10,000 (still
// open) − the freed 11,500 payment = −1,500: invoice A's own position
// (net credit 11,000 vs PRT net debit 11,000) nets to exactly zero.
Check("(c) reversal mirrors the invoice NET (PRT debit 11,000; A nets to zero)",
    Scalar($"SELECT debit FROM party_ledger WHERE company_id='{CompanyCode}' AND doc_kind='PRT' AND party_id={supplier.LedgerId}") == 11000m
    && PartyBalance(supplier.LedgerId, CompanyCode) == -1500m,
    PartyBalance(supplier.LedgerId, CompanyCode).ToString(CultureInfo.InvariantCulture));
Check("(c) reversal mirrors withholding payable (net 2215 for invoice A = 0)",
    Scalar($"""
        SELECT COALESCE(SUM(l.credit)-SUM(l.debit),0) FROM journal_entry_lines l
        JOIN chart_of_accounts a ON a.account_id=l.account_id
        JOIN journal_entries e ON e.journal_entry_id=l.journal_entry_id
        WHERE a.account_code='2215' AND e.company_id='{companyId:D}' AND e.status=1
          AND e.source_doc_id IN ('{invA}', (SELECT CAST(doc_id AS TEXT) FROM purchase_documents WHERE original_doc_id={invA}))
        """) == 0m);
Check("(c) reversal writes NEGATIVE register mirror row, original row remains",
    Scalar($"SELECT COUNT(*) FROM tax_transactions WHERE company_code='{CompanyCode}' AND tax_type='WITHHOLDING' AND tax_amount=-500 AND source_doc_kind='PRT'") == 1
    && Scalar($"SELECT COUNT(*) FROM tax_transactions WHERE company_code='{CompanyCode}' AND source_doc_id={invA} AND tax_type='WITHHOLDING' AND tax_amount=500") == 1);
Check("(c) reversal releases the allocation + paid_amount",
    Scalar($"SELECT paid_amount FROM purchase_documents WHERE doc_id={invA}") == 0m
    && Scalar($"SELECT COUNT(*) FROM payment_allocations WHERE company_id='{CompanyCode}' AND doc_id={invA}") == 0);

// ------------------------------------------------------------ (f) tie-out
// Period register total (both companies are separate jurisdictions'
// books; tie out per company): manual company register net =
// 500 (a) + 0 (d) + 100 (e) − 500 (reversal) = 100.
var registerNet = Scalar($"SELECT COALESCE(SUM(tax_amount),0) FROM tax_transactions WHERE company_code='{CompanyCode}' AND tax_type='WITHHOLDING'");
var glNet = Scalar($"""
    SELECT COALESCE(SUM(l.credit)-SUM(l.debit),0) FROM journal_entry_lines l
    JOIN chart_of_accounts a ON a.account_id=l.account_id
    JOIN journal_entries e ON e.journal_entry_id=l.journal_entry_id
    WHERE a.account_code='2215' AND e.company_id='{companyId:D}' AND e.status=1
    """);
Check("(f) register withholding total == GL 2215 movement (manual company)", registerNet == glNet && registerNet == 100m,
    $"register={registerNet} gl={glNet}");
var chainRegister = Scalar($"SELECT COALESCE(SUM(tax_amount),0) FROM tax_transactions WHERE company_code='{ChainCode}' AND tax_type='WITHHOLDING'");
var chainGl = Scalar($"""
    SELECT COALESCE(SUM(l.credit)-SUM(l.debit),0) FROM journal_entry_lines l
    JOIN chart_of_accounts a ON a.account_id=l.account_id
    JOIN journal_entries e ON e.journal_entry_id=l.journal_entry_id
    WHERE a.account_code='2215' AND e.company_id='{chainCompanyId:D}' AND e.status=1
    """);
Check("(f) register withholding total == GL 2215 movement (chain company)", chainRegister == chainGl && chainRegister == 500m,
    $"register={chainRegister} gl={chainGl}");

Console.WriteLine($"W1 HARNESS: {pass} PASS / {fail} FAIL");
return fail == 0 ? 0 : 1;
