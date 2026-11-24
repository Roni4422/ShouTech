// BinHarness — recycle-bin completion proof (SOFT_DELETE_STUDY §6).
//   A: posted sales invoice full cycle — post → soft-delete (GL/party/
//      stock/tax all back to pre-post values, doc invisible, listed in
//      the bin as Posted) → double-delete blocked (BIN-8208) → restore
//      (effects back exactly) → double-restore blocked → soft-delete →
//      purge (record gone, ledger chain retained and netting to zero)
//      → delete after purge is BIN-8201.
//   B: draft document round-trip (no posting effects at any point).
//   C: posted journal cycle + posted-journal purge block (BIN-8205)
//      + draft journal purge.
//   D: payment-allocation guard (BIN-8202).
//   E: card restore unique conflict mapped to BIN-8206 (warehouses
//      partial unique index on card_no), then clean restore.
using System.Globalization;
using Microsoft.Data.Sqlite;
using ShouTech.App;
using ShouTech.App.Configuration;
using ShouTech.App.Reporting.Invoices;
using ShouTech.App.Services;
using ShouTech.Domain;
using ShouTech.Domain.Entities;
using ShouTech.Infrastructure;

const string Actor = "bin-harness";
const string CompanyCode = "BINH";

var root = Path.Combine(Path.GetTempPath(), "bin-harness-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var dbPath = Path.Combine(root, "bin.db");
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

string? BinError(Action action)
{
    try { action(); return null; }
    catch (AppError ex) { return ex.Code; }
    catch (Exception ex) { return "WRONG-EX:" + ex.GetType().Name; }
}

decimal GlNet(string sourceType, long sourceId, string account)
    => Scalar($"SELECT COALESCE(SUM(l.debit)-SUM(l.credit),0) FROM journal_entry_lines l JOIN chart_of_accounts a ON a.account_id=l.account_id JOIN journal_entries e ON e.journal_entry_id=l.journal_entry_id WHERE e.source_doc_type='{sourceType}' AND e.source_doc_id='{sourceId}' AND e.company_id='{companyId:D}' AND a.account_code='{account}' AND e.status=1");

decimal GlAccountNet(string account)
    => Scalar($"SELECT COALESCE(SUM(l.debit)-SUM(l.credit),0) FROM journal_entry_lines l JOIN chart_of_accounts a ON a.account_id=l.account_id JOIN journal_entries e ON e.journal_entry_id=l.journal_entry_id WHERE e.company_id='{companyId:D}' AND a.account_code='{account}' AND e.status=1");

decimal PartyBalance(long partyId)
    => Scalar($"SELECT COALESCE(SUM(debit)-SUM(credit),0) FROM party_ledger WHERE party_id={partyId} AND (company_id='{CompanyCode}' OR company_id='{companyId:D}')");

decimal StockQty(long materialId)
    => Scalar($"SELECT COALESCE(SUM(available_qty),0) FROM material_stock_balances WHERE company_id='{CompanyCode}' AND material_id={materialId} AND is_deleted=0");

decimal TaxTotal(long docId)
    => Scalar($"SELECT COALESCE(SUM(tax_amount),0) FROM tax_transactions WHERE company_code='{CompanyCode}' AND source_doc_id={docId}");

// ---------------------------------------------------------------- setup
var migration = await SchemaMigrator.ApplyAllAsync("SQLite", dbPath);
Check("schema migrates (through 122)", migration.Success, migration.Message);
Check("migration 122 objects exist",
    Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='bin_posting_events'") == 1
    && Scalar("SELECT COUNT(*) FROM pragma_table_info('journal_entries') WHERE name='is_deleted'") == 1
    && Scalar("SELECT COUNT(*) FROM pragma_table_info('sales_documents') WHERE name='deleted_at'") == 1);

var settings = AppCfgSvc.Current;
settings.ConnectionStrings["Default"] = new ConnectionStringSettings { Provider = "SQLite", ConnectionString = dbPath };
settings.Database ??= new DatabaseSettings();
settings.Database.DefaultProvider = "SQLite";
AppState.SetCompany(CompanyCode, "Bin Harness Co", "Bin Harness Co");

Exec($"INSERT INTO companies (company_id, company_code, company_name_ar, company_name_en) VALUES ('{companyId:D}', '{CompanyCode}', 'Bin Co', 'Bin Co');");

var cashAccountId = Guid.NewGuid();
var salesAccountId = Guid.NewGuid();
foreach (var (id, code, name, type) in new[]
{
    (Guid.NewGuid(), "1201", "Receivable", "ASSET"), (Guid.NewGuid(), "1203", "Inventory", "ASSET"),
    (Guid.NewGuid(), "2205", "Output VAT", "LIABILITY"), (salesAccountId, "301", "Sales", "REVENUE"),
    (Guid.NewGuid(), "401", "COGS", "EXPENSE"), (cashAccountId, "131", "Cash", "ASSET"),
})
    Exec($"INSERT INTO chart_of_accounts (account_id, company_id, account_code, account_name, account_type, is_postable, is_active, is_deleted) VALUES ('{id:D}', '{companyId:D}', '{code}', '{name}', '{type}', 1, 1, 0);");

var structure = new AccountingStructureRepository("SQLite", cs);
await structure.CreateFiscalYearAsync(companyId, 2026, Actor);

Exec($"INSERT INTO warehouses (warehouse_id, company_id, warehouse_code, warehouse_name_ar, warehouse_name_en, is_active, is_deleted) VALUES (1, '{CompanyCode}', 'WH-01', 'Main', 'Main', 1, 0);");
Exec($"INSERT INTO materials (material_id, company_id, code, name_ar, name_en, is_stocked, is_service, cost_method, allow_negative, is_deleted) VALUES (42, '{CompanyCode}', 'MAT-42', 'Item 42', 'Item 42', 1, 0, 'WeightedAvg', 0, 0);");
Exec($"INSERT INTO material_stock_balances (company_id, warehouse_id, material_id, available_qty, reserved_qty, average_cost, last_cost, stock_value, is_deleted) VALUES ('{CompanyCode}', 1, 42, 100, 0, 10, 10, 1000, 0);");
Exec($"INSERT INTO parties (id, company_id, kind, code, name_en, credit_limit, is_active, is_deleted, created_by, ledger_id) VALUES ('{Guid.NewGuid():D}', '{CompanyCode}', 'C', 'CUS-1', 'Bin Customer', 0, 1, 0, '{Actor}', 7);");
Exec("""
    INSERT INTO company_tax_settings(company_code, country_code, tax_enabled, tax_scheme) VALUES ('BINH', 'SA', 1, 'VAT');
    INSERT INTO company_tax_rules (company_code, country_code, tax_code, tax_name, tax_type, tax_treatment, rate, calculation_mode, is_recoverable, output_account_code, input_account_code, effective_from, is_default, is_active)
      VALUES ('BINH', 'SA', 'BINH-VAT15', 'Standard VAT', 'VAT', 'Standard', 15, 'Exclusive', 1, '2205', '1205', '2026-01-01', 1, 1);
    """);

// ------------------------------------------------------- A: posted SAL
InvDoc PostSalesInvoice(decimal qty, decimal price)
{
    var doc = new InvDoc
    {
        Kind = InvDocKind.SalesInvoice,
        DocNo = InvRepo.NextDocNo(InvDocKind.SalesInvoice),
        DocDate = new DateTime(2026, 11, 1),
        PartyId = 7,
        PartyCode = "CUS-1",
        PartyName = "Bin Customer",
        WarehouseId = 1,
        CurrencyCode = "SAR",
        ExchangeRate = 1m,
        PaymentMethod = InvPaymentMethod.Credit,
        Lines =
        {
            new InvLine { LineNo = 1, MaterialId = 42, MaterialCode = "MAT-42", Qty = qty, UnitPrice = price, TaxCode = "BINH-VAT15", TaxRate = 15m, WarehouseId = 1 }
        }
    };
    InvRepo.SaveDraft(doc);
    InvRepo.Post(doc.DocId, doc.Kind, Actor);
    return doc;
}

var inv = PostSalesInvoice(5m, 20m);
Check("A0 posted invoice effects: GL receivable 115, party 115, stock 95, tax 15",
    GlNet("SAL", inv.DocId, "1201") == 115m && PartyBalance(7) == 115m && StockQty(42) == 95m && TaxTotal(inv.DocId) == 15m,
    $"gl={GlNet("SAL", inv.DocId, "1201")} party={PartyBalance(7)} stock={StockQty(42)} tax={TaxTotal(inv.DocId)}");

DocBinSvc.SoftDeleteDocument(inv.DocId, InvDocKind.SalesInvoice, Actor);
Check("A1 binned: GL/party/stock/tax all back to pre-post values",
    GlNet("SAL", inv.DocId, "1201") == 0m && PartyBalance(7) == 0m && StockQty(42) == 100m && TaxTotal(inv.DocId) == 0m,
    $"gl={GlNet("SAL", inv.DocId, "1201")} party={PartyBalance(7)} stock={StockQty(42)} tax={TaxTotal(inv.DocId)}");
Check("A2 binned: header flagged and document invisible to InvRepo",
    Scalar($"SELECT is_deleted FROM sales_documents WHERE doc_id={inv.DocId}") == 1
    && InvRepo.LoadByNumber(inv.DocNo, InvDocKind.SalesInvoice) is null);
var binItems = await RecycleBinSvc.ListAsync(CompanyCode);
Check("A3 binned: listed in the recycle bin as a Posted sales document",
    binItems.Any(i => i.TableName == "sales_documents" && i.PrimaryKeyValue == inv.DocId.ToString(CultureInfo.InvariantCulture)
        && i.Status == "Posted" && i.DocKindCode == "SAL"));
Check("A4 double soft-delete blocked (BIN-8208)",
    BinError(() => DocBinSvc.SoftDeleteDocument(inv.DocId, InvDocKind.SalesInvoice, Actor)) == "BIN-8208");

DocBinSvc.RestoreDocument(inv.DocId, InvDocKind.SalesInvoice, Actor);
Check("A5 restored: effects reinstated exactly (GL 115, party 115, stock 95, tax 15, tax mirror pair complete)",
    GlNet("SAL", inv.DocId, "1201") == 115m && PartyBalance(7) == 115m && StockQty(42) == 95m && TaxTotal(inv.DocId) == 15m
    && Scalar($"SELECT COUNT(*) FROM tax_transactions WHERE company_code='{CompanyCode}' AND source_doc_kind='SAL-BINR' AND source_doc_id={inv.DocId}") == 1
    && Scalar($"SELECT is_deleted FROM sales_documents WHERE doc_id={inv.DocId}") == 0,
    $"gl={GlNet("SAL", inv.DocId, "1201")} party={PartyBalance(7)} stock={StockQty(42)} tax={TaxTotal(inv.DocId)}");
Check("A6 double restore blocked (BIN-8208)",
    BinError(() => DocBinSvc.RestoreDocument(inv.DocId, InvDocKind.SalesInvoice, Actor)) == "BIN-8208");

DocBinSvc.SoftDeleteDocument(inv.DocId, InvDocKind.SalesInvoice, Actor);
DocBinSvc.PurgeDocument(inv.DocId, InvDocKind.SalesInvoice, Actor);
Check("A7 purged: header+lines gone; ledger chain retained (4 entries) and netting to zero",
    Scalar($"SELECT COUNT(*) FROM sales_documents WHERE doc_id={inv.DocId}") == 0
    && Scalar($"SELECT COUNT(*) FROM sales_document_lines WHERE doc_id={inv.DocId}") == 0
    && Scalar($"SELECT COUNT(*) FROM journal_entries WHERE company_id='{companyId:D}' AND source_doc_type='SAL' AND source_doc_id='{inv.DocId}'") == 4
    && GlNet("SAL", inv.DocId, "1201") == 0m && PartyBalance(7) == 0m && StockQty(42) == 100m,
    $"entries={Scalar($"SELECT COUNT(*) FROM journal_entries WHERE company_id='{companyId:D}' AND source_doc_type='SAL' AND source_doc_id='{inv.DocId}'")} gl={GlNet("SAL", inv.DocId, "1201")}");
Check("A8 delete after purge is BIN-8201",
    BinError(() => DocBinSvc.SoftDeleteDocument(inv.DocId, InvDocKind.SalesInvoice, Actor)) == "BIN-8201");

// ------------------------------------------------------------ B: draft
var draft = new InvDoc
{
    Kind = InvDocKind.SalesInvoice,
    DocNo = InvRepo.NextDocNo(InvDocKind.SalesInvoice),
    DocDate = new DateTime(2026, 11, 1),
    PartyId = 7, PartyCode = "CUS-1", PartyName = "Bin Customer", WarehouseId = 1,
    CurrencyCode = "SAR", ExchangeRate = 1m, PaymentMethod = InvPaymentMethod.Credit,
    Lines = { new InvLine { LineNo = 1, MaterialId = 42, MaterialCode = "MAT-42", Qty = 1m, UnitPrice = 20m, WarehouseId = 1 } }
};
InvRepo.SaveDraft(draft);
DocBinSvc.SoftDeleteDocument(draft.DocId, InvDocKind.SalesInvoice, Actor);
Check("B1 draft binned with zero posting footprint",
    Scalar($"SELECT is_deleted FROM sales_documents WHERE doc_id={draft.DocId}") == 1
    && Scalar($"SELECT COUNT(*) FROM journal_entries WHERE company_id='{companyId:D}' AND source_doc_type='SAL' AND source_doc_id='{draft.DocId}'") == 0
    && StockQty(42) == 100m);
DocBinSvc.RestoreDocument(draft.DocId, InvDocKind.SalesInvoice, Actor);
DocBinSvc.SoftDeleteDocument(draft.DocId, InvDocKind.SalesInvoice, Actor);
DocBinSvc.PurgeDocument(draft.DocId, InvDocKind.SalesInvoice, Actor);
Check("B2 draft restored, re-binned and purged cleanly",
    Scalar($"SELECT COUNT(*) FROM sales_documents WHERE doc_id={draft.DocId}") == 0);

// ---------------------------------------------------------- C: journal
var journals = new JournalRepository("SQLite", cs);
var entry = new JournalEntry(new DateOnly(2026, 11, 2), "Bin harness journal", null, "SAR", 1m, "JV-BIN-1");
entry.AddLine(cashAccountId, 50m, 0m);
entry.AddLine(salesAccountId, 0m, 50m);
await journals.SaveDraftAsync(companyId, entry, Actor);
await journals.PostAsync(entry.Id, Actor);
Check("C0 posted journal moves cash account +50", GlAccountNet("131") == 50m, $"net={GlAccountNet("131")}");

DocBinSvc.SoftDeleteJournal(entry.Id, Actor);
Check("C1 binned journal: cash net 0 and entry invisible to the voucher read path",
    GlAccountNet("131") == 0m
    && await journals.FindByDocumentNumberAsync(companyId, "JV-BIN-1") is null,
    $"net={GlAccountNet("131")}");
DocBinSvc.RestoreJournal(entry.Id, Actor);
Check("C2 restored journal: cash net back to +50", GlAccountNet("131") == 50m);
DocBinSvc.SoftDeleteJournal(entry.Id, Actor);
Check("C3 posted journal purge blocked (BIN-8205)",
    BinError(() => DocBinSvc.PurgeJournal(entry.Id, Actor)) == "BIN-8205");
DocBinSvc.RestoreJournal(entry.Id, Actor);
Check("C4 journal restored after blocked purge; cash net +50", GlAccountNet("131") == 50m);

var draftEntry = new JournalEntry(new DateOnly(2026, 11, 2), "Bin harness draft", null, "SAR", 1m, "JV-BIN-2");
draftEntry.AddLine(cashAccountId, 5m, 0m);
draftEntry.AddLine(salesAccountId, 0m, 5m);
await journals.SaveDraftAsync(companyId, draftEntry, Actor);
DocBinSvc.SoftDeleteJournal(draftEntry.Id, Actor);
DocBinSvc.PurgeJournal(draftEntry.Id, Actor);
Check("C5 draft journal binned and purged completely",
    Scalar($"SELECT COUNT(*) FROM journal_entries WHERE document_number='JV-BIN-2'") == 0);

// ------------------------------------------------- D: allocation guard
var inv2 = PostSalesInvoice(1m, 20m);
Exec($"INSERT INTO payment_allocations (allocation_id, company_id, party_kind, party_id, payment_ledger_id, doc_table, doc_id, doc_no, amount, allocated_by, allocated_at) VALUES ('{Guid.NewGuid():D}', '{companyId:D}', 'C', 7, '{Guid.NewGuid():D}', 'SALES', {inv2.DocId}, '{inv2.DocNo}', 10, '{Actor}', '2026-11-03 00:00:00');");
Check("D1 soft-delete blocked by payment allocation (BIN-8202), document untouched",
    BinError(() => DocBinSvc.SoftDeleteDocument(inv2.DocId, InvDocKind.SalesInvoice, Actor)) == "BIN-8202"
    && Scalar($"SELECT is_deleted FROM sales_documents WHERE doc_id={inv2.DocId}") == 0);

// ------------------------------------------- E: card restore conflict
Exec($"INSERT INTO warehouses (warehouse_id, company_id, warehouse_code, warehouse_name_ar, warehouse_name_en, card_no, is_active, is_deleted, deleted_at, deleted_by) VALUES (2, '{CompanyCode}', 'WH-02', 'Second', 'Second', 5, 1, 1, '2026-11-03 10:00:00', '{Actor}');");
Exec($"INSERT INTO warehouses (warehouse_id, company_id, warehouse_code, warehouse_name_ar, warehouse_name_en, card_no, is_active, is_deleted) VALUES (3, '{CompanyCode}', 'WH-03', 'Third', 'Third', 5, 1, 0);");
var whItem = new RecycleBinItem
{
    TableName = "warehouses", DatasetKey = "Warehouses", PrimaryKeyColumn = "warehouse_id",
    PrimaryKeyValue = "2", DisplayLabel = "Second", CompanyCode = CompanyCode
};
var conflict = await RecycleBinSvc.RestoreAsync(whItem);
Check("E1 card restore colliding on the live unique key fails as BIN-8206",
    !conflict.Succeeded && (conflict.ErrorMessage ?? string.Empty).Contains("BIN-8206"),
    conflict.ErrorMessage ?? "");
Exec("DELETE FROM warehouses WHERE warehouse_id = 3;");
var restored = await RecycleBinSvc.RestoreAsync(whItem);
Check("E2 card restore succeeds once the conflict is removed",
    restored.Succeeded && Scalar("SELECT is_deleted FROM warehouses WHERE warehouse_id=2") == 0,
    restored.ErrorMessage ?? "");

Console.WriteLine($"BIN HARNESS: {pass} PASS / {fail} FAIL");
return fail == 0 ? 0 : 1;
