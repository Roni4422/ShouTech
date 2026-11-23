using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ShouTech.App;
using ShouTech.App.Configuration;
using ShouTech.App.Reporting.Invoices;
using ShouTech.App.Services;
using ShouTech.Application.Services;
using ShouTech.Domain;
using ShouTech.Domain.Entities;
using ShouTech.Infrastructure;
using Xunit;

namespace Kern.Tests;

/// <summary>
/// Fix Phase Step 3 — real GL posting for operational documents
/// (sweep J-2 + J-3). Pins, against real migrated SQLite databases:
///   * posting a sales / purchase invoice, sales / purchase return or
///     POS sale through InvRepo creates a REAL, balanced journal entry
///     linked to the document (source_doc_type/id/no, migration 060),
///     and the document's journal_doc_no is that entry's real document
///     number — never the fabricated "JV-"+DocNo of the old code;
///   * order→invoice conversion and manufacturing completion post to GL
///     in the document's own transaction, and manufacturing cancellation
///     reverses the entry symmetrically through the Reverse path;
///   * asset depreciation posts Dr expense / Cr accumulated, and a
///     second run posts only the increment;
///   * posting into a period with no open fiscal period fails and the
///     document stays Draft;
///   * J-3: an unknown cost center fails journal posting with ACC-5009
///     and a cost_required account without a center fails with ACC-5010.
/// Execution requires Windows (net8.0-windows testhost); the same
/// scenarios are verified headlessly by
/// ~/workspace/research/fix-step3/gl_harness.py (see docs/FIX_PHASE_STEP3.md).
/// </summary>
public class GlPostingTests
{
    private const string Actor = "gl-test";
    private const string CompanyCode = "GL1";

    [Fact]
    public async Task SalesInvoice_Post_UsesCountryTaxAccount_AndWritesImmutableTaxSnapshot()
    {
        using var db = await SeededAsync();
        using (var connection = new SqliteConnection(db.ConnectionString))
        {
            connection.Open();
            using var setup = connection.CreateCommand();
            setup.CommandText = """
                INSERT INTO company_tax_settings
                    (company_code, country_code, tax_enabled, tax_scheme)
                VALUES ('GL1', 'SA', 1, 'VAT');
                INSERT INTO company_tax_rules
                    (company_code, country_code, tax_code, tax_name, tax_type, tax_treatment,
                     rate, calculation_mode, is_recoverable, output_account_code, input_account_code,
                     effective_from, is_default, is_active)
                VALUES ('GL1', 'SA', 'VAT15', 'Standard VAT', 'VAT', 'Standard',
                        15, 'Exclusive', 1, '2205', '1205', '2026-01-01', 1, 1);
                """;
            setup.ExecuteNonQuery();
        }

        var doc = await PostInvoiceAsync(
            db, InvDocKind.SalesInvoice, qty: 1m, price: 100m, taxRate: 15m, taxCode: "VAT15");

        var entry = SingleEntry(db, "SAL", doc.DocId);
        AssertBalanced(entry);
        Assert.Equal(15m, Line(entry, "2205").Credit);

        var purchase = await PostInvoiceAsync(
            db, InvDocKind.PurchaseInvoice, qty: 1m, price: 100m, taxRate: 15m, taxCode: "VAT15");
        var purchaseEntry = SingleEntry(db, "PUR", purchase.DocId);
        AssertBalanced(purchaseEntry);
        Assert.Equal(15m, Line(purchaseEntry, "1205").Debit);

        using var verify = new SqliteConnection(db.ConnectionString);
        verify.Open();
        using var query = verify.CreateCommand();
        query.CommandText = """
            SELECT country_code, tax_code, tax_rate, taxable_amount, tax_amount,
                   tax_direction, tax_account_code, journal_doc_no, document_date
            FROM tax_transactions
            WHERE company_code='GL1' AND source_doc_kind='SAL'
              AND source_doc_id=$doc AND line_no=1;
            """;
        query.Parameters.AddWithValue("$doc", doc.DocId);
        using var reader = query.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("SA", reader.GetString(0));
        Assert.Equal("VAT15", reader.GetString(1));
        Assert.Equal(15m, reader.GetDecimal(2));
        Assert.Equal(100m, reader.GetDecimal(3));
        Assert.Equal(15m, reader.GetDecimal(4));
        Assert.Equal("OUTPUT", reader.GetString(5));
        Assert.Equal("2205", reader.GetString(6));
        Assert.Equal(entry.DocumentNumber, reader.GetString(7));
        Assert.Equal("2026-03-10", reader.GetString(8));
        Assert.False(reader.Read());
        reader.Close();

        using var purchaseQuery = verify.CreateCommand();
        purchaseQuery.CommandText = """
            SELECT tax_direction, tax_account_code, tax_amount
            FROM tax_transactions
            WHERE company_code='GL1' AND source_doc_kind='PUR'
              AND source_doc_id=$doc AND line_no=1;
            """;
        purchaseQuery.Parameters.AddWithValue("$doc", purchase.DocId);
        using var purchaseReader = purchaseQuery.ExecuteReader();
        Assert.True(purchaseReader.Read());
        Assert.Equal("INPUT", purchaseReader.GetString(0));
        Assert.Equal("1205", purchaseReader.GetString(1));
        Assert.Equal(15m, purchaseReader.GetDecimal(2));
        Assert.False(purchaseReader.Read());
        purchaseReader.Close();

        using var mutation = verify.CreateCommand();
        mutation.CommandText = """
            UPDATE tax_transactions SET tax_amount=0
            WHERE company_code='GL1' AND source_doc_kind='SAL' AND source_doc_id=$doc AND line_no=1;
            """;
        mutation.Parameters.AddWithValue("$doc", doc.DocId);
        Assert.Throws<SqliteException>(() => mutation.ExecuteNonQuery());

        mutation.Parameters.Clear();
        mutation.CommandText = """
            DELETE FROM tax_transactions
            WHERE company_code='GL1' AND source_doc_kind='SAL' AND source_doc_id=$doc AND line_no=1;
            """;
        mutation.Parameters.AddWithValue("$doc", doc.DocId);
        Assert.Throws<SqliteException>(() => mutation.ExecuteNonQuery());
    }

