using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.Domain;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.Infrastructure;

/// <summary>
/// Provider-neutral Operations persistence (SQLite + PostgreSQL): parameterized SQL
/// only, company scoping on every statement, single-transaction writes.
/// Covers bank reconciliation marks (035), POS cash closings + support tickets
/// (037), the outbound_messages outbox (006), the audit logs (001/029),
/// fiscal_years roll-over, audited compression of soft-deleted drafts and
/// transactional document-total repost.
/// </summary>
public sealed class OperationsRepository : IOperationsRepository
{
    private const int ListLimit = 1000;

    private readonly string _provider;
    private readonly string _connectionString;

    public OperationsRepository(string provider, string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("A database connection string is required.", nameof(connectionString));
        _provider = NormalizeProvider(provider ?? throw new ArgumentNullException(nameof(provider)));
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    }

    internal string Provider => _provider;
    internal string ConnectionString => _connectionString;

    private bool IsPg => _provider == "PostgreSQL";
    private string NotDeleted => IsPg ? "is_deleted = false" : "is_deleted = 0";
    private string Deleted => IsPg ? "is_deleted = true" : "is_deleted = 1";
    private string NowExpr => IsPg ? "now()" : "datetime('now')";
    private object Bool(bool value) => IsPg ? value : (value ? 1 : 0);

    /// <summary>
    /// Callers of this repository pass the company CODE (BankRecWin passes
    /// the shell's CurrentCompanyCode), but chart_of_accounts and
    /// journal_entries are keyed by the company Guid (migration 059
    /// unification). Resolve code -&gt; Guid through the companies row for
    /// those two families; code-keyed tables (banks, marks, documents)
    /// keep using the passed value unchanged.
    /// </summary>
    private async Task<string> ResolveChartCompanyAsync(DbConnection connection, string companyId, CancellationToken ct)
    {
        var value = companyId.Trim();
        if (Guid.TryParse(value, out _))
            return value;
        if (!await TableExistsAsync(connection, "companies").ConfigureAwait(false))
            return value;
        await using var cmd = CreateCommand(connection, null,
            "SELECT CAST(company_id AS TEXT) FROM companies WHERE company_code = @company");
        Add(cmd, "@company", value);
        var resolved = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        var text = resolved is null or DBNull ? null : Convert.ToString(resolved, CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(text) ? value : text!;
    }

    // ------------------------------------------------------------ Bank rec

    public async Task<IReadOnlyList<BankAccountOption>> GetBankAccountsAsync(string companyId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        if (await TableExistsAsync(connection, "banks").ConfigureAwait(false))
        {
            var bankRows = await QueryOptionsAsync(connection,
                $"SELECT gl_account_code, name_en FROM banks WHERE company_id = @company AND gl_account_code IS NOT NULL AND gl_account_code <> '' AND {NotDeleted} ORDER BY name_en",
                companyId, cancellationToken).ConfigureAwait(false);
            if (bankRows.Count > 0)
                return bankRows;
        }

        // Canonical chart only (migration 059): the legacy accounts table
        // no longer exists. Reconcile-flagged accounts win; otherwise fall
        // back to postable asset accounts.
        if (await TableExistsAsync(connection, "chart_of_accounts").ConfigureAwait(false))
        {
            var chartCompany = await ResolveChartCompanyAsync(connection, companyId, cancellationToken).ConfigureAwait(false);
            var flagged = await QueryOptionsAsync(connection,
                $"SELECT account_code, account_name FROM chart_of_accounts WHERE CAST(company_id AS TEXT) = @company AND allow_reconcile = {(IsPg ? "true" : "1")} AND {NotDeleted} ORDER BY account_code",
                chartCompany, cancellationToken).ConfigureAwait(false);
            if (flagged.Count > 0)
                return flagged;

            return await QueryOptionsAsync(connection,
                $"SELECT account_code, account_name FROM chart_of_accounts WHERE CAST(company_id AS TEXT) = @company AND account_type = 'ASSET' AND is_postable = {(IsPg ? "true" : "1")} AND {NotDeleted} ORDER BY account_code",
                chartCompany, cancellationToken).ConfigureAwait(false);
        }

        return Array.Empty<BankAccountOption>();
    }

    private async Task<List<BankAccountOption>> QueryOptionsAsync(DbConnection connection, string sql, string companyId, CancellationToken ct)
    {
        await using var command = CreateCommand(connection, null, sql);
        Add(command, "@company", companyId.Trim());
        var result = new List<BankAccountOption>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var code = ReadString(reader, 0);
            if (string.IsNullOrWhiteSpace(code))
                continue;
            var name = ReadString(reader, 1);
            result.Add(new BankAccountOption
            {
                AccountCode = code.Trim(),
                DisplayName = string.IsNullOrWhiteSpace(name) ? code.Trim() : $"{code.Trim()} - {name.Trim()}"
            });
        }
        return result;
    }

    public async Task<IReadOnlyList<BankJournalLineRow>> GetBankJournalLinesAsync(string companyId, string accountCode, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        if (string.IsNullOrWhiteSpace(accountCode))
            throw new ArgumentException("Account code is required.", nameof(accountCode));
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Resolve the account code to the line-level account_id values used
        // by journal_entry_lines: the canonical chart_of_accounts.account_id
        // (text/uuid). The legacy integer accounts ids are gone (059).
        var chartCompany = await ResolveChartCompanyAsync(connection, companyId, cancellationToken).ConfigureAwait(false);
        var accountIds = new List<string>();
        if (await TableExistsAsync(connection, "chart_of_accounts").ConfigureAwait(false))
        {
            await using var cmd = CreateCommand(connection, null,
                "SELECT CAST(account_id AS TEXT) FROM chart_of_accounts WHERE CAST(company_id AS TEXT) = @company AND account_code = @code");
            Add(cmd, "@company", chartCompany);
            Add(cmd, "@code", accountCode.Trim());
            await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var v = ReadString(r, 0);
                if (!string.IsNullOrWhiteSpace(v)) accountIds.Add(v!);
            }
        }
        // Free-entry fallback: the code itself may be the stored account_id.
        if (accountIds.Count == 0)
            accountIds.Add(accountCode.Trim());

        var inList = string.Join(", ", accountIds.Select((_, i) => $"@acct{i}"));
        var sql = $"""
            SELECT CAST(l.journal_entry_line_id AS TEXT), CAST(j.journal_entry_id AS TEXT),
                   CAST(j.entry_date AS TEXT), COALESCE(j.document_number, CAST(j.journal_entry_id AS TEXT)),
                   COALESCE(l.note, j.description), l.debit, l.credit,
                   m.statement_date, m.statement_ref, m.id
            FROM journal_entry_lines l
            INNER JOIN journal_entries j ON CAST(j.journal_entry_id AS TEXT) = CAST(l.journal_entry_id AS TEXT)
            LEFT JOIN bank_reconciliation_marks m
              ON m.company_id = @company AND m.journal_line_key = CAST(l.journal_entry_line_id AS TEXT)
            WHERE CAST(j.company_id AS TEXT) = @companyGuid AND j.status = 1 AND CAST(l.account_id AS TEXT) IN ({inList})
            ORDER BY j.entry_date, l.line_number
            """;
        // No row cap (BNK-03: the old LIMIT 1000 silently truncated the
        // book side, so every total — and any difference computed from
        // it — was wrong for accounts with more posted lines).
        await using var command = CreateCommand(connection, null, sql);
        Add(command, "@company", companyId.Trim());
        Add(command, "@companyGuid", chartCompany);
        for (var i = 0; i < accountIds.Count; i++)
            Add(command, $"@acct{i}", accountIds[i]);

