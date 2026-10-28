using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.Infrastructure;

/// <summary>Provider-neutral journal persistence for the supported SQLite and PostgreSQL engines.</summary>
public sealed class JournalRepository : IJournalRepository
{
    private readonly string _provider;
    private readonly string _connectionString;

    public JournalRepository(string provider, string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("A database connection string is required.", nameof(connectionString));
        _provider = NormalizeProvider(provider);
        _connectionString = connectionString;
    }

    public async Task<Guid> ResolveCompanyIdAsync(string companyCode, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(companyCode))
            throw new ArgumentException("Company code is required.", nameof(companyCode));

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT company_id FROM companies WHERE company_code=@code AND is_active=" + (_provider == "PostgreSQL" ? "true" : "1") + " AND is_deleted=" + (_provider == "PostgreSQL" ? "false" : "0");
        Add(command, "@code", companyCode.Trim());
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is null or DBNull || !Guid.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var companyId) || companyId == Guid.Empty)
            throw new InvalidOperationException($"Active company '{companyCode.Trim()}' could not be resolved to a valid database company ID.");
        return companyId;
    }

    public async Task<Guid> ResolveAccountIdAsync(Guid companyId, string accountCode, CancellationToken cancellationToken = default)
    {
        if (companyId == Guid.Empty) throw new ArgumentException("Company is required.", nameof(companyId));
        if (string.IsNullOrWhiteSpace(accountCode)) throw new ArgumentException("Account code is required.", nameof(accountCode));

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var activePredicate = _provider == "PostgreSQL"
            ? "is_active=true AND is_deleted=false AND is_postable=true"
            : "is_active=1 AND is_deleted=0 AND is_postable=1";
        command.CommandText = $"SELECT account_id FROM chart_of_accounts WHERE company_id=@company AND account_code=@code AND {activePredicate}";
        Add(command, "@company", FormatGuid(companyId));
        Add(command, "@code", accountCode.Trim());
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is null or DBNull || !Guid.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var accountId) || accountId == Guid.Empty)
            throw new InvalidOperationException($"Account code '{accountCode.Trim()}' is missing, inactive, deleted, or not postable for this company.");
        return accountId;
    }

    public async Task<string> ResolveAccountNameAsync(Guid companyId, string accountCode, CancellationToken cancellationToken = default)
    {
        if (companyId == Guid.Empty) throw new ArgumentException("Company is required.", nameof(companyId));
        if (string.IsNullOrWhiteSpace(accountCode)) throw new ArgumentException("Account code is required.", nameof(accountCode));

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var activePredicate = _provider == "PostgreSQL"
            ? "is_active=true AND is_deleted=false AND is_postable=true"
            : "is_active=1 AND is_deleted=0 AND is_postable=1";
        command.CommandText = $"SELECT account_name FROM chart_of_accounts WHERE company_id=@company AND account_code=@code AND {activePredicate}";
        Add(command, "@company", FormatGuid(companyId));
        Add(command, "@code", accountCode.Trim());
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is null or DBNull || string.IsNullOrWhiteSpace(Convert.ToString(value, CultureInfo.InvariantCulture)))
            throw new InvalidOperationException($"Account code '{accountCode.Trim()}' could not be resolved to an active, postable account for this company.");
        return Convert.ToString(value, CultureInfo.InvariantCulture)!;
    }

    public async Task<JournalEntrySnapshot?> FindByDocumentNumberAsync(
        Guid companyId,
        string documentNumber,
        CancellationToken cancellationToken = default)
    {
        if (companyId == Guid.Empty) throw new ArgumentException("Company is required.", nameof(companyId));
        if (string.IsNullOrWhiteSpace(documentNumber)) throw new ArgumentException("Document number is required.", nameof(documentNumber));

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        Guid journalId;
        DateOnly entryDate;
        string companyIdText;
        string description;
        int statusValue;
        string? referenceNumber;
        string currencyCode;
        decimal exchangeRate;
        string? storedDocumentNumber;

        await using (var header = connection.CreateCommand())
        {
            header.CommandText = """
                SELECT journal_entry_id, company_id, entry_date, description, status,
                       reference_number, currency_code, exchange_rate, document_number
                FROM journal_entries
                WHERE company_id = @company AND document_number = @documentNumber
                """;
            Add(header, "@company", FormatGuid(companyId));
            Add(header, "@documentNumber", documentNumber.Trim());

            await using var reader = await header.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return null;

            if (!Guid.TryParse(Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture), out journalId) ||
                journalId == Guid.Empty)
                throw new InvalidOperationException("The saved journal has an invalid identifier.");
            companyIdText = Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) ?? string.Empty;
            entryDate = _provider == "PostgreSQL"
                ? reader.GetFieldValue<DateOnly>(2)
                : DateOnly.Parse(Convert.ToString(reader.GetValue(2), CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture);
            description = reader.GetString(3);
            statusValue = Convert.ToInt32(reader.GetValue(4), CultureInfo.InvariantCulture);
            referenceNumber = reader.IsDBNull(5) ? null : reader.GetString(5);
            currencyCode = reader.GetString(6);
            exchangeRate = Convert.ToDecimal(reader.GetValue(7), CultureInfo.InvariantCulture);
            storedDocumentNumber = reader.IsDBNull(8) ? null : reader.GetString(8);

            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException(
                    $"More than one journal uses document number '{documentNumber.Trim()}' for this company. Resolve the duplicate records before opening this journal.");
        }

        if (!Guid.TryParse(companyIdText, out var storedCompanyId) || storedCompanyId != companyId)
            throw new InvalidOperationException("The saved journal company does not match the requested company.");
        if (!Enum.IsDefined(typeof(JournalEntryStatus), statusValue))
            throw new InvalidOperationException($"The saved journal has an unknown status value '{statusValue}'.");

        var linesResult = new List<JournalEntryLineSnapshot>();
        await using (var lines = connection.CreateCommand())
        {
            lines.CommandText = """
                SELECT l.line_number, l.account_id, a.account_code, a.account_name,
                       l.debit, l.credit, l.cost_center, l.note
                FROM journal_entry_lines l
                LEFT JOIN chart_of_accounts a
                  ON a.account_id = l.account_id AND a.company_id = @company
                WHERE l.journal_entry_id = @journalId
                ORDER BY l.line_number
                """;
            Add(lines, "@company", FormatGuid(companyId));
            Add(lines, "@journalId", FormatGuid(journalId));
            await using var reader = await lines.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.IsDBNull(2) || reader.IsDBNull(3))
                    throw new InvalidOperationException(
                        $"Journal line {Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture)} refers to an account missing from the selected company.");

                if (!Guid.TryParse(Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture), out var accountId) ||
                    accountId == Guid.Empty)
                    throw new InvalidOperationException("A saved journal line contains an invalid account identifier.");

                linesResult.Add(new JournalEntryLineSnapshot(
                    Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture),
                    accountId,
                    reader.GetString(2),
                    reader.GetString(3),
                    Convert.ToDecimal(reader.GetValue(4), CultureInfo.InvariantCulture),
                    Convert.ToDecimal(reader.GetValue(5), CultureInfo.InvariantCulture),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7)));
            }
        }

        if (linesResult.Count < 2)
            throw new InvalidOperationException("The saved journal contains fewer than two lines; its integrity must be reviewed.");

        return new JournalEntrySnapshot(
            journalId,
            storedCompanyId,
            entryDate,
            description,
            storedDocumentNumber,
            referenceNumber,
            currencyCode,
            exchangeRate,
            (JournalEntryStatus)statusValue,
            linesResult.AsReadOnly());
    }

    public async Task SaveDraftAsync(Guid companyId, JournalEntry entry, string userId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (companyId == Guid.Empty) throw new ArgumentException("Company is required.", nameof(companyId));
        if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("User is required.", nameof(userId));
        entry.EnsureBalanced();

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            object entryDateValue = _provider == "PostgreSQL"
                ? entry.EntryDate
                : entry.EntryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string periodId;
            await using (var period = CreateCommand(connection, transaction,
                "SELECT fp.fiscal_period_id FROM fiscal_periods fp JOIN fiscal_years fy ON fy.fiscal_year_id=fp.fiscal_year_id WHERE fp.company_id=@company AND @date BETWEEN fp.start_date AND fp.end_date AND fp.status='OPEN' AND fy.is_closed=" + (_provider == "PostgreSQL" ? "false" : "0")))
            {
                Add(period, "@company", FormatGuid(companyId));
                Add(period, "@date", entryDateValue);
                var value = await period.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (value is null || value is DBNull)
                    throw new InvalidOperationException("No open fiscal period matches the journal date and company.");
                periodId = Convert.ToString(value, CultureInfo.InvariantCulture)!;
            }

            var activePredicate = _provider == "PostgreSQL" ? "a.is_active = true AND a.is_deleted = false AND a.is_postable = true" : "a.is_active = 1 AND a.is_deleted = 0 AND a.is_postable = 1";
            foreach (var line in entry.Lines)
            {
                await using var accountCheck = CreateCommand(connection, transaction,
                    $"SELECT COUNT(*) FROM chart_of_accounts a WHERE a.account_id=@account AND a.company_id=@company AND {activePredicate}");
                Add(accountCheck, "@account", FormatGuid(line.AccountId));
                Add(accountCheck, "@company", FormatGuid(companyId));
                if (Convert.ToInt64(await accountCheck.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 1)
                    throw new InvalidOperationException($"Account '{line.AccountId}' is missing, inactive, deleted, or not postable for this company.");
            }

            await using (var command = CreateCommand(connection, transaction,
                "INSERT INTO journal_entries (journal_entry_id, company_id, entry_date, description, status, created_by, fiscal_period_id, reference_number, currency_code, exchange_rate, document_number) VALUES (@id, @company, @date, @description, @status, @actor, @period, @reference, @currency, @rate, @documentNumber)"))
            {
                Add(command, "@id", FormatGuid(entry.Id));
                Add(command, "@company", FormatGuid(companyId));
                Add(command, "@date", entryDateValue);
                Add(command, "@description", entry.Description);
                Add(command, "@status", (int)JournalEntryStatus.Draft);
                Add(command, "@actor", userId.Trim());
                Add(command, "@period", _provider == "PostgreSQL" ? (object)Guid.Parse(periodId) : periodId);
                Add(command, "@reference", (object?)entry.ReferenceNumber ?? DBNull.Value);
                Add(command, "@currency", entry.CurrencyCode);
                Add(command, "@rate", entry.ExchangeRate);
                Add(command, "@documentNumber", (object?)entry.DocumentNumber ?? DBNull.Value);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var lineNumber = 0;
            foreach (var line in entry.Lines)
            {
                await using var command = CreateCommand(connection, transaction,
                    "INSERT INTO journal_entry_lines (journal_entry_id, line_number, account_id, debit, credit, cost_center, note) VALUES (@id, @line, @account, @debit, @credit, @costCenter, @note)");
                Add(command, "@id", FormatGuid(entry.Id));
                Add(command, "@line", ++lineNumber);
                Add(command, "@account", FormatGuid(line.AccountId));
                Add(command, "@debit", line.Debit);
                Add(command, "@credit", line.Credit);
                Add(command, "@costCenter", (object?)line.CostCenter ?? DBNull.Value);
                Add(command, "@note", (object?)line.Note ?? DBNull.Value);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await AddAuditAsync(connection, transaction, entry.Id, 1, userId, "Draft created", cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task PostAsync(Guid journalEntryId, string userId, CancellationToken cancellationToken = default)
    {
        if (journalEntryId == Guid.Empty) throw new ArgumentException("Journal entry is required.", nameof(journalEntryId));
        if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("User is required.", nameof(userId));
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var check = CreateCommand(connection, transaction,
                "SELECT COUNT(*), COALESCE(SUM(debit), 0), COALESCE(SUM(credit), 0) FROM journal_entry_lines WHERE journal_entry_id = @id"))
            {
                Add(check, "@id", FormatGuid(journalEntryId));
                await using var reader = await check.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) throw new InvalidOperationException("Unable to validate journal lines.");
                var count = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
                var debit = Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture);
                var credit = Convert.ToDecimal(reader.GetValue(2), CultureInfo.InvariantCulture);
                if (count < 2 || debit <= 0 || debit != credit)
                    throw new InvalidOperationException("Only a balanced journal entry with at least two lines can be posted.");
            }

            await using (var update = CreateCommand(connection, transaction,
                "UPDATE journal_entries SET status = @posted, posted_by = @actor, posted_at = CURRENT_TIMESTAMP WHERE journal_entry_id = @id AND status = @draft"))
            {
                Add(update, "@posted", (int)JournalEntryStatus.Posted);
                Add(update, "@actor", userId.Trim());
                Add(update, "@id", FormatGuid(journalEntryId));
                Add(update, "@draft", (int)JournalEntryStatus.Draft);
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new InvalidOperationException("Journal entry was not found or is not in draft status.");
            }
            await AddAuditAsync(connection, transaction, journalEntryId, 2, userId, "Journal posted", cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<Guid> ReverseAsync(Guid journalEntryId, string userId, string? details = null, CancellationToken cancellationToken = default)
    {
        if (journalEntryId == Guid.Empty) throw new ArgumentException("Journal entry is required.", nameof(journalEntryId));
        if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("User is required.", nameof(userId));

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string companyId;
            DateOnly entryDate;
            string description;
            string periodId;
            string? referenceNumber;
            string? documentNumber;
            string currencyCode;
            decimal exchangeRate;
            int status;
            await using (var header = CreateCommand(connection, transaction,
                "SELECT company_id, entry_date, description, status, fiscal_period_id, reference_number, currency_code, exchange_rate, document_number FROM journal_entries WHERE journal_entry_id = @id"))
            {
                Add(header, "@id", FormatGuid(journalEntryId));
                await using var reader = await header.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("Journal entry was not found.");
                companyId = Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!;
                entryDate = _provider == "PostgreSQL"
                    ? reader.GetFieldValue<DateOnly>(1)
                    : DateOnly.Parse(Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture);
                description = reader.GetString(2);
                status = Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture);
                periodId = reader.IsDBNull(4) ? string.Empty : Convert.ToString(reader.GetValue(4), CultureInfo.InvariantCulture)!;
                referenceNumber = reader.IsDBNull(5) ? null : reader.GetString(5);
                currencyCode = reader.GetString(6);
                exchangeRate = Convert.ToDecimal(reader.GetValue(7), CultureInfo.InvariantCulture);
                documentNumber = reader.IsDBNull(8) ? null : reader.GetString(8);
            }
            if (status != (int)JournalEntryStatus.Posted)
                throw new InvalidOperationException("Only an existing posted journal entry can be reversed.");
            if (string.IsNullOrWhiteSpace(periodId))
                throw new InvalidOperationException("The source journal has no fiscal period. Assign it to a valid period before reversal.");
            var openPredicate = _provider == "PostgreSQL" ? "fy.is_closed = false" : "fy.is_closed = 0";
            await using (var periodCheck = CreateCommand(connection, transaction,
                "SELECT COUNT(*) FROM fiscal_periods fp JOIN fiscal_years fy ON fy.fiscal_year_id=fp.fiscal_year_id WHERE fp.fiscal_period_id=@period AND fp.company_id=@company AND fp.status='OPEN' AND " + openPredicate))
            {
                Add(periodCheck, "@period", _provider == "PostgreSQL" ? (object)Guid.Parse(periodId) : periodId);
                Add(periodCheck, "@company", _provider == "PostgreSQL" ? (object)Guid.Parse(companyId) : companyId);
                if (Convert.ToInt64(await periodCheck.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 1)
                    throw new InvalidOperationException("The original fiscal period is closed. Reversal in a closed period is blocked; use an approved open-period adjustment workflow.");
            }

            var sourceLines = new List<(string AccountId, decimal Debit, decimal Credit, string? CostCenter, string? Note)>();
            await using (var lines = CreateCommand(connection, transaction,
                "SELECT account_id, debit, credit, cost_center, note FROM journal_entry_lines WHERE journal_entry_id = @id ORDER BY line_number"))
            {
                Add(lines, "@id", FormatGuid(journalEntryId));
                await using var reader = await lines.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    sourceLines.Add((Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!,
                        Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture),
                        Convert.ToDecimal(reader.GetValue(2), CultureInfo.InvariantCulture),
                        reader.IsDBNull(3) ? null : reader.GetString(3),
                        reader.IsDBNull(4) ? null : reader.GetString(4)));
                }
            }
            if (sourceLines.Count < 2 || sourceLines.Sum(x => x.Debit) <= 0 || sourceLines.Sum(x => x.Debit) != sourceLines.Sum(x => x.Credit))
                throw new InvalidOperationException("The source journal is not balanced and cannot be reversed safely.");

            var reversalId = Guid.NewGuid();
            var reversalDescription = $"Reversal: {description}";
            if (reversalDescription.Length > 500) reversalDescription = reversalDescription[..500];
            await using (var insert = CreateCommand(connection, transaction,
                "INSERT INTO journal_entries (journal_entry_id, company_id, entry_date, description, status, created_by, reversal_of_entry_id, fiscal_period_id, reference_number, currency_code, exchange_rate, document_number) VALUES (@id, @company, @date, @description, @draft, @actor, @source, @period, @reference, @currency, @rate, @documentNumber)"))
            {
                Add(insert, "@id", FormatGuid(reversalId));
                Add(insert, "@company", _provider == "PostgreSQL" ? (object)Guid.Parse(companyId) : companyId);
                Add(insert, "@date", _provider == "PostgreSQL" ? (object)entryDate : entryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                Add(insert, "@description", reversalDescription);
                Add(insert, "@draft", (int)JournalEntryStatus.Draft);
                Add(insert, "@actor", userId.Trim());
                Add(insert, "@source", FormatGuid(journalEntryId));
                Add(insert, "@period", _provider == "PostgreSQL" ? (object)Guid.Parse(periodId) : periodId);
                Add(insert, "@reference", (object?)referenceNumber ?? DBNull.Value);
                Add(insert, "@currency", currencyCode);
                Add(insert, "@rate", exchangeRate);
                Add(insert, "@documentNumber", $"REV-{reversalId.ToString("N")[..12]}");
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var lineNumber = 0;
            foreach (var line in sourceLines)
            {
                await using var insertLine = CreateCommand(connection, transaction,
                    "INSERT INTO journal_entry_lines (journal_entry_id, line_number, account_id, debit, credit, cost_center, note) VALUES (@id, @line, @account, @debit, @credit, @costCenter, @note)");
                Add(insertLine, "@id", FormatGuid(reversalId));
                Add(insertLine, "@line", ++lineNumber);
                Add(insertLine, "@account", _provider == "PostgreSQL" ? (object)Guid.Parse(line.AccountId) : line.AccountId);
                Add(insertLine, "@debit", line.Credit);
                Add(insertLine, "@credit", line.Debit);
                Add(insertLine, "@costCenter", (object?)line.CostCenter ?? DBNull.Value);
                Add(insertLine, "@note", (object?)line.Note ?? DBNull.Value);
                await insertLine.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var postReversal = CreateCommand(connection, transaction,
                "UPDATE journal_entries SET status = @posted, posted_by = @actor, posted_at = CURRENT_TIMESTAMP WHERE journal_entry_id = @id AND status = @draft"))
            {
                Add(postReversal, "@posted", (int)JournalEntryStatus.Posted);
                Add(postReversal, "@draft", (int)JournalEntryStatus.Draft);
                Add(postReversal, "@actor", userId.Trim());
                Add(postReversal, "@id", FormatGuid(reversalId));
                if (await postReversal.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new InvalidOperationException("Reversal journal could not be posted under fiscal-period controls.");
            }

            await using (var updateOriginal = CreateCommand(connection, transaction,
                "UPDATE journal_entries SET status = @reversed, reversed_by = @actor, reversed_at = CURRENT_TIMESTAMP WHERE journal_entry_id = @id AND status = @posted"))
            {
                Add(updateOriginal, "@reversed", (int)JournalEntryStatus.Reversed);
                Add(updateOriginal, "@actor", userId.Trim());
                Add(updateOriginal, "@id", FormatGuid(journalEntryId));
                Add(updateOriginal, "@posted", (int)JournalEntryStatus.Posted);
                if (await updateOriginal.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new InvalidOperationException("Journal entry changed while reversal was being created.");
            }

            await AddAuditAsync(connection, transaction, journalEntryId, 3, userId, details ?? $"Reversed by journal {reversalId:D}", cancellationToken).ConfigureAwait(false);
            await AddAuditAsync(connection, transaction, reversalId, 2, userId, $"Reversal of journal {journalEntryId:D}", cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return reversalId;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task AddAuditAsync(DbConnection connection, DbTransaction transaction, Guid entryId, int eventType, string actor, string? details, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction,
            "INSERT INTO journal_audit_events (journal_entry_id, event_type, actor, details) VALUES (@id, @type, @actor, @details)");
        Add(command, "@id", FormatGuid(entryId));
        Add(command, "@type", eventType);
        Add(command, "@actor", actor.Trim());
        Add(command, "@details", (object?)details ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private DbConnection CreateConnection() => _provider == "SQLite"
        ? new SqliteConnection(_connectionString)
        : new NpgsqlConnection(_connectionString);

    private static DbCommand CreateCommand(DbConnection connection, DbTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
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

    private object FormatGuid(Guid value) => _provider == "PostgreSQL" ? value : value.ToString();

    private static string NormalizeProvider(string provider) => provider.Trim().ToLowerInvariant() switch
    {
        "sqlite" or "sqlite3" => "SQLite",
        "postgres" or "postgresql" or "npgsql" => "PostgreSQL",
        _ => throw new NotSupportedException($"Unsupported database provider '{provider}'. Use SQLite or PostgreSQL.")
    };
}