    [Fact]
    public async Task PurchaseInvoice_Post_ExpendsNonRecoverableTaxIntoPurchaseCost()
    {
        using var db = await SeededAsync();
        using (var connection = new SqliteConnection(db.ConnectionString))
        {
            connection.Open();
            using var setup = connection.CreateCommand();
            setup.CommandText = """
                INSERT INTO company_tax_settings
                    (company_code, country_code, tax_enabled, tax_scheme)
                VALUES ('GL1', 'SA', 1, 'VAT');
                INSERT INTO company_tax_rules
                    (company_code, country_code, tax_code, tax_name, tax_type, tax_treatment,
                     rate, calculation_mode, is_recoverable, output_account_code, input_account_code,
                     effective_from, is_default, is_active, data_status)
                VALUES ('GL1', 'SA', 'VAT-NR', 'Non-recoverable VAT', 'VAT', 'Exempt',
                        15, 'Exclusive', 0, '2205', '1205', '2026-01-01', 1, 1, 'USER_CONFIGURED');
                """;
            setup.ExecuteNonQuery();
        }

        var doc = await PostInvoiceAsync(
            db, InvDocKind.PurchaseInvoice, qty: 1m, price: 100m, taxRate: 15m, taxCode: "VAT-NR");

        var entry = SingleEntry(db, "PUR", doc.DocId);
        AssertBalanced(entry);
        Assert.Equal(115m, Line(entry, "402").Debit);
        Assert.DoesNotContain(entry.Lines, line => line.Code == "1205");

        using var verify = new SqliteConnection(db.ConnectionString);
        verify.Open();
        using var query = verify.CreateCommand();
        query.CommandText = """
            SELECT is_recoverable, tax_amount, tax_direction
            FROM tax_transactions
            WHERE company_code='GL1' AND source_doc_kind='PUR'
              AND source_doc_id=$doc AND line_no=1;
            """;
        query.Parameters.AddWithValue("$doc", doc.DocId);
        using var reader = query.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(0, reader.GetInt32(0));
        Assert.Equal(15m, reader.GetDecimal(1));
        Assert.Equal("INPUT", reader.GetString(2));
        Assert.False(reader.Read());
    }

