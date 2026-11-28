using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ShouTech.App.Services;
using ShouTech.Application.Services;
using ShouTech.Domain;
using ShouTech.Domain.Entities;
using ShouTech.Infrastructure;
using Xunit;

namespace Kern.Tests;

/// <summary>
/// Fix Phase Step 8 — cheques + bank reconciliation (sweep
/// cheques-bankrec.md). Pins, against REAL migrated SQLite databases
/// (SchemaMigrator applies every migration including 063) with the
/// REAL ChequeService / JournalRepository (GlPosting) and
/// OperationsService / OperationsRepository:
///   * a batch posts ONE journal entry per cheque; bouncing one
///     cheque (with fees) reverses only its own entry — siblings' GL
///     and statuses are untouched and balances are exact (CHQ-02);
///   * outgoing issue + clear posts Dr Party / Cr Box at issue and
///     Dr Box / Cr Bank at clearance (CHQ-01 direction + CHQ-04);
///   * a HISTORICAL batch entry shared by two cheques is never
///     reversed for one cheque: a per-cheque compensating entry is
///     posted instead and the sibling stays exact;
///   * BankRec: a statement whose closing balance exceeds the matched
///     book movement by a known amount yields exactly that Difference
///     after matching, moves by each matched amount, refuses to close
///     while non-zero (ACC-5016), and closes at a legitimate zero
///     (BNK-01);
///   * migration 063 is idempotent on a double migrator run.
/// Execution requires Windows (net8.0-windows testhost); the same
/// scenarios are verified headlessly by
/// ~/workspace/research/fix-step8/step8_harness.py (see
/// docs/FIX_PHASE_STEP1.md).
/// </summary>
public class ChequeBankRecStep8Tests
{
    private const string Actor = "step8-test";
    private const string CompanyCode = "S81";
    private const string Portfolio = "1101";
    private const string Bank = "1102";
    private const string Party = "4101";
    private const string Supplier = "2101";

    [Fact]
    public async Task BatchOfThree_BounceOne_ReversesOnlyItsOwnEntry()
    {
        using var db = await SeededAsync();
        var service = db.Cheques();
        var cheques = new List<Cheque>
        {
            NewCheque("8001", 100m), NewCheque("8002", 200m), NewCheque("8003", 300m)
        };
        var ids = await service.SaveBatchAsync(CompanyCode, cheques, Portfolio, Actor, new DateOnly(2026, 11, 17));

        var rows = await service.GetAllAsync(CompanyCode);
        Assert.Equal(3, rows.Count);
        // Per-cheque entries: three DISTINCT journal ids, three live
        // CHQ-ISSUE entries in the GL.
        Assert.Equal(3, rows.Select(r => r.JournalEntryId).Distinct().Count());
        Assert.Equal(3L, db.ScalarLong(
            "SELECT COUNT(*) FROM journal_entries WHERE status = 1 AND source_doc_type = 'CHQ-ISSUE'"));
        Assert.Equal(600m, db.AccountNetDebit(Portfolio));
        Assert.Equal(-600m, db.AccountNetDebit(Party));

        await service.BounceAsync(CompanyCode, ids[1], new DateOnly(2026, 11, 20), "Insufficient funds", 10m, Actor);

        // Only cheque #2's entry was reversed; the siblings' entries
        // are still posted and their statuses are untouched.
        var after = await service.GetAllAsync(CompanyCode);
        Assert.Equal(ChequeStatuses.Bounced, after.Single(r => r.Id == ids[1]).Status);
        Assert.Equal(10m, after.Single(r => r.Id == ids[1]).FeesAmount);
        Assert.All(after.Where(r => r.Id != ids[1]), r => Assert.Equal(ChequeStatuses.InPortfolio, r.Status));
        Assert.Equal(2L, db.ScalarLong(
            "SELECT COUNT(*) FROM journal_entries WHERE status = 1 AND reversal_of_entry_id IS NULL AND source_doc_type = 'CHQ-ISSUE'"));
        Assert.Equal(1L, db.ScalarLong(
            "SELECT COUNT(*) FROM journal_entries WHERE status = 2 AND source_doc_type = 'CHQ-ISSUE'"));
        // The fee was POSTED (CHQ-07): Dr Party / Cr Portfolio 10.
        Assert.Equal(1L, db.ScalarLong(
            "SELECT COUNT(*) FROM journal_entries WHERE status = 1 AND source_doc_type = 'CHQ-FEE'"));
        // Exact balances: portfolio 600 - 200 (reversal) - 10 (fee
        // credit) = 390 Dr; party mirrors at 390 Cr.
        Assert.Equal(390m, db.AccountNetDebit(Portfolio));
        Assert.Equal(-390m, db.AccountNetDebit(Party));
    }

