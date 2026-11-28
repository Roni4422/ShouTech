using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.Infrastructure;

/// <summary>
/// Provider-neutral Cheque persistence (SQLite + PostgreSQL): parameterized SQL
/// only, company scoping on every statement, single-transaction writes.
/// Lifecycle updates are compare-and-swap on the stored status so a stale
/// caller can never transition a cheque twice.
/// </summary>
public sealed class ChequeRepository : IChequeRepository
{
    private const string Columns =
        "id, direction, cheque_no, bank_name, branch_name, drawer_name, party_account_code, party_name, " +
        "amount, currency_code, exchange_rate, issue_date, due_date, status, portfolio_account_code, " +
        "deposit_bank_account_code, deposit_date, clearance_date, endorsed_to_account_code, bounce_date, " +
        "bounce_reason, fees_amount, journal_entry_id, notes, batch_doc_no, cancel_reason";

    private readonly string _provider;
    private readonly string _connectionString;

    public ChequeRepository(string provider, string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("A database connection string is required.", nameof(connectionString));
        _provider = NormalizeProvider(provider);
        _connectionString = connectionString;
    }

    internal string Provider => _provider;
    internal string ConnectionString => _connectionString;

    private string NotDeleted => _provider == "PostgreSQL" ? "is_deleted = false" : "is_deleted = 0";
    private string NowExpr => _provider == "PostgreSQL" ? "now()" : "datetime('now')";

    public async Task<IReadOnlyList<Cheque>> GetAllAsync(string companyId, string? status = null, string? direction = null, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var sql = $"SELECT {Columns} FROM cheques WHERE company_id = @company AND {NotDeleted}";
        if (!string.IsNullOrWhiteSpace(status)) sql += " AND status = @status";
        if (!string.IsNullOrWhiteSpace(direction)) sql += " AND direction = @direction";
        // No row cap (standing unlimited-rows rule; CHQ-13: the old
        // LIMIT 1000 silently truncated the register and its totals).
        sql += " ORDER BY due_date, cheque_no";
        await using var command = CreateCommand(connection, null, sql);
        Add(command, "@company", companyId.Trim());
        if (!string.IsNullOrWhiteSpace(status)) Add(command, "@status", status.Trim());
        if (!string.IsNullOrWhiteSpace(direction)) Add(command, "@direction", direction.Trim());
        var result = new List<Cheque>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(ReadCheque(reader));
        return result;
    }