        var result = new List<BankJournalLineRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var cleared = !reader.IsDBNull(9);
            result.Add(new BankJournalLineRow
            {
                LineKey = ReadString(reader, 0) ?? string.Empty,
                EntryId = ReadString(reader, 1) ?? string.Empty,
                EntryDate = ShortDate(ReadString(reader, 2)),
                DocNo = ReadString(reader, 3) ?? string.Empty,
                Memo = ReadString(reader, 4) ?? string.Empty,
                Debit = ReadDecimal(reader, 5),
                Credit = ReadDecimal(reader, 6),
                IsCleared = cleared,
                Selected = cleared,
                StatementDate = ReadString(reader, 7),
                StatementRef = ReadString(reader, 8)
            });
        }
        return result;
    }

    public async Task SaveBankMarksAsync(string companyId, IReadOnlyList<BankReconciliationMark> marks, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(marks);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var mark in marks)
            {
                await using var cmd = CreateCommand(connection, transaction, """
                    INSERT INTO bank_reconciliation_marks
                        (id, company_id, journal_entry_id, journal_line_key, account_code, statement_date, statement_ref, cleared_amount, is_cleared, created_by)
                    VALUES (@id, @company, @entry, @key, @code, @sdate, @sref, @amount, 1, @user)
                    ON CONFLICT (company_id, journal_line_key) DO UPDATE SET
                        statement_date = EXCLUDED.statement_date,
                        statement_ref = EXCLUDED.statement_ref,
                        cleared_amount = EXCLUDED.cleared_amount,
                        is_cleared = 1,
                        updated_by = EXCLUDED.created_by,
                        updated_at = CURRENT_TIMESTAMP
                    """);
                // SQLite does not support EXCLUDED in the same way in older versions;
                // the statement above is valid for SQLite >= 3.24 and PostgreSQL.
                Add(cmd, "@id", FormatGuid(mark.Id));
                Add(cmd, "@company", companyId.Trim());
                Add(cmd, "@entry", mark.JournalEntryId);
                Add(cmd, "@key", mark.JournalLineKey);
                Add(cmd, "@code", mark.AccountCode.Trim());
                Add(cmd, "@sdate", mark.StatementDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                Add(cmd, "@sref", (object?)NullIfBlank(mark.StatementRef) ?? DBNull.Value);
                Add(cmd, "@amount", mark.ClearedAmount);
                Add(cmd, "@user", userId);
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task UnmarkBankLinesAsync(string companyId, IReadOnlyList<string> lineKeys, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(lineKeys);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var key in lineKeys)
            {
                await using var cmd = CreateCommand(connection, transaction,
                    "DELETE FROM bank_reconciliation_marks WHERE company_id = @company AND journal_line_key = @key");
                Add(cmd, "@company", companyId.Trim());
                Add(cmd, "@key", key);
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    // ------------------------------------------------------------ Bank statements (063)

    public async Task<Guid> SaveBankStatementAsync(string companyId, BankStatement statement, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(statement);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var update = CreateCommand(connection, transaction, """
                UPDATE bank_statements SET account_code = @code, statement_date = @sdate, statement_ref = @sref,
                    period_from = @from, period_to = @to, opening_balance = @opening, closing_balance = @closing,
                    status = @status, updated_by = @user, updated_at = CURRENT_TIMESTAMP
                WHERE company_id = @company AND id = @id AND is_deleted = 0
                """))
            {
                BindStatement(update, companyId, statement, userId);
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1)
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return statement.Id;
                }
            }
            await using var insert = CreateCommand(connection, transaction, """
                INSERT INTO bank_statements
                    (id, company_id, account_code, statement_date, statement_ref, period_from, period_to,
                     opening_balance, closing_balance, status, created_by)
                VALUES (@id, @company, @code, @sdate, @sref, @from, @to, @opening, @closing, @status, @user)
                """);
            BindStatement(insert, companyId, statement, userId);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return statement.Id;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private void BindStatement(DbCommand command, string companyId, BankStatement statement, string userId)
    {
        Add(command, "@id", FormatGuid(statement.Id));
        Add(command, "@company", companyId.Trim());
        Add(command, "@code", statement.AccountCode.Trim());
        Add(command, "@sdate", statement.StatementDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add(command, "@sref", (object?)NullIfBlank(statement.StatementRef) ?? DBNull.Value);
        Add(command, "@from", (object?)statement.PeriodFrom?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? DBNull.Value);
        Add(command, "@to", (object?)statement.PeriodTo?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? DBNull.Value);
        Add(command, "@opening", statement.OpeningBalance);
        Add(command, "@closing", statement.ClosingBalance);
        Add(command, "@status", string.IsNullOrWhiteSpace(statement.Status) ? BankStatementStatuses.Open : statement.Status);
        Add(command, "@user", userId);
    }

    public async Task<IReadOnlyList<BankStatement>> GetBankStatementsAsync(string companyId, string accountCode, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = CreateCommand(connection, null, """
            SELECT id, account_code, statement_date, statement_ref, period_from, period_to, opening_balance, closing_balance, status
            FROM bank_statements WHERE company_id = @company AND account_code = @code AND is_deleted = 0
            ORDER BY statement_date DESC, created_at DESC
            """);
        Add(cmd, "@company", companyId.Trim());
        Add(cmd, "@code", accountCode.Trim());
        var result = new List<BankStatement>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(ReadStatement(reader));
        return result;
    }

    public async Task<BankStatement?> GetBankStatementAsync(string companyId, Guid statementId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = CreateCommand(connection, null, """
            SELECT id, account_code, statement_date, statement_ref, period_from, period_to, opening_balance, closing_balance, status
            FROM bank_statements WHERE company_id = @company AND id = @id AND is_deleted = 0
            """);
        Add(cmd, "@company", companyId.Trim());
        Add(cmd, "@id", FormatGuid(statementId));
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadStatement(reader) : null;
    }

    private static BankStatement ReadStatement(DbDataReader reader) => new()
    {
        Id = ReadGuid(reader, 0),
        AccountCode = ReadString(reader, 1) ?? string.Empty,
        StatementDate = ReadDate(reader, 2) ?? DateOnly.MinValue,
        StatementRef = ReadString(reader, 3),
        PeriodFrom = ReadDate(reader, 4),
        PeriodTo = ReadDate(reader, 5),
        OpeningBalance = ReadDecimal(reader, 6),
        ClosingBalance = ReadDecimal(reader, 7),
        Status = ReadString(reader, 8) ?? BankStatementStatuses.Open
    };

    public async Task<Guid> AddBankStatementLineAsync(string companyId, BankStatementLine line, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(line);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureStatementOpenAsync(connection, transaction, companyId, line.StatementId, cancellationToken).ConfigureAwait(false);
            await using var cmd = CreateCommand(connection, transaction, """
                INSERT INTO bank_statement_lines
                    (id, statement_id, company_id, account_code, line_date, reference, description, amount, matched_journal_line_key, created_by)
                VALUES (@id, @statement, @company, @code, @date, @ref, @desc, @amount, NULL, @user)
                """);
            Add(cmd, "@id", FormatGuid(line.Id));
            Add(cmd, "@statement", FormatGuid(line.StatementId));
            Add(cmd, "@company", companyId.Trim());
            Add(cmd, "@code", line.AccountCode.Trim());
            Add(cmd, "@date", line.LineDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            Add(cmd, "@ref", (object?)NullIfBlank(line.Reference) ?? DBNull.Value);
            Add(cmd, "@desc", (object?)NullIfBlank(line.Description) ?? DBNull.Value);
            Add(cmd, "@amount", line.Amount);
            Add(cmd, "@user", userId);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return line.Id;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IReadOnlyList<BankStatementLine>> GetBankStatementLinesAsync(string companyId, Guid statementId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = CreateCommand(connection, null, """
            SELECT id, statement_id, account_code, line_date, reference, description, amount, matched_journal_line_key
            FROM bank_statement_lines WHERE company_id = @company AND statement_id = @statement AND is_deleted = 0
            ORDER BY line_date, created_at
            """);
        Add(cmd, "@company", companyId.Trim());
        Add(cmd, "@statement", FormatGuid(statementId));
        var result = new List<BankStatementLine>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new BankStatementLine
            {
                Id = ReadGuid(reader, 0),
                StatementId = ReadGuid(reader, 1),
                AccountCode = ReadString(reader, 2) ?? string.Empty,
                LineDate = ReadDate(reader, 3) ?? DateOnly.MinValue,
                Reference = ReadString(reader, 4),
                Description = ReadString(reader, 5),
                Amount = ReadDecimal(reader, 6),
                MatchedJournalLineKey = ReadString(reader, 7)
            });
        }
        return result;
    }

    public async Task RemoveBankStatementLineAsync(string companyId, Guid lineId, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = CreateCommand(connection, null, """
            UPDATE bank_statement_lines SET is_deleted = 1, updated_by = @user, updated_at = CURRENT_TIMESTAMP
            WHERE company_id = @company AND id = @id AND is_deleted = 0 AND matched_journal_line_key IS NULL
            """);
        Add(cmd, "@company", companyId.Trim());
        Add(cmd, "@id", FormatGuid(lineId));
        Add(cmd, "@user", userId);
        if (await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("Statement line was not found, already removed, or is matched (unmatch it first).");
    }

    public async Task MatchBankLineAsync(string companyId, Guid statementLineId, BankReconciliationMark mark, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(mark);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The statement line must exist, belong to an OPEN statement,
            // and be currently unmatched.
            Guid statementId;
            string? existingMatch;
            await using (var check = CreateCommand(connection, transaction, """
                SELECT CAST(statement_id AS TEXT), matched_journal_line_key FROM bank_statement_lines
                WHERE company_id = @company AND id = @id AND is_deleted = 0
                """))
            {
                Add(check, "@company", companyId.Trim());
                Add(check, "@id", FormatGuid(statementLineId));
                await using var reader = await check.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("Statement line was not found for this company.");
                statementId = ReadGuid(reader, 0);
                existingMatch = ReadString(reader, 1);
            }
            if (!string.IsNullOrWhiteSpace(existingMatch))
                throw new AppError(ErrorCodes.StatementLineAlreadyMatched,
                    "The statement line is already matched to a book line; unmatch it first.", statementLineId.ToString("D"));
            await EnsureStatementOpenAsync(connection, transaction, companyId, statementId, cancellationToken).ConfigureAwait(false);

            await using (var upsert = CreateCommand(connection, transaction, """
                INSERT INTO bank_reconciliation_marks
                    (id, company_id, journal_entry_id, journal_line_key, account_code, statement_date, statement_ref, cleared_amount, is_cleared, statement_id, statement_line_id, created_by)
                VALUES (@id, @company, @entry, @key, @code, @sdate, @sref, @amount, 1, @statement, @sline, @user)
                ON CONFLICT (company_id, journal_line_key) DO UPDATE SET
                    statement_date = EXCLUDED.statement_date,
                    statement_ref = EXCLUDED.statement_ref,
                    cleared_amount = EXCLUDED.cleared_amount,
                    is_cleared = 1,
                    statement_id = EXCLUDED.statement_id,
                    statement_line_id = EXCLUDED.statement_line_id,
                    updated_by = EXCLUDED.created_by,
                    updated_at = CURRENT_TIMESTAMP
                """))
            {
                Add(upsert, "@id", FormatGuid(mark.Id));
                Add(upsert, "@company", companyId.Trim());
                Add(upsert, "@entry", mark.JournalEntryId);
                Add(upsert, "@key", mark.JournalLineKey);
                Add(upsert, "@code", mark.AccountCode.Trim());
                Add(upsert, "@sdate", mark.StatementDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                Add(upsert, "@sref", (object?)NullIfBlank(mark.StatementRef) ?? DBNull.Value);
                Add(upsert, "@amount", mark.ClearedAmount);
                Add(upsert, "@statement", FormatGuid(statementId));
                Add(upsert, "@sline", FormatGuid(statementLineId));
                Add(upsert, "@user", userId);
                await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using var link = CreateCommand(connection, transaction, """
                UPDATE bank_statement_lines SET matched_journal_line_key = @key, updated_by = @user, updated_at = CURRENT_TIMESTAMP
                WHERE company_id = @company AND id = @id AND matched_journal_line_key IS NULL AND is_deleted = 0
                """);
            Add(link, "@key", mark.JournalLineKey);
            Add(link, "@user", userId);
            Add(link, "@company", companyId.Trim());
            Add(link, "@id", FormatGuid(statementLineId));
            if (await link.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("Statement line changed while matching; refresh and retry.");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task UnmatchBankLineAsync(string companyId, Guid statementLineId, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string? matchedKey;
            Guid statementId;
            await using (var check = CreateCommand(connection, transaction, """
                SELECT CAST(statement_id AS TEXT), matched_journal_line_key FROM bank_statement_lines
                WHERE company_id = @company AND id = @id AND is_deleted = 0
                """))
            {
                Add(check, "@company", companyId.Trim());
                Add(check, "@id", FormatGuid(statementLineId));
                await using var reader = await check.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("Statement line was not found for this company.");
                statementId = ReadGuid(reader, 0);
                matchedKey = ReadString(reader, 1);
            }
            if (string.IsNullOrWhiteSpace(matchedKey))
                throw new InvalidOperationException("Statement line is not matched.");
            await EnsureStatementOpenAsync(connection, transaction, companyId, statementId, cancellationToken).ConfigureAwait(false);

            await using (var del = CreateCommand(connection, transaction,
                "DELETE FROM bank_reconciliation_marks WHERE company_id = @company AND journal_line_key = @key AND statement_line_id = @sline"))
            {
                Add(del, "@company", companyId.Trim());
                Add(del, "@key", matchedKey);
                Add(del, "@sline", FormatGuid(statementLineId));
                await del.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await using var unlink = CreateCommand(connection, transaction, """
                UPDATE bank_statement_lines SET matched_journal_line_key = NULL, updated_by = @user, updated_at = CURRENT_TIMESTAMP
                WHERE company_id = @company AND id = @id AND is_deleted = 0
                """);
            Add(unlink, "@user", userId);
            Add(unlink, "@company", companyId.Trim());
            Add(unlink, "@id", FormatGuid(statementLineId));
            if (await unlink.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("Statement line changed while unmatching; refresh and retry.");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task CloseBankStatementAsync(string companyId, Guid statementId, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = CreateCommand(connection, null, """
            UPDATE bank_statements SET status = 'Closed', updated_by = @user, updated_at = CURRENT_TIMESTAMP
            WHERE company_id = @company AND id = @id AND status = 'Open' AND is_deleted = 0
            """);
        Add(cmd, "@company", companyId.Trim());
        Add(cmd, "@id", FormatGuid(statementId));
        Add(cmd, "@user", userId);
        if (await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("Statement was not found or is already closed.");
    }

    private async Task EnsureStatementOpenAsync(DbConnection connection, DbTransaction transaction, string companyId, Guid statementId, CancellationToken ct)
    {
        await using var cmd = CreateCommand(connection, transaction,
            "SELECT status FROM bank_statements WHERE company_id = @company AND id = @id AND is_deleted = 0");
        Add(cmd, "@company", companyId.Trim());
        Add(cmd, "@id", FormatGuid(statementId));
        var status = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (status is null or DBNull)
            throw new InvalidOperationException("Bank statement was not found for this company.");
        if (!string.Equals(Convert.ToString(status, CultureInfo.InvariantCulture), BankStatementStatuses.Open, StringComparison.Ordinal))
            throw new InvalidOperationException("Bank statement is closed; its lines can no longer change.");
    }

    // ------------------------------------------------------------ POS closing

    public async Task<PosSalesSummary> GetPosSalesSummaryAsync(string companyId, DateOnly businessDate, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = CreateCommand(connection, null, $"""
            SELECT COUNT(*), COALESCE(SUM(grand_total), 0) FROM sales_documents
            WHERE company_id = @company AND doc_kind = 'SAL' AND doc_date = @date
              AND {NotDeleted} AND status <> 'Cancelled'
            """);
        Add(cmd, "@company", companyId.Trim());
        Add(cmd, "@date", businessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return new PosSalesSummary(Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture), ReadDecimal(reader, 1));
        return new PosSalesSummary(0, 0m);
    }

    public async Task SavePosClosingAsync(string companyId, PosCashClosing closing, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(closing);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = CreateCommand(connection, null, """
            INSERT INTO pos_cash_closings
                (id, company_id, terminal_code, business_date, opened_by, closed_by, opening_amount, expected_amount, counted_amount, difference, status, notes, created_by)
            VALUES (@id, @company, @terminal, @date, @opened, @closed, @opening, @expected, @counted, @diff, @status, @notes, @user)
            """);
        Add(cmd, "@id", FormatGuid(closing.Id));
        Add(cmd, "@company", companyId.Trim());
        Add(cmd, "@terminal", closing.TerminalCode.Trim());
        Add(cmd, "@date", closing.BusinessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add(cmd, "@opened", (object?)NullIfBlank(closing.OpenedBy) ?? DBNull.Value);
        Add(cmd, "@closed", (object?)NullIfBlank(closing.ClosedBy) ?? DBNull.Value);
        Add(cmd, "@opening", closing.OpeningAmount);
        Add(cmd, "@expected", closing.ExpectedAmount);
        Add(cmd, "@counted", closing.CountedAmount);
        Add(cmd, "@diff", closing.Difference);
        Add(cmd, "@status", string.IsNullOrWhiteSpace(closing.Status) ? "Closed" : closing.Status);
        Add(cmd, "@notes", (object?)NullIfBlank(closing.Notes) ?? DBNull.Value);
        Add(cmd, "@user", userId);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PosCashClosing>> GetPosClosingsAsync(string companyId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = CreateCommand(connection, null, $"""
            SELECT id, terminal_code, business_date, opened_by, closed_by, opening_amount, expected_amount, counted_amount, difference, status, notes
            FROM pos_cash_closings WHERE company_id = @company ORDER BY business_date DESC, created_at DESC LIMIT {ListLimit}
            """);
        Add(cmd, "@company", companyId.Trim());
        var result = new List<PosCashClosing>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new PosCashClosing
            {
                Id = ReadGuid(reader, 0),
                TerminalCode = ReadString(reader, 1) ?? string.Empty,
                BusinessDate = ReadDate(reader, 2) ?? DateOnly.MinValue,
                OpenedBy = ReadString(reader, 3),
                ClosedBy = ReadString(reader, 4),
                OpeningAmount = ReadDecimal(reader, 5),
                ExpectedAmount = ReadDecimal(reader, 6),
                CountedAmount = ReadDecimal(reader, 7),
                Difference = ReadDecimal(reader, 8),
                Status = ReadString(reader, 9) ?? "Closed",
                Notes = ReadString(reader, 10)
            });
        }
        return result;
    }

    // ------------------------------------------------------------ Outbox

    public async Task<IReadOnlyList<OutboundMessageRow>> GetOutboundMessagesAsync(string companyId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = CreateCommand(connection, null, $"""
            SELECT outbound_message_id, channel, recipient_address, COALESCE(subject, ''), status,
                   attempt_count, max_attempts, last_error, CAST(created_at AS TEXT), CAST(next_attempt_at AS TEXT)
            FROM outbound_messages
            WHERE (company_id IS NULL OR company_id = @company)
            ORDER BY outbound_message_id DESC LIMIT {ListLimit}
            """);
        Add(cmd, "@company", companyId.Trim());
        var result = new List<OutboundMessageRow>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new OutboundMessageRow
            {
                Id = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                Channel = ReadString(reader, 1) ?? string.Empty,
                Recipient = ReadString(reader, 2) ?? string.Empty,
                Subject = ReadString(reader, 3) ?? string.Empty,
                Status = ReadString(reader, 4) ?? string.Empty,
                Attempts = Convert.ToInt32(reader.GetValue(5), CultureInfo.InvariantCulture),
                MaxAttempts = Convert.ToInt32(reader.GetValue(6), CultureInfo.InvariantCulture),
                LastError = ReadString(reader, 7),
                CreatedAt = ReadString(reader, 8) ?? string.Empty,
                NextAttemptAt = ReadString(reader, 9)
            });
        }
        return result;
    }

    public async Task RetryOutboundMessageAsync(long messageId, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = CreateCommand(connection, null, $"""
            UPDATE outbound_messages
            SET status = 'QUEUED', attempt_count = 0, last_error = NULL,
                next_attempt_at = {NowExpr}, updated_at = {NowExpr}
            WHERE outbound_message_id = @id AND status = 'FAILED'
            """);
        Add(cmd, "@id", messageId);
        if (await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("Only a FAILED outbound message can be retried (it may have been sent or already re-queued).");
    }

    // ------------------------------------------------------------ Audit viewers

    public async Task<IReadOnlyList<SecurityAuditRow>> GetSecurityAuditAsync(string companyId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var resolved = await ResolveCompanyIdAsync(connection, companyId, cancellationToken).ConfigureAwait(false);
        await using var cmd = CreateCommand(connection, null, $"""
            SELECT security_audit_id, CAST(occurred_at AS TEXT), username, event_type, details
            FROM security_audit_log
            WHERE (company_id IS NULL OR company_id = @company OR company_id = @resolved)
              AND (@from IS NULL OR CAST(occurred_at AS TEXT) >= @from)
              AND (@to IS NULL OR CAST(occurred_at AS TEXT) < @to)
            ORDER BY occurred_at DESC, security_audit_id DESC LIMIT {ListLimit}
            """);
        Add(cmd, "@company", companyId.Trim());
        Add(cmd, "@resolved", resolved);
        Add(cmd, "@from", (object?)from?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? DBNull.Value);
        Add(cmd, "@to", (object?)to?.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? DBNull.Value);
        var result = new List<SecurityAuditRow>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new SecurityAuditRow
            {
                Id = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                OccurredAt = ReadString(reader, 1) ?? string.Empty,
                Username = ReadString(reader, 2) ?? string.Empty,
                EventType = ReadString(reader, 3) ?? string.Empty,
                Details = ReadString(reader, 4)
            });
        }
        return result;
    }

    public async Task<IReadOnlyList<JournalAuditRow>> GetJournalAuditAsync(string companyId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var resolved = await ResolveCompanyIdAsync(connection, companyId, cancellationToken).ConfigureAwait(false);
        await using var cmd = CreateCommand(connection, null, $"""
            SELECT e.journal_audit_event_id, CAST(e.journal_entry_id AS TEXT), CAST(e.event_type AS TEXT), e.actor, CAST(e.event_at AS TEXT), e.details
            FROM journal_audit_events e
            INNER JOIN journal_entries j ON CAST(j.journal_entry_id AS TEXT) = CAST(e.journal_entry_id AS TEXT)
            WHERE (j.company_id = @company OR j.company_id = @resolved)
              AND (@from IS NULL OR CAST(e.event_at AS TEXT) >= @from)
              AND (@to IS NULL OR CAST(e.event_at AS TEXT) < @to)
            ORDER BY e.event_at DESC, e.journal_audit_event_id DESC LIMIT {ListLimit}
            """);
        Add(cmd, "@company", companyId.Trim());
        Add(cmd, "@resolved", resolved);
        Add(cmd, "@from", (object?)from?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? DBNull.Value);
        Add(cmd, "@to", (object?)to?.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? DBNull.Value);
        var result = new List<JournalAuditRow>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new JournalAuditRow
            {
                Id = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                EntryId = ReadString(reader, 1) ?? string.Empty,
                EventType = ReadString(reader, 2) ?? string.Empty,
                Actor = ReadString(reader, 3) ?? string.Empty,
                EventAt = ReadString(reader, 4) ?? string.Empty,
                Details = ReadString(reader, 5)
            });
        }
        return result;
    }

    // ------------------------------------------------------------ Maintenance

    public async Task VacuumAnalyzeAsync(CancellationToken cancellationToken = default)
    {
        if (IsPg)
            throw new NotSupportedException("VACUUM/ANALYZE from this window is SQLite-only; on PostgreSQL use the database server's autovacuum/maintenance tooling.");
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var vacuum = CreateCommand(connection, null, "VACUUM"))
            await vacuum.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await using (var analyze = CreateCommand(connection, null, "ANALYZE"))
            await analyze.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> GetIntegritySummaryAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (!IsPg)
        {
            await using var cmd = CreateCommand(connection, null, "PRAGMA integrity_check");
            var value = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            var tables = await ScalarLongAsync(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table'", cancellationToken).ConfigureAwait(false);
            return $"SQLite integrity_check: {Convert.ToString(value, CultureInfo.InvariantCulture)}; tables: {tables}";
        }
        else
        {
            await using var cmd = CreateCommand(connection, null, "SELECT version()");
            var version = Convert.ToString(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            var tables = await ScalarLongAsync(connection, "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'public'", cancellationToken).ConfigureAwait(false);
            return $"PostgreSQL reachable; tables (public): {tables}; {version}";
        }
    }

    /// <summary>
    /// Trial-balance integrity audit (B5, read-only): posted entries
    /// (status = 1) must balance per fiscal period and individually;
    /// journal lines must never be orphaned; a posted entry must carry
    /// lines. journal_entries is Guid-keyed (059 unification) while
    /// callers pass the company code — the dual condition matches the
    /// fiscal-year queries above.
    /// </summary>
    public async Task<TrialBalanceIntegrityResult> GetTrialBalanceIntegrityAsync(string companyId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var chart = await ResolveChartCompanyAsync(connection, companyId, cancellationToken).ConfigureAwait(false);
        const string companyCond = "(e.company_id = @company OR CAST(e.company_id AS TEXT) = @chart)";
        var hasPeriodCol = await ColumnExistsAsync(connection, "journal_entries", "fiscal_period_id").ConfigureAwait(false);
        var hasPeriods = await TableExistsAsync(connection, "fiscal_periods").ConfigureAwait(false);
        var periodExpr = hasPeriodCol && hasPeriods ? "COALESCE(fp.period_name, '')" : "''";
        var periodStatusExpr = hasPeriodCol && hasPeriods ? "COALESCE(fp.status, '')" : "''";
        var periodJoin = hasPeriodCol && hasPeriods
            ? "LEFT JOIN fiscal_periods fp ON fp.fiscal_period_id = e.fiscal_period_id"
            : string.Empty;

        var result = new TrialBalanceIntegrityResult();

        // Per-period posted totals (entries without lines contribute 0
        // through the LEFT JOIN and are counted separately below).
        var periods = new List<TrialBalancePeriodRow>();
        await using (var cmd = CreateCommand(connection, null, $"""
            SELECT {periodExpr} AS period, COUNT(DISTINCT e.journal_entry_id) AS entries,
                   COALESCE(SUM(l.debit), 0) AS dr, COALESCE(SUM(l.credit), 0) AS cr, {periodStatusExpr} AS pstatus
            FROM journal_entries e
            {periodJoin}
            LEFT JOIN journal_entry_lines l ON l.journal_entry_id = e.journal_entry_id
            WHERE {companyCond} AND e.status = 1
            GROUP BY period, pstatus
            ORDER BY period
            """))
        {
            Add(cmd, "@company", companyId.Trim());
            Add(cmd, "@chart", chart);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                periods.Add(new TrialBalancePeriodRow
                {
                    Period = ReadString(reader, 0) ?? string.Empty,
                    Entries = Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture),
                    Debit = Convert.ToDecimal(reader.GetValue(2), CultureInfo.InvariantCulture),
                    Credit = Convert.ToDecimal(reader.GetValue(3), CultureInfo.InvariantCulture),
                    Status = ReadString(reader, 4) ?? string.Empty
                });
            }
        }
        result.Periods = periods;

        // Individually unbalanced posted entries (no cap: an integrity
        // failure list must be complete, however long).
        var unbalanced = new List<UnbalancedEntryRow>();
        await using (var cmd = CreateCommand(connection, null, $"""
            SELECT e.journal_entry_id, CAST(e.entry_date AS TEXT),
                   COALESCE(SUM(l.debit), 0) AS dr, COALESCE(SUM(l.credit), 0) AS cr
            FROM journal_entries e
            JOIN journal_entry_lines l ON l.journal_entry_id = e.journal_entry_id
            WHERE {companyCond} AND e.status = 1
            GROUP BY e.journal_entry_id, e.entry_date
            HAVING ABS(COALESCE(SUM(l.debit), 0) - COALESCE(SUM(l.credit), 0)) > 0.004
            ORDER BY e.entry_date, e.journal_entry_id
            """))
        {
            Add(cmd, "@company", companyId.Trim());
            Add(cmd, "@chart", chart);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                unbalanced.Add(new UnbalancedEntryRow
                {
                    EntryId = ReadString(reader, 0) ?? string.Empty,
                    EntryDate = ReadString(reader, 1) ?? string.Empty,
                    Debit = Convert.ToDecimal(reader.GetValue(2), CultureInfo.InvariantCulture),
                    Credit = Convert.ToDecimal(reader.GetValue(3), CultureInfo.InvariantCulture)
                });
            }
        }
        result.UnbalancedEntries = unbalanced;

        // Orphan lines: a line whose entry no longer exists (the FK
        // should make this impossible — that is the point of the check).
        await using (var cmd = CreateCommand(connection, null, """
            SELECT COUNT(*) FROM journal_entry_lines l
            LEFT JOIN journal_entries e ON e.journal_entry_id = l.journal_entry_id
            WHERE e.journal_entry_id IS NULL
            """))
        {
            var value = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            result.OrphanLineCount = Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }

        // Posted entries carrying no lines at all.
        await using (var cmd = CreateCommand(connection, null, $"""
            SELECT COUNT(*) FROM journal_entries e
            WHERE {companyCond} AND e.status = 1
              AND NOT EXISTS (SELECT 1 FROM journal_entry_lines l WHERE l.journal_entry_id = e.journal_entry_id)
            """))
        {
            Add(cmd, "@company", companyId.Trim());
            Add(cmd, "@chart", chart);
            var value = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            result.PostedEntriesWithoutLines = Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }

        return result;
    }

    // ------------------------------------------------------------ Fiscal roll-over

    public async Task<IReadOnlyList<FiscalYearRow>> GetFiscalYearsAsync(string companyId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var resolved = await ResolveCompanyIdAsync(connection, companyId, cancellationToken).ConfigureAwait(false);
        var hasClosedCols = await ColumnExistsAsync(connection, "fiscal_years", "closed_by").ConfigureAwait(false);
        var closedCols = hasClosedCols ? ", closed_by, CAST(closed_at AS TEXT)" : ", NULL, NULL";
        var hasActivation = await ColumnExistsAsync(connection, "fiscal_years", "period_end_activation").ConfigureAwait(false);
        var activationCol = hasActivation ? ", period_end_activation" : (IsPg ? ", true" : ", 1");
        await using var cmd = CreateCommand(connection, null, $"""
            SELECT fiscal_year_id, fiscal_year, CAST(start_date AS TEXT), CAST(end_date AS TEXT), is_current, is_closed {closedCols} {activationCol}
            FROM fiscal_years
            WHERE company_id = @company OR CAST(company_id AS TEXT) = @resolved
            ORDER BY fiscal_year
            """);
        Add(cmd, "@company", companyId.Trim());
        Add(cmd, "@resolved", resolved);
        var result = new List<FiscalYearRow>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new FiscalYearRow
            {
                Id = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                FiscalYear = Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture),
                StartDate = (ReadString(reader, 2) ?? string.Empty),
                EndDate = (ReadString(reader, 3) ?? string.Empty),
                IsCurrent = ReadBool(reader, 4),
                IsClosed = ReadBool(reader, 5),
                ClosedBy = ReadString(reader, 6),
                ClosedAt = ReadString(reader, 7),
                PeriodEndActivation = ReadBool(reader, 8)
            });
        }
        return result;
    }

    public async Task CreateNextFiscalYearAsync(string companyId, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        var years = await GetFiscalYearsAsync(companyId, cancellationToken).ConfigureAwait(false);
        if (years.Count == 0)
            throw new InvalidOperationException("No fiscal year exists yet for this company; create the first year from company setup.");
        var last = years[^1];
        if (!DateOnly.TryParse(last.EndDate.Length >= 10 ? last.EndDate[..10] : last.EndDate, CultureInfo.InvariantCulture, out var lastEnd) ||
            !DateOnly.TryParse(last.StartDate.Length >= 10 ? last.StartDate[..10] : last.StartDate, CultureInfo.InvariantCulture, out var lastStart))
            throw new InvalidOperationException("The latest fiscal year has invalid dates and cannot be rolled forward.");
        var newStart = lastEnd.AddDays(1);
        var length = lastEnd.DayNumber - lastStart.DayNumber;
        var newEnd = newStart.AddDays(length);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var resolved = await ResolveCompanyIdAsync(connection, companyId, cancellationToken).ConfigureAwait(false);
        string dbName;
        await using (var cmd = CreateCommand(connection, null,
            "SELECT database_name FROM fiscal_years WHERE fiscal_year_id = @id"))
        {
            Add(cmd, "@id", last.Id);
            dbName = Convert.ToString(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) ?? string.Empty;
        }
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var newYearId = await InsertFiscalYearAsync(connection, transaction, resolved, newStart, newEnd, dbName, cancellationToken).ConfigureAwait(false);
            // The rolled year must be postable: create its 12 OPEN periods
            // (previously the year was created bare and the GL period
            // resolver could never match a date inside it).
            await InsertYearPeriodsAsync(connection, transaction, resolved, newYearId, newStart, newEnd, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<long> InsertFiscalYearAsync(DbConnection connection, DbTransaction transaction, string resolvedCompany, DateOnly newStart, DateOnly newEnd, string dbName, CancellationToken ct)
    {
        await using var insert = CreateCommand(connection, transaction, """
            INSERT INTO fiscal_years (company_id, fiscal_year, database_name, start_date, end_date, is_current, is_closed)
            VALUES (@company, @year, @db, @start, @end, @current, @closed)
            """ + (IsPg ? " RETURNING fiscal_year_id" : string.Empty));
        Add(insert, "@company", resolvedCompany);
        Add(insert, "@year", newStart.Year);
        Add(insert, "@db", dbName);
        Add(insert, "@start", newStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add(insert, "@end", newEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add(insert, "@current", Bool(false));
        Add(insert, "@closed", Bool(false));
        if (IsPg)
            return Convert.ToInt64(await insert.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await using var idCmd = CreateCommand(connection, transaction, "SELECT last_insert_rowid()");
        return Convert.ToInt64(await idCmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    /// <summary>Creates the 12 OPEN monthly periods of a fiscal year,
    /// segmented from the year's start (the accounting engine's calendar
    /// segmentation generalized to years that start mid-year); the last
    /// period is clamped to the year's end.</summary>
    private async Task InsertYearPeriodsAsync(DbConnection connection, DbTransaction transaction, string resolvedCompany, long fiscalYearId, DateOnly yearStart, DateOnly yearEnd, CancellationToken ct)
    {
        object companyValue = IsPg && Guid.TryParse(resolvedCompany, out var companyGuid) ? companyGuid : resolvedCompany;
        for (var i = 1; i <= 12; i++)
        {
            var pStart = yearStart.AddMonths(i - 1);
            if (pStart > yearEnd)
                break;
            var pEnd = pStart.AddMonths(1).AddDays(-1);
            if (pEnd > yearEnd)
                pEnd = yearEnd;
            await using var cmd = CreateCommand(connection, transaction, """
                INSERT INTO fiscal_periods (fiscal_period_id, company_id, fiscal_year_id, period_number, period_name, start_date, end_date, status)
                VALUES (@id, @company, @yearId, @number, @name, @start, @end, 'OPEN')
                """);
            Add(cmd, "@id", IsPg ? (object)Guid.NewGuid() : Guid.NewGuid().ToString("D"));
            Add(cmd, "@company", companyValue);
            Add(cmd, "@yearId", fiscalYearId);
            Add(cmd, "@number", i);
            Add(cmd, "@name", pStart.ToString("yyyy-MM", CultureInfo.InvariantCulture));
            Add(cmd, "@start", pStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            Add(cmd, "@end", pEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task CloseFiscalYearAsync(string companyId, long fiscalYearId, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var resolved = await ResolveCompanyIdAsync(connection, companyId, cancellationToken).ConfigureAwait(false);
        var hasClosedCols = await ColumnExistsAsync(connection, "fiscal_years", "closed_by").ConfigureAwait(false);
        var extra = hasClosedCols ? $", closed_by = @user, closed_at = {NowExpr}" : string.Empty;
        await using var cmd = CreateCommand(connection, null, $"""
            UPDATE fiscal_years SET is_closed = @closed, is_current = @current {extra}
            WHERE fiscal_year_id = @id AND (company_id = @company OR CAST(company_id AS TEXT) = @resolved)
              AND is_closed = @open
            """);
        Add(cmd, "@closed", Bool(true));
        Add(cmd, "@current", Bool(false));
        Add(cmd, "@open", Bool(false));
        Add(cmd, "@user", userId);
        Add(cmd, "@id", fiscalYearId);
        Add(cmd, "@company", companyId.Trim());
        Add(cmd, "@resolved", resolved);
        if (await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("Fiscal year was not found or is already closed.");
    }

    // ------------------------------------------------------------ Compression

    // ---- Period close (docs/PERIOD_CLOSE_STUDY.md) ----------------------
    // Reads are served here; the close/reopen writes delegate to the one
    // existing engine (AccountingStructureRepository) after validating the
    // period belongs to the caller's company — no parallel mechanism.

    public async Task<IReadOnlyList<FiscalPeriodRow>> GetFiscalPeriodsAsync(string companyId, long fiscalYearId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var resolved = await ResolveCompanyIdAsync(connection, companyId, cancellationToken).ConfigureAwait(false);
        await using var cmd = CreateCommand(connection, null, """
            SELECT CAST(fp.fiscal_period_id AS TEXT), fp.fiscal_year_id, fp.period_number, fp.period_name,
                   CAST(fp.start_date AS TEXT), CAST(fp.end_date AS TEXT), fp.status, fp.closed_by, CAST(fp.closed_at AS TEXT)
            FROM fiscal_periods fp
            WHERE fp.fiscal_year_id = @year
              AND (CAST(fp.company_id AS TEXT) = @resolved OR CAST(fp.company_id AS TEXT) = @company)
            ORDER BY fp.period_number
            """);
        Add(cmd, "@year", fiscalYearId);
        Add(cmd, "@resolved", resolved);
        Add(cmd, "@company", companyId.Trim());
        var result = new List<FiscalPeriodRow>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new FiscalPeriodRow
            {
                Id = Guid.Parse(ReadString(reader, 0) ?? string.Empty),
                FiscalYearId = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                PeriodNumber = Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture),
                PeriodName = ReadString(reader, 3) ?? string.Empty,
                StartDate = ReadString(reader, 4) ?? string.Empty,
                EndDate = ReadString(reader, 5) ?? string.Empty,
                Status = ReadString(reader, 6) ?? string.Empty,
                ClosedBy = ReadString(reader, 7),
                ClosedAt = ReadString(reader, 8)
            });
        }
        return result;
    }

    public async Task<IReadOnlyList<PeriodUnpostedItemRow>> GetPeriodUnpostedItemsAsync(string companyId, Guid fiscalPeriodId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var period = await LoadPeriodHeaderAsync(connection, companyId, fiscalPeriodId, cancellationToken).ConfigureAwait(false);
        var items = new List<PeriodUnpostedItemRow>();
        await using (var cmd = CreateCommand(connection, null, $"""
            SELECT e.document_number, CAST(e.entry_date AS TEXT), e.description,
                   (SELECT COALESCE(SUM(l.debit), 0) FROM journal_entry_lines l WHERE l.journal_entry_id = e.journal_entry_id)
            FROM journal_entries e
            WHERE CAST(e.fiscal_period_id AS TEXT) = @period AND e.status = 0 AND {NotDeleted}
            ORDER BY e.entry_date
            """))
        {
            Add(cmd, "@period", fiscalPeriodId.ToString("D"));
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(new PeriodUnpostedItemRow
                {
                    Kind = "JOURNAL",
                    DocKind = "JV",
                    DocNo = ReadString(reader, 0) ?? string.Empty,
                    DocDate = ReadString(reader, 1) ?? string.Empty,
                    Description = ReadString(reader, 2) ?? string.Empty,
                    Amount = Convert.ToDecimal(reader.GetValue(3), CultureInfo.InvariantCulture)
                });
            }
        }
        foreach (var table in new[] { "sales_documents", "purchase_documents" })
        {
            if (!await TableExistsAsync(connection, table).ConfigureAwait(false))
                continue;
            await using var cmd = CreateCommand(connection, null, $"""
                SELECT doc_kind, doc_no, CAST(doc_date AS TEXT), party_name, grand_total
                FROM {table}
                WHERE company_id = @company AND status = 'Draft' AND {NotDeleted}
                  AND doc_date BETWEEN @start AND @end
                ORDER BY doc_date
                """);
            Add(cmd, "@company", period.CompanyText);
            Add(cmd, "@start", period.StartDate.Length >= 10 ? period.StartDate[..10] : period.StartDate);
            Add(cmd, "@end", period.EndDate.Length >= 10 ? period.EndDate[..10] : period.EndDate);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(new PeriodUnpostedItemRow
                {
                    Kind = "DOCUMENT",
                    DocKind = ReadString(reader, 0) ?? string.Empty,
                    DocNo = ReadString(reader, 1) ?? string.Empty,
                    DocDate = ReadString(reader, 2) ?? string.Empty,
                    Description = ReadString(reader, 3) ?? string.Empty,
                    Amount = Convert.ToDecimal(reader.GetValue(4), CultureInfo.InvariantCulture)
                });
            }
        }
        return items;
    }

    public async Task ClosePeriodAsync(string companyId, Guid fiscalPeriodId, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        _ = await LoadPeriodHeaderAsync(connection, companyId, fiscalPeriodId, cancellationToken).ConfigureAwait(false);
        var engine = new AccountingStructureRepository(_provider, _connectionString);
        await engine.ClosePeriodAsync(fiscalPeriodId, userId, cancellationToken).ConfigureAwait(false);
    }

    public async Task ReopenPeriodAsync(string companyId, Guid fiscalPeriodId, string userId, string reason, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        _ = await LoadPeriodHeaderAsync(connection, companyId, fiscalPeriodId, cancellationToken).ConfigureAwait(false);
        var engine = new AccountingStructureRepository(_provider, _connectionString);
        await engine.ReopenPeriodAsync(fiscalPeriodId, userId, reason, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The fiscal-period activation control (study SS7): deactivating the
    /// latest year's activation is the deliberate, audited roll-forward —
    /// in ONE transaction the latest year's flag flips OFF, the next
    /// fiscal year is created with its 12 OPEN periods (the same bounds
    /// math as CreateNextFiscalYearAsync; the new year starts activated
    /// by the migration-127 schema default), and a
    /// FISCAL_YEAR_ROLL_FORWARD row lands in security_audit_log.
    /// </summary>
    public async Task DeactivatePeriodEndAndRollForwardAsync(string companyId, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        var years = await GetFiscalYearsAsync(companyId, cancellationToken).ConfigureAwait(false);
        if (years.Count == 0)
            throw new InvalidOperationException("No fiscal year exists yet for this company; create the first year from company setup.");
        var last = years[^1];
        if (!last.PeriodEndActivation)
            throw new InvalidOperationException("Period-end activation is already off for the latest fiscal year.");
        if (!DateOnly.TryParse(last.EndDate.Length >= 10 ? last.EndDate[..10] : last.EndDate, CultureInfo.InvariantCulture, out var lastEnd) ||
            !DateOnly.TryParse(last.StartDate.Length >= 10 ? last.StartDate[..10] : last.StartDate, CultureInfo.InvariantCulture, out var lastStart))
            throw new InvalidOperationException("The latest fiscal year has invalid dates and cannot be rolled forward.");
        var newStart = lastEnd.AddDays(1);
        var length = lastEnd.DayNumber - lastStart.DayNumber;
        var newEnd = newStart.AddDays(length);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var resolved = await ResolveCompanyIdAsync(connection, companyId, cancellationToken).ConfigureAwait(false);
        if (!await ColumnExistsAsync(connection, "fiscal_years", "period_end_activation").ConfigureAwait(false))
            throw new InvalidOperationException("Period-end activation is unavailable; apply migration 127 first.");
        string dbName;
        await using (var cmd = CreateCommand(connection, null,
            "SELECT database_name FROM fiscal_years WHERE fiscal_year_id = @id"))
        {
            Add(cmd, "@id", last.Id);
            dbName = Convert.ToString(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) ?? string.Empty;
        }
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var off = CreateCommand(connection, transaction,
                "UPDATE fiscal_years SET period_end_activation = @off WHERE fiscal_year_id = @id"))
            {
                Add(off, "@off", Bool(false));
                Add(off, "@id", last.Id);
                if (await off.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new InvalidOperationException("The latest fiscal year could not be deactivated.");
            }
            var newYearId = await InsertFiscalYearAsync(connection, transaction, resolved, newStart, newEnd, dbName, cancellationToken).ConfigureAwait(false);
            await InsertYearPeriodsAsync(connection, transaction, resolved, newYearId, newStart, newEnd, cancellationToken).ConfigureAwait(false);
            if (await TableExistsAsync(connection, "security_audit_log").ConfigureAwait(false))
            {
                await using var audit = CreateCommand(connection, transaction, """
                    INSERT INTO security_audit_log (company_id, user_id, username, event_type, details)
                    VALUES (@company, @user, @user, 'FISCAL_YEAR_ROLL_FORWARD', @details)
                    """);
                Add(audit, "@company", resolved);
                Add(audit, "@user", userId);
                Add(audit, "@details", $"Period-end activation deactivated for fiscal year {last.FiscalYear}; rolled forward into {newStart.Year} ({newStart:yyyy-MM-dd} to {newEnd:yyyy-MM-dd}).");
                await audit.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private readonly record struct PeriodHeader(string CompanyText, string PeriodName, string StartDate, string EndDate, string Status);

    /// <summary>Loads a period header and proves it belongs to the
    /// caller's company (dual-key: stored Guid text or the company code).</summary>
    private async Task<PeriodHeader> LoadPeriodHeaderAsync(DbConnection connection, string companyId, Guid fiscalPeriodId, CancellationToken ct)
    {
        var resolved = await ResolveCompanyIdAsync(connection, companyId, ct).ConfigureAwait(false);
        await using var cmd = CreateCommand(connection, null, """
            SELECT CAST(company_id AS TEXT), period_name, CAST(start_date AS TEXT), CAST(end_date AS TEXT), status
            FROM fiscal_periods
            WHERE CAST(fiscal_period_id AS TEXT) = @period
            """);
        Add(cmd, "@period", fiscalPeriodId.ToString("D"));
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            throw new InvalidOperationException("Fiscal period was not found.");
        var companyText = ReadString(reader, 0) ?? string.Empty;
        if (!string.Equals(companyText, resolved, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(companyText, companyId.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Fiscal period does not belong to the current company.");
        return new PeriodHeader(
            companyText,
            ReadString(reader, 1) ?? string.Empty,
            ReadString(reader, 2) ?? string.Empty,
            ReadString(reader, 3) ?? string.Empty,
            ReadString(reader, 4) ?? string.Empty);
    }

    public async Task<CompressCounts> GetCompressCountsAsync(string companyId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var sales = await CountDeletedDraftsAsync(connection, "sales_documents", companyId, cancellationToken).ConfigureAwait(false);
        var purchase = await CountDeletedDraftsAsync(connection, "purchase_documents", companyId, cancellationToken).ConfigureAwait(false);
        return new CompressCounts(sales, purchase);
    }

    private async Task<int> CountDeletedDraftsAsync(DbConnection connection, string table, string companyId, CancellationToken ct)
    {
        await using var cmd = CreateCommand(connection, null,
            $"SELECT COUNT(*) FROM {table} WHERE company_id = @company AND {Deleted} AND status = 'Draft'");
        Add(cmd, "@company", companyId.Trim());
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    public async Task<int> CompressDeletedDraftsAsync(string companyId, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var auditExists = await TableExistsAsync(connection, "security_audit_log").ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var deleted = 0;
            deleted += await PurgeTableAsync(connection, transaction, "sales_documents", "sales_document_lines", companyId, cancellationToken).ConfigureAwait(false);
            deleted += await PurgeTableAsync(connection, transaction, "purchase_documents", "purchase_document_lines", companyId, cancellationToken).ConfigureAwait(false);

            if (auditExists && deleted > 0)
            {
                await using var audit = CreateCommand(connection, transaction, """
                    INSERT INTO security_audit_log (company_id, user_id, username, event_type, details)
                    VALUES (@company, NULL, @user, 'INVOICE_COMPRESS', @details)
                    """);
                Add(audit, "@company", companyId.Trim());
                Add(audit, "@user", userId);
                Add(audit, "@details", $"Hard-deleted {deleted} soft-deleted draft document(s).");
                await audit.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return deleted;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<int> PurgeTableAsync(DbConnection connection, DbTransaction transaction, string table, string linesTable, string companyId, CancellationToken ct)
    {
        // Lines first (explicit, so the purge does not depend on FK cascade),
        // then the draft headers. The status/Deleted predicate is repeated on
        // both statements: posted documents can never match.
        await using (var lines = CreateCommand(connection, transaction, $"""
            DELETE FROM {linesTable} WHERE doc_id IN
                (SELECT doc_id FROM {table} WHERE company_id = @company AND {Deleted} AND status = 'Draft')
            """))
        {
            Add(lines, "@company", companyId.Trim());
            await lines.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await using var docs = CreateCommand(connection, transaction,
            $"DELETE FROM {table} WHERE company_id = @company AND {Deleted} AND status = 'Draft'");
        Add(docs, "@company", companyId.Trim());
        return await docs.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------ Repost

    public async Task<DocumentTotalsRow?> GetDocumentTotalsAsync(string companyId, string table, string docNo, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        var (docs, lines) = Tables(table);
        if (string.IsNullOrWhiteSpace(docNo))
            throw new ArgumentException("Document number is required.", nameof(docNo));
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        DocumentTotalsRow? row = null;
        await using (var cmd = CreateCommand(connection, null, $"""
            SELECT doc_id, doc_kind, doc_no, CAST(doc_date AS TEXT), status, subtotal, discount_amount, tax_amount, grand_total
            FROM {docs} WHERE company_id = @company AND doc_no = @no AND {NotDeleted}
            ORDER BY doc_id LIMIT 1
            """))
        {
            Add(cmd, "@company", companyId.Trim());
            Add(cmd, "@no", docNo.Trim());
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                row = new DocumentTotalsRow
                {
                    DocId = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                    DocKind = ReadString(reader, 1) ?? string.Empty,
                    DocNo = ReadString(reader, 2) ?? string.Empty,
                    DocDate = ReadString(reader, 3) ?? string.Empty,
                    Status = ReadString(reader, 4) ?? string.Empty,
                    StoredSubtotal = ReadDecimal(reader, 5),
                    StoredDiscount = ReadDecimal(reader, 6),
                    StoredTax = ReadDecimal(reader, 7),
                    StoredGrand = ReadDecimal(reader, 8)
                };
            }
        }
        if (row is null)
            return null;

        await using var linesCmd = CreateCommand(connection, null, $"""
            SELECT COUNT(*),
                   COALESCE(SUM(qty * unit_price), 0),
                   COALESCE(SUM(qty * unit_price * discount_pct / 100.0), 0),
                   COALESCE(SUM(line_tax), 0)
            FROM {lines} WHERE doc_id = @id
            """);
        Add(linesCmd, "@id", row.DocId);
        await using var linesReader = await linesCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await linesReader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            row.LineCount = Convert.ToInt32(linesReader.GetValue(0), CultureInfo.InvariantCulture);
            row.ComputedSubtotal = Math.Round(ReadDecimal(linesReader, 1), 2);
            row.ComputedDiscount = Math.Round(ReadDecimal(linesReader, 2), 2);
            row.ComputedTax = Math.Round(ReadDecimal(linesReader, 3), 2);
            row.ComputedGrand = Math.Round(row.ComputedSubtotal - row.ComputedDiscount + row.ComputedTax, 2);
        }
        return row;
    }

    public async Task ApplyRepostAsync(string companyId, string table, DocumentTotalsRow totals, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(totals);
        var (docs, _) = Tables(table);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var auditExists = await TableExistsAsync(connection, "security_audit_log").ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var cmd = CreateCommand(connection, transaction, $"""
                UPDATE {docs} SET subtotal = @sub, discount_amount = @disc, tax_amount = @tax, grand_total = @grand
                WHERE company_id = @company AND doc_id = @id AND {NotDeleted}
                """))
            {
                Add(cmd, "@sub", totals.ComputedSubtotal);
                Add(cmd, "@disc", totals.ComputedDiscount);
                Add(cmd, "@tax", totals.ComputedTax);
                Add(cmd, "@grand", totals.ComputedGrand);
                Add(cmd, "@company", companyId.Trim());
                Add(cmd, "@id", totals.DocId);
                if (await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new InvalidOperationException("Document could not be reposted (missing or deleted).");
            }
            if (auditExists)
            {
                await using var audit = CreateCommand(connection, transaction, """
                    INSERT INTO security_audit_log (company_id, user_id, username, event_type, details)
                    VALUES (@company, NULL, @user, 'INVOICE_REPOST', @details)
                    """);
                Add(audit, "@company", companyId.Trim());
                Add(audit, "@user", userId);
                Add(audit, "@details", $"Reposted {table} document {totals.DocNo}: grand {totals.StoredGrand} -> {totals.ComputedGrand}.");
                await audit.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<DocumentTotalsRow?> GetMfgOrderTotalsAsync(string companyId, string docNo, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        if (string.IsNullOrWhiteSpace(docNo) || !long.TryParse(docNo.Trim(), out var docNumber))
            throw new ArgumentException("Manufacturing order number must be a whole number.", nameof(docNo));
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (!await TableExistsAsync(connection, "mfg_orders").ConfigureAwait(false))
            return null;

        string orderId = string.Empty;
        decimal overhead = 0m, qty = 0m;
        DocumentTotalsRow? row = null;
        await using (var cmd = CreateCommand(connection, null, $"""
            SELECT CAST(id AS TEXT), CAST(doc_date AS TEXT), status, total_cost, overhead_amount, qty
            FROM mfg_orders WHERE company_id = @company AND doc_no = @no AND {NotDeleted} LIMIT 1
            """))
        {
            Add(cmd, "@company", companyId.Trim());
            Add(cmd, "@no", docNumber);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                orderId = ReadString(reader, 0) ?? string.Empty;
                row = new DocumentTotalsRow
                {
                    DocKind = "MFG",
                    DocNo = docNo.Trim(),
                    DocDate = ReadString(reader, 1) ?? string.Empty,
                    Status = ReadString(reader, 2) ?? string.Empty,
                    StoredSubtotal = ReadDecimal(reader, 3),
                    StoredGrand = ReadDecimal(reader, 3)
                };
                overhead = ReadDecimal(reader, 4);
                qty = ReadDecimal(reader, 5);
            }
        }
        if (row is null)
            return null;

        await using var linesCmd = CreateCommand(connection, null,
            "SELECT COUNT(*), COALESCE(SUM(amount), 0) FROM mfg_order_lines WHERE CAST(order_id AS TEXT) = @id");
        Add(linesCmd, "@id", orderId);
        await using var linesReader = await linesCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await linesReader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            row.LineCount = Convert.ToInt32(linesReader.GetValue(0), CultureInfo.InvariantCulture);
            var computed = Math.Round(ReadDecimal(linesReader, 1) + overhead, 2);
            row.ComputedSubtotal = computed;
            row.ComputedGrand = computed;
            row.StoredDiscount = qty; // carried for unit-cost display in the window (qty, not money)
        }
        return row;
    }

    public async Task ApplyMfgRepostAsync(string companyId, DocumentTotalsRow totals, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(totals);
        if (!long.TryParse(totals.DocNo, out var docNumber))
            throw new ArgumentException("Manufacturing order number must be a whole number.", nameof(totals));
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var auditExists = await TableExistsAsync(connection, "security_audit_log").ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var cmd = CreateCommand(connection, transaction, $"""
                UPDATE mfg_orders
                SET total_cost = @total,
                    unit_cost = CASE WHEN qty > 0 THEN @total / qty ELSE 0 END,
                    updated_by = @user, updated_at = {NowExpr}
                WHERE company_id = @company AND doc_no = @no AND {NotDeleted}
                """))
            {
                Add(cmd, "@total", totals.ComputedGrand);
                Add(cmd, "@user", userId);
                Add(cmd, "@company", companyId.Trim());
                Add(cmd, "@no", docNumber);
                if (await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new InvalidOperationException("Manufacturing order could not be reposted (missing or deleted).");
            }
            if (auditExists)
            {
                await using var audit = CreateCommand(connection, transaction, """
                    INSERT INTO security_audit_log (company_id, user_id, username, event_type, details)
                    VALUES (@company, NULL, @user, 'MFG_REPOST', @details)
                    """);
                Add(audit, "@company", companyId.Trim());
                Add(audit, "@user", userId);
                Add(audit, "@details", $"Reposted manufacturing order {totals.DocNo}: total cost {totals.StoredGrand} -> {totals.ComputedGrand}.");
                await audit.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static (string Docs, string Lines) Tables(string table) => table.Trim().ToLowerInvariant() switch
    {
        "sales" => ("sales_documents", "sales_document_lines"),
        "purchase" => ("purchase_documents", "purchase_document_lines"),
        _ => throw new ArgumentException("Table must be 'sales' or 'purchase'.", nameof(table))
    };

    // ------------------------------------------------------------ Support tickets

    public async Task<SupportTicket> CreateTicketAsync(string companyId, SupportTicket ticket, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(ticket);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int nextNo;
            await using (var max = CreateCommand(connection, transaction,
                "SELECT COALESCE(MAX(ticket_no), 0) FROM support_tickets WHERE company_id = @company"))
            {
                Add(max, "@company", companyId.Trim());
                nextNo = Convert.ToInt32(await max.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) + 1;
            }
            await using var cmd = CreateCommand(connection, transaction, """
                INSERT INTO support_tickets (id, company_id, ticket_no, kind, subject, body, contact_email, status, created_by)
                VALUES (@id, @company, @no, @kind, @subject, @body, @email, 'Open', @user)
                """);
            Add(cmd, "@id", FormatGuid(ticket.Id));
            Add(cmd, "@company", companyId.Trim());
            Add(cmd, "@no", nextNo);
            Add(cmd, "@kind", ticket.Kind);
            Add(cmd, "@subject", ticket.Subject.Trim());
            Add(cmd, "@body", ticket.Body.Trim());
            Add(cmd, "@email", (object?)NullIfBlank(ticket.ContactEmail) ?? DBNull.Value);
            Add(cmd, "@user", userId);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            ticket.TicketNo = nextNo;
            ticket.Status = "Open";
            return ticket;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IReadOnlyList<SupportTicket>> GetTicketsAsync(string companyId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = CreateCommand(connection, null, $"""
            SELECT id, ticket_no, kind, subject, body, contact_email, status, CAST(created_at AS TEXT)
            FROM support_tickets WHERE company_id = @company ORDER BY ticket_no DESC LIMIT {ListLimit}
            """);
        Add(cmd, "@company", companyId.Trim());
        var result = new List<SupportTicket>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new SupportTicket
            {
                Id = ReadGuid(reader, 0),
                TicketNo = Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture),
                Kind = ReadString(reader, 2) ?? SupportTicketKinds.Support,
                Subject = ReadString(reader, 3) ?? string.Empty,
                Body = ReadString(reader, 4) ?? string.Empty,
                ContactEmail = ReadString(reader, 5),
                Status = ReadString(reader, 6) ?? "Open",
                CreatedAt = ReadString(reader, 7) ?? string.Empty
            });
        }
        return result;
    }

    // ------------------------------------------------------------ Helpers

    private async Task<string> ResolveCompanyIdAsync(DbConnection connection, string companyCode, CancellationToken ct)
    {
        try
        {
            await using var cmd = CreateCommand(connection, null,
                "SELECT CAST(company_id AS TEXT) FROM companies WHERE company_code = @code LIMIT 1");
            Add(cmd, "@code", companyCode.Trim());
            var value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            var text = Convert.ToString(value, CultureInfo.InvariantCulture);
            return string.IsNullOrWhiteSpace(text) ? companyCode.Trim() : text!;
        }
        catch
        {
            return companyCode.Trim();
        }
    }

    private async Task<bool> TableExistsAsync(DbConnection connection, string table)
    {
        await using var cmd = CreateCommand(connection, null, IsPg
            ? "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = @t"
            : "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @t");
        Add(cmd, "@t", table);
        var value = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture) > 0;
    }

    private async Task<bool> ColumnExistsAsync(DbConnection connection, string table, string column)
    {
        await using var cmd = CreateCommand(connection, null, IsPg
            ? "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = 'public' AND table_name = @t AND column_name = @c"
            : "SELECT COUNT(*) FROM pragma_table_info(@t) WHERE name = @c");
        Add(cmd, "@t", table);
        Add(cmd, "@c", column);
        var value = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture) > 0;
    }

    private static async Task<long> ScalarLongAsync(DbConnection connection, string sql, CancellationToken ct)
    {
        await using var cmd = CreateCommand(connection, null, sql);
        var value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static void RequireCompany(string companyId)
    {
        if (string.IsNullOrWhiteSpace(companyId))
            throw new ArgumentException("Company is required.", nameof(companyId));
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string ShortDate(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : (value.Length >= 10 ? value[..10] : value);

    private DbConnection CreateConnection() => IsPg
        ? new NpgsqlConnection(_connectionString)
        : new SqliteConnection(_connectionString);

    private static DbCommand CreateCommand(DbConnection connection, DbTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        if (transaction != null)
            command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private object FormatGuid(Guid value) => IsPg ? value : value.ToString();

    private static Guid ReadGuid(DbDataReader reader, int ordinal)
    {
        var value = reader.GetValue(ordinal);
        return value switch
        {
            Guid g => g,
            string s when Guid.TryParse(s, out var parsed) => parsed,
            _ => Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!)
        };
    }

    private static string? ReadString(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static decimal ReadDecimal(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? 0m : Convert.ToDecimal(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static bool ReadBool(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return false;
        var value = reader.GetValue(ordinal);
        return value switch
        {
            bool b => b,
            string s => s is "1" or "true" or "True" or "t",
            _ => Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0
        };
    }

    private static DateOnly? ReadDate(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return null;
        var value = reader.GetValue(ordinal);
        return value switch
        {
            DateOnly d => d,
            DateTime dt => DateOnly.FromDateTime(dt),
            string s when s.Length >= 10 && DateOnly.TryParse(s[..10], CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    private static string NormalizeProvider(string provider) => provider.Trim().ToLowerInvariant() switch
    {
        "sqlite" or "sqlite3" => "SQLite",
        "postgres" or "postgresql" or "npgsql" => "PostgreSQL",
        _ => throw new NotSupportedException($"Unsupported database provider '{provider}'. Use SQLite or PostgreSQL.")
    };
}