    [Fact]
    public async Task Outgoing_IssueThenClear_CreditsThePayingBank()
    {
        using var db = await SeededAsync();
        var service = db.Cheques();
        var outgoing = NewCheque("9001", 400m);
        outgoing.Direction = ChequeDirections.Out;
        outgoing.PartyAccountCode = Supplier;
        var ids = await service.SaveBatchAsync(CompanyCode, new List<Cheque> { outgoing }, Portfolio, Actor, new DateOnly(2026, 11, 17));

        // Issue: Dr Supplier / Cr Box.
        Assert.Equal(400m, db.AccountNetDebit(Supplier));
        Assert.Equal(-400m, db.AccountNetDebit(Portfolio));

        await service.ClearAsync(CompanyCode, ids[0], new DateOnly(2026, 11, 25), Actor, Bank);

        // Clearance (CHQ-04): Dr Box / Cr Bank — the bank is credited,
        // the box leg unwinds to zero.
        Assert.Equal(ChequeStatuses.Cleared, (await service.GetByIdAsync(CompanyCode, ids[0]))!.Status);
        Assert.Equal(1L, db.ScalarLong(
            "SELECT COUNT(*) FROM journal_entries WHERE status = 1 AND source_doc_type = 'CHQ-CLEAR'"));
        Assert.Equal(0m, db.AccountNetDebit(Portfolio));
        Assert.Equal(-400m, db.AccountNetDebit(Bank));
        Assert.Equal(400m, db.AccountNetDebit(Supplier));
    }

    [Fact]
    public async Task LegacySharedBatchEntry_BounceOne_CompensatesOnlyThatCheque()
    {
        using var db = await SeededAsync();
        var journal = db.Journal();
        var companyGuid = await db.ChequeRepo().ResolveCompanyGuidAsync(CompanyCode);
        // Simulate pre-Step-8 data: ONE posted entry shared by two
        // cheques (200 + 300 = Dr Portfolio 500 / Cr Party 500).
        var shared = await journal.PostDocumentAsync(new GlDocumentRequest(
            companyGuid, new DateOnly(2026, 11, 10), "Legacy cheque batch",
            "LEGACY-BATCH", "legacy-1", "LEGACY-1", "SAR", 1m,
            new[]
            {
                new GlLineRequest(Portfolio, 500m, 0m),
                new GlLineRequest(Party, 0m, 500m)
            }), Actor);
        var first = NewCheque("7001", 200m);
        var second = NewCheque("7002", 300m);
        first.JournalEntryId = shared.JournalEntryId;
        second.JournalEntryId = shared.JournalEntryId;
        first.PortfolioAccountCode = Portfolio;
        second.PortfolioAccountCode = Portfolio;
        first.Status = ChequeStatuses.InPortfolio;
        second.Status = ChequeStatuses.InPortfolio;
        await db.ChequeRepo().InsertBatchAsync(CompanyCode, new List<Cheque> { first, second }, Actor);

        var service = db.Cheques();
        await service.BounceAsync(CompanyCode, first.Id, new DateOnly(2026, 11, 21), "Bounced", 0m, Actor);

        // The shared entry is STILL POSTED (never reversed), a
        // per-cheque CHQ-BOUNCE compensation of exactly 200 exists,
        // and the sibling's 300 remains fully in the GL.
        Assert.Equal(0L, db.ScalarLong(
            $"SELECT COUNT(*) FROM journal_entries WHERE status = 2 AND journal_entry_id = '{shared.JournalEntryId:D}'"));
        Assert.Equal(1L, db.ScalarLong(
            "SELECT COUNT(*) FROM journal_entries WHERE status = 1 AND source_doc_type = 'CHQ-BOUNCE'"));
        Assert.Equal(ChequeStatuses.InPortfolio, (await service.GetByIdAsync(CompanyCode, second.Id))!.Status);
        Assert.Equal(300m, db.AccountNetDebit(Portfolio));
        Assert.Equal(-300m, db.AccountNetDebit(Party));
    }