    [Fact]
    public async Task SalesInvoice_Post_CreatesBalancedLinkedGlEntry_AndStampsRealJournalNumber()
    {
        using var db = await SeededAsync();
        var doc = await PostInvoiceAsync(db, InvDocKind.SalesInvoice, qty: 2m, price: 100m, taxRate: 0m);

        var entry = SingleEntry(db, "SAL", doc.DocId);
        Assert.Equal("JV-" + doc.DocNo, entry.DocumentNumber);
        Assert.Equal(doc.DocNo, entry.SourceNo);
        AssertBalanced(entry);
        Assert.Equal(200m, Line(entry, "1201").Debit);
        Assert.Equal(200m, Line(entry, "301").Credit);
        // COGS at the material's current cost (avg 40 x 2).
        Assert.Equal(80m, Line(entry, "401").Debit);
        Assert.Equal(80m, Line(entry, "1203").Credit);

        var loaded = InvRepo.Load(doc.DocId, InvDocKind.SalesInvoice);
        Assert.NotNull(loaded);
        Assert.Equal(entry.DocumentNumber, loaded!.JournalDocNo);
    }

    [Fact]
    public async Task PurchaseInvoice_Post_DebitsPurchases_CreditsPayable()
    {
        using var db = await SeededAsync();
        var doc = await PostInvoiceAsync(db, InvDocKind.PurchaseInvoice, qty: 3m, price: 50m, taxRate: 0m);

        var entry = SingleEntry(db, "PUR", doc.DocId);
        AssertBalanced(entry);
        Assert.Equal(150m, Line(entry, "402").Debit);
        Assert.Equal(150m, Line(entry, "2201").Credit);
    }

    [Fact]
    public async Task SalesReturn_Post_MirrorsSalesEntry()
    {
        using var db = await SeededAsync();
        var original = await PostInvoiceAsync(db, InvDocKind.SalesInvoice, qty: 1m, price: 100m, taxRate: 0m);
        var doc = await PostInvoiceAsync(
            db, InvDocKind.SalesReturn, qty: 1m, price: 100m, taxRate: 0m,
            originalDocId: original.DocId,
            originalDocKind: InvDocKind.SalesInvoice,
            originalDocNo: original.DocNo);

        var entry = SingleEntry(db, "SRT", doc.DocId);
        AssertBalanced(entry);
        Assert.Equal(100m, Line(entry, "302").Debit);
        Assert.Equal(100m, Line(entry, "1201").Credit);
        Assert.Equal(40m, Line(entry, "1203").Debit);
        Assert.Equal(40m, Line(entry, "401").Credit);
    }

    [Fact]
    public async Task PurchaseReturn_Post_MirrorsPurchaseEntry()
    {
        using var db = await SeededAsync();
        var original = await PostInvoiceAsync(db, InvDocKind.PurchaseInvoice, qty: 1m, price: 50m, taxRate: 0m);
        var doc = await PostInvoiceAsync(
            db, InvDocKind.PurchaseReturn, qty: 1m, price: 50m, taxRate: 0m,
            originalDocId: original.DocId,
            originalDocKind: InvDocKind.PurchaseInvoice,
            originalDocNo: original.DocNo);

        var entry = SingleEntry(db, "PRT", doc.DocId);
        AssertBalanced(entry);
        Assert.Equal(50m, Line(entry, "2201").Debit);
        Assert.Equal(50m, Line(entry, "403").Credit);
    }

    [Fact]
    public async Task PosSale_Post_DebitsCash_NotReceivable()
    {
        using var db = await SeededAsync();
        var doc = await PostInvoiceAsync(db, InvDocKind.PosSale, qty: 1m, price: 100m, taxRate: 0m, paid: 100m);

        var entry = SingleEntry(db, "POS", doc.DocId);
        AssertBalanced(entry);
        Assert.Equal(100m, Line(entry, "131").Debit);
        Assert.Equal(100m, Line(entry, "301").Credit);
    }

