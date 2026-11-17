using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.Domain;
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

    public async Task UpdateDraftAsync(Guid companyId, Guid journalEntryId, JournalEntry entry, string userId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (companyId == Guid.Empty) throw new ArgumentException("Company is required.", nameof(companyId));
        if (journalEntryId == Guid.Empty) throw new ArgumentException("Journal entry is required.", nameof(journalEntryId));
        if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("User is required.", nameof(userId));
        entry.EnsureBalanced();

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int status;
            await using (var header = CreateCommand(connection, transaction,
                "SELECT company_id, status FROM journal_entries WHERE journal_entry_id = @id"))
            {
                Add(header, "@id", FormatGuid(journalEntryId));
                await using var reader = await header.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("Journal entry was not found.");
                var storedCompany = Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture);
                if (!Guid.TryParse(storedCompany, out var storedCompanyId) || storedCompanyId != companyId)
                    throw new InvalidOperationException("The journal entry does not belong to the requested company.");
                status = Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);
            }

            // Drafts only: posted and reversed entries are immutable. The
            // status is re-checked by the UPDATE's WHERE clause below, so a
            // concurrent post cannot slip an edit in after this read.
            if (status != (int)JournalEntryStatus.Draft)
                throw new AppError(ErrorCodes.JournalEntryNotEditable,
                    "Only a draft journal entry can be edited. Posted and reversed entries are immutable.");

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

            // The document number is the entry's identity (windows load by
            // it) and is deliberately NOT updated.
            await using (var update = CreateCommand(connection, transaction,
                "UPDATE journal_entries SET entry_date = @date, description = @description, reference_number = @reference, currency_code = @currency, exchange_rate = @rate, fiscal_period_id = @period WHERE journal_entry_id = @id AND company_id = @company AND status = @draft"))
            {
                Add(update, "@date", entryDateValue);
                Add(update, "@description", entry.Description);
                Add(update, "@reference", (object?)entry.ReferenceNumber ?? DBNull.Value);
                Add(update, "@currency", entry.CurrencyCode);
                Add(update, "@rate", entry.ExchangeRate);
                Add(update, "@period", _provider == "PostgreSQL" ? (object)Guid.Parse(periodId) : periodId);
                Add(update, "@id", FormatGuid(journalEntryId));
                Add(update, "@company", FormatGuid(companyId));
                Add(update, "@draft", (int)JournalEntryStatus.Draft);
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new InvalidOperationException("Journal entry changed while it was being updated.");
            }

            await using (var deleteLines = CreateCommand(connection, transaction,
                "DELETE FROM journal_entry_lines WHERE journal_entry_id = @id"))
            {
                Add(deleteLines, "@id", FormatGuid(journalEntryId));
                await deleteLines.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var lineNumber = 0;
            foreach (var line in entry.Lines)
            {
                await using var command = CreateCommand(connection, transaction,
                    "INSERT INTO journal_entry_lines (journal_entry_id, line_number, account_id, debit, credit, cost_center, note) VALUES (@id, @line, @account, @debit, @credit, @costCenter, @note)");
                Add(command, "@id", FormatGuid(journalEntryId));
                Add(command, "@line", ++lineNumber);
                Add(command, "@account", FormatGuid(line.AccountId));
                Add(command, "@debit", line.Debit);
                Add(command, "@credit", line.Credit);
                Add(command, "@costCenter", (object?)line.CostCenter ?? DBNull.Value);
                Add(command, "@note", (object?)line.Note ?? DBNull.Value);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await AddAuditAsync(connection, transaction, journalEntryId, 4, userId, "Draft updated", cancellationToken).ConfigureAwait(false);
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
            Guid companyId;
            DateOnly entryDate;
            await using (var header = CreateCommand(connection, transaction,
                "SELECT company_id, entry_date FROM journal_entries WHERE journal_entry_id = @id"))
            {
                Add(header, "@id", FormatGuid(journalEntryId));
                await using var reader = await header.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("Journal entry was not found.");
                companyId = Guid.Parse(Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!);
                entryDate = _provider == "PostgreSQL"
                    ? reader.GetFieldValue<DateOnly>(1)
                    : DateOnly.Parse(Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture);
            }

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

            await ApplyCostCenterAllocationsAsync(connection, transaction, companyId, journalEntryId, entryDate, enforceValidation: true, cancellationToken).ConfigureAwait(false);

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

            // The reversal mirrors an already-posted entry: record its cost
            // distribution too, but never block a reversal on master-data
            // changes made after the original posting.
            await ApplyCostCenterAllocationsAsync(connection, transaction, Guid.Parse(companyId), reversalId, entryDate, enforceValidation: false, cancellationToken).ConfigureAwait(false);

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

    /// <summary>
    /// Validates the cost centers of an entry being posted and records the
    /// applied distribution in journal_line_cost_splits, inside the posting
    /// transaction. Rules per line whose cost_center code resolves to a
    /// cost_centers row of the company (cost centers are keyed by company
    /// CODE in the master tables; the journal world keys by company Guid, so
    /// the code is resolved through the companies row first):
    /// - If the center has an active allocation (migration 031) effective on
    ///   the entry date, the allocation targets must all be active leaf
    ///   centers and the percentages must total 100; the line's debit and
    ///   credit are then split across the targets, with rounding residue
    ///   assigned to the largest target so the splits sum EXACTLY to the
    ///   line amounts (ACC-5007 when invalid).
    /// - Without an allocation, the center itself must be an active leaf;
    ///   the line maps 100% to it (ACC-5008 when it is a group or inactive).
    /// Lines whose cost-center text matches no master row keep the legacy
    /// free-text behavior (no validation, no splits). When
    /// <paramref name="enforceValidation"/> is false (reversal entries,
    /// which mirror an already-posted entry), nothing is blocked: an
    /// allocation that no longer validates falls back to a 100% self-split
    /// so the recorded distribution still sums to the line amounts.
    /// </summary>
    private async Task ApplyCostCenterAllocationsAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid companyId,
        Guid journalEntryId,
        DateOnly entryDate,
        bool enforceValidation,
        CancellationToken cancellationToken)
    {
        var boolTrue = _provider == "PostgreSQL" ? "true" : "1";
        var boolFalse = _provider == "PostgreSQL" ? "false" : "0";

        string? companyCode;
        await using (var company = CreateCommand(connection, transaction,
            "SELECT company_code FROM companies WHERE company_id = @company"))
        {
            Add(company, "@company", FormatGuid(companyId));
            var value = await company.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            companyCode = value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
        }
        if (string.IsNullOrWhiteSpace(companyCode))
            return;

        var lines = new List<(int LineNumber, decimal Debit, decimal Credit, string? CostCenter)>();
        await using (var load = CreateCommand(connection, transaction,
            "SELECT line_number, debit, credit, cost_center FROM journal_entry_lines WHERE journal_entry_id = @id ORDER BY line_number"))
        {
            Add(load, "@id", FormatGuid(journalEntryId));
            await using var reader = await load.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                lines.Add((Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture),
                    Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture),
                    Convert.ToDecimal(reader.GetValue(2), CultureInfo.InvariantCulture),
                    reader.IsDBNull(3) ? null : reader.GetString(3)));
            }
        }

        await using (var clear = CreateCommand(connection, transaction,
            "DELETE FROM journal_line_cost_splits WHERE journal_entry_id = @id"))
        {
            Add(clear, "@id", FormatGuid(journalEntryId));
            await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        async Task InsertSplitAsync(int lineNumber, string centerId, string centerCode, decimal percentage, decimal debitShare, decimal creditShare)
        {
            await using var insert = CreateCommand(connection, transaction,
                "INSERT INTO journal_line_cost_splits (id, company_id, journal_entry_id, line_number, cost_center_id, cost_center_code, percentage, debit_amount, credit_amount) VALUES (@id, @company, @entry, @line, @center, @code, @pct, @debit, @credit)");
            Add(insert, "@id", FormatGuid(Guid.NewGuid()));
            Add(insert, "@company", companyCode);
            Add(insert, "@entry", FormatGuid(journalEntryId));
            Add(insert, "@line", lineNumber);
            Add(insert, "@center", _provider == "PostgreSQL" ? (object)Guid.Parse(centerId) : centerId);
            Add(insert, "@code", centerCode);
            Add(insert, "@pct", percentage);
            Add(insert, "@debit", debitShare);
            Add(insert, "@credit", creditShare);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line.CostCenter))
                continue;
            var code = line.CostCenter.Trim();

            string centerId;
            bool isGroup;
            bool isActive;
            await using (var center = CreateCommand(connection, transaction,
                $"SELECT id, is_group, is_active FROM cost_centers WHERE company_id = @company AND code = @code AND is_deleted = {boolFalse}"))
            {
                Add(center, "@company", companyCode);
                Add(center, "@code", code);
                await using var reader = await center.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    continue; // Free-text cost center: no master row, legacy behavior.
                centerId = Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!;
                isGroup = Convert.ToBoolean(reader.GetValue(1), CultureInfo.InvariantCulture);
                isActive = Convert.ToBoolean(reader.GetValue(2), CultureInfo.InvariantCulture);
            }

            string? allocationId = null;
            await using (var allocation = CreateCommand(connection, transaction,
                $"""
                SELECT a.id FROM cost_center_allocations a
                JOIN cost_centers main ON main.id = a.main_cost_center_id
                WHERE a.company_id = @company AND main.code = @code
                  AND a.is_active = {boolTrue} AND a.is_deleted = {boolFalse} AND main.is_deleted = {boolFalse}
                  AND a.valid_from <= @date
                ORDER BY a.valid_from DESC LIMIT 1
                """))
            {
                Add(allocation, "@company", companyCode);
                Add(allocation, "@code", code);
                Add(allocation, "@date", entryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                var value = await allocation.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                allocationId = value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
            }

            var allocationApplied = false;
            if (allocationId is not null)
            {
                var targets = new List<(string Id, string Code, decimal Percentage, bool Valid)>();
                await using (var loadTargets = CreateCommand(connection, transaction,
                    $"""
                    SELECT c.id, c.code, l.percentage, c.is_group, c.is_active, c.is_deleted
                    FROM cost_center_allocation_lines l
                    JOIN cost_centers c ON c.id = l.cost_center_id
                    WHERE l.allocation_id = @allocation
                    """))
                {
                    Add(loadTargets, "@allocation", _provider == "PostgreSQL" ? (object)Guid.Parse(allocationId) : allocationId);
                    await using var reader = await loadTargets.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        var targetIsGroup = Convert.ToBoolean(reader.GetValue(3), CultureInfo.InvariantCulture);
                        var targetIsActive = Convert.ToBoolean(reader.GetValue(4), CultureInfo.InvariantCulture);
                        var targetIsDeleted = Convert.ToBoolean(reader.GetValue(5), CultureInfo.InvariantCulture);
                        targets.Add((Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!,
                            reader.GetString(1),
                            Convert.ToDecimal(reader.GetValue(2), CultureInfo.InvariantCulture),
                            !targetIsGroup && targetIsActive && !targetIsDeleted));
                    }
                }

                var totalPercentage = targets.Sum(t => t.Percentage);
                var allocationValid = targets.Count > 0
                    && targets.All(t => t.Valid)
                    && Math.Abs(totalPercentage - 100m) <= 0.01m;
                if (!allocationValid)
                {
                    if (enforceValidation)
                        throw new AppError(ErrorCodes.CostCenterAllocationInvalid,
                            $"The allocation for cost center '{code}' is invalid: its percentages must total 100 and every target must be an active leaf cost center.", code);
                }
                else
                {
                    // Every target except the largest gets its rounded
                    // share; the largest absorbs the rounding residue so
                    // the splits sum exactly to the line amounts.
                    var ordered = targets.OrderByDescending(t => t.Percentage).ToList();
                    var debitAssigned = 0m;
                    var creditAssigned = 0m;
                    for (var i = 1; i < ordered.Count; i++)
                    {
                        var target = ordered[i];
                        var debitShare = Math.Round(line.Debit * target.Percentage / 100m, 4, MidpointRounding.AwayFromZero);
                        var creditShare = Math.Round(line.Credit * target.Percentage / 100m, 4, MidpointRounding.AwayFromZero);
                        debitAssigned += debitShare;
                        creditAssigned += creditShare;
                        await InsertSplitAsync(line.LineNumber, target.Id, target.Code, target.Percentage, debitShare, creditShare).ConfigureAwait(false);
                    }
                    var largest = ordered[0];
                    await InsertSplitAsync(line.LineNumber, largest.Id, largest.Code, largest.Percentage,
                        line.Debit - debitAssigned, line.Credit - creditAssigned).ConfigureAwait(false);
                    allocationApplied = true;
                }
            }

            if (allocationApplied)
                continue;

            if (enforceValidation && (isGroup || !isActive))
                throw new AppError(ErrorCodes.CostCenterNotLeaf,
                    $"Cost center '{code}' is a group or inactive cost center and cannot receive postings.", code);

            await InsertSplitAsync(line.LineNumber, centerId, code, 100m, line.Debit, line.Credit).ConfigureAwait(false);
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