    [Fact]
    public async Task BankRec_StatementDifference_IsReal_AndClosesOnlyAtZero()
    {
        using var db = await SeededAsync();
        var journal = db.Journal();
        var companyGuid = await db.ChequeRepo().ResolveCompanyGuidAsync(CompanyCode);
        // Book side: two receipts into the bank account, 600 + 400.
        foreach (var amount in new[] { 600m, 400m })
        {
            await journal.PostDocumentAsync(new GlDocumentRequest(
                companyGuid, new DateOnly(2026, 11, 15), $"Receipt {amount}",
                "TEST-RCPT", Guid.NewGuid().ToString("D"), $"RCPT-{amount}", "SAR", 1m,
                new[]
                {
                    new GlLineRequest(Bank, amount, 0m),
                    new GlLineRequest(Party, 0m, amount)
                }), Actor);
        }

        var ops = db.Operations();
        var statement = new BankStatement
        {
            AccountCode = Bank,
            StatementDate = new DateOnly(2026, 11, 30),
            StatementRef = "STMT-NOV",
            OpeningBalance = 0m,
            ClosingBalance = 1050m // the bank shows 50 the books lack
        };
        await ops.SaveBankStatementAsync(CompanyCode, statement, Actor);
        var repository = new OperationsRepository("SQLite", db.ConnectionString);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.CloseBankStatementAsync(CompanyCode, statement.Id, Actor));
        foreach (var amount in new[] { 600m, 400m, 50m })
        {
            await ops.AddBankStatementLineAsync(CompanyCode, new BankStatementLine
            {
                StatementId = statement.Id, AccountCode = Bank,
                LineDate = new DateOnly(2026, 11, 15), Amount = amount,
                Description = $"Statement movement {amount}"
            }, Actor);
        }

        var bookLines = await ops.GetBankJournalLinesAsync(CompanyCode, Bank);
        Assert.Equal(2, bookLines.Count);
        var statementLines = await ops.GetBankStatementLinesAsync(CompanyCode, statement.Id);

        // Nothing matched yet: the whole statement movement is open.
        var totals = OperationsService.ComputeTotals(bookLines, statement, statementLines);
        Assert.Equal(1050m, totals.Difference);
        Assert.Equal(1000m, totals.BookBalance);
        Assert.Equal(1050m, totals.UnmatchedStatementTotal);

        // Matching moves the difference by the matched amounts.
        await MatchAsync(ops, statement, statementLines, bookLines, 600m);
        statementLines = await ops.GetBankStatementLinesAsync(CompanyCode, statement.Id);
        bookLines = await ops.GetBankJournalLinesAsync(CompanyCode, Bank);
        totals = OperationsService.ComputeTotals(bookLines, statement, statementLines);
        Assert.Equal(450m, totals.Difference);