    public async Task<Cheque?> GetByIdAsync(string companyId, Guid chequeId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        if (chequeId == Guid.Empty) throw new ArgumentException("Cheque is required.", nameof(chequeId));
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            $"SELECT {Columns} FROM cheques WHERE company_id = @company AND id = @id AND {NotDeleted}");
        Add(command, "@company", companyId.Trim());
        Add(command, "@id", FormatGuid(chequeId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadCheque(reader) : null;
    }

    public async Task InsertBatchAsync(string companyId, IReadOnlyList<Cheque> cheques, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(cheques);
        if (cheques.Count == 0) throw new InvalidOperationException("At least one cheque is required.");
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var cheque in cheques)
            {
                // Duplicate guard inside the transaction (the partial unique
                // index ux_cheques_company_bank_drawer_no is the second line).
                await using var dup = CreateCommand(connection, transaction,
                    $"SELECT COUNT(*) FROM cheques WHERE company_id = @company AND bank_name = @bank AND COALESCE(drawer_name,'') = COALESCE(@drawer,'') AND cheque_no = @no AND {NotDeleted}");
                Add(dup, "@company", companyId.Trim());
                Add(dup, "@bank", cheque.BankName.Trim());
                Add(dup, "@drawer", (object?)NullIfBlank(cheque.DrawerName) ?? DBNull.Value);
                Add(dup, "@no", cheque.ChequeNo.Trim());
                if (Convert.ToInt64(await dup.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) > 0)
                    throw new InvalidOperationException(
                        $"Cheque '{cheque.ChequeNo.Trim()}' from drawer '{cheque.DrawerName}' at bank '{cheque.BankName.Trim()}' already exists for this company.");

                await using var insert = CreateCommand(connection, transaction,
                    $"INSERT INTO cheques ({Columns}, company_id, created_by) VALUES (@id, @direction, @no, @bank, @branch, @drawer, @partyAcc, @partyName, @amount, @currency, @rate, @issue, @due, @status, @portfolio, @depositBank, @depositDate, @clearance, @endorsed, @bounceDate, @bounceReason, @fees, @journal, @notes, @batchDocNo, @cancelReason, @company, @user)");
                Bind(insert, companyId, cheque, userId);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task UpdateTransitionAsync(string companyId, Cheque cheque, string expectedStatus, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(cheque);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-validate the current status inside the transaction.
            string? current;
            await using (var check = CreateCommand(connection, transaction,
                $"SELECT status FROM cheques WHERE company_id = @company AND id = @id AND {NotDeleted}"))
            {
                Add(check, "@company", companyId.Trim());
                Add(check, "@id", FormatGuid(cheque.Id));
                var value = await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                current = value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
            }
            if (current is null)
                throw new InvalidOperationException("Cheque was not found for this company.");
            if (!string.Equals(current, expectedStatus, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Cheque status changed: expected '{expectedStatus}' but the stored status is '{current}'. Refresh and retry.");

            await using var update = CreateCommand(connection, transaction,
                $"UPDATE cheques SET status = @status, deposit_bank_account_code = @depositBank, deposit_date = @depositDate, " +
                $"clearance_date = @clearance, endorsed_to_account_code = @endorsed, bounce_date = @bounceDate, " +
                $"bounce_reason = @bounceReason, fees_amount = @fees, journal_entry_id = @journal, notes = @notes, cancel_reason = @cancelReason, " +
                $"updated_by = @user, updated_at = {NowExpr} " +
                $"WHERE company_id = @company AND id = @id AND status = @expected AND {NotDeleted}");
            Add(update, "@status", cheque.Status);
            Add(update, "@depositBank", (object?)NullIfBlank(cheque.DepositBankAccountCode) ?? DBNull.Value);
            Add(update, "@depositDate", (object?)FormatDate(cheque.DepositDate) ?? DBNull.Value);
            Add(update, "@clearance", (object?)FormatDate(cheque.ClearanceDate) ?? DBNull.Value);
            Add(update, "@endorsed", (object?)NullIfBlank(cheque.EndorsedToAccountCode) ?? DBNull.Value);
            Add(update, "@bounceDate", (object?)FormatDate(cheque.BounceDate) ?? DBNull.Value);
            Add(update, "@bounceReason", (object?)NullIfBlank(cheque.BounceReason) ?? DBNull.Value);
            Add(update, "@fees", cheque.FeesAmount);
            Add(update, "@journal", (object?)(cheque.JournalEntryId?.ToString("D")) ?? DBNull.Value);
            Add(update, "@notes", (object?)NullIfBlank(cheque.Notes) ?? DBNull.Value);
            Add(update, "@cancelReason", (object?)NullIfBlank(cheque.CancelReason) ?? DBNull.Value);
            Add(update, "@user", userId);
            Add(update, "@company", companyId.Trim());
            Add(update, "@id", FormatGuid(cheque.Id));
            Add(update, "@expected", expectedStatus);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("Cheque could not be updated (missing, deleted, or status changed).");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<Guid> ResolveCompanyGuidAsync(string companyCode, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(companyCode))
            throw new ArgumentException("Company code is required.", nameof(companyCode));
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            "SELECT company_id FROM companies WHERE company_code = @code AND is_active = " +
            (_provider == "PostgreSQL" ? "true" : "1") + " AND is_deleted = " + (_provider == "PostgreSQL" ? "false" : "0"));
        Add(command, "@code", companyCode.Trim());
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is null or DBNull || !Guid.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var companyId) || companyId == Guid.Empty)
            throw new InvalidOperationException($"Active company '{companyCode.Trim()}' could not be resolved to a valid database company ID.");
        return companyId;
    }

    public async Task<Guid> ResolveAccountIdAsync(Guid companyGuid, string accountCode, CancellationToken cancellationToken = default)
    {
        if (companyGuid == Guid.Empty) throw new ArgumentException("Company is required.", nameof(companyGuid));
        if (string.IsNullOrWhiteSpace(accountCode)) throw new ArgumentException("Account code is required.", nameof(accountCode));
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var activePredicate = _provider == "PostgreSQL"
            ? "is_active = true AND is_deleted = false AND is_postable = true"
            : "is_active = 1 AND is_deleted = 0 AND is_postable = 1";
        await using var command = CreateCommand(connection, null,
            $"SELECT account_id FROM chart_of_accounts WHERE company_id = @company AND account_code = @code AND {activePredicate}");
        Add(command, "@company", FormatGuid(companyGuid));
        Add(command, "@code", accountCode.Trim());
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is null or DBNull || !Guid.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var accountId) || accountId == Guid.Empty)
            throw new InvalidOperationException($"Account code '{accountCode.Trim()}' is missing, inactive, deleted, or not postable for this company.");
        return accountId;
    }

    private void Bind(DbCommand command, string companyId, Cheque cheque, string userId)
    {
        Add(command, "@id", FormatGuid(cheque.Id));
        Add(command, "@company", companyId.Trim());
        Add(command, "@direction", cheque.Direction);
        Add(command, "@no", cheque.ChequeNo.Trim());
        Add(command, "@bank", cheque.BankName.Trim());
        Add(command, "@branch", (object?)NullIfBlank(cheque.BranchName) ?? DBNull.Value);
        Add(command, "@drawer", (object?)NullIfBlank(cheque.DrawerName) ?? DBNull.Value);
        Add(command, "@partyAcc", (object?)NullIfBlank(cheque.PartyAccountCode) ?? DBNull.Value);
        Add(command, "@partyName", (object?)NullIfBlank(cheque.PartyName) ?? DBNull.Value);
        Add(command, "@amount", cheque.Amount);
        Add(command, "@currency", string.IsNullOrWhiteSpace(cheque.CurrencyCode) ? "SAR" : cheque.CurrencyCode.Trim().ToUpperInvariant());
        Add(command, "@rate", cheque.ExchangeRate <= 0 ? 1m : cheque.ExchangeRate);
        Add(command, "@issue", (object?)FormatDate(cheque.IssueDate) ?? DBNull.Value);
        Add(command, "@due", FormatDate(cheque.DueDate));
        Add(command, "@status", cheque.Status);
        Add(command, "@portfolio", (object?)NullIfBlank(cheque.PortfolioAccountCode) ?? DBNull.Value);
        Add(command, "@depositBank", (object?)NullIfBlank(cheque.DepositBankAccountCode) ?? DBNull.Value);
        Add(command, "@depositDate", (object?)FormatDate(cheque.DepositDate) ?? DBNull.Value);
        Add(command, "@clearance", (object?)FormatDate(cheque.ClearanceDate) ?? DBNull.Value);
        Add(command, "@endorsed", (object?)NullIfBlank(cheque.EndorsedToAccountCode) ?? DBNull.Value);
        Add(command, "@bounceDate", (object?)FormatDate(cheque.BounceDate) ?? DBNull.Value);
        Add(command, "@bounceReason", (object?)NullIfBlank(cheque.BounceReason) ?? DBNull.Value);
        Add(command, "@fees", cheque.FeesAmount);
        Add(command, "@journal", (object?)(cheque.JournalEntryId?.ToString("D")) ?? DBNull.Value);
        Add(command, "@notes", (object?)NullIfBlank(cheque.Notes) ?? DBNull.Value);
        Add(command, "@batchDocNo", (object?)NullIfBlank(cheque.BatchDocNo) ?? DBNull.Value);
        Add(command, "@cancelReason", (object?)NullIfBlank(cheque.CancelReason) ?? DBNull.Value);
        Add(command, "@user", userId);
    }

    public async Task<int> CountByJournalEntryAsync(string companyId, Guid journalEntryId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        if (journalEntryId == Guid.Empty) throw new ArgumentException("Journal entry is required.", nameof(journalEntryId));
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            $"SELECT COUNT(*) FROM cheques WHERE company_id = @company AND journal_entry_id = @journal AND {NotDeleted}");
        Add(command, "@company", companyId.Trim());
        Add(command, "@journal", journalEntryId.ToString("D"));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static Cheque ReadCheque(DbDataReader reader) => new()
    {
        Id = ReadGuid(reader, 0),
        Direction = ReadString(reader, 1) ?? ChequeDirections.In,
        ChequeNo = ReadString(reader, 2) ?? string.Empty,
        BankName = ReadString(reader, 3) ?? string.Empty,
        BranchName = ReadString(reader, 4),
        DrawerName = ReadString(reader, 5),
        PartyAccountCode = ReadString(reader, 6),
        PartyName = ReadString(reader, 7),
        Amount = ReadDecimal(reader, 8),
        CurrencyCode = ReadString(reader, 9) ?? "SAR",
        ExchangeRate = ReadDecimal(reader, 10),
        IssueDate = ReadDate(reader, 11),
        DueDate = ReadDate(reader, 12) ?? DateOnly.FromDateTime(DateTime.Today),
        Status = ReadString(reader, 13) ?? ChequeStatuses.InPortfolio,
        PortfolioAccountCode = ReadString(reader, 14),
        DepositBankAccountCode = ReadString(reader, 15),
        DepositDate = ReadDate(reader, 16),
        ClearanceDate = ReadDate(reader, 17),
        EndorsedToAccountCode = ReadString(reader, 18),
        BounceDate = ReadDate(reader, 19),
        BounceReason = ReadString(reader, 20),
        FeesAmount = ReadDecimal(reader, 21),
        JournalEntryId = Guid.TryParse(ReadString(reader, 22), out var j) ? j : null,
        Notes = ReadString(reader, 23),
        BatchDocNo = ReadString(reader, 24),
        CancelReason = ReadString(reader, 25)
    };

    private static void RequireCompany(string companyId)
    {
        if (string.IsNullOrWhiteSpace(companyId))
            throw new ArgumentException("Company is required.", nameof(companyId));
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string FormatDate(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string? FormatDate(DateOnly? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private DbConnection CreateConnection() => _provider == "SQLite"
        ? new SqliteConnection(_connectionString)
        : new NpgsqlConnection(_connectionString);

    private static DbCommand CreateCommand(DbConnection connection, DbTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        if (transaction != null) command.Transaction = transaction;
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

    private object FormatGuid(Guid value) => _provider == "PostgreSQL" ? value : value.ToString("D");

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

    private static DateOnly? ReadDate(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
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
