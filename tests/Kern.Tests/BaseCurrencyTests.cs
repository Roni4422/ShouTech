using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
/// Fix Phase Step 7 — base-currency amounts on every money line
/// (sweep J-5, migration 062). Pins, against real migrated SQLite
/// databases whose company base currency is SAR:
///   * a USD journal entry at 3.75 stores base amounts at 3.75x and
///     its base totals balance (the sweep's J-5 case);
///   * a foreign-currency sales invoice posts GL lines and a
///     party_ledger row whose base amounts are stored total x rate,
///     while its COGS leg's base amount stays the engine's base cost;
///   * mixed-currency entries each balance in base, and an entry whose
///     stored base amounts do NOT balance is refused at posting
///     (ACC-5001) even though it balances in transaction currency;
///   * a foreign document with NO rate (stored 0) and no master rate
///     fails with ACC-5015 and writes nothing; with a master rate in
///     currency_rates it posts at the master rate;
///   * a base-currency document posts identically to before (rate 1,
///     base == transaction);
///   * a legacy draft whose lines lack base amounts is healed from
///     its header rate inside the posting transaction.
/// Execution requires Windows (net8.0-windows testhost); the same
/// scenarios are verified headlessly by
/// ~/workspace/research/fix-step7/fx_harness.py (see
/// docs/FIX_PHASE_STEP1.md).
/// </summary>
public class BaseCurrencyTests
{
    private const string Actor = "fx-test";
    private const string CompanyCode = "FX1";

    [Fact]
    public async Task UsdJournal_At375_StoresBaseAmounts_AndBalancesInBase()
    {
        using var db = await SeededAsync();
        var store = new JournalRepository("SQLite", db.ConnectionString);
        var journal = new JournalService(store);
        var cash = await store.ResolveAccountIdAsync(db.CompanyId, "131");
        var sales = await store.ResolveAccountIdAsync(db.CompanyId, "301");
        var entryId = await journal.CreateDraftAsync(db.CompanyId, new DateOnly(2026, 3, 10), "USD sale", Actor,
            new[]
            {
                new JournalLineRequest(cash, 100m, 0m),
                new JournalLineRequest(sales, 0m, 100m)
            },
            currencyCode: "USD", exchangeRate: 3.75m, documentNumber: "JV-FX-1");
        await journal.PostAsync(entryId, Actor);

        var snapshot = await store.FindByDocumentNumberAsync(db.CompanyId, "JV-FX-1");
        Assert.NotNull(snapshot);
        Assert.Equal(375m, snapshot!.Lines.Single(l => l.AccountCode == "131").BaseDebit);
        Assert.Equal(375m, snapshot.Lines.Single(l => l.AccountCode == "301").BaseCredit);
        Assert.Equal(375m, snapshot.Lines.Sum(l => l.BaseDebit));
        Assert.Equal(snapshot.Lines.Sum(l => l.BaseDebit), snapshot.Lines.Sum(l => l.BaseCredit));
        // Header carries the base currency the amounts are expressed in.
        Assert.Equal("SAR", Scalar(db, "SELECT base_currency_code FROM journal_entries WHERE document_number='JV-FX-1'"));
    }