        await MatchAsync(ops, statement, statementLines, bookLines, 400m);
        statementLines = await ops.GetBankStatementLinesAsync(CompanyCode, statement.Id);
        bookLines = await ops.GetBankJournalLinesAsync(CompanyCode, Bank);
        totals = OperationsService.ComputeTotals(bookLines, statement, statementLines);
        // Every book line is matched, yet the difference is exactly
        // the known 50 discrepancy — non-zero, and the statement
        // refuses to close (ACC-5016).
        Assert.Equal(50m, totals.Difference);
        var closeError = await Assert.ThrowsAsync<AppError>(() =>
            ops.CloseBankStatementAsync(CompanyCode, statement.Id, Actor));
        Assert.Equal(ErrorCodes.BankStatementNotReconciled, closeError.Code);

        // Post the missing book entry (the bank's 50), match it, and
        // the difference reaches zero LEGITIMATELY; close succeeds.
        await journal.PostDocumentAsync(new GlDocumentRequest(
            companyGuid, new DateOnly(2026, 11, 16), "Bank interest",
            "TEST-RCPT", Guid.NewGuid().ToString("D"), "RCPT-50", "SAR", 1m,
            new[]
            {
                new GlLineRequest(Bank, 50m, 0m),
                new GlLineRequest(Party, 0m, 50m)
            }), Actor);
        statementLines = await ops.GetBankStatementLinesAsync(CompanyCode, statement.Id);
        bookLines = await ops.GetBankJournalLinesAsync(CompanyCode, Bank);
        await MatchAsync(ops, statement, statementLines, bookLines, 50m);
        statementLines = await ops.GetBankStatementLinesAsync(CompanyCode, statement.Id);
        bookLines = await ops.GetBankJournalLinesAsync(CompanyCode, Bank);
        totals = OperationsService.ComputeTotals(bookLines, statement, statementLines);
        Assert.Equal(0m, totals.Difference);
        Assert.Equal(0m, totals.UnmatchedStatementTotal);

        await ops.CloseBankStatementAsync(CompanyCode, statement.Id, Actor);
        var closed = (await ops.GetBankStatementAsync(CompanyCode, statement.Id))!;
        Assert.Equal(BankStatementStatuses.Closed, closed.Status);

        // Closed statements are immutable: the ordinary save route must
        // not reopen them or rewrite their bank-reported closing balance.
        var attemptedEdit = new BankStatement
        {
            Id = closed.Id,
            AccountCode = closed.AccountCode,
            StatementDate = closed.StatementDate,
            StatementRef = closed.StatementRef,
            OpeningBalance = closed.OpeningBalance,
            ClosingBalance = 9999m,
            Status = BankStatementStatuses.Open
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ops.SaveBankStatementAsync(CompanyCode, attemptedEdit, Actor));
        Assert.Equal(1050m, (await ops.GetBankStatementAsync(CompanyCode, statement.Id))!.ClosingBalance);