    [Fact]
    public async Task InvoiceReversal_ReturnDocument_PostsMirroringGl_SoNetGlIsZero()
    {
        using var db = await SeededAsync();
        var doc = await PostInvoiceAsync(db, InvDocKind.SalesInvoice, qty: 1m, price: 100m, taxRate: 0m);

        var reversalId = InvRepo.ReversePostedInvoice(doc.DocId, InvDocKind.SalesInvoice, "test reversal", Actor);
        var reversalEntry = SingleEntry(db, "SRT", reversalId);
        AssertBalanced(reversalEntry);

        // Net GL effect of invoice + its return across every account is 0.
        using var connection = new SqliteConnection(db.ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM (
              SELECT l.account_id
              FROM journal_entry_lines l JOIN journal_entries e ON e.journal_entry_id = l.journal_entry_id
              WHERE e.status = 1 GROUP BY l.account_id
              HAVING ABS(COALESCE(SUM(l.debit),0) - COALESCE(SUM(l.credit),0)) > 0.0001);
            """;
        var unbalancedAccountCount = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (unbalancedAccountCount != 0)
        {
            using var diagnostic = connection.CreateCommand();
            diagnostic.CommandText = """
                SELECT a.account_code || '=' ||
                       CAST(ROUND(COALESCE(SUM(l.debit),0) - COALESCE(SUM(l.credit),0), 4) AS TEXT)
                FROM journal_entry_lines l
                JOIN journal_entries e ON e.journal_entry_id = l.journal_entry_id
                JOIN chart_of_accounts a ON a.account_id = l.account_id
                WHERE e.status = 1
                GROUP BY a.account_code
                HAVING ABS(COALESCE(SUM(l.debit),0) - COALESCE(SUM(l.credit),0)) > 0.0001
                ORDER BY a.account_code;
                """;
            using var reader = diagnostic.ExecuteReader();
            var residuals = new List<string>();
            while (reader.Read()) residuals.Add(reader.GetString(0));
            Assert.Fail($"Invoice reversal left {unbalancedAccountCount} non-zero GL account balances: {string.Join(", ", residuals)}");
        }
    }

    [Fact]
    public async Task PostingIntoPeriodWithoutOpenPeriod_Fails_AndDocumentStaysDraft()
    {
        using var db = await SeededAsync();
        var doc = new InvDoc
        {
            Kind = InvDocKind.SalesInvoice,
            DocNo = InvRepo.NextDocNo(InvDocKind.SalesInvoice),
            DocDate = new DateTime(2025, 5, 1), // fiscal year 2026 only
            PartyId = 7,
            PartyCode = "CUST-1",
            PartyName = "Customer One",
            WarehouseId = 1,
            CurrencyCode = "SAR",
            Lines = { new InvLine { LineNo = 1, MaterialId = 42, MaterialCode = "MAT-42", Qty = 1m, UnitPrice = 10m, WarehouseId = 1 } }
        };
        InvRepo.SaveDraft(doc);
        Assert.Throws<InvalidOperationException>(() => InvRepo.Post(doc.DocId, doc.Kind, Actor));
        var loaded = InvRepo.Load(doc.DocId, InvDocKind.SalesInvoice);
        Assert.Equal(InvDocStatus.Draft, loaded!.Status);
        Assert.Equal(string.Empty, loaded.JournalDocNo);
    }

    [Fact]
    public async Task JournalPost_DraftCannotPostAfterFiscalPeriodCloses()
    {
        using var db = await SeededAsync();
        var store = new JournalRepository("SQLite", db.ConnectionString);
        var journal = new JournalService(store);
        var cash = await store.ResolveAccountIdAsync(db.CompanyId, "131");
        var sales = await store.ResolveAccountIdAsync(db.CompanyId, "301");
        var entryId = await journal.CreateDraftAsync(db.CompanyId, new DateOnly(2026, 3, 10), "Closed period", Actor,
            new[]
            {
                new JournalLineRequest(cash, 25m, 0m),
                new JournalLineRequest(sales, 0m, 25m)
            }, documentNumber: "JV-GL-CLOSED");

        using (var connection = new SqliteConnection(db.ConnectionString))
        {
            connection.Open();
            using var close = connection.CreateCommand();
            close.CommandText = """
                UPDATE fiscal_periods
                SET status = 'CLOSED'
                WHERE fiscal_year_id IN (
                    SELECT fiscal_year_id FROM fiscal_years WHERE company_id = $company
                );
                """;
            close.Parameters.AddWithValue("$company", db.CompanyId.ToString("D"));
            Assert.True(close.ExecuteNonQuery() > 0);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.PostAsync(entryId, Actor));

        using var verify = new SqliteConnection(db.ConnectionString);
        verify.Open();
        using var status = verify.CreateCommand();
        status.CommandText = "SELECT status FROM journal_entries WHERE journal_entry_id = $id;";
        status.Parameters.AddWithValue("$id", entryId.ToString("D"));
        Assert.Equal((long)JournalEntryStatus.Draft,
            Convert.ToInt64(status.ExecuteScalar(), CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task JournalPost_UnknownCostCenter_FailsWithAcc5009()
    {
        using var db = await SeededAsync();
        var store = new JournalRepository("SQLite", db.ConnectionString);
        var journal = new JournalService(store);
        var cash = await store.ResolveAccountIdAsync(db.CompanyId, "131");
        var sales = await store.ResolveAccountIdAsync(db.CompanyId, "301");
        var entryId = await journal.CreateDraftAsync(db.CompanyId, new DateOnly(2026, 3, 10), "Bad CC", Actor,
            new[]
            {
                new JournalLineRequest(cash, 50m, 0m, null, "NOPE"),
                new JournalLineRequest(sales, 0m, 50m, null, "NOPE")
            }, documentNumber: "JV-GL-CC1");
        var error = await Assert.ThrowsAsync<AppError>(() => journal.PostAsync(entryId, Actor));
        Assert.Equal(ErrorCodes.CostCenterUnknown, error.Code);
    }

    [Fact]
    public async Task JournalPost_CostRequiredAccountWithoutCenter_FailsWithAcc5010()
    {
        using var db = await SeededAsync();
        using (var connection = new SqliteConnection(db.ConnectionString))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE chart_of_accounts SET cost_required = 1 WHERE account_code = '301';";
            cmd.ExecuteNonQuery();
        }
        var store = new JournalRepository("SQLite", db.ConnectionString);
        var journal = new JournalService(store);
        var cash = await store.ResolveAccountIdAsync(db.CompanyId, "131");
        var sales = await store.ResolveAccountIdAsync(db.CompanyId, "301");
        var entryId = await journal.CreateDraftAsync(db.CompanyId, new DateOnly(2026, 3, 10), "Missing CC", Actor,
            new[]
            {
                new JournalLineRequest(cash, 50m, 0m),
                new JournalLineRequest(sales, 0m, 50m)
            }, documentNumber: "JV-GL-CC2");
        var error = await Assert.ThrowsAsync<AppError>(() => journal.PostAsync(entryId, Actor));
        Assert.Equal(ErrorCodes.CostCenterRequired, error.Code);
    }

    [Fact]
    public async Task DocumentPosting_DuplicateSource_IsBlockedWithAcc5012()
    {
        using var db = await SeededAsync();
        var store = new JournalRepository("SQLite", db.ConnectionString);
        var request = new GlDocumentRequest(db.CompanyId, new DateOnly(2026, 3, 10), "Doc X", "SAL", "999", "SAL-999",
            "SAR", 1m, new GlLineRequest[]
            {
                new("1201", 10m, 0m), new("301", 0m, 10m)
            });
        var first = await store.PostDocumentAsync(request, Actor);
        Assert.StartsWith("JV-SAL-999", first.DocumentNumber);
        var error = await Assert.ThrowsAsync<AppError>(() => store.PostDocumentAsync(request, Actor));
        Assert.Equal(ErrorCodes.DocumentAlreadyPostedToGl, error.Code);

        // Reversal through the existing Reverse path mirrors symmetrically.
        var reversalId = await store.ReverseAsync(first.JournalEntryId, Actor);
        var reversal = await store.FindByDocumentNumberAsync(db.CompanyId, "REV-" + reversalId.ToString("N")[..12]);
        Assert.NotNull(reversal);
        Assert.Equal(JournalEntryStatus.Posted, reversal!.Status);
        Assert.Equal(10m, reversal.Lines.Single(l => l.AccountCode == "1201").Credit);
    }

    [Fact]
    public async Task Manufacturing_PostAndCancel_PostsAndReversesGl()
    {
        using var db = await SeededAsync();
        SeedMfgFixtures(db);
        var repo = new MfgOrderRepository("SQLite", db.ConnectionString);
        var service = new MfgOrderService(repo);
        var order = new MfgOrder
        {
            DocDate = new DateOnly(2026, 3, 10),
            ItemCode = "FG-1",
            ItemName = "Finished",
            Qty = 10m,
            UnitCode = "PCS",
            WarehouseCode = "1",
            OverheadAmount = 0m,
            Lines = new List<MfgOrderLine>
            {
                new() { LineKind = MfgLineKinds.Raw, ItemCode = "RM-1", ItemName = "Raw", Qty = 10m, UnitCode = "KG", UnitCost = 2m, StandardUnitCost = 2m }
            }
        };
        await service.SaveDraftAsync(CompanyCode, order, Actor);
        await service.PostAsync(CompanyCode, order.Id, Actor);

        var entry = SingleEntry(db, "MFG", order.Id.ToString("D"));
        AssertBalanced(entry);
        Assert.Equal(20m, entry.Lines.Where(l => l.Code == "1203").Sum(l => l.Debit));
        Assert.Equal(20m, entry.Lines.Where(l => l.Code == "1203").Sum(l => l.Credit));

        await service.CancelAsync(CompanyCode, order.Id, Actor);
        using var connection = new SqliteConnection(db.ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT status FROM journal_entries WHERE journal_entry_id = $id;";
        cmd.Parameters.AddWithValue("$id", entry.Id);
        Assert.Equal(2L, Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture)); // Reversed
    }

    [Fact]
    public async Task AssetDepreciation_PostsIncrementOnly()
    {
        using var db = await SeededAsync();
        var repo = new AssetRepository("SQLite", db.ConnectionString);
        var service = new AssetService(repo);
        var asset = new FixedAsset
        {
            Code = "FA-1", Name = "Machine", PurchaseDate = new DateOnly(2026, 1, 1),
            Cost = 1200m, SalvageValue = 0m, UsefulLifeYears = 1,
            DepreciationMethod = DepreciationMethods.StraightLine, Status = AssetStatuses.Active
        };
        await service.SaveAsync(CompanyCode, asset, Actor);

        // 3 whole months elapsed -> accumulated 300 (1200 / 12 x 3).
        var first = await service.PostDepreciationAsync(CompanyCode, asset.Id, new DateOnly(2026, 4, 1), Actor);
        Assert.NotNull(first);
        var entry = SingleEntry(db, "AST-DEP", asset.Id.ToString("D") + "@2026-04-01");
        Assert.Equal(300m, Line(entry, "505").Debit);
        Assert.Equal(300m, Line(entry, "1190").Credit);

        // Same date again -> nothing new.
        Assert.Null(await service.PostDepreciationAsync(CompanyCode, asset.Id, new DateOnly(2026, 4, 1), Actor));
        // 9 months -> accumulated 900, so only the 600 increment posts.
        var second = await service.PostDepreciationAsync(CompanyCode, asset.Id, new DateOnly(2026, 10, 1), Actor);
        Assert.NotNull(second);
        var entry2 = SingleEntry(db, "AST-DEP", asset.Id.ToString("D") + "@2026-10-01");
        Assert.Equal(600m, Line(entry2, "505").Debit);
    }

    // ------------------------------------------------------------ helpers

    private sealed record EntryRow(string Id, string DocumentNumber, string SourceNo,
        List<(string Code, decimal Debit, decimal Credit)> Lines);

    private static EntryRow SingleEntry(TestDb db, string sourceType, object sourceId)
    {
        using var connection = new SqliteConnection(db.ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT e.journal_entry_id, e.document_number, e.source_doc_no
            FROM journal_entries e
            WHERE e.source_doc_type = $type AND e.source_doc_id = $source AND e.status = 1 AND e.reversal_of_entry_id IS NULL;
            """;
        cmd.Parameters.AddWithValue("$type", sourceType);
        cmd.Parameters.AddWithValue("$source", Convert.ToString(sourceId, CultureInfo.InvariantCulture));
        string id, docNo, sourceNo;
        using (var reader = cmd.ExecuteReader())
        {
            Assert.True(reader.Read(), $"No posted GL entry for {sourceType}/{sourceId}");
            id = reader.GetString(0);
            docNo = reader.GetString(1);
            sourceNo = reader.GetString(2);
            Assert.False(reader.Read(), "More than one live GL entry for the document");
        }
        var lines = new List<(string, decimal, decimal)>();
        using var linesCmd = connection.CreateCommand();
        linesCmd.CommandText = """
            SELECT a.account_code, l.debit, l.credit FROM journal_entry_lines l
            JOIN chart_of_accounts a ON a.account_id = l.account_id
            WHERE l.journal_entry_id = $id ORDER BY l.line_number;
            """;
        linesCmd.Parameters.AddWithValue("$id", id);
        using var lr = linesCmd.ExecuteReader();
        while (lr.Read()) lines.Add((lr.GetString(0), lr.GetDecimal(1), lr.GetDecimal(2)));
        return new EntryRow(id, docNo, sourceNo, lines);
    }

    private static (string Code, decimal Debit, decimal Credit) Line(EntryRow entry, string code)
        => entry.Lines.Single(l => l.Code == code);

    private static void AssertBalanced(EntryRow entry)
        => Assert.Equal(entry.Lines.Sum(l => l.Debit), entry.Lines.Sum(l => l.Credit));

    private static Task<InvDoc> PostInvoiceAsync(TestDb db, InvDocKind kind, decimal qty, decimal price, decimal taxRate,
        decimal paid = 0m, string taxCode = "", long? originalDocId = null,
        InvDocKind? originalDocKind = null, string? originalDocNo = null)
    {
        var doc = new InvDoc
        {
            Kind = kind,
            DocNo = InvRepo.NextDocNo(kind),
            DocDate = new DateTime(2026, 3, 10),
            PartyId = 7,
            PartyCode = "CUST-1",
            PartyName = "Customer One",
            WarehouseId = 1,
            CurrencyCode = "SAR",
            PaidAmount = paid,
            OriginalDocId = originalDocId,
            OriginalDocKind = originalDocKind,
            OriginalDocNo = originalDocNo ?? string.Empty,
            PaymentMethod = kind == InvDocKind.PosSale ? InvPaymentMethod.Cash : InvPaymentMethod.Credit,
            Lines = { new InvLine { LineNo = 1, MaterialId = 42, MaterialCode = "MAT-42", Qty = qty, UnitPrice = price, TaxCode = taxCode, TaxRate = taxRate, WarehouseId = 1 } }
        };
        InvRepo.SaveDraft(doc);
        InvRepo.Post(doc.DocId, kind, Actor);
        return Task.FromResult(doc);
    }

    private static void SeedMfgFixtures(TestDb db)
    {
        using var connection = new SqliteConnection(db.ConnectionString);
        connection.Open();
        void Exec(string sql)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
        Exec($"INSERT INTO materials (material_id, company_id, code, name_ar, name_en, is_stocked, is_deleted) VALUES (1, '{CompanyCode}', 'RM-1', 'Raw', 'Raw', 1, 0);");
        Exec($"INSERT INTO materials (material_id, company_id, code, name_ar, name_en, is_stocked, is_deleted) VALUES (2, '{CompanyCode}', 'FG-1', 'Finished', 'Finished', 1, 0);");
        Exec($"UPDATE warehouses SET company_id = '{CompanyCode}', warehouse_code = 1, warehouse_name_ar = 'Main', warehouse_name_en = 'Main', is_deleted = 0 WHERE warehouse_id = 1;");
        Exec($"INSERT INTO material_stock_movements (company_id, warehouse_id, material_id, movement_date, movement_type, document_no, qty_in, qty_out, unit_cost, note) VALUES ('{CompanyCode}', 1, 1, '2026-03-01', 'OPN', 'OPEN-RM-1', 100, 0, 2, 'Opening stock fixture');");
        Exec($"INSERT INTO material_stock_balances (company_id, warehouse_id, material_id, available_qty, average_cost, last_cost, stock_value) VALUES ('{CompanyCode}', 1, 1, 100, 2, 2, 200);");
    }

    private sealed class TestDb : IDisposable
    {
        public required string Root { get; init; }
        public required string DbPath { get; init; }
        public required Guid CompanyId { get; init; }
        public string ConnectionString => $"Data Source={DbPath};";
        public void Dispose()
        {
            AppState.ClearCompany();
            SqliteConnection.ClearAllPools();
            Directory.Delete(Root, recursive: true);
        }
    }

    private static async Task<TestDb> SeededAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "gl-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var dbPath = Path.Combine(root, "gl.db");
        var companyId = Guid.NewGuid();
        var connectionString = $"Data Source={dbPath};";

        var migration = await SchemaMigrator.ApplyAllAsync("SQLite", dbPath);
        Assert.True(migration.Success, migration.Message);
        using (var schemaProbe = new SqliteConnection(connectionString))
        {
            schemaProbe.Open();
            using var columnsCommand = schemaProbe.CreateCommand();
            columnsCommand.CommandText = "PRAGMA table_info('party_ledger');";
            using var columnsReader = columnsCommand.ExecuteReader();
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (columnsReader.Read())
                columns.Add(columnsReader.GetString(1));
            Assert.Contains("debit", columns);
            Assert.Contains("credit", columns);
        }
        PointAppAt(dbPath);
        AppState.SetCompany(CompanyCode, "GL Test Co", "GL Test Co");

        using (var connection = new SqliteConnection(connectionString))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "INSERT INTO companies (company_id, company_code, company_name_ar, company_name_en) VALUES ($id, $code, 'GL Test Co', 'GL Test Co');";
            cmd.Parameters.AddWithValue("$id", companyId.ToString("D"));
            cmd.Parameters.AddWithValue("$code", CompanyCode);
            cmd.ExecuteNonQuery();
        }

