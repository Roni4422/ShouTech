using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.Application.Services;
using ShouTech.Domain.Entities;
using ShouTech.Infrastructure;
using Xunit;

namespace Kern.Tests;

/// <summary>
/// Chunk F2 operations coverage against a REAL OperationsRepository on a
/// temp SQLite database (schema mirrors migrations 035/037 + the core
/// journal/document tables): bank-rec mark/unmark round-trip, POS closing
/// save + difference math, support ticket create/list, and compress
/// counting/purge on seeded soft-deleted drafts.
/// </summary>
public class OperationsServiceTests : IDisposable
{
    private readonly string _path;
    private readonly OperationsService _service;

    public OperationsServiceTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"shoutech-ops-{Guid.NewGuid():N}.db");
        var repo = new OperationsRepository("SQLite", $"Data Source={_path}");
        _service = new OperationsService(repo);
        CreateSchema();
        SeedJournal();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_path))
            File.Delete(_path);
    }

    [Fact]
    public async Task Maintenance_PostgreSql_ChecksAndVacuumAnalyze()
    {
        var connectionString = Environment.GetEnvironmentVariable("SHOUTECH_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
            return; // The PostgreSQL CI job supplies a dedicated migrated database.

        var repository = new OperationsRepository("PostgreSQL", connectionString);
        var service = new OperationsService(repository);

        var summary = await service.GetIntegritySummaryAsync();
        Assert.Contains("PostgreSQL integrity_check: ok", summary);

        var schema = await service.GetSchemaVersionAsync();
        Assert.True(schema.AppliedCount > 0);

        var audit = await service.GetTrialBalanceIntegrityAsync("C1");
        Assert.True(audit.IsClean);

        await service.VacuumAnalyzeAsync();
    }

    [Fact]
    public async Task Maintenance_IntegrityCheck_ReportsCleanSqliteDatabase()
    {
        var summary = await _service.GetIntegritySummaryAsync();

        Assert.Contains("SQLite integrity_check: ok", summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("foreign_key_violations: 0", summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Maintenance_IntegrityCheck_DetectsSqliteForeignKeyViolations()
    {
        await using (var connection = new SqliteConnection($"Data Source={_path}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA foreign_keys = OFF;
                CREATE TABLE maintenance_fk_parent (id INTEGER PRIMARY KEY);
                CREATE TABLE maintenance_fk_child (parent_id INTEGER REFERENCES maintenance_fk_parent(id));
                INSERT INTO maintenance_fk_child (parent_id) VALUES (42);
                """;
            await command.ExecuteNonQueryAsync();
        }

        var summary = await _service.GetIntegritySummaryAsync();

        Assert.Contains("SQLite integrity_check: ok", summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("foreign_key_violations: 1", summary, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------ Bank rec

    [Fact]
    public async Task BankRec_MarkUnmark_RoundTrip()
    {
        var lines = await _service.GetBankJournalLinesAsync("C1", "1110");
        Assert.Single(lines);
        var line = lines[0];
        Assert.False(line.IsCleared);
        Assert.Equal(500m, line.Amount);

        var totalsBefore = OperationsService.ComputeTotals(lines, null);
        Assert.Equal(500m, totalsBefore.BookBalance);
        Assert.Equal(0m, totalsBefore.ClearedTotal);
        Assert.Equal(500m, totalsBefore.UnclearedTotal);

        await _service.SaveBankMarksAsync("C1", new[]
        {
            new BankReconciliationMark
            {
                JournalEntryId = line.EntryId,
                JournalLineKey = line.LineKey,
                AccountCode = "1110",
                StatementDate = new DateOnly(2026, 1, 31),
                StatementRef = "STMT-1",
                ClearedAmount = line.Amount
            }
        }, "u1");

        var after = await _service.GetBankJournalLinesAsync("C1", "1110");
        Assert.Single(after);
        Assert.True(after[0].IsCleared);
        Assert.Equal("STMT-1", after[0].StatementRef);
        var totalsAfter = OperationsService.ComputeTotals(after, null);
        Assert.Equal(500m, totalsAfter.ClearedTotal);
        Assert.Equal(0m, totalsAfter.UnclearedTotal);
        // NOTE (Step 8): the old assertion `Difference == 0` here was
        // the BNK-01 tautology (Book - Cleared - Uncleared is zero for
        // EVERY dataset). Difference is now computed against a real
        // bank statement and is asserted in ChequeBankRecStep8Tests.

        await _service.UnmarkBankLinesAsync("C1", new[] { line.LineKey }, "u1");
        var unmarked = await _service.GetBankJournalLinesAsync("C1", "1110");
        Assert.False(unmarked[0].IsCleared);
    }

    [Fact]
    public async Task BankRec_OtherCompanySeesNoLines()
    {
        var lines = await _service.GetBankJournalLinesAsync("OTHER", "1110");
        Assert.Empty(lines);
    }

    // ------------------------------------------------------------ POS closing

    [Fact]
    public void PosClosing_DifferenceMath()
    {
        Assert.Equal(1050m, OperationsService.ComputeExpected(50m, new PosSalesSummary(3, 1000m)));
        Assert.Equal(-20m, OperationsService.ComputeDifference(1030m, 1050m));
        Assert.Equal(0m, OperationsService.ComputeDifference(1050m, 1050m));
    }

    [Fact]
    public async Task PosClosing_Save_PersistsComputedDifference()
    {
        var closing = new PosCashClosing
        {
            TerminalCode = "POS-1",
            BusinessDate = new DateOnly(2026, 2, 1),
            OpeningAmount = 100m,
            ExpectedAmount = 900m,
            CountedAmount = 875m,
            Notes = "End of day"
        };
        var id = await _service.SavePosClosingAsync("C1", closing, "u1");

        Assert.Equal(id, closing.Id);
        Assert.Equal(-25m, closing.Difference); // service recomputes, never trusts the caller
        var rows = await _service.GetPosClosingsAsync("C1");
        var saved = Assert.Single(rows);
        Assert.Equal("POS-1", saved.TerminalCode);
        Assert.Equal(-25m, saved.Difference);
        Assert.Equal(900m, saved.ExpectedAmount);
    }

    [Fact]
    public async Task PosClosing_Save_RejectsNegativeAmounts()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.SavePosClosingAsync("C1", new PosCashClosing { TerminalCode = "T", CountedAmount = -1m }, "u1"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.SavePosClosingAsync("C1", new PosCashClosing { TerminalCode = " " }, "u1"));
    }

    // ------------------------------------------------------------ Tickets

    [Fact]
    public async Task SupportTicket_CreateList_AssignsSequentialNumbers()
    {
        var first = await _service.CreateTicketAsync("C1", new SupportTicket
        {
            Kind = SupportTicketKinds.Support, Subject = "Printer broken", Body = "POS printer does not print."
        }, "u1");
        var second = await _service.CreateTicketAsync("C1", new SupportTicket
        {
            Kind = SupportTicketKinds.Suggestion, Subject = "Dark mode", Body = "Please add a dark theme.", ContactEmail = "a@b.c"
        }, "u1");

        Assert.Equal(1, first.TicketNo);
        Assert.Equal(2, second.TicketNo);
        var rows = await _service.GetTicketsAsync("C1");
        Assert.Equal(2, rows.Count);
        Assert.Equal(2, rows[0].TicketNo); // newest first
        Assert.Equal("Open", rows[0].Status);
    }

    [Fact]
    public async Task SupportTicket_Create_ValidatesInput()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.CreateTicketAsync("C1", new SupportTicket { Kind = "BOGUS", Subject = "x", Body = "y" }, "u1"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.CreateTicketAsync("C1", new SupportTicket { Kind = SupportTicketKinds.Support, Subject = " ", Body = "y" }, "u1"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.CreateTicketAsync("C1", new SupportTicket { Kind = SupportTicketKinds.Support, Subject = "x", Body = "y", ContactEmail = "no-at-sign" }, "u1"));
    }

    // ------------------------------------------------------------ Compress

    [Fact]
    public async Task Compress_CountsAndPurges_OnlySoftDeletedDrafts()
    {
        SeedDocuments();
        var counts = await _service.GetCompressCountsAsync("C1");
        Assert.Equal(1, counts.SalesDrafts);
        Assert.Equal(1, counts.PurchaseDrafts);
        Assert.Equal(2, counts.Total);

        var deleted = await _service.CompressDeletedDraftsAsync("C1", "u1");
        Assert.Equal(2, deleted);

        var after = await _service.GetCompressCountsAsync("C1");
        Assert.Equal(0, after.Total);

        // The posted document and the live draft must survive untouched.
        await using var connection = new SqliteConnection($"Data Source={_path}");
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sales_documents WHERE company_id = 'C1'";
        Assert.Equal(2L, Convert.ToInt64(await cmd.ExecuteScalarAsync()));
        cmd.CommandText = "SELECT COUNT(*) FROM security_audit_log WHERE event_type = 'INVOICE_COMPRESS'";
        Assert.Equal(1L, Convert.ToInt64(await cmd.ExecuteScalarAsync()));
    }

    // ------------------------------------------------------------ Seeds

    private void Exec(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={_path}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private void CreateSchema()
    {
        Exec("""
            CREATE TABLE chart_of_accounts (
                account_id TEXT PRIMARY KEY, company_id TEXT NOT NULL, account_code TEXT NOT NULL,
                account_name TEXT NOT NULL, account_type TEXT NOT NULL, is_postable INTEGER NOT NULL DEFAULT 1,
                is_active INTEGER NOT NULL DEFAULT 1, is_deleted INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE journal_entries (
                journal_entry_id TEXT PRIMARY KEY, company_id TEXT NOT NULL, entry_date TEXT NOT NULL,
                description TEXT NOT NULL, status INTEGER NOT NULL DEFAULT 0, document_number TEXT,
                created_by TEXT NOT NULL DEFAULT 'u1');
            CREATE TABLE journal_entry_lines (
                journal_entry_line_id INTEGER PRIMARY KEY AUTOINCREMENT, journal_entry_id TEXT NOT NULL,
                line_number INTEGER NOT NULL, account_id TEXT NOT NULL,
                debit NUMERIC NOT NULL DEFAULT 0, credit NUMERIC NOT NULL DEFAULT 0, note TEXT);
            CREATE TABLE bank_reconciliation_marks (
                id TEXT PRIMARY KEY, company_id TEXT NOT NULL, journal_entry_id TEXT NOT NULL,
                journal_line_key TEXT NOT NULL, account_code TEXT NOT NULL, statement_date TEXT NOT NULL,
                statement_ref TEXT, cleared_amount REAL NOT NULL DEFAULT 0, is_cleared INTEGER NOT NULL DEFAULT 1,
                created_by TEXT, created_at TEXT NOT NULL DEFAULT (datetime('now')), updated_by TEXT, updated_at TEXT,
                UNIQUE (company_id, journal_line_key));
            CREATE TABLE pos_cash_closings (
                id TEXT PRIMARY KEY, company_id TEXT NOT NULL, terminal_code TEXT NOT NULL, business_date TEXT NOT NULL,
                opened_by TEXT, closed_by TEXT, opening_amount REAL NOT NULL DEFAULT 0, expected_amount REAL NOT NULL DEFAULT 0,
                counted_amount REAL NOT NULL DEFAULT 0, difference REAL NOT NULL DEFAULT 0, status TEXT NOT NULL DEFAULT 'Closed',
                notes TEXT, created_by TEXT, created_at TEXT NOT NULL DEFAULT (datetime('now')), updated_by TEXT, updated_at TEXT);
            CREATE TABLE support_tickets (
                id TEXT PRIMARY KEY, company_id TEXT NOT NULL, ticket_no INTEGER NOT NULL,
                kind TEXT NOT NULL CHECK (kind IN ('SUPPORT','SUGGESTION')), subject TEXT NOT NULL, body TEXT NOT NULL,
                contact_email TEXT, status TEXT NOT NULL DEFAULT 'Open',
                created_by TEXT, created_at TEXT NOT NULL DEFAULT (datetime('now')), updated_by TEXT, updated_at TEXT,
                UNIQUE (company_id, ticket_no));
            CREATE TABLE sales_documents (
                doc_id INTEGER PRIMARY KEY AUTOINCREMENT, company_id TEXT NOT NULL, doc_kind TEXT NOT NULL DEFAULT 'SAL',
                doc_no TEXT NOT NULL, doc_date TEXT NOT NULL, subtotal NUMERIC NOT NULL DEFAULT 0,
                discount_amount NUMERIC NOT NULL DEFAULT 0, tax_amount NUMERIC NOT NULL DEFAULT 0,
                grand_total NUMERIC NOT NULL DEFAULT 0, status TEXT NOT NULL DEFAULT 'Draft',
                is_deleted INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE sales_document_lines (
                line_id INTEGER PRIMARY KEY AUTOINCREMENT, doc_id INTEGER NOT NULL, line_no INTEGER NOT NULL,
                description TEXT NOT NULL, qty NUMERIC NOT NULL DEFAULT 0, unit_price NUMERIC NOT NULL DEFAULT 0,
                discount_pct NUMERIC NOT NULL DEFAULT 0, tax_rate NUMERIC NOT NULL DEFAULT 0,
                line_subtotal NUMERIC NOT NULL DEFAULT 0, line_tax NUMERIC NOT NULL DEFAULT 0, line_total NUMERIC NOT NULL DEFAULT 0);
            CREATE TABLE purchase_documents (
                doc_id INTEGER PRIMARY KEY AUTOINCREMENT, company_id TEXT NOT NULL, doc_kind TEXT NOT NULL DEFAULT 'PUR',
                doc_no TEXT NOT NULL, doc_date TEXT NOT NULL, subtotal NUMERIC NOT NULL DEFAULT 0,
                discount_amount NUMERIC NOT NULL DEFAULT 0, tax_amount NUMERIC NOT NULL DEFAULT 0,
                grand_total NUMERIC NOT NULL DEFAULT 0, status TEXT NOT NULL DEFAULT 'Draft',
                is_deleted INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE purchase_document_lines (
                line_id INTEGER PRIMARY KEY AUTOINCREMENT, doc_id INTEGER NOT NULL, line_no INTEGER NOT NULL,
                description TEXT NOT NULL, qty NUMERIC NOT NULL DEFAULT 0, unit_price NUMERIC NOT NULL DEFAULT 0,
                discount_pct NUMERIC NOT NULL DEFAULT 0, tax_rate NUMERIC NOT NULL DEFAULT 0,
                line_subtotal NUMERIC NOT NULL DEFAULT 0, line_tax NUMERIC NOT NULL DEFAULT 0, line_total NUMERIC NOT NULL DEFAULT 0);
            CREATE TABLE security_audit_log (
                security_audit_id INTEGER PRIMARY KEY AUTOINCREMENT, company_id TEXT, user_id TEXT,
                username TEXT NOT NULL, event_type TEXT NOT NULL, details TEXT,
                occurred_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP);
            """);
    }

    private void SeedJournal()
    {
        Exec("""
            INSERT INTO chart_of_accounts (account_id, company_id, account_code, account_name, account_type)
            VALUES ('ACC-BANK', 'C1', '1110', 'Bank - Main', 'ASSET');
            INSERT INTO journal_entries (journal_entry_id, company_id, entry_date, description, status, document_number)
            VALUES ('JE-1', 'C1', '2026-01-15', 'Customer receipt', 1, 'JV-1');
            INSERT INTO journal_entry_lines (journal_entry_id, line_number, account_id, debit, credit)
            VALUES ('JE-1', 1, 'ACC-BANK', 500, 0);
            INSERT INTO journal_entry_lines (journal_entry_id, line_number, account_id, debit, credit)
            VALUES ('JE-1', 2, 'ACC-OTHER', 0, 500);
            """);
    }

    private void SeedDocuments()
    {
        Exec("""
            INSERT INTO sales_documents (company_id, doc_no, doc_date, status, is_deleted) VALUES ('C1', 'S-DEL', '2026-01-01', 'Draft', 1);
            INSERT INTO sales_documents (company_id, doc_no, doc_date, status, is_deleted) VALUES ('C1', 'S-LIVE', '2026-01-02', 'Draft', 0);
            INSERT INTO sales_documents (company_id, doc_no, doc_date, status, is_deleted) VALUES ('C1', 'S-POST', '2026-01-03', 'Posted', 1);
            INSERT INTO purchase_documents (company_id, doc_no, doc_date, status, is_deleted) VALUES ('C1', 'P-DEL', '2026-01-01', 'Draft', 1);
            INSERT INTO purchase_documents (company_id, doc_no, doc_date, status, is_deleted) VALUES ('C1', 'P-POST', '2026-01-03', 'Posted', 1);
            """);
        // Attach one line to the deletable sales draft (doc_id 1).
        Exec("INSERT INTO sales_document_lines (doc_id, line_no, description, qty, unit_price) VALUES (1, 1, 'Item', 1, 10)");
    }
}