        var attemptedClosedInsert = new BankStatement
        {
            AccountCode = Bank,
            StatementDate = new DateOnly(2026, 12, 31),
            ClosingBalance = 1m,
            Status = BankStatementStatuses.Closed
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ops.SaveBankStatementAsync(CompanyCode, attemptedClosedInsert, Actor));
    }

    [Fact]
    public async Task Migration063_DoubleRun_IsIdempotent()
    {
        var root = Path.Combine(Path.GetTempPath(), "step8-mig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var dbPath = Path.Combine(root, "mig.db");
            Assert.True((await SchemaMigrator.ApplyAllAsync("SQLite", dbPath)).Success);
            Assert.True((await SchemaMigrator.ApplyAllAsync("SQLite", dbPath)).Success);
            using var connection = new SqliteConnection($"Data Source={dbPath};");
            connection.Open();
            long Scalar(string sql)
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = sql;
                return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
            Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='bank_statements'"));
            Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='bank_statement_lines'"));
            Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM pragma_table_info('cheques') WHERE name='batch_doc_no'"));
            Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM pragma_table_info('cheques') WHERE name='cancel_reason'"));
            Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM pragma_table_info('bank_reconciliation_marks') WHERE name='statement_line_id'"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    // ------------------------------------------------------------------

    private static async Task MatchAsync(OperationsService ops, BankStatement statement,
        IReadOnlyList<BankStatementLine> statementLines, IReadOnlyList<BankJournalLineRow> bookLines, decimal amount)
    {
        var sl = statementLines.Single(l => l.Amount == amount && !l.IsMatched);
        var bl = bookLines.Single(l => l.Amount == amount && !l.IsCleared);
        await ops.MatchBankLineAsync(CompanyCode, sl, bl, statement.Id, Actor);
    }

    private static Cheque NewCheque(string no, decimal amount) => new()
    {
        Direction = ChequeDirections.In,
        ChequeNo = no,
        BankName = "Bank A",
        DrawerName = "Drawer",
        PartyName = "Drawer",
        PartyAccountCode = Party,
        Amount = amount,
        CurrencyCode = "SAR",
        ExchangeRate = 1m,
        IssueDate = new DateOnly(2026, 11, 17),
        DueDate = new DateOnly(2026, 12, 17)
    };

    private static async Task<TestDb> SeededAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "step8-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var dbPath = Path.Combine(root, "step8.db");
        var companyId = Guid.NewGuid();
        var connectionString = $"Data Source={dbPath};";
        Assert.True((await SchemaMigrator.ApplyAllAsync("SQLite", dbPath)).Success);

        using (var connection = new SqliteConnection(connectionString))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "INSERT INTO companies (company_id, company_code, company_name_ar, company_name_en, currency_code) VALUES ($id, $code, 'Step8 Co', 'Step8 Co', 'SAR');";
            cmd.Parameters.AddWithValue("$id", companyId.ToString("D"));
            cmd.Parameters.AddWithValue("$code", CompanyCode);
            cmd.ExecuteNonQuery();
            foreach (var code in new[] { Portfolio, Bank, Party, Supplier })
            {
                cmd.Parameters.Clear();
                cmd.CommandText = "INSERT INTO chart_of_accounts (account_id, company_id, account_code, account_name, account_type, is_active, is_deleted, is_postable, cost_required) VALUES ($id, $company, $code, $code, 'ASSET', 1, 0, 1, 0);";
                cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
                cmd.Parameters.AddWithValue("$company", companyId.ToString("D"));
                cmd.Parameters.AddWithValue("$code", code);
                cmd.ExecuteNonQuery();
            }
        }

        var structure = new AccountingStructureRepository("SQLite", connectionString);
        await structure.CreateFiscalYearAsync(companyId, 2026, Actor);
        return new TestDb { Root = root, DbPath = dbPath, CompanyId = companyId, ConnectionString = connectionString };
    }

    private sealed class TestDb : IDisposable
    {
        public required string Root { get; init; }
        public required string DbPath { get; init; }
        public required Guid CompanyId { get; init; }
        public required string ConnectionString { get; init; }

        public JournalRepository Journal() => new("SQLite", ConnectionString);
        public ChequeRepository ChequeRepo() => new("SQLite", ConnectionString);
        public ChequeService Cheques() => new(ChequeRepo(), Journal());
        public OperationsService Operations() => new(new OperationsRepository("SQLite", ConnectionString));

        public long ScalarLong(string sql)
        {
            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        /// <summary>Net debit of an account over every entry that was
        /// ever posted (status 1 = Posted, 2 = Reversed): a reversed
        /// original and its posted reversal are both included, so the
        /// pair nets to exactly zero — the economic balance.</summary>
        public decimal AccountNetDebit(string accountCode)
        {
            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT COALESCE(SUM(l.debit - l.credit), 0)
                FROM journal_entry_lines l
                JOIN journal_entries e ON e.journal_entry_id = l.journal_entry_id
                JOIN chart_of_accounts a ON a.account_id = l.account_id
                WHERE e.status IN (1, 2) AND a.account_code = $code;
                """;
            cmd.Parameters.AddWithValue("$code", accountCode);
            return Convert.ToDecimal(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(Root, recursive: true);
        }
    }
}