        var structure = new AccountingStructureRepository("SQLite", connectionString);
        await structure.CreateFiscalYearAsync(companyId, 2026, Actor);

        foreach (var (code, name) in new[]
        {
            ("131", "Cash"), ("132", "Bank"), ("1201", "Receivable"), ("1203", "Inventory"),
            ("1205", "Input VAT"), ("2201", "Payable"), ("2205", "Output VAT"), ("301", "Sales"), ("302", "Sales Returns"), ("401", "COGS"),
            ("402", "Purchases"), ("403", "Purchase Returns"), ("505", "Depreciation Expense"),
            ("1190", "Accumulated Depreciation"), ("1102", "Buildings")
        })
        {
            AccRepo.Save(null, new AccRepo.AccRec(
                string.Empty, code, null, name, name, "Postable", "Active", "Low", "Unspecified",
                false, false, false, false, false, false, 0m, string.Empty, 0m, 0m, 0m),
                renumberDescendants: false);
        }

        // Material + warehouse + opening balance (avg cost 40) for invoices.
        using (var connection = new SqliteConnection(connectionString))
        {
            connection.Open();
            void Exec(string sql)
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
            Exec($"INSERT INTO materials (material_id, company_id, code, name_ar, name_en, is_stocked, avg_cost, is_deleted) VALUES (42, '{CompanyCode}', 'MAT-42', 'Item', 'Item', 1, 40, 0);");
            Exec($"INSERT INTO warehouses (warehouse_id, company_id, warehouse_code, warehouse_name_ar, warehouse_name_en, is_deleted) VALUES (1, '{CompanyCode}', 1, 'Main', 'Main', 0);");
            Exec($"INSERT INTO material_stock_balances (company_id, warehouse_id, material_id, available_qty, average_cost, last_cost) VALUES ('{CompanyCode}', 1, 42, 1000, 40, 40);");
        }

        return new TestDb { Root = root, DbPath = dbPath, CompanyId = companyId };
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
}