    [Fact]
    public async Task ForeignSalesInvoice_PostsGlAndPartyLedger_InBase()
    {
        using var db = await SeededAsync();
        var doc = PostInvoice(db, InvDocKind.SalesInvoice, qty: 2m, price: 100m, currency: "USD", rate: 3.75m);

        // GL: receivable base = stored total (200) x 3.75; COGS base
        // stays the engine's base cost (avg 40 x 2 = 80), NOT 80 x 3.75.
        Assert.Equal(750m, BaseSum(db, "1201", debit: true));
        Assert.Equal(750m, BaseSum(db, "301", debit: false));
        Assert.Equal(80m, BaseSum(db, "401", debit: true));
        Assert.Equal(80m, BaseSum(db, "1203", debit: false));
        // Party ledger: base exposure is the converted amount.
        Assert.Equal(750m, Convert.ToDecimal(Scalar(db,
            "SELECT CAST(COALESCE(SUM(base_debit),0) AS TEXT) FROM party_ledger WHERE doc_kind='SAL'"), CultureInfo.InvariantCulture));
        Assert.Equal(200m, Convert.ToDecimal(Scalar(db,
            "SELECT CAST(COALESCE(SUM(debit),0) AS TEXT) FROM party_ledger WHERE doc_kind='SAL'"), CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task MixedCurrencyEntries_EachBalanceInBase_AndBaseImbalanceIsRefused()
    {
        using var db = await SeededAsync();
        var store = new JournalRepository("SQLite", db.ConnectionString);
        var journal = new JournalService(store);
        var cash = await store.ResolveAccountIdAsync(db.CompanyId, "131");
        var sales = await store.ResolveAccountIdAsync(db.CompanyId, "301");

        var usd = await journal.CreateDraftAsync(db.CompanyId, new DateOnly(2026, 3, 10), "USD", Actor,
            new[] { new JournalLineRequest(cash, 100m, 0m), new JournalLineRequest(sales, 0m, 100m) },
            currencyCode: "USD", exchangeRate: 3.75m, documentNumber: "JV-FX-M1");
        var eur = await journal.CreateDraftAsync(db.CompanyId, new DateOnly(2026, 3, 11), "EUR", Actor,
            new[] { new JournalLineRequest(cash, 50m, 0m), new JournalLineRequest(sales, 0m, 50m) },
            currencyCode: "EUR", exchangeRate: 4.2m, documentNumber: "JV-FX-M2");
        await journal.PostAsync(usd, Actor);
        await journal.PostAsync(eur, Actor);
        Assert.Equal(375m + 210m, Convert.ToDecimal(Scalar(db,
            "SELECT CAST(COALESCE(SUM(base_debit),0) AS TEXT) FROM journal_entry_lines l JOIN journal_entries e ON e.journal_entry_id=l.journal_entry_id WHERE e.status=1"), CultureInfo.InvariantCulture));

        // Corrupt one draft's stored base amounts so it balances in
        // transaction currency but NOT in base: posting must refuse.
        var bad = await journal.CreateDraftAsync(db.CompanyId, new DateOnly(2026, 3, 12), "Bad base", Actor,
            new[] { new JournalLineRequest(cash, 10m, 0m), new JournalLineRequest(sales, 0m, 10m) },
            currencyCode: "USD", exchangeRate: 3.75m, documentNumber: "JV-FX-M3");
        Exec(db, $"UPDATE journal_entry_lines SET base_credit = 1 WHERE journal_entry_id = '{bad:D}' AND base_credit > 0;");
        var error = await Assert.ThrowsAsync<AppError>(() => journal.PostAsync(bad, Actor));
        Assert.Equal(ErrorCodes.JournalNotBalanced, error.Code);
    }

    [Fact]
    public async Task ForeignInvoice_WithoutAnyRate_FailsAcc5015_AndWritesNothing()
    {
        using var db = await SeededAsync();
        var doc = new InvDoc
        {
            Kind = InvDocKind.SalesInvoice,
            DocNo = InvRepo.NextDocNo(InvDocKind.SalesInvoice),
            DocDate = new DateTime(2026, 3, 10),
            PartyId = 7,
            PartyCode = "CUST-1",
            PartyName = "Customer One",
            WarehouseId = 1,
            CurrencyCode = "USD",
            ExchangeRate = 0m, // no document rate, and no master rate exists
            Lines = { new InvLine { LineNo = 1, MaterialId = 42, MaterialCode = "MAT-42", Qty = 1m, UnitPrice = 100m, WarehouseId = 1 } }
        };
        InvRepo.SaveDraft(doc);
        var error = Assert.Throws<AppError>(() => InvRepo.Post(doc.DocId, doc.Kind, Actor));
        Assert.Equal(ErrorCodes.ExchangeRateMissing, error.Code);
        Assert.Equal(InvDocStatus.Draft, InvRepo.Load(doc.DocId, InvDocKind.SalesInvoice)!.Status);
        Assert.Equal("0", Scalar(db, "SELECT CAST(COUNT(*) AS TEXT) FROM journal_entries WHERE source_doc_id = '" + doc.DocId + "'"));
        Assert.Equal("0", Scalar(db, "SELECT CAST(COUNT(*) AS TEXT) FROM party_ledger WHERE doc_id = " + doc.DocId));
    }

    [Fact]
    public async Task ForeignInvoice_WithMasterRateOnly_PostsAtMasterRate()
    {
        using var db = await SeededAsync();
        Exec(db, $"INSERT INTO currency_rates (id, company_id, currency_code, rate_date, rate) VALUES ('{Guid.NewGuid():D}', '{CompanyCode}', 'USD', '2026-03-01', 3.75);");
        var doc = new InvDoc
        {
            Kind = InvDocKind.SalesInvoice,
            DocNo = InvRepo.NextDocNo(InvDocKind.SalesInvoice),
            DocDate = new DateTime(2026, 3, 10),
            PartyId = 7,
            PartyCode = "CUST-1",
            PartyName = "Customer One",
            WarehouseId = 1,
            CurrencyCode = "USD",
            ExchangeRate = 0m,
            Lines = { new InvLine { LineNo = 1, MaterialId = 42, MaterialCode = "MAT-42", Qty = 1m, UnitPrice = 100m, WarehouseId = 1 } }
        };
        InvRepo.SaveDraft(doc);
        InvRepo.Post(doc.DocId, doc.Kind, Actor);
        Assert.Equal(375m, BaseSum(db, "1201", debit: true));
        // The journal header stores the EFFECTIVE (master) rate.
        Assert.Equal("3.75", Scalar(db, "SELECT CAST(exchange_rate AS TEXT) FROM journal_entries WHERE source_doc_id = '" + doc.DocId + "'"));
    }

    [Fact]
    public async Task BaseCurrencyInvoice_BaseEqualsTransaction()
    {
        using var db = await SeededAsync();
        var doc = PostInvoice(db, InvDocKind.SalesInvoice, qty: 1m, price: 100m, currency: "SAR", rate: 1m);
        Assert.Equal(100m, BaseSum(db, "1201", debit: true));
        Assert.Equal(100m, Convert.ToDecimal(Scalar(db,
            "SELECT CAST(COALESCE(SUM(base_debit),0) AS TEXT) FROM party_ledger WHERE doc_kind='SAL'"), CultureInfo.InvariantCulture));
        _ = doc;
    }

    [Fact]
    public async Task LegacyDraft_WithoutBaseAmounts_IsHealedAtPosting()
    {
        using var db = await SeededAsync();
        var store = new JournalRepository("SQLite", db.ConnectionString);
        var journal = new JournalService(store);
        var cash = await store.ResolveAccountIdAsync(db.CompanyId, "131");
        var sales = await store.ResolveAccountIdAsync(db.CompanyId, "301");
        var entryId = await journal.CreateDraftAsync(db.CompanyId, new DateOnly(2026, 3, 10), "Legacy", Actor,
            new[] { new JournalLineRequest(cash, 100m, 0m), new JournalLineRequest(sales, 0m, 100m) },
            currencyCode: "USD", exchangeRate: 3.75m, documentNumber: "JV-FX-LEG");
        // Simulate a pre-062 draft: base amounts absent.
        Exec(db, $"UPDATE journal_entry_lines SET base_debit = NULL, base_credit = NULL, exchange_rate = NULL WHERE journal_entry_id = '{entryId:D}';");
        await journal.PostAsync(entryId, Actor);
        var snapshot = await store.FindByDocumentNumberAsync(db.CompanyId, "JV-FX-LEG");
        Assert.Equal(375m, snapshot!.Lines.Single(l => l.AccountCode == "131").BaseDebit);
    }

    // ------------------------------------------------------------------

    private static decimal BaseSum(TestDb db, string accountCode, bool debit)
    {
        var column = debit ? "base_debit" : "base_credit";
        return Convert.ToDecimal(Scalar(db, $"""
            SELECT CAST(COALESCE(SUM(l.{column}),0) AS TEXT)
            FROM journal_entry_lines l
            JOIN journal_entries e ON e.journal_entry_id = l.journal_entry_id
            JOIN chart_of_accounts a ON a.account_id = l.account_id
            WHERE e.status = 1 AND a.account_code = '{accountCode}'
            """), CultureInfo.InvariantCulture);
    }

    private static string Scalar(TestDb db, string sql)
    {
        using var connection = new SqliteConnection(db.ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "";
    }

    private static void Exec(TestDb db, string sql)
    {
        using var connection = new SqliteConnection(db.ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static InvDoc PostInvoice(TestDb db, InvDocKind kind, decimal qty, decimal price, string currency, decimal rate)
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
            CurrencyCode = currency,
            ExchangeRate = rate,
            Lines = { new InvLine { LineNo = 1, MaterialId = 42, MaterialCode = "MAT-42", Qty = qty, UnitPrice = price, WarehouseId = 1 } }
        };
        InvRepo.SaveDraft(doc);
        InvRepo.Post(doc.DocId, kind, Actor);
        return doc;
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
        var root = Path.Combine(Path.GetTempPath(), "fx-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var dbPath = Path.Combine(root, "fx.db");
        var companyId = Guid.NewGuid();
        var connectionString = $"Data Source={dbPath};";

        Assert.True((await SchemaMigrator.ApplyAllAsync("SQLite", dbPath)).Success);
        PointAppAt(dbPath);
        AppState.SetCompany(CompanyCode, "FX Test Co", "FX Test Co");

        using (var connection = new SqliteConnection(connectionString))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            // Base currency SAR — the premise of every case in this file.
            cmd.CommandText = "INSERT INTO companies (company_id, company_code, company_name_ar, company_name_en, currency_code) VALUES ($id, $code, 'FX Test Co', 'FX Test Co', 'SAR');";
            cmd.Parameters.AddWithValue("$id", companyId.ToString("D"));
            cmd.Parameters.AddWithValue("$code", CompanyCode);
            cmd.ExecuteNonQuery();
        }

        var structure = new AccountingStructureRepository("SQLite", connectionString);
        await structure.CreateFiscalYearAsync(companyId, 2026, Actor);

        foreach (var (code, name) in new[]
        {
            ("131", "Cash"), ("132", "Bank"), ("1201", "Receivable"), ("1203", "Inventory"),
            ("2201", "Payable"), ("301", "Sales"), ("302", "Sales Returns"), ("401", "COGS"),
            ("402", "Purchases"), ("403", "Purchase Returns")
        })
        {
            AccRepo.Save(null, new AccRepo.AccRec(
                string.Empty, code, null, name, name, "Postable", "Active", "Low", "Unspecified",
                false, false, false, false, false, false, 0m, string.Empty, 0m, 0m, 0m),
                renumberDescendants: false);
        }

        using (var connection = new SqliteConnection(connectionString))
        {
            connection.Open();
            void ExecLocal(string sql)
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
            ExecLocal($"INSERT INTO materials (material_id, company_id, code, name, is_stocked, avg_cost, is_deleted) VALUES (42, '{CompanyCode}', 'MAT-42', 'Item', 1, 40, 0);");
            ExecLocal($"INSERT INTO warehouses (warehouse_id, company_id, warehouse_code, warehouse_name_ar, warehouse_name_en, is_deleted) VALUES (1, '{CompanyCode}', 1, 'Main', 'Main', 0);");
            ExecLocal($"INSERT INTO material_stock_balances (company_id, warehouse_id, material_id, available_qty, average_cost, last_cost) VALUES ('{CompanyCode}', 1, 42, 1000, 40, 40);");
            // Canonical party for the ledger rows (migration 061 identity).
            ExecLocal($"INSERT INTO parties (id, company_id, kind, code, name_en, name_ar, is_active, is_deleted, ledger_id) VALUES ('{Guid.NewGuid():D}', '{CompanyCode}', 'C', 'CUST-1', 'Customer One', 'Customer One', 1, 0, 7);");
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
