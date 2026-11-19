using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using ShouTech.Application.Services;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;
using ShouTech.Infrastructure;
using Xunit;

namespace Kern.Tests;

/// <summary>
/// Chunk C coverage (ribbon completion): cheque CRUD + duplicate-number guard
/// + lifecycle transition legality (service over a real SQLite
/// ChequeRepository with an in-memory IJournalRepository double, following
/// the UnitTest1 InMemoryJournalRepository pattern), and manufacturing
/// save/post/cancel with repository-level stock-movement assertions.
/// SQLite schemas are created from the real migration files 033/034 plus the
/// minimal stock tables the manufacturing post path touches.
/// </summary>
public class ChequeMfgServiceTests
{
    private const string Company = "CHUNK-C";

    // ------------------------------------------------------------------
    // Cheques: save batch + journal map
    // ------------------------------------------------------------------
    [Fact]
    public async Task Cheque_SaveBatch_Incoming_PostsBalancedJournal_AndStoresRows()
    {
        using var db = new TempDb(withCheques: true, withStock: false);
        var journal = new FakeJournalRepository();
        var service = new ChequeService(new ChequeRepository("SQLite", db.ConnectionString), journal);
        var cheques = new List<Cheque>
        {
            NewCheque("1001", "Bank A", "Drawer One", "4101", 300m),
            NewCheque("1002", "Bank A", "Drawer Two", "4102", 200m)
        };

        var ids = await service.SaveBatchAsync(Company, cheques, "1101", "tester",
            new DateOnly(2026, 11, 17));

        Assert.Equal(2, ids.Count);
        // Step 8: ONE journal entry per cheque (never a shared batch entry).
        Assert.Equal(2, journal.DocumentPostings.Count);
        Assert.All(journal.DocumentPostings, p =>
        {
            Assert.Equal(p.Request.Lines.Sum(l => l.Debit), p.Request.Lines.Sum(l => l.Credit));
            Assert.Equal(ChequeSourceTypes.Issue, p.Request.SourceDocType);
        });
        Assert.Equal(300m, journal.DocumentPostings[0].Request.Lines.Sum(l => l.Debit));
        Assert.Equal(200m, journal.DocumentPostings[1].Request.Lines.Sum(l => l.Debit));
        var rows = await service.GetAllAsync(Company);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(ChequeStatuses.InPortfolio, r.Status));
        // Each row carries its OWN entry id, and the two ids differ.
        Assert.Equal(2, rows.Select(r => r.JournalEntryId).Distinct().Count());
        Assert.All(rows, r => Assert.Contains(journal.DocumentPostings, p => p.EntryId == r.JournalEntryId));
    }

    [Fact]
    public async Task Cheque_SaveBatch_RequiresJournal_AndValidatesInput()
    {
        using var db = new TempDb(withCheques: true, withStock: false);
        var noJournal = new ChequeService(new ChequeRepository("SQLite", db.ConnectionString), journal: null);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            noJournal.SaveBatchAsync(Company, new List<Cheque> { NewCheque("2001", "Bank A", "D", "4101", 10m) },
                "1101", "tester", new DateOnly(2026, 11, 17)));

        var service = new ChequeService(new ChequeRepository("SQLite", db.ConnectionString), new FakeJournalRepository());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveBatchAsync(Company, new List<Cheque> { NewCheque("2002", "Bank A", "D", "4101", 0m) },
                "1101", "tester", new DateOnly(2026, 11, 17)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveBatchAsync(Company, new List<Cheque> { NewCheque(" ", "Bank A", "D", "4101", 10m) },
                "1101", "tester", new DateOnly(2026, 11, 17)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveBatchAsync(Company, new List<Cheque> { NewCheque("2003", " ", "D", "4101", 10m) },
                "1101", "tester", new DateOnly(2026, 11, 17)));
    }

    [Fact]
    public async Task Cheque_DuplicateNumber_IsRejected_AndJournalIsCompensated()
    {
        using var db = new TempDb(withCheques: true, withStock: false);
        var journal = new FakeJournalRepository();
        var service = new ChequeService(new ChequeRepository("SQLite", db.ConnectionString), journal);
        await service.SaveBatchAsync(Company,
            new List<Cheque> { NewCheque("3001", "Bank A", "Same Drawer", "4101", 100m) },
            "1101", "tester", new DateOnly(2026, 11, 17));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveBatchAsync(Company,
                new List<Cheque> { NewCheque("3001", "Bank A", "Same Drawer", "4101", 100m) },
                "1101", "tester", new DateOnly(2026, 11, 17)));

        // The second (duplicate) batch posted its per-cheque journal
        // before the insert failed; the service must have reversed it
        // as compensation.
        Assert.Equal(2, journal.DocumentPostings.Count);
        Assert.Single(journal.Reversed);
        Assert.Equal(journal.DocumentPostings[1].EntryId, journal.Reversed[0]);
        Assert.Single(await service.GetAllAsync(Company));
    }

    // ------------------------------------------------------------------
    // Cheques: lifecycle state machine
    // ------------------------------------------------------------------
    [Fact]
    public async Task Cheque_Transitions_EnforceLegalOrder()
    {
        using var db = new TempDb(withCheques: true, withStock: false);
        var journal = new FakeJournalRepository();
        var service = new ChequeService(new ChequeRepository("SQLite", db.ConnectionString), journal);
        var ids = await service.SaveBatchAsync(Company,
            new List<Cheque> { NewCheque("4001", "Bank A", "Drawer", "4101", 250m) },
            "1101", "tester", new DateOnly(2026, 11, 17));
        var id = ids[0];
        var today = new DateOnly(2026, 11, 18);

        // Clear directly from InPortfolio is illegal (must Deposit first).
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ClearAsync(Company, id, today, "tester"));
        // Endorse is legal from InPortfolio, but then Deposit is not.
        Assert.True(ChequeService.CanTransition(ChequeDirections.In, ChequeStatuses.InPortfolio, ChequeStatuses.Endorsed));
        Assert.False(ChequeService.CanTransition(ChequeDirections.In, ChequeStatuses.Endorsed, ChequeStatuses.Deposited));
        Assert.False(ChequeService.CanTransition(ChequeDirections.Out, ChequeStatuses.Issued, ChequeStatuses.Bounced));

        await service.DepositAsync(Company, id, "1102", today, "tester");
        var deposited = await service.GetByIdAsync(Company, id);
        Assert.Equal(ChequeStatuses.Deposited, deposited!.Status);
        Assert.Equal("1102", deposited.DepositBankAccountCode);

        // Deposit twice is illegal.
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DepositAsync(Company, id, "1102", today, "tester"));

        await service.ClearAsync(Company, id, today, "tester");
        var cleared = await service.GetByIdAsync(Company, id);
        Assert.Equal(ChequeStatuses.Cleared, cleared!.Status);
        Assert.Equal(today, cleared.ClearanceDate);
        // Clear posted Dr Bank / Cr Portfolio as the cheque's OWN
        // entry through the journal double.
        Assert.Equal(2, journal.DocumentPostings.Count);
        var clearPosting = journal.DocumentPostings[1];
        Assert.Equal(ChequeSourceTypes.Clear, clearPosting.Request.SourceDocType);
        Assert.Equal(250m, clearPosting.Request.Lines.Sum(l => l.Debit));
        Assert.Equal(250m, clearPosting.Request.Lines.Sum(l => l.Credit));

        // Terminal: nothing may leave Cleared.
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.BounceAsync(Company, id, today, "late", 0m, "tester"));
    }

    [Fact]
    public async Task Cheque_Bounce_ReversesReceipt_AndRecordsFees()
    {
        using var db = new TempDb(withCheques: true, withStock: false);
        var journal = new FakeJournalRepository();
        var service = new ChequeService(new ChequeRepository("SQLite", db.ConnectionString), journal);
        var ids = await service.SaveBatchAsync(Company,
            new List<Cheque> { NewCheque("5001", "Bank B", "Drawer", "4101", 90m) },
            "1101", "tester", new DateOnly(2026, 11, 17));
        var receiptEntryId = journal.DocumentPostings[0].EntryId;

        await service.BounceAsync(Company, ids[0], new DateOnly(2026, 11, 20), "Insufficient funds", 15m, "tester");

        var bounced = await service.GetByIdAsync(Company, ids[0]);
        Assert.Equal(ChequeStatuses.Bounced, bounced!.Status);
        Assert.Equal(15m, bounced.FeesAmount);
        Assert.Equal("Insufficient funds", bounced.BounceReason);
        Assert.Contains(receiptEntryId, journal.Reversed);
    }

    [Fact]
    public async Task Cheque_Outgoing_IssuedThenCancel_ReversesPayment()
    {
        using var db = new TempDb(withCheques: true, withStock: false);
        var journal = new FakeJournalRepository();
        var service = new ChequeService(new ChequeRepository("SQLite", db.ConnectionString), journal);
        var outgoing = NewCheque("6001", "Bank C", "Supplier", "2101", 400m);
        outgoing.Direction = ChequeDirections.Out;
        var ids = await service.SaveBatchAsync(Company, new List<Cheque> { outgoing },
            "1101", "tester", new DateOnly(2026, 11, 17));

        var issued = await service.GetByIdAsync(Company, ids[0]);
        Assert.Equal(ChequeStatuses.Issued, issued!.Status);
        var entry = journal.DocumentPostings[0];
        Assert.Equal(400m, entry.Request.Lines.Sum(l => l.Debit));
        Assert.Equal(ChequeSourceTypes.Issue, entry.Request.SourceDocType);

        // Bounce does not exist for outgoing cheques.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.BounceAsync(Company, ids[0], new DateOnly(2026, 11, 18), "n/a", 0m, "tester"));

        await service.CancelOutgoingAsync(Company, ids[0], "tester");
        Assert.Equal(ChequeStatuses.Cancelled, (await service.GetByIdAsync(Company, ids[0]))!.Status);
        Assert.Contains(entry.EntryId, journal.Reversed);
    }

    [Fact]
    public async Task Cheque_RepositoryTransition_RevalidatesStoredStatus()
    {
        using var db = new TempDb(withCheques: true, withStock: false);
        var repo = new ChequeRepository("SQLite", db.ConnectionString);
        var cheque = NewCheque("7001", "Bank D", "Drawer", "4101", 50m);
        cheque.Status = ChequeStatuses.InPortfolio;
        await repo.InsertBatchAsync(Company, new List<Cheque> { cheque }, "tester");

        // A caller holding a stale expected status must be refused.
        cheque.Status = ChequeStatuses.Deposited;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repo.UpdateTransitionAsync(Company, cheque, expectedStatus: ChequeStatuses.Deposited, "tester"));
        // With the true stored status the transition succeeds.
        await repo.UpdateTransitionAsync(Company, cheque, expectedStatus: ChequeStatuses.InPortfolio, "tester");
        Assert.Equal(ChequeStatuses.Deposited, (await repo.GetByIdAsync(Company, cheque.Id))!.Status);
    }

    // ------------------------------------------------------------------
    // Manufacturing
    // ------------------------------------------------------------------
    [Fact]
    public async Task Mfg_SaveDraft_AssignsSequentialDocNumbers_AndComputesTotals()
    {
        using var db = new TempDb(withCheques: false, withStock: true);
        var service = new MfgOrderService(new MfgOrderRepository("SQLite", db.ConnectionString));

        var first = NewOrder(qty: 10m, rawQty: 25m, rawCost: 2m, overhead: 10m);
        var second = NewOrder(qty: 5m, rawQty: 10m, rawCost: 3m, overhead: 0m);
        await service.SaveDraftAsync(Company, first, "tester");
        await service.SaveDraftAsync(Company, second, "tester");

        Assert.Equal(1, first.DocNo);
        Assert.Equal(2, second.DocNo);
        Assert.Equal(60m, first.TotalCost);          // 25*2 + 10
        Assert.Equal(6m, first.UnitCost);            // 60 / 10
        var loaded = await service.GetByDocNoAsync(Company, 1);
        Assert.NotNull(loaded);
        Assert.Single(loaded!.Lines);
        Assert.Equal(50m, loaded.Lines[0].Amount);
        Assert.Equal(MfgOrderStatuses.Draft, loaded.Status);
    }

    [Fact]
    public async Task Mfg_Service_RejectsInvalidOrders()
    {
        using var db = new TempDb(withCheques: false, withStock: true);
        var service = new MfgOrderService(new MfgOrderRepository("SQLite", db.ConnectionString));

        var noLines = NewOrder(1m, 1m, 1m, 0m);
        noLines.Lines.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveDraftAsync(Company, noLines, "tester"));
        var badQty = NewOrder(0m, 1m, 1m, 0m);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveDraftAsync(Company, badQty, "tester"));
        var badLine = NewOrder(1m, 0m, 1m, 0m);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveDraftAsync(Company, badLine, "tester"));
    }

    [Fact]
    public async Task Mfg_Post_ConsumesRaw_ProducesFinished_AndComputesCosts()
    {
        using var db = new TempDb(withCheques: false, withStock: true);
        db.SeedMaterial(1, "RM-1");
        db.SeedMaterial(2, "FG-1");
        db.SeedWarehouse(1);
        db.SeedOpeningStock(materialId: 1, warehouseId: 1, qty: 100m, cost: 2m);
        var repo = new MfgOrderRepository("SQLite", db.ConnectionString);
        var service = new MfgOrderService(repo);
        var order = NewOrder(qty: 10m, rawQty: 25m, rawCost: 2m, overhead: 10m);
        await service.SaveDraftAsync(Company, order, "tester");

        await service.PostAsync(Company, order.Id, "tester");

        var posted = await service.GetByDocNoAsync(Company, order.DocNo);
        Assert.Equal(MfgOrderStatuses.Posted, posted!.Status);
        Assert.Equal(60m, posted.TotalCost);
        Assert.Equal(6m, posted.UnitCost);

        Assert.Equal(25m, db.ScalarDecimal("SELECT qty_out FROM material_stock_movements WHERE movement_type='MFG_CONSUME' AND material_id=1"));
        Assert.Equal(10m, db.ScalarDecimal("SELECT qty_in FROM material_stock_movements WHERE movement_type='MFG_PRODUCE' AND material_id=2"));
        Assert.Equal(6m, db.ScalarDecimal("SELECT unit_cost FROM material_stock_movements WHERE movement_type='MFG_PRODUCE' AND material_id=2"));
        Assert.Equal(75m, db.ScalarDecimal("SELECT available_qty FROM material_stock_balances WHERE material_id=1 AND warehouse_id=1"));
        Assert.Equal(10m, db.ScalarDecimal("SELECT available_qty FROM material_stock_balances WHERE material_id=2 AND warehouse_id=1"));

        // Double-post is refused by the repository status guard.
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PostAsync(Company, order.Id, "tester"));
    }

    [Fact]
    public async Task Mfg_Post_InsufficientStock_RollsBackEverything()
    {
        using var db = new TempDb(withCheques: false, withStock: true);
        db.SeedMaterial(1, "RM-1");
        db.SeedMaterial(2, "FG-1");
        db.SeedWarehouse(1);
        db.SeedOpeningStock(materialId: 1, warehouseId: 1, qty: 10m, cost: 2m);
        var service = new MfgOrderService(new MfgOrderRepository("SQLite", db.ConnectionString));
        var order = NewOrder(qty: 10m, rawQty: 25m, rawCost: 2m, overhead: 0m);
        await service.SaveDraftAsync(Company, order, "tester");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PostAsync(Company, order.Id, "tester"));

        var still = await service.GetByDocNoAsync(Company, order.DocNo);
        Assert.Equal(MfgOrderStatuses.Draft, still!.Status);
        Assert.Equal(0m, db.ScalarDecimal("SELECT COUNT(*) FROM material_stock_movements WHERE movement_type LIKE 'MFG%'"));
        Assert.Equal(10m, db.ScalarDecimal("SELECT available_qty FROM material_stock_balances WHERE material_id=1 AND warehouse_id=1"));
    }

    [Fact]
    public async Task Mfg_CancelPosted_ReversesMovements_AndRestoresBalances()
    {
        using var db = new TempDb(withCheques: false, withStock: true);
        db.SeedMaterial(1, "RM-1");
        db.SeedMaterial(2, "FG-1");
        db.SeedWarehouse(1);
        db.SeedOpeningStock(materialId: 1, warehouseId: 1, qty: 100m, cost: 2m);
        var service = new MfgOrderService(new MfgOrderRepository("SQLite", db.ConnectionString));
        var order = NewOrder(qty: 10m, rawQty: 25m, rawCost: 2m, overhead: 10m);
        await service.SaveDraftAsync(Company, order, "tester");
        await service.PostAsync(Company, order.Id, "tester");

        await service.CancelAsync(Company, order.Id, "tester");

        Assert.Equal(MfgOrderStatuses.Cancelled, (await service.GetByDocNoAsync(Company, order.DocNo))!.Status);
        Assert.Equal(100m, db.ScalarDecimal("SELECT available_qty FROM material_stock_balances WHERE material_id=1 AND warehouse_id=1"));
        Assert.Equal(0m, db.ScalarDecimal("SELECT available_qty FROM material_stock_balances WHERE material_id=2 AND warehouse_id=1"));
        Assert.Equal(25m, db.ScalarDecimal("SELECT qty_in FROM material_stock_movements WHERE movement_type='MFG_CONSUME_REV' AND material_id=1"));
        Assert.Equal(10m, db.ScalarDecimal("SELECT qty_out FROM material_stock_movements WHERE movement_type='MFG_PRODUCE_REV' AND material_id=2"));
    }

    // ------------------------------------------------------------------
    // NumWords (printed voucher wording)
    // ------------------------------------------------------------------
    [Fact]
    public void NumWords_FormatsAmountsInEnglish()
    {
        Assert.Equal("Zero Riyals Only", NumWordsCheck(0m, "SAR"));
        Assert.Equal("One Riyal Only", NumWordsCheck(1m, "SAR"));
        Assert.Equal("One Thousand Two Hundred Thirty-Four Riyals and Fifty Halalas Only", NumWordsCheck(1234.50m, "SAR"));
        Assert.Equal("Two Million Five Dollars and Five Cents Only", NumWordsCheck(2000005.05m, "USD"));
    }

    private static string NumWordsCheck(decimal amount, string currency)
        => ShouTech.App.Services.NumWords.ToWords(amount, currency);

    // ------------------------------------------------------------------
    // Builders / doubles
    // ------------------------------------------------------------------
    private static Cheque NewCheque(string no, string bank, string drawer, string partyAccount, decimal amount) => new()
    {
        Direction = ChequeDirections.In,
        ChequeNo = no,
        BankName = bank,
        DrawerName = drawer,
        PartyName = drawer,
        PartyAccountCode = partyAccount,
        Amount = amount,
        CurrencyCode = "SAR",
        ExchangeRate = 1m,
        IssueDate = new DateOnly(2026, 11, 17),
        DueDate = new DateOnly(2026, 12, 17)
    };

    private static MfgOrder NewOrder(decimal qty, decimal rawQty, decimal rawCost, decimal overhead) => new()
    {
        DocDate = new DateOnly(2026, 11, 17),
        ItemCode = "FG-1",
        ItemName = "Finished Good",
        Qty = qty,
        UnitCode = "PCS",
        WarehouseCode = "1",
        OverheadAmount = overhead,
        StandardUnitCost = 5m,
        Lines = new List<MfgOrderLine>
        {
            new() { LineKind = MfgLineKinds.Raw, ItemCode = "RM-1", ItemName = "Raw Material", Qty = rawQty, UnitCode = "KG", UnitCost = rawCost, StandardUnitCost = rawCost }
        }
    };

    /// <summary>In-memory journal double (UnitTest1 pattern): records drafts,
    /// posts and reversals; resolves company/account GUIDs deterministically
    /// is NOT its job — the real ChequeRepository does that against the
    /// companies / chart_of_accounts tables seeded by TempDb.</summary>
    private sealed class FakeJournalRepository : IJournalRepository
    {
        public List<JournalEntry> Posted { get; } = new();
        public List<Guid> Reversed { get; } = new();
        /// <summary>Step 8: cheque postings arrive as GlPosting document
        /// requests — one per cheque/event, each with its own entry id.</summary>
        public List<(GlDocumentRequest Request, Guid EntryId)> DocumentPostings { get; } = new();
        private readonly Dictionary<Guid, JournalEntry> _drafts = new();

        public Task<GlPostingResult> PostDocumentAsync(GlDocumentRequest request, string userId, CancellationToken cancellationToken = default)
        {
            var id = Guid.NewGuid();
            DocumentPostings.Add((request, id));
            return Task.FromResult(new GlPostingResult(id, "JV-" + request.SourceDocNo));
        }

        public Task<JournalEntrySnapshot?> FindByDocumentNumberAsync(Guid companyId, string documentNumber, CancellationToken cancellationToken = default)
            => Task.FromResult<JournalEntrySnapshot?>(null);

        public Task SaveDraftAsync(Guid companyId, JournalEntry entry, string userId, CancellationToken cancellationToken = default)
        {
            _drafts[entry.Id] = entry;
            return Task.CompletedTask;
        }

        public Task UpdateDraftAsync(Guid companyId, Guid journalEntryId, JournalEntry entry, string userId, CancellationToken cancellationToken = default)
        {
            _drafts[journalEntryId] = entry;
            return Task.CompletedTask;
        }

        public Task<System.Collections.Generic.IReadOnlyList<BudgetWarning>> PostAsync(Guid journalEntryId, string userId, CancellationToken cancellationToken = default)
        {
            if (_drafts.TryGetValue(journalEntryId, out var entry))
                Posted.Add(entry);
            return Task.FromResult<System.Collections.Generic.IReadOnlyList<BudgetWarning>>(Array.Empty<BudgetWarning>());
        }

        public Task<Guid> ReverseAsync(Guid journalEntryId, string userId, string? details = null, CancellationToken cancellationToken = default)
        {
            Reversed.Add(journalEntryId);
            return Task.FromResult(Guid.NewGuid());
        }
    }

    private sealed class TempDb : IDisposable
    {
        private readonly string _path;
        public string ConnectionString { get; }
        public Guid CompanyGuid { get; } = Guid.NewGuid();

        public TempDb(bool withCheques, bool withStock)
        {
            _path = Path.Combine(Path.GetTempPath(), $"shoutech-chunkc-{Guid.NewGuid():N}.sqlite3");
            ConnectionString = $"Data Source={_path};";
            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();
            Exec(connection, """
                CREATE TABLE companies (company_id TEXT PRIMARY KEY, company_code TEXT NOT NULL, is_active INTEGER NOT NULL DEFAULT 1, is_deleted INTEGER NOT NULL DEFAULT 0);
                CREATE TABLE chart_of_accounts (account_id TEXT PRIMARY KEY, company_id TEXT NOT NULL, account_code TEXT NOT NULL, account_name TEXT, account_type TEXT NOT NULL DEFAULT 'ASSET', is_active INTEGER NOT NULL DEFAULT 1, is_deleted INTEGER NOT NULL DEFAULT 0, is_postable INTEGER NOT NULL DEFAULT 1, cost_required INTEGER NOT NULL DEFAULT 0);
                """);
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO companies (company_id, company_code) VALUES ($id, $code);";
                cmd.Parameters.AddWithValue("$id", CompanyGuid.ToString("D"));
                cmd.Parameters.AddWithValue("$code", Company);
                cmd.ExecuteNonQuery();
            }
            foreach (var (code, name) in new[] { ("1101", "Cash Box"), ("1102", "Bank Account"), ("4101", "Customer A"), ("4102", "Customer B"), ("2101", "Supplier A") })
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "INSERT INTO chart_of_accounts (account_id, company_id, account_code, account_name) VALUES ($id, $company, $code, $name);";
                cmd.Parameters.AddWithValue("$id", DeterministicAccountGuid(code).ToString("D"));
                cmd.Parameters.AddWithValue("$company", CompanyGuid.ToString("D"));
                cmd.Parameters.AddWithValue("$code", code);
                cmd.Parameters.AddWithValue("$name", name);
                cmd.ExecuteNonQuery();
            }
            if (withCheques)
            {
                Exec(connection, ReadMigration("033_cheques.sql"));
                // Migration 063 columns (plain-SQL equivalent; the
                // migrator-directive file itself is exercised through
                // SchemaMigrator in ChequeBankRecStep8Tests).
                Exec(connection, "ALTER TABLE cheques ADD COLUMN batch_doc_no TEXT; ALTER TABLE cheques ADD COLUMN cancel_reason TEXT;");
            }
            if (withStock)
            {
                // Step 3: MFG completion/cancellation post + reverse real
                // GL entries, so the fixture needs the journal tables, an
                // open fiscal year, and the canonical inventory account.
                Exec(connection, """
                    CREATE TABLE fiscal_years (fiscal_year_id INTEGER PRIMARY KEY AUTOINCREMENT, company_id TEXT NOT NULL, fiscal_year INTEGER NOT NULL, database_name TEXT NOT NULL DEFAULT '', start_date TEXT NOT NULL, end_date TEXT NOT NULL, is_current INTEGER NOT NULL DEFAULT 1, is_closed INTEGER NOT NULL DEFAULT 0);
                    CREATE TABLE fiscal_periods (fiscal_period_id TEXT PRIMARY KEY, company_id TEXT NOT NULL, fiscal_year_id INTEGER NOT NULL, period_number INTEGER NOT NULL, period_name TEXT NOT NULL, start_date TEXT NOT NULL, end_date TEXT NOT NULL, status TEXT NOT NULL DEFAULT 'OPEN', closed_by TEXT, closed_at TEXT);
                    CREATE TABLE journal_entries (journal_entry_id TEXT PRIMARY KEY, company_id TEXT NOT NULL, entry_date TEXT NOT NULL, description TEXT NOT NULL, status INTEGER NOT NULL DEFAULT 0, created_by TEXT NOT NULL, created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP, posted_by TEXT, posted_at TEXT, reversed_by TEXT, reversed_at TEXT, reversal_of_entry_id TEXT, fiscal_period_id TEXT, reference_number TEXT, currency_code TEXT NOT NULL DEFAULT 'SAR', exchange_rate NUMERIC NOT NULL DEFAULT 1, document_number TEXT, source_doc_type TEXT, source_doc_id TEXT, source_doc_no TEXT);
                    CREATE TABLE journal_entry_lines (journal_entry_line_id INTEGER PRIMARY KEY AUTOINCREMENT, journal_entry_id TEXT NOT NULL, line_number INTEGER NOT NULL, account_id TEXT NOT NULL, debit NUMERIC NOT NULL DEFAULT 0, credit NUMERIC NOT NULL DEFAULT 0, cost_center TEXT, note TEXT);
                    CREATE TABLE journal_audit_events (journal_audit_event_id INTEGER PRIMARY KEY AUTOINCREMENT, journal_entry_id TEXT NOT NULL, event_type INTEGER NOT NULL, actor TEXT NOT NULL, event_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP, details TEXT);
                    CREATE TABLE cost_centers (id TEXT PRIMARY KEY, company_id TEXT NOT NULL, code TEXT NOT NULL, is_group INTEGER NOT NULL DEFAULT 0, is_active INTEGER NOT NULL DEFAULT 1, is_deleted INTEGER NOT NULL DEFAULT 0);
                    CREATE TABLE cost_center_allocations (id TEXT PRIMARY KEY, company_id TEXT NOT NULL, main_cost_center_id TEXT NOT NULL, is_active INTEGER NOT NULL DEFAULT 1, is_deleted INTEGER NOT NULL DEFAULT 0, valid_from TEXT NOT NULL);
                    CREATE TABLE cost_center_allocation_lines (id TEXT PRIMARY KEY, allocation_id TEXT NOT NULL, cost_center_id TEXT NOT NULL, percentage NUMERIC NOT NULL DEFAULT 0);
                    CREATE TABLE journal_line_cost_splits (id TEXT PRIMARY KEY, company_id TEXT NOT NULL, journal_entry_id TEXT NOT NULL, line_number INTEGER NOT NULL, cost_center_id TEXT NOT NULL, cost_center_code TEXT NOT NULL, percentage REAL NOT NULL, debit_amount NUMERIC NOT NULL DEFAULT 0, credit_amount NUMERIC NOT NULL DEFAULT 0, created_at TEXT NOT NULL DEFAULT (datetime('now')));
                    """);
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "INSERT INTO fiscal_years (company_id, fiscal_year, start_date, end_date) VALUES ($c, 2026, '2026-01-01', '2026-12-31'); SELECT last_insert_rowid();";
                    cmd.Parameters.AddWithValue("$c", CompanyGuid.ToString("D"));
                    var yearId = (long)cmd.ExecuteScalar()!;
                    cmd.Parameters.Clear();
                    cmd.CommandText = "INSERT INTO fiscal_periods (fiscal_period_id, company_id, fiscal_year_id, period_number, period_name, start_date, end_date, status) VALUES ($id, $c, $y, 11, '2026-11', '2026-11-01', '2026-11-30', 'OPEN');";
                    cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
                    cmd.Parameters.AddWithValue("$c", CompanyGuid.ToString("D"));
                    cmd.Parameters.AddWithValue("$y", yearId);
                    cmd.ExecuteNonQuery();
                    cmd.Parameters.Clear();
                    cmd.CommandText = "INSERT INTO chart_of_accounts (account_id, company_id, account_code, account_name) VALUES ($id, $company, '1203', 'Merchandise Inventory');";
                    cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
                    cmd.Parameters.AddWithValue("$company", CompanyGuid.ToString("D"));
                    cmd.ExecuteNonQuery();
                }
                // The MFG fixture uses the real inventory/RBAC schema rather than
                // a hand-written subset, so the valuation engine sees the same
                // columns and stock-ledger tables as the application.
                Exec(connection, """
                    CREATE TABLE roles (
                        role_id INTEGER PRIMARY KEY AUTOINCREMENT,
                        role_code TEXT NOT NULL UNIQUE,
                        is_active INTEGER NOT NULL DEFAULT 1
                    );
                    CREATE TABLE permissions (
                        permission_id INTEGER PRIMARY KEY AUTOINCREMENT,
                        permission_code TEXT NOT NULL UNIQUE,
                        permission_name TEXT NOT NULL,
                        group_code TEXT NOT NULL DEFAULT 'SYSTEM',
                        feature_code TEXT NOT NULL DEFAULT '',
                        operation_code TEXT NOT NULL DEFAULT '',
                        name_ar TEXT NOT NULL DEFAULT '',
                        name_en TEXT NOT NULL DEFAULT '',
                        description TEXT,
                        display_order INTEGER NOT NULL DEFAULT 0,
                        is_sensitive INTEGER NOT NULL DEFAULT 0,
                        is_active INTEGER NOT NULL DEFAULT 1
                    );
                    CREATE TABLE role_permissions (
                        role_id INTEGER NOT NULL,
                        permission_id INTEGER NOT NULL,
                        PRIMARY KEY (role_id, permission_id)
                    );
                    """);
                ExecMigration(connection, "013_inventory_schema.sql");
                ExecMigration(connection, "016_material_groups_warehouses.sql");
                ExecMigration(connection, "053_valuation_engine.sql");
                ExecMigration(connection, "054_batch_serial.sql");
                ExecMigration(connection, "034_manufacturing.sql");
                ExecMigration(connection, "058_manufacturing_bom_planning.sql");
            }
        }

        // The fake journal records entries by the account GUIDs the real
        // repository resolved; account codes map to GUIDs deterministically
        // so SaveBatchAsync resolution succeeds against the seeded COA.
        private static Guid DeterministicAccountGuid(string code)
        {
            // ChequeRepository.ResolveAccountIdAsync reads the GUID stored in
            // chart_of_accounts, so any stable GUID works.
            return Guid.Parse($"00000000-0000-0000-0000-{(Array.IndexOf(new[] { "1101", "1102", "4101", "4102", "2101" }, code) + 1):D12}");
        }

        public void SeedMaterial(long id, string code)
        {
            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "INSERT INTO materials (material_id, company_id, code, name_ar) VALUES ($id, $company, $code, $code);";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$company", Company);
            cmd.Parameters.AddWithValue("$code", code);
            cmd.ExecuteNonQuery();
        }

        public void SeedWarehouse(long id)
        {
            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "INSERT INTO warehouses (warehouse_id, company_id, warehouse_code) VALUES ($id, $company, $id);";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$company", Company);
            cmd.ExecuteNonQuery();
        }

        public void SeedOpeningStock(long materialId, long warehouseId, decimal qty, decimal cost)
        {
            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO material_stock_movements (company_id, warehouse_id, material_id, movement_date, movement_type, document_no, qty_in, qty_out, unit_cost, note)
                VALUES ($company, $wh, $mat, '2026-11-01', 'OPN', 'OPEN-1', $qty, 0, $cost, 'Opening stock');
                INSERT INTO material_stock_balances (company_id, warehouse_id, material_id, available_qty, average_cost, last_cost)
                VALUES ($company, $wh, $mat, $qty, $cost, $cost);
                """;
            cmd.Parameters.AddWithValue("$company", Company);
            cmd.Parameters.AddWithValue("$wh", warehouseId);
            cmd.Parameters.AddWithValue("$mat", materialId);
            cmd.Parameters.AddWithValue("$qty", qty);
            cmd.Parameters.AddWithValue("$cost", cost);
            cmd.ExecuteNonQuery();
        }

        public decimal ScalarDecimal(string sql)
        {
            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            var value = cmd.ExecuteScalar();
            return value is null or DBNull ? 0m : Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        }

        private static void Exec(SqliteConnection connection, string sql)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        private static void ExecMigration(SqliteConnection connection, string fileName)
        {
            var sql = ReadMigration(fileName);
            var directive = new Regex(
                @"(?m)^-- @if-column-missing ([A-Za-z0-9_]+)\.([A-Za-z0-9_]+)\s*\r?\n\s*(ALTER TABLE [^;]+;)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            sql = directive.Replace(sql, match =>
                HasColumn(connection, match.Groups[1].Value, match.Groups[2].Value)
                    ? string.Empty
                    : match.Groups[3].Value);
            Exec(connection, sql);
        }

        private static bool HasColumn(SqliteConnection connection, string table, string column)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info(\"{table}\");";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static string ReadMigration(string fileName)
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "dba", "mig")))
                root = root.Parent;
            Assert.NotNull(root);
            var path = Path.Combine(root!.FullName, "dba", "mig", "sqlite", fileName);
            Assert.True(File.Exists(path), $"Migration not found: {path}");
            return File.ReadAllText(path);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(_path)) File.Delete(_path);
            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                var sidecar = _path + suffix;
                if (File.Exists(sidecar)) File.Delete(sidecar);
            }
        }
    }
}
