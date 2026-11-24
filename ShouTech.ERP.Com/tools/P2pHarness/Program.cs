// P2P consolidated purchase-cycle harness (SQLite, headless).
// Drives the REAL services through the owner report's 10-stage spine:
//   Requisition -> Approval -> RFQ -> Quotation comparison/award -> PO
//   -> Goods Receipt (partial) -> Supplier Invoice (three-way match,
//   bill identity) -> Payment allocation -> Landed Cost (Critical-1
//   linked mode + unlinked mode) -> GRNI period-end tie-out ->
//   Purchase Return (match unwind) + re-invoice.
// Withholding/TDS has its own executed harness (tools/W1Harness, 36/36).
using System.Globalization;
using Microsoft.Data.Sqlite;
using ShouTech.App;
using ShouTech.App.Configuration;
using ShouTech.App.Reporting.Invoices;
using ShouTech.App.Services;
using ShouTech.Application.Services;
using ShouTech.Domain;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;
using ShouTech.Infrastructure;

const string Actor = "p2p-harness";
const string CompanyCode = "P2P";

var root = Path.Combine(Path.GetTempPath(), "p2p-harness-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var dbPath = Path.Combine(root, "p2p.db");
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

string ScalarText(string sql)
{
    using var connection = new SqliteConnection(cs);
    connection.Open();
    using var cmd = connection.CreateCommand();
    cmd.CommandText = sql;
    return Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
}

(decimal Debit, decimal Credit) Gl(string sourceType, long sourceId, string account)
    => (Scalar($"SELECT COALESCE(SUM(l.debit),0) FROM journal_entry_lines l JOIN chart_of_accounts a ON a.account_id=l.account_id JOIN journal_entries e ON e.journal_entry_id=l.journal_entry_id WHERE e.source_doc_type='{sourceType}' AND e.source_doc_id='{sourceId}' AND e.company_id='{companyId:D}' AND a.account_code='{account}' AND e.status=1"),
        Scalar($"SELECT COALESCE(SUM(l.credit),0) FROM journal_entry_lines l JOIN chart_of_accounts a ON a.account_id=l.account_id JOIN journal_entries e ON e.journal_entry_id=l.journal_entry_id WHERE e.source_doc_type='{sourceType}' AND e.source_doc_id='{sourceId}' AND e.company_id='{companyId:D}' AND a.account_code='{account}' AND e.status=1"));

decimal AccountNet(string account) // credit - debit over all posted entries
    => Scalar($"SELECT COALESCE(SUM(l.credit)-SUM(l.debit),0) FROM journal_entry_lines l JOIN chart_of_accounts a ON a.account_id=l.account_id JOIN journal_entries e ON e.journal_entry_id=l.journal_entry_id WHERE e.company_id='{companyId:D}' AND a.account_code='{account}' AND e.status=1");

decimal PartyBalance(long partyId)
    => Scalar($"SELECT COALESCE(SUM(credit)-SUM(debit),0) FROM party_ledger WHERE company_id='{CompanyCode}' AND party_id={partyId}");

decimal StockQty(long materialId)
    => Scalar($"SELECT COALESCE(SUM(available_qty),0) FROM material_stock_balances WHERE company_id='{CompanyCode}' AND material_id={materialId}");

async Task<Exception?> Capture(Func<Task> action)
{
    try { await action(); return null; }
    catch (Exception ex) { return ex; }
}

// ---------------------------------------------------------------- setup
var migration = await SchemaMigrator.ApplyAllAsync("SQLite", dbPath);
Check("schema migrates (through 099)", migration.Success, migration.Message);

var settings = AppCfgSvc.Current;
settings.ConnectionStrings["Default"] = new ConnectionStringSettings { Provider = "SQLite", ConnectionString = dbPath };
settings.Database ??= new DatabaseSettings();
settings.Database.DefaultProvider = "SQLite";
AppState.SetCompany(CompanyCode, "P2P Harness Co", "P2P Harness Co");

Exec($"INSERT INTO companies (company_id, company_code, company_name_ar, company_name_en) VALUES ('{companyId:D}', '{CompanyCode}', 'P2P Co', 'P2P Co');");
foreach (var (code, name, type) in new[]
{
    ("1201", "Receivable", "ASSET"), ("1203", "Inventory", "ASSET"), ("1205", "Input VAT", "ASSET"),
    ("1215", "Advances to Suppliers", "ASSET"), ("131", "Cash", "ASSET"),
    ("2201", "Payable", "LIABILITY"), ("2203", "GRNI", "LIABILITY"), ("2205", "Output VAT", "LIABILITY"),
    ("2215", "Withholding Tax Payable", "LIABILITY"),
    ("301", "Sales", "REVENUE"), ("302", "Sales Returns", "REVENUE"),
    ("401", "COGS", "EXPENSE"), ("402", "Purchases", "EXPENSE"), ("403", "Purchase Returns", "EXPENSE"),
    ("404", "Freight-in", "EXPENSE"), ("415", "PPV", "EXPENSE")
})
    Exec($"INSERT INTO chart_of_accounts (account_id, company_id, account_code, account_name, account_type, is_postable, is_active, is_deleted) VALUES ('{Guid.NewGuid():D}', '{companyId:D}', '{code}', '{name}', '{type}', 1, 1, 0);");

var structure = new AccountingStructureRepository("SQLite", cs);
await structure.CreateFiscalYearAsync(companyId, 2026, Actor);

Exec($"INSERT INTO warehouses (warehouse_id, company_id, warehouse_code, warehouse_name_ar, warehouse_name_en, is_active, is_deleted) VALUES (1, '{CompanyCode}', 'WH-01', 'Main', 'Main', 1, 0);");
Exec($"INSERT INTO materials (material_id, company_id, code, name_ar, name_en, is_stocked, is_service, cost_method, allow_negative, is_deleted) VALUES (42, '{CompanyCode}', 'MAT-42', 'Item 42', 'Item 42', 1, 0, 'WeightedAvg', 0, 0);");
Exec($"INSERT INTO material_stock_balances (company_id, warehouse_id, material_id, available_qty, reserved_qty, average_cost, last_cost, stock_value, is_deleted) VALUES ('{CompanyCode}', 1, 42, 100, 0, 10, 10, 1000, 0);");
Exec($"INSERT INTO parties (id, company_id, kind, code, name_en, credit_limit, is_active, is_deleted, created_by, ledger_id) VALUES ('{Guid.NewGuid():D}', '{CompanyCode}', 'S', 'SUP-1', 'P2P Supplier', 0, 1, 0, '{Actor}', 9);");
Exec($"INSERT INTO parties (id, company_id, kind, code, name_en, credit_limit, is_active, is_deleted, created_by, ledger_id) VALUES ('{Guid.NewGuid():D}', '{CompanyCode}', 'S', 'SHIP-1', 'Shipper One', 0, 1, 0, '{Actor}', 10);");

var orders = new OrderDocumentRepository("SQLite", cs);
var chain = new CommercialDocumentRepository("SQLite", cs);
var service = new OrderService(orders, chain);
var workflow = new PurchaseRequisitionWorkflowService(
    new PurchaseRequisitionWorkflowRepository("SQLite", cs), service, _ => true);

PurchaseRequisitionApprovalStep[] Steps(params string[] approvers)
{
    var steps = new PurchaseRequisitionApprovalStep[approvers.Length];
    for (var i = 0; i < approvers.Length; i++)
        steps[i] = new PurchaseRequisitionApprovalStep(i + 1, approvers[i]);
    return steps;
}

// ------------------------------------------- stage 1-2: requisition + approval
var prqId = await workflow.CreateDraftAsync(new CommercialDocumentDraft(
    companyId, CommercialDocKinds.PurchaseRequisition, "PRQ-P2P-1",
    new DateOnly(2026, 11, 1), null, null, 1, "SAR", 1m, "Consolidated harness requisition",
    new[] { new CommercialLineDraft(42, "MAT-42", "Item 42", "PCS", 10m, 12m, 0m, 0m, 1) },
    RequiredByDate: new DateOnly(2026, 11, 20), RequestType: "Purchase", Department: "Ops"), Actor);
Check("requisition draft created", prqId > 0);
Check("requisition header facts persisted (required-by / type / department)",
    ScalarText($"SELECT request_type FROM purchase_documents WHERE doc_id={prqId}") == "Purchase"
    && ScalarText($"SELECT department FROM purchase_documents WHERE doc_id={prqId}") == "Ops"
    && ScalarText($"SELECT required_by_date FROM purchase_documents WHERE doc_id={prqId}").StartsWith("2026-11-20"));

await workflow.SubmitAsync(companyId, prqId, Steps("manager-1", "manager-2"), Actor);
var wf = await workflow.GetAsync(companyId, prqId);
Check("requisition submitted with 2 approval steps", wf is not null && wf.Status == "Submitted" && wf.Approvals.Count == 2);

var selfEx = await Capture(() => workflow.DecideAsync(companyId, prqId, wf!.Approvals[0].ApprovalId, Actor, true, null));
Check("requester cannot approve own requisition (hard self-approval ban)", selfEx is not null, selfEx?.GetType().Name ?? "");
Check("status unchanged after self-approval attempt",
    (await workflow.GetAsync(companyId, prqId))!.Status == "Submitted");

await workflow.DecideAsync(companyId, prqId, wf!.Approvals[0].ApprovalId, "manager-1", true, null);
wf = await workflow.GetAsync(companyId, prqId);
await workflow.DecideAsync(companyId, prqId, wf!.Approvals[1].ApprovalId, "manager-2", true, "ok");
Check("sequential approvals complete -> Approved",
    (await workflow.GetAsync(companyId, prqId))!.Status == "Approved");

// ------------------------------------------------ stage 3: PRQ -> RFQ -> SQ -> PO
var rfqConv = await workflow.ConvertToRequestForQuotationDraftAsync(companyId, prqId, null, Actor);
var rfqRetry = await workflow.ConvertToRequestForQuotationDraftAsync(companyId, prqId, null, Actor);
var rfqId = rfqConv.RfqId;
Check("PRQ -> RFQ conversion persists exactly one link (idempotent retry)",
    rfqConv.RfqId == rfqRetry.RfqId
    && Scalar($"SELECT COUNT(*) FROM document_links WHERE company_id='{companyId:D}' AND source_doc_id={prqId} AND target_doc_id={rfqId}") == 1);

await service.PostOrderAsync(CommercialDocSide.Purchase, rfqId, Actor);
var (sqId, _) = await service.CreateSupplierQuotationFromRfqAsync(
    rfqId, 9, "SUP-1", "P2P Supplier", "SQ-P2P-1",
    new DateOnly(2026, 11, 3), new DateOnly(2026, 11, 30), "SAR", 1m,
    new[] { new CommercialLineDraft(42, "MAT-42", "Item 42", "PCS", 10m, 11m, SourceLineNo: 1) }, Actor);
await service.PostOrderAsync(CommercialDocSide.Purchase, sqId, Actor);
var offers = await service.CompareSupplierQuotationsAsync(rfqId);
Check("quotation comparison returns the offer at base-currency total 110",
    offers.Count == 1 && offers[0].Lines.Sum(l => l.BaseCurrencyLineTotal) == 110m);

var (poId, _) = await service.CreatePurchaseOrderFromSupplierQuotationAsync(sqId, new DateOnly(2026, 11, 5), Actor);
await service.PostOrderAsync(CommercialDocSide.Purchase, poId, Actor);
var po = await orders.FindByIdAsync(CommercialDocSide.Purchase, poId);
Check("awarded PO posted: supplier SUP-1, total 110, stored progress 0/0",
    po is not null && po.PartyCode == "SUP-1" && po.GrandTotal == 110m
    && Scalar($"SELECT per_received FROM purchase_documents WHERE doc_id={poId}") == 0m
    && Scalar($"SELECT per_billed FROM purchase_documents WHERE doc_id={poId}") == 0m);

// ------------------------------------------------ stage 4: partial receipts + GRNI
var (rcv1Id, _) = await service.CreatePurchaseReceiptAsync(companyId, poId, new DateOnly(2026, 11, 6),
    new[] { new CommercialLineDraft(42, "MAT-42", "Item 42", "PCS", 6m, 11m) }, Actor);
Check("partial receipt 6/10: stock 106, GRNI Cr 66, per_received 60",
    StockQty(42) == 106m && Gl("RCV", rcv1Id, "2203").Credit == 66m && Gl("RCV", rcv1Id, "1203").Debit == 66m
    && Scalar($"SELECT per_received FROM purchase_documents WHERE doc_id={poId}") == 60m);

var (rcv2Id, _) = await service.CreatePurchaseReceiptAsync(companyId, poId, new DateOnly(2026, 11, 7),
    new[] { new CommercialLineDraft(42, "MAT-42", "Item 42", "PCS", 4m, 11m) }, Actor);
Check("receipt 2 completes: stock 110, GRNI cumulative 110, per_received 100",
    StockQty(42) == 110m && AccountNet("2203") == 110m
    && Scalar($"SELECT per_received FROM purchase_documents WHERE doc_id={poId}") == 100m);

// --------------------------------- stage 5: three-way match invariant + invoices
var overEx = await Capture(() => service.CreatePurchaseInvoiceAsync(companyId, poId, new DateOnly(2026, 11, 8),
    new[] { new CommercialLineDraft(42, "MAT-42", "Item 42", "PCS", 11m, 11m) }, Actor));
Check("over-billing beyond received qty is blocked (three-way qty invariant)", overEx is not null);

var (inv1Id, inv1No) = await service.CreatePurchaseInvoiceAsync(companyId, poId, new DateOnly(2026, 11, 8),
    new[] { new CommercialLineDraft(42, "MAT-42", "Item 42", "PCS", 6m, 12m) }, Actor, "BILL-P2P-1", new DateOnly(2026, 11, 8));
var inv1Gl2203 = Gl("PUR", inv1Id, "2203");
var inv1Gl415 = Gl("PUR", inv1Id, "415");
var inv1Gl2201 = Gl("PUR", inv1Id, "2201");
Check("invoice 1 (6 @ 12 vs receipt 11): GRNI cleared 66, PPV 6 to 415, AP Cr 72",
    inv1Gl2203.Debit == 66m && inv1Gl415.Debit == 6m && inv1Gl2201.Credit == 72m,
    $"2203={inv1Gl2203} 415={inv1Gl415} 2201={inv1Gl2201}");
Check("invoice 1 bill identity persisted + 078 match rows = 6 + per_billed 60",
    ScalarText($"SELECT reference_no FROM purchase_documents WHERE doc_id={inv1Id}") == "BILL-P2P-1"
    && Scalar($"SELECT COALESCE(SUM(matched_qty),0) FROM purchase_receipt_invoice_matches WHERE invoice_doc_id={inv1Id}") == 6m
    && Scalar($"SELECT per_billed FROM purchase_documents WHERE doc_id={poId}") == 60m);

var dupEx = await Capture(() => service.CreatePurchaseInvoiceAsync(companyId, poId, new DateOnly(2026, 11, 9),
    new[] { new CommercialLineDraft(42, "MAT-42", "Item 42", "PCS", 1m, 11m) }, Actor, "BILL-P2P-1", new DateOnly(2026, 11, 9)));
Check("duplicate supplier bill reference rejected with catalogued PUR-1011",
    dupEx is AppError dup && dup.Code == "PUR-1011", dupEx?.ToString() ?? "no exception");

var (inv2Id, inv2No) = await service.CreatePurchaseInvoiceAsync(companyId, poId, new DateOnly(2026, 11, 9),
    new[] { new CommercialLineDraft(42, "MAT-42", "Item 42", "PCS", 4m, 11m) }, Actor, "BILL-P2P-2", new DateOnly(2026, 11, 9));
Check("invoice 2 (4 @ 11): GRNI cleared 44, AP Cr 44, per_billed 100, chain GRNI = 0",
    Gl("PUR", inv2Id, "2203").Debit == 44m && Gl("PUR", inv2Id, "2201").Credit == 44m
    && Scalar($"SELECT per_billed FROM purchase_documents WHERE doc_id={poId}") == 100m
    && AccountNet("2203") == 0m);

// ------------------------------------------------ stage 6: payment + allocation
long payLedgerId;
using (var connection = new SqliteConnection(cs))
{
    connection.Open();
    using var tx = connection.BeginTransaction();
    payLedgerId = PartyLedger.WriteEntry(connection, tx, "SQLite", CompanyCode, "S", 9,
        "PAY", null, "PAY-P2P-1", "2026-11-10", 116m, 0m, "SAR", "Supplier payment", exchangeRate: 1m);
    tx.Commit();
}
var recon = new ReconciliationRepository("SQLite", cs);
var openBefore = await recon.GetOpenDocumentsAsync(CompanyCode, "S");
Check("open documents before payment: invoice 1 = 72, invoice 2 = 44",
    openBefore.Single(r => r.DocId == inv1Id).OpenAmount == 72m
    && openBefore.Single(r => r.DocId == inv2Id).OpenAmount == 44m);
await recon.AllocateAsync(CompanyCode, "S", payLedgerId, "PURCHASE", inv1Id, 72m, Actor);
await recon.AllocateAsync(CompanyCode, "S", payLedgerId, "PURCHASE", inv2Id, 44m, Actor);
Check("allocations settle both invoices; supplier party balance = 0",
    Scalar($"SELECT paid_amount FROM purchase_documents WHERE doc_id={inv1Id}") == 72m
    && Scalar($"SELECT paid_amount FROM purchase_documents WHERE doc_id={inv2Id}") == 44m
    && (await recon.GetOpenDocumentsAsync(CompanyCode, "S")).All(r => r.DocId != inv1Id && r.DocId != inv2Id)
    && PartyBalance(9) == 0m, PartyBalance(9).ToString(CultureInfo.InvariantCulture));

// ------------------------------------------------ stage 7: landed cost (Critical-1 linked + unlinked)
var lcPoId = await service.CreateOrderAsync(
    new CommercialDocumentDraft(companyId, CommercialDocKinds.PurchaseOrder, "PO-P2P-LC",
        new DateOnly(2026, 11, 10), 9, "P2P Supplier", 1, "SAR", 1m, null,
        new[] { new CommercialLineDraft(42, "MAT-42", "Item 42", "PCS", 3m, 12m) }, "SUP-1"), Actor);
await service.PostOrderAsync(CommercialDocSide.Purchase, lcPoId, Actor);
var (lcRcvId, lcRcvNo) = await service.CreatePurchaseReceiptAsync(companyId, lcPoId, new DateOnly(2026, 11, 10),
    new[] { new CommercialLineDraft(42, "MAT-42", "Item 42", "PCS", 3m, 12m) }, Actor);
Check("landed-cost receipt: stock 113, GRNI open 36 on this chain",
    StockQty(42) == 113m && AccountNet("2203") == 36m);

// Consume one unit so the landed-cost split has inventory + consumed parts.
using (var connection = new SqliteConnection(cs))
{
    connection.Open();
    using var tx = connection.BeginTransaction();
    StockValuationEngine.Apply(connection, tx, "SQLite",
        new StockMovementRequest(CompanyCode, 1, 42, "2026-11-11", "SAL", "SAL-P2P-1", QtyIn: 0m, QtyOut: 1m));
    tx.Commit();
}

// Carrier freight invoice: the liability lives HERE (party 10, Dr 404 / Cr 2201).
long freightId;
using (var connection = new SqliteConnection(cs))
{
    connection.Open();
    using var tx = connection.BeginTransaction();
    using (var insert = connection.CreateCommand())
    {
        insert.Transaction = tx;
        insert.CommandText = """
            INSERT INTO purchase_documents
                (company_id, doc_kind, doc_no, doc_date, party_id, party_name,
                 currency_code, exchange_rate, subtotal, grand_total, status, created_by)
            VALUES ($company, 'PUR', 'PIN-FRT-P2P', '2026-11-11', 10, 'Shipper One',
                    'SAR', 1, 500, 500, 'Posted', $actor);
            SELECT last_insert_rowid();
            """;
        insert.Parameters.AddWithValue("$company", companyId.ToString("D"));
        insert.Parameters.AddWithValue("$actor", Actor);
        freightId = Convert.ToInt64(insert.ExecuteScalar());
    }
    var gl = GlPosting.PostInTransaction(connection, tx, "SQLite",
        new GlDocumentRequest(companyId, new DateOnly(2026, 11, 11),
            "Purchase invoice PIN-FRT-P2P", "PUR", freightId.ToString(CultureInfo.InvariantCulture), "PIN-FRT-P2P",
            "SAR", 1m,
            new[] { new GlLineRequest("404", 500m, 0m), new GlLineRequest("2201", 0m, 500m) }),
        Actor);
    PartyLedger.WriteEntry(connection, tx, "SQLite", CompanyCode, "S", 10,
        "PUR", freightId, "PIN-FRT-P2P", "2026-11-11", 0m, 500m, "SAR",
        "Freight invoice PIN-FRT-P2P", gl.JournalEntryId.ToString("D"), 1m, 0m, 500m);
    tx.Commit();
}
Check("carrier freight invoice posted: carrier balance 500", PartyBalance(10) == 500m);

var landedCosts = new PurchaseLandedCostService(new PurchaseLandedCostRepository("SQLite", cs), _ => true);
var linkedId = await landedCosts.CreateDraftAsync(
    companyId, new[] { new PurchaseLandedCostReceiptSelection(lcRcvId) }, "LC-P2P-1", new DateOnly(2026, 11, 12),
    "SAR", 1m,
    new[]
    {
        new PurchaseLandedCostChargeDraft("FREIGHT", "Freight already invoiced by carrier", 500m,
            LandedCostAllocationBases.ByValue,
            CreditMode: LandedCostCreditModes.Linked, LinkedDocumentId: freightId)
    }, Actor);
Check("linked charge stored: payee = carrier (10), account resolved from its journal (404)",
    Scalar($"SELECT payee_party_id FROM purchase_landed_cost_charges WHERE landed_cost_id = '{linkedId:D}'") == 10m
    && ScalarText($"SELECT linked_account_code FROM purchase_landed_cost_charges WHERE landed_cost_id = '{linkedId:D}'") == "404");

var linkedJournal = await landedCosts.PostAsync(companyId, linkedId, Actor);
Dictionary<string, (decimal Debit, decimal Credit)> JournalAccounts(Guid journalId)
{
    var rows = new Dictionary<string, (decimal Debit, decimal Credit)>();
    using var connection = new SqliteConnection(cs);
    connection.Open();
    using var cmd = connection.CreateCommand();
    cmd.CommandText = """
        SELECT a.account_code, COALESCE(SUM(l.debit),0), COALESCE(SUM(l.credit),0)
        FROM journal_entries e
        JOIN journal_entry_lines l ON l.journal_entry_id=e.journal_entry_id
        JOIN chart_of_accounts a ON a.account_id=l.account_id
        WHERE e.journal_entry_id=$id AND e.status=1
        GROUP BY a.account_code;
        """;
    cmd.Parameters.AddWithValue("$id", journalId.ToString("D"));
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
        rows[reader.GetString(0)] = (reader.GetDecimal(1), reader.GetDecimal(2));
    return rows;
}
var linkedGl = JournalAccounts(linkedJournal);
Check("CRITICAL-1: linked LCV creates NO new AP credit and NO party-ledger rows",
    !linkedGl.ContainsKey("2201")
    && Scalar("SELECT COUNT(*) FROM party_ledger WHERE doc_kind IN ('LND', 'LND-REV')") == 0m
    && PartyBalance(10) == 500m);
Check("linked LCV reclasses 500 out of Freight-in into inventory + consumed COGS",
    linkedGl.TryGetValue("404", out var f404) && f404.Credit == 500m
    && linkedGl.Values.Sum(x => x.Debit) == 500m && linkedGl.Values.Sum(x => x.Credit) == 500m
    && linkedGl["1203"].Debit > 0m && linkedGl["401"].Debit > 0m,
    string.Join(",", linkedGl.Select(kv => $"{kv.Key}:{kv.Value.Debit}/{kv.Value.Credit}")));

var dupLinkEx = await Capture(() => landedCosts.CreateDraftAsync(
    companyId, new[] { new PurchaseLandedCostReceiptSelection(lcRcvId) }, "LC-P2P-DUP", new DateOnly(2026, 11, 12),
    "SAR", 1m,
    new[]
    {
        new PurchaseLandedCostChargeDraft("FREIGHT", "Second voucher on same document", 100m,
            LandedCostAllocationBases.ByValue,
            CreditMode: LandedCostCreditModes.Linked, LinkedDocumentId: freightId)
    }, Actor));
Check("consumed document cannot be linked twice (PUR-1009)",
    dupLinkEx is AppError dle && dle.Code == "PUR-1009", dupLinkEx?.ToString() ?? "no exception");

await landedCosts.ReverseAsync(companyId, linkedId, "LC-P2P-1-REV", new DateOnly(2026, 11, 13), "Harness reversal", Actor);
Check("linked LCV reversal restores Freight-in exactly and frees the document",
    AccountNet("404") == -500m
    && (await landedCosts.ListLinkableDocumentsAsync(companyId)).Any(x => x.DocumentId == freightId),
    AccountNet("404").ToString(CultureInfo.InvariantCulture));

// Unlinked charge on the main receipt: liability to the receipt supplier.
var unlinkedId = await landedCosts.CreateDraftAsync(
    companyId, new[] { new PurchaseLandedCostReceiptSelection(rcv1Id) }, "LC-P2P-2", new DateOnly(2026, 11, 13),
    "SAR", 1m,
    new[] { new PurchaseLandedCostChargeDraft("FREIGHT", "Unlinked inbound freight", 20m, LandedCostAllocationBases.ByValue) },
    Actor);
var unlinkedJournal = await landedCosts.PostAsync(companyId, unlinkedId, Actor);
var unlinkedGl = JournalAccounts(unlinkedJournal);
Check("unlinked LCV: AP Cr 20, balanced, supplier party credit 20 (LND)",
    unlinkedGl.TryGetValue("2201", out var u2201) && u2201.Credit == 20m
    && unlinkedGl.Values.Sum(x => x.Debit) == 20m && unlinkedGl.Values.Sum(x => x.Credit) == 20m
    && Scalar("SELECT COALESCE(SUM(credit),0) FROM party_ledger WHERE doc_kind = 'LND' AND party_id = 9") == 20m);

// ------------------------------------------------ stage 8: GRNI period-end tie-out
var reconRows = await RptQuery.GoodsReceivedNotInvoicedReconciliationAsync(new DateTime(2026, 12, 31), CancellationToken.None);
var reconRow = reconRows.Single();
decimal ReconVal(string key) => Convert.ToDecimal(reconRow[key], CultureInfo.InvariantCulture);
Check("GRNI tie-out at period end: open receipts 36 == GL 2203 36, difference 0",
    ReconVal("OpenReceiptBase") == 36m && ReconVal("GlBalanceBase") == 36m && ReconVal("DifferenceBase") == 0m,
    $"open={ReconVal("OpenReceiptBase")} gl={ReconVal("GlBalanceBase")} diff={ReconVal("DifferenceBase")}");

// ------------------------------------------------ stage 9: purchase return (PRT) + re-invoice
long PostReturn(decimal qty, string creditRef)
{
    // Mirror the real window: load the original invoice and copy its
    // line's material/unit/price/discount/tax/warehouse verbatim.
    var orig = InvRepo.LoadByNumber(inv2No, InvDocKind.PurchaseInvoice)!;
    var ol = orig.Lines[0];
    var doc = new InvDoc
    {
        Kind = InvDocKind.PurchaseReturn,
        DocNo = InvRepo.NextDocNo(InvDocKind.PurchaseReturn),
        DocDate = new DateTime(2026, 11, 14),
        PartyId = 9,
        PartyCode = "SUP-1",
        PartyName = "P2P Supplier",
        WarehouseId = orig.WarehouseId,
        CurrencyCode = "SAR",
        ExchangeRate = 1m,
        PaymentMethod = InvPaymentMethod.Credit,
        OriginalDocId = inv2Id,
        OriginalDocKind = InvDocKind.PurchaseInvoice,
        OriginalDocNo = inv2No,
        SupplierCreditNoteRef = creditRef,
        Lines =
        {
            new InvLine
            {
                LineNo = 1, MaterialId = ol.MaterialId, MaterialCode = ol.MaterialCode,
                Description = ol.Description, Unit = ol.Unit,
                Qty = qty, UnitPrice = ol.UnitPrice, DiscountPct = ol.DiscountPct,
                TaxCode = ol.TaxCode, TaxRate = ol.TaxRate, TaxCalculationMode = ol.TaxCalculationMode,
                WarehouseId = ol.WarehouseId
            }
        }
    };
    InvRepo.SaveDraft(doc);
    InvRepo.Post(doc.DocId, doc.Kind, Actor);
    return doc.DocId;
}

var prtId = PostReturn(2m, "CN-P2P-1");
Check("PRT (2 @ 11 vs invoice 2): own credit-note ref stored, stock 112 -> 110, supplier balance +20 (LCV) - 22 (PRT) = -2",
    ScalarText($"SELECT COALESCE(supplier_credit_note_ref, '') FROM purchase_documents WHERE doc_id = {prtId}") == "CN-P2P-1"
    && StockQty(42) == 110m && PartyBalance(9) == -2m,
    $"stock={StockQty(42)} bal={PartyBalance(9)}");
Check("PRT unwind: 078 matched qty 10 -> 8",
    Scalar("SELECT COALESCE(SUM(matched_qty),0) FROM purchase_receipt_invoice_matches") == 8m,
    Scalar("SELECT COALESCE(SUM(matched_qty),0) FROM purchase_receipt_invoice_matches").ToString(CultureInfo.InvariantCulture));
Check("PRT unwind: stored per_billed 100 -> 80",
    Scalar($"SELECT per_billed FROM purchase_documents WHERE doc_id={poId}") == 80m,
    Scalar($"SELECT per_billed FROM purchase_documents WHERE doc_id={poId}").ToString(CultureInfo.InvariantCulture));
Check("PRT unwind: unwind rows recorded",
    Scalar($"SELECT COUNT(*) FROM purchase_return_match_unwinds WHERE return_doc_id = {prtId}") >= 1,
    Scalar($"SELECT COUNT(*) FROM purchase_return_match_unwinds WHERE return_doc_id = {prtId}").ToString(CultureInfo.InvariantCulture));
var reconRowsMid = await RptQuery.GoodsReceivedNotInvoicedReconciliationAsync(new DateTime(2026, 12, 31), CancellationToken.None);
var reconRowMid = reconRowsMid.Single();
decimal ReconValMid(string key) => Convert.ToDecimal(reconRowMid[key], CultureInfo.InvariantCulture);
Check("GRNI tie-out exact right after the return (open 58 == GL 58, diff 0)",
    ReconValMid("OpenReceiptBase") == 58m && ReconValMid("GlBalanceBase") == 58m && ReconValMid("DifferenceBase") == 0m,
    $"open={ReconValMid("OpenReceiptBase")} gl={ReconValMid("GlBalanceBase")} diff={ReconValMid("DifferenceBase")}");
Check("PRT journal mirrors the chain clearing: Cr 2203 = unwound receipt base 22",
    Gl("PRT", prtId, "2203").Credit == 22m,
    Gl("PRT", prtId, "2203").Credit.ToString(CultureInfo.InvariantCulture));

var (inv3Id, _) = await service.CreatePurchaseInvoiceAsync(companyId, poId, new DateOnly(2026, 11, 15),
    new[] { new CommercialLineDraft(42, "MAT-42", "Item 42", "PCS", 2m, 11m) }, Actor, "BILL-P2P-3", new DateOnly(2026, 11, 15));
Check("re-invoice of returned qty succeeds after unwind: match back to 10, per_billed 100",
    Scalar("SELECT COALESCE(SUM(matched_qty),0) FROM purchase_receipt_invoice_matches") == 10m
    && Scalar($"SELECT per_billed FROM purchase_documents WHERE doc_id={poId}") == 100m);

var reconRows2 = await RptQuery.GoodsReceivedNotInvoicedReconciliationAsync(new DateTime(2026, 12, 31), CancellationToken.None);
var reconRow2 = reconRows2.Single();
decimal ReconVal2(string key) => Convert.ToDecimal(reconRow2[key], CultureInfo.InvariantCulture);
Check("GRNI tie-out still exact after return + re-invoice (open 36 == GL 36, diff 0)",
    ReconVal2("OpenReceiptBase") == 36m && ReconVal2("GlBalanceBase") == 36m && ReconVal2("DifferenceBase") == 0m,
    $"open={ReconVal2("OpenReceiptBase")} gl={ReconVal2("GlBalanceBase")} diff={ReconVal2("DifferenceBase")}");

// ------------------------------------------------ stage 10: rewind restores the unwound match + stored progress
// (RewindPurchaseReturnMatches is the match-ledger half of a PRT
// reversal; its contract is the ledger + stored progress. The GL half
// is the reversal journal of the PRT itself, which mirrors the PRT
// entry — including the GRNI reclass legs — so the tie-out is
// restored by the pair, not by the rewind alone.)
var rwPoId = await service.CreateOrderAsync(
    new CommercialDocumentDraft(companyId, CommercialDocKinds.PurchaseOrder, "PO-P2P-RW",
        new DateOnly(2026, 11, 16), 9, "P2P Supplier", 1, "SAR", 1m, null,
        new[] { new CommercialLineDraft(42, "MAT-42", "Item 42", "PCS", 2m, 11m) }, "SUP-1"), Actor);
await service.PostOrderAsync(CommercialDocSide.Purchase, rwPoId, Actor);
var (rwRcvId, _) = await service.CreatePurchaseReceiptAsync(companyId, rwPoId, new DateOnly(2026, 11, 16),
    new[] { new CommercialLineDraft(42, "MAT-42", "Item 42", "PCS", 2m, 11m) }, Actor);
var (rwInvId, rwInvNo) = await service.CreatePurchaseInvoiceAsync(companyId, rwPoId, new DateOnly(2026, 11, 16),
    new[] { new CommercialLineDraft(42, "MAT-42", "Item 42", "PCS", 2m, 11m) }, Actor, "BILL-P2P-RW", new DateOnly(2026, 11, 16));

long PostReturnAgainst(long origId, string origNo, decimal qty, string creditRef)
{
    var orig = InvRepo.LoadByNumber(origNo, InvDocKind.PurchaseInvoice)!;
    var ol = orig.Lines[0];
    var doc = new InvDoc
    {
        Kind = InvDocKind.PurchaseReturn,
        DocNo = InvRepo.NextDocNo(InvDocKind.PurchaseReturn),
        DocDate = new DateTime(2026, 11, 17),
        PartyId = 9,
        PartyCode = "SUP-1",
        PartyName = "P2P Supplier",
        // The RW chain's invoice line carries no warehouse (NULL), and
        // the lineage validator requires the return line's warehouse to
        // equal the original's exactly — so the header must not default
        // one in on save either.
        WarehouseId = null,
        CurrencyCode = "SAR",
        ExchangeRate = 1m,
        PaymentMethod = InvPaymentMethod.Credit,
        OriginalDocId = origId,
        OriginalDocKind = InvDocKind.PurchaseInvoice,
        OriginalDocNo = origNo,
        SupplierCreditNoteRef = creditRef,
        Lines =
        {
            new InvLine
            {
                LineNo = 1, MaterialId = ol.MaterialId, MaterialCode = ol.MaterialCode,
                Description = ol.Description, Unit = ol.Unit,
                Qty = qty, UnitPrice = ol.UnitPrice, DiscountPct = ol.DiscountPct,
                TaxCode = ol.TaxCode, TaxRate = ol.TaxRate, TaxCalculationMode = ol.TaxCalculationMode,
                WarehouseId = null
            }
        }
    };
    InvRepo.SaveDraft(doc);
    InvRepo.Post(doc.DocId, doc.Kind, Actor);
    return doc.DocId;
}

var rwPrtId = PostReturnAgainst(rwInvId, rwInvNo, 1m, "CN-P2P-RW");
Check("rewind fixture: PRT 1/2 unwinds the RW match (matched 1, stored per_billed 50)",
    Scalar($"SELECT COALESCE(SUM(matched_qty),0) FROM purchase_receipt_invoice_matches WHERE invoice_doc_id={rwInvId}") == 1m
    && Scalar($"SELECT per_billed FROM purchase_documents WHERE doc_id={rwPoId}") == 50m);
InvRepo.RewindPurchaseReturnMatches(rwPrtId, Actor);
Check("rewind restores the match exactly and stored per_billed returns to 100; unwind rows consumed",
    Scalar($"SELECT COALESCE(SUM(matched_qty),0) FROM purchase_receipt_invoice_matches WHERE invoice_doc_id={rwInvId}") == 2m
    && Scalar($"SELECT per_billed FROM purchase_documents WHERE doc_id={rwPoId}") == 100m
    && Scalar($"SELECT COUNT(*) FROM purchase_return_match_unwinds WHERE return_doc_id = {rwPrtId}") == 0m);

// Global journal balance across the whole scenario.
Check("every posted journal in the scenario is balanced (total Dr == total Cr)",
    Scalar($"SELECT COALESCE(SUM(l.debit),0) FROM journal_entry_lines l JOIN journal_entries e ON e.journal_entry_id=l.journal_entry_id WHERE e.company_id='{companyId:D}' AND e.status=1")
    == Scalar($"SELECT COALESCE(SUM(l.credit),0) FROM journal_entry_lines l JOIN journal_entries e ON e.journal_entry_id=l.journal_entry_id WHERE e.company_id='{companyId:D}' AND e.status=1"));

Console.WriteLine($"P2P HARNESS: {pass} PASS / {fail} FAIL");
return fail == 0 ? 0 : 1;
